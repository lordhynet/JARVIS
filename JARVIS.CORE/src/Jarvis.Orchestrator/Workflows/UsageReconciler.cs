using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

public sealed class UsageReconciler(IRuntimeStore store, TimeProvider clock)
{
    public Task<UsageReconciliation> ReconcileAsync(BudgetActor actor, Guid id, ReconcileUsageRequest request, CancellationToken ct = default)
    {
        BudgetAuthorization.RequireOwner(actor);
        Guard.Require(request.RequestId != Guid.Empty && request.Confirmation == "CONFIRM VERIFIED CHARGE" && request.ActualUsd is >= 0 and <= 10000,
            400, "Supply a valid verified charge, operation ID and exact confirmation.");
        Guard.Require(request.ActualGbp is null || request.ActualGbp is >= 0 and <= 10000 &&
            request.ActualGbp.Value * 100 == decimal.Truncate(request.ActualGbp.Value * 100), 400, "Verified GBP charges require nonnegative whole pence within the supported accounting bound.");
        var evidence = Guard.Text(request.Evidence, "Billing evidence reference", 500);
        return store.UpdateAsync(s =>
        {
            if (s.UsageReconciliations.TryGetValue(request.RequestId, out var previous))
            {
                Guard.Require(previous.ReservationId == id && previous.ActualUsd == request.ActualUsd && previous.ActualGbp == request.ActualGbp && previous.Evidence == evidence, 409, "Reconciliation ID was reused."); return previous;
            }
            Guard.Require(s.UsageReservations.TryGetValue(id, out var charge), 404, "Charge not found.");
            Guard.Require(!s.UsageReconciliations.Values.Any(x => x.ReservationId == id), 409, "Charge already reconciled.");
            var hasGbp = s.GbpCommitments.TryGetValue(id, out var gbp);
            Guard.Require(request.ActualGbp is null || hasGbp, 409, "Classify the legacy charge before recording verified GBP billing.");
            var gbpCorrection = request.ActualGbp is not null && hasGbp && !gbp!.GbpVerified;
            Guard.Require(charge.ActualUsd is null || charge.ReconciliationRequired || gbpCorrection && charge.ActualUsd == request.ActualUsd,
                409, "Charge does not require reconciliation, or verified USD would be changed without a reconciliation discrepancy.");
            // Retain an upper bound until the caller's provider request or lease has ended.
            Guard.Require(charge.At <= clock.GetUtcNow().AddMinutes(-10), 409, "Allow in-flight provider calls to finish before reconciliation.");
            s.UsageReservations[id] = charge with { ActualUsd = request.ActualUsd, ReconciliationRequired = false };
            if (s.GbpCommitments.TryGetValue(id, out var commitment))
                s.GbpCommitments[id] = commitment with { AccountedGbp = request.ActualGbp ?? GbpBudgetRules.Convert(request.ActualUsd, commitment.Fx, commitment.TaxMultiplier, commitment.SafetyMultiplier),
                    GbpVerified = request.ActualGbp is not null };
            var result = new UsageReconciliation(request.RequestId, id, request.ActualUsd, evidence, clock.GetUtcNow(), request.ActualGbp);
            s.UsageReconciliations.Add(result.Id, result); return result;
        }, ct);
    }
}
