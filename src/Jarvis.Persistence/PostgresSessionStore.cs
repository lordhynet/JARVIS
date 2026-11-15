using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Jarvis.Persistence;

// Each operation owns a context, so the store is safe to share across requests.
public sealed class PostgresSessionStore(IDbContextFactory<SessionDbContext> factory) : ISessionStore
{
    public async Task<SessionAudioState?> ReadAudioStateAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var state = await db.Sessions.AsNoTracking().Where(s => s.Id == id).Select(s => new SessionAudioState(s.Status, s.ActiveDeviceId)).SingleOrDefaultAsync(ct);
        await tx.CommitAsync(ct); return state;
    }
    public async Task<ConversationSession> AddAsync(ConversationSession session, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = SessionRow.FromContract(session);
        db.Sessions.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row.ToContract();
    }

    public async Task<ConversationSession?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.Sessions.AsNoTracking().Include(s => s.Checkpoints)
            .SingleOrDefaultAsync(s => s.Id == id, ct);
        var result = row?.ToContract(); await tx.CommitAsync(ct); return result;
    }

    public async Task<IReadOnlyList<ConversationSession>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var rows = await db.Sessions.AsNoTracking().Include(s => s.Checkpoints)
            .OrderByDescending(s => s.UpdatedAt).ThenBy(s => s.Id).ToListAsync(ct);
        var result = rows.Select(s => s.ToContract()).ToList(); await tx.CommitAsync(ct); return result;
    }

    public async Task<ConversationSession?> AppendCheckpointAsync(Guid sessionId,
        Func<ConversationSession, SessionCheckpoint> createCheckpoint, DateTimeOffset now,
        CancellationToken ct = default)
        => await UpdateAsync(sessionId, current =>
        {
            var checkpoint = createCheckpoint(current);
            return current with
            {
                Checkpoints = [.. current.Checkpoints, checkpoint],
                CurrentTask = checkpoint.CurrentTask ?? current.CurrentTask,
                UpdatedAt = now > current.UpdatedAt ? now : current.UpdatedAt
            };
        }, ct);

    public async Task<ConversationSession?> UpdateAsync(Guid sessionId,
        Func<ConversationSession, ConversationSession> update, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        // Lock the parent before reading checkpoints. PostgreSQL serializes writers even
        // across API processes; a process-local lock would lose updates after scaling out.
        var rows = await db.Sessions.FromSqlInterpolated(
            $"SELECT * FROM \"Sessions\" WHERE \"Id\" = {sessionId} FOR UPDATE").ToListAsync(ct);
        var row = rows.SingleOrDefault();
        if (row is null) return null;

        await db.Entry(row).Collection(s => s.Checkpoints).LoadAsync(ct);
        var updated = update(row.ToContract());
        foreach (var checkpoint in updated.Checkpoints.Where(c => row.Checkpoints.All(old => old.Id != c.Id)))
        {
            var added = CheckpointRow.FromContract(checkpoint);
            row.Checkpoints.Add(added);
            db.Checkpoints.Add(added);
        }
        row.CurrentTask = updated.CurrentTask;
        row.Status = updated.Status;
        row.ActiveDeviceId = updated.ActiveDeviceId;
        row.HandoffJson = updated.Handoff is null ? null : JsonSerializer.Serialize(updated.Handoff);
        row.MessagesJson = JsonSerializer.Serialize(updated.Messages ?? []);
        row.UpdatedAt = SessionRow.DatabaseTimestamp(updated.UpdatedAt);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return row.ToContract();
    }
}
