using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

// A proposal is a child task, so its approval/receipt cannot disappear when the planning claim completes.
public sealed class AgentActionService(IRuntimeStore store, AgentToolGateway gateway, IDeviceGrantStore devices,
    IDeviceCapabilityCatalog capabilities, AgentSpecialistCatalog specialists, IDeviceCommandReader commands,
    IDeviceReceiptReader receipts, TimeProvider clock, AgentRuntime runtime)
{
    public async Task<object[]> OptionsAsync(AgentTask task, CancellationToken ct)
    {
        var grants = await store.ReadAsync(s => s.AgentToolGrants.Values.Where(g => g.RunId == task.RunId && g.RevokedAt is null &&
            g.ExpiresAt > clock.GetUtcNow() && g.GrantedBy != AgentToolScopeKinds.InstalledPolicy &&
            specialists.Get(task.Role).AllowedCapabilities.Contains(g.CapabilityId)).ToArray(), ct);
        if (!await store.ReadAsync(s => s.AgentRuns[task.RunId].Workflow is "personal-plan" or "career-plan", ct)) return [];
        var deviceGrants = await devices.ListAsync(null, ct);
        return grants.SelectMany(g => deviceGrants.Where(d => d.RevokedAt is null && d.Audit is not null &&
            d.ExpiresAt > clock.GetUtcNow() && d.CapabilityId == g.CapabilityId && d.CapabilityVersion == g.CapabilityVersion)
            .Select(d => (object)new { grantId = g.Id, deviceGrantId = d.Id, capabilityId = g.CapabilityId,
                schema = capabilities.Get(d.CapabilityId, d.CapabilityVersion).ArgumentSchemaJson, constraints = d.ConstraintsJson })).Take(20).ToArray();
    }

    public async Task<AgentActionView> ProposeAsync(AgentTask parent, int index, Guid grantId, Guid deviceGrantId, string arguments, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Agent && index is >= 0 and < 3, 403, "Only a live specialist may propose a bounded action.");
        var device = await devices.GetAsync(deviceGrantId, ct);
        Guard.Require(device is not null && device.Audit is not null && device.RevokedAt is null && device.ExpiresAt > clock.GetUtcNow() &&
            capabilities.Permits(device.CapabilityId, device.CapabilityVersion, device.ConstraintsJson, arguments), 403, "No device grant permits the proposed arguments.");
        var id = AgentRuntime.StableId("agent-action-v1:" + parent.Id + ":" + index);
        await store.UpdateAsync(s => {
            Guard.Require(s.AgentTasks.TryGetValue(parent.Id, out var live) && live.ClaimToken == parent.ClaimToken && live.Status == AgentTaskStatus.Running &&
                live.LeaseExpiresAt > clock.GetUtcNow() && !s.AgentActions.ContainsKey(parent.Id), 409, "Planning claim changed.");
            var run = s.AgentRuns[live.RunId];
            Guard.Require(run.Workflow is "personal-plan" or "career-plan", 403, "Only installed personal and career planning workflows may propose device actions.");
            Guard.Require(run.DeletedAt is null && run.Status == AgentRunStatus.Running && run.Deadline > clock.GetUtcNow() &&
                s.AgentToolGrants.TryGetValue(grantId, out var grant) && grant.RunId == run.Id && grant.CapabilityId == device.CapabilityId &&
                grant.CapabilityVersion == device.CapabilityVersion && grant.GrantedBy != AgentToolScopeKinds.InstalledPolicy, 403, "A separate scoped owner grant is required.");
            var expires = run.Deadline < device.ExpiresAt ? run.Deadline : device.ExpiresAt;
            var proposal = new AgentActionProposal(id, run.Id, parent.Id, grantId, device.Id, device.DeviceId, device.CapabilityId, device.CapabilityVersion, arguments, expires,
                !device.PreApproved || capabilities.Get(device.CapabilityId, device.CapabilityVersion).Risk != DeviceCapabilityRisk.Reversible);
            if (s.AgentActions.TryGetValue(id, out var prior)) { Guard.Require(prior == proposal, 409, "Action ID was reused with different parameters."); return true; }
            Guard.Require(s.AgentActions.Count < 10000 && specialists.Get(parent.Role).AllowedCapabilities.Contains(device.CapabilityId), 403, "Specialist cannot propose this action.");
            s.AgentActions.Add(id, proposal);
            s.AgentTasks.Add(id, new(id, run.Id, parent.Id, parent.Role, 1, "Governed action proposal", AgentTaskStatus.Running, 1,
                clock.GetUtcNow(), expires, Guid.NewGuid(), SourceRefs: live.SourceRefs));
            s.AgentRuns[run.Id] = run with { TaskIds = [..run.TaskIds, id] }; return true;
        }, ct);
        await DispatchAsync(id, ct);
        return (await ListAsync(ct)).Single(x => x.Proposal.Id == id);
    }

    public Task<AgentActionView[]> ListAsync(CancellationToken ct = default) => store.ReadAsync(s => s.AgentActions.Values
        .Where(p => s.AgentRuns.TryGetValue(p.RunId, out var r) && r.DeletedAt is null)
        .OrderBy(p => p.Id).Take(100).Select(p => {
            var invocation = s.AgentToolInvocations.GetValueOrDefault(p.Id);
            return new AgentActionView(p, s.AgentTasks[p.Id].Status.ToString(), invocation?.ArgumentsHash ?? "",
                capabilities.Get(p.CapabilityId, p.CapabilityVersion).Risk.ToString(), invocation?.ApprovedBy);
        }).ToArray(), ct);

    public async Task ApproveAsync(Guid id, string hash, BudgetActor owner, CancellationToken ct)
    {
        await gateway.ConfirmAsync(id, hash, owner, ct);
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        await DispatchAsync(id, ct);
    }

    private async Task DispatchAsync(Guid id, CancellationToken ct)
    {
        var (proposal, task) = await store.ReadAsync(s => (s.AgentActions[id], s.AgentTasks[id]), ct);
        if (task.Status is AgentTaskStatus.AwaitingReceipt or AgentTaskStatus.Succeeded) return;
        try {
            await gateway.InvokeAsync(new(id, id, task.ClaimToken!.Value, proposal.GrantId, proposal.CapabilityId,
                proposal.CapabilityVersion, proposal.ArgumentsJson, ReasoningDataClass.Personal), ct);
            await store.UpdateAsync(s => {
                if (s.AgentTasks.TryGetValue(id, out var live) && live.ClaimToken == task.ClaimToken && live.Status is AgentTaskStatus.Running or AgentTaskStatus.AwaitingApproval)
                    s.AgentTasks[id] = live with { Status = AgentTaskStatus.AwaitingReceipt }; return true;
            }, ct);
        }
        catch (DomainException) {
            if (!await store.ReadAsync(s => s.AgentToolInvocations.GetValueOrDefault(id)?.Status == AgentToolStatus.AwaitingApproval, ct)) throw;
            await store.UpdateAsync(s => { if (s.AgentTasks.TryGetValue(id, out var live) && live.Status == AgentTaskStatus.Running)
                s.AgentTasks[id] = live with { Status = AgentTaskStatus.AwaitingApproval }; return true; }, ct);
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var pending = await store.ReadAsync(s => s.AgentActions.Values.Where(p => s.AgentTasks.GetValueOrDefault(p.Id)?.Status == AgentTaskStatus.AwaitingReceipt).Take(100).ToArray(), ct);
        foreach (var proposal in pending) {
            var command = await commands.GetAsync(proposal.Id, ct);
            var recorded = await receipts.ListAsync(proposal.Id, ct);
            var success = command?.Status == DeviceCommandStatus.Succeeded && recorded.Any(r => r.Kind == DeviceReceiptKind.Succeeded && !r.Late);
            if (!success && command?.Status is not (DeviceCommandStatus.Failed or DeviceCommandStatus.Cancelled or DeviceCommandStatus.Expired)) continue;
            await store.UpdateAsync(s => {
                if (!s.AgentTasks.TryGetValue(proposal.Id, out var task) || task.Status != AgentTaskStatus.AwaitingReceipt ||
                    s.AgentRuns[task.RunId].Status != AgentRunStatus.Running || s.AgentRuns[task.RunId].DeletedAt is not null) return false;
                s.AgentTasks[task.Id] = task with { Status = success ? AgentTaskStatus.Succeeded : AgentTaskStatus.Failed, ClaimToken = null,
                    CompletedAt = clock.GetUtcNow(), Output = success ? JsonSerializer.Serialize(new { commandId = proposal.Id, verifiedReceipt = true }) : null,
                    ErrorCode = success ? null : "action_not_verified" }; return true;
            }, ct);
        }
    }

    public async Task<object> VoiceAsync(string action, Guid session, Guid source, Func<CancellationToken, Task<bool>>? audio, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Voice && audio is not null && await audio(ct), 403, "A live audio window is required.");
        if (action == "list") return await store.UpdateAsync<object>(s => {
            Guard.Require(!VoiceAccessRules.ToolRestricted(s, session) && s.AudioLease!.ExpiresAt > clock.GetUtcNow() && !s.RetiredSessions.Contains(session), 403, "Agent status requires current unlocked audio.");
            foreach (var id in s.AgentVoiceReviews.Where(p => p.Value.At < clock.GetUtcNow().AddMinutes(-2) || s.RetiredSessions.Contains(p.Key)).Select(p => p.Key).ToArray()) s.AgentVoiceReviews.Remove(id);
            var pending = s.AgentActions.Values.Where(p => s.AgentTasks[p.Id].Status == AgentTaskStatus.AwaitingApproval && p.ExpiresAt > clock.GetUtcNow() && p.ArgumentsJson.Length <= 1000).OrderBy(p => p.Id).Take(3).ToArray();
            var runs = s.AgentRuns.Values.Where(r => r.DeletedAt is null && r.Status is AgentRunStatus.Running or AgentRunStatus.Queued).OrderBy(r => r.Id).Take(10).ToArray();
            s.AgentVoiceReviews[session] = new(pending.Select(p => p.Id).ToArray(), runs.Select(r => r.Id).ToArray(), clock.GetUtcNow());
            return new { actions = pending.Select((p, i) => new { number = i + 1, p.CapabilityId, p.ArgumentsJson,
                risk = capabilities.Get(p.CapabilityId, p.CapabilityVersion).Risk.ToString() }), runs = runs.Select((r, i) => new { number = i + 1, r.Workflow, r.Status }) };
        }, ct);
        var selection = await store.ReadAsync(s => {
            var turn = s.VoiceTurns.GetValueOrDefault(source); var review = s.AgentVoiceReviews.GetValueOrDefault(session);
            Guard.Require(!VoiceAccessRules.ToolRestricted(s, session) && s.AudioLease!.ExpiresAt > clock.GetUtcNow() && !s.RetiredSessions.Contains(session) && turn is not null && turn.SessionId == session &&
                turn.Role == ChatRoles.User && !turn.Restricted && turn.At >= clock.GetUtcNow().AddSeconds(-90) &&
                review is not null && review.At >= clock.GetUtcNow().AddMinutes(-2), 403, "List the current runs first and use a separate captured request.");
            var text = turn.Text.Trim().TrimEnd('.', '!', '?').ToLowerInvariant();
            var match = Regex.Match(text, action == "approve" ? @"^(?:please )?approve (?:action |the )?(first|second|third|[1-9])(?: one)?$" : @"^(?:please )?cancel (?:run |the )?(first|second|third|[1-9])(?: one)?$");
            var ids = action == "approve" ? review.Actions : review.Runs;
            Guid target;
            if (match.Success) {
                var number = match.Groups[1].Value switch { "first" => 1, "second" => 2, "third" => 3, var n => int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) };
                Guard.Require(number <= ids.Count, 409, "That numbered entry is not in the last spoken list."); target = ids[number - 1];
            } else {
                Guard.Require(action == "cancel" && text is "cancel the research run" or "cancel research run", 400, "Use an explicit numbered approval or cancellation from the last list.");
                var research = ids.Where(id => s.AgentRuns[id].Workflow.Contains("research", StringComparison.OrdinalIgnoreCase)).ToArray();
                Guard.Require(research.Length == 1, 409, "The research run is ambiguous. Use its number."); target = research[0];
            }
            return (Id: target, Hash: s.AgentToolInvocations.GetValueOrDefault(target)?.ArgumentsHash ?? "");
        }, ct);
        Guard.Require(await audio(ct), 403, "The voice window changed.");
        if (action == "approve") {
            await gateway.ConfirmCapturedAsync(selection.Id, selection.Hash, session, source, ct);
            using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent); await DispatchAsync(selection.Id, ct);
            return new { status = "approved", detail = "Await a verified device receipt before reporting success." };
        }
        Guard.Require(action == "cancel", 400, "Unknown agent voice action.");
        // The normal cancellation path revokes grants and prevents subsequent device dispatch.
        await runtime.CancelAsync(selection.Id, false, ct);
        return new { status = "cancelled" };
    }
}

