namespace Jarvis.Contracts;

public sealed record TravelLine(string Id, string Name, string Mode);
public sealed record TravelStop(string Id, string Name, IReadOnlyList<string> LineIds);
public sealed record TravelWindow(IReadOnlyList<DayOfWeek> Days, string Start, string End);
public sealed record TravelRoute(Guid Id, string Name, IReadOnlyList<TravelLine> Lines, IReadOnlyList<TravelStop> Stops,
    IReadOnlyList<TravelWindow> Windows, string TimeZone, bool Enabled, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    Guid LastRequestId = default, string? LastRequestHash = null);
public sealed record SaveTravelRouteRequest(Guid RequestId, int? ExpectedRevision, string Name, IReadOnlyList<string> LineIds,
    IReadOnlyList<string>? StopIds, IReadOnlyList<TravelWindow> Windows, bool Enabled = true, string TimeZone = "Europe/London");
public sealed record TravelStatus(string LineId, string LineName, int Severity, string Description, string Reason, DateTimeOffset ObservedAt);
public sealed record TravelArrival(string LineId, string LineName, string StopId, string Destination, string Platform, int Seconds, DateTimeOffset ExpectedAt);
public sealed record TravelLineState(string LineId, TravelStatus? Confirmed = null, string? ReasonHash = null,
    string? CandidateHash = null, TravelStatus? Candidate = null, int CandidateCount = 0, DateTimeOffset? LastSeenAt = null,
    DateTimeOffset? LastNoticeAt = null, TravelStatus? LastNotified = null, IReadOnlyList<string>? WindowKeys = null);
public sealed record TravelMonitorState(DateTimeOffset? NextPollAt = null, Guid? Claim = null, DateTimeOffset? LeaseUntil = null,
    int Failures = 0, DateOnly? NoticeDay = null, int DisruptionNotices = 0, string? LastError = null);
public sealed record TravelNoticeAuthority(Guid RouteId, int RouteRevision, string LineId, string Occurrence);
public static class NotificationCategories { public const string Agent = "Agent", Travel = "Travel"; }
