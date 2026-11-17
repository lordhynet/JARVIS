using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

internal sealed class RuntimeAudioRow
{
    public int Id { get; set; }
    public Guid? LeaseSessionId { get; set; }
    public string? LeaseDeviceId { get; set; }
    public long? LeaseFence { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public Guid? DrainSessionId { get; set; }
    public string? DrainDeviceId { get; set; }
    public long? DrainFence { get; set; }
    public DateTimeOffset? DrainExpiresAt { get; set; }
    public DateTimeOffset? DrainUntil { get; set; }
    public long NextFence { get; set; } = 1;

    public RuntimeAudioLeaseState ToState() => new(
        ReadLease(LeaseSessionId, LeaseDeviceId, LeaseFence, LeaseExpiresAt),
        ReadLease(DrainSessionId, DrainDeviceId, DrainFence, DrainExpiresAt),
        DrainUntil, NextFence);

    public void Apply(RuntimeAudioLeaseState state)
    {
        LeaseSessionId = state.Lease?.SessionId;
        LeaseDeviceId = state.Lease?.DeviceId;
        LeaseFence = state.Lease?.Fence;
        LeaseExpiresAt = state.Lease?.ExpiresAt;
        DrainSessionId = state.DrainingLease?.SessionId;
        DrainDeviceId = state.DrainingLease?.DeviceId;
        DrainFence = state.DrainingLease?.Fence;
        DrainExpiresAt = state.DrainingLease?.ExpiresAt;
        DrainUntil = state.DrainUntil;
        NextFence = state.NextFence;
    }

    public static async Task<RuntimeAudioRow> LockAsync(SessionDbContext db, CancellationToken ct) =>
        (await db.RuntimeAudioStates.FromSqlRaw(
            """SELECT * FROM "RuntimeAudioStates" WHERE "Id" = 1 FOR UPDATE""").ToListAsync(ct)).Single();

    private static AudioLease? ReadLease(Guid? session, string? device, long? fence, DateTimeOffset? expires)
    {
        if (session is null && device is null && fence is null && expires is null) return null;
        if (session is not { } sessionId || device is null || fence is not { } leaseFence || expires is not { } expiresAt)
            throw new InvalidOperationException("Persisted audio lease is incomplete.");
        return new(sessionId, device, leaseFence, expiresAt);
    }
}