public static class AgentActionAuthority
{
    public static bool Current(RuntimeState s, AgentActionProposal p, DateTimeOffset now)
    {
        if (!AgentSpecialistCatalog.AllowsDeviceAction(p.CapabilityId) || !s.AgentRuns.TryGetValue(p.RunId, out var run) || run.DeletedAt is not null || run.Status != AgentRunStatus.Running || run.Deadline <= now ||
            s.AgentControls.EffectiveAgentsPaused || s.AgentControls.PausedWorkflows?.Contains(run.Workflow) == true ||
            !s.AgentTasks.TryGetValue(p.Id, out var task) || task.Status is not (AgentTaskStatus.Running or AgentTaskStatus.AwaitingApproval or AgentTaskStatus.AwaitingReceipt) ||
            !(task.SourceRefs ?? []).All(r => AgentRuntime.SourceCurrent(s, r)) ||
            !s.AgentToolGrants.TryGetValue(p.GrantId, out var grant) || grant.RevokedAt is not null || grant.ExpiresAt <= now ||
            grant.RunId != p.RunId || grant.CapabilityId != p.CapabilityId || grant.CapabilityVersion != p.CapabilityVersion) return false;
        return grant.ScopeKind switch {
            AgentToolScopeKinds.Goal => s.Goals.TryGetValue(grant.ScopeId, out var goal) && goal.Revision == grant.ScopeRevision && goal.Status == GoalStatus.Active && goal.LastRunId == run.Id,
            AgentToolScopeKinds.Trigger => s.AgentTriggers.TryGetValue(grant.ScopeId, out var trigger) && trigger.Revision == grant.ScopeRevision && trigger.Status == AgentTriggerStatus.Dispatched && trigger.RunId == run.Id,
            AgentToolScopeKinds.Run => grant.ScopeId == run.Id && grant.ScopeRevision == run.Version,
            _ => false
        };
    }
}

