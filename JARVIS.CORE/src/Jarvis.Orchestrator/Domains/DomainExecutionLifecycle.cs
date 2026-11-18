using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Domains;

public static class DomainExecutionLifecycle
{
    public static HashSet<string> Enabled(DomainRegistryState state) => state.Definitions.Values.Where(d =>
        DomainRegistryProjection.Active(state, d.Key) is not null).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);

    public static void DisableRuns(RuntimeState state, IReadOnlySet<string> keys, DateTimeOffset now)
    {
        foreach (var practice in state.VoiceCoachings.Values.Where(v => v.ScenarioKey is { } key && keys.Contains(key) && v.Status is "Question" or "Correcting").ToArray())
            state.VoiceCoachings[practice.Id] = practice with { Status = "Cancelled", UpdatedAt = now };
        foreach (var run in state.AgentRuns.Values.Where(r => r.DeletedAt is null && r.WorkflowPolicy?.DomainKey is { } key && keys.Contains(key) &&
            r.Status is AgentRunStatus.Queued or AgentRunStatus.Running or AgentRunStatus.AwaitingSetup).ToArray()) {
            AgentToolLifecycle.Retire(state, run.Id, now, false);
            state.AgentRuns[run.Id] = run with { Status = AgentRunStatus.Cancelled };
            foreach (var id in run.TaskIds)
                if (state.AgentTasks[id].Status is AgentTaskStatus.Queued or AgentTaskStatus.Running or AgentTaskStatus.RetryScheduled or AgentTaskStatus.AwaitingApproval or AgentTaskStatus.AwaitingReceipt)
                    state.AgentTasks[id] = state.AgentTasks[id] with { Status = AgentTaskStatus.Cancelled, ClaimToken = null, LeaseExpiresAt = null, CompletedAt = now };
            foreach (var notice in state.AgentOutbox.Values.Where(n => n.RunId == run.Id).ToArray()) state.AgentOutbox.Remove(notice.Id);
        }
    }
}
