using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task ForgeProtected_postgres_versions_and_original_retry_result_survive_restart()
    {
        await using var database = await TestDatabase.CreateAsync(); ForgeProtectedRecord first;
        using (var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true))
        using (var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            first = (await new ForgeProtectedStorageFixture(host.Services).Versions()).First;
        }
        using var restartedBase = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var restarted = restartedBase.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var fixture = new ForgeProtectedStorageFixture(restarted.Services); var receipt = (await fixture.Receipt(first))!;
        Assert.Equal(1, receipt.Revision); Assert.Equal(2, (await fixture.Latest(first))!.Revision);
        Assert.True(fixture.Matches(receipt, "Synthetic private observation")); Assert.Equal("Synthetic private observation", fixture.Open(receipt).Value);
        var contexts = restarted.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await contexts.CreateDbContextAsync();
        Assert.Equal(2, await db.ForgeCheckIns.CountAsync()); Assert.Equal(1, await db.ForgeCapacity.CountAsync());
        var row = await db.ForgeCheckIns.AsNoTracking().OrderBy(x => x.Revision).FirstAsync();
        Assert.Equal(first.Envelope.Ciphertext, row.Ciphertext); Assert.Equal(first.RetryProof.Mac, row.RetryMac);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [PostgresFact]
    public async Task ForgeProtected_postgres_rollback_and_project_delete_remove_all_versions_and_receipts()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        await new ForgeProtectedStorageFixture(host.Services).RollbackAndDelete();
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.ForgeCheckIns.ToListAsync()); Assert.Empty(await db.ForgeCapacity.ToListAsync());
    }

    [PostgresFact]
    public async Task ForgeProtected_postgres_erasure_clears_both_tables_and_rejects_old_epoch()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        await new ForgeProtectedStorageFixture(host.Services).Erasure();
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.ForgeCheckIns.ToListAsync()); Assert.Empty(await db.ForgeCapacity.ToListAsync());
    }

    [PostgresFact]
    public async Task ForgeProtected_two_hosts_enforce_one_revision_and_request_identity()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var firstBase = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var first = firstBase.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        using var secondBase = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var second = secondBase.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var a = new ForgeProtectedStorageFixture(first.Services); var b = new ForgeProtectedStorageFixture(second.Services);
        var programme = ForgeProtectedStorageFixture.Programme(); await a.Transaction.ExecuteAsync(async w => { await w.SaveForgeProgrammeAsync(programme); return true; });
        var id = Guid.NewGuid(); var winnerCount = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i => {
            var fixture = i % 2 == 0 ? a : b;
            try { await fixture.Append(fixture.Record(programme, ForgeProtectedKind.CheckIn, id)); Interlocked.Increment(ref winnerCount); }
            catch (DomainException e) { Assert.Equal(409, e.StatusCode); }
        }));
        Assert.Equal(1, winnerCount);
        var latest = await a.Transaction.ExecuteAsync(w => w.ReadForgeProtectedRecordAsync(ForgeProtectedKind.CheckIn, programme.OwnerScope, programme.Id, id));
        Assert.Equal(1, latest!.Revision);
    }

    [PostgresFact]
    public async Task ForgeProtected_database_constraints_and_guarded_empty_downgrade_preserve_learning()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var fixture = new ForgeProtectedStorageFixture(host.Services); var (programme, first, _) = await fixture.Versions();
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var cardId = Guid.NewGuid(); var at = ForgeProtectedStorageFixture.At;
        await store.UpdateAsync(s => { s.Learning[cardId] = new(cardId, LearningDomain.InterviewStory, "Existing interview card", "Keep this", 2, 5, at, at); return true; });
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync()) {
            var invalid = ForgeProtectedRow.From(first with { Id = Guid.NewGuid(), RequestId = Guid.NewGuid() }); invalid.Nonce = [];
            db.ForgeCheckIns.Add(invalid);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await using (var db = await contexts.CreateDbContextAsync()) {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.GetService<IMigrator>().MigrateAsync("20261120041624_SharedLearningRows"));
            Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
            Assert.Equal(2, await db.ForgeCheckIns.CountAsync());
        }
        await fixture.Transaction.ExecuteAsync(async w => { await w.SaveForgeProgrammeAsync(programme with { DeletedAt = at, Revision = 2 }); return true; });
        await using (var db = await contexts.CreateDbContextAsync()) {
            await db.Database.GetService<IMigrator>().MigrateAsync("20261120041624_SharedLearningRows");
            await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.ForgeCheckIns.ToListAsync()); Assert.Empty(await db.ForgeCapacity.ToListAsync());
        }
        Assert.Equal("Keep this", await store.ReadAsync(s => s.Learning[cardId].Answer));
    }
}
