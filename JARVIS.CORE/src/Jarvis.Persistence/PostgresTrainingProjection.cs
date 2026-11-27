using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed partial class PostgresRuntimeStore
{
    // Materialized inputs for one question, never a complete-unit snapshot/diff.
    private sealed record TrainingQuestionProjection(Guid Question, LearningItem Baseline, HashSet<Guid> Included,
        TrainingAttempt[] Attempts, TrainingReviewInput[] Reviews, RuntimeLearningRow? CardRow, LearningItem? OldCard)
    {
        public LearningItem Project(Guid id, TrainingAttempt replacement)
            => TrainingProjection.ProjectEvents(Question, Baseline, Included, Reviews, Attempts.Where(a => a.Id != id).Append(replacement));
        public void WriteCard(SessionDbContext db, LearningItem card)
        {
            if (CardRow is null) db.RuntimeLearningRows.Add(new() { Kind = nameof(RuntimeState.Learning), Id = Question,
                Json = JsonSerializer.Serialize(card), DueAt = card.DueAt.ToUniversalTime() });
            else if (JsonSerializer.Serialize(OldCard) != JsonSerializer.Serialize(card)) {
                CardRow.Json = JsonSerializer.Serialize(card); CardRow.DueAt = card.DueAt.ToUniversalTime();
            }
        }
    }

    private static async Task<TrainingQuestionProjection?> ReadTrainingProjectionAsync(SessionDbContext db, Guid question, bool correction, CancellationToken ct)
    {
        var phase = Stopwatch.GetTimestamp();
        var baselineRow = await db.RuntimeLearningRows.AsNoTracking().SingleOrDefaultAsync(
            r => r.Kind == nameof(RuntimeState.TrainingBaselines) && r.Id == question, ct);
        var baseline = baselineRow is null ? null : LearningCard(baselineRow);
        if (correction) baseline = TrainingCorrection.RequireBaseline(question, baseline);
        // Deletion may still retire its source after privacy has removed projection authority.
        if (baseline is null) {
            CoreTelemetry.RecordStoragePhase(phase, "deletion.learning.baseline"); return null;
        }
        var membership = await db.RuntimeLearningRows.AsNoTracking().SingleOrDefaultAsync(
            r => r.Kind == nameof(RuntimeState.TrainingBaselineReviews) && r.Id == question, ct);
        var included = membership is null ? [] : JsonSerializer.Deserialize<HashSet<Guid>>(membership.Json)
            ?? throw new InvalidDataException("Null training baseline review membership.");
        var includedIds = included.ToArray();
        phase = CoreTelemetry.RecordStoragePhase(phase, correction ? "correction.learning.baseline" : "deletion.learning.baseline");
        var questionRows = await db.RuntimeTrainingRows.FromSqlInterpolated($"""
            SELECT "Kind","Id","Json" FROM "RuntimeTrainingRows"
            WHERE "Kind"='TrainingAttempts' AND ("Json"->>'QuestionId')::uuid={question}
            """).AsNoTracking().ToArrayAsync(ct);
        var attempts = questionRows.Select(RuntimeTrainingSnapshot.DecodeAttempt).ToArray();
        if (attempts.Any(a => a.QuestionId != question)) throw new InvalidDataException("Scoped training question identity differs.");
        phase = CoreTelemetry.RecordStoragePhase(phase, correction ? "correction.training.inputs" : "deletion.training.inputs");
        // Only canonical Event + request identity + sequence, never replay Result bodies.
        var reviewRows = await db.Database.SqlQuery<TrainingProjectionReviewRow>($"""
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
        CoreTelemetry.RecordStoragePhase(phase, correction ? "correction.learning.history" : "deletion.learning.history");
        return new(question, baseline, included, attempts, reviews, cardRow, oldCard);
    }

    private sealed class TrainingProjectionReviewRow
    {
        public Guid Id { get; set; }
        public string? RequestId { get; set; }
        public string EventJson { get; set; } = "";
        public string? SequenceJson { get; set; }
    }
}
