using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Coaching;

// Stores selection/lifecycle only. Answers live in the existing canonical coaching history.
public sealed record VoiceCoachingView(Guid Id, string Status, CoachingPrompt? Prompt, Guid? AttemptId,
    string? CapturedAnswer, CoachingAnalysis? TextAnalysis, int? Revision = null);

public sealed class VoiceCoachingService(IRuntimeStore store, ISessionReader sessions, TimeProvider clock)
{
    private static VoiceCoaching? Active(RuntimeState s, Guid session) => s.VoiceCoachings.Values
        .SingleOrDefault(x => x.SessionId == session && x.Status is "Question" or "Correcting");
    public async Task<VoiceCoachingView?> RunAsync(Guid sessionId, string action, Guid utteranceId,
        string? exerciseId, Guid? storyId, int? storyVersion, int? claimIndex, CancellationToken ct)
    {
        Guard.Require(action is "start" or "current" or "answer" or "stop", 400, "Unsupported coaching action.");
        Guard.Require(action == "start" || exerciseId is null && storyId is null && storyVersion is null && claimIndex is null,
            400, "Exercise selection is only valid when starting coaching.");
        var session = await sessions.GetAsync(sessionId, ct);
        Guard.Require(session is { Status: SessionStatus.Active }, 409, "Resume the conversation before coaching.");
        var source = session.Messages?.LastOrDefault(m => m.Role == ChatRoles.User);
        if (action != "current") Guard.Require(source is not null && !source.Restricted && source.RequestId == utteranceId,
            409, "Use the latest captured user speech, not generated or historical text.");
        return await store.UpdateAsync(s => {
            Guard.Require(!s.RetiredSessions.Contains(sessionId), 409, "This conversation was retired.");
            var active = Active(s, sessionId);
            if (action == "current")
            {
                var latest = active ?? s.VoiceCoachings.Values.Where(x => x.SessionId == sessionId && x.Status == "Completed")
                    .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefault();
                return latest is null ? null : View(s, latest);
            }
            var id = VoicePracticeService.Operation("coaching-start", sessionId, utteranceId);
            if (action == "start")
            {
                CoachingService.Prompt(s, exerciseId!, storyId, storyVersion, claimIndex);
                if (s.VoiceCoachings.TryGetValue(id, out var replay))
                {
                    Guard.Require(Same(replay, exerciseId, storyId, storyVersion, claimIndex), 409, "That utterance already selected a different exercise.");
                    return View(s, replay);
                }
                if (active is not null)
                {
                    Guard.Require(Same(active, exerciseId, storyId, storyVersion, claimIndex), 409, "Stop the current coaching exercise before selecting another.");
                    return View(s, active);
                }
                Guard.Require(s.VoiceCoachings.Count < 1000, 409, "Voice coaching history is full; apply retention first.");
                var started = new VoiceCoaching(id, sessionId, exerciseId!, storyId, storyVersion, claimIndex, utteranceId, clock.GetUtcNow(),
                    ExerciseVersion: CoachingCatalog.Get(exerciseId!).Version);
                s.VoiceCoachings.Add(id, started); return View(s, started);
            }
            var prior = s.VoiceCoachings.Values.FirstOrDefault(x => x.SessionId == sessionId &&
                (action == "answer" ? x.AnswerUtteranceId == utteranceId : x.StopUtteranceId == utteranceId));
            if (prior is not null) return action == "stop" && prior.AttemptId is not null
                ? new(prior.Id, prior.Status, null, prior.AttemptId, null, null) : View(s, prior);
            if (action == "stop")
            {
                if (active is null) return null;
                var stopped = active with { Status = active.Status == "Correcting" ? "Completed" : "Cancelled", StopUtteranceId = utteranceId, UpdatedAt = clock.GetUtcNow() };
                s.VoiceCoachings[active.Id] = stopped;
                // Cancelling correction must remain possible even if its saved source was changed/deleted.
                return active.Status == "Correcting" ? new(stopped.Id, stopped.Status, null, stopped.AttemptId, null, null) : View(s, stopped);
            }
            Guard.Require(active is not null, 409, "Start a coaching exercise before answering.");
            Guard.Require(active.Status == "Question", 409, "A correction is pending. Use the coaching correction answer action.");
            Guard.Require(utteranceId != active.StartUtteranceId && source!.At >= active.CreatedAt,
                409, "The start instruction is not an answer. Ask the prompt and wait for the user.");
            var prompt = CoachingService.Prompt(s, active.ExerciseId, active.StoryId, active.StoryVersion, active.ClaimIndex);
            Guard.Require(prompt.Exercise.Version == active.ExerciseVersion, 409, "This exercise changed. Stop it and start its current version.");
            var saved = CoachingService.Save(s, new(VoicePracticeService.Operation("coaching-answer", sessionId, utteranceId),
                sessionId, prompt.Exercise.Id, prompt.Exercise.Version, source!.Text, active.StoryId, active.StoryVersion, active.ClaimIndex), clock.GetUtcNow());
            var completed = active with { Status = "Completed", AnswerUtteranceId = utteranceId, AttemptId = saved.Attempt.Id, UpdatedAt = clock.GetUtcNow() };
            s.VoiceCoachings[active.Id] = completed; return View(s, completed);
        }, ct);
    }
    public async Task<VoiceCoachingView> CorrectAsync(Guid sessionId, string action, Guid utteranceId, Guid attemptId, int expectedRevision, CancellationToken ct)
    {
        Guard.Require(action is "prepare" or "answer", 400, "Unsupported coaching correction action.");
        var session = await sessions.GetAsync(sessionId, ct);
        Guard.Require(session is { Status: SessionStatus.Active }, 409, "Resume the conversation before correcting an answer.");
        var source = session.Messages?.LastOrDefault(m => m.Role == ChatRoles.User);
        Guard.Require(source is not null && !source.Restricted && source.RequestId == utteranceId, 409, "Use the latest unrestricted captured user speech for correction.");
        return await store.UpdateAsync(s => {
            Guard.Require(!s.RetiredSessions.Contains(sessionId), 409, "This conversation was retired.");
            var selected = s.VoiceCoachings.Values.SingleOrDefault(x => x.SessionId == sessionId && x.AttemptId == attemptId);
            Guard.Require(selected is not null, 404, "This conversation has no such voice coaching answer.");
            var operation = VoicePracticeService.Operation("coaching-correct", sessionId, utteranceId);
            if (action == "answer" && s.CoachingOperations.ContainsKey(operation))
            {
                CoachingService.Correct(s, attemptId, new(operation, expectedRevision, source.Text), clock.GetUtcNow());
                return View(s, selected);
            }
            var view = View(s, selected); // Reject removed answers and changed saved sources before any write.
            Guard.Require(expectedRevision > 0 && view.Revision == expectedRevision, 409, "The answer changed. Resume its latest feedback before correcting it.");
            if (action == "prepare")
            {
                Guard.Require(selected.Status is "Completed" or "Correcting", 409, "Finish the original answer before correcting it.");
                Guard.Require(Active(s, sessionId) is not { } other || other.Id == selected.Id, 409, "Stop the current exercise before correcting another answer.");
                Guard.Require(utteranceId != selected.AnswerUtteranceId, 409, "Request correction separately from the original answer.");
                if (selected.Status == "Correcting")
                {
                    Guard.Require(selected.CorrectionStartUtteranceId == utteranceId && selected.CorrectionExpectedRevision == expectedRevision,
                        409, "A correction is already pending. Restate the answer or stop correction first.");
                    return view;
                }
                var preparing = selected with { Status = "Correcting", CorrectionStartUtteranceId = utteranceId,
                    CorrectionExpectedRevision = expectedRevision, CorrectionStartedAt = clock.GetUtcNow() };
                s.VoiceCoachings[selected.Id] = preparing; return View(s, preparing);
            }
            Guard.Require(selected.Status == "Correcting" && selected.CorrectionExpectedRevision == expectedRevision &&
                utteranceId != selected.CorrectionStartUtteranceId && source.At >= selected.CorrectionStartedAt,
                409, "Request correction, then wait for a separate complete replacement answer.");
            CoachingService.Correct(s, attemptId, new(operation, expectedRevision, source.Text), clock.GetUtcNow());
            var completed = selected with { Status = "Completed", UpdatedAt = clock.GetUtcNow() };
            s.VoiceCoachings[selected.Id] = completed; return View(s, completed);
        }, ct);
    }
    private static bool Same(VoiceCoaching x, string? exercise, Guid? story, int? version, int? claim) =>
        x.ExerciseId == exercise && x.StoryId == story && x.StoryVersion == version && x.ClaimIndex == claim;
    private static VoiceCoachingView View(RuntimeState s, VoiceCoaching x)
    {
        if (x.Status == "Question") Guard.Require(CoachingCatalog.Get(x.ExerciseId).Version == x.ExerciseVersion, 409, "This exercise changed. Stop it and start its current version.");
        CoachingAttemptView? answer = null;
        if (x.AttemptId is Guid id)
        {
            Guard.Require(s.CoachingAttempts.TryGetValue(id, out var attempt) && attempt.DeletedAt is null, 409, "That coaching answer was removed; it cannot be replayed.");
            Guard.Require(CoachingService.SourceCurrent(s, attempt), 409, "The source story changed. Start a new exercise with its current version.");
            answer = CoachingService.View(s, attempt);
        }
        return new(x.Id, x.Status, x.Status == "Question" ? CoachingService.Prompt(s, x.ExerciseId, x.StoryId, x.StoryVersion, x.ClaimIndex) : null,
            x.AttemptId, answer?.Attempt.Answers.Last().Text, answer?.Analysis, answer?.Attempt.Revision);
    }
}
