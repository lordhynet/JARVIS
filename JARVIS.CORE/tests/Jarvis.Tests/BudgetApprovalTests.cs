using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class BudgetApprovalTests
{
    private const string OwnerKey = "fixture-owner-management-only-123456789";
    private static void Configure(IWebHostBuilder builder, MutableBudgetSource source, BudgetClock clock, bool owner = true)
    {
        builder.ConfigureServices(s => { s.AddSingleton<IBudgetPolicySource>(source); s.AddSingleton<TimeProvider>(clock); });
        if (owner) { builder.UseSetting("BudgetManagement:OwnerKey", OwnerKey); builder.UseSetting("BudgetManagement:SubjectId", "fixture-owner"); }
        else { builder.UseSetting("BudgetManagement:OwnerKey", ""); builder.UseSetting("BudgetManagement:SubjectId", ""); }
    }
    [Fact]
    public async Task Approval_API_requires_separate_owner_header_and_never_trusts_query_credentials()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now));
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => Configure(b, source, clock));
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var status = (await client.GetFromJsonAsync<GbpBudgetStatus>("/api/budget/status"))!;
        var request = new BudgetApprovalRequest(Guid.NewGuid(), status.PolicyHash, status.Month, 200, clock.Now.AddMinutes(10), status.Revision, "APPROVE MONTHLY AI CEILING");
        using var denied = await client.PostAsJsonAsync("/api/budget/approvals", request); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var query = await client.PostAsJsonAsync("/api/budget/approvals?X-Jarvis-Owner-Key=" + OwnerKey, request); Assert.Equal(HttpStatusCode.Forbidden, query.StatusCode);
        client.DefaultRequestHeaders.Add("X-Jarvis-Owner-Key", "forged");
        using var forged = await client.PostAsJsonAsync("/api/budget/approvals", request); Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Jarvis-Owner-Key"); client.DefaultRequestHeaders.Add("X-Jarvis-Owner-Key", OwnerKey);
        using var approved = await client.PostAsJsonAsync("/api/budget/approvals", request); approved.EnsureSuccessStatusCode();
        var json = await approved.Content.ReadAsStringAsync(); Assert.DoesNotContain(OwnerKey, json);
        Assert.Equal(200m, (await client.GetFromJsonAsync<GbpBudgetStatus>("/api/budget/status"))!.CeilingGbp);
        using var invalid = await client.PostAsJsonAsync("/api/budget/approvals", request with { RequestId = Guid.NewGuid(), CeilingGbp = 201, ExpectedRevision = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    [Fact]
    public async Task Missing_management_setup_is_disabled_and_ordinary_key_cannot_reconcile_spending_away()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now));
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => Configure(b, source, clock, false));
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        using var denied = await client.PostAsJsonAsync("/api/usage/charges/" + Guid.NewGuid() + "/reconcile",
            new ReconcileUsageRequest(Guid.NewGuid(), 0, "untrusted claim", "CONFIRM VERIFIED CHARGE"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        var state = host.Services.GetRequiredService<IRuntimeStore>(); Assert.Empty(await state.ReadAsync(s => s.UsageReconciliations));
        var app = host.Services.GetRequiredService<BudgetApprovalService>();
        await Assert.ThrowsAsync<DomainException>(() => app.ApproveAsync(new("untrusted", []),
            new(Guid.NewGuid(), "", "", 200, clock.Now.AddMinutes(1), 0, "APPROVE MONTHLY AI CEILING")));
    }
    [Fact]
    public async Task Stale_or_mismatched_approval_previews_and_wrong_environment_are_rejected()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now)); var state = new InMemoryRuntimeStore();
        var approvals = new BudgetApprovalService(state, source, clock); var status = await approvals.StatusAsync();
        var request = new BudgetApprovalRequest(Guid.NewGuid(), status.PolicyHash, status.Month, 150m, clock.Now.AddMinutes(10), 0, "APPROVE MONTHLY AI CEILING");
        foreach (var changed in new[] { request with { PolicyHash = "stale" }, request with { ExpectedRevision = 1 },
            request with { Month = "2026-12" }, request with { ExpiresAt = clock.Now }, request with { Confirmation = "yes" } })
            await Assert.ThrowsAsync<DomainException>(() => approvals.ApproveAsync(TestBudgetSource.Owner, changed));
        source.Policy = source.Policy with { Environment = "Development" };
        await Assert.ThrowsAsync<DomainException>(() => approvals.ApproveAsync(TestBudgetSource.Owner, request with { PolicyHash = GbpBudgetRules.PolicyHash(source.Policy) }));
        Assert.Empty(await state.ReadAsync(s => s.BudgetApprovals));
    }
    [Fact]
    public async Task Approval_expiry_and_policy_revision_remove_extra_authority_without_a_refund()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now)); var state = new InMemoryRuntimeStore();
        var approvals = new BudgetApprovalService(state, source, clock); var status = await approvals.StatusAsync();
        await approvals.ApproveAsync(TestBudgetSource.Owner, new(Guid.NewGuid(), status.PolicyHash, status.Month, 200, clock.Now.AddMinutes(5), 0, "APPROVE MONTHLY AI CEILING"));
        Assert.Equal(200m, (await approvals.StatusAsync()).CeilingGbp);
        source.Policy = source.Policy with { Version = "policy-v2" }; Assert.Equal(100m, (await approvals.StatusAsync()).CeilingGbp);
        source.Policy = source.Policy with { Version = "fixture-v1" }; clock.Now = clock.Now.AddMinutes(6);
        Assert.Equal(100m, (await approvals.StatusAsync()).CeilingGbp);
    }
    [Fact]
    public async Task Real_privacy_boundary_allows_late_numeric_settlement_but_preserves_GBP_commitments()
    {
        var clock = new BudgetClock(); var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now));
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => Configure(b, source, clock));
        var budget = host.Services.GetRequiredService<UsageBudget>(); var id = Guid.NewGuid();
        await budget.ReserveAsync(id, Guid.NewGuid(), "fixture", 1m, new(10, 100, 10), default);
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), PrivacyData.Hash(JarvisApiFactory.TestKey), clock.Now);
        await budget.SettleAsync(id, .75m, 1, 1, default);
        var exported = await privacy.ExportAsync(clock.Now); Assert.True(exported.Privacy.Erased);
        Assert.Equal(.75m, Assert.Single(exported.GbpCommitments!).AccountedGbp); Assert.Single(exported.Usage);
        Assert.Empty(exported.Sessions); Assert.Empty(exported.Stories);
    }
}
