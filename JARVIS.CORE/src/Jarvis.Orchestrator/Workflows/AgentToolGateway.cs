using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

public sealed record AgentToolCall(Guid Id, Guid TaskId, Guid ClaimToken, Guid GrantId, string CapabilityId,
    string CapabilityVersion, string ArgumentsJson, ReasoningDataClass DataClass);
public sealed record AgentToolContext(AgentToolCall Call, AgentTask Task, AgentRun Run, AgentSpecialistDescriptor Specialist);
public sealed record AgentToolCharge(Guid Id, Guid TaskId, string Provider, string Model, decimal MaximumUsd, BudgetLimits Limits, UsageLane Lane);
public interface IPreparedAgentTool
{
    AgentToolCharge? Charge { get; }
    Guid? CachedReservationId { get; }
    Task<string> ExecuteAsync(CancellationToken ct);
    Task RefusedAsync(CancellationToken ct) => Task.CompletedTask;
}
// Prepare must not contact a provider or execute an action. Only installed handlers are resolved.
public interface IAgentToolHandler
{
    string CapabilityId { get; }
    Task<IPreparedAgentTool> PrepareAsync(AgentToolContext context, CancellationToken ct);
}
public static class AgentToolStatus
{
    public const string AwaitingApproval = "AwaitingApproval", Preparing = "Preparing", Authorized = "Authorized",
        Executing = "Executing", Succeeded = "Succeeded", Refused = "Refused", Uncertain = "Uncertain";
}
public static class AgentToolScopeKinds
{
    public const string Goal = "goal", Trigger = "trigger", Run = "run", InstalledPolicy = "installed-artifact-policy";
}

