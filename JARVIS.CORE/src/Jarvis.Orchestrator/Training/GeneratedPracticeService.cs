using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

// Core-held context for one generation step. Route revision and provenance come from the realtime session's own prepared
// route and capture, never from tool arguments; the model cannot attest them.
public sealed record GenerationContext(Guid SessionId, Guid UtteranceId, long PrivacyEpoch, string RouteRevision, string ProvenanceJson)
{
    public PendingBinding Binding => new(UtteranceId, PrivacyEpoch, RouteRevision);
}

public sealed record GeneratedQuestionText(string OutlineKey, string Title, string Prompt, string Reference, IReadOnlyList<string> FollowUps);
public sealed record GeneratedPracticeResult(TrainingDrill Drill, Guid QuestionId, bool Replayed);

// Starts generated drills and accepts generated questions inside one canonical store-port transaction each. Every gate from
// the approved design is enforced here, so no caller can skip one. Off unless PracticeGenerationOptions.Enabled.
public sealed class GeneratedPracticeService(IRuntimeStore store, PracticeGenerationOptions options, TimeProvider clock)
{
    public const int HourlyLimit = 30;
    public static readonly TimeSpan AuthoredWindow = TimeSpan.FromDays(30);
    public bool Enabled => options.Enabled;

    // Core-side identity of the prepared realtime route, and the provenance stored with each generated question.
    public static string RouteRevision(ModelRoute route) => $"{route.Provider}/{route.Model}/{route.PolicyVersion}/{route.RateCardRevision}";
    public static string Provenance(ModelRoute route) => JsonSerializer.Serialize(new
        { source = "ai-realtime", provider = route.Provider, model = route.Model, policyVersion = route.PolicyVersion, rateCardRevision = route.RateCardRevision });
    // One deterministic operation per captured request: retries of the same tool call replay instead of duplicating.
    public static Guid OperationFor(Guid session, Guid utterance) =>
        new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"generated-practice-op-v1:{session:N}:{utterance:N}")).AsSpan(0, 16));

    // Null means "not applicable": generation off, unsupported style, or authored questions not yet exhausted. The caller
    // then keeps its existing authored behaviour unchanged.
    public Task<TrainingDrill?> TryStartAsync(Guid requestId, TrainingMode mode, LearningDomain topic, GenerationContext context, CancellationToken ct = default)
    {
        if (!Enabled || !GeneratedDrillFlow.SupportsMode(mode)) return Task.FromResult<TrainingDrill?>(null);
        return store.UpdateWithGeneratedQuestionsAsync(new(context.SessionId, [], [], [context.SessionId]), (s, unit) =>
        {
            if (s.TrainingDrills.TryGetValue(requestId, out var replay)) return replay;
            Guard.Require(!s.RetiredSessions.Contains(context.SessionId), 409, "This session was retired by retention.");
            // One active practice per conversation, decided in this same transaction. An active authored drill is left to the
            // authored path (which resumes or refuses it). The same generated practice resumes, re-binding a pending need to
            // this newer request so an older generator response can no longer land; a different one is refused.
            var active = s.TrainingDrills.Values.Where(d => d.SessionId == context.SessionId && d.Status == "Active")
                .OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).FirstOrDefault();
            if (active is not null)
            {
                if (active.Generated is null) return null;
                Guard.Require(active.Mode == mode && active.Generated.Topic == topic, 409,
                    "A different practice is active. Ask whether to stop it before starting another.");
                if (!GeneratedDrillFlow.AwaitingQuestion(active)) return active;
                var refreshed = GeneratedDrillFlow.RefreshBinding(active, context.Binding);
                s.TrainingDrills[active.Id] = refreshed;
                return refreshed;
            }
            var now = clock.GetUtcNow();
            if (!PracticeOutlines.AuthoredExhausted(s.TrainingAttempts.Values, topic, now, AuthoredWindow)) return null;
            var next = PracticeOutlines.NextEntry(topic, AnsweredByEntry(s, unit, context.SessionId, topic));
            if (next is null) return null;
            Guard.Require(s.TrainingDrills.Count < 1000, 409, "Drill history is full.");
            var drill = GeneratedDrillFlow.Start(requestId, context.SessionId, mode, topic, next, context.Binding, now);
            s.TrainingDrills.Add(drill.Id, drill);
            return (TrainingDrill?)drill;
        }, ct);
    }

    public Task<GeneratedPracticeResult> GenerateAsync(GenerationContext context, Guid operationId, GeneratedQuestionText text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(text.FollowUps);
        Guard.Require(Enabled, 503, "Generated practice is not enabled.");
        Guard.Require(operationId != Guid.Empty, 400, "A generation operation ID is required.");
        var followUps = text.FollowUps.ToArray();
        return store.UpdateWithGeneratedQuestionsAsync(new(context.SessionId, [operationId], [], [context.SessionId]), (s, unit) =>
        {
            var now = clock.GetUtcNow();
            var drill = s.TrainingDrills.Values.Where(d => d.SessionId == context.SessionId && d.Generated is not null)
                .OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).FirstOrDefault();
            // Identical replay returns the frozen question; changed input under the same operation is a conflict.
            if (unit.ByOperation(operationId) is { } prior)
            {
                Guard.Require(drill is not null && prior.Fingerprint.Length > 0 &&
                    prior.Fingerprint == Fingerprint(context.SessionId, drill.Id, PriorStep(drill, prior.Question.Id), drill.Generated!.Topic, text, followUps),
                    409, "This practice question request was used for different content.");
                return new GeneratedPracticeResult(drill, prior.Question.Id, true);
            }
            Guard.Require(drill is { Status: "Active" } && GeneratedDrillFlow.AwaitingQuestion(drill), 409, "This practice is not waiting for a question.");
            Guard.Require(!s.RetiredSessions.Contains(context.SessionId), 409, "This session was retired by retention.");
            var pending = drill.Generated!.Pending!;
            // The response is bound to the current capture/authority; anything else is stale and changes nothing.
            var expected = pending with { OutlineKey = text.OutlineKey, TriggerUtteranceId = context.UtteranceId,
                PrivacyEpoch = context.PrivacyEpoch, RouteRevision = context.RouteRevision };
            Guard.Require(expected == pending, 409, "This practice question is out of date.");
            var manifest = s.PracticeContextManifests.GetValueOrDefault(context.SessionId);
            Guard.Require(manifest is not null && PracticeLineage.Current(s, manifest), 409,
                "Practice questions can't be generated here because personal context shown in this conversation can't be verified.");
            Guard.Require(unit.SessionCountSince(now.AddHours(-1)) < HourlyLimit, 429, "The hourly practice question limit has been reached.");
            var fingerprint = Fingerprint(context.SessionId, drill.Id, pending.StepIndex, drill.Generated.Topic, text, followUps);
            var id = unit.Add(new(context.SessionId, operationId, drill.Generated.Topic, text.OutlineKey, pending.OutlineVersion, fingerprint,
                text.Prompt, text.Reference, followUps, context.ProvenanceJson, PracticeLineage.LineageJson(manifest), now));
            var title = string.IsNullOrWhiteSpace(text.Title) ? "Practice question" : text.Title.Trim()[..Math.Min(text.Title.Trim().Length, 120)];
            var updated = GeneratedDrillFlow.ApplyQuestion(drill, pending, new(id, 1, title, text.Prompt, text.Reference, followUps));
            s.TrainingDrills[drill.Id] = updated;
            return new GeneratedPracticeResult(updated, id, false);
        }, ct);
    }

    public const string SubmitKind = "generated-submit";

    // Saves a canonical answer to the current generated step and, once the question group is answered, prepares the next need
    // (least-practised outline entry) bound to this answer's capture, or ends the drill on quota/outline exhaustion. One
    // port transaction, so the answer, the drill advance and the next need commit together. No revision card is created
    // yet: recall and dashboards do not label generated cards until the consumer slice lands.
    public Task<TrainingAttempt> SubmitAsync(GenerationContext context, Guid requestId, Guid drillId, int stepIndex, string answer, CancellationToken ct = default)
    {
        Guard.Require(Enabled, 503, "Generated practice is not enabled.");
        Guard.Require(requestId != Guid.Empty && drillId != Guid.Empty, 400, "Answer identities are required.");
        var text = Guard.Text(answer, "Answer", 20000);
        return store.UpdateWithGeneratedQuestionsAsync(new(context.SessionId, [], [], [context.SessionId]), (s, unit) =>
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, context.SessionId, drillId, stepIndex, text })));
            if (s.TrainingOperations.TryGetValue(requestId, out var prior))
            {
                Guard.Require(prior.Kind == SubmitKind && prior.Fingerprint.Length > 0 && prior.Fingerprint == fingerprint, 409, "RequestId was used for different training input.");
                var replay = s.TrainingAttempts[prior.AttemptId];
                Guard.Require(replay.DeletedAt is null, 409, "This training attempt was deleted; use a new RequestId for new practice.");
                return replay;
            }
            Guard.Require(!s.RetiredSessions.Contains(context.SessionId), 409, "This session was retired by retention.");
            Guard.Require(s.TrainingDrills.TryGetValue(drillId, out var drill) && drill is { Status: "Active", Generated: not null } && drill.SessionId == context.SessionId,
                409, "Generated practice changed, was stopped, or belongs to another conversation.");
            Guard.Require(!GeneratedDrillFlow.AwaitingQuestion(drill) && stepIndex == drill.AttemptIds.Count && stepIndex < drill.Steps.Count, 409, "Drill step changed. Reload before saving.");
            var step = drill.Steps[stepIndex];
            var question = step.GeneratedQuestionId is { } qid ? unit.LoadedIn(context.SessionId).FirstOrDefault(r => r.Question.Id == qid) : null;
            Guard.Require(question is not null, 409, "This practice question was removed; start a new practice request.");
            Guard.Require(s.TrainingAttempts.Count < 10000, 409, "Training history is full. Additional attempts are unavailable in this personal test build.");
            var now = clock.GetUtcNow();
            var sequence = s.NextTrainingSequence; s.NextTrainingSequence = checked(sequence + 1);
            var attempt = new TrainingAttempt(requestId, context.SessionId, question.Question.Id, step.QuestionVersion, drill.Mode, sequence, 1, false, now,
                [new(1, text, "text_only", now)], [], DrillId: drill.Id, StepIndex: stepIndex);
            s.TrainingAttempts.Add(attempt.Id, attempt);
            s.TrainingOperations.Add(requestId, new(SubmitKind, attempt.Id, fingerprint));
            var answered = drill with { AttemptIds = [.. drill.AttemptIds, attempt.Id], Revision = checked(drill.Revision + 1) };
            var next = PracticeOutlines.NextEntry(drill.Generated.Topic, AnsweredByEntry(s, unit, context.SessionId, drill.Generated.Topic));
            var quota = unit.SessionCountSince(now.AddHours(-1)) < HourlyLimit;
            s.TrainingDrills[drill.Id] = GeneratedDrillFlow.AfterAnswer(answered, next, context.Binding, quota);
            return attempt;
        }, ct);
    }

    // Answers per outline entry in this conversation, from retained canonical attempts on its generated questions.
    private static IReadOnlyDictionary<string, int> AnsweredByEntry(RuntimeState s, GeneratedQuestionUnit unit, Guid session, LearningDomain topic)
    {
        var outlineById = unit.LoadedIn(session).Where(r => r.Question.Topic == topic).ToDictionary(r => r.Question.Id, r => r.Question.OutlineKey);
        return s.TrainingAttempts.Values.Where(a => a.SessionId == session && a.DeletedAt is null && !a.Skipped && outlineById.ContainsKey(a.QuestionId))
            .GroupBy(a => outlineById[a.QuestionId]).ToDictionary(g => g.Key, g => g.Count());
    }

    private static int PriorStep(TrainingDrill drill, Guid questionId) =>
        drill.Steps.FirstOrDefault(step => step.GeneratedQuestionId == questionId)?.Index ?? -1;

    // Complete input fingerprint: identical replay matches; any change to the frozen content or its place conflicts.
    private static string Fingerprint(Guid session, Guid drill, int step, LearningDomain topic, GeneratedQuestionText text, string[] followUps) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, session, drill, step, topic, text.OutlineKey, text.Title, text.Prompt, text.Reference, FollowUps = followUps
        })));
}
