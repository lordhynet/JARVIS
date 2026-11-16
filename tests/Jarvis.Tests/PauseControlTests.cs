using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Tests;

public sealed class PauseControlTests
{
    [Fact]
    public async Task Narrow_paid_pause_read_preserves_legacy_and_independent_flags()
    {
        var store = new InMemoryRuntimeStore();
        await store.UpdateAsync(s => { s.AgentControls = new(Paused: true); return true; });
        Assert.True(await store.ReadPaidAiPausedAsync());
        await store.UpdateAsync(s => { s.AgentControls = new(PaidAiPaused: false, AgentsPaused: true); return true; });
        Assert.False(await store.ReadPaidAiPausedAsync());
    }

    [Fact]
    public async Task Historical_pause_loads_both_controls_and_new_flags_resume_independently()
    {
        var legacy = JsonSerializer.Deserialize<AgentControls>("{\"Paused\":true}")!;
        Assert.True(legacy.EffectivePaidAiPaused); Assert.True(legacy.EffectiveAgentsPaused);
        var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, TimeProvider.System);
        var fence = new AudioLease(Guid.NewGuid(), "phone", 5, DateTimeOffset.UtcNow.AddSeconds(15));
        await store.UpdateAsync(s => { s.AudioLease = fence; s.AgentControls = legacy; return true; });
        var updated = await runtime.ControlsAsync(new(PaidAiPaused: true, AgentsPaused: false), default);
        Assert.True(updated.PaidAiPaused); Assert.False(updated.AgentsPaused); Assert.False(updated.Paused);
        Assert.Equal(fence, await store.ReadAsync(s => s.AudioLease));
        await runtime.StartAsync(new(Guid.NewGuid(), "practice-reminder", false), default); Assert.NotNull(await runtime.ClaimAsync(default));
        var budget = new UsageBudget(store, TimeProvider.System, new TestBudgetSource(TimeProvider.System));
        await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .001m, new(1, 1, 1), default));
        await runtime.ControlsAsync(new(PaidAiPaused: false, AgentsPaused: true), default);
        Assert.Null(await runtime.ClaimAsync(default));
        Assert.NotNull(await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .001m, new(1, 1, 1), default));
        Assert.Equal(fence, await store.ReadAsync(s => s.AudioLease));
    }
    [Fact]
    public void Neither_pause_blocks_unpaid_audio_and_erasure_still_keeps_both_work_types_paused()
    {
        var state = new RuntimeState { AgentControls = new(Paused: true) };
        AudioFencing.RequireAvailable(state, DateTimeOffset.UtcNow);
        var erased = PrivacyData.EmptyAfterErase(state);
        Assert.True(erased.AgentControls.EffectivePaidAiPaused); Assert.True(erased.AgentControls.EffectiveAgentsPaused);
        Assert.Null(erased.AudioLease);
    }
}
