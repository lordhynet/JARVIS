using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class ForgeDayAllocatorTests
{
    private static readonly DateOnly Day = new(2026, 12, 1); // GMT, offset 0
    private static readonly DateTimeOffset Midnight = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static ForgeProgramme Programme(bool paused = false) => new(Guid.NewGuid(), "owner", 1, 1, Day, new(2027, 8, 18), "Europe/London", paused,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static ForgeCapacityLocalInterval Block(int fromHour, int toHour, int offsetHours = 0, DateOnly? day = null)
    {
        var d = day ?? Day;
        var end = toHour == 24 ? d.AddDays(1).ToDateTime(TimeOnly.MinValue) : d.ToDateTime(new TimeOnly(toHour, 0));
        return new(d.ToDateTime(new TimeOnly(fromHour, 0)), end, "Europe/London", TimeSpan.FromHours(offsetHours), TimeSpan.FromHours(offsetHours));
    }
    // Sleep 00-07, work 09-17, sleep 23-24: open 07-09 (120) and 17-23 (360).
    private static ForgeCapacityProjection Typical(int net = 180, ForgeProgramme? programme = null) =>
        ForgeCapacityProjection.ForDay(programme ?? Programme(), 1, Day, net, 30, [Block(0, 7), Block(9, 17), Block(23, 24)]);
    private static ForgePlanCandidate Task(string key, ForgePlanTaskKind kind, int minutes, int minimum = 0, int priority = 50) =>
        new(key, kind, minutes, minimum == 0 ? minutes : minimum, priority);

    private static void AssertSound(ForgeCapacityProjection capacity, ForgeDayPlan plan)
    {
        Assert.True(plan.PlannedMinutes <= (capacity.AllocatableMinutes ?? 0));
        Assert.Equal(plan.Scheduled.Sum(x => x.Minutes), plan.PlannedMinutes);
        var blocks = capacity.FixedBlocks.Select(x => (S: new DateTimeOffset(x.LocalStart, x.StartOffset), E: new DateTimeOffset(x.LocalEnd, x.EndOffset))).ToArray();
        var ordered = plan.Scheduled.OrderBy(x => x.Start).ToArray();
        for (var i = 0; i < ordered.Length; i++) {
            Assert.True(ordered[i].End > ordered[i].Start);
            Assert.DoesNotContain(blocks, b => ordered[i].Start < b.E && b.S < ordered[i].End);
            if (i > 0) Assert.True(ordered[i].Start >= ordered[i - 1].End);
        }
    }

    [Fact]
    public void Unknown_paused_and_zero_capacity_plan_nothing_and_give_every_task_a_reason()
    {
        ForgePlanCandidate[] tasks = [Task("review.fr", ForgePlanTaskKind.Review, 15), Task("study.dotnet", ForgePlanTaskKind.Study, 60)];
        var unknown = ForgeDayAllocator.Plan(ForgeCapacityProjection.ForDay(Programme(), 1, Day, null, 30, []), tasks, Midnight);
        Assert.Equal(ForgePlanStatus.CapacityUnknown, unknown.Status); Assert.Empty(unknown.Scheduled);
        Assert.All(unknown.Deferred, x => Assert.Equal(ForgeDeferReason.CapacityUnknown, x.Reason)); Assert.Equal(2, unknown.Deferred.Count);
        var paused = ForgeDayAllocator.Plan(Typical(programme: Programme(paused: true)), tasks, Midnight);
        Assert.Equal(ForgePlanStatus.ProgrammePaused, paused.Status); Assert.Empty(paused.Scheduled);
        Assert.All(paused.Deferred, x => Assert.Equal(ForgeDeferReason.ProgrammePaused, x.Reason));
        var rest = ForgeDayAllocator.Plan(Typical(net: 0), tasks, Midnight);
        Assert.Equal(ForgePlanStatus.NoCapacity, rest.Status); Assert.Equal(0, rest.PlannedMinutes); Assert.Null(unknown.AllocatableMinutes);
    }

    [Fact]
    public void Never_exceeds_allocatable_minutes_and_never_overlaps_fixed_blocks_or_other_items()
    {
        var capacity = Typical(net: 180);
        ForgePlanCandidate[] tasks = [
            Task("review.fr", ForgePlanTaskKind.Review, 20), Task("practice.es", ForgePlanTaskKind.Practice, 45),
            Task("study.dotnet", ForgePlanTaskKind.Study, 90, 30), Task("study.systems", ForgePlanTaskKind.Study, 90, 60),
            Task("optional.reading", ForgePlanTaskKind.Optional, 30)];
        var plan = ForgeDayAllocator.Plan(capacity, tasks, Midnight);
        AssertSound(capacity, plan);
        // review 20 + practice 45 + dotnet 90 = 155; 25 left is below systems' 60 minimum and optional's 30.
        Assert.Equal(155, plan.PlannedMinutes);
        Assert.Equal(tasks.Length, plan.Scheduled.Count + plan.Deferred.Count);
        Assert.Equal(new[] { "optional.reading", "study.systems" }, plan.Deferred.Select(x => x.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.All(plan.Deferred, x => Assert.Equal(ForgeDeferReason.BudgetExhausted, x.Reason));
        // The 90-minute task didn't fit the 55 minutes left before work, so it went to the evening window intact.
        Assert.Contains(plan.Scheduled, x => x.Key == "study.dotnet" && !x.Shortened && x.Start == Midnight.AddHours(17));
    }

    [Fact]
    public void Reviews_go_first_and_ordering_is_deterministic_regardless_of_input_order()
    {
        var capacity = Typical(net: 120);
        ForgePlanCandidate[] tasks = [Task("study.a", ForgePlanTaskKind.Study, 60, priority: 1), Task("review.b", ForgePlanTaskKind.Review, 20, priority: 90),
            Task("practice.c", ForgePlanTaskKind.Practice, 30, priority: 10), Task("optional.d", ForgePlanTaskKind.Optional, 30, priority: 0)];
        var first = ForgeDayAllocator.Plan(capacity, tasks, Midnight);
        var second = ForgeDayAllocator.Plan(capacity, tasks.Reverse().ToArray(), Midnight);
        Assert.Equal(first.Scheduled, second.Scheduled); Assert.Equal(first.Deferred, second.Deferred);
        Assert.Equal("review.b", first.Scheduled.OrderBy(x => x.Start).First().Key);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 7, 0, 0, TimeSpan.Zero), first.Scheduled.OrderBy(x => x.Start).First().Start);
        Assert.Contains(first.Deferred, x => x.Key == "optional.d" && x.Reason == ForgeDeferReason.BudgetExhausted);
    }

    [Fact]
    public void Interview_within_seven_days_moves_practice_ahead_and_within_24_hours_defers_optional_work()
    {
        var capacity = Typical(net: 90);
        ForgePlanCandidate[] tasks = [Task("study.a", ForgePlanTaskKind.Study, 60, priority: 0), Task("interview.story", ForgePlanTaskKind.InterviewPractice, 60, priority: 50),
            Task("optional.d", ForgePlanTaskKind.Optional, 15, priority: 0)];
        var noInterview = ForgeDayAllocator.Plan(capacity, tasks, Midnight);
        Assert.Contains(noInterview.Scheduled, x => x.Key == "study.a" && !x.Shortened);
        var inFiveDays = ForgeDayAllocator.Plan(capacity, tasks, Midnight, Midnight.AddDays(5));
        Assert.Contains(inFiveDays.Scheduled, x => x.Key == "interview.story" && !x.Shortened);
        Assert.Equal("interview.story", inFiveDays.Scheduled.OrderBy(x => x.Start).First().Key);
        var tomorrow = ForgeDayAllocator.Plan(capacity, tasks, Midnight, Midnight.AddHours(20));
        Assert.Contains(tomorrow.Deferred, x => x.Key == "optional.d" && x.Reason == ForgeDeferReason.InterviewWithin24Hours);
        AssertSound(capacity, tomorrow);
        // Sleep and work blocks are untouched by urgency: nothing is placed inside them.
        Assert.All(tomorrow.Scheduled, x => Assert.True(x.Start >= Midnight.AddHours(7)));
    }

    [Fact]
    public void Tasks_without_a_long_enough_window_are_deferred_not_split_across_blocks()
    {
        // Open windows are only 07-09 (120) and 17-23 (360). A 400-minute minimum fits nowhere.
        var capacity = Typical(net: 450); // 1440 - 960 blocked - 30 buffer
        var plan = ForgeDayAllocator.Plan(capacity, [Task("deep.work", ForgePlanTaskKind.Study, 420, 400)], Midnight);
        Assert.Empty(plan.Scheduled);
        Assert.Equal(ForgeDeferReason.NoOpenWindow, Assert.Single(plan.Deferred).Reason);
    }

    [Fact]
    public void Replanning_later_in_the_day_never_places_work_in_the_past()
    {
        var capacity = Typical(net: 180);
        var now = new DateTimeOffset(2026, 12, 1, 18, 30, 20, TimeSpan.Zero);
        var plan = ForgeDayAllocator.Plan(capacity, [Task("review.fr", ForgePlanTaskKind.Review, 20), Task("study.a", ForgePlanTaskKind.Study, 60)], now);
        Assert.All(plan.Scheduled, x => Assert.True(x.Start >= now));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 18, 31, 0, TimeSpan.Zero), plan.Scheduled.OrderBy(x => x.Start).First().Start);
        AssertSound(capacity, plan);
    }

    [Fact]
    public void Quiet_hours_unioned_by_policy_are_respected_without_double_counting()
    {
        var programme = Programme();
        var owner = ForgeCapacityProjection.ForDay(programme, 1, Day, 120, 0, [Block(0, 7), Block(9, 17)]);
        // Global quiet hours 21-24 overlap nothing the owner entered; the union blocks them once.
        var capacity = ForgeCapacityProjection.WithPolicyQuietHours(programme, owner, [Block(21, 24)]);
        var plan = ForgeDayAllocator.Plan(capacity, [Task("study.a", ForgePlanTaskKind.Study, 240, 60), Task("review.b", ForgePlanTaskKind.Review, 20)], Midnight);
        AssertSound(capacity, plan);
        Assert.All(plan.Scheduled, x => Assert.True(x.End <= Midnight.AddHours(21)));
        Assert.Equal(120, plan.PlannedMinutes);
    }

    [Fact]
    public void Autumn_clock_change_day_plans_in_real_time()
    {
        var day = new DateOnly(2027, 10, 31); // 25-hour London day; programme graduates in August, so widen dates.
        var programme = new ForgeProgramme(Guid.NewGuid(), "owner", 1, 1, Day, new(2027, 12, 31), "Europe/London", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var capacity = ForgeCapacityProjection.ForDay(programme, 1, day, 60, 0, [new(day.ToDateTime(TimeOnly.MinValue), day.ToDateTime(new TimeOnly(7, 0)), "Europe/London", TimeSpan.FromHours(1), TimeSpan.Zero), Block(8, 24, 0, day)]);
        var plan = ForgeDayAllocator.Plan(capacity, [Task("review.fr", ForgePlanTaskKind.Review, 60)], DateTimeOffset.MinValue);
        var item = Assert.Single(plan.Scheduled);
        Assert.Equal(new DateTimeOffset(2027, 10, 31, 7, 0, 0, TimeSpan.Zero), item.Start);
        Assert.Equal(60, item.Minutes); AssertSound(capacity, plan);
    }

    [Fact]
    public void Plan_graph_refuses_json_in_both_directions()
    {
        var plan = ForgeDayAllocator.Plan(Typical(), [Task("review.fr", ForgePlanTaskKind.Review, 20), Task("optional.x", ForgePlanTaskKind.Optional, 600)], Midnight);
        Assert.NotEmpty(plan.Scheduled); Assert.NotEmpty(plan.Deferred);
        Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Serialize(plan));
        Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Serialize(plan.Scheduled[0]));
        Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Serialize(plan.Deferred[0]));
        Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Serialize(Task("a", ForgePlanTaskKind.Study, 5)));
        Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Serialize(new object[] { plan }));
        Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Deserialize<ForgeDayPlan>("{}"));
    }

    [Fact]
    public void Day_end_after_day_and_end_of_time_produce_no_windows_without_overflow()
    {
        var capacity = Typical();
        ForgePlanCandidate[] tasks = [Task("review.fr", ForgePlanTaskKind.Review, 20)];
        foreach (var notBefore in new[] { Midnight.AddDays(1), Midnight.AddDays(3), DateTimeOffset.MaxValue,
            new DateTimeOffset(DateTime.MaxValue.AddHours(-3), TimeSpan.FromHours(2)) })
        {
            var plan = ForgeDayAllocator.Plan(capacity, tasks, notBefore);
            Assert.Equal(ForgePlanStatus.Planned, plan.Status); Assert.Empty(plan.Scheduled);
            Assert.Equal(ForgeDeferReason.NoOpenWindow, Assert.Single(plan.Deferred).Reason);
        }
    }

    [Fact]
    public void Spring_forward_window_crossing_the_gap_is_planned_in_elapsed_time()
    {
        // 28 Mar 2027: 01:00 GMT jumps to 02:00 BST (23-hour day). Open 00:00 GMT to 03:00 BST = 120 real minutes.
        var day = new DateOnly(2027, 3, 28);
        var capacity = ForgeCapacityProjection.ForDay(Programme(), 1, day, 120, 0, [Block(3, 24, 1, day)]);
        Assert.Equal(1380, capacity.DayMinutes);
        var plan = ForgeDayAllocator.Plan(capacity, [Task("review.fr", ForgePlanTaskKind.Review, 90), Task("study.a", ForgePlanTaskKind.Study, 30)], DateTimeOffset.MinValue);
        Assert.Equal(120, plan.PlannedMinutes); AssertSound(capacity, plan);
        var first = plan.Scheduled[0];
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 0, 0, 0, TimeSpan.Zero), first.Start);
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 30, 0, TimeSpan.Zero), first.End); // 02:30 BST wall time
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 2, 0, 0, TimeSpan.Zero), plan.Scheduled[1].End); // 03:00 BST, the block start
    }

    [Fact]
    public void Fall_back_window_crossing_the_repeated_hour_is_planned_in_elapsed_time()
    {
        // 31 Oct 2027: 02:00 BST falls back to 01:00 GMT (25-hour day). Open 00:00 BST to 03:00 GMT = 240 real minutes.
        var day = new DateOnly(2027, 10, 31);
        var programme = new ForgeProgramme(Guid.NewGuid(), "owner", 1, 1, Day, new(2027, 12, 31), "Europe/London", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var capacity = ForgeCapacityProjection.ForDay(programme, 1, day, 240, 0, [Block(3, 24, 0, day)]);
        Assert.Equal(1500, capacity.DayMinutes);
        var plan = ForgeDayAllocator.Plan(capacity, [Task("study.a", ForgePlanTaskKind.Study, 180)], DateTimeOffset.MinValue);
        var item = Assert.Single(plan.Scheduled);
        Assert.Equal(new DateTimeOffset(2027, 10, 30, 23, 0, 0, TimeSpan.Zero), item.Start); // 00:00 BST
        Assert.Equal(180, item.Minutes); // crosses the repeated 01:00-02:00 hour without losing or adding time
        Assert.Equal(new DateTimeOffset(2027, 10, 31, 2, 0, 0, TimeSpan.Zero), item.End);
        AssertSound(capacity, plan);
    }

    [Fact]
    public void Invalid_candidates_are_rejected()
    {
        var capacity = Typical();
        void Invalid(params ForgePlanCandidate[] tasks) => Assert.Equal(400, Assert.Throws<DomainException>(() => ForgeDayAllocator.Plan(capacity, tasks, Midnight)).StatusCode);
        Invalid(Task("Bad Key", ForgePlanTaskKind.Study, 30));
        Invalid(Task("a", ForgePlanTaskKind.Study, 0));
        Invalid(new ForgePlanCandidate("a", ForgePlanTaskKind.Study, 30, 40, 1));
        Invalid(Task("a", ForgePlanTaskKind.Study, 30), Task("a", ForgePlanTaskKind.Review, 10));
        Invalid(Task("a", (ForgePlanTaskKind)99, 30));
        Invalid(Enumerable.Range(0, 65).Select(i => Task("t" + i, ForgePlanTaskKind.Study, 5)).ToArray());
    }
}
