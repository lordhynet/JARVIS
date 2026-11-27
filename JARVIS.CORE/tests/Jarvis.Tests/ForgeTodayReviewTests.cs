using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class ForgeTodayReviewTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-12-02T07:30:00Z");
    internal static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-12-03T00:00:00Z");
    internal static ForgeProgramme Programme() => new(Guid.NewGuid(), ForgeProgrammePolicy.OwnerScope, 1, 1,
        ForgeCalendar.DefaultStart, ForgeCalendar.DefaultGraduation, ForgeCalendar.DefaultTimeZone, false, Now.AddDays(-1), Now.AddDays(-1));
    internal static PersonalGoal Goal(string title, string division, int deadlineDays) => new(Guid.NewGuid(), 1, title, "Fixture", "practice-reminder", false,
        Now, End.AddDays(deadlineDays), GoalStatus.Active, Now, Now, DivisionKey: division);

    [Fact]
    public void Unknown_interview_time_does_not_override_canonical_candidate_order()
    {
        var programme = Programme(); var goals = new[] { Goal("Core due today", "forge.tech", 0), Goal("Interview later", "forge.career", 10) };
        var candidates = ForgePlanCandidates.Build(End, [], goals, Now);
        Assert.Equal("goal." + goals[0].Id.ToString("N"), candidates.Candidates[0].Key);
        var briefing = ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, Now).Calendar, candidates, goals, 0);
        Assert.Equal("TECH: Core due today", briefing.Top[0].Label);
    }

    [Fact]
    public async Task Next_midnight_card_belongs_to_tomorrow_not_today()
    {
        IRuntimeStore store = new InMemoryRuntimeStore();
        await store.UpdateAsync(s => { var id = Guid.NewGuid(); s.Learning[id] = new(id, LearningDomain.French, "Fixture", "Fixture", 1, 5, End, Now); return true; });
        var result = await new ForgeTodayService(new AvailabilityFixture(true), new Programmes(Programme()), store).BuildAsync(Now);
        Assert.NotNull(result); Assert.Equal(0, result.DueReviews); Assert.Empty(result.Top);
    }

    [Theory]
    [InlineData("date")] [InlineData("revision")] [InlineData("capacity")] [InlineData("budget")] [InlineData("sum")]
    [InlineData("key")] [InlineData("kind")] [InlineData("overlap")] [InlineData("fractional")] [InlineData("outside")] [InlineData("duplicate")]
    public void Stale_or_unversioned_plan_is_not_reported_as_scheduled(string fault)
    {
        var programme = Programme(); var goals = new[] { Goal("Current work", "forge.tech", 0), Goal("Other work", "forge.voice", 0) };
        var candidates = ForgePlanCandidates.Build(End, [], goals, Now);
        var key = "goal." + goals[0].Id.ToString("N"); var other = "goal." + goals[1].Id.ToString("N");
        var plan = new ForgeDayPlan(ForgePlanStatus.Planned, new(2026, 12, 2), 1, 1, 120, 60,
            [new(key, ForgePlanTaskKind.Practice, Now, Now.AddHours(1), false)], []);
        plan = fault switch {
            "date" => plan with { LocalDate = new(2026, 12, 1) }, "revision" => plan with { ProgrammeRevision = 99 },
            "capacity" => plan with { CapacityRevision = 0 }, "budget" => plan with { AllocatableMinutes = 30 }, "sum" => plan with { PlannedMinutes = 59 },
            "key" => plan with { Scheduled = [plan.Scheduled[0] with { Key = "missing" }] },
            "kind" => plan with { Scheduled = [plan.Scheduled[0] with { Kind = ForgePlanTaskKind.Review }] },
            "overlap" => plan with { PlannedMinutes = 90, Scheduled = [plan.Scheduled[0], new(other, ForgePlanTaskKind.Practice, Now.AddMinutes(30), Now.AddHours(1), false)] },
            "fractional" => plan with { Scheduled = [plan.Scheduled[0] with { End = Now.AddHours(1).AddTicks(1) }] },
            "outside" => plan with { Scheduled = [plan.Scheduled[0] with { Start = Now.AddDays(1), End = Now.AddDays(1).AddHours(1) }] },
            _ => plan with { PlannedMinutes = 120, Scheduled = [plan.Scheduled[0], new(key, ForgePlanTaskKind.Practice, Now.AddHours(1), Now.AddHours(2), false)] }
        };
        var briefing = ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, Now).Calendar, candidates, goals, 0, plan);
        Assert.False(briefing.Scheduled);
    }

    [Fact]
    public void Valid_shortened_current_plan_uses_placed_minutes_without_exposing_capacity()
    {
        var programme = Programme(); var goals = new[] { Goal("Current work", "forge.tech", 0) }; var candidates = ForgePlanCandidates.Build(End, [], goals, Now);
        var plan = new ForgeDayPlan(ForgePlanStatus.Planned, new(2026, 12, 2), 1, 1, 120, 15,
            [new(candidates.Candidates[0].Key, ForgePlanTaskKind.Practice, Now, Now.AddMinutes(15), true)], []);
        var result = ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, Now).Calendar, candidates, goals, 0, plan);
        Assert.True(result.Scheduled); Assert.Equal(15, Assert.Single(result.Top).Minutes);
    }

    [Theory]
    [InlineData(1, "CAREER: Interview later")] [InlineData(30, "TECH: Core due today")]
    public void Confirmed_interview_context_keeps_the_allocator_selection_order(int interviewDays, string expected)
    {
        var programme = Programme(); var goals = new[] { Goal("Core due today", "forge.tech", 0), Goal("Interview later", "forge.career", 10) };
        var candidates = ForgePlanCandidates.Build(End, [], goals, Now, Now.AddDays(interviewDays));
        Assert.Equal(expected, ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, Now).Calendar, candidates, goals, 0).Top[0].Label);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Disabled_or_missing_programme_does_not_read_personal_runtime(bool enabled)
    {
        IForgeProgrammeReader source = enabled ? new Programmes() : new ForbiddenProgrammes();
        Assert.Null(await new ForgeTodayService(new AvailabilityFixture(enabled), source, new ForbiddenRuntime()).BuildAsync(Now));
    }

    [Fact]
    public async Task Canonical_ticks_on_both_sides_of_midnight_are_preserved()
    {
        IRuntimeStore store = new InMemoryRuntimeStore();
        await store.UpdateAsync(s => { foreach (var at in new[] { End.AddTicks(-1), End, End.AddTicks(1) }) {
            var id = Guid.NewGuid(); s.Learning[id] = new(id, LearningDomain.French, "Fixture", "Fixture", 1, 5, at, Now);
        } return true; });
        Assert.Equal(1, (await new ForgeTodayService(new AvailabilityFixture(true), new Programmes(Programme()), store).BuildAsync(Now))!.DueReviews);
    }

    [Fact]
    public async Task Goal_attention_at_next_midnight_is_not_suggested_today()
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); var tomorrow = Goal("Tomorrow work", "forge.tech", 2) with { NextReviewAt = End };
        var today = Goal("Today work", "forge.tech", 2) with { NextReviewAt = End.AddTicks(-1) };
        await store.UpdateAsync(s => { s.Goals[tomorrow.Id] = tomorrow; s.Goals[today.Id] = today; return true; });
        var result = await new ForgeTodayService(new AvailabilityFixture(true), new Programmes(Programme()), store).BuildAsync(Now);
        Assert.Equal("TECH: Today work", Assert.Single(result!.Top).Label);
    }

    [Theory]
    [InlineData("2027-03-28T00:30:00Z", "2027-03-28T23:00:00Z")]
    [InlineData("2026-10-25T00:30:00Z", "2026-10-26T00:00:00Z")]
    public async Task Local_day_end_uses_the_boundary_offset_across_dst(string now, string end)
    {
        var at = DateTimeOffset.Parse(now); var boundary = DateTimeOffset.Parse(end); IRuntimeStore store = new InMemoryRuntimeStore();
        await store.UpdateAsync(s => { var id = Guid.NewGuid(); s.Learning[id] = new(id, LearningDomain.French, "Fixture", "Fixture", 1, 5, boundary.AddTicks(-1), at); return true; });
        var reader = new CutoffReader(store); var result = await new ForgeTodayService(new AvailabilityFixture(true), new Programmes(Programme()), reader).BuildAsync(at);
        Assert.Equal(boundary, reader.Cutoff); Assert.Equal(1, result!.DueReviews);
    }

    [Fact]
    public void Every_protected_division_omits_goal_title_identity_and_deadline_reason()
    {
        foreach (var division in ForgeDivisions.All.Where(d => d.Protected)) {
            var programme = Programme(); var goal = Goal("PRIVATE_SENTINEL_" + division.Code, division.Key, 7); var candidates = ForgePlanCandidates.Build(End, [], [goal], Now);
            var result = ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, Now).Calendar, candidates, [goal], 0);
            var item = Assert.Single(result.Top); Assert.Equal(division.Code + " goal", item.Label);
            if (!division.Optional) Assert.Equal("goal review", item.Why);
            var json = System.Text.Json.JsonSerializer.Serialize(result); Assert.DoesNotContain(goal.Title, json, StringComparison.Ordinal);
            Assert.DoesNotContain(goal.Id.ToString("N"), json, StringComparison.Ordinal); Assert.DoesNotContain(goal.Deadline.ToString("O"), json, StringComparison.Ordinal);
        }
    }

    private sealed class ForbiddenProgrammes : IForgeProgrammeReader
    {
        public Task<ForgeProgramme?> GetAsync(string ownerScope, Guid id, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected programme read");
        public Task<IReadOnlyList<ForgeProgramme>> ListAsync(string ownerScope, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected programme read");
    }
    private sealed class ForbiddenRuntime : IRuntimeReader
    {
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected personal runtime read");
    }
    private sealed class CutoffReader(IRuntimeReader inner) : IRuntimeReader
    {
        public DateTimeOffset Cutoff { get; private set; }
        public Task<IReadOnlyList<LearningItem>> ReadLearningItemsAsync(bool dueOnly, DateTimeOffset cutoff, CancellationToken ct = default) { Cutoff = cutoff; return inner.ReadLearningItemsAsync(dueOnly, cutoff, ct); }
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => inner.ReadAsync(read, ct);
    }

    internal sealed class AvailabilityFixture(bool on) : IForgeAvailability { public bool Enabled => on; }
    internal sealed class Programmes(params ForgeProgramme[] items) : IForgeProgrammeReader
    {
        public Task<ForgeProgramme?> GetAsync(string ownerScope, Guid id, CancellationToken ct = default) => Task.FromResult(items.FirstOrDefault(p => p.Id == id));
        public Task<IReadOnlyList<ForgeProgramme>> ListAsync(string ownerScope, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ForgeProgramme>>(items);
    }
}
