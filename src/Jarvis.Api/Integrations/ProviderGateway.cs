using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Cqrs.Integrations;

namespace Jarvis.Api.Integrations;

public sealed partial class ProviderGateway(IHttpClientFactory clients, IConfiguration config) : IHomeControl, IIntegrationReader
{
    public IReadOnlyList<IntegrationStatus> Status() =>
    [
        new("OpenAI", Has(ConfigKeys.OpenAiApiKey) && Has(ConfigKeys.OpenAiModel), "Conversation requires ApiKey and Model."),
        new("Transcription", Has(ConfigKeys.OpenAiApiKey) && Has(ConfigKeys.OpenAiTranscriptionModel), "Recorded microphone audio is sent to OpenAI only on request."),
        new("HomeAssistant", Has("HomeAssistant:BaseUrl") && Has("HomeAssistant:Token"), "Actions require an allowed entity and explicit confirmation."),
        new("GoogleCalendar", Has("Calendar:Google:AccessToken"), "Read-only calendar access; provide a current OAuth access token."),
        new("MicrosoftCalendar", Has("Calendar:Microsoft:AccessToken"), "Read-only calendar access; provide a current OAuth access token."),
        new("TfL", true, "Public live data; an optional AppKey can be configured.")
    ];

    private bool Has(string key) => !string.IsNullOrWhiteSpace(config[key]);
    public string Required(string key) => Has(key) ? config[key]! : throw new DomainException(503, $"Configure {key} to use this integration.");

