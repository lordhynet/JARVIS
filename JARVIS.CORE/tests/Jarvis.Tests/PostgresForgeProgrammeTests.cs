using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    private static HttpClient ForgeOwner(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        TestOwnerManagement.Authorize(client); return client;
    }
    private static async Task<ForgeProgramme> CreateForge(HttpClient client, Guid id)
    {
        using var response = await client.PostAsJsonAsync(ForgeProgrammeTests.Root, new CreateForgeProgrammeRequest(id)); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ForgeProgramme>())!;
    }

    [PostgresFact]
    public async Task Forge_concurrent_create_across_hosts_and_restart_preserves_one_identity_and_interview_state()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock(); var id = Guid.NewGuid();
        using var baseFirst = new PostgresApiFactory(database.ConnectionString, clock);
        using var first = baseFirst.WithWebHostBuilder(b => b.UseSetting("Forge:Enabled", "true")); using var owner = ForgeOwner(first);
        using var started = await owner.PostAsJsonAsync("/api/programme", new StartProgrammeRequest(Guid.NewGuid(), "2026-11-01")); started.EnsureSuccessStatusCode();
        var before = await owner.GetStringAsync("/api/programme");
        using var baseSecond = new PostgresApiFactory(database.ConnectionString, clock);
        using var second = baseSecond.WithWebHostBuilder(b => b.UseSetting("Forge:Enabled", "true")); using var other = ForgeOwner(second);
        var results = await Task.WhenAll(CreateForge(owner, id), CreateForge(other, id));
        Assert.Equal(results[0], results[1]); Assert.Equal(before, await other.GetStringAsync("/api/programme"));
        Assert.Single(await second.Services.GetRequiredService<IForgeProgrammeReader>().ListAsync(ForgeProgrammePolicy.OwnerScope));
        var contexts = first.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await contexts.CreateDbContextAsync();
        Assert.DoesNotContain(id.ToString(), (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json);
        using var baseRestart = new PostgresApiFactory(database.ConnectionString, clock);
        using var restart = baseRestart.WithWebHostBuilder(b => b.UseSetting("Forge:Enabled", "true")); using var resumed = ForgeOwner(restart);
        Assert.Equal(results[0], (await resumed.GetFromJsonAsync<ForgeProgrammeView>($"{ForgeProgrammeTests.Root}/{id}", LifeScenarioTests.Json))!.Programme);
    }

    [PostgresFact]
    public async Task Forge_runtime_transaction_failure_and_owner_scope_are_isolated()
    {
        await using var database = await TestDatabase.CreateAsync(); using var baseHost = new PostgresApiFactory(database.ConnectionString);
        using var host = baseHost.WithWebHostBuilder(b => b.UseSetting("Forge:Enabled", "true")); using var owner = ForgeOwner(host);
        var programme = await CreateForge(owner, Guid.NewGuid()); var transaction = host.Services.GetRequiredService<ICoreTransaction>();
        var runtime = host.Services.GetRequiredService<IRuntimeReader>(); var controls = await runtime.ReadAsync(s => s.AgentControls);
        await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync<bool>(async writes => {
            await writes.SaveForgeProgrammeAsync(programme with { Paused = true, Revision = 2 });
            writes.Runtime.AgentControls = controls with { Paused = !controls.Paused };
            throw new IOException("Synthetic pre-commit failure");
        }));
        var reader = host.Services.GetRequiredService<IForgeProgrammeReader>();
        Assert.Equal(programme, await reader.GetAsync(ForgeProgrammePolicy.OwnerScope, programme.Id));
        Assert.Null(await reader.GetAsync("another-owner", programme.Id)); Assert.Empty(await reader.ListAsync("another-owner"));
        Assert.Equal(controls, await runtime.ReadAsync(s => s.AgentControls));
    }

    [PostgresFact]
    public async Task Forge_privacy_export_and_erasure_work_even_when_the_feature_is_disabled()
    {
        await using var database = await TestDatabase.CreateAsync(); using var baseHost = new PostgresApiFactory(database.ConnectionString);
        using var host = baseHost.WithWebHostBuilder(b => b.UseSetting("Forge:Enabled", "true")); using var owner = ForgeOwner(host);
        var programme = await CreateForge(owner, Guid.NewGuid());
        using var disabled = new PostgresApiFactory(database.ConnectionString); using var privacyOwner = disabled.AuthorisedClient();
        using var absent = await privacyOwner.GetAsync(ForgeProgrammeTests.Root); Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var exported = await disabled.Services.GetRequiredService<IPrivacyStore>().ExportAsync(DateTimeOffset.UtcNow);
        Assert.Equal(programme, Assert.Single(exported.ForgeProgrammes!));
        var privacy = disabled.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        var contexts = disabled.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await contexts.CreateDbContextAsync();
        Assert.Empty(await db.ForgeProgrammes.AsNoTracking().ToArrayAsync());
        var job = Assert.Single(await db.PrivacyJobs.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<PrivacyCounts>(job.CountsJson)!.ForgeProgrammes);
    }

    [PostgresFact]
    public async Task Forge_migration_down_and_reapply_leave_existing_interview_json_unchanged()
    {
        await using var database = await TestDatabase.CreateAsync();
        using (var baseHost = new PostgresApiFactory(database.ConnectionString, manualAgents: true))
        using (var host = baseHost.WithWebHostBuilder(b => b.UseSetting("Forge:Enabled", "true")))
        using (var owner = ForgeOwner(host)) {
            using var started = await owner.PostAsJsonAsync("/api/programme", new StartProgrammeRequest(Guid.NewGuid(), "2026-11-01")); started.EnsureSuccessStatusCode();
            await CreateForge(owner, Guid.NewGuid());
        }
        var options = new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var db = new SessionDbContext(options);
        var migrator = db.Database.GetService<IMigrator>();
        // Isolate the Forge migration's own storage layout. Later migrations may
        // legitimately move learning properties out of, and back into, this JSON.
        await migrator.MigrateAsync("20261120005002_ForgeProgrammes");
        var runtimeBefore = (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json;
        await migrator.MigrateAsync("20261119000007_InterviewRecordings");
        Assert.Equal(runtimeBefore, (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json);
        await migrator.MigrateAsync("20261120005002_ForgeProgrammes"); Assert.Empty(await db.ForgeProgrammes.AsNoTracking().ToArrayAsync());
        Assert.Equal(runtimeBefore, (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
