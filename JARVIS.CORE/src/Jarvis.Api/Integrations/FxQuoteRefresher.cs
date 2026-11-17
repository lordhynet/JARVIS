using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

// Readiness checks are synchronous. Charge reservations and the status endpoint use the transactional record.
public sealed class FxQuoteCache
{
    private FxRefreshState? current;
    internal FxRefreshState? Current => Volatile.Read(ref current);
    internal void Publish(FxRefreshState? value) => Volatile.Write(ref current, value);
}

internal sealed class FxQuoteRefresher(IHttpClientFactory clients, IConfiguration config, IRuntimeStore store,
    FxQuoteCache cache, TimeProvider clock, ILogger<FxQuoteRefresher> logger) : BackgroundService
{
    internal const string SourceUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private const int MaxResponseBytes = 64 * 1024;
    private const decimal MaxRateChange = .05m;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly XNamespace Envelope = "http://www.gesmes.org/xml/2002-08-01";
    private static readonly XNamespace Rates = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";

    private IConfigurationSection Settings => config.GetSection(ConfigKeys.Budget).GetSection(ConfigKeys.BudgetFxAuto);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var hours = FxAutoSettings.DefaultRefreshHours;
            try { hours = await RefreshOnceAsync(stoppingToken); }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Automatic FX state could not be refreshed ({ErrorType}).", error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromHours(hours), clock, stoppingToken);
        }
    }

    internal async Task<int> RefreshOnceAsync(CancellationToken ct)
    {
        if (!FxAutoSettings.IsEnabled(Settings)) return FxAutoSettings.DefaultRefreshHours;
        cache.Publish(await store.ReadAsync(s => s.FxRefresh, ct));
        FxAutoSettings settings;
        try { settings = FxAutoSettings.Read(Settings); }
        catch (DomainException error)
        {
            await SaveAsync(clock.GetUtcNow(), null, null, error.Message, ct);
            return FxAutoSettings.DefaultRefreshHours;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(FetchTimeout);
            var (publication, rawRate) = await FetchAsync(deadline.Token);
            var now = clock.GetUtcNow();
            var age = DateOnly.FromDateTime(now.UtcDateTime).DayNumber - publication.DayNumber;
            Guard.Require(age >= 0 && age <= settings.MaxPublicationAgeDays, 503, "The ECB publication date is outside the configured age limit.");
            var adjusted = rawRate * (1 + settings.MarginPercent / 100m);
            var source = FormattableString.Invariant($"ECB reference {publication:yyyy-MM-dd}, +{settings.MarginPercent}% margin");
            var quote = new BudgetFxQuote(source, adjusted, now, now.AddHours(24), publication);
            Guard.Require(GbpBudgetRules.IsCurrentQuote(quote, now), 503, "The adjusted ECB rate is outside the supported range.");
            await SaveAsync(now, quote, rawRate, null, ct);
        }
        catch (DomainException error) { await SaveAsync(clock.GetUtcNow(), null, null, error.Message, ct); }
        catch (XmlException) { await SaveAsync(clock.GetUtcNow(), null, null, "The ECB response was invalid XML.", ct); }
        catch (OverflowException) { await SaveAsync(clock.GetUtcNow(), null, null, "The adjusted ECB rate is outside the supported range.", ct); }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException)
        {
            await SaveAsync(clock.GetUtcNow(), null, null, "The ECB feed could not be reached within the refresh deadline.", ct);
        }
        return settings.RefreshHours;
    }

    private async Task<(DateOnly Publication, decimal RawRate)> FetchAsync(CancellationToken ct)
    {
        using var client = clients.CreateClient(FxAutoSettings.ClientName);
        using var response = await client.GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        Guard.Require(response.StatusCode == HttpStatusCode.OK, 503, "The ECB feed did not return a successful response.");
        Guard.Require(response.Content.Headers.ContentLength is null or <= MaxResponseBytes, 503, "The ECB response exceeded the size limit.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[MaxResponseBytes + 1];
        var size = await body.ReadAtLeastAsync(bytes.AsMemory(), bytes.Length, false, ct);
        Guard.Require(size <= MaxResponseBytes, 503, "The ECB response exceeded the size limit.");
        using var stream = new MemoryStream(bytes, 0, size);
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxResponseBytes
        });
        var document = XDocument.Load(xml);
        var outer = document.Root?.Elements(Rates + "Cube").ToArray();
        Guard.Require(document.Root?.Name == Envelope + "Envelope" && outer is { Length: 1 }, 503, "The ECB response had an unexpected structure.");
        var days = outer[0].Elements(Rates + "Cube").ToArray();
        var publication = default(DateOnly);
        Guard.Require(days.Length == 1 && DateOnly.TryParseExact((string?)days[0].Attribute("time"), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out publication), 503, "The ECB response had an invalid publication date.");
        return (publication, Rate(days[0], "GBP") / Rate(days[0], "USD"));
    }

    private static decimal Rate(XElement day, string currency)
    {
        var rows = day.Elements(Rates + "Cube").Where(row => (string?)row.Attribute("currency") == currency).ToArray();
        var rate = 0m;
        Guard.Require(rows.Length == 1 && decimal.TryParse((string?)rows[0].Attribute("rate"), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out rate) && rate is >= .3m and <= 2m, 503, $"The ECB response had an invalid {currency} rate.");
        return rate;
    }

    private async Task SaveAsync(DateTimeOffset now, BudgetFxQuote? quote, decimal? rawRate, string? error, CancellationToken ct)
    {
        var saved = await store.UpdateAsync(state =>
        {
            var prior = state.FxRefresh ?? new();
            var rejection = error;
            if (prior.LastAttemptAt > now) return prior;
            if (quote is not null && prior.Quote?.PublishedOn > quote.PublishedOn)
                rejection = "The ECB publication was older than the last accepted publication.";
            if (rawRate is decimal rate && prior.UnadjustedRate is > 0 and var last && Math.Abs(rate - last) > last * MaxRateChange)
                rejection = "The ECB cross-rate moved more than 5% from the last accepted rate.";
            return state.FxRefresh = rejection is null
                ? new(quote, rawRate, now)
                : prior with { LastAttemptAt = now, LastError = rejection };
        }, ct);
        cache.Publish(saved);
        if (saved.LastError is { } lastError) logger.LogWarning("Automatic FX refresh rejected. {Reason}", lastError);
    }
}
