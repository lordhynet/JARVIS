using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task GBP_reservations_are_atomic_across_hosts_and_failed_settlement_cannot_partially_commit()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new BudgetClock();
        var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now, "Development"));
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var left = a.WithWebHostBuilder(w => w.ConfigureServices(s => { s.AddSingleton<IBudgetPolicySource>(source); s.AddSingleton<TimeProvider>(clock); }));
        using var right = b.WithWebHostBuilder(w => w.ConfigureServices(s => { s.AddSingleton<IBudgetPolicySource>(source); s.AddSingleton<TimeProvider>(clock); }));
        using var x = left.CreateClient(); using var y = right.CreateClient();
        var l = left.Services.GetRequiredService<UsageBudget>(); var r = right.Services.GetRequiredService<UsageBudget>();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i => {
            try { await (i % 2 == 0 ? l : r).ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .75m,
                new(1000, 1000, 1000, 1000, 1000), default, i % 2 == 0 ? UsageLane.Voice : UsageLane.Background, i % 2 == 0 ? "openai" : "anthropic"); return true; }
            catch (DomainException e) when (e.StatusCode == 429) { return false; }
        }));
        Assert.Equal(13, attempts.Count(ok => ok));
        var reader = right.Services.GetRequiredService<IRuntimeReader>(); var charges = await reader.ReadAsync(s => s.UsageReservations.Values.ToArray());
        Assert.Equal(13, charges.Length); Assert.Equal(13, await reader.ReadAsync(s => s.GbpCommitments.Count));
        Assert.Equal(9.75m, (await right.Services.GetRequiredService<BudgetApprovalService>().StatusAsync()).CommittedGbp);
        var id = charges[0].Id;
        await Assert.ThrowsAsync<DomainException>(() => l.SettleAsync(id, decimal.MaxValue, 1, 1, default));
        Assert.Null(await reader.ReadAsync(s => s.UsageReservations[id].ActualUsd));
        Assert.Null(await reader.ReadAsync(s => s.GbpCommitments[id].AccountedGbp));
        using var restarted = new PostgresApiFactory(database.ConnectionString);
        using var restored = restarted.WithWebHostBuilder(w => w.ConfigureServices(s => { s.AddSingleton<IBudgetPolicySource>(source); s.AddSingleton<TimeProvider>(clock); }));
        using var z = restored.CreateClient();
        Assert.Equal(9.75m, (await restored.Services.GetRequiredService<BudgetApprovalService>().StatusAsync()).CommittedGbp);
    }
    [PostgresFact]
    public async Task GBP_approval_revocation_serializes_with_concurrent_dispatches_and_never_erases_commitments()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new BudgetClock();
        var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now));
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var left = a.WithWebHostBuilder(w => w.ConfigureServices(s => { s.AddSingleton<IBudgetPolicySource>(source); s.AddSingleton<TimeProvider>(clock); }));
        using var right = b.WithWebHostBuilder(w => w.ConfigureServices(s => { s.AddSingleton<IBudgetPolicySource>(source); s.AddSingleton<TimeProvider>(clock); }));
        using var x = left.CreateClient(); using var y = right.CreateClient();
        var budget = left.Services.GetRequiredService<UsageBudget>(); var service = right.Services.GetRequiredService<BudgetApprovalService>();
        var loose = new BudgetLimits(1000, 1000, 1000); await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", 100, loose, default);
        var preview = await service.StatusAsync();
        var approval = await service.ApproveAsync(TestBudgetSource.Owner, new(Guid.NewGuid(), preview.PolicyHash, preview.Month, 150, clock.Now.AddMinutes(10), preview.Revision, "APPROVE MONTHLY AI CEILING"));
        var dispatches = Enumerable.Range(0, 10).Select(async _ => {
            try { await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", 1, loose, default); return true; }
            catch (DomainException e) when (e.StatusCode == 429) { return false; }
        }).ToArray();
        await service.RevokeAsync(TestBudgetSource.Owner, approval.Id, new(Guid.NewGuid(), 1, "REVOKE AI CEILING APPROVAL"));
        var completed = await Task.WhenAll(dispatches); var final = await service.StatusAsync();
        Assert.Equal(100m, final.CeilingGbp); Assert.Equal(100m + completed.Count(ok => ok), final.CommittedGbp);
        await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .01m, loose, default));
    }
}
