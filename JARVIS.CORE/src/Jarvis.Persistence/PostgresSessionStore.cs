using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;

namespace Jarvis.Persistence;

// Each operation owns a context, so the store is safe to share across requests.
public sealed class PostgresSessionStore(IDbContextFactory<SessionDbContext> factory, AudioFenceChanges? audioChanges = null) : ISessionStore
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
        var phase = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "session.preparation.begin_and_gate");
        // Count entries from the stored JSON text without constructing a binary JSON document.
        List<PreparationRow> rows;
        if (deviceId is null)
            rows = await db.Database.SqlQueryRaw<PreparationRow>("""
            SELECT s."Id", s."Status", s."ActiveDeviceId", s."Title", s."UpdatedAt", s."HandoffJson",
                json_array_length(s."MessagesJson"::json) AS "MessageCount"
            FROM "Sessions" s
            """).ToListAsync(ct);
        else
            rows = await db.Database.SqlQuery<PreparationRow>($"""
                SELECT s."Id", s."Status", s."ActiveDeviceId", s."Title", s."UpdatedAt", s."HandoffJson",
                    json_array_length(s."MessagesJson"::json) AS "MessageCount"
                FROM "Sessions" s
                WHERE s."Id" = {preferredCheckpointId}
                   OR EXISTS (SELECT 1 FROM "Checkpoints" c WHERE c."SessionId" = s."Id" AND c."Id" = {preferredCheckpointId})
                   OR s."ActiveDeviceId" = {deviceId}
                   OR (s."Status" = {(int)SessionStatus.Paused} AND s."ActiveDeviceId" IS NULL AND s."Title" = 'JARVIS voice')
                   OR (s."Status" = {(int)SessionStatus.Active} AND s."HandoffJson" IS NOT NULL
                       AND s."HandoffJson"::jsonb ->> 'ToDeviceId' = {deviceId})
                """).ToListAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "session.preparation.candidates");
        var sessionIds = rows.Select(s => s.Id).ToArray();
        var checkpoints = await db.Checkpoints.AsNoTracking().Where(c => sessionIds.Contains(c.SessionId))
            .Select(c => new { c.Id, c.SessionId }).ToListAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "session.preparation.checkpoints");
        await tx.CommitAsync(ct);
        var ids = checkpoints.ToLookup(c => c.SessionId, c => c.Id);
        var candidates = rows.Select(s => new SessionPreparationState(s.Id, s.Status, s.ActiveDeviceId, s.Title, s.UpdatedAt,
            s.HandoffJson is null ? null : JsonSerializer.Deserialize<SessionHandoff>(s.HandoffJson), ids[s.Id].ToArray(), s.MessageCount)).ToArray();
        CoreTelemetry.RecordStoragePhase(phase, "session.preparation.commit_and_project");
        return candidates;
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
        var phase = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "session.audio.context");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "session.audio.begin");
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "session.audio.privacy_gate");
        var state = await ReadAudioStateAsync(db, id, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "session.audio.load");
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "session.audio.commit");
        return state;
    }
    internal static Task<SessionAudioState?> ReadAudioStateAsync(SessionDbContext db, Guid id, CancellationToken ct, bool lockSession = false)
    {
        var query = lockSession
            ? db.Sessions.FromSqlInterpolated($"SELECT * FROM \"Sessions\" WHERE \"Id\" = {id} FOR SHARE")
            : db.Sessions.Where(s => s.Id == id);
        return query.AsNoTracking().Select(s => new SessionAudioState(s.Status, s.ActiveDeviceId)).SingleOrDefaultAsync(ct);
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
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        // Split query: a joined include repeats the whole MessagesJson once per checkpoint, and voice preparation
        // appends a checkpoint every time, so long-lived sessions grew slower with each conversation.
        var row = await db.Sessions.AsNoTracking().Include(s => s.Checkpoints).AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == id, ct);
        var result = row?.ToContract(); await tx.CommitAsync(ct); return result;
    }

    public async Task<IReadOnlyList<ConversationSession>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var rows = await db.Sessions.AsNoTracking().Include(s => s.Checkpoints).AsSplitQuery()
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
        var changed = await UpdateInTransactionAsync(db, sessionId, update, ct);
        await transaction.CommitAsync(ct);
        if (changed is { } completed) audioChanges?.SessionChanged(completed.Before, completed.After);
        return changed?.After;
    }

    internal static async Task<(ConversationSession Before, ConversationSession After)?> UpdateInTransactionAsync(
        SessionDbContext db, Guid sessionId, Func<ConversationSession, ConversationSession> update, CancellationToken ct)
    {
        // Lock the parent before reading checkpoints. PostgreSQL serializes writers even
        // across API processes; a process-local lock would lose updates after scaling out.
        var rows = await db.Sessions.FromSqlInterpolated(
            $"SELECT * FROM \"Sessions\" WHERE \"Id\" = {sessionId} FOR UPDATE").ToListAsync(ct);
        var row = rows.SingleOrDefault();
        if (row is null) return null;

        await db.Entry(row).Collection(s => s.Checkpoints).LoadAsync(ct);
        var current = row.ToContract();
        var updated = update(current);
        foreach (var checkpoint in row.Checkpoints.Where(c => updated.Checkpoints.All(keep => keep.Id != c.Id)).ToArray()) {
            row.Checkpoints.Remove(checkpoint); db.Checkpoints.Remove(checkpoint);
        }
        foreach (var checkpoint in updated.Checkpoints.Where(c => row.Checkpoints.All(old => old.Id != c.Id)))
        {
            var added = CheckpointRow.FromContract(checkpoint);
            row.Checkpoints.Add(added);
            db.Checkpoints.Add(added);
        }
        row.Title = updated.Title;
        row.CurrentTask = updated.CurrentTask;
        row.Status = updated.Status;
        row.ActiveDeviceId = updated.ActiveDeviceId;
        row.HandoffJson = updated.Handoff is null ? null : JsonSerializer.Serialize(updated.Handoff);
        row.MessagesJson = JsonSerializer.Serialize(updated.Messages ?? []);
        row.PendingVoiceProjectionIdsJson = JsonSerializer.Serialize(updated.PendingVoiceProjectionIds ?? []);
        row.VoiceSummaryJson = updated.VoiceSummary is null ? null : JsonSerializer.Serialize(updated.VoiceSummary);
        row.UpdatedAt = SessionRow.DatabaseTimestamp(updated.UpdatedAt);
        await db.SaveChangesAsync(ct);
        return (current, row.ToContract());
    }
}
