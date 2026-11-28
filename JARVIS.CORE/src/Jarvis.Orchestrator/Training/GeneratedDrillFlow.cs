using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

// The current capture/authority a pending need is bound to. A newer utterance, privacy epoch or prepared route makes it stale.
public sealed record PendingBinding(Guid TriggerUtteranceId, long PrivacyEpoch, string RouteRevision);
public sealed record OutlineEntry(string Key, int Version);
// A generated question as accepted by the writer: ID from GeneratedQuestionIds, text already bounded by GeneratedQuestionUnit.
public sealed record GeneratedQuestionContent(Guid QuestionId, int Version, string Title, string Prompt, string Reference, IReadOnlyList<string> FollowUps);

// Pure state transitions for generated drills inside the existing canonical drill (design R3-2, review R4 item 2). No storage,
// tool or caller yet: the store port and generation tool apply these inside the canonical transaction later.
public static class GeneratedDrillFlow
{
    public const string EndQuota = "quota", EndOutlineExhausted = "outline_exhausted", EndCancelled = "cancelled";
    // Must equal the writer bound (GeneratedQuestionUnit.MaximumFollowUps, pending merge); unify once both are in development.
    public const int MaximumFollowUps = 2;
    public static bool SupportsMode(TrainingMode mode) => mode is TrainingMode.RapidDrill or TrainingMode.DeepDive;

    // Authored content exhausted: an Active drill with no steps yet and a pending need for step 0.
    public static TrainingDrill Start(Guid id, Guid sessionId, TrainingMode mode, LearningDomain topic, OutlineEntry outline,
        PendingBinding binding, DateTimeOffset at)
    {
        Guard.Require(id != Guid.Empty && sessionId != Guid.Empty, 400, "A generated drill needs stable identities.");
        Guard.Require(SupportsMode(mode), 400, "Generated questions support rapid drill and deep dive only.");
        Guard.Require(TrainingDrillService.Topics.Contains(topic), 400, "There is no practice outline for that topic.");
        ValidOutline(outline); ValidBinding(binding);
        return new(id, sessionId, mode, null, 1, "Active", at, [], [], topic,
            Generated: new(topic, new(1, 0, outline.Key, outline.Version, binding.TriggerUtteranceId, binding.PrivacyEpoch, binding.RouteRevision), 0));
    }

    // Pending-safe view: the drill is waiting for its next question rather than an answer, so there is no step to index.
    public static bool AwaitingQuestion(TrainingDrill drill) =>
        drill is { Status: "Active", Generated.Pending: not null } && drill.AttemptIds.Count == drill.Steps.Count;

    // A generator response bound to `answered` becomes the next question group. Anything stale (another revision, step,
    // outline, utterance, epoch or route), a second generator, or a stopped/finished drill is refused and changes nothing.
    public static TrainingDrill ApplyQuestion(TrainingDrill drill, PendingNeed answered, GeneratedQuestionContent question)
    {
        ArgumentNullException.ThrowIfNull(answered); ArgumentNullException.ThrowIfNull(question);
        Guard.Require(drill.Status == "Active" && drill.Generated is not null, 409, "This practice is no longer waiting for a question.");
        Guard.Require(AwaitingQuestion(drill) && drill.Generated.Pending == answered && answered.DrillRevision == drill.Revision
            && answered.StepIndex == drill.Steps.Count, 409, "This practice question is out of date.");
        Guard.Require(question.QuestionId != Guid.Empty && question.Version > 0 && question.FollowUps is { Count: <= MaximumFollowUps },
            400, "Generated question content is invalid.");
        var start = drill.Steps.Count;
        var added = new List<TrainingDrillStep>
        {
            new(start, question.QuestionId, question.Version, question.Title, question.Prompt, question.Reference, "", GeneratedQuestionId: question.QuestionId)
        };
        // Deep dive mirrors the authored shape: the question, then each follow-up as its own stage of the same question group.
        if (drill.Mode == TrainingMode.DeepDive)
            foreach (var followUp in question.FollowUps)
                added.Add(new(start + added.Count, question.QuestionId, question.Version, "Follow-up " + added.Count,
                    question.Prompt + "\nNow focus on: " + followUp, question.Reference, "", GeneratedQuestionId: question.QuestionId));
        return drill with
        {
            Steps = [.. drill.Steps, .. added], Revision = checked(drill.Revision + 1),
            Generated = drill.Generated with { Pending = null, QuestionsPrepared = drill.Generated.QuestionsPrepared + 1 }
        };
    }

    // After a canonical answer is saved. Authored drills are untouched. While stages of the current question remain, nothing
    // changes. When the whole group is answered the drill stays Active with a new need, or ends with a recorded reason.
    public static TrainingDrill AfterAnswer(TrainingDrill drill, OutlineEntry? next, PendingBinding binding, bool quotaAvailable)
    {
        if (drill.Generated is null) return drill;
        Guard.Require(drill.Status == "Active", 409, "This practice is no longer active.");
        if (drill.AttemptIds.Count < drill.Steps.Count) return drill;
        var revision = checked(drill.Revision + 1);
        if (!quotaAvailable || next is null)
            return drill with { Status = "Completed", Revision = revision,
                Generated = drill.Generated with { Pending = null, EndReason = quotaAvailable ? EndOutlineExhausted : EndQuota } };
        ValidOutline(next); ValidBinding(binding);
        return drill with { Revision = revision, Generated = drill.Generated with
            { Pending = new(revision, drill.Steps.Count, next.Key, next.Version, binding.TriggerUtteranceId, binding.PrivacyEpoch, binding.RouteRevision) } };
    }

    // A newer utterance, renewal, route change or handoff refreshes the same logical need under a new binding and revision,
    // so the user is never stuck "awaiting question" and any response bound to the old need is refused.
    public static TrainingDrill RefreshBinding(TrainingDrill drill, PendingBinding binding)
    {
        Guard.Require(AwaitingQuestion(drill), 409, "This practice is not waiting for a question.");
        ValidBinding(binding);
        var pending = drill.Generated!.Pending!;
        if (pending.TriggerUtteranceId == binding.TriggerUtteranceId && pending.PrivacyEpoch == binding.PrivacyEpoch &&
            pending.RouteRevision == binding.RouteRevision) return drill;
        var revision = checked(drill.Revision + 1);
        return drill with { Revision = revision, Generated = drill.Generated with { Pending = pending with
            { DrillRevision = revision, TriggerUtteranceId = binding.TriggerUtteranceId, PrivacyEpoch = binding.PrivacyEpoch, RouteRevision = binding.RouteRevision } } };
    }

    // Stop clears the pending need; a later generator response can never reopen or advance the drill.
    public static TrainingDrill Cancel(TrainingDrill drill)
    {
        if (drill.Status == "Cancelled") return drill;
        Guard.Require(drill.Status == "Active", 409, "Drill changed or is already finished.");
        return drill with { Status = "Cancelled", Revision = checked(drill.Revision + 1),
            Generated = drill.Generated is null ? null : drill.Generated with { Pending = null, EndReason = EndCancelled } };
    }

    private static void ValidOutline(OutlineEntry outline) =>
        Guard.Require(outline is { Key.Length: > 0 and <= 64, Version: > 0 }, 400, "A valid outline entry is required.");
    private static void ValidBinding(PendingBinding binding) =>
        Guard.Require(binding is { TriggerUtteranceId: var u, RouteRevision.Length: > 0 and <= 128 } && u != Guid.Empty && binding.PrivacyEpoch >= 0,
            400, "A current capture binding is required.");
}
