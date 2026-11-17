using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Integrations;

public sealed class TextReasoningService(RateCard cards, IEnumerable<ITextReasoningAdapter> installed, IRuntimeStore store,
    UsageBudget budget, TimeProvider clock) : ITextReasoningProvider
{
    private readonly IReadOnlyDictionary<string, ITextReasoningAdapter> adapters = installed.ToDictionary(x => x.Provider, StringComparer.Ordinal);
    public async Task<TextReasoningResult> ReasonAsync(TextReasoningTask task, CancellationToken ct)
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
            ManagedAi.RequireSourceCurrent(s, task.ScopeId);
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
        try {
            // Every installed adapter enters through one durable admission boundary.
            // UTF-8 bytes conservatively bound text tokens; framing covers both supported codecs.
            if (!cached) await budget.ReserveAsync(task.OperationId, task.ScopeId, rates.Route.Model,
                rates.MaximumCost(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(task)) + 32768),
                rates.Limits, ct, task.Lane, rates.Route.Provider);
            var result = await adapter.ExecuteAsync(task, rates, ct);
            await store.UpdateAsync(s => {
                ManagedAi.RequireSourceCurrent(s, task.ScopeId);
                var attempt = s.ProviderAttempts[task.OperationId];
                s.ProviderAttempts[task.OperationId] = attempt with { Outcome = cached ? ProviderAttemptOutcome.Cached : ProviderAttemptOutcome.Completed, CompletedAt = clock.GetUtcNow() }; return true;
            }, ct);
            return new(result.Text, result.Citations, result.ServedRoute ?? rates.Route);
        }
        catch {
            try { await store.UpdateAsync(s => {
                if (s.ProviderAttempts.TryGetValue(task.OperationId, out var attempt)) {
                    var charge = s.UsageReservations.GetValueOrDefault(task.OperationId);
                    s.ProviderAttempts[task.OperationId] = attempt with { Outcome = charge is null ? ProviderAttemptOutcome.Refused : charge.ActualUsd.HasValue ? ProviderAttemptOutcome.Failed : ProviderAttemptOutcome.Uncertain, CompletedAt = clock.GetUtcNow() };
                }
                return true;
            }, CancellationToken.None); } catch { /* Original error wins; the durable preparing record still prevents replay. */ }
            throw;
        }
    }
}
