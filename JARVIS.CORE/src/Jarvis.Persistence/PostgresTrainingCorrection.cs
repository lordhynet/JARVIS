using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed partial class PostgresRuntimeStore
{
    public async Task<TrainingAttempt> CorrectTrainingAnswerAsync(Guid id, CorrectTrainingAnswerRequest request, TimeProvider clock, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp(); var normalized = TrainingCorrection.Normalize(request);
        await using var db = await factory.CreateDbContextAsync(ct);
        var phase = CoreTelemetry.RecordStoragePhase(started, "correction.context");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.begin");
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.privacy_gate");
        _ = (await db.Database.SqlQueryRaw<RuntimeDocumentStamp>(
            "SELECT \"Id\",\"Fingerprint\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1 FOR UPDATE").ToArrayAsync(ct)).Single();
        CoreTelemetry.RuntimeLockWait.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.lock");

        var receiptRow = await db.RuntimeTrainingRows.AsNoTracking().SingleOrDefaultAsync(
            r => r.Kind == nameof(RuntimeState.TrainingOperations) && r.Id == request.RequestId, ct);
        var receipt = receiptRow is null ? null : RuntimeTrainingSnapshot.DecodeOperation(receiptRow);
        TrainingCorrection.IsReplay(id, normalized, receipt); // refuse purge/conflict before loading any target content
        var attemptRow = await db.RuntimeTrainingRows.SingleOrDefaultAsync(
            r => r.Kind == nameof(RuntimeState.TrainingAttempts) && r.Id == id, ct);
        var attempt = attemptRow is null ? null : RuntimeTrainingSnapshot.DecodeAttempt(attemptRow);
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.target");
        var change = TrainingCorrection.Decide(id, normalized, receipt, attempt, clock.GetUtcNow());
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.decision");
        if (change.Operation is null) {
            await tx.CommitAsync(ct); CoreTelemetry.RecordStoragePhase(phase, "correction.commit");
            CoreTelemetry.RuntimeNoOpWrites.Add(1); return change.Result;
        }
        var projection = (await ReadTrainingProjectionAsync(db, change.Result.QuestionId, correction: true, ct))!;
        phase = Stopwatch.GetTimestamp();
        var card = projection.Project(id, change.Result);
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.project");

        attemptRow!.Json = JsonSerializer.Serialize(change.Result);
        db.RuntimeTrainingRows.Add(new() { Kind = nameof(RuntimeState.TrainingOperations), Id = request.RequestId, Json = JsonSerializer.Serialize(change.Operation) });
        projection.WriteCard(db, card);
        await db.SaveChangesAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.save");
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "correction.commit"); CoreTelemetry.RuntimeWrites.Add(1);
        return change.Result;
    }
}
