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
        var question = change.Result.QuestionId;
        var baselineRow = await db.RuntimeLearningRows.AsNoTracking().SingleOrDefaultAsync(
            r => r.Kind == nameof(RuntimeState.TrainingBaselines) && r.Id == question, ct);
        var baseline = TrainingCorrection.RequireBaseline(question, baselineRow is null ? null : LearningCard(baselineRow));
        var membership = await db.RuntimeLearningRows.AsNoTracking().SingleOrDefaultAsync(
            r => r.Kind == nameof(RuntimeState.TrainingBaselineReviews) && r.Id == question, ct);
        var included = membership is null ? [] : JsonSerializer.Deserialize<HashSet<Guid>>(membership.Json)
            ?? throw new InvalidDataException("Null training baseline review membership.");
        var includedIds = included.ToArray();
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.learning.baseline");
        var questionRows = await db.RuntimeTrainingRows.FromSqlInterpolated($"""
            SELECT "Kind","Id","Json" FROM "RuntimeTrainingRows"
            WHERE "Kind"='TrainingAttempts' AND ("Json"->>'QuestionId')::uuid={question}
            """).AsNoTracking().ToArrayAsync(ct);
        var attempts = questionRows.Select(RuntimeTrainingSnapshot.DecodeAttempt).ToArray();
        if (attempts.Any(a => a.QuestionId != question)) throw new InvalidDataException("Scoped training question identity differs.");
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.training.inputs");
        // Only canonical Event + request identity + sequence cross the projection boundary, never replay Result bodies.
        var reviewRows = await db.Database.SqlQuery<CorrectionReviewRow>($"""
            SELECT r."Id",r."Json"->>'RequestId' AS "RequestId",(r."Json"->'Event')::text AS "EventJson",s."Json"::text AS "SequenceJson"
            FROM "RuntimeLearningRows" r LEFT JOIN "RuntimeLearningRows" s ON s."Kind"='TrainingManualSequences' AND s."Id"=r."Id"
            WHERE r."Kind"='Reviews' AND lower(r."Json"->'Event'->>'LearningItemId')={question.ToString("D")}
              AND NOT(r."Id"=ANY({includedIds}))
            """).ToArrayAsync(ct);
        var reviews = reviewRows.Select(row => {
            if (!Guid.TryParse(row.RequestId, out var requestId) || row.Id != requestId)
                throw new InvalidDataException("Scoped training review identity differs.");
            var e = JsonSerializer.Deserialize<ReviewEvent>(row.EventJson) ?? throw new InvalidDataException("Null training review event.");
            if (e.LearningItemId != question) throw new InvalidDataException("Scoped training review question differs.");
            var sequence = row.SequenceJson is null ? 0 : JsonSerializer.Deserialize<long>(row.SequenceJson);
            return new TrainingReviewInput(requestId, e, sequence);
        }).ToArray();
        var cardRow = await db.RuntimeLearningRows.SingleOrDefaultAsync(r => r.Kind == nameof(RuntimeState.Learning) && r.Id == question, ct);
        var oldCard = cardRow is null ? null : LearningCard(cardRow);
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.learning.history");
        var card = TrainingProjection.ProjectEvents(question, baseline, included, reviews,
            attempts.Where(a => a.Id != id).Append(change.Result));
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.project");

        attemptRow!.Json = JsonSerializer.Serialize(change.Result);
        db.RuntimeTrainingRows.Add(new() { Kind = nameof(RuntimeState.TrainingOperations), Id = request.RequestId, Json = JsonSerializer.Serialize(change.Operation) });
        if (cardRow is null) db.RuntimeLearningRows.Add(new() { Kind = nameof(RuntimeState.Learning), Id = question,
            Json = JsonSerializer.Serialize(card), DueAt = card.DueAt.ToUniversalTime() });
        else if (JsonSerializer.Serialize(oldCard) != JsonSerializer.Serialize(card)) {
            cardRow.Json = JsonSerializer.Serialize(card); cardRow.DueAt = card.DueAt.ToUniversalTime();
        }
        await db.SaveChangesAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "correction.save");
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "correction.commit"); CoreTelemetry.RuntimeWrites.Add(1);
        return change.Result;
    }
    private sealed class CorrectionReviewRow
    {
        public Guid Id { get; set; }
        public string? RequestId { get; set; }
        public string EventJson { get; set; } = "";
        public string? SequenceJson { get; set; }
    }
}
