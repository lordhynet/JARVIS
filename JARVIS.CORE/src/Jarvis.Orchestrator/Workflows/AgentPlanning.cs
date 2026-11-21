using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

public sealed partial class AgentRuntime
{
    public async Task<PersonalGoal> SaveGoalAsync(SaveGoalRequest request, CancellationToken ct = default)
    {
        var title = Guard.Text(request.Title, "Goal title", 120);
        var brief = Guard.Text(request.Brief, "Goal brief", 2000);
        Guard.Require(request.RequestId != Guid.Empty, 400, "A goal ID and supported workflow are required.");
        Guard.Require(request.Status is GoalStatus.Active or GoalStatus.Paused or GoalStatus.Completed or GoalStatus.Cancelled, 400, "Invalid goal state.");
        Guard.Require(request.Deadline > request.NextReviewAt && request.Deadline <= clock.GetUtcNow().AddYears(5), 400, "Review must precede the goal deadline (within five years).");
        // A division tag is inert planning metadata: it must name a code-owned Forge division, but grants no
        // workflow, capability or consent and does not require Forge to be enabled.
        var division = request.DivisionKey is null ? null : Jarvis.Orchestrator.Forge.ForgeDivisions.RequireKey(request.DivisionKey);
        var descriptor = await workflowCatalog.ResolveAsync(request.Workflow, ct);
        RequireConsent(descriptor, request.AllowCloudProcessing);
        return await store.UpdateAsync(s =>
        {
            var now = clock.GetUtcNow(); var prior = s.Goals.GetValueOrDefault(request.RequestId);
            Guard.Require(prior?.Status != GoalStatus.Deleted, 409, "Deleted goals cannot be recreated.");
            if (prior is not null && request.ExpectedRevision is null)
            {
                Guard.Require(prior.Title == title && prior.Brief == brief && prior.Workflow == request.Workflow && prior.AllowCloudProcessing == request.AllowCloudProcessing &&
                    prior.NextReviewAt == request.NextReviewAt && prior.Deadline == request.Deadline && prior.Status == request.Status &&
                    prior.DivisionKey == division, 409, "Goal ID was reused. Reload before editing.");
                return prior;
            }
            Guard.Require(prior is null ? request.ExpectedRevision is null : request.ExpectedRevision == prior.Revision, 409, "Goal changed. Reload before editing.");
            Guard.Require(prior is not null || s.Goals.Count < 200, 409, "Goal capacity reached.");
            if (prior?.LastRunId is Guid runId) Supersede(s, runId, "goal_changed");
            var goal = new PersonalGoal(request.RequestId, (prior?.Revision ?? 0) + 1, title, brief, request.Workflow,
                request.AllowCloudProcessing, request.NextReviewAt.ToUniversalTime(), request.Deadline.ToUniversalTime(), request.Status, prior?.CreatedAt ?? now, now, DivisionKey: division);
            s.Goals[goal.Id] = goal; return goal;
        }, ct);
    }

    public Task<bool> DeleteGoalAsync(Guid id, int revision, CancellationToken ct = default) => store.UpdateAsync(s =>
    {
        Guard.Require(s.Goals.TryGetValue(id, out var goal), 404, "Goal not found.");
        Guard.Require(goal.Revision == revision, 409, "Goal changed. Reload before deleting.");
        if (goal.LastRunId is Guid runId) Supersede(s, runId, "goal_deleted");
        // Retain a content-free tombstone so stale clients cannot recreate the goal.
        s.Goals[id] = goal with { Title = "", Brief = "", DivisionKey = null, Status = GoalStatus.Deleted, Revision = goal.Revision + 1, UpdatedAt = clock.GetUtcNow() };
        return true;
    }, ct);

    public async Task<DurableAgentTrigger> IngestTriggerAsync(AgentTriggerRequest request, CancellationToken ct = default)
    {
        var source = Guard.Text(request.Source, "Trigger source", 80); var occurrence = Guard.Text(request.OccurrenceId, "Occurrence ID", 200);
        var correlation = Guard.Text(request.CorrelationId, "Correlation ID", 200);
        Guard.Require(request.Revision > 0, 400, "Positive source revision and supported workflow required.");
        Guard.Require(request.ExpiresAt > request.DueAt && request.ExpiresAt - request.DueAt <= TimeSpan.FromDays(31), 400, "Trigger validity must be positive and at most 31 days.");
        var brief = request.Cancelled ? "" : Guard.Text(request.Brief, "Trigger brief", 2000);
        var descriptor = await workflowCatalog.ResolveAsync(request.Workflow, ct);
        RequireConsent(descriptor, request.AllowCloudProcessing);
        var normalized = request with { Source = source, OccurrenceId = occurrence, CorrelationId = correlation, Brief = brief,
            DueAt = request.DueAt.ToUniversalTime(), ExpiresAt = request.ExpiresAt.ToUniversalTime() };
        var fingerprint = PrivacyData.Hash(JsonSerializer.Serialize(normalized)); var id = StableId("trigger-v1:" + source + ":" + occurrence);
        return await store.UpdateAsync(s =>
        {
            var prior = s.AgentTriggers.GetValueOrDefault(id);
            if (prior is not null)
            {
                Guard.Require(request.Revision >= prior.Revision, 409, "Obsolete trigger revision.");
                if (request.Revision == prior.Revision) { Guard.Require(prior.Fingerprint == fingerprint, 409, "Occurrence revision was reused for different input."); return prior; }
                if (prior.RunId is Guid runId) Supersede(s, runId, "event_superseded");
            }
            Guard.Require(prior is not null || s.AgentTriggers.Count < 5000, 409, "Trigger capacity reached.");
            var trigger = new DurableAgentTrigger(id, source, occurrence, request.Revision, request.Workflow,
                normalized.DueAt, normalized.ExpiresAt, brief, request.AllowCloudProcessing, correlation, fingerprint, request.Cancelled ? AgentTriggerStatus.Cancelled : AgentTriggerStatus.Pending);
            s.AgentTriggers[id] = trigger; return trigger;
        }, ct);
    }

