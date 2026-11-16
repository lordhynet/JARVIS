using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Paid_usage_budget_serializes_competing_hosts_and_survives_restart()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var a = new PostgresApiFactory(database.ConnectionString);
        using var b = new PostgresApiFactory(database.ConnectionString);
        var budgetA = a.Services.GetRequiredService<UsageBudget>(); var budgetB = b.Services.GetRequiredService<UsageBudget>();
        var accepted = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            try { await (i % 2 == 0 ? budgetA : budgetB).ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .5m, new(1, 50, 15), default); return true; }
            catch (DomainException ex) when (ex.StatusCode == 429) { return false; }
        }));
        Assert.Equal(2, accepted.Count(x => x));
        using var restarted = new PostgresApiFactory(database.ConnectionString);
        var total = await restarted.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.Sum(x => x.ActualUsd ?? x.ReservedUsd));
        Assert.Equal(1m, total);
    }

    [PostgresFact]
    public async Task Workflow_trigger_and_claim_are_unique_across_two_hosts()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var originalA = new PostgresApiFactory(database.ConnectionString);
        using var originalB = new PostgresApiFactory(database.ConnectionString);
        using var a = originalA.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        using var b = originalB.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var clock = new TestWorkflowClock();
        var storeA = a.Services.GetRequiredService<IWorkflowStore>();
        var storeB = b.Services.GetRequiredService<IWorkflowStore>();
        var serviceA = new WorkflowService(storeA, clock);
        var serviceB = new WorkflowService(storeB, clock);
        var request = new CreatePracticePlanRequest(Guid.NewGuid());
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? serviceA : serviceB).CreateAsync(request)));
        Assert.Single(await storeA.ListAsync());
        Assert.Single(await storeB.EventsAsync(request.RequestId, 0));
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            (i % 2 == 0 ? serviceA : serviceB).ClaimAsync($"worker-{i}")));
        var claimed = Assert.Single(claims, x => x is not null)!;
        await serviceB.CompleteAsync(claimed.Id, claimed.ClaimToken!.Value, new([], ["No practice material yet."]));
        Assert.Equal(WorkflowStatus.Succeeded, (await storeA.GetAsync(claimed.Id))!.Status);
        Assert.Equal(new[] { 1, 2, 3 }, (await storeA.EventsAsync(claimed.Id, 0)).Select(x => x.Revision));
        Assert.Single(await storeA.EventsAsync(claimed.Id, 0), x => x.Status == WorkflowStatus.Succeeded);
    }

    [PostgresFact]
    public async Task Workflow_claim_recovers_after_host_restart_and_rejects_the_old_worker()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new TestWorkflowClock();
        WorkflowRun claimed;
        using (var original = new PostgresApiFactory(database.ConnectionString))
        using (var host = original.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false")))
        {
            var service = new WorkflowService(host.Services.GetRequiredService<IWorkflowStore>(), clock);
            await service.CreateAsync(new(Guid.NewGuid()));
            claimed = (await service.ClaimAsync("before-restart"))!;
        }
        clock.Advance(TimeSpan.FromSeconds(31));
        using var next = new PostgresApiFactory(database.ConnectionString);
        using var restarted = next.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var store = restarted.Services.GetRequiredService<IWorkflowStore>();
        var recoveredService = new WorkflowService(store, clock);
        var recovered = (await recoveredService.ClaimAsync("after-restart"))!;
        Assert.Equal(claimed.Id, recovered.Id);
        Assert.Equal(2, recovered.Attempt);
        await Assert.ThrowsAsync<DomainException>(() => recoveredService.CompleteAsync(claimed.Id, claimed.ClaimToken!.Value, new([], [])));
        await recoveredService.CompleteAsync(recovered.Id, recovered.ClaimToken!.Value, new([], []));
        Assert.Equal(4, (await store.EventsAsync(recovered.Id, 0)).Count);
    }

    [PostgresFact]
    public async Task Workflow_mutation_and_events_roll_back_together_and_claims_skip_locked_rows()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var original = new PostgresApiFactory(database.ConnectionString);
        using var host = original.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var store = host.Services.GetRequiredService<IWorkflowStore>();
        var factory = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        var clock = new TestWorkflowClock();
        var service = new WorkflowService(store, clock);
        var first = await service.CreateAsync(new(Guid.NewGuid()));
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await service.CreateAsync(new(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(first.Id,
            _ => throw new InvalidOperationException("Synthetic interrupted mutation")));
        Assert.Single(await store.EventsAsync(first.Id, 0));
        Assert.Equal(WorkflowStatus.Queued, (await store.GetAsync(first.Id))!.Status);
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.WorkflowRuns.FromSqlInterpolated($"""
            SELECT * FROM "WorkflowRuns" WHERE "Id" = {first.Id} FOR UPDATE
            """).ToListAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var claimed = await service.ClaimAsync("nonblocking-worker", timeout.Token);
        Assert.Equal(second.Id, claimed!.Id);
        await tx.RollbackAsync();
    }
}
