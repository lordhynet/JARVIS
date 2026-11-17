using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed class AudioFenceChanges
{
    internal event Action<AudioLease>? LeaseRevoked;
    internal event Action<Guid, string>? SessionOwnershipLost;

    public void LeaseChanged(AudioLease? previous, AudioLease? current)
    {
        if (previous is not null && !Matches(previous, current)) LeaseRevoked?.Invoke(previous);
    }

    public void SessionChanged(ConversationSession previous, ConversationSession current)
    {
        if (previous is { Status: SessionStatus.Active, ActiveDeviceId: { } deviceId } &&
            (current.Status != SessionStatus.Active || current.ActiveDeviceId != deviceId))
            SessionOwnershipLost?.Invoke(previous.Id, deviceId);
    }

    internal static bool Matches(AudioLease expected, AudioLease? actual) =>
        actual?.Fence == expected.Fence && actual.SessionId == expected.SessionId && actual.DeviceId == expected.DeviceId;
}

/// <summary>A bounded, in-process copy of the authoritative lease for one realtime connection.</summary>
public sealed class LiveAudioFence : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(1);

    private readonly AudioLease lease;
    private readonly AudioOwnership ownership;
    private readonly AudioFenceChanges changes;
    private readonly TimeProvider clock;
    private Snapshot? snapshot;
    private int revoked;

    private sealed record Snapshot(long CheckedAt, DateTimeOffset ExpiresAt);

    public LiveAudioFence(AudioLease lease, AudioOwnership ownership, AudioFenceChanges changes, TimeProvider clock)
    {
        this.lease = lease;
        this.ownership = ownership;
        this.changes = changes;
        this.clock = clock;
        changes.LeaseRevoked += OnLeaseRevoked;
        changes.SessionOwnershipLost += OnSessionOwnershipLost;
    }

    public bool IsCurrent()
    {
        var current = Volatile.Read(ref snapshot);
        return current is not null && clock.GetElapsedTime(current.CheckedAt) < MaximumAge &&
            clock.GetUtcNow() < current.ExpiresAt && Volatile.Read(ref revoked) == 0;
    }

    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref revoked) != 0) return false;
        // A delayed read cannot extend the time during which the stream trusts an old fence.
        var checkedAt = clock.GetTimestamp();
        var current = await ownership.ReadCurrentAsync(lease, ct);
        Volatile.Write(ref snapshot, current is null ? null : new Snapshot(checkedAt, current.ExpiresAt));
        return IsCurrent();
    }

    private void OnLeaseRevoked(AudioLease previous)
    {
        if (AudioFenceChanges.Matches(lease, previous)) Volatile.Write(ref revoked, 1);
    }

    private void OnSessionOwnershipLost(Guid sessionId, string deviceId)
    {
        if (sessionId == lease.SessionId && deviceId == lease.DeviceId) Volatile.Write(ref revoked, 1);
    }

    public void Dispose()
    {
        Volatile.Write(ref revoked, 1);
        changes.LeaseRevoked -= OnLeaseRevoked;
        changes.SessionOwnershipLost -= OnSessionOwnershipLost;
    }
}
