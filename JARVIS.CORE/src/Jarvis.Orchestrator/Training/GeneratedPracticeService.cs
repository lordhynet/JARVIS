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

    // Null means "not applicable": generation off, unsupported style, or authored questions not yet exhausted. The caller
    // then keeps its existing authored behaviour unchanged.
    public Task<TrainingDrill?> TryStartAsync(Guid requestId, TrainingMode mode, LearningDomain topic, GenerationContext context, CancellationToken ct = default)
    {
        if (!Enabled || !GeneratedDrillFlow.SupportsMode(mode)) return Task.FromResult<TrainingDrill?>(null);
        return store.UpdateWithGeneratedQuestionsAsync(new(context.SessionId, [], [], [context.SessionId]), (s, unit) =>
        {
            if (s.TrainingDrills.TryGetValue(requestId, out var replay)) return replay;
            Guard.Require(!s.RetiredSessions.Contains(context.SessionId), 409, "This session was retired by retention.");
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