public sealed class AgentToolGateway(IRuntimeStore store, TimeProvider clock, ICapabilityRegistry registry,
    AgentSpecialistCatalog specialists, IEnumerable<IAgentToolHandler> installed, UsageBudget budget)
{
    public const string ManageScope = "jarvis:agent-tools:manage";
    private const int MaximumGrants = 2000, MaximumInvocations = 10000;
    private readonly IReadOnlyDictionary<string, IAgentToolHandler> handlers = installed.ToDictionary(x => x.CapabilityId, StringComparer.Ordinal);
    private sealed record Scope(Guid Id, long Revision, string Kind);
    private static Scope ScopeOf(RuntimeState state, AgentRun run)
    {
        var goal = state.Goals.Values.SingleOrDefault(g => g.LastRunId == run.Id);
        if (goal is not null) {
            Guard.Require(goal.Status == GoalStatus.Active, 409, "Agent goal is inactive.");
            return new(goal.Id, goal.Revision, AgentToolScopeKinds.Goal);
        }
        var trigger = state.AgentTriggers.Values.SingleOrDefault(t => t.RunId == run.Id);
        if (trigger is not null) {
            Guard.Require(trigger.Status == AgentTriggerStatus.Dispatched, 409, "Agent trigger is inactive.");
            return new(trigger.Id, trigger.Revision, AgentToolScopeKinds.Trigger);
        }
        return new(run.Id, run.Version, AgentToolScopeKinds.Run);
    }
    private AgentRun CurrentRun(RuntimeState state, Guid id, bool ownerSetup = false)
    {
        Guard.Require(state.AgentRuns.TryGetValue(id, out var run) && run.DeletedAt is null &&
            (run.Status is AgentRunStatus.Queued or AgentRunStatus.Running || ownerSetup && run.Status == AgentRunStatus.AwaitingSetup) && run.Deadline > clock.GetUtcNow() &&
            (ownerSetup || !state.AgentControls.EffectiveAgentsPaused && state.AgentControls.PausedWorkflows?.Contains(run.Workflow) != true),
            409, "Agent run is paused, cancelled or expired.");
        return run;
    }
    private (AgentTask Task, AgentRun Run, AgentSpecialistDescriptor Specialist) CurrentTask(RuntimeState state, Guid id, Guid claim)
    {
        Guard.Require(state.AgentTasks.TryGetValue(id, out var task) && claim != Guid.Empty && task.ClaimToken == claim &&
            task.Status is AgentTaskStatus.Running or AgentTaskStatus.AwaitingApproval && task.LeaseExpiresAt > clock.GetUtcNow(), 409, "Agent tool claim expired or changed.");
        var run = CurrentRun(state, task.RunId);
        Guard.Require(run.TaskIds.Contains(task.Id) && (task.SourceRefs ?? []).All(s => AgentRuntime.SourceCurrent(state, s)), 409, "Agent tool source changed.");
        var specialist = specialists.Get(task.Role);
        Guard.Require(DeviceJsonSchema.Accepts(DeviceJsonSchema.ReadCapability(specialist.InputSchemaJson), JsonSerializer.SerializeToElement(new { input = task.Input })),
            400, "Agent task input does not match the specialist schema.");
        return (task, run, specialist);
    }
    private static string DescriptorHash(CapabilityDescriptor descriptor) => PrivacyData.Hash(JsonSerializer.Serialize(descriptor));
    private static string Hash(AgentToolCall call) => PrivacyData.Hash(JsonSerializer.Serialize(new {
        call.TaskId, call.GrantId, call.CapabilityId, call.CapabilityVersion, call.ArgumentsJson, call.DataClass
    }));
    private static string Hash(RuntimeState state, AgentToolCall call) => state.AgentActions.TryGetValue(call.Id, out var p)
        ? PrivacyData.Hash(JsonSerializer.Serialize(new { Invocation = Hash(call), p.DeviceGrantId, p.DeviceId, p.ParentTaskId, p.RequiresOwnerApproval })) : Hash(call);
    private AgentToolGrant RequireGrant(RuntimeState state, AgentToolCall call, AgentRun run, CapabilityDescriptor descriptor)
    {
        var scope = ScopeOf(state, run);
        Guard.Require(state.AgentToolGrants.TryGetValue(call.GrantId, out var grant) && grant.RunId == run.Id &&
            grant.ScopeId == scope.Id && grant.ScopeRevision == scope.Revision && grant.ScopeKind == scope.Kind &&
            grant.RevokedAt is null && grant.ExpiresAt > clock.GetUtcNow() && grant.CapabilityId == call.CapabilityId &&
            grant.CapabilityVersion == call.CapabilityVersion && grant.DescriptorHash == DescriptorHash(descriptor) && grant.DataClasses.Contains(call.DataClass),
            403, "No current goal-scoped capability grant permits this invocation.");
        return grant;
    }
    private (AgentToolContext Context, CapabilityDescriptor Descriptor, AgentToolGrant Grant) Check(RuntimeState state, AgentToolCall call)
    {
        Guard.Require(call.Id != Guid.Empty && Enum.IsDefined(call.DataClass), 400, "A scoped agent tool invocation is required.");
        var (task, run, specialist) = CurrentTask(state, call.TaskId, call.ClaimToken);
        Guard.Require(specialist.AllowedCapabilities.Contains(call.CapabilityId, StringComparer.Ordinal) && specialist.DataClasses.Contains(call.DataClass),
            403, "The specialist descriptor does not permit this tool or data class.");
        Guard.Require(Encoding.UTF8.GetByteCount(call.ArgumentsJson) <= specialist.MaximumInputBytes &&
            registry.Accepts(call.CapabilityId, call.CapabilityVersion, call.ArgumentsJson), 400, "Agent tool parameters do not match the installed schema.");
        var descriptor = registry.Get(call.CapabilityId);
        Guard.Require(descriptor.DataClasses?.Contains(call.DataClass) == true, 403, "The tool does not accept this data class.");
        Guard.Require(!descriptor.RequiresBudget || run.AllowCloudProcessing, 403, "This run has no cloud-processing consent.");
        return (new(call, task, run, specialist), descriptor, RequireGrant(state, call, run, descriptor));
    }
    private static void Owner(BudgetActor actor) => Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient &&
        actor.Scopes.Contains(ManageScope) && !string.IsNullOrWhiteSpace(actor.SubjectId) && actor.SubjectId.Length <= 120,
        403, "Manual owner authorization is required for agent tool permissions.");

    public Task<AgentToolGrant> GrantAsync(Guid id, Guid runId, string capabilityId, decimal maximumUsd, int maximumCalls,
        bool preApproved, BudgetActor actor, CancellationToken ct = default)
    {
        Owner(actor); var descriptor = registry.Get(capabilityId);
        Guard.Require(id != Guid.Empty && maximumUsd is >= 0 and <= 10 && maximumCalls is > 0 and <= 100 &&
            (!preApproved || !descriptor.RequiresOwnerApproval && !descriptor.Irreversible &&
                (descriptor.Risk == CapabilityRisk.ReadOnly || descriptor.ActionRisk == DeviceCapabilityRisk.Reversible)), 400, "Invalid agent capability grant.");
        return store.UpdateAsync(state => {
            var run = CurrentRun(state, runId, true); var scope = ScopeOf(state, run);
            var grant = new AgentToolGrant(id, runId, scope.Id, scope.Revision, scope.Kind, descriptor.Key, descriptor.Version,
                DescriptorHash(descriptor), descriptor.DataClasses ?? [], maximumUsd, maximumCalls, preApproved, actor.SubjectId, run.Deadline);
            if (state.AgentToolGrants.TryGetValue(id, out var prior)) {
                Guard.Require(JsonSerializer.Serialize(prior) == JsonSerializer.Serialize(grant), 409, "Grant ID was reused or revoked."); return prior;
            }
            Guard.Require(state.AgentToolGrants.Count < MaximumGrants, 409, "Agent grant capacity reached. Apply retention.");
            state.AgentToolGrants.Add(id, grant); return grant;
        }, ct);
    }
    public Task<bool> RevokeAsync(Guid id, BudgetActor actor, CancellationToken ct = default)
    {
        Owner(actor); return store.UpdateAsync(state => {
            Guard.Require(state.AgentToolGrants.TryGetValue(id, out var grant), 404, "Agent grant not found.");
            state.AgentToolGrants[id] = grant with { RevokedAt = grant.RevokedAt ?? clock.GetUtcNow() }; return true;
        }, ct);
    }
    public Task<bool> ConfirmAsync(Guid id, string argumentsHash, BudgetActor actor, CancellationToken ct = default)
    {
        Owner(actor); return ConfirmCoreAsync(id, argumentsHash, actor.SubjectId, null, ct);
    }
    internal Task<bool> ConfirmCapturedAsync(Guid id, string hash, Guid session, Guid source, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Voice, 403, "Captured voice authorization is required.");
        return ConfirmCoreAsync(id, hash, "voice:" + session, state => {
            Guard.Require(!VoiceAccessRules.ToolRestricted(state, session) && state.AudioLease!.ExpiresAt > clock.GetUtcNow() && !state.RetiredSessions.Contains(session) && state.VoiceTurns.TryGetValue(source, out var turn) &&
                turn.SessionId == session && turn.Role == ChatRoles.User && !turn.Restricted && turn.At >= clock.GetUtcNow().AddSeconds(-90),
                403, "A current unlocked captured utterance is required.");
            Guard.Require(state.AgentToolInvocations.TryGetValue(id, out var invocation) &&
                registry.Get(invocation.CapabilityId).ActionRisk == DeviceCapabilityRisk.Reversible && !registry.Get(invocation.CapabilityId).Irreversible,
                403, "Only reversible actions can be approved by voice.");
        }, ct);
    }
    private Task<bool> ConfirmCoreAsync(Guid id, string argumentsHash, string subject, Action<RuntimeState>? additional, CancellationToken ct)
    {
        return store.UpdateAsync(state => {
            additional?.Invoke(state);
            Guard.Require(state.AgentToolInvocations.TryGetValue(id, out var invocation) && invocation.Status == AgentToolStatus.AwaitingApproval &&
                invocation.ArgumentsHash == argumentsHash && invocation.ExpiresAt > clock.GetUtcNow(), 409, "Approval must match the exact pending parameters.");
            Guard.Require(state.AgentTasks.TryGetValue(invocation.TaskId, out var task), 409, "Agent task was removed.");
            Guard.Require(task.ClaimToken is Guid currentClaim && invocation.ClaimHash == PrivacyData.Hash(currentClaim.ToString()), 409, "Approval belongs to an obsolete agent claim.");
            var run = CurrentTask(state, task.Id, task.ClaimToken ?? Guid.Empty).Run;
            var descriptor = registry.Get(invocation.CapabilityId);
            _ = RequireGrant(state, new(invocation.Id, task.Id, task.ClaimToken!.Value, invocation.GrantId, invocation.CapabilityId,
                invocation.CapabilityVersion, "{}", invocation.DataClass), run, descriptor);
            state.AgentToolInvocations[id] = invocation with { ApprovedBy = subject, ApprovedHash = argumentsHash, Status = AgentToolStatus.Authorized }; return true;
        }, ct);
    }

    // Only the existing read-only artifact paths receive installed-policy grants. A domain
    // definition or a model can never select another capability through this convenience entry.
    public async Task<string> InvokeBuiltInAsync(AgentTask task, Guid id, string capability, string arguments, ReasoningDataClass dataClass, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Agent, 403, "Agent invocation context is required.");
        Guard.Require(capability is AgentCapabilityIds.Template or AgentCapabilityIds.Reasoning or AgentCapabilityIds.Research, 403, "Only installed artifact tools receive baseline grants.");
        var descriptor = registry.Get(capability);
        var grant = await store.UpdateAsync(state => {
            var (_, run, specialist) = CurrentTask(state, task.Id, task.ClaimToken ?? Guid.Empty);
            Guard.Require(specialist.AllowedCapabilities.Contains(capability) && descriptor.Risk == CapabilityRisk.ReadOnly && !descriptor.RequiresOwnerApproval && !descriptor.Irreversible,
                403, "The installed baseline cannot grant this tool.");
            var grantId = AgentRuntime.StableId("agent-tool-grant-v1:" + run.Id + ":" + capability);
            if (state.AgentToolGrants.TryGetValue(grantId, out var prior)) return prior;
            Guard.Require(state.AgentToolGrants.Count < MaximumGrants, 409, "Agent grant capacity reached. Apply retention.");
            var scope = ScopeOf(state, run);
            var created = new AgentToolGrant(grantId, run.Id, scope.Id, scope.Revision, scope.Kind, capability, descriptor.Version,
                DescriptorHash(descriptor), descriptor.DataClasses ?? [], specialist.MaximumReservationUsd, 100, true, AgentToolScopeKinds.InstalledPolicy, run.Deadline);
            state.AgentToolGrants.Add(grantId, created); return created;
        }, ct);
        return await InvokeAsync(new(id, task.Id, task.ClaimToken!.Value, grant.Id, capability, descriptor.Version, arguments, dataClass), ct);
    }

    public async Task<string> InvokeAsync(AgentToolCall call, CancellationToken ct = default)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Agent, 403, "Agent tool execution requires the agent invocation boundary.");
        Guard.Require(handlers.TryGetValue(call.CapabilityId, out var handler), 403, "No executable agent handler is installed for this capability.");
        var admitted = await store.UpdateAsync(state => {
            var policy = Check(state, call); var hash = Hash(state, call); var claimHash = PrivacyData.Hash(call.ClaimToken.ToString());
            if (state.AgentToolInvocations.TryGetValue(call.Id, out var prior)) {
                Guard.Require(prior.ArgumentsHash == hash && prior.TaskId == call.TaskId && prior.ClaimHash == claimHash && prior.GrantId == call.GrantId,
                    409, "Invocation ID or approval was reused with different parameters or authority.");
                if (prior.Status == AgentToolStatus.AwaitingApproval) return (policy.Context, policy.Descriptor, policy.Grant, prior);
                Guard.Require(prior.Status == AgentToolStatus.Authorized || prior.Status == AgentToolStatus.Succeeded && policy.Descriptor.Risk == CapabilityRisk.ReadOnly && !policy.Descriptor.Irreversible,
                    409, "This tool invocation cannot be replayed automatically.");
                if (policy.Descriptor.RequiresOwnerApproval || policy.Descriptor.Irreversible || state.AgentActions.GetValueOrDefault(call.Id)?.RequiresOwnerApproval == true || !policy.Grant.PreApproved && policy.Descriptor.Risk != CapabilityRisk.ReadOnly)
                    Guard.Require(prior.ApprovedHash == hash && prior.ApprovedBy is not null, 403, "Exact owner approval is required.");
                var preparing = prior with { Status = AgentToolStatus.Preparing, CompletedAt = null };
                state.AgentToolInvocations[call.Id] = preparing; return (policy.Context, policy.Descriptor, policy.Grant, preparing);
            }
            Guard.Require(state.AgentToolInvocations.Count < MaximumInvocations &&
                state.AgentToolInvocations.Values.Count(x => x.TaskId == call.TaskId) < policy.Context.Specialist.MaximumCalls &&
                state.AgentToolInvocations.Values.Count(x => x.GrantId == call.GrantId) < policy.Grant.MaximumCalls,
                429, "Agent tool invocation limit reached.");
            var approval = policy.Descriptor.RequiresOwnerApproval || policy.Descriptor.Irreversible || state.AgentActions.GetValueOrDefault(call.Id)?.RequiresOwnerApproval == true || !policy.Grant.PreApproved && policy.Descriptor.Risk != CapabilityRisk.ReadOnly;
            var invocation = new AgentToolInvocation(call.Id, call.GrantId, policy.Context.Run.Id, call.TaskId, claimHash, call.CapabilityId, call.CapabilityVersion,
                hash, call.DataClass, approval ? AgentToolStatus.AwaitingApproval : AgentToolStatus.Preparing, clock.GetUtcNow(), policy.Grant.ExpiresAt);
            state.AgentToolInvocations.Add(call.Id, invocation); return (policy.Context, policy.Descriptor, policy.Grant, invocation);
        }, ct);
        Guard.Require(admitted.Item4.Status != AgentToolStatus.AwaitingApproval, 409, "Exact owner approval is required before this agent tool can execute.");
        IPreparedAgentTool? prepared = null; Guid? reservationId = null;
        try {
            prepared = await handler.PrepareAsync(admitted.Context, ct);
            var charge = prepared.Charge;
            Guard.Require(!admitted.Descriptor.RequiresBudget || charge is not null || prepared.CachedReservationId is not null,
                403, "The paid tool has no budget reservation plan.");
            Guard.Require(charge is null || admitted.Descriptor.RequiresBudget && charge.Id == call.Id && charge.TaskId == call.TaskId &&
                charge.MaximumUsd > 0 && charge.MaximumUsd <= Math.Min(admitted.Grant.MaximumUsd, admitted.Context.Specialist.MaximumReservationUsd) &&
                charge.Lane == UsageLane.Background, 403, "Agent tool cost exceeds its scoped grant or specialist policy.");
            if (charge is not null) {
                await budget.ReserveAsync(charge.Id, charge.TaskId, charge.Model, charge.MaximumUsd, charge.Limits, ct, charge.Lane, charge.Provider);
                reservationId = charge.Id;
            }
            else reservationId = prepared.CachedReservationId;
            await store.UpdateAsync(state => {
                _ = Check(state, call);
                Guard.Require(state.AgentToolInvocations[call.Id].Status == AgentToolStatus.Preparing, 409, "Tool dispatch authority changed.");
                if (admitted.Descriptor.RequiresBudget) {
                    Guard.Require(reservationId is Guid reserved && state.UsageReservations.TryGetValue(reserved, out var entry) &&
                        entry.RunId == call.TaskId && !entry.ReconciliationRequired &&
                        (charge is not null ? entry.Model == charge.Model && entry.Provider == charge.Provider && entry.ReservedUsd == charge.MaximumUsd && entry.ActualUsd is null && !state.AgentControls.EffectivePaidAiPaused
                            : reserved == call.Id && entry.ActualUsd is not null), 403, "A current matching budget reservation is required before dispatch.");
                }
                state.AgentToolInvocations[call.Id] = state.AgentToolInvocations[call.Id] with { Status = AgentToolStatus.Executing, ReservationId = reservationId }; return true;
            }, ct);
            using var execution = AgentToolExecutionScope.Enter(call.Id, call.TaskId);
            var result = await prepared.ExecuteAsync(ct);
            Guard.Require(Encoding.UTF8.GetByteCount(result) <= admitted.Context.Specialist.MaximumOutputBytes, 502, "Tool output exceeded the specialist limit.");
            await store.UpdateAsync(state => {
                _ = Check(state, call);
                Guard.Require(state.AgentToolInvocations[call.Id].Status == AgentToolStatus.Executing, 409, "Tool completion authority changed.");
                state.AgentToolInvocations[call.Id] = state.AgentToolInvocations[call.Id] with { Status = AgentToolStatus.Succeeded, OutputHash = PrivacyData.Hash(result), CompletedAt = clock.GetUtcNow() }; return true;
            }, ct);
            return result;
        }
        catch {
            if (prepared is not null) try { await prepared.RefusedAsync(CancellationToken.None); } catch { }
            try { await store.UpdateAsync(state => {
                if (state.AgentToolInvocations.TryGetValue(call.Id, out var invocation)) state.AgentToolInvocations[call.Id] = invocation with {
                    Status = reservationId is not null ? AgentToolStatus.Uncertain : AgentToolStatus.Refused,
                    ReservationId = reservationId, CompletedAt = clock.GetUtcNow() }; return true;
            }, CancellationToken.None); } catch { /* Do not replace the original error or recreate erased state. */ }
            throw;
        }
    }
    public void ValidateArtifact(AgentTask task, string artifact) => Guard.Require(
        DeviceJsonSchema.Accepts(DeviceJsonSchema.ReadCapability(specialists.Get(task.Role).OutputSchemaJson), JsonSerializer.SerializeToElement(new { artifact })),
        502, "Agent artifact does not match the specialist output schema.");
}

