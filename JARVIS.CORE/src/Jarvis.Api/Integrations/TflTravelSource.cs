using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Travel;

namespace Jarvis.Api.Integrations;

public sealed class TflTravelSource(IHttpClientFactory clients, IConfiguration config, TimeProvider clock, ILogger<TflTravelSource> logger) : ITravelSource
{
    public const string ClientName = "tfl-travel";
    private readonly SemaphoreSlim catalogueLock = new(1, 1);
    private DateOnly? cachedDay;
    private IReadOnlyList<TravelLine> cached = [];
    public static string SafeUrl(Uri uri) => uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length == 0 ? "" : "?[redacted]");
    private static string Text(JsonElement item, string name, string fallback = "") => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static string Clean(string value, int limit) => TravelPolicy.ClipUtf8(Regex.Replace(value, @"[\p{Cc}<>]", " ").Trim(), limit);
    private async Task<JsonElement> ReadAsync(string path, CancellationToken ct)
    {
        var key = config["TfL:AppKey"];
        Guard.Require(key is null || key.Length <= 512 && !key.Any(char.IsControl), 503, "TfL AppKey configuration is invalid.");
        var uri = new Uri("https://api.tfl.gov.uk/" + path + (string.IsNullOrWhiteSpace(key) ? "" : "?app_key=" + Uri.EscapeDataString(key)));
        logger.LogDebug("TfL GET {Url}", SafeUrl(uri));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        try {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, ct);
            Guard.Require(response.IsSuccessStatusCode, 502, "TfL is unavailable. Check the free AppKey and try later.");
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) {
            throw new DomainException(502, "TfL is unavailable or returned invalid bounded data.");
        }
    }
    public async Task<IReadOnlyList<TravelLine>> CatalogueAsync(CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TravelPolicy.Zone).DateTime);
        await catalogueLock.WaitAsync(ct);
        try {
            if (cachedDay == day) return cached;
            var json = await ReadAsync("Line/Mode/" + string.Join(',', TravelPolicy.Modes), ct);
            Guard.Require(json.ValueKind == JsonValueKind.Array && json.GetArrayLength() is > 0 and <= 2000, 502, "TfL catalogue is invalid.");
            var lines = json.EnumerateArray().Select(item => new TravelLine(Text(item, "id").ToLowerInvariant(), Clean(Text(item, "name"), 100), Text(item, "modeName"))).ToArray();
            Guard.Require(lines.All(l => TravelPolicy.ValidId(l.Id) && l.Name.Length > 0 && TravelPolicy.Modes.Contains(l.Mode)) && lines.DistinctBy(l => l.Id).Count() == lines.Length, 502, "TfL catalogue contains invalid or duplicate lines.");
            cached = Array.AsReadOnly(lines.OrderBy(l => l.Name, StringComparer.Ordinal).ToArray()); cachedDay = day; return cached;
        }
        finally { catalogueLock.Release(); }
    }
    public async Task<IReadOnlyList<TravelStop>> StopsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        Guard.Require(ids is { Count: > 0 and <= 12 } && ids.All(TravelPolicy.ValidStop), 400, "Invalid saved stop IDs.");
        var json = await ReadAsync("StopPoint/" + string.Join(',', ids), ct);
        var items = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToArray() : [json];
        Guard.Require(items.Length <= 12 && items.All(i => i.ValueKind == JsonValueKind.Object), 502, "TfL returned invalid stops.");
        var stops = items.Select(item => new TravelStop(Text(item, "naptanId", Text(item, "id")), Clean(Text(item, "commonName", Text(item, "name")), 100),
            item.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array ? lines.EnumerateArray().Select(l => Text(l, "id").ToLowerInvariant()).Where(TravelPolicy.ValidId).Distinct().Take(100).ToArray() : [])).ToArray();
        Guard.Require(stops.Length == ids.Count && ids.All(id => stops.Count(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) == 1) && stops.All(s => s.Name.Length > 0), 400, "TfL could not validate the selected stops.");
        return stops;
    }
    public async Task<IReadOnlyList<TravelStatus>> StatusAsync(IReadOnlyList<TravelLine> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return [];
        Guard.Require(lines.Count <= 120 && lines.All(l => TravelPolicy.ValidId(l.Id) && TravelPolicy.Modes.Contains(l.Mode)), 400, "TfL status needs validated public line references.");
        var path = lines.Count <= 20 ? "Line/" + string.Join(',', lines.Select(l => l.Id)) + "/Status"
            : "Line/Mode/" + string.Join(',', lines.Select(l => l.Mode).Distinct().Order()) + "/Status";
        var json = await ReadAsync(path, ct);
        Guard.Require(json.ValueKind == JsonValueKind.Array && json.GetArrayLength() <= 2000, 502, "TfL status response is invalid.");
        var requested = lines.ToDictionary(l => l.Id, StringComparer.OrdinalIgnoreCase); var result = new Dictionary<string, TravelStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in json.EnumerateArray()) {
            var id = Text(item, "id").ToLowerInvariant();
            if (!requested.TryGetValue(id, out var selected) || !item.TryGetProperty("lineStatuses", out var raw) || raw.ValueKind != JsonValueKind.Array) continue;
            Guard.Require(raw.GetArrayLength() <= 20, 502, "TfL returned too many statuses for a line.");
            var statuses = raw.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("statusSeverity", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) && number is >= 0 and <= 1000)
                .Select(s => new TravelStatus(id, Clean(Text(item, "name", selected.Name), 100), s.GetProperty("statusSeverity").GetInt32(), Clean(Text(s, "statusSeverityDescription"), 100), Clean(Text(s, "reason"), 600), clock.GetUtcNow()))
                .Where(s => s.Description.Length > 0).ToArray();
            if (statuses.Length == 0) continue;
            var primary = statuses.OrderByDescending(s => TravelPolicy.Good(s) ? 0 : 1).ThenByDescending(s => TravelPolicy.Impact(s) ?? int.MaxValue).First();
            var reasons = statuses.Where(s => !TravelPolicy.Good(s) && s.Reason.Length > 0).Select(s => s.Reason).Distinct();
            Guard.Require(result.TryAdd(id, primary with { Reason = TravelPolicy.ClipUtf8(string.Join("; ", reasons), 600) }), 502, "TfL returned a duplicate requested line.");
        }
        Guard.Require(result.Count > 0, 502, "TfL did not return a usable requested line status."); return result.Values.ToArray();
    }
    public async Task<IReadOnlyList<TravelArrival>> ArrivalsAsync(TravelStop stop, CancellationToken ct)
    {
        Guard.Require(TravelPolicy.ValidStop(stop.Id), 400, "Choose a validated saved stop.");
        var json = await ReadAsync("StopPoint/" + stop.Id + "/Arrivals", ct);
        Guard.Require(json.ValueKind == JsonValueKind.Array && json.GetArrayLength() <= 2000, 502, "TfL arrivals response is invalid.");
        var now = clock.GetUtcNow(); var result = new List<TravelArrival>();
        foreach (var item in json.EnumerateArray()) {
            var line = Text(item, "lineId").ToLowerInvariant();
            if (!stop.LineIds.Contains(line, StringComparer.OrdinalIgnoreCase) || !DateTimeOffset.TryParse(Text(item, "expectedArrival"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) || at < now.AddSeconds(-30) || at > now.AddDays(1)) continue;
            result.Add(new(line, Clean(Text(item, "lineName", line), 50), stop.Id, Clean(Text(item, "destinationName"), 70), Clean(Text(item, "platformName"), 30), Math.Max(0, (int)Math.Ceiling((at - now).TotalSeconds)), at.ToUniversalTime()));
        }
        return result.OrderBy(a => a.ExpectedAt).Take(5).ToArray();
    }
}
// Testing composition refuses real TfL traffic unless a fixture explicitly replaces this handler.
internal sealed class UnconfiguredTflTestHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("Configure a fake TfL handler for this test.");
}
public sealed class TravelDeliverySettings(IConfiguration config) : ITravelDeliveryPolicy
{
    public bool NotifyWhenAgentsPaused => config.GetValue("Travel:NotifyWhenAgentsPaused", true);
}
public sealed class TravelMonitorWorker(TravelMonitor monitor, IPrivacyReader privacy, ILogger<TravelMonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken)) try {
            var state = await privacy.StatusAsync(stoppingToken); if (state.Erased) continue;
            using var scope = PrivacyContext.Enter(state.Epoch); await monitor.PollAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        catch (Exception error) { logger.LogWarning("Travel monitor failed ({Type}).", error.GetType().Name); }
    }
}
