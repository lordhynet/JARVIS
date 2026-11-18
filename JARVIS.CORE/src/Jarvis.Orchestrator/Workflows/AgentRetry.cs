using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

public sealed partial class AgentRuntime
{
    public Task<AgentRunView> ReleaseSetupAsync(Guid id, BudgetActor owner, CancellationToken ct) => store.UpdateAsync(s => {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient && owner.Scopes.Contains(AgentToolGateway.ManageScope), 403, "Manual owner authorization is required.");
        Guard.Require(s.AgentRuns.TryGetValue(id, out var run) && run.DeletedAt is null && run.Deadline > clock.GetUtcNow() && run.Status == AgentRunStatus.AwaitingSetup,
            409, "Run is no longer waiting for setup.");
        Guard.Require(!s.AgentControls.EffectiveAgentsPaused && s.AgentControls.PausedWorkflows?.Contains(run.Workflow) != true, 409, "Agents are paused.");
        s.AgentRuns[id] = run with { Status = AgentRunStatus.Queued, Deadline = clock.GetUtcNow().AddMinutes(5) };
        return View(s, s.AgentRuns[id]);
    }, ct);

    public async Task<AgentRunView> RetryAsync(Guid id, RetryAgentRunRequest request, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient && request.RequestId != Guid.Empty && request.RequestId != id,
            403, "An explicit manual retry with a new request ID is required.");
        var workflow = await store.ReadAsync(s => s.AgentRuns.TryGetValue(id, out var run) && run.DeletedAt is null ? run.Workflow : throw new DomainException(404, "Run not found."), ct);
        var descriptor = await workflowCatalog.ResolveAsync(workflow, ct);
        return await store.UpdateAsync(s => {
            var prior = s.AgentRuns[id];
            Guard.Require(prior.DeletedAt is null && prior.Status is AgentRunStatus.Failed or AgentRunStatus.Expired or AgentRunStatus.NeedsReconciliation,
                409, "Only failed or expired work can be retried.");
            var tasks = prior.TaskIds.Select(t => s.AgentTasks[t]).ToArray();
            Guard.Require(!tasks.Any(t => s.UsageReservations.Values.Any(r => r.RunId == t.Id && (r.ActualUsd is null || r.ReconciliationRequired))) &&
                !s.AgentActions.Values.Any(p => p.RunId == id), 409, "Reconcile spending or review external actions before retrying. External actions are never replayed.");
            var notBefore = tasks.Max(t => t.AvailableAt);
            if (notBefore > clock.GetUtcNow()) throw new DomainException(429, "Provider cooldown is still active.") { RetryAfter = notBefore - clock.GetUtcNow() };
            var occurrence = "retry:" + id;
            if (s.AgentRuns.TryGetValue(request.RequestId, out var existing)) {
                Guard.Require(existing.Occurrence == occurrence && existing.DeletedAt is null, 409, "Retry ID was reused for another run."); return View(s, existing);
            }
            RequireConsent(descriptor, prior.AllowCloudProcessing);
            return View(s, Create(s, request.RequestId, null, occurrence, prior.Workflow, prior.AllowCloudProcessing,
                tasks.First(t => !s.AgentActions.ContainsKey(t.Id)).Input, clock.GetUtcNow(), descriptor));
        }, ct);
    }

    public Task<bool> RecordRetryAfterAsync(Guid taskId, TimeSpan delay, CancellationToken ct) => store.UpdateAsync(s => {
        if (!s.AgentTasks.TryGetValue(taskId, out var task) || task.Status != AgentTaskStatus.Running) return false;
        s.AgentTasks[taskId] = task with { AvailableAt = clock.GetUtcNow().AddSeconds(Math.Clamp(delay.TotalSeconds, 1, 86400)) }; return true;
    }, ct);
}
