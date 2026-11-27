using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

// Transient inputs/results for the existing correction transition, never stored as a second receipt or history.
public sealed record NormalizedTrainingCorrection(Guid RequestId, int ExpectedRevision, string Text, string Fingerprint);
public sealed record TrainingCorrectionChange(TrainingAttempt Result, TrainingOperation? Operation);

public static class TrainingCorrection
{
    public static NormalizedTrainingCorrection Normalize(CorrectTrainingAnswerRequest request)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        var text = Guard.Text(request.Text, "Corrected answer", 20000);
        // Preserve the existing anonymous object's names/order and default JSON/UTF8 encoding for old receipts.
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request.ExpectedRevision, text })));
        return new(request.RequestId, request.ExpectedRevision, text, fingerprint);
    }

    public static bool IsReplay(Guid id, NormalizedTrainingCorrection request, TrainingOperation? prior)
    {
        if (prior is null) return false;
        Guard.Require(!string.IsNullOrEmpty(prior.Fingerprint), 409, "This training operation was purged; start a new practice request.", "training_operation_purged");
        Guard.Require(prior.Kind == "correction" && prior.AttemptId == id && prior.Fingerprint == request.Fingerprint,
            409, "RequestId was used for different training input.");
        return true;
    }

    public static TrainingCorrectionChange Decide(Guid id, NormalizedTrainingCorrection request, TrainingOperation? prior,
        TrainingAttempt? current, DateTimeOffset now)
    {
        var replay = IsReplay(id, request, prior);
        if (current is not null && current.Id != id) throw new InvalidDataException("Training attempt identity differs.");
        if (replay) {
            Guard.Require(current is not null, 409, "This training operation target is unavailable.", "training_operation_unavailable");
            Guard.Require(current.DeletedAt is null, 409, "This training attempt was deleted; use a new RequestId for new practice.");
            return new(current, null); // matching replay returns CURRENT state, not an old response snapshot
        }
        Guard.Require(current is not null && current.DeletedAt is null, 404, "Training attempt not found.");
        Guard.Require(request.ExpectedRevision > 0 && current.Revision == request.ExpectedRevision, 409, "Training attempt changed. Reload before editing.");
        Guard.Require(!current.Skipped && current.Answers.Count < 5, 409, "Skipped answers cannot be corrected, and answer history is limited to five versions.");
        var updated = current with { Revision = checked(current.Revision + 1), CanonicalAssessmentId = null,
            Answers = [.. current.Answers, new(current.TextRevision + 1, request.Text, "user_corrected", now)] };
        return new(updated, new("correction", id, request.Fingerprint));
    }

    public static LearningItem RequireBaseline(Guid question, LearningItem? baseline)
    {
        Guard.Require(baseline is not null, 409, "Training projection source was removed.", "training_baseline_missing");
        Guard.Require(baseline.Id == question, 409, "Projection baseline belongs to another question.");
        return baseline;
    }

    public static TrainingAttempt Apply(RuntimeState state, Guid id, NormalizedTrainingCorrection request, TimeProvider clock)
    {
        var prior = state.TrainingOperations.GetValueOrDefault(request.RequestId);
        IsReplay(id, request, prior);
        var change = Decide(id, request, prior, state.TrainingAttempts.GetValueOrDefault(id), clock.GetUtcNow());
        if (change.Operation is null) return change.Result;
        var question = change.Result.QuestionId;
        var baseline = RequireBaseline(question, state.TrainingBaselines.GetValueOrDefault(question));
        var card = TrainingProjection.Project(question, baseline, state.TrainingBaselineReviews.GetValueOrDefault(question) ?? [],
            state.Reviews.Values, state.TrainingManualSequences, state.TrainingAttempts.Values.Where(a => a.Id != id).Append(change.Result));
        state.TrainingAttempts[id] = change.Result; state.TrainingOperations.Add(request.RequestId, change.Operation);
        state.Learning[question] = card;
        return change.Result;
    }
}
