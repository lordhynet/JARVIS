using System.Diagnostics;
using System.Text.Json;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed partial class PostgresRuntimeStore
{
    // One transaction: privacy gate, root FOR UPDATE lock, generated rows loaded for the scope and quota counts read under that
    // lock, the runtime callback, then runtime and row changes saved by the same SaveChanges. No second context commits.
    public async Task<T> UpdateWithGeneratedQuestionsAsync<T>(GeneratedQuestionScope scope, Func<RuntimeState, GeneratedQuestionUnit, T> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(update);
        var started = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        var phase = CoreTelemetry.RecordStoragePhase(started, "generated.update.context");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "generated.update.privacy_gate");
        var ids = scope.QuestionIds.ToArray(); var operations = scope.OperationIds.ToArray();
        var outcome = await UpdateInTransactionAsync(db, async state =>
        {
            // Rows are read only after the root lock is held (inside UpdateInTransactionAsync), so writers and purges serialize.
            var rows = await db.GeneratedPracticeQuestions.Where(r => ids.Contains(r.Id) || operations.Contains(r.OperationId)).ToArrayAsync(ct);
            var created = await db.GeneratedPracticeQuestions.AsNoTracking().Where(r => r.SessionId == scope.SessionId).Select(r => r.CreatedAt).ToArrayAsync(ct);
            var total = await db.GeneratedPracticeQuestions.CountAsync(ct);
            var unit = new GeneratedQuestionUnit(scope, rows.Select(r => r.Record()), created, total);
            var result = update(state, unit);
            foreach (var (id, write) in unit.Adds)
                db.GeneratedPracticeQuestions.Add(new()
                {
                    Id = id, SessionId = write.SessionId, OperationId = write.OperationId, Topic = (int)write.Topic, OutlineKey = write.OutlineKey,
                    OutlineVersion = write.OutlineVersion, Version = 1, Fingerprint = write.Fingerprint,
                    Json = JsonSerializer.Serialize(new { write.Prompt, write.ReferenceOutline, FollowUps = write.FollowUps.ToArray() }),
                    Provenance = write.ProvenanceJson, Lineage = write.LineageJson, Verified = false, CreatedAt = write.CreatedAt
                });
            var tracked = db.ChangeTracker.Entries<GeneratedPracticeQuestionRow>().Select(e => e.Entity).ToDictionary(r => r.Id);
            foreach (var (id, at) in unit.Purges)
                if (tracked.TryGetValue(id, out var row) && row.PurgedAt is null)
                {
                    // Content-free tombstone: text, content-derived fingerprint and lineage are removed; the ID remains.
                    row.Json = null; row.Fingerprint = ""; row.Lineage = "[]"; row.PurgedAt = at;
                }
            return result;
        }, started, phase, ct);
        await tx.CommitAsync(ct);
        audioChanges?.LeaseChanged(outcome.Before, outcome.After);
        CoreTelemetry.RecordStoragePhase(outcome.SavedAt, "generated.update.commit");
        return outcome.Result;
    }
}
