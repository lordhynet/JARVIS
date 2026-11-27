using System.Diagnostics;
using System.Text.Json;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed partial class PostgresRuntimeStore
{
    public async Task<bool> DeleteTrainingAttemptAsync(Guid id, int expectedRevision, TimeProvider clock, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        var phase = CoreTelemetry.RecordStoragePhase(started, "deletion.context");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.begin");
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.privacy_gate");
        _ = (await db.Database.SqlQueryRaw<RuntimeDocumentStamp>(
            "SELECT \"Id\",\"Fingerprint\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1 FOR UPDATE").ToArrayAsync(ct)).Single();
        CoreTelemetry.RuntimeLockWait.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.lock");
        var row = await db.RuntimeTrainingRows.SingleOrDefaultAsync(r => r.Kind == nameof(RuntimeState.TrainingAttempts) && r.Id == id, ct);
        var current = row is null ? null : RuntimeTrainingSnapshot.DecodeAttempt(row);
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.target");
        var change = TrainingDeletion.Decide(id, expectedRevision, current, clock.GetUtcNow());
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.decision");
        if (change is null) {
            await tx.CommitAsync(ct); CoreTelemetry.RecordStoragePhase(phase, "deletion.commit");
            CoreTelemetry.RuntimeNoOpWrites.Add(1); return false;
        }
        var projection = await ReadTrainingProjectionAsync(db, change.QuestionId, correction: false, ct);
        phase = Stopwatch.GetTimestamp();
        var card = projection?.Project(id, change);
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.project");

        // Reasoning remains canonical in cold JSON. Reuse its existing lifecycle and codec;
        // never omit the purge to make deletion look cold-free or use a partial whole-unit diff.
        var root = await db.RuntimeDocuments.SingleAsync(r => r.Id == 1, ct);
        CoreTelemetry.RuntimeBytes.Record(System.Text.Encoding.UTF8.GetByteCount(root.Json));
        var cold = JsonSerializer.Deserialize<RuntimeState>(root.Json) ?? throw new InvalidDataException("Null cold reasoning state.");
        var oldHash = root.Fingerprint.Length == 0 ? PostgresRuntimeCodec.Fingerprint(PostgresRuntimeCodec.Serialize(cold)) : root.Fingerprint;
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.reasoning.hydrate");
        ReasoningLifecycle.Retire(cold, id);
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.reasoning.retire");
        var json = PostgresRuntimeCodec.Serialize(cold); var hash = PostgresRuntimeCodec.Fingerprint(json);
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.cold.serialize_hash");

        row!.Json = JsonSerializer.Serialize(change);
        if (card is not null) projection!.WriteCard(db, card);
        if (hash != oldHash) { root.Json = json; root.Fingerprint = hash; }
        await db.SaveChangesAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "deletion.save");
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "deletion.commit"); CoreTelemetry.RuntimeWrites.Add(1);
        return true;
    }
}
