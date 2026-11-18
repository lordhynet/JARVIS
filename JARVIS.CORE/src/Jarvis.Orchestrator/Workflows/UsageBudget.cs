using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

public sealed record BudgetLimits(decimal DailyUsd, decimal MonthlyUsd, decimal BackgroundUsd, decimal VoiceUsd = 30,
    decimal InteractiveUsd = 5, decimal? VoiceSeriousDailyGbp = null);

// The existing PostgreSQL runtime row lock serializes reservations across hosts/devices.
// Unknown outcomes retain their maximum charge; a crashed caller never refunds money.
public sealed class UsageBudget(IRuntimeStore store, TimeProvider clock, IBudgetPolicySource policies)
{
    public void RequireConfigured() => GbpBudgetRules.Validate(policies.Read(), clock.GetUtcNow());
    public decimal EstimateGbp(decimal usd)
    {
        var policy = policies.Read();
        GbpBudgetRules.Validate(policy, clock.GetUtcNow());
        return GbpBudgetRules.Convert(usd, policy);
    }

    public Task<UsageReservation> ReserveAsync(Guid id, Guid runId, string model, decimal maximum,
        BudgetLimits limits, CancellationToken ct, UsageLane lane = UsageLane.Background, string provider = ProviderIds.Anthropic) =>
        ReserveCoreAsync(id, runId, model, maximum, limits, ct, lane, provider, null, null, null);

    public Task<UsageReservation> ReserveAsync(Guid id, Guid sessionId, ModelRoute route, VoiceMode mode, decimal maximum,
        BudgetLimits limits, CancellationToken ct, decimal? confirmedWindowGbp = null) =>
        ReserveCoreAsync(id, sessionId, route.Model, maximum, limits, ct, UsageLane.Voice, route.Provider, mode, route, confirmedWindowGbp);

    private Task<UsageReservation> ReserveCoreAsync(Guid id, Guid runId, string model, decimal maximum,
        BudgetLimits limits, CancellationToken ct, UsageLane lane, string provider, VoiceMode? mode, ModelRoute? route,
        decimal? confirmedWindowGbp) => store.UpdateAsync(s =>
    {
        Guard.Require(maximum > 0 && limits.DailyUsd > 0 && limits.MonthlyUsd > 0,
            503, "Configure positive paid AI budgets and token rates before enabling the provider.");
        Guard.Require(!s.AgentControls.EffectivePaidAiPaused, 503, "Paid AI is globally paused.");
        Guard.Require(!s.UsageReservations.ContainsKey(id), 409, "This provider dispatch was already reserved; do not replay it.");
        Guard.Require(route is null || !s.ProviderAttempts.ContainsKey(id), 409, "This voice provider attempt already exists.");
        Guard.Require(!s.UsageReservations.Values.Any(x => x.ReconciliationRequired), 503,
            "A provider rate or usage mismatch requires reconciliation before further paid work.");
        var now = clock.GetUtcNow();
        var policy = policies.Read(s); GbpBudgetRules.Validate(policy, now); GbpBudgetRules.BindScope(s, policy);
        Guard.Require(GbpBudgetRules.Unclassified(s, now) == 0, 503, "Legacy AI charges require explicit owner classification before new paid dispatch.");
        var gbp = GbpBudgetRules.Convert(maximum, policy);
        Guard.Require(mode is null || Enum.IsDefined(mode.Value), 400, "Unknown voice reservation mode.");
        if (mode == VoiceMode.Serious)
        {
            Guard.Require(confirmedWindowGbp is null || gbp <= confirmedWindowGbp, 409,
                "The Serious voice estimate has increased. Review the current estimate before continuing.");
            Guard.Require(limits.VoiceSeriousDailyGbp is > 0 and <= 200, 503, "Configure a positive Serious voice daily GBP cap.");
            var today = now.UtcDateTime.Date;
            var seriousGbp = s.UsageReservations.Values.Where(entry => entry.VoiceMode == VoiceMode.Serious && entry.At.UtcDateTime.Date == today)
                .Select(entry => s.GbpCommitments.GetValueOrDefault(entry.Id))
                .Where(entry => entry is not null && entry.ScopeId == policy.ScopeId && entry.Environment == policy.Environment)
                .Sum(entry => entry!.AccountedGbp ?? entry.ReservedGbp);
            Guard.Require(seriousGbp + gbp <= limits.VoiceSeriousDailyGbp, 429, "Serious voice daily GBP cap reached.", VoiceEndReasons.SeriousDailyCap);
        }
        Guard.Require(GbpBudgetRules.Committed(s, policy, now) + gbp <= GbpBudgetRules.Ceiling(s, policy, now),
            429, "GBP AI budget exhausted. A valid production approval is required above £100; £200 is the hard maximum.", VoiceEndReasons.GbpBudgetCeiling);
        decimal Cost(UsageReservation r) => r.ActualUsd ?? r.ReservedUsd;
        var month = s.UsageReservations.Values.Where(r => r.At.Year == now.Year && r.At.Month == now.Month).Sum(Cost);
        var day = s.UsageReservations.Values.Where(r => r.At.UtcDateTime.Date == now.UtcDateTime.Date).Sum(Cost);
        var laneCost = s.UsageReservations.Values.Where(r => r.At.Year == now.Year && r.At.Month == now.Month && r.Lane == lane).Sum(Cost);
        var laneLimit = lane switch { UsageLane.Voice => limits.VoiceUsd, UsageLane.Interactive => limits.InteractiveUsd, _ => limits.BackgroundUsd };
        Guard.Require(laneLimit > 0 && day + maximum <= limits.DailyUsd && month + maximum <= limits.MonthlyUsd && laneCost + maximum <= laneLimit,
            429, "Paid AI budget exhausted. Existing results and unpaid practice remain available.");
        var reservation = new UsageReservation(id, runId, model, now, maximum, Lane: lane, Provider: provider, VoiceMode: mode);
        s.UsageReservations.Add(id, reservation);
        s.GbpCommitments.Add(id, GbpBudgetRules.Commitment(reservation, policy));
        if (route is not null)
            s.ProviderAttempts.Add(id, new(id, runId, "", route, id, ProviderAttemptOutcome.Preparing, now, VoiceMode: mode));
        return reservation;
    }, ct);

