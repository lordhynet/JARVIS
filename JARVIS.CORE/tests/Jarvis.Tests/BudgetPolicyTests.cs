using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed class BudgetClock : TimeProvider
{
    public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}
public sealed class MutableBudgetSource(GbpBudgetPolicy policy) : IBudgetPolicySource
{
    public GbpBudgetPolicy Policy = policy;
    public GbpBudgetPolicy Read() => Policy;
    public static GbpBudgetPolicy Fixture(DateTimeOffset now, string environment = "Production") =>
        new(true, "fixture-account", environment, "fixture-v1", new("synthetic-test-only", 1m, now.AddMinutes(-1), now.AddHours(1)), 1m, 1m);
}
public sealed class BudgetPolicyTests
{
    private static readonly BudgetLimits Loose = new(10000, 10000, 10000, 10000, 10000);
    [Theory]
    [InlineData("Development", 10)]
    [InlineData("Production", 100)]
    public async Task Monthly_authority_is_central_and_callers_cannot_raise_it(string environment, int ceiling)
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now, environment));
        var state = new InMemoryRuntimeStore(); var budget = new UsageBudget(state, clock, source);
        await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", ceiling, Loose, default, UsageLane.Voice, "openai");
        var denied = await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "other", .01m, Loose, default, UsageLane.Background, "anthropic"));
        Assert.Equal(429, denied.StatusCode); Assert.Single(await state.ReadAsync(s => s.UsageReservations));
        Assert.Single(await state.ReadAsync(s => s.GbpCommitments));
    }
    [Fact]
    public async Task Production_increase_is_total_ceiling_and_revocation_or_expiry_never_refunds_spend()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now)); var state = new InMemoryRuntimeStore();
        var budget = new UsageBudget(state, clock, source); var approvals = new BudgetApprovalService(state, source, clock);
        var first = Guid.NewGuid(); await budget.ReserveAsync(first, Guid.NewGuid(), "fixture", 100, Loose, default);
        var preview = await approvals.StatusAsync();
        var request = new BudgetApprovalRequest(Guid.NewGuid(), preview.PolicyHash, preview.Month, 150m, clock.Now.AddMinutes(10), preview.Revision, "APPROVE MONTHLY AI CEILING");
        var approved = await approvals.ApproveAsync(TestBudgetSource.Owner, request);
        Assert.Equal(approved, await approvals.ApproveAsync(TestBudgetSource.Owner, request));
        await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", 50, Loose, default);
        Assert.Equal(150m, (await approvals.StatusAsync()).CommittedGbp);
        await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .01m, Loose, default));
        await approvals.RevokeAsync(TestBudgetSource.Owner, approved.Id, new(Guid.NewGuid(), 1, "REVOKE AI CEILING APPROVAL"));
        Assert.Equal(100m, (await approvals.StatusAsync()).CeilingGbp); Assert.Equal(150m, (await approvals.StatusAsync()).CommittedGbp);
        // Settlement accounts known usage at the original quote, even after policy FX changes.
        source.Policy = source.Policy with { Fx = source.Policy.Fx! with { UsdToGbp = 2m } };
        await budget.SettleAsync(first, 80m, 1, 1, default);
        Assert.Equal(130m, (await approvals.StatusAsync()).CommittedGbp);
    }
    [Theory]
    [InlineData(201)]
    [InlineData(100)]
    public async Task Invalid_ceiling_cannot_be_approved(int ceiling)
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now)); var state = new InMemoryRuntimeStore();
        var approvals = new BudgetApprovalService(state, source, clock);
        var error = await Assert.ThrowsAsync<DomainException>(() => approvals.ApproveAsync(TestBudgetSource.Owner,
            new(Guid.NewGuid(), GbpBudgetRules.PolicyHash(source.Policy), GbpBudgetRules.Month(clock.Now), ceiling, clock.Now.AddMinutes(10), 0, "APPROVE MONTHLY AI CEILING")));
        Assert.Equal(400, error.StatusCode); Assert.Empty(await state.ReadAsync(s => s.BudgetApprovals));
    }
    [Fact]
    public async Task Missing_expired_future_and_oversized_FX_or_unset_tax_policy_block_before_any_commit()
    {
        var clock = new BudgetClock(); var valid = MutableBudgetSource.Fixture(clock.Now);
        foreach (var policy in new[] { valid with { Enabled = false }, valid with { Fx = null }, valid with { TaxMultiplier = null },
            valid with { SafetyMultiplier = null }, valid with { Environment = "caller-invented" },
            valid with { Fx = valid.Fx! with { ObservedAt = clock.Now.AddSeconds(1) } },
            valid with { Fx = valid.Fx! with { ExpiresAt = clock.Now } },
            valid with { Fx = valid.Fx! with { ExpiresAt = clock.Now.AddDays(2) } } })
        {
            var state = new InMemoryRuntimeStore(); var budget = new UsageBudget(state, clock, new MutableBudgetSource(policy));
            Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .1m, Loose, default))).StatusCode);
            Assert.Empty(await state.ReadAsync(s => s.UsageReservations)); Assert.Empty(await state.ReadAsync(s => s.GbpCommitments));
        }
    }
    [Fact]
    public async Task Currency_rounding_uses_pence_and_new_scope_cannot_reset_allowance()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now, "Development")); var state = new InMemoryRuntimeStore();
        var budget = new UsageBudget(state, clock, source);
        await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .00001m, Loose, default);
        Assert.Equal(.01m, Assert.Single(await state.ReadAsync(s => s.GbpCommitments.Values.ToArray())).ReservedGbp);
        source.Policy = source.Policy with { ScopeId = "new-allowance" };
        Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .1m, Loose, default))).StatusCode);
    }
    [Fact]
    public async Task Legacy_charges_block_until_explicit_classification_and_are_never_repriced_by_config_change()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now)); var state = new InMemoryRuntimeStore();
        var budget = new UsageBudget(state, clock, source); var approvals = new BudgetApprovalService(state, source, clock); var id = Guid.NewGuid();
        await state.UpdateAsync(s => { s.UsageReservations[id] = new(id, Guid.NewGuid(), "legacy", clock.Now, 2m); return true; });
        await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .1m, Loose, default));
        var status = await approvals.StatusAsync(); Assert.False(status.Ready); Assert.Equal(1, status.UnclassifiedCharges);
        var request = new ClassifyLegacyChargeRequest(Guid.NewGuid(), status.PolicyHash, status.ConversionHash, 2m, "fixture historical billing review", "ASSIGN LEGACY AI CHARGE");
        var classified = await approvals.ClassifyAsync(TestBudgetSource.Owner, id, request);
        Assert.Equal(classified, await approvals.ClassifyAsync(TestBudgetSource.Owner, id, request)); Assert.True(classified.Legacy);
        await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", 1m, Loose, default);
        Assert.Equal(3m, (await approvals.StatusAsync()).CommittedGbp);
    }
    [Fact]
    public async Task Erasure_preserves_accounting_revokes_authority_and_fences_old_approval_requests()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now)); var state = new InMemoryRuntimeStore();
        var approvals = new BudgetApprovalService(state, source, clock); var budget = new UsageBudget(state, clock, source);
        var status = await approvals.StatusAsync(); var request = new BudgetApprovalRequest(Guid.NewGuid(), status.PolicyHash, status.Month, 200, clock.Now.AddMinutes(10), 0, "APPROVE MONTHLY AI CEILING");
        await approvals.ApproveAsync(TestBudgetSource.Owner, request); var id = Guid.NewGuid();
        await budget.ReserveAsync(id, Guid.NewGuid(), "fixture", 150, Loose, default);
        var erased = await state.ReadAsync(PrivacyData.EmptyAfterErase); state.ReplacePrivacyData(erased);
        Assert.Equal(150m, (await approvals.StatusAsync()).CommittedGbp); Assert.Equal(100m, (await approvals.StatusAsync()).CeilingGbp);
        Assert.All(await state.ReadAsync(s => s.BudgetApprovals.Values.ToArray()), a => { Assert.True(a.Revoked); Assert.Equal("", a.ActorId); });
        await Assert.ThrowsAsync<DomainException>(() => approvals.ApproveAsync(TestBudgetSource.Owner, request));
        await budget.SettleAsync(id, 120m, 1, 1, default); Assert.Equal(120m, (await approvals.StatusAsync()).CommittedGbp);
    }
    [Fact]
    public async Task UTC_month_rollover_does_not_carry_an_approval_into_next_month()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now)); var state = new InMemoryRuntimeStore();
        var approvals = new BudgetApprovalService(state, source, clock); var status = await approvals.StatusAsync();
        await approvals.ApproveAsync(TestBudgetSource.Owner, new(Guid.NewGuid(), status.PolicyHash, status.Month, 200, clock.Now.AddMinutes(5), 0, "APPROVE MONTHLY AI CEILING"));
        clock.Now = clock.Now.AddMonths(1); source.Policy = MutableBudgetSource.Fixture(clock.Now);
        Assert.Equal(100m, (await approvals.StatusAsync()).CeilingGbp); Assert.Empty((await approvals.StatusAsync()).Approvals);
    }
    [Fact]
    public void Configuration_cannot_raise_the_hardcoded_authorization_ceiling()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Budget:Enabled"] = "true", ["Budget:MonthlyLimitGbp"] = "999" }).Build();
        Assert.Throws<DomainException>(() => new BudgetSettings(config).Read());
    }
    [Theory]
    [InlineData("Environment")]
    [InlineData("ScopeId")]
    public void Missing_trusted_accounting_identity_is_not_inferred_from_the_host_or_caller(string missing)
    {
        var clock = new BudgetClock(); var values = new Dictionary<string, string?> {
            ["Budget:Enabled"] = "true", ["Budget:ScopeId"] = "fixture-account", ["Budget:Environment"] = "Production",
            ["Budget:TaxMultiplier"] = "1", ["Budget:SafetyMultiplier"] = "1", ["Budget:Fx:Source"] = "synthetic-test-only",
            ["Budget:Fx:UsdToGbp"] = "1", ["Budget:Fx:ObservedAt"] = clock.Now.AddMinutes(-1).ToString("O"),
            ["Budget:Fx:ExpiresAt"] = clock.Now.AddHours(1).ToString("O")
        };
        values.Remove("Budget:" + missing);
        var policy = new BudgetSettings(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).Read();
        Assert.Throws<DomainException>(() => GbpBudgetRules.Validate(policy, clock.Now));
    }
    [Fact]
    public async Task Verified_GBP_billing_correction_preserves_USD_history_and_cannot_be_replayed_or_forged()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now)); var state = new InMemoryRuntimeStore();
        var budget = new UsageBudget(state, clock, source); var id = Guid.NewGuid();
        await budget.ReserveAsync(id, Guid.NewGuid(), "fixture", 1m, Loose, default);
        await budget.SettleAsync(id, .75m, 1, 1, default); clock.Now = clock.Now.AddMinutes(11);
        var reconciler = new UsageReconciler(state, clock);
        var request = new ReconcileUsageRequest(Guid.NewGuid(), .75m, "verified GBP invoice reference", "CONFIRM VERIFIED CHARGE", .90m);
        await Assert.ThrowsAsync<DomainException>(() => reconciler.ReconcileAsync(new("untrusted", []), id, request));
        await Assert.ThrowsAsync<DomainException>(() => reconciler.ReconcileAsync(TestBudgetSource.Owner, id, request with { ActualUsd = .80m }));
        var result = await reconciler.ReconcileAsync(TestBudgetSource.Owner, id, request);
        Assert.Equal(result, await reconciler.ReconcileAsync(TestBudgetSource.Owner, id, request));
        Assert.Equal(.75m, await state.ReadAsync(s => s.UsageReservations[id].ActualUsd));
        Assert.Equal(.90m, await state.ReadAsync(s => s.GbpCommitments[id].AccountedGbp));
        Assert.True(await state.ReadAsync(s => s.GbpCommitments[id].GbpVerified));
        await budget.SettleAsync(id, .75m, 1, 1, default); // A late duplicate must not overwrite verified GBP.
        Assert.Equal(.90m, await state.ReadAsync(s => s.GbpCommitments[id].AccountedGbp));
        await Assert.ThrowsAsync<DomainException>(() => reconciler.ReconcileAsync(TestBudgetSource.Owner, id, request with { RequestId = Guid.NewGuid() }));
    }
}
