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
    private const decimal ConfirmationTolerance = .01m;
    private static readonly TimeSpan InitialRetry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly XNamespace Envelope = "http://www.gesmes.org/xml/2002-08-01";
    private static readonly XNamespace Rates = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";

    private IConfigurationSection Settings => config.GetSection(ConfigKeys.Budget).GetSection(ConfigKeys.BudgetFxAuto);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try { delay = await RefreshOnceAsync(stoppingToken); }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Automatic FX state could not be refreshed ({ErrorType}).", error.GetType().Name);
                delay = InitialRetry;
            }
            await Task.Delay(delay, clock, stoppingToken);
        }
    }

    internal async Task<TimeSpan> RefreshOnceAsync(CancellationToken ct)
    {
        var defaultInterval = TimeSpan.FromHours(FxAutoSettings.DefaultRefreshHours);
        if (!FxAutoSettings.IsEnabled(Settings)) return defaultInterval;
        cache.Publish(await store.ReadAsync(s => s.FxRefresh, ct));
        FxAutoSettings settings;
        try { settings = FxAutoSettings.Read(Settings); }
        catch (DomainException error)
        {
            var invalid = await SaveAsync(clock.GetUtcNow(), null, null, error.Message, 0, ct);
            return NextDelay(invalid, defaultInterval);
        }

        FxRefreshState saved;
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
            saved = await SaveAsync(now, quote, rawRate, null, settings.MaxPublicationAgeDays, ct);
        }
        catch (DomainException error) { saved = await SaveAsync(clock.GetUtcNow(), null, null, error.Message, 0, ct); }
        catch (XmlException) { saved = await SaveAsync(clock.GetUtcNow(), null, null, "The ECB response was invalid XML.", 0, ct); }
        catch (OverflowException) { saved = await SaveAsync(clock.GetUtcNow(), null, null, "The adjusted ECB rate is outside the supported range.", 0, ct); }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException)
        {
            saved = await SaveAsync(clock.GetUtcNow(), null, null, "The ECB feed could not be reached within the refresh deadline.", 0, ct);
        }
        return NextDelay(saved, TimeSpan.FromHours(settings.RefreshHours));
    }

    private static TimeSpan NextDelay(FxRefreshState state, TimeSpan interval) =>
        state.LastError is null ? interval : TimeSpan.FromTicks(Math.Min(interval.Ticks,
            InitialRetry.Ticks * (1L << Math.Clamp(state.ConsecutiveFailures - 1, 0, 7))));

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

    private async Task<FxRefreshState> SaveAsync(DateTimeOffset now, BudgetFxQuote? quote, decimal? rawRate, string? error,
        int maxPublicationAgeDays, CancellationToken ct)
    {
        var saved = await store.UpdateAsync(state =>
        {
            var prior = state.FxRefresh ?? new();
            if (prior.LastAttemptAt > now) return prior;
            return state.FxRefresh = Update(prior, now, quote, rawRate, error, maxPublicationAgeDays);
        }, ct);
        cache.Publish(saved);
        if (saved.LastError is { } lastError) logger.LogWarning("Automatic FX refresh rejected. {Reason}", lastError);
        return saved;
    }

    private static FxRefreshState Update(FxRefreshState prior, DateTimeOffset now, BudgetFxQuote? quote, decimal? rawRate,
        string? error, int maxPublicationAgeDays)
    {
        FxRefreshState Reject(string reason) => prior with { LastAttemptAt = now, LastError = reason,
            ConsecutiveFailures = Math.Clamp(prior.ConsecutiveFailures, 0, 7) + 1 };
        if (error is not null) return Reject(error);
        if (quote?.PublishedOn is not DateOnly published || rawRate is not decimal rate) return prior;
        var pending = prior.PendingMove;
        var lastPublication = prior.Quote?.PublishedOn;
        if (lastPublication > published || pending?.PublishedOn > published)
            return Reject("The ECB publication was older than the last observed publication.");

        var acceptedStillFresh = lastPublication is DateOnly accepted &&
            DateOnly.FromDateTime(now.UtcDateTime).DayNumber - accepted.DayNumber <= 2L * maxPublicationAgeDays;
        if (acceptedStillFresh && prior.UnadjustedRate is > 0 and var last && Math.Abs(rate - last) > last * MaxRateChange)
        {
            if (pending is null || published <= pending.PublishedOn || Math.Abs(rate - pending.Rate) > pending.Rate * ConfirmationTolerance)
                return Reject("Pending confirmation of a large move. The ECB cross-rate moved more than 5% and needs a second publication.")
                    with { PendingMove = new(published, rate) };
        }
        return new(quote, rate, now);
    }
}