    public async Task<JsonElement> JsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await clients.CreateClient("providers").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new DomainException(502, $"Provider returned HTTP {(int)response.StatusCode}. Check credentials and provider availability.");
        try { return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct); }
        catch (JsonException) { throw new DomainException(502, "Provider returned invalid JSON."); }
    }

    public async Task<IReadOnlyList<TransitLine>> TransitAsync(string? lines, CancellationToken ct)
    {
        lines = string.IsNullOrWhiteSpace(lines) ? "elizabeth,jubilee,northern" : lines;
        Guard.Require(lines.Length <= 250 && Regex.IsMatch(lines, "^[a-z-]+(,[a-z-]+)*$"), 400, "Invalid TfL line IDs.");
        var key = config["TfL:AppKey"];
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.tfl.gov.uk/Line/" + lines + "/Status" + (string.IsNullOrWhiteSpace(key) ? "" : "?app_key=" + Uri.EscapeDataString(key)));
        var json = await JsonAsync(request, ct);
        return json.EnumerateArray().Select(line => new TransitLine(
            line.GetProperty("id").GetString()!, line.GetProperty("name").GetString()!,
            line.GetProperty("lineStatuses").EnumerateArray().Select(s =>
                s.GetProperty("statusSeverityDescription").GetString()! +
                (s.TryGetProperty("reason", out var reason) ? ": " + reason.GetString() : "")).ToList())).ToList();
    }

    public async Task<IReadOnlyList<CalendarEntry>> CalendarAsync(string provider, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        Guard.Require(end > start && end - start <= TimeSpan.FromDays(31), 400, "Calendar range must be between 0 and 31 days.");
        Guard.Require(provider is "google" or "microsoft", 400, "Choose google or microsoft.");
        var google = provider == "google";
        var token = Required(google ? "Calendar:Google:AccessToken" : "Calendar:Microsoft:AccessToken");
        var from = Uri.EscapeDataString(start.ToString("O", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(end.ToString("O", CultureInfo.InvariantCulture));
        var calendarId = Uri.EscapeDataString(config["Calendar:Google:CalendarId"] ?? "primary");
        var url = google
            ? $"https://www.googleapis.com/calendar/v3/calendars/{calendarId}/events?singleEvents=true&orderBy=startTime&maxResults=250&timeMin={from}&timeMax={to}"
            : $"https://graph.microsoft.com/v1.0/me/calendarView?startDateTime={from}&endDateTime={to}&$top=250&$orderby=start/dateTime";
        var initial = url;
        var result = new List<CalendarEntry>();
        for (var page = 0; page < 20; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!google) request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
            var json = await JsonAsync(request, ct);
            foreach (var item in json.GetProperty(google ? "items" : "value").EnumerateArray())
            {
                if (google && item.TryGetProperty("status", out var status) && status.GetString() == "cancelled") continue;
                if (!google && item.TryGetProperty("isCancelled", out var cancelled) && cancelled.GetBoolean()) continue;
                var startValue = item.GetProperty("start");
                var allDay = google ? startValue.TryGetProperty("date", out _) : item.GetProperty("isAllDay").GetBoolean();
                string Time(JsonElement value)
                {
                    if (google && allDay) return value.GetProperty("date").GetString()!;
                    var timestamp = value.GetProperty("dateTime").GetString()!;
                    if (google) return timestamp;
                    Guard.Require(DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsed), 502, "Calendar returned an invalid timestamp.");
                    return parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                }
                result.Add(new(item.GetProperty("id").GetString()!,
                    item.TryGetProperty(google ? "summary" : "subject", out var title) ? title.GetString() ?? "Untitled" : "Untitled",
                    Time(startValue), Time(item.GetProperty("end")), allDay, provider));
            }
            if (google)
            {
                if (!json.TryGetProperty("nextPageToken", out var next)) return result;
                url = initial + "&pageToken=" + Uri.EscapeDataString(next.GetString()!);
            }
            else
            {
                if (!json.TryGetProperty("@odata.nextLink", out var next)) return result;
                url = next.GetString()!;
                Guard.Require(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                    uri.Host == "graph.microsoft.com" && uri.IsDefaultPort, 502, "Invalid calendar pagination URL.");
            }
        }
        throw new DomainException(502, "Calendar exceeded the 20-page limit. Request a shorter date range.");
    }

    public async Task<string> ExecuteAsync(string entityId, string action, CancellationToken ct)
    {
        var (root, token) = HomeTarget(entityId);
        var domain = entityId.Split('.')[0];
        using var request = new HttpRequestMessage(HttpMethod.Post, root + $"/api/services/{domain}/{action}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { entity_id = entityId });
        await JsonAsync(request, ct);
        return "Home Assistant accepted the service call. Verify the physical device if needed.";
    }

    internal async Task<string> VerifyHomeStateAsync(string entityId, string action, CancellationToken ct)
    {
        var reply = await HomeStateAsync(entityId, ct);
        var expected = action == "turn_on" ? "on" : "off";
        Guard.Require(reply.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == expected,
            502, "Home Assistant did not report the requested entity state.");
        return $"Home Assistant reports the entity is {expected}.";
    }

    internal async Task<string?> HomeFriendlyNameAsync(string entityId, CancellationToken ct)
    {
        var state = await HomeStateAsync(entityId, ct);
        return state.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
            attributes.TryGetProperty("friendly_name", out var name) && name.ValueKind == JsonValueKind.String &&
            name.GetString() is { Length: > 0 and <= 120 } friendly ? friendly : null;
    }

    private async Task<JsonElement> HomeStateAsync(string entityId, CancellationToken ct)
    {
        var (root, token) = HomeTarget(entityId);
        using var request = new HttpRequestMessage(HttpMethod.Get, root + "/api/states/" + Uri.EscapeDataString(entityId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var reply = await JsonAsync(request, ct);
        Guard.Require(reply.ValueKind == JsonValueKind.Object &&
            reply.TryGetProperty("entity_id", out var entity) && entity.ValueKind == JsonValueKind.String && entity.GetString() == entityId,
            502, "Home Assistant did not report the requested entity.");
        return reply;
    }

    private (string Root, string Token) HomeTarget(string entityId)
    {
        var root = Required("HomeAssistant:BaseUrl");
        Guard.Require(Uri.TryCreate(root, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo),
            503, "HomeAssistant:BaseUrl must be an HTTP(S) URL without credentials.");
        Guard.Require(uri!.Scheme == "https" || uri.IsLoopback || config.GetValue<bool>("HomeAssistant:AllowInsecureLan"),
            503, "Home Assistant requires HTTPS outside loopback. Explicitly authorize AllowInsecureLan only on a trusted private network.");
        var allowed = config.GetSection("HomeAssistant:AllowedEntities").Get<string[]>() ?? [];
        Guard.Require(allowed.Contains(entityId, StringComparer.Ordinal), 403, "Entity is not in HomeAssistant:AllowedEntities.");
        return (root.TrimEnd('/'), Required("HomeAssistant:Token"));
    }
}
