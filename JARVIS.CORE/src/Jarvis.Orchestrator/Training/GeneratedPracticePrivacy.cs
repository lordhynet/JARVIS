using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Training;

// All-copy purge of AI-generated practice content from runtime state, modelled on RecordedPracticePrivacy.Purge.
// Runs inside the caller's canonical transaction; the generated-question rows themselves are purged by the same
// transaction's store port. After purge only content-free IDs, timestamps, topic/outline and tombstone flags remain.
// No caller yet (slice 2b-i): nothing can create generated content, so this cannot be reached in production.
public static class GeneratedPracticePrivacy
{
    public const string TombstoneTitle = "Removed practice question";
    public const string TombstoneText = "This AI-generated practice question was removed.";

    // Returns the question IDs whose content was found and purged in state (the caller purges their rows too).
    private static readonly IReadOnlySet<Guid> AuthoredIds = QuestionBank.All.Select(q => q.Id).ToHashSet();

    public static IReadOnlySet<Guid> Purge(RuntimeState state, IReadOnlySet<Guid> requested, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(requested);
        // Authored interview questions can never be purged through this path, whatever a caller passes.
        var questionIds = requested.Where(id => id != Guid.Empty && !AuthoredIds.Contains(id)).ToHashSet();
        if (questionIds.Count == 0) return new HashSet<Guid>();
        bool Generated(LearningItem card) => card.GeneratedQuestionId is { } q && questionIds.Contains(q);

        // Every durable card copy: live card, projection baseline, review result snapshots and creation receipts.
        var evidence = state.Learning.Values.Concat(state.TrainingBaselines.Values).Concat(state.Reviews.Values.Select(r => r.Result)).ToArray();
        var cardIds = evidence.Where(Generated).Select(c => c.Id)
            .Concat(state.CreationReceipts.Values.Where(r => r.Result is { } result && Generated(result)).Select(r => r.Result!.Id))
            .ToHashSet();
        LearningCreationReceipt.Retire(state, cardIds, at);
        foreach (var cardId in cardIds)
        {
            AgentRuntime.InvalidateSource(state, "learning", cardId);
            state.Learning.Remove(cardId); state.TrainingBaselines.Remove(cardId); state.TrainingBaselineReviews.Remove(cardId);
            foreach (var review in state.Reviews.Values.Where(r => r.Event.LearningItemId == cardId || Generated(r.Result)).ToArray())
            {
                state.Reviews.Remove(review.RequestId); state.TrainingManualSequences.Remove(review.RequestId);
            }
            foreach (var recall in state.VoiceRecalls.Values.Where(r => r.CardId == cardId).ToArray()) state.VoiceRecalls.Remove(recall.Id);
        }

        // Answers and assessments are cleared and tombstoned; content-derived receipt fingerprints are purged so a replay
        // can neither match old text nor restore it (existing purged-operation refusal).
        var attempts = state.TrainingAttempts.Values.Where(a => questionIds.Contains(a.QuestionId)).ToArray();
        foreach (var attempt in attempts)
        {
            var changed = attempt.DeletedAt is null || attempt.Answers.Count != 0 || attempt.Assessments.Count != 0 || attempt.CanonicalAssessmentId is not null;
            state.TrainingAttempts[attempt.Id] = attempt with { Revision = changed ? checked(attempt.Revision + 1) : attempt.Revision,
                DeletedAt = attempt.DeletedAt ?? at, Answers = [], Assessments = [], CanonicalAssessmentId = null };
            ReasoningLifecycle.Retire(state, attempt.Id);
        }
        var attemptIds = attempts.Select(a => a.Id).ToHashSet();
        foreach (var operation in state.TrainingOperations.Where(x => attemptIds.Contains(x.Value.AttemptId)).ToArray())
            state.TrainingOperations[operation.Key] = operation.Value with { Fingerprint = "" };

        // Drill steps carry the prompt/reference too: replace them with a fixed content-free tombstone. A drill whose
        // unanswered step was purged can no longer be answered, so an active one is cancelled rather than left pointing at it.
        var found = new HashSet<Guid>(cardIds.Where(questionIds.Contains));
        foreach (var attempt in attempts) found.Add(attempt.QuestionId);
        foreach (var drill in state.TrainingDrills.Values.ToArray())
        {
            if (!drill.Steps.Any(step => Purgeable(step) && !IsTombstone(step))) continue;   // idempotent: already purged
            var steps = drill.Steps.Select(step => Purgeable(step) && !IsTombstone(step) ? Tombstone(step) : step).ToArray();
            foreach (var step in drill.Steps.Where(Purgeable)) found.Add(step.GeneratedQuestionId ?? step.QuestionId);
            var pendingPurged = drill.Status == "Active" && drill.AttemptIds.Count < drill.Steps.Count && Purgeable(drill.Steps[drill.AttemptIds.Count]);
            state.TrainingDrills[drill.Id] = drill with { Steps = steps, Revision = checked(drill.Revision + 1),
                Status = pendingPurged ? "Cancelled" : drill.Status };
        }
        return found;

        bool Purgeable(TrainingDrillStep step) => step.GeneratedQuestionId is { } q ? questionIds.Contains(q) : questionIds.Contains(step.QuestionId);
    }

    private static bool IsTombstone(TrainingDrillStep step) => step.Prompt == TombstoneText && step.Reference == TombstoneText && step.Title == TombstoneTitle;

    private static TrainingDrillStep Tombstone(TrainingDrillStep step) => step with
    {
        Title = TombstoneTitle, Prompt = TombstoneText, Reference = TombstoneText, SourceUrl = "", CodeSnippet = null
    };
}
