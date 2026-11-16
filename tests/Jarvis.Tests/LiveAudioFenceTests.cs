using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Tests;

public sealed class LiveAudioFenceTests
{
    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => start + Elapsed;
    }

    [Fact]
    public async Task Delayed_refresh_cannot_restore_a_locally_revoked_fence()
    {
        var clock = new Clock();
        var changes = new AudioFenceChanges();
        var runtime = new InMemoryRuntimeStore(changes);
        var sessions = new InMemorySessionStore(changes);
        var lease = await SeedAsync(runtime, sessions, clock);
        var reader = new DelayedLeaseReader(runtime);
        var ownership = new AudioOwnership(reader, sessions, new DeviceService(reader, clock), clock, new MemoryCoreTransaction(runtime, sessions, new()));
        using var fence = new LiveAudioFence(lease, ownership, changes, clock);
        Assert.True(await fence.RefreshAsync(default));

        reader.Delay = true;
        var pending = fence.RefreshAsync(default);
        await reader.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.UpdateAsync(state => { AudioFencing.Revoke(state); return true; });
        Assert.False(fence.IsCurrent());
        reader.Release.TrySetResult();
        Assert.False(await pending);
        Assert.False(fence.IsCurrent());
    }

    [Fact]
    public async Task Remote_revocation_cannot_outlive_the_snapshot_and_local_session_loss_invalidates_immediately()
    {
        var clock = new Clock();
        var localChanges = new AudioFenceChanges();
        var runtime = new InMemoryRuntimeStore();
        var sessions = new InMemorySessionStore(localChanges);
        var lease = await SeedAsync(runtime, sessions, clock);
        var ownership = new AudioOwnership(runtime, sessions, new DeviceService(runtime, clock), clock, new MemoryCoreTransaction(runtime, sessions, new()));
        using var fence = new LiveAudioFence(lease, ownership, localChanges, clock);
        Assert.False(fence.IsCurrent());
        Assert.True(await fence.RefreshAsync(default));

        await runtime.UpdateAsync(state => { AudioFencing.Revoke(state); return true; });
        clock.Elapsed = TimeSpan.FromSeconds(1);
        Assert.False(fence.IsCurrent());
        Assert.False(await fence.RefreshAsync(default));

        var replacement = lease with { Fence = lease.Fence + 1 };
        await runtime.UpdateAsync(state => { state.AudioLease = replacement; return true; });
        using var nextFence = new LiveAudioFence(replacement, ownership, localChanges, clock);
        Assert.True(await nextFence.RefreshAsync(default));
        localChanges.LeaseChanged(lease, null);
        Assert.True(nextFence.IsCurrent());
        await sessions.UpdateAsync(lease.SessionId, s => s with { ActiveDeviceId = null });
        Assert.False(nextFence.IsCurrent());
    }

    [Fact]
    public async Task Snapshot_honors_the_authoritative_expiry_even_when_it_is_fresh()
    {
        var clock = new Clock();
        var changes = new AudioFenceChanges();
        var runtime = new InMemoryRuntimeStore(changes);
        var sessions = new InMemorySessionStore(changes);
        var lease = await SeedAsync(runtime, sessions, clock);
        var ownership = new AudioOwnership(runtime, sessions, new DeviceService(runtime, clock), clock, new MemoryCoreTransaction(runtime, sessions, new()));
        using var fence = new LiveAudioFence(lease, ownership, changes, clock);
        clock.Elapsed = TimeSpan.FromMilliseconds(14_750);
        Assert.True(await fence.RefreshAsync(default));
        clock.Elapsed = TimeSpan.FromSeconds(15);
        Assert.False(fence.IsCurrent());
    }

    private static async Task<AudioLease> SeedAsync(IRuntimeStore runtime, ISessionStore sessions, TimeProvider clock)
    {
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var lease = new AudioLease(id, "fence-device", 1, now.AddSeconds(15));
        await sessions.AddAsync(new(id, ActivityType.General, "Fence fixture", SessionStatus.Active, lease.DeviceId, null, now, now, []));
        await runtime.UpdateAsync(state => { state.AudioLease = lease; return true; });
        return lease;
    }

    private sealed class DelayedLeaseReader(IRuntimeStore inner) : IRuntimeStore
    {
        public bool Delay { get; set; }
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AudioLease?> ReadAudioLeaseAsync(CancellationToken ct = default)
        {
            var lease = await inner.ReadAudioLeaseAsync(ct);
            if (Delay)
            {
                Captured.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return lease;
        }

        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => inner.ReadAsync(read, ct);
        public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default) => inner.UpdateAsync(update, ct);
    }
}
