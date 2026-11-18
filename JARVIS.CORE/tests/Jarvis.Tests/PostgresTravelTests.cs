using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Travel;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Travel_migration_can_upgrade_downgrade_and_reapply_without_changing_existing_runtime()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString, new TravelClock());
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        var before = (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json;
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261118184236_RuntimeHotControlState");
        await db.Database.MigrateAsync();
        Assert.Empty(await db.TravelRoutes.AsNoTracking().ToArrayAsync());
        Assert.Equal("{}", (await db.TravelMonitorState.AsNoTracking().SingleAsync()).Json);
        Assert.Equal(before, (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json);
        await migrator.MigrateAsync("20261118184236_RuntimeHotControlState");
        await db.Database.MigrateAsync();
        Assert.Equal(before, (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json);
    }
    [PostgresFact]
    public async Task Travel_candidate_survives_restart_and_confirmation_outbox_and_erasure_share_D2()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new TravelClock(); var source = new FakeTravelSource(clock); var route = TravelRig.Route();
        using (var first = new PostgresApiFactory(database.ConnectionString, clock)) {
            var store = first.Services.GetRequiredService<ITravelStore>(); await store.UpdateAsync((s, _) => { s.Routes[route.Id] = route; return true; });
            Assert.True(await new TravelMonitor(store, source, clock, new TravelPolicyFixture()).PollAsync());
            Assert.Equal(1, await store.ReadAsync(s => s.Lines["jubilee"].CandidateCount));
            Assert.Empty(await first.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AgentOutbox));
        }
        clock.Now = clock.Now.AddMinutes(2);
        using var restarted = new PostgresApiFactory(database.ConnectionString, clock); var travel = restarted.Services.GetRequiredService<ITravelStore>();
        Assert.True(await new TravelMonitor(travel, source, clock, new TravelPolicyFixture()).PollAsync());
        var runtime = restarted.Services.GetRequiredService<IRuntimeStore>(); Assert.Single(await runtime.ReadAsync(s => s.AgentOutbox.Values.ToArray()));
        var factory = restarted.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.TravelRoutes.ToArrayAsync()); Assert.DoesNotContain(route.Name, (await db.RuntimeDocuments.SingleAsync()).Json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => travel.UpdateAsync<bool>((s, state) => { s.Routes.Clear(); state.AgentOutbox.Clear(); throw new InvalidOperationException("Synthetic rollback"); }));
        Assert.Single(await travel.ReadAsync(s => s.Routes.Values.ToArray())); Assert.Single(await runtime.ReadAsync(s => s.AgentOutbox.Values.ToArray()));
        using var owner = restarted.AuthorisedClient(); var status = (await owner.GetFromJsonAsync<PrivacyStatus>("/api/privacy/status"))!;
        using var erased = await owner.PostAsJsonAsync("/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase)); erased.EnsureSuccessStatusCode();
        db.ChangeTracker.Clear(); Assert.Empty(await db.TravelRoutes.ToArrayAsync()); Assert.Empty(await db.TravelLineStates.ToArrayAsync()); Assert.Equal("{}", (await db.TravelMonitorState.SingleAsync()).Json);
    }
}
