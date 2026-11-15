using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class PostgresVoiceRepairStore(IDbContextFactory<SessionDbContext> factory) : IVoiceRepairStore
{
    public async Task<int> RepairTranscriptsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        // Pending marker projection only: idle repair does not read MessagesJson or RuntimeDocuments.
        var rows = await db.Database.SqlQueryRaw<PendingRow>("""
            SELECT s."Id" AS "SessionId", s."PendingVoiceProjectionIdsJson"::text AS "RequestIdsJson"
            FROM "Sessions" s WHERE s."PendingVoiceProjectionIdsJson" <> '[]'::jsonb
            ORDER BY s."UpdatedAt", s."Id" LIMIT 100
            """).ToListAsync(ct);
        await tx.CommitAsync(ct);
        if (rows.Count == 0) return 0;

        var sessions = new PostgresSessionStore(factory);
        var turns = new List<VoiceTurn>();
        foreach (var pending in rows) {
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(pending.RequestIdsJson) ?? [];
            var session = await sessions.GetAsync(pending.SessionId, ct);
            if (session is null) continue;
            turns.AddRange((session.Messages ?? []).Where(m => ids.Contains(m.RequestId) && m.IsVoice)
                .Select(m => VoiceTranscriptRules.Turn(session.Id, m)));
        }
        var runtime = new PostgresRuntimeStore(factory);
        var count = turns.Count == 0 ? 0 : await runtime.UpdateAsync(state => {
            var written = 0;
            foreach (var turn in turns.Where(t => !state.RetiredSessions.Contains(t.SessionId) && !state.VoiceTurns.ContainsKey(t.Id))) {
                VoiceTranscriptRules.Publish(state, turn); written++;
            }
            return written;
        }, ct);
        foreach (var pending in rows) {
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(pending.RequestIdsJson) ?? [];
            await sessions.UpdateAsync(pending.SessionId, current => current with {
                PendingVoiceProjectionIds = (current.PendingVoiceProjectionIds ?? []).Where(id => !ids.Contains(id)).ToArray()
            }, ct);
        }
        return count;
    }
    private sealed class PendingRow { public Guid SessionId { get; set; } public string RequestIdsJson { get; set; } = "[]"; }
}
