using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

public sealed class BudgetApprovalService(IRuntimeStore store, IBudgetPolicySource policies, TimeProvider clock)
{
    public Task<GbpBudgetStatus> StatusAsync(CancellationToken ct = default) => store.ReadAsync(s =>
    {
        var now = clock.GetUtcNow(); var p = policies.Read(); string detail;
        var ready = true;
        try {
            GbpBudgetRules.Validate(p, now);
            Guard.Require(s.BudgetScopeId is null || s.BudgetScopeId == p.ScopeId, 503, "Accounting scope needs explicit migration.");
            Guard.Require(GbpBudgetRules.Unclassified(s, now) == 0, 503, "Legacy AI charges require owner classification before paid dispatch.");
            Guard.Require(!s.AgentControls.Paused && !s.UsageReservations.Values.Any(r => r.ReconciliationRequired), 503, "Paid work is paused or requires charge reconciliation.");
            detail = "GBP authority ready. Existing USD daily/month/lane limits remain additional restrictions.";
        }
        catch (DomainException e) { ready = false; detail = e.Message; }
        var ceiling = GbpBudgetRules.Ceiling(s, p, now); var committed = GbpBudgetRules.Committed(s, p, now);
        if (ready && committed >= ceiling) { ready = false; detail = "GBP AI budget exhausted; no new paid dispatch is authorized."; }
        return new GbpBudgetStatus(ready, detail, p.ScopeId, p.Environment, GbpBudgetRules.Month(now), GbpBudgetRules.PolicyHash(p),
            GbpBudgetRules.ConversionHash(p), ceiling, committed, GbpBudgetRules.Unclassified(s, now), s.BudgetRevision,
            s.BudgetApprovals.Values.Where(a => a.ScopeId == p.ScopeId && a.Environment == p.Environment && a.Month == GbpBudgetRules.Month(now)).ToArray(), p);
    }, ct);

    public Task<LegacyChargePreview> PreviewLegacyAsync(BudgetActor actor, Guid id, CancellationToken ct = default)
    {
        BudgetAuthorization.RequireOwner(actor);
        return store.ReadAsync(s => {
            var p = policies.Read(); GbpBudgetRules.Validate(p, clock.GetUtcNow());
            Guard.Require(s.BudgetScopeId is null || s.BudgetScopeId == p.ScopeId, 503, "Budget accounting scope needs explicit migration.");
            Guard.Require(s.UsageReservations.TryGetValue(id, out var charge), 404, "Legacy charge not found.");
            Guard.Require(!s.GbpCommitments.ContainsKey(id), 409, "That charge is already classified.");
            return new LegacyChargePreview(id, charge.ActualUsd ?? charge.ReservedUsd, GbpBudgetRules.ConversionHash(p), GbpBudgetRules.Commitment(charge, p, true));
        }, ct);
    }

