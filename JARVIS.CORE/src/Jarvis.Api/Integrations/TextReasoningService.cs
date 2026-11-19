using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

internal sealed class PreparedReasoningCall(AgentToolCharge? charge, Guid? cachedReservation,
    Func<CancellationToken, Task<TextReasoningResult>> execute, Func<CancellationToken, Task> refused)
{
    public AgentToolCharge? Charge => charge;
    public Guid? CachedReservationId => cachedReservation;
    public Task<TextReasoningResult> ExecuteAsync(CancellationToken ct) => execute(ct);
    public Task RefusedAsync(CancellationToken ct) => refused(ct);
}

public sealed class TextReasoningService(RateCard cards, IEnumerable<ITextReasoningAdapter> installed, IRuntimeStore store,
    UsageBudget budget, TimeProvider clock) : ITextReasoningProvider
{
    private readonly IReadOnlyDictionary<string, ITextReasoningAdapter> adapters = installed.ToDictionary(x => x.Provider, StringComparer.Ordinal);
    public async Task<TextReasoningResult> ReasonAsync(TextReasoningTask task, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin != InvocationOrigin.Agent || AgentToolExecutionScope.Contains(task.OperationId, task.ScopeId) || await LegacyReasoningAdmission.PermitsAsync(task, ct),
            403, "Agent reasoning must enter through its tool-policy gateway.");
        var prepared = await PrepareAsync(task, ct);
        try {
            if (prepared.Charge is { } charge) await budget.ReserveAsync(charge.Id, charge.TaskId, charge.Model, charge.MaximumUsd, charge.Limits, ct, charge.Lane, charge.Provider);
            return await prepared.ExecuteAsync(ct);
        }
        catch { await prepared.RefusedAsync(CancellationToken.None); throw; }
    }
    // Shared by ordinary reasoning and the agent gateway. Preparation does no provider I/O.
    internal async Task<PreparedReasoningCall> PrepareAsync(TextReasoningTask task, CancellationToken ct)
    {
        Guard.Require(task.OperationId != Guid.Empty && task.ScopeId != Guid.Empty && task.AllowCloudProcessing, 403, "A scoped request ID and cloud consent are required.");
        Guard.Require(Enum.IsDefined(task.Capability) && Enum.IsDefined(task.DataClass) && Enum.IsDefined(task.Lane) &&
            task.Messages.Count is > 0 and <= 24 && task.Messages.All(m => m.Role is ChatRoles.User or ChatRoles.Assistant && !string.IsNullOrWhiteSpace(m.Text)), 400, "Invalid normalized reasoning request.");
        Guard.Require(!string.IsNullOrWhiteSpace(task.Instructions) && Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(task)) <= 180000, 400, "Reasoning context exceeded its bound.");
        var rates = cards.ReadText(task.Profile, task.Capability, task.DataClass, task.OutputLimit);
        rates = rates with { Route = rates.Route with { CloudConsent = task.AllowCloudProcessing },
            Fallbacks = rates.Fallbacks.Select(r => r with { CloudConsent = task.AllowCloudProcessing }).ToArray() };
        Guard.Require(adapters.TryGetValue(rates.Route.Provider, out var adapter), 503, "The selected provider adapter is not installed. No fallback was attempted.");
        Guard.Require(adapter.Capabilities.Contains(task.Capability), 403, "The selected adapter cannot supply the required evidence or output capability. No fallback was attempted.");
        budget.RequireConfigured();
        var fingerprint = PrivacyData.Hash(JsonSerializer.Serialize(new { task, rates.Route }));
        var cached = await store.UpdateAsync(s => {
            ManagedAi.RequireSourceCurrent(s, task.ScopeId, clock.GetUtcNow());
            if (s.ProviderAttempts.TryGetValue(task.OperationId, out var previous)) {
                Guard.Require(previous.ScopeId == task.ScopeId && previous.Fingerprint == fingerprint, 409, "Reasoning request ID was reused for another task or route.");
                Guard.Require(previous.Outcome is ProviderAttemptOutcome.Completed or ProviderAttemptOutcome.Cached && s.ProviderResults.ContainsKey(task.OperationId), 409,
                    "This provider attempt is pending, failed or outside its cache window. Automatic replay is blocked.");
                return true;
            }
            var wasCached = s.ProviderResults.ContainsKey(task.OperationId);
            s.ProviderAttempts[task.OperationId] = new(task.OperationId, task.ScopeId, fingerprint, rates.Route, task.OperationId, ProviderAttemptOutcome.Preparing, clock.GetUtcNow());
            return wasCached;
        }, ct);
        async Task Refused(CancellationToken token)
        {
            try { await store.UpdateAsync(s => {
                if (s.ProviderAttempts.TryGetValue(task.OperationId, out var attempt)) {
                    var reservation = s.UsageReservations.GetValueOrDefault(task.OperationId);
                    if (attempt.Outcome is not (ProviderAttemptOutcome.Completed or ProviderAttemptOutcome.Cached))
                        s.ProviderAttempts[task.OperationId] = attempt with { Outcome = reservation is null ? ProviderAttemptOutcome.Refused : reservation.ActualUsd.HasValue ? ProviderAttemptOutcome.Failed : ProviderAttemptOutcome.Uncertain, CompletedAt = clock.GetUtcNow() };
                }
                return true;
            }, token); } catch { /* Original failure wins; preparing/uncertain still blocks replay. */ }
        }
        async Task<TextReasoningResult> Dispatch(CancellationToken token)
        {
            Guard.Require(CoreInvocationContext.Origin != InvocationOrigin.Agent || AgentToolExecutionScope.Contains(task.OperationId, task.ScopeId) || await LegacyReasoningAdmission.PermitsAsync(task, token),
                403, "Agent reasoning must enter through its tool-policy gateway.");
            await store.ReadAsync(s => {
                ManagedAi.RequireSourceCurrent(s, task.ScopeId, clock.GetUtcNow());
                Guard.Require(s.UsageReservations.TryGetValue(task.OperationId, out var reservation) && reservation.RunId == task.ScopeId &&
                    reservation.Model == rates.Route.Model && reservation.Provider == rates.Route.Provider && !reservation.ReconciliationRequired &&
                    (cached ? reservation.ActualUsd is not null : reservation.ActualUsd is null && !s.AgentControls.EffectivePaidAiPaused),
                    403, "A matching reasoning reservation is required before dispatch."); return true;
            }, token);
            try {
            var result = await adapter.ExecuteAsync(task, rates, token);
            await store.UpdateAsync(s => {
                ManagedAi.RequireSourceCurrent(s, task.ScopeId, clock.GetUtcNow());
                var attempt = s.ProviderAttempts[task.OperationId];
                s.ProviderAttempts[task.OperationId] = attempt with { Outcome = cached ? ProviderAttemptOutcome.Cached : ProviderAttemptOutcome.Completed, CompletedAt = clock.GetUtcNow() }; return true;
            }, token);
            return new(result.Text, result.Citations, result.ServedRoute ?? rates.Route);
            } catch { await Refused(CancellationToken.None); throw; }
        }
        return new(cached ? null : new AgentToolCharge(task.OperationId, task.ScopeId, rates.Route.Provider, rates.Route.Model,
            rates.MaximumCost(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(task)) + 32768), rates.Limits, task.Lane),
            cached ? task.OperationId : null, Dispatch, Refused);
    }
}
