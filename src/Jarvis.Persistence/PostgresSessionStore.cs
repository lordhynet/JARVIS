using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Jarvis.Persistence;

// Each operation owns a context, so the store is safe to share across requests.
public sealed class PostgresSessionStore(IDbContextFactory<SessionDbContext> factory) : ISessionStore
{
    public async Task<SessionHandoff?> ReadHandoffAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var json = await db.Sessions.AsNoTracking().Where(s => s.Id == id).Select(s => s.HandoffJson).SingleOrDefaultAsync(ct);
        await tx.CommitAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<SessionHandoff>(json);
    }
    public async Task<IReadOnlyList<SessionPreparationState>> ListPreparationAsync(CancellationToken ct = default)
        => await ListPreparationCoreAsync(null, null, ct);
    public async Task<IReadOnlyList<SessionPreparationState>> ListPreparationAsync(string deviceId, Guid preferredCheckpointId, CancellationToken ct = default)
        => await ListPreparationCoreAsync(deviceId, preferredCheckpointId, ct);
    private async Task<IReadOnlyList<SessionPreparationState>> ListPreparationCoreAsync(string? deviceId, Guid? preferredCheckpointId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        // Count JSON array entries in PostgreSQL; never transfer transcript contents for selection.
        List<PreparationRow> rows;
        if (deviceId is null)
            rows = await db.Database.SqlQueryRaw<PreparationRow>("""
            SELECT s."Id", s."Status", s."ActiveDeviceId", s."Title", s."UpdatedAt", s."HandoffJson",
                jsonb_array_length(s."MessagesJson"::jsonb) AS "MessageCount"
            FROM "Sessions" s
            """).ToListAsync(ct);
        else
            rows = await db.Database.SqlQuery<PreparationRow>($"""
                SELECT s."Id", s."Status", s."ActiveDeviceId", s."Title", s."UpdatedAt", s."HandoffJson",
                    jsonb_array_length(s."MessagesJson"::jsonb) AS "MessageCount"
                FROM "Sessions" s
                WHERE s."Id" = {preferredCheckpointId}
                   OR EXISTS (SELECT 1 FROM "Checkpoints" c WHERE c."SessionId" = s."Id" AND c."Id" = {preferredCheckpointId})
                   OR s."ActiveDeviceId" = {deviceId}
                   OR (s."Status" = {(int)SessionStatus.Paused} AND s."ActiveDeviceId" IS NULL AND s."Title" = 'JARVIS voice')
                   OR (s."Status" = {(int)SessionStatus.Active} AND s."HandoffJson" IS NOT NULL
                       AND s."HandoffJson"::jsonb ->> 'ToDeviceId' = {deviceId})
                """).ToListAsync(ct);
        var sessionIds = rows.Select(s => s.Id).ToArray();
        var checkpoints = await db.Checkpoints.AsNoTracking().Where(c => sessionIds.Contains(c.SessionId))
            .Select(c => new { c.Id, c.SessionId }).ToListAsync(ct);
        await tx.CommitAsync(ct);
        var ids = checkpoints.ToLookup(c => c.SessionId, c => c.Id);
        return rows.Select(s => new SessionPreparationState(s.Id, s.Status, s.ActiveDeviceId, s.Title, s.UpdatedAt,
            s.HandoffJson is null ? null : JsonSerializer.Deserialize<SessionHandoff>(s.HandoffJson), ids[s.Id].ToArray(), s.MessageCount)).ToArray();
    }
    private sealed class PreparationRow
    {
        public Guid Id { get; set; }
        public SessionStatus Status { get; set; }
        public string? ActiveDeviceId { get; set; }
        public string Title { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; }
        public string? HandoffJson { get; set; }
        public int MessageCount { get; set; }
    }
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
