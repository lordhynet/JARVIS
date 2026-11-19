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
        foreach (var practice in practices) PurgeSession(state, practice, at);
        var cardIds = state.Learning.Values.Where(c => c.SourceRecordingId == recordingId).Select(c => c.Id)
            .Concat(state.Reviews.Values.Where(r => r.Result.SourceRecordingId == recordingId).Select(r => r.Event.LearningItemId)).ToHashSet();
        foreach (var cardId in cardIds) {
            AgentRuntime.InvalidateSource(state, "learning", cardId); state.Learning.Remove(cardId);
            foreach (var review in state.Reviews.Values.Where(r => r.Event.LearningItemId == cardId).ToArray()) state.Reviews.Remove(review.RequestId);
            foreach (var recall in state.VoiceRecalls.Values.Where(r => r.CardId == cardId).ToArray()) state.VoiceRecalls.Remove(recall.Id);
        }
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
