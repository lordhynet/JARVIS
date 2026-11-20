namespace Jarvis.Contracts;

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