    public Task<BudgetApproval> ApproveAsync(BudgetActor actor, BudgetApprovalRequest request, CancellationToken ct = default)
    {
        BudgetAuthorization.RequireOwner(actor);
        Guard.Require(request.RequestId != Guid.Empty && request.Confirmation == "APPROVE MONTHLY AI CEILING", 400, "Explicit monthly AI ceiling confirmation is required.");
        return store.UpdateAsync(s => {
            var now = clock.GetUtcNow(); var p = policies.Read(); GbpBudgetRules.ValidateIdentity(p); GbpBudgetRules.BindScope(s, p);
            var fingerprint = Fingerprint(actor, request);
            if (Replay(s, request.RequestId, "approve", request.RequestId, fingerprint)) return s.BudgetApprovals[request.RequestId];
            Guard.Require(request.PolicyHash == GbpBudgetRules.PolicyHash(p) && request.ExpectedRevision == s.BudgetRevision, 409, "Budget policy or approval state changed. Review the latest preview.");
            Guard.Require(p.Environment == "Production" && request.Month == GbpBudgetRules.Month(now) && request.CeilingGbp is > 100 and <= 200,
                400, "Only this production month's total AI ceiling may be approved above £100 and at most £200.");
            var end = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
            Guard.Require(request.ExpiresAt > now && request.ExpiresAt <= end && request.CeilingGbp * 100 == decimal.Truncate(request.CeilingGbp * 100),
                400, "Approval must expire within the accounting month and use whole pence.");
            Guard.Require(s.BudgetApprovals.Count < 2000, 409, "Budget approval history is full.");
            var approval = new BudgetApproval(request.RequestId, p.ScopeId, p.Environment, request.Month, request.PolicyHash,
                request.CeilingGbp, request.ExpiresAt, s.BudgetEpoch, actor.SubjectId, now);
            s.BudgetApprovals.Add(approval.Id, approval); Record(s, actor, request.RequestId, "approve", approval.Id, fingerprint, now); return approval;
        }, ct);
    }
    public Task<BudgetApproval> RevokeAsync(BudgetActor actor, Guid id, RevokeBudgetApprovalRequest request, CancellationToken ct = default)
    {
        BudgetAuthorization.RequireOwner(actor);
        Guard.Require(request.RequestId != Guid.Empty && request.Confirmation == "REVOKE AI CEILING APPROVAL", 400, "Explicit approval revocation is required.");
        return store.UpdateAsync(s => {
            var fingerprint = Fingerprint(actor, new { id, request });
            if (Replay(s, request.RequestId, "revoke", id, fingerprint)) return s.BudgetApprovals[id];
            Guard.Require(request.ExpectedRevision == s.BudgetRevision, 409, "Budget approval state changed.");
            Guard.Require(s.BudgetApprovals.TryGetValue(id, out var approval), 404, "Budget approval not found.");
            s.BudgetApprovals[id] = approval with { Revoked = true };
            Record(s, actor, request.RequestId, "revoke", id, fingerprint, clock.GetUtcNow()); return s.BudgetApprovals[id];
        }, ct);
    }
    public Task<GbpCommitment> ClassifyAsync(BudgetActor actor, Guid id, ClassifyLegacyChargeRequest request, CancellationToken ct = default)
    {
        BudgetAuthorization.RequireOwner(actor);
        Guard.Require(request.RequestId != Guid.Empty && request.Confirmation == "ASSIGN LEGACY AI CHARGE", 400, "Explicit legacy charge assignment is required.");
        var evidence = Guard.Text(request.Evidence, "Accounting evidence reference", 500);
        return store.UpdateAsync(s => {
            var now = clock.GetUtcNow(); var p = policies.Read(); GbpBudgetRules.Validate(p, now); GbpBudgetRules.BindScope(s, p);
            var fingerprint = Fingerprint(actor, new { id, request });
            if (Replay(s, request.RequestId, "classify", id, fingerprint)) return s.GbpCommitments[id];
            Guard.Require(request.PolicyHash == GbpBudgetRules.PolicyHash(p) && request.ConversionHash == GbpBudgetRules.ConversionHash(p), 409, "Policy or conversion basis changed. Review the latest preview.");
            Guard.Require(s.UsageReservations.TryGetValue(id, out var charge), 404, "Legacy charge not found.");
            Guard.Require(!s.GbpCommitments.ContainsKey(id) && (charge.ActualUsd ?? charge.ReservedUsd) == request.ExpectedUsd, 409, "Charge was classified or its amount changed.");
            var entry = GbpBudgetRules.Commitment(charge, p, true); s.GbpCommitments.Add(id, entry);
            Record(s, actor, request.RequestId, "classify", id, fingerprint, now, evidence); return entry;
        }, ct);
    }
    private static string Fingerprint(BudgetActor actor, object request) => PrivacyData.Hash(JsonSerializer.Serialize(new { actor.SubjectId, request }));
    private static bool Replay(RuntimeState s, Guid id, string kind, Guid target, string fingerprint)
    {
        if (!s.BudgetReceipts.TryGetValue(id, out var receipt)) return false;
        Guard.Require(receipt.Epoch == s.BudgetEpoch && receipt.Kind == kind && receipt.TargetId == target && receipt.Fingerprint == fingerprint,
            409, "Budget operation ID belongs to different input or an earlier authority epoch."); return true;
    }
    private static void Record(RuntimeState s, BudgetActor actor, Guid id, string kind, Guid target, string fingerprint, DateTimeOffset at, string evidence = "")
    {
        Guard.Require(s.BudgetReceipts.Count < 10000, 409, "Budget management history is full.");
        s.BudgetReceipts.Add(id, new(id, kind, target, fingerprint, s.BudgetEpoch, actor.SubjectId, at, evidence));
        s.BudgetRevision = checked(s.BudgetRevision + 1);
    }
}
