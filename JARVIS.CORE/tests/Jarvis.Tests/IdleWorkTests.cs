using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Tests;

public sealed class IdleWorkTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingStore : IRuntimeStore
    {
        private readonly InMemoryRuntimeStore inner = new();
        public int Writes;
        public Task<T> ReadAsync<T>(Func<RuntimeState,T> read, CancellationToken ct = default) => inner.ReadAsync(read, ct);
        public Task<T> UpdateAsync<T>(Func<RuntimeState,T> update, CancellationToken ct = default) { Writes++; return inner.UpdateAsync(update, ct); }
    }
    [Fact]
    public async Task Idle_agent_tick_and_claim_do_not_enter_write_transactions()
    {
        var store = new CountingStore(); var runtime = new AgentRuntime(store, TimeProvider.System);
        for (var i = 0; i < 10; i++) { Assert.Equal(0, await runtime.TickAsync(default)); Assert.Null(await runtime.ClaimAsync(default)); }
        Assert.Equal(0, store.Writes);
        await runtime.SaveScheduleAsync(new(Guid.NewGuid(), null, "Paused schedule", "practice-reminder", "UTC", "00:00", false, false), default);
        store.Writes = 0;
        Assert.Equal(0, await runtime.TickAsync(default)); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Tick_eligibility_matches_deadline_expiration_transition()
    {
        var clock = new Clock(); var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, clock);
        var deadline = clock.Now.AddHours(1); var id = Guid.NewGuid();
        await runtime.SaveGoalAsync(new(id, null, "Deadline", "Expire this goal", "practice-reminder", false,
            clock.Now, deadline), default);
        clock.Now = deadline.AddSeconds(1);
        var catalog = WorkflowCatalog.BuiltIns.ToDictionary(x => x.Key, StringComparer.Ordinal);
        Assert.True(AgentEligibility.HasTickWork(await store.ReadAsync(s => s), clock.Now, catalog));
        Assert.Equal(0, await runtime.TickAsync(default));
        Assert.Equal("Expired", await store.ReadAsync(s => s.Goals[id].Status));
    }

    [Fact]
    public async Task Claim_eligibility_matches_stale_lease_recovery_transition()
    {
        var clock = new Clock(); var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, clock);
        var run = await runtime.StartAsync(new(Guid.NewGuid(), "practice-reminder", false, "brief"), default);
        var claimed = Assert.IsType<AgentTask>(await runtime.ClaimAsync(default));
        Assert.Equal(run.Run.Id, claimed.RunId);
        clock.Now = clock.Now.AddMinutes(2);
        Assert.True(AgentEligibility.HasClaimWork(await store.ReadAsync(s => s), clock.Now));
        Assert.Null(await runtime.ClaimAsync(default));
        Assert.Equal(AgentTaskStatus.RetryScheduled, await store.ReadAsync(s => s.AgentTasks[claimed.Id].Status));
    }
}
