using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Tests;

public sealed class IntelligenceRuntimeTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Audio_tickets_are_single_use_and_stale_fences_cannot_release_replacement()
    {
        var clock = new Clock(); var store = new InMemoryRuntimeStore(); var sessions = new InMemorySessionStore();
        var devices = new DeviceService(store, clock);
        var audio = new AudioOwnership(store, sessions, devices, clock, new MemoryCoreTransaction(store, sessions, new()));
        await devices.RegisterAsync(new("phone", "Test phone", DeviceKind.AndroidCompanion, new(true, true, true, false, [])));
        var id = Guid.NewGuid(); await sessions.AddAsync(new(id, ActivityType.Communication, "Voice", SessionStatus.Active, "phone", null, clock.Now, clock.Now, []));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => audio.StartAsync(new(id, "phone", true), default))).StatusCode);
        await devices.VerifyAsync("phone");
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => audio.StartAsync(new(id, "phone", false), default))).StatusCode);
        var first = await audio.StartAsync(new(id, "phone", true), default);
        Assert.Equal(first.Lease, await audio.ConsumeAsync(first.Ticket, default));
        Assert.Equal(401, (await Assert.ThrowsAsync<DomainException>(() => audio.ConsumeAsync(first.Ticket, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => audio.StartAsync(new(id, "phone", true), default))).StatusCode);
        clock.Now = clock.Now.AddSeconds(16);
        Assert.False(await audio.CurrentAsync(first.Lease, default));
        var second = await audio.StartAsync(new(id, "phone", true), default);
        Assert.True(second.Lease.Fence > first.Lease.Fence);
        Assert.False(await audio.ReleaseAsync(first.Lease, default));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => audio.RenewAsync(first.Lease, default))).StatusCode);
        Assert.True(await audio.CurrentAsync(second.Lease, default));
        await new AgentRuntime(store, clock).ControlsAsync(new(Paused: true), default);
        Assert.True(await audio.CurrentAsync(second.Lease, default));
    }

    [Fact]
    public async Task Concurrent_agent_claims_are_bounded_and_review_waits_for_specialists()
    {
        var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, new Clock());
        var request = new StartAgentRunRequest(Guid.NewGuid(), "daily-coaching", false);
        var run = await runtime.StartAsync(request, default);
        Assert.Equal(run.Run.Id, (await runtime.StartAsync(request, default)).Run.Id);
        var claimed = (await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => runtime.ClaimAsync(default)))).OfType<AgentTask>().ToList();
        Assert.Equal(2, claimed.Count); Assert.DoesNotContain(claimed, x => x.Role == AgentRole.Review);
        foreach (var task in claimed) await runtime.CompleteAsync(task.Id, task.ClaimToken!.Value, "draft", null, false, default);
        var review = await runtime.ClaimAsync(default); Assert.Equal(AgentRole.Review, review!.Role);
        await runtime.CompleteAsync(review.Id, review.ClaimToken!.Value, "review", null, false, default);
        await runtime.TickAsync(default); await runtime.TickAsync(default);
        var notification = Assert.Single(await store.ReadAsync(s => s.AgentOutbox.Values.ToList()));
        Assert.True(notification.Delivered);
        await runtime.CancelAsync(run.Run.Id, true, default);
        Assert.Empty(await store.ReadAsync(s => s.AgentOutbox));
        Assert.All(await store.ReadAsync(s => s.AgentTasks.Values.ToList()), task => { Assert.Null(task.Output); Assert.Empty(task.Input); });
    }

    [Fact]
    public async Task Scheduled_occurrences_are_deduplicated_and_quiet_hours_hold_notifications()
    {
        var clock = new Clock { Now = DateTimeOffset.Parse("2026-11-13T23:00Z") };
        var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, clock);
        await runtime.SaveScheduleAsync(new(Guid.NewGuid(), null, "Evening practice", "practice-reminder", "Europe/London", "23:00", true, false), default);
        Assert.Equal(1, await runtime.TickAsync(default)); Assert.Equal(0, await runtime.TickAsync(default));
        var task = await runtime.ClaimAsync(default); Assert.NotNull(task);
        await runtime.CompleteAsync(task.Id, task.ClaimToken!.Value, "local reminder", null, false, default);
        await runtime.TickAsync(default);
        Assert.False(Assert.Single(await store.ReadAsync(s => s.AgentOutbox.Values.ToList())).Delivered);
        clock.Now = clock.Now.AddHours(9); await runtime.TickAsync(default);
        // A six-hour-old notice expires rather than being delivered as fresh morning advice.
        Assert.False(Assert.Single(await store.ReadAsync(s => s.AgentOutbox.Values.ToList())).Delivered);
        Assert.Single(await store.ReadAsync(s => s.AgentRuns));
    }

    [Fact]
    public async Task Cancel_pause_and_source_change_reject_late_publication()
    {
        var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, new Clock());
        var run = await runtime.StartAsync(new(Guid.NewGuid(), "practice-reminder", false), default);
        var task = (await runtime.ClaimAsync(default))!;
        await runtime.ControlsAsync(new(Paused: true), default);
        Assert.Null(await runtime.ClaimAsync(default));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => runtime.CompleteAsync(task.Id, task.ClaimToken!.Value, "late", null, false, default))).StatusCode);
        await runtime.ControlsAsync(new(), default);
        await store.UpdateAsync(s => { s.AgentTasks[task.Id] = s.AgentTasks[task.Id] with { SourceRefs = [new("story", Guid.NewGuid(), "1")] }; return true; });
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => runtime.CompleteAsync(task.Id, task.ClaimToken!.Value, "stale", null, false, default))).StatusCode);
        await runtime.CancelAsync(run.Run.Id, false, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => runtime.RenewAsync(task.Id, task.ClaimToken!.Value, default))).StatusCode);
    }

    [Theory]
    [InlineData("research-review")] [InlineData("career-plan")] [InlineData("personal-plan")]
    public async Task Cloud_only_workflows_require_consent_before_creating_work(string workflow)
    {
        var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, new Clock());
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => runtime.StartAsync(new(Guid.NewGuid(), workflow, false), default))).StatusCode);
        Assert.Empty(await store.ReadAsync(s => s.AgentRuns));
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => runtime.SaveScheduleAsync(new(Guid.NewGuid(), null, "Goal", workflow, "Europe/London", "12:00", true, false), default))).StatusCode);
    }

    [Fact]
    public async Task Shared_budget_caps_all_lanes_and_uncertain_outcomes_are_never_replayed()
    {
        var store = new InMemoryRuntimeStore(); var budget = new UsageBudget(store, new Clock(), new TestBudgetSource(new Clock())); var limits = new BudgetLimits(1, 5, 2, 2, 2);
        var id = Guid.NewGuid(); await budget.ReserveAsync(id, Guid.NewGuid(), "test", .6m, limits, default, UsageLane.Voice, "openai");
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(id, Guid.NewGuid(), "test", .6m, limits, default, UsageLane.Voice))).StatusCode);
        Assert.Equal(429, (await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "test", .5m, limits, default, UsageLane.Background))).StatusCode);
        var settlement = await budget.SettleAsync(id, .7m, 1, 1, default); Assert.True(settlement.ReconciliationRequired);
        Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "test", .1m, limits, default, UsageLane.Interactive))).StatusCode);
    }
}
