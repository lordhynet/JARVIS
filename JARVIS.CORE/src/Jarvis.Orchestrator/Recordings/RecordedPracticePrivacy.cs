using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Recordings;

public static class RecordedPracticePrivacy
{
    public static bool PrivateSession(RuntimeState state, Guid session) => state.VoiceCoachings.Values.Any(p => p.SourceRecordingId is not null && p.SessionId == session);
    public static ConversationSession Redact(ConversationSession session, DateTimeOffset at) => session with {
        Title = "Removed private interview practice", Status = SessionStatus.Ended, ActiveDeviceId = null, CurrentTask = null,
        Messages = [], Checkpoints = [], Handoff = null, PendingVoiceProjectionIds = [], VoiceSummary = null, UpdatedAt = at
    };
    public static async Task RedactSessionsAsync(RuntimeState state, Func<Guid, Func<ConversationSession, ConversationSession>, Task<ConversationSession?>>? update,
        IEnumerable<Guid> sessions, DateTimeOffset at)
    {
        var ids = sessions.Distinct().ToArray(); if (ids.Length == 0) return;
        Guard.Require(update is not null, 503, "Private interview deletion requires the composite session transaction.");
        foreach (var id in ids) await update(id, session => Redact(session, at));
    }
    public static IReadOnlyList<Guid> Purge(RuntimeState state, Guid recordingId, DateTimeOffset at)
    {
        var practices = state.VoiceCoachings.Values.Where(p => p.SourceRecordingId == recordingId).ToArray();
        var questions = practices.SelectMany(p => p.RecordedQuestions ?? []).ToHashSet();
        var evidence = state.Learning.Values.Concat(state.TrainingBaselines.Values).Concat(state.Reviews.Values.Select(r => r.Result)).ToArray();
        // Resolve transcript lineage only when question-only evidence needs it. A retained
        // baseline may be the last source-bearing record after ordinary card removal.
        if (evidence.Any(c => c.RecordedQuestionId is not null && c.SourceRecordingId is null) ||
            state.CreationReceipts.Values.Any(r => r.Result is { RecordedQuestionId: not null, SourceRecordingId: null }))
            foreach (var document in state.ImportedDocuments.Values.Where(d => d.SourceRecordingId == recordingId && d.DeletedAt is null))
                foreach (var question in RecordedInterviewPractice.Questions(state, document, matchStories: false)) questions.Add(question.Id);
        foreach (var card in evidence.Where(c => c.SourceRecordingId == recordingId))
            if (card.RecordedQuestionId is { } question) questions.Add(question);
        foreach (var practice in practices) PurgeSession(state, practice, at);
        var cardIds = evidence.Where(c => c.SourceRecordingId == recordingId || c.RecordedQuestionId is { } question && questions.Contains(question)).Select(c => c.Id)
            .Concat(state.CreationReceipts.Values.Where(r => r.Result is { } result &&
                (result.SourceRecordingId == recordingId || result.RecordedQuestionId is { } question && questions.Contains(question))).Select(r => r.Result!.Id)).ToHashSet();
        LearningCreationReceipt.Retire(state, cardIds, at);
        foreach (var cardId in cardIds) {
            AgentRuntime.InvalidateSource(state, "learning", cardId); state.Learning.Remove(cardId);
            state.TrainingBaselines.Remove(cardId); state.TrainingBaselineReviews.Remove(cardId);
            foreach (var review in state.Reviews.Values.Where(r => r.Event.LearningItemId == cardId).ToArray()) {
                state.Reviews.Remove(review.RequestId); state.TrainingManualSequences.Remove(review.RequestId);
            }
            foreach (var recall in state.VoiceRecalls.Values.Where(r => r.CardId == cardId).ToArray()) state.VoiceRecalls.Remove(recall.Id);
        }
        var attempts = state.TrainingAttempts.Values.Where(a => cardIds.Contains(a.QuestionId)).ToArray();
        foreach (var attempt in attempts) {
            var changed = attempt.DeletedAt is null || attempt.Answers.Count != 0 || attempt.Assessments.Count != 0 || attempt.CanonicalAssessmentId is not null;
            state.TrainingAttempts[attempt.Id] = attempt with { Revision = changed ? checked(attempt.Revision + 1) : attempt.Revision,
                DeletedAt = attempt.DeletedAt ?? at, Answers = [], Assessments = [], CanonicalAssessmentId = null };
            ReasoningLifecycle.Retire(state, attempt.Id);
        }
        var attemptIds = attempts.Select(a => a.Id).ToHashSet();
        foreach (var operation in state.TrainingOperations.Where(x => attemptIds.Contains(x.Value.AttemptId)).ToArray())
            state.TrainingOperations[operation.Key] = operation.Value with { Fingerprint = "" };
        return practices.Select(p => p.SessionId).Distinct().ToArray();
    }
    public static void PurgeSession(RuntimeState state, VoiceCoaching practice, DateTimeOffset at)
    {
        var session = practice.SessionId; state.RetiredSessions.Add(session); state.VoiceCoachings.Remove(practice.Id);
        foreach (var voice in state.VoiceTurns.Values.Where(v => v.SessionId == session).ToArray()) {
            AgentRuntime.InvalidateSource(state, "voice", voice.Id); state.VoiceTurns.Remove(voice.Id);
        }
        foreach (var attempt in state.CoachingAttempts.Values.Where(a => a.SessionId == session).ToArray())
            Jarvis.Orchestrator.Coaching.CoachingService.Purge(state, attempt, at);
        foreach (var tool in state.Tools.Values.Where(t => t.Invocation.SessionId == session).ToArray()) state.Tools.Remove(tool.Invocation.Id);
        foreach (var result in state.ProviderResults.Values.Where(r => r.ScopeId == session).ToArray()) state.ProviderResults.Remove(result.Id);
        foreach (var authority in state.DeviceVoiceAuthorities.Where(p => p.Value.SessionId == session).ToArray()) state.DeviceVoiceAuthorities.Remove(authority.Key);
        state.AgentVoiceReviews.Remove(session); ReasoningLifecycle.Retire(state, session);
        if (state.AudioLease?.SessionId == session) state.AudioLease = null;
        if (state.AudioDrainLease?.SessionId == session) { state.AudioDrainLease = null; state.AudioDrainUntil = null; }
    }
}