public static class AgentToolExecutionScope
{
    private sealed class Permit(Guid invocation, Guid task) {
        public readonly Guid Invocation = invocation, Task = task;
        public int Active = 1;
    }
    private static readonly AsyncLocal<Permit?> Current = new();
    public static bool Contains(Guid invocation, Guid task) => Current.Value is { } value && Volatile.Read(ref value.Active) == 1 && value.Invocation == invocation && value.Task == task;
    internal static IDisposable Enter(Guid invocation, Guid task) {
        var previous = Current.Value; var permit = new Permit(invocation, task); Current.Value = permit;
        return new Restore(() => { Volatile.Write(ref permit.Active, 0); Current.Value = previous; });
    }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}

public static class AgentToolLifecycle
{
    public static void Retire(RuntimeState state, Guid runId, DateTimeOffset at, bool erase)
    {
        foreach (var grant in state.AgentToolGrants.Values.Where(x => x.RunId == runId).ToArray()) {
            if (erase) state.AgentToolGrants.Remove(grant.Id);
            else state.AgentToolGrants[grant.Id] = grant with { RevokedAt = grant.RevokedAt ?? at };
        }
        if (erase) {
            foreach (var id in state.AgentToolInvocations.Values.Where(x => x.RunId == runId).Select(x => x.Id).ToArray()) state.AgentToolInvocations.Remove(id);
            foreach (var id in state.AgentActions.Values.Where(x => x.RunId == runId).Select(x => x.Id).ToArray()) state.AgentActions.Remove(id);
        }
    }
}
