using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Tests;

public sealed class IdleWorkTests
{
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
}
