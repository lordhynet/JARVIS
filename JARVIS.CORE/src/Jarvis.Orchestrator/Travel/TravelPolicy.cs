using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Travel;

public static class TravelPolicy
{
    public const string London = "Europe/London";
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(London);
    public static readonly string[] Modes = ["tube", "dlr", "overground", "elizabeth-line", "tram", "bus"];
    public static readonly TravelLine[] Defaults = [new("elizabeth", "Elizabeth line", "elizabeth-line"), new("jubilee", "Jubilee", "tube"), new("northern", "Northern", "tube")];
    public static bool ValidId(string? value) => value is { Length: > 0 and <= 60 } && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9-]*$", RegexOptions.CultureInvariant);
    public static bool ValidStop(string? value) => value is { Length: >= 6 and <= 30 } && Regex.IsMatch(value, "^[A-Za-z0-9]+$", RegexOptions.CultureInvariant);
    public static TimeOnly Time(string value) { Guard.Require(TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time), 400, "Travel times must use HH:mm."); return time; }
    public static void Validate(SaveTravelRouteRequest request)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.TimeZone == London, 400, "Travel routes require a request ID and Europe/London timezone.");
        Guard.Text(request.Name, "Route name", 80);
        Guard.Require(!request.Name.Any(char.IsControl), 400, "Route names cannot contain control characters.");
        Guard.Require(request.LineIds is { Count: > 0 and <= 12 } && request.LineIds.All(ValidId) && request.LineIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == request.LineIds.Count, 400, "Choose one to twelve distinct TfL lines.");
        Guard.Require(request.StopIds is null || request.StopIds.Count <= 12 && request.StopIds.All(ValidStop) && request.StopIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == request.StopIds.Count, 400, "Choose at most twelve distinct saved NaPTAN stops.");
        Guard.Require(request.Windows is { Count: > 0 and <= 8 }, 400, "Choose one to eight route windows.");
        foreach (var window in request.Windows) {
            Guard.Require(window.Days is { Count: > 0 and <= 7 } && window.Days.All(Enum.IsDefined) && window.Days.Distinct().Count() == window.Days.Count && Time(window.Start) != Time(window.End), 400, "Route windows need distinct weekdays and different start/end minutes.");
        }
    }
    public static IReadOnlyList<string> Occurrences(TravelRoute route, DateTimeOffset now)
    {
        if (!route.Enabled || route.TimeZone != London || route.Windows is not { Count: > 0 and <= 8 }) return [];
        var local = TimeZoneInfo.ConvertTime(now, Zone); var time = TimeOnly.FromDateTime(local.DateTime); var result = new List<string>();
        for (var i = 0; i < route.Windows.Count; i++) {
            var window = route.Windows[i]; var start = Time(window.Start); var end = Time(window.End);
            if (start == end) continue;
            var date = local.Date; if (start > end && time < end) date = date.AddDays(-1);
            if (window.Days.Contains(date.DayOfWeek) && (start < end ? time >= start && time < end : time >= start || time < end))
                result.Add($"{route.Id:N}:{date:yyyy-MM-dd}:{i}:{window.Start}");
        }
        return result;
    }
    public static bool Active(TravelRoute route, DateTimeOffset now) => Occurrences(route, now).Count > 0;
    public static bool PollEligible(TravelRoute route, DateTimeOffset now) => route.Enabled && Enumerable.Range(0, 61).Any(i => Active(route, now.AddMinutes(i)));
    public static DateTimeOffset ActiveUntil(TravelRoute route, DateTimeOffset now)
    {
        var probe = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        for (var i = 1; i <= 1440; i++) if (!Active(route, probe.AddMinutes(i))) return probe.AddMinutes(i);
        return now.AddHours(24);
    }
    public static bool Good(TravelStatus status) => status.Description.Equals("Good Service", StringComparison.OrdinalIgnoreCase);
    // Application impact labels, never an ordering of TfL numeric severity codes.
    public static int? Impact(TravelStatus status) => status.Description.ToLowerInvariant() switch {
        "good service" => 0, "minor delays" or "reduced service" => 1, "severe delays" => 2,
        "part suspended" or "part closure" => 3, "suspended" or "service closed" or "planned closure" => 4, _ => null
    };
    public static string ReasonHash(TravelStatus status) => PrivacyData.Hash(Regex.Replace(status.Reason.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim());
    public static string Fingerprint(TravelStatus status) => PrivacyData.Hash($"{status.Severity}:{status.Description.ToLowerInvariant()}:{ReasonHash(status)}");
    public static string ClipUtf8(string value, int bytes)
    {
        var text = new StringBuilder(); var used = 0;
        foreach (var rune in value.EnumerateRunes()) { if (used + rune.Utf8SequenceLength > bytes) break; text.Append(rune); used += rune.Utf8SequenceLength; }
        return text.ToString();
    }
}
