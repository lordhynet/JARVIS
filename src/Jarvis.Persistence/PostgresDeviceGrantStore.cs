using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class PostgresDeviceGrantStore(IDbContextFactory<SessionDbContext> factory) : IDeviceGrantStore
{
    public async Task<DeviceGrant> AddAsync(DeviceGrant grant, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = DeviceGrantRow.From(grant); db.DeviceGrants.Add(row); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return row.Contract();
    }
    public async Task<DeviceGrant?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.DeviceGrants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); await tx.CommitAsync(ct);
        return row?.Contract();
    }
    public async Task<IReadOnlyList<DeviceGrant>> ListAsync(string? deviceId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var query = db.DeviceGrants.AsNoTracking().AsQueryable();
        if (deviceId is not null) query = query.Where(x => x.DeviceId == deviceId);
        var rows = await query.OrderBy(x => x.DeviceId).ThenBy(x => x.CapabilityId).ThenByDescending(x => x.GrantedAt).ToListAsync(ct);
        await tx.CommitAsync(ct); return rows.Select(x => x.Contract()).ToArray();
    }
    public async Task<DeviceGrant?> FindActiveAsync(string deviceId, string capabilityId, string capabilityVersion,
        string credentialHash, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.DeviceGrants.AsNoTracking().Where(x => x.DeviceId == deviceId && x.CapabilityId == capabilityId &&
            x.CapabilityVersion == capabilityVersion && x.CredentialHash == credentialHash && x.RevokedAt == null && x.ExpiresAt > now)
            .OrderByDescending(x => x.GrantedAt).FirstOrDefaultAsync(ct);
        await tx.CommitAsync(ct); return row?.Contract();
    }
    public async Task<bool> RevokeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.DeviceGrants.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null || row.RevokedAt is not null) { await tx.CommitAsync(ct); return false; }
        row.RevokedAt = SessionRow.DatabaseTimestamp(now); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }
    public async Task<int> RevokeForDeviceAsync(string deviceId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var rows = await db.DeviceGrants.Where(x => x.DeviceId == deviceId && x.RevokedAt == null).ToListAsync(ct);
        foreach (var row in rows) row.RevokedAt = SessionRow.DatabaseTimestamp(now);
        if (rows.Count > 0) await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return rows.Count;
    }
    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var removed = await db.DeviceGrants.Where(x => x.ExpiresAt <= now || x.RevokedAt != null).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct); return removed;
    }
}