    private int TickPlanning(RuntimeState s, DateTimeOffset now, IReadOnlyDictionary<string, WorkflowDescriptor> available)
    {
        var count = 0;
        foreach (var goal in s.Goals.Values.Where(x => x.Status == GoalStatus.Active).ToList())
        {
            if (goal.Deadline <= now) { if (goal.LastRunId is Guid expiredRun) Supersede(s, expiredRun, "goal_expired"); s.Goals[goal.Id] = goal with { Status = GoalStatus.Expired, Revision = goal.Revision + 1, UpdatedAt = now }; continue; }
            if (!available.ContainsKey(goal.Workflow) || available[goal.Workflow].RequiresCloud && !goal.AllowCloudProcessing || s.AgentControls.EffectiveAgentsPaused || s.AgentControls.PausedWorkflows?.Contains(goal.Workflow) == true || goal.NextReviewAt > now || goal.LastRunId is not null) continue;
            var id = StableId($"goal-v1:{goal.Id}:{goal.Revision}");
            Create(s, id, null, $"goal:{goal.Id}:{goal.Revision}", goal.Workflow, goal.AllowCloudProcessing, goal.Title + "\n" + goal.Brief, now, available[goal.Workflow]);
            s.AgentRuns[id] = s.AgentRuns[id] with { Deadline = s.AgentRuns[id].Deadline < goal.Deadline ? s.AgentRuns[id].Deadline : goal.Deadline };
            s.Goals[goal.Id] = goal with { LastRunId = id, UpdatedAt = now }; count++;
        }
        foreach (var trigger in s.AgentTriggers.Values.Where(x => x.Status is AgentTriggerStatus.Pending or AgentTriggerStatus.Dispatched).OrderBy(x => x.DueAt).ToList())
        {
            if (trigger.ExpiresAt <= now) { if (trigger.RunId is Guid expiredRun) Supersede(s, expiredRun, "event_expired"); s.AgentTriggers[trigger.Id] = trigger with { Status = AgentTriggerStatus.Expired, Brief = "" }; continue; }
            if (!available.ContainsKey(trigger.Workflow) || available[trigger.Workflow].RequiresCloud && !trigger.AllowCloudProcessing || trigger.Status != AgentTriggerStatus.Pending || s.AgentControls.EffectiveAgentsPaused || s.AgentControls.PausedWorkflows?.Contains(trigger.Workflow) == true || trigger.DueAt > now) continue;
            var id = StableId($"trigger-run-v1:{trigger.Id}:{trigger.Revision}");
            Create(s, id, null, $"event:{trigger.Id}:{trigger.Revision}", trigger.Workflow, trigger.AllowCloudProcessing, trigger.Brief, now, available[trigger.Workflow]);
            s.AgentRuns[id] = s.AgentRuns[id] with { Deadline = s.AgentRuns[id].Deadline < trigger.ExpiresAt ? s.AgentRuns[id].Deadline : trigger.ExpiresAt };
            CoreTelemetry.ScheduleDelay.Record(Math.Max(0, (now - trigger.DueAt).TotalSeconds));
            s.AgentTriggers[trigger.Id] = trigger with { Status = AgentTriggerStatus.Dispatched, RunId = id }; count++;
        }
        return count;
    }

    private void Supersede(RuntimeState s, Guid runId, string reason)
    {
        if (!s.AgentRuns.TryGetValue(runId, out var run)) return;
        AgentToolLifecycle.Retire(s, runId, clock.GetUtcNow(), false);
        s.AgentRuns[runId] = run with { Status = AgentRunStatus.Cancelled };
        foreach (var id in run.TaskIds)
        {
            var task = s.AgentTasks[id]; s.AgentTasks[id] = task with { Status = AgentTaskStatus.Cancelled, ClaimToken = null, LeaseExpiresAt = null, Input = "", Output = null, ErrorCode = reason };
            foreach (var cache in s.ProviderResults.Values.Where(x => x.ScopeId == id).ToList()) s.ProviderResults.Remove(cache.Id);
        }
        foreach (var note in s.AgentOutbox.Values.Where(x => x.RunId == runId).ToList()) s.AgentOutbox.Remove(note.Id);
    }
}
