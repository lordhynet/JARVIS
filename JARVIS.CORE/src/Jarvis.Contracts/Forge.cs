namespace Jarvis.Contracts;

public sealed record CreateForgeProgrammeRequest(Guid RequestId,
    string LocalStartDate = "2026-12-01", string LocalGraduationDate = "2027-08-18", string TimeZone = "Europe/London");
public sealed record PauseForgeProgrammeRequest(int ExpectedRevision, bool Paused);
// This is programme configuration, not another coaching or learning history.
public sealed record ForgeProgramme(Guid Id, string OwnerScope, int Revision, int ConfigurationVersion,
    DateOnly Start, DateOnly Graduation, string TimeZone, bool Paused,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt = null);
public sealed record ForgeProgrammeView(ForgeProgramme Programme, ForgeCalendarView Calendar);

public enum ForgeProgrammeStatus { NotStarted, Active, Graduated }

/// <summary>A programme phase in local calendar dates, both ends inclusive.</summary>
public sealed record ForgePhase(string Key, string Title, DateOnly Start, DateOnly End);

/// <summary>
/// Calendar position only. <see cref="CalendarProgress"/> is elapsed time between start and graduation
/// (0 on the first day, 1 on graduation day); it is never a measure of mastery.
/// </summary>
public sealed record ForgeCalendarView(
    ForgeProgrammeStatus Status,
    DateOnly LocalToday,
    DateOnly Start,
    DateOnly Graduation,
    int ProgrammeDays,
    int DayNumber,
    int DaysUntilStart,
    int DaysRemaining,
    decimal CalendarProgress,
    ForgePhase? Phase,
    ForgePhase? NextPhase);

/// <summary>Install the code-owned Forge division templates as Domain Registry drafts (never activates them).</summary>
public sealed record InstallForgeDivisionsRequest(bool IncludeOptional = false);
/// <summary>Status is "drafted", "existing" (left untouched) or "skipped-optional".</summary>
public sealed record ForgeDivisionDraft(string Key, string Code, string Status, bool Protected, bool Optional, DomainRiskClass? Risk);

/// <summary>One Forge "today" item. Labels never contain protected-division goal titles.</summary>
public sealed record ForgeBriefingItem(string Label, int Minutes, string Why);
/// <summary>
/// The Forge "today" block shared by the spoken briefing and the browser Today view (PF-09). <c>Scheduled</c> is true
/// only when a capacity-backed plan placed the items; otherwise they are suggestions and capacity is never assumed.
/// </summary>
public sealed record ForgeBriefing(string Status, int Day, int Days, string? Phase, int? StartsInDays, bool Scheduled,
    IReadOnlyList<ForgeBriefingItem> Top, int DueReviews);