    public Task<UsageReservation> SettleAsync(Guid id, decimal actual, int input, int output, CancellationToken ct) =>
        store.SettleUsageAsync(id, actual, input, output, ct, completedAt: clock.GetUtcNow());

    public Task MarkVoiceDispatchedAsync(Guid id, CancellationToken ct) => store.UpdateAsync(s =>
    {
        Guard.Require(!s.AgentControls.EffectivePaidAiPaused, 503, "Paid AI is globally paused.");
        Guard.Require(!s.UsageReservations.Values.Any(entry => entry.ReconciliationRequired), 503,
            "A provider rate or usage mismatch requires reconciliation before further paid work.");
        Guard.Require(s.ProviderAttempts.TryGetValue(id, out var attempt) && attempt.VoiceMode is not null &&
            attempt.Outcome == ProviderAttemptOutcome.Preparing && s.UsageReservations.GetValueOrDefault(id) is { ActualUsd: null },
            409, "An initial unsettled voice reservation is required before provider dispatch.");
        s.ProviderAttempts[id] = attempt with { Outcome = ProviderAttemptOutcome.Dispatched };
        return true;
    }, ct);

    public Task MarkVoiceUncertainAsync(Guid id, CancellationToken ct) => store.UpdateAsync(s =>
    {
        if (s.ProviderAttempts.GetValueOrDefault(id) is { VoiceMode: not null, Outcome: ProviderAttemptOutcome.Dispatched } attempt)
            s.ProviderAttempts[id] = attempt with { Outcome = ProviderAttemptOutcome.Uncertain, CompletedAt = clock.GetUtcNow() };
        return true;
    }, ct);
}

// Settlement updates the existing monetary record and existing attempt metadata without recreating source data.
public static class UsageAccounting
{
    public static UsageReservation Settle(RuntimeState s, Guid id, decimal actual, int input, int output, DateTimeOffset? completedAt = null)
    {
        Guard.Require(s.UsageReservations.TryGetValue(id, out var prior), 404, "Usage reservation not found.");
        Guard.Require(actual >= 0 && input >= 0 && output >= 0, 502, "Provider returned invalid usage.");
        if (prior.ActualUsd is not null)
        {
            Guard.Require(prior.ActualUsd == actual && prior.InputTokens == input && prior.OutputTokens == output,
                409, "Usage was already settled differently.");
            return prior;
        }
        var settled = prior with { ActualUsd = actual, InputTokens = input, OutputTokens = output,
            ReconciliationRequired = actual > prior.ReservedUsd };
        s.UsageReservations[id] = settled;
        if (s.GbpCommitments.TryGetValue(id, out var commitment))
            s.GbpCommitments[id] = commitment with { AccountedGbp = GbpBudgetRules.Convert(actual, commitment.Fx, commitment.TaxMultiplier, commitment.SafetyMultiplier) };
        if (s.ProviderAttempts.GetValueOrDefault(id) is { VoiceMode: not null } attempt)
            s.ProviderAttempts[id] = attempt with
            {
                Outcome = attempt.Outcome == ProviderAttemptOutcome.Preparing ? ProviderAttemptOutcome.Refused : ProviderAttemptOutcome.Completed,
                CompletedAt = completedAt ?? TimeProvider.System.GetUtcNow()
            };
        return settled;
    }
}
