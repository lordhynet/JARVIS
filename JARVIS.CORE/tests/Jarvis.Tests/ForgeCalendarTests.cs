using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;

namespace Jarvis.Tests;

public sealed class ForgeCalendarTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById(ForgeCalendar.DefaultTimeZone);
    private static ForgeCalendarView At(string utc) => ForgeCalendar.Evaluate(DateTimeOffset.Parse(utc), London);

    [Fact]
    public void Default_programme_has_261_inclusive_days_and_260_days_between_dates()
    {
        Assert.Equal(261, ForgeCalendar.ProgrammeDays(ForgeCalendar.DefaultStart, ForgeCalendar.DefaultGraduation));
        Assert.Equal(260, ForgeCalendar.DefaultGraduation.DayNumber - ForgeCalendar.DefaultStart.DayNumber);
    }

    [Fact]
    public void Before_the_start_it_is_not_started_at_zero_percent()
    {
        var view = At("2026-11-30T23:30:00Z"); // 23:30 GMT on 30 Nov
        Assert.Equal(ForgeProgrammeStatus.NotStarted, view.Status);
        Assert.Equal(0, view.DayNumber);
        Assert.Equal(1, view.DaysUntilStart);
        Assert.Equal(0m, view.CalendarProgress);
        Assert.Null(view.Phase);
        Assert.Equal("foundation", view.NextPhase!.Key);
    }

    [Fact]
    public void First_day_is_day_one_of_the_foundation_phase_at_zero_percent()
    {
        var view = At("2026-12-01T00:00:00Z");
        Assert.Equal(ForgeProgrammeStatus.Active, view.Status);
        Assert.Equal(1, view.DayNumber);
        Assert.Equal(260, view.DaysRemaining);
        Assert.Equal(0m, view.CalendarProgress);
        Assert.Equal("foundation", view.Phase!.Key);
        Assert.Equal("market-readiness", view.NextPhase!.Key);
    }

    [Fact]
    public void Phase_boundaries_are_inclusive()
    {
        Assert.Equal("foundation", At("2026-12-31T12:00:00Z").Phase!.Key);
        Assert.Equal("market-readiness", At("2027-01-01T12:00:00Z").Phase!.Key);
        Assert.Equal("market-readiness", At("2027-02-28T12:00:00Z").Phase!.Key);
        Assert.Equal("acceleration", At("2027-03-01T12:00:00Z").Phase!.Key);
        Assert.Equal("authority", At("2027-05-31T12:00:00Z").Phase!.Key); // technical checkpoint day
        Assert.Equal("consolidation", At("2027-07-01T12:00:00Z").Phase!.Key);
    }

    [Fact]
    public void Graduation_day_is_the_last_day_at_one_hundred_percent()
    {
        var view = At("2027-08-18T12:00:00Z");
        Assert.Equal(ForgeProgrammeStatus.Active, view.Status);
        Assert.Equal(261, view.DayNumber);
        Assert.Equal(0, view.DaysRemaining);
        Assert.Equal(1m, view.CalendarProgress);
        Assert.Equal("consolidation", view.Phase!.Key);
        Assert.Null(view.NextPhase);
    }

    [Fact]
    public void After_graduation_the_timeline_freezes_at_one_hundred_percent()
    {
        var view = At("2027-08-18T23:30:00Z"); // 00:30 BST on 19 Aug
        Assert.Equal(ForgeProgrammeStatus.Graduated, view.Status);
        Assert.Equal(new DateOnly(2027, 8, 19), view.LocalToday);
        Assert.Equal(261, view.DayNumber);
        Assert.Equal(1m, view.CalendarProgress);
        Assert.Null(view.Phase);
    }

    [Fact]
    public void Local_dates_follow_british_summer_time_without_shifting_day_numbers()
    {
        // Clocks go forward on 28 Mar 2027 and back on 31 Oct 2027 in Europe/London.
        var beforeChange = At("2027-03-27T23:30:00Z"); // 23:30 GMT on 27 Mar
        var afterChange = At("2027-03-28T23:30:00Z");  // 00:30 BST on 29 Mar
        Assert.Equal(new DateOnly(2027, 3, 27), beforeChange.LocalToday);
        Assert.Equal(new DateOnly(2027, 3, 29), afterChange.LocalToday);
        Assert.Equal(beforeChange.DayNumber + 2, afterChange.DayNumber);
        Assert.Equal(new DateOnly(2027, 3, 27).DayNumber - ForgeCalendar.DefaultStart.DayNumber + 1, beforeChange.DayNumber);
    }

    [Fact]
    public void Progress_is_monotonic_and_never_exceeds_one()
    {
        var previous = -1m;
        for (var day = ForgeCalendar.DefaultStart; day <= ForgeCalendar.DefaultGraduation.AddDays(2); day = day.AddDays(1))
        {
            var view = ForgeCalendar.Evaluate(new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), London);
            Assert.InRange(view.CalendarProgress, 0m, 1m);
            Assert.True(view.CalendarProgress >= previous);
            previous = view.CalendarProgress;
        }
    }

    [Fact]
    public void Invalid_phase_configuration_is_rejected()
    {
        var start = ForgeCalendar.DefaultStart; var end = ForgeCalendar.DefaultGraduation;
        Assert.Throws<ArgumentException>(() => ForgeCalendar.ValidatePhases(start, end, []));
        Assert.Throws<ArgumentException>(() => ForgeCalendar.ValidatePhases(start, end, [new("a", "A", start, end.AddDays(-1))]));
        Assert.Throws<ArgumentException>(() => ForgeCalendar.ValidatePhases(start, end,
            [new("a", "A", start, new(2026, 12, 31)), new("b", "B", new(2027, 1, 2), end)]));
        Assert.Throws<ArgumentException>(() => ForgeCalendar.ValidatePhases(end, start, [new("a", "A", end, start)]));
        ForgeCalendar.ValidatePhases(start, end, ForgeCalendar.DefaultPhases);
    }

    [Theory]
    [InlineData("2026-12-31", "foundation")]
    [InlineData("2027-01-01", "market-readiness")]
    [InlineData("2027-02-28", "market-readiness")]
    [InlineData("2027-03-01", "acceleration")]
    [InlineData("2027-04-30", "acceleration")]
    [InlineData("2027-05-01", "authority")]
    [InlineData("2027-06-30", "authority")]
    [InlineData("2027-07-01", "consolidation")]
    [InlineData("2027-08-18", "consolidation")]
    public void Every_phase_transition_uses_inclusive_local_dates(string date, string phase) =>
        Assert.Equal(phase, At(date + "T12:00:00Z").Phase!.Key);

    [Fact]
    public void Technical_checkpoint_is_day_182_and_calendar_progress_is_not_a_score()
    {
        var view = At("2027-05-31T12:00:00Z");
        Assert.Equal(182, view.DayNumber);
        Assert.Equal(ForgeCalendar.TechnicalCheckpoint, view.LocalToday);
        Assert.Equal(0.6961m, view.CalendarProgress);
    }

    [Fact]
    public void Autumn_repeated_hour_keeps_the_same_local_day_and_frozen_progress()
    {
        var first = At("2027-10-31T00:30:00Z"); // 01:30 BST
        var second = At("2027-10-31T01:30:00Z"); // 01:30 GMT
        Assert.Equal(new DateOnly(2027, 10, 31), first.LocalToday);
        Assert.Equal(first, second);
        Assert.Equal(ForgeProgrammeStatus.Graduated, second.Status);
        Assert.Equal(1m, second.CalendarProgress);
    }

    [Fact]
    public void Spring_clock_jump_keeps_the_same_local_day()
    {
        var before = At("2027-03-28T00:30:00Z"); // 00:30 GMT
        var after = At("2027-03-28T01:30:00Z"); // 02:30 BST
        Assert.Equal(before, after);
        Assert.Equal(new DateOnly(2027, 3, 28), after.LocalToday);
    }

    [Fact]
    public void Summer_midnight_starts_and_ends_graduation_on_the_local_date()
    {
        Assert.Equal(new DateOnly(2027, 8, 18), At("2027-08-17T23:00:00Z").LocalToday);
        Assert.Equal(ForgeProgrammeStatus.Active, At("2027-08-18T22:59:59Z").Status);
        Assert.Equal(ForgeProgrammeStatus.Graduated, At("2027-08-18T23:00:00Z").Status);
    }

    [Fact]
    public void Custom_single_day_programme_has_one_day_and_no_division_by_zero()
    {
        var date = new DateOnly(2028, 2, 29);
        ForgePhase[] phases = [new("single", "One day", date, date)];
        var view = ForgeCalendar.Evaluate(DateTimeOffset.Parse("2028-02-29T12:00:00Z"), London, date, date, phases);
        Assert.Equal(1, view.ProgrammeDays);
        Assert.Equal(1, view.DayNumber);
        Assert.Equal(1m, view.CalendarProgress);
        Assert.Equal("single", view.Phase!.Key);
    }

    [Fact]
    public void Overlapping_phases_and_duplicate_keys_are_rejected()
    {
        var start = ForgeCalendar.DefaultStart; var end = ForgeCalendar.DefaultGraduation;
        Assert.Throws<ArgumentException>(() => ForgeCalendar.ValidatePhases(start, end,
            [new("a", "A", start, new(2026, 12, 31)), new("b", "B", new(2026, 12, 31), end)]));
        Assert.Throws<ArgumentException>(() => ForgeCalendar.ValidatePhases(start, end,
            [new("a", "A", start, new(2026, 12, 31)), new("a", "B", new(2027, 1, 1), end)]));
        Assert.Throws<ArgumentException>(() => ForgeCalendar.ProgrammeDays(end, start));
    }
}
