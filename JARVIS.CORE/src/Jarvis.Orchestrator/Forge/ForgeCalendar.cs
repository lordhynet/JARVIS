using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Forge;

/// <summary>
/// Pure Project Forge calendar (ADR 0023). Dates are local programme dates in the programme time zone;
/// callers pass UTC instants. Day counting uses calendar dates, so daylight-saving changes never shift a day.
/// </summary>
public static class ForgeCalendar
{
    public const string DefaultTimeZone = "Europe/London";
    public static readonly DateOnly DefaultStart = new(2026, 12, 1);
    public static readonly DateOnly DefaultGraduation = new(2027, 8, 18);
    public static readonly DateOnly TechnicalCheckpoint = new(2027, 5, 31);

    /// <summary>Provisional windows from the reviewed plan; verify against the original addendum before activation.</summary>
    public static IReadOnlyList<ForgePhase> DefaultPhases { get; } = Array.AsReadOnly<ForgePhase>(
    [
        new("foundation", "Foundation and reset", new(2026, 12, 1), new(2026, 12, 31)),
        new("market-readiness", "Market readiness", new(2027, 1, 1), new(2027, 2, 28)),
        new("acceleration", "Acceleration", new(2027, 3, 1), new(2027, 4, 30)),
        new("authority", "Authority and commercial traction", new(2027, 5, 1), new(2027, 6, 30)),
        new("consolidation", "Consolidation and assessment", new(2027, 7, 1), new(2027, 8, 18)),
    ]);

    /// <summary>Inclusive number of programme days (261 for the default programme).</summary>
    public static int ProgrammeDays(DateOnly start, DateOnly graduation)
    {
        if (graduation < start) throw new ArgumentException("Graduation must not be before the start date.");
        return graduation.DayNumber - start.DayNumber + 1;
    }

    public static DateOnly LocalDate(DateTimeOffset utcNow, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);

    public static ForgeCalendarView Evaluate(DateTimeOffset utcNow, TimeZoneInfo zone) =>
        Evaluate(utcNow, zone, DefaultStart, DefaultGraduation, DefaultPhases);

    public static ForgeCalendarView Evaluate(DateTimeOffset utcNow, TimeZoneInfo zone, DateOnly start, DateOnly graduation, IReadOnlyList<ForgePhase> phases)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(phases);
        // Take one configuration snapshot for validation and evaluation.
        phases = phases.ToArray();
        ValidatePhases(start, graduation, phases);
        var today = LocalDate(utcNow, zone);
        var total = ProgrammeDays(start, graduation);
        var span = graduation.DayNumber - start.DayNumber; // 260 for the default programme

        if (today < start)
            return new(ForgeProgrammeStatus.NotStarted, today, start, graduation, total, 0,
                start.DayNumber - today.DayNumber, total, 0m, null, phases[0]);

        if (today > graduation)
            return new(ForgeProgrammeStatus.Graduated, today, start, graduation, total, total, 0, 0, 1m, null, null);

        var elapsed = today.DayNumber - start.DayNumber;
        var index = phases.ToList().FindIndex(p => p.Start <= today && today <= p.End);
        var progress = span == 0 ? 1m : Math.Round((decimal)elapsed / span, 4, MidpointRounding.ToZero);
        return new(ForgeProgrammeStatus.Active, today, start, graduation, total, elapsed + 1, 0,
            graduation.DayNumber - today.DayNumber, progress, phases[index], index + 1 < phases.Count ? phases[index + 1] : null);
    }

    /// <summary>Phases must be ordered, contiguous and cover the whole programme exactly.</summary>
    public static void ValidatePhases(DateOnly start, DateOnly graduation, IReadOnlyList<ForgePhase> phases)
    {
        ArgumentNullException.ThrowIfNull(phases);
        if (graduation < start) throw new ArgumentException("Graduation must not be before the start date.");
        if (phases.Count == 0) throw new ArgumentException("At least one phase is required.");
        if (phases.Any(p => p is null || string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.Title)))
            throw new ArgumentException("Every phase needs a key and title.");
        if (phases.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != phases.Count)
            throw new ArgumentException("Phase keys must be unique.");
        if (phases[0].Start != start || phases[^1].End != graduation) throw new ArgumentException("Phases must cover the programme exactly.");
        for (var i = 0; i < phases.Count; i++)
        {
            if (phases[i].End < phases[i].Start) throw new ArgumentException("A phase ends before it starts.");
            if (i > 0 && phases[i].Start.DayNumber != phases[i - 1].End.DayNumber + 1) throw new ArgumentException("Phases must be contiguous without gaps or overlaps.");
        }
    }
}
