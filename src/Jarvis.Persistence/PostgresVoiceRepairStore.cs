using System.Text.Json;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class PostgresVoiceRepairStore(IDbContextFactory<SessionDbContext> factory) : IVoiceRepairStore
{
    private sealed class RepairRow
    {
        public Guid SessionId { get; set; }
        public Guid RequestId { get; set; }
        public string Role { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTimeOffset At { get; set; }
        public double? SpeechSeconds { get; set; }
    }
    private static Task<List<RepairRow>> Missing(SessionDbContext db, CancellationToken ct) => db.Database.SqlQueryRaw<RepairRow>("""
        SELECT s."Id" AS "SessionId", (m.value->>'RequestId')::uuid AS "RequestId", m.value->>'Role' AS "Role",
            m.value->>'Text' AS "Text", (m.value->>'At')::timestamptz AS "At", (m.value->>'SpeechSeconds')::double precision AS "SpeechSeconds"
        FROM "Sessions" s CROSS JOIN LATERAL jsonb_array_elements(s."MessagesJson"::jsonb) m(value)
        CROSS JOIN "RuntimeDocuments" r
        WHERE r."Id"=1 AND m.value->>'IsVoice'='true'
            AND NOT (COALESCE(r."Json"->'VoiceTurns',jsonb_build_object()) ? (m.value->>'RequestId'))
            AND NOT (COALESCE(r."Json"->'RetiredSessions','[]'::jsonb) ? s."Id"::text)
        ORDER BY s."Id", (m.value->>'At')::timestamptz LIMIT 100
        """).ToListAsync(ct);
    public async Task<int> RepairTranscriptsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        // Read-only precheck: an idle sweep never locks or serializes the runtime row.
        if ((await Missing(db, ct)).Count == 0) { await tx.CommitAsync(ct); return 0; }
        var row = (await db.RuntimeDocuments.FromSqlRaw("SELECT * FROM \"RuntimeDocuments\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(ct)).Single();
        var state = JsonSerializer.Deserialize<RuntimeState>(row.Json)!; var count = 0;
        foreach (var missing in await Missing(db, ct)) {
            if (state.RetiredSessions.Contains(missing.SessionId) || state.VoiceTurns.ContainsKey(missing.RequestId)) continue;
            VoiceTranscriptRules.Publish(state, new(missing.RequestId, missing.SessionId, missing.Role, missing.Text, missing.SpeechSeconds, missing.At)); count++;
        }
        if (count > 0) { row.Json = JsonSerializer.Serialize(state); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct); return count;
    }
}
