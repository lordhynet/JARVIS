using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

public static class TrainingDeletion
{
    // Null means the existing idempotent false response, including a repeated deletion with an old revision.
    public static TrainingAttempt? Decide(Guid id, int expectedRevision, TrainingAttempt? current, DateTimeOffset at)
    {
        if (current is null) return null;
        if (current.Id != id) throw new InvalidDataException("Training deletion identity differs.");
        if (current.DeletedAt is not null) return null;
        Guard.Require(expectedRevision > 0 && current.Revision == expectedRevision, 409, "Training attempt changed. Reload before editing.");
        return current with { Revision = checked(current.Revision + 1), DeletedAt = at,
            Answers = [], Assessments = [], CanonicalAssessmentId = null };
    }

    public static bool Apply(RuntimeState state, Guid id, int expectedRevision, TimeProvider clock)
    {
        var change = Decide(id, expectedRevision, state.TrainingAttempts.GetValueOrDefault(id), clock.GetUtcNow());
        if (change is null) return false;
        // A privacy-removed baseline stays absent. Complete projection before mutating the cohesive state.
        var card = state.TrainingBaselines.TryGetValue(change.QuestionId, out var baseline)
            ? TrainingProjection.Project(change.QuestionId, baseline,
                state.TrainingBaselineReviews.GetValueOrDefault(change.QuestionId) ?? [], state.Reviews.Values,
                state.TrainingManualSequences, state.TrainingAttempts.Values.Where(a => a.Id != id).Append(change)) : null;
        ReasoningLifecycle.Retire(state, id);
        state.TrainingAttempts[id] = change;
        if (card is not null) state.Learning[change.QuestionId] = card;
        return true;
    }
}
