using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Coaching;

public sealed class CoachingService(IRuntimeStore store, ISessionReader sessions, TimeProvider clock)
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    public static double? Duration(double? seconds)
    {
        Guard.Require(seconds is null || double.IsFinite(seconds.Value) && seconds is >= 1 and <= 3600, 400, "Reported duration must be 1–3600 seconds or omitted.");
        return seconds;
    }
    public static CoachingPrompt Prompt(RuntimeState state, string exerciseId, Guid? storyId, int? storyVersion, int? claimIndex)
    {
        var exercise = CoachingCatalog.Get(exerciseId);
        Guard.Require(!exercise.RequiresStory || storyId.HasValue, 400, "Select a real saved story for this exercise.");
        Guard.Require(storyId.HasValue || storyVersion is null && claimIndex is null, 400, "Story version/claim requires a story.");
        if (storyId is not Guid id) return new(exercise, exercise.Prompt, null, null, null);
        Guard.Require(state.Stories.TryGetValue(id, out var story), 404, "Source story not found.");
        Guard.Require(storyVersion == story.Version, 409, "Source story changed. Reload it before practising.");
        string? claim = null;
        if (exercise.RequiresClaim)
        {
            Guard.Require(claimIndex is >= 0 && claimIndex < (story.Content.CvClaims?.Length ?? 0), 400, "Select an existing saved CV claim.");
            claim = story.Content.CvClaims![claimIndex.Value];
        }
        else Guard.Require(claimIndex is null, 400, "This exercise does not select a CV claim.");
        return new(exercise, $"{exercise.Prompt}\nStory: {story.Content.Title}. Service: {story.Content.ServiceFeature}." +
            (claim is null ? "" : $"\nSaved claim: {claim}\nWhat was measured, what did your service own, and what evidence supports this claim?"), id, story.Version, claimIndex);
    }
    public static bool SourceCurrent(RuntimeState state, CoachingAttempt attempt) => attempt.StoryId is not Guid id ||
        state.Stories.TryGetValue(id, out var story) && story.Version == attempt.StoryVersion;
    public static CoachingAttemptView View(RuntimeState state, CoachingAttempt attempt)
    {
        var answer = attempt.Answers[^1];
        return new(attempt, CoachingCatalog.Get(attempt.ExerciseId), TextCoach.Analyse(answer.Text, answer.ReportedDurationSeconds), SourceCurrent(state, attempt),
            attempt.AiAssessmentId is Guid id && SourceCurrent(state, attempt) ? state.AiCoachAssessments.GetValueOrDefault(id) : null);
    }
    public async Task<CoachingAttemptView> SaveAsync(SaveCoachingAnswerRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.SessionId != Guid.Empty, 400, "RequestId and SessionId are required.");
        Guard.Require(await sessions.GetAsync(request.SessionId, ct) is not null, 404, "Session not found.");
        return await store.UpdateAsync(s => Save(s, request, clock.GetUtcNow()), ct);
    }
    // Shared transaction-level operation for text and transcript-backed voice answers.
    public static CoachingAttemptView Save(RuntimeState s, SaveCoachingAnswerRequest request, DateTimeOffset now)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.SessionId != Guid.Empty, 400, "RequestId and SessionId are required.");
        var exercise = CoachingCatalog.Get(request.ExerciseId);
        Guard.Require(request.ExerciseVersion == exercise.Version, 409, "Exercise version changed.");
        var text = Guard.Text(request.Text, "Answer", 20000); var seconds = Duration(request.ReportedDurationSeconds);
        var fingerprint = Hash(new { request.SessionId, request.ExerciseId, request.ExerciseVersion, text, request.StoryId, request.StoryVersion, request.ClaimIndex, seconds });
        if (Replay(s, request.RequestId, "save", request.RequestId, fingerprint) is { } replay) return View(s, replay);
        Guard.Require(!s.RetiredSessions.Contains(request.SessionId), 409, "This session was retired by retention.");
        Prompt(s, request.ExerciseId, request.StoryId, request.StoryVersion, request.ClaimIndex);
        Guard.Require(s.CoachingAttempts.Count < 2000, 409, "Coaching history is full for this personal test build.");
        var attempt = new CoachingAttempt(request.RequestId, request.SessionId, request.ExerciseId, request.ExerciseVersion,
            request.StoryId, request.StoryVersion, request.ClaimIndex, 1, now, [new(1, text, seconds, now)]);
        s.CoachingAttempts.Add(attempt.Id, attempt); s.CoachingOperations.Add(request.RequestId, new("save", attempt.Id, fingerprint));
        return View(s, attempt);
    }
    public Task<CoachingAttemptView> CorrectAsync(Guid id, CorrectCoachingAnswerRequest request, CancellationToken ct) =>
        store.UpdateAsync(s => Correct(s, id, request, clock.GetUtcNow()), ct);
    public static CoachingAttemptView Correct(RuntimeState s, Guid id, CorrectCoachingAnswerRequest request, DateTimeOffset now)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        var text = Guard.Text(request.Text, "Answer", 20000); var seconds = Duration(request.ReportedDurationSeconds);
        var fingerprint = Hash(new { request.ExpectedRevision, text, seconds });
        if (Replay(s, request.RequestId, "correct", id, fingerprint) is { } replay) return View(s, replay);
        var attempt = Current(s, id, request.ExpectedRevision);
        Guard.Require(SourceCurrent(s, attempt), 409, "Source story changed. Start a new attempt with its current version.");
        Guard.Require(attempt.Answers.Count < 5, 409, "Answer history is limited to five versions.");
        var updated = attempt with { Revision = checked(attempt.Revision + 1), AiAssessmentId = null, Answers = [..attempt.Answers, new(attempt.Answers.Count + 1, text, seconds, now)] };
        s.CoachingAttempts[id] = updated; s.CoachingOperations.Add(request.RequestId, new("correct", id, fingerprint));
        return View(s, updated);
    }
    public Task<bool> DeleteAsync(Guid id, int revision, CancellationToken ct) => store.UpdateAsync(s =>
    {
        if (!s.CoachingAttempts.TryGetValue(id, out var prior) || prior.DeletedAt is not null) return false;
        var attempt = Current(s, id, revision); Purge(s, attempt, clock.GetUtcNow()); return true;
    }, ct);
    public static void Purge(RuntimeState state, CoachingAttempt attempt, DateTimeOffset at)
    {
        state.CoachingAttempts[attempt.Id] = attempt with { Answers = [], DeletedAt = at, Revision = checked(attempt.Revision + 1), AiAssessmentId = null };
        foreach (var id in state.AiCoachAssessments.Where(x => x.Value.AttemptId == attempt.Id).Select(x => x.Key).ToList()) state.AiCoachAssessments.Remove(id);
        ReasoningLifecycle.Retire(state, attempt.Id);
    }
    private static CoachingAttempt Current(RuntimeState s, Guid id, int revision)
    {
        Guard.Require(s.CoachingAttempts.TryGetValue(id, out var attempt) && attempt.DeletedAt is null, 404, "Coaching attempt not found.");
        Guard.Require(revision > 0 && attempt.Revision == revision, 409, "Coaching attempt changed. Reload before editing."); return attempt;
    }
    private static CoachingAttempt? Replay(RuntimeState s, Guid operation, string kind, Guid id, string fingerprint)
    {
        if (!s.CoachingOperations.TryGetValue(operation, out var previous)) return null;
        Guard.Require(previous.Kind == kind && previous.AttemptId == id && previous.Fingerprint == fingerprint, 409, "RequestId was used for different coaching input.");
        Guard.Require(s.CoachingAttempts.TryGetValue(id, out var attempt) && attempt.DeletedAt is null, 409, "Coaching attempt was deleted. Use a new request ID."); return attempt;
    }
}
