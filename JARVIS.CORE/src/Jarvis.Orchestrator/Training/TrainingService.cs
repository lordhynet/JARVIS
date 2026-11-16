using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Training;

public sealed class TrainingService(IRuntimeStore store, ISessionReader sessions, TimeProvider clock)
{
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    public async Task<TrainingAttempt> SubmitAsync(SubmitTrainingAnswerRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.SessionId != Guid.Empty, 400, "RequestId and SessionId are required.");
        Guard.Require(Enum.IsDefined(request.Mode), 400, "Unknown training mode.");
        Guard.Require(request.DrillId.HasValue || request.StepIndex is null, 400, "A step index requires a drill.");
        Guard.Require(request.DrillId.HasValue || request.Mode is TrainingMode.RapidDrill or TrainingMode.ExplainSimply or TrainingMode.SpacedReview,
            400, "Start a guided drill for this mode so its authored scenario and progress are preserved.");
        var question = QuestionBank.Get(request.QuestionId);
        Guard.Require(question.Version == request.QuestionVersion, 409, "Question version changed. Reload the question.");
        var text = request.Skipped ? "" : Guard.Text(request.Text, "Answer", 20000);
        Guard.Require(!request.Skipped || string.IsNullOrWhiteSpace(request.Text), 400, "A skipped attempt cannot include an answer.");
        var session = await sessions.GetAsync(request.SessionId, ct);
        Guard.Require(session is not null, 404, "Session not found.");
        var fingerprint = Hash(new { request.SessionId, question.Id, question.Version, request.Mode, text, request.Skipped });
        if (request.DrillId.HasValue) fingerprint = Hash(new { request.SessionId, question.Id, question.Version, request.Mode, text, request.Skipped, request.DrillId, request.StepIndex });
        return await store.UpdateAsync(state =>
        {
            if (Replay(state, request.RequestId, "submit", request.RequestId, fingerprint) is { } replay) return replay;
            Guard.Require(!state.RetiredSessions.Contains(request.SessionId), 409, "This session was retired by retention.");
            TrainingDrill? drill = null;
            if (request.DrillId is Guid drillId)
            {
                Guard.Require(session.Status == SessionStatus.Active, 409, "Resume the session before submitting another guided stage.");
                Guard.Require(state.TrainingDrills.TryGetValue(drillId, out drill) && drill.Status == "Active" && drill.SessionId == request.SessionId && drill.Mode == request.Mode,
                    409, "Guided drill changed, was cancelled, or belongs to another session.");
                Guard.Require(request.StepIndex == drill.AttemptIds.Count && drill.AttemptIds.Count < drill.Steps.Count, 409, "Drill step changed. Reload before saving.");
                var step = drill.Steps[drill.AttemptIds.Count];
                Guard.Require(step.QuestionId == question.Id && step.QuestionVersion == question.Version, 409, "Question does not match the current drill step.");
            }
            Guard.Require(state.TrainingAttempts.Count < 10000, 409, "Training history is full. Additional attempts are unavailable in this personal test build.");
            var now = clock.GetUtcNow();
            if (!state.Learning.TryGetValue(question.Id, out var card))
            {
                card = new(question.Id, question.Domain, question.Prompt, question.Reference, 0, 5, now, now);
                // Skipping a previously unseen question must not add a due card or reduce mastery.
                if (!request.Skipped) state.Learning.Add(card.Id, card);
            }
            if (!request.Skipped)
            {
                Guard.Require(card.Domain == question.Domain && card.Prompt == question.Prompt, 409, "Question card conflicts with existing content.");
                if (state.TrainingBaselines.TryAdd(question.Id, card))
                    state.TrainingBaselineReviews[question.Id] = state.Reviews.Values.Where(x => x.Event.LearningItemId == question.Id).Select(x => x.RequestId).ToHashSet();
            }
            var sequence = state.NextTrainingSequence;
            state.NextTrainingSequence = checked(sequence + 1);
            var attempt = new TrainingAttempt(request.RequestId, request.SessionId, question.Id, question.Version, request.Mode,
                sequence, 1, request.Skipped, now, request.Skipped ? [] : [new(1, text, "text_only", now)], [], DrillId: request.DrillId, StepIndex: request.StepIndex);
            state.TrainingAttempts.Add(attempt.Id, attempt);
            state.TrainingOperations.Add(request.RequestId, new("submit", attempt.Id, fingerprint));
            if (drill is not null) state.TrainingDrills[drill.Id] = drill with { Revision = drill.Revision + 1,
                AttemptIds = [..drill.AttemptIds, attempt.Id], Status = drill.AttemptIds.Count + 1 == drill.Steps.Count ? "Completed" : "Active" };
            return attempt;
        }, ct);
    }

    public Task<TrainingAttempt> AssessAsync(Guid id, SelfAssessmentRequest request, CancellationToken ct)
        => AssessCoreAsync(id, request, "self", ct);
    public Task<TrainingAttempt> AssessAiAsync(Guid id, SelfAssessmentRequest request, string model, CancellationToken ct)
        => AssessCoreAsync(id, request, "ai:" + model, ct);
    private Task<TrainingAttempt> AssessCoreAsync(Guid id, SelfAssessmentRequest request, string source, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        var zone = TrainingProjection.Zone(request.TimeZone);
        Guard.Require(request.Scores is { Count: > 0 and <= 7 }, 400, "Provide one to seven dimension entries.");
        var scores = request.Scores.Select(x =>
        {
            Guard.Require(x is not null && Enum.IsDefined(x.Dimension) && (x.Value is null || x.Value is >= 0 and <= 4), 400, "Invalid assessment dimension or score.");
            var evidence = string.IsNullOrWhiteSpace(x.Evidence) ? "" : Guard.Text(x.Evidence, "Evidence", 512);
            var rationale = x.Value is null ? "" : Guard.Text(x.Rationale, "Self-assessment rationale", 512);
            return new DimensionScore(x.Dimension, x.Value, evidence, rationale);
        }).OrderBy(x => x.Dimension).ToList();
        Guard.Require(scores.Select(x => x.Dimension).Distinct().Count() == scores.Count, 400, "A dimension can appear only once.");
        var fingerprint = Hash(new { request.ExpectedRevision, request.TextRevision, scores, zone.Id });
        return store.UpdateAsync(state =>
        {
            var kind = source == "self" ? "assessment" : "assessment:" + source;
            if (Replay(state, request.RequestId, kind, id, fingerprint) is { } replay) return replay;
            var attempt = Current(state, id, request.ExpectedRevision);
            Guard.Require(!attempt.Skipped && request.TextRevision == attempt.TextRevision, 409, "Only the current non-skipped answer can be assessed.");
            Guard.Require(attempt.Assessments.Count < 5, 409, "Assessment history is full. Start a fresh practice attempt.");
            foreach (var score in scores)
            {
                Guard.Require(string.IsNullOrEmpty(score.Evidence) || attempt.CurrentAnswer.Contains(score.Evidence, StringComparison.Ordinal), 400, "Evidence must quote the current answer.");
                Guard.Require(score.Value is not (3 or 4) || !string.IsNullOrWhiteSpace(score.Evidence), 400, "A strong score requires an answer excerpt.");
            }
            var content = scores.Where(x => x.Dimension != AssessmentDimension.CommunicationStructure && x.Value.HasValue).Select(x => x.Value!.Value).ToList();
            var assessment = new TrainingAssessment(request.RequestId, attempt.Assessments.Count + 1, attempt.TextRevision,
                source, source == "self" ? "self-rubric-v1" : "ai-rubric-v1", scores, content.Count == 0 ? null : content.Average(), zone.Id, clock.GetUtcNow());
            var updated = attempt with { Revision = checked(attempt.Revision + 1), Assessments = [..attempt.Assessments, assessment], CanonicalAssessmentId = assessment.Id };
            state.TrainingAttempts[id] = updated;
            state.TrainingOperations.Add(request.RequestId, new(kind, id, fingerprint));
            TrainingProjection.Rebuild(state, attempt.QuestionId);
            return updated;
        }, ct);
    }

    public Task<TrainingAttempt> CorrectAsync(Guid id, CorrectTrainingAnswerRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        var text = Guard.Text(request.Text, "Corrected answer", 20000);
        var fingerprint = Hash(new { request.ExpectedRevision, text });
        return store.UpdateAsync(state =>
        {
            if (Replay(state, request.RequestId, "correction", id, fingerprint) is { } replay) return replay;
            var attempt = Current(state, id, request.ExpectedRevision);
            Guard.Require(!attempt.Skipped && attempt.Answers.Count < 5, 409, "Skipped answers cannot be corrected, and answer history is limited to five versions.");
            var updated = attempt with { Revision = checked(attempt.Revision + 1), CanonicalAssessmentId = null,
                Answers = [..attempt.Answers, new(attempt.TextRevision + 1, text, "user_corrected", clock.GetUtcNow())] };
            state.TrainingAttempts[id] = updated;
            state.TrainingOperations.Add(request.RequestId, new("correction", id, fingerprint));
            TrainingProjection.Rebuild(state, attempt.QuestionId);
            return updated;
        }, ct);
    }

    public Task<bool> DeleteAsync(Guid id, int expectedRevision, CancellationToken ct) => store.UpdateAsync(state =>
    {
        if (!state.TrainingAttempts.TryGetValue(id, out var prior) || prior.DeletedAt is not null) return false;
        var attempt = Current(state, id, expectedRevision);
        state.TrainingAttempts[id] = attempt with { Revision = checked(attempt.Revision + 1), DeletedAt = clock.GetUtcNow(),
            Answers = [], Assessments = [], CanonicalAssessmentId = null };
        ReasoningLifecycle.Retire(state, id);
        if (state.TrainingBaselines.ContainsKey(attempt.QuestionId)) TrainingProjection.Rebuild(state, attempt.QuestionId);
        return true;
    }, ct);

    private static TrainingAttempt Current(RuntimeState state, Guid id, int revision)
    {
        Guard.Require(state.TrainingAttempts.TryGetValue(id, out var attempt) && attempt.DeletedAt is null, 404, "Training attempt not found.");
        Guard.Require(revision > 0 && attempt.Revision == revision, 409, "Training attempt changed. Reload before editing.");
        return attempt;
    }

    private static TrainingAttempt? Replay(RuntimeState state, Guid operation, string kind, Guid id, string fingerprint)
    {
        if (!state.TrainingOperations.TryGetValue(operation, out var prior)) return null;
        Guard.Require(prior.Kind == kind && prior.AttemptId == id && prior.Fingerprint == fingerprint, 409, "RequestId was used for different training input.");
        var attempt = state.TrainingAttempts[id];
        Guard.Require(attempt.DeletedAt is null, 409, "This training attempt was deleted; use a new RequestId for new practice.");
        return attempt;
    }
}
