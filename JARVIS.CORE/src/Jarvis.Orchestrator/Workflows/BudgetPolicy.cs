using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

public interface IBudgetPolicySource
{
    GbpBudgetPolicy Read();
    GbpBudgetPolicy Read(RuntimeState state) => Read();
}

public static class GbpBudgetRules
{
    public static string Month(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
    public static void ValidateIdentity(GbpBudgetPolicy p)
    {
        Guard.Require(p.Enabled, 503, "GBP budget authority is disabled; configure it before paid dispatch.");
        Guard.Require(p.ScopeId is { Length: > 0 and <= 80 } && p.ScopeId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
            p.Environment is "Development" or "Production" && p.Version is { Length: > 0 and <= 80 }, 503, "GBP budget scope, environment or policy version is invalid.");
        Guard.Require(p.TaxMultiplier is >= 1 and <= 10 && p.SafetyMultiplier is >= 1 and <= 10, 503,
            "Configure explicit AI tax and FX safety multipliers; no billing assumptions are automatic.");
    }
    public static void Validate(GbpBudgetPolicy p, DateTimeOffset now)
    {
        ValidateIdentity(p);
        Guard.Require(IsCurrentQuote(p.Fx, now), 503, "A sourced, current USD-to-GBP quote valid for at most 24 hours is required." +
            (p.FxStatus switch {
                { AutomaticEnabled: false } => " Automatic FX refresh is disabled.",
                { LastError: { } error } => " Automatic FX refresh failed. " + error,
                { AutomaticEnabled: true } => " No current automatic quote is available yet.",
                _ => ""
            }));
    }
    public static bool IsCurrentQuote(BudgetFxQuote? quote, DateTimeOffset now) =>
        quote is { UsdToGbp: > 0 and <= 1000 } q && q.Source is { Length: > 0 and <= 200 } &&
        q.ObservedAt <= now && q.ExpiresAt > now && q.ExpiresAt > q.ObservedAt && q.ExpiresAt - q.ObservedAt <= TimeSpan.FromHours(24);
    public static string PolicyHash(GbpBudgetPolicy p) => PrivacyData.Hash(JsonSerializer.Serialize(new { p.ScopeId, p.Environment, p.Version, p.TaxMultiplier, p.SafetyMultiplier }));
    public static string ConversionHash(GbpBudgetPolicy p) => PrivacyData.Hash(JsonSerializer.Serialize(new { Policy = PolicyHash(p), p.Fx }));
    public static decimal Convert(decimal usd, BudgetFxQuote quote, decimal tax, decimal safety)
    {
        Guard.Require(usd >= 0, 502, "Provider charge cannot be negative.");
        try { return decimal.Ceiling(checked(usd * quote.UsdToGbp * tax * safety * 100m)) / 100m; }
        catch (OverflowException) { throw new DomainException(503, "AI charge conversion exceeded the supported numeric bound."); }
    }
    public static decimal Convert(decimal usd, GbpBudgetPolicy p) => Convert(usd, p.Fx!, p.TaxMultiplier!.Value, p.SafetyMultiplier!.Value);
    public static bool ValidApproval(RuntimeState s, GbpBudgetPolicy p, BudgetApproval a, DateTimeOffset now) =>
        !a.Revoked && a.Epoch == s.BudgetEpoch && a.ScopeId == p.ScopeId && a.Environment == "Production" && p.Environment == "Production" &&
        a.Month == Month(now) && a.PolicyHash == PolicyHash(p) && a.ExpiresAt > now && a.CeilingGbp is > 100 and <= 200;
    public static decimal Ceiling(RuntimeState s, GbpBudgetPolicy p, DateTimeOffset now) => p.Environment == "Development" ? 10m : p.Environment == "Production" ?
        s.BudgetApprovals.Values.Where(a => ValidApproval(s, p, a, now)).Select(a => a.CeilingGbp).DefaultIfEmpty(100m).Max() : 0m;
    public static decimal Committed(RuntimeState s, GbpBudgetPolicy p, DateTimeOffset now) => s.GbpCommitments.Values
        .Where(c => c.ScopeId == p.ScopeId && c.Environment == p.Environment && c.Month == Month(now)).Sum(c => c.AccountedGbp ?? c.ReservedGbp);
    public static int Unclassified(RuntimeState s, DateTimeOffset now) => s.UsageReservations.Values.Count(r => Month(r.At) == Month(now) && !s.GbpCommitments.ContainsKey(r.Id));
    public static void BindScope(RuntimeState s, GbpBudgetPolicy p)
    {
        Guard.Require(s.BudgetScopeId is null || s.BudgetScopeId == p.ScopeId, 503, "Budget accounting scope changed; an explicit accounting migration is required.");
        s.BudgetScopeId ??= p.ScopeId;
    }
    public static GbpCommitment Commitment(UsageReservation reservation, GbpBudgetPolicy p, bool legacy = false) => new(reservation.Id,
        p.ScopeId, p.Environment, Month(reservation.At), PolicyHash(p), p.Fx!, p.TaxMultiplier!.Value, p.SafetyMultiplier!.Value,
        Convert(reservation.ReservedUsd, p), reservation.ActualUsd is decimal actual ? Convert(actual, p) : null, legacy);
}
