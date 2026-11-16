using System.Globalization;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Workflows;

/// <summary>Shared read-only work predicates used before and inside runtime write transactions.</summary>
public static class AgentEligibility
{
    public static bool HasTickWork(RuntimeState s, DateTimeOffset now, IReadOnlyDictionary<string, WorkflowDescriptor> available)
    {
        if (s.AgentRuns.Values.Any(r => r.DeletedAt is null && r.Status is AgentRunStatus.Queued or AgentRunStatus.Running)) return true;
        if (s.Goals.Values.Any(g => g.Status == GoalStatus.Active && (g.Deadline <= now ||
            !s.AgentControls.EffectiveAgentsPaused && g.LastRunId is null && g.NextReviewAt <= now &&
            available.TryGetValue(g.Workflow, out var goalWorkflow) && (!goalWorkflow.RequiresCloud || g.AllowCloudProcessing) &&
            s.AgentControls.PausedWorkflows?.Contains(g.Workflow) != true))) return true;
        if (s.AgentTriggers.Values.Any(t => (t.Status is AgentTriggerStatus.Pending or AgentTriggerStatus.Dispatched) &&
            (t.ExpiresAt <= now || !s.AgentControls.EffectiveAgentsPaused && t.Status == AgentTriggerStatus.Pending && t.DueAt <= now &&
             available.TryGetValue(t.Workflow, out var triggerWorkflow) && (!triggerWorkflow.RequiresCloud || t.AllowCloudProcessing) &&
             s.AgentControls.PausedWorkflows?.Contains(t.Workflow) != true))) return true;
        if (!s.AgentControls.EffectiveAgentsPaused)
        {
            if (!AgentRuntime.IsQuiet(s.AgentControls, now) && s.AgentOutbox.Values.Any(n => !n.Delivered && n.DeliverAfter <= now && n.ExpiresAt > now)) return true;
            foreach (var schedule in s.AgentSchedules.Values)
            {
                if (!schedule.Enabled || !available.ContainsKey(schedule.Workflow) || s.AgentControls.PausedWorkflows?.Contains(schedule.Workflow) == true) continue;
                if (available[schedule.Workflow].RequiresCloud && !schedule.AllowCloudProcessing) continue;
                var zone = TrainingProjection.Zone(schedule.TimeZone); var local = TimeZoneInfo.ConvertTime(now, zone);
                var at = local.Date + AgentRuntime.LocalTime(schedule.LocalTime).ToTimeSpan(); if (zone.IsInvalidTime(at)) at = at.AddHours(1);
                var offset = zone.IsAmbiguousTime(at) ? zone.GetAmbiguousTimeOffsets(at).Max() : zone.GetUtcOffset(at);
                var delta = now - new DateTimeOffset(at, offset).ToUniversalTime();
                if (delta >= TimeSpan.Zero && delta <= TimeSpan.FromMinutes(30) &&
                    schedule.LastOccurrence != local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ":" + schedule.LocalTime) return true;
            }
        }
        return false;
    }

    public static bool HasClaimWork(RuntimeState s, DateTimeOffset now)
    {
        if (s.AgentControls.EffectiveAgentsPaused) return false;
        if (s.AgentTasks.Values.Any(t => t.Status == AgentTaskStatus.Running && t.LeaseExpiresAt <= now)) return true;
        if (s.AgentTasks.Values.Count(x => x.Status == AgentTaskStatus.Running && x.LeaseExpiresAt > now) >= 2) return false;
        return s.AgentTasks.Values.Any(t => (t.Status is AgentTaskStatus.Queued or AgentTaskStatus.RetryScheduled) && t.AvailableAt <= now &&
            s.AgentRuns.TryGetValue(t.RunId, out var run) && run.DeletedAt is null && run.Status is AgentRunStatus.Queued or AgentRunStatus.Running && run.Deadline > now &&
            s.AgentControls.PausedWorkflows?.Contains(run.Workflow) != true &&
            (t.Role != AgentRole.Review || run.TaskIds.Where(id => id != t.Id).All(id => s.AgentTasks[id].Status == AgentTaskStatus.Succeeded)));
    }
}