public sealed class AgentDeviceTool(string capability, IRuntimeStore store, IDeviceGrantStore grants, IDeviceCapabilityCatalog catalog,
    DeviceCommandService commands, TimeProvider clock) : IAgentToolHandler
{
    public string CapabilityId => capability;
    public async Task<IPreparedAgentTool> PrepareAsync(AgentToolContext context, CancellationToken ct)
    {
        var proposal = await store.ReadAsync(s => {
            Guard.Require(s.AgentActions.TryGetValue(context.Call.Id, out var p) && p.Id == context.Task.Id && p.RunId == context.Run.Id &&
                p.GrantId == context.Call.GrantId && p.CapabilityId == context.Call.CapabilityId && p.ArgumentsJson == context.Call.ArgumentsJson &&
                AgentActionAuthority.Current(s, p, clock.GetUtcNow()), 403, "The governed action proposal is not current."); return p;
        }, ct);
        var grant = await grants.GetAsync(proposal.DeviceGrantId, ct);
        Guard.Require(grant is not null && grant.RevokedAt is null && grant.ExpiresAt > clock.GetUtcNow() &&
            catalog.Permits(grant.CapabilityId, grant.CapabilityVersion, grant.ConstraintsJson, proposal.ArgumentsJson), 403, "Device grant changed.");
        return new Prepared(proposal, commands);
    }
    private sealed class Prepared(AgentActionProposal p, DeviceCommandService commands) : IPreparedAgentTool
    {
        public AgentToolCharge? Charge => null;
        public Guid? CachedReservationId => null;
        public async Task<string> ExecuteAsync(CancellationToken ct) {
            Guard.Require(AgentToolExecutionScope.Contains(p.Id, p.Id), 403, "Only the agent gateway may dispatch this handler.");
            var command = await commands.ProposeAsync(p.DeviceId, new(p.Id, p.DeviceGrantId, p.ArgumentsJson, p.ExpiresAt), ct);
            if (command.Status == DeviceCommandStatus.AwaitingConfirmation) await commands.ConfirmGovernedAgentAsync(p.Id, ct);
            return JsonSerializer.Serialize(new { commandId = p.Id, status = "awaiting_device_receipt" });
        }
    }
}
