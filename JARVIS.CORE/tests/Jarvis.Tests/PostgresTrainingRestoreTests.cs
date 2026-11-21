using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    // Deliberately covers only the synthetic training footprint on the current schema.
    // This is NOT a full Core backup/key/recording restore or the owner-gated D1 rehearsal.
    [PostgresFact]
    public async Task Training_storage_fixture_survives_current_schema_restore_replay_and_post_restore_edits()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var target = await TestDatabase.CreateAsync();
        var fixture = new TrainingMigrationFixture();
        TrainingFootprint backup;
        string expected;
        using (var factory = new PostgresApiFactory(source.ConnectionString, fixture.Clock, manualAgents: true))
        using (var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            var store = host.Services.GetRequiredService<IRuntimeStore>();
            await fixture.Seed(store, host.Services.GetRequiredService<ISessionStore>());
            expected = await TrainingMigrationFixture.Snapshot(store);
            backup = await TrainingFootprint.Capture(host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>());
        }
        using (var factory = new PostgresApiFactory(target.ConnectionString, fixture.Clock, manualAgents: true))
        using (var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            // Boot creates the current schema; the source host has already stopped.
            var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
            await backup.Restore(contexts);
        }
        string edited;
        using (var factory = new PostgresApiFactory(target.ConnectionString, fixture.Clock, manualAgents: true))
        using (var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            var store = host.Services.GetRequiredService<IRuntimeStore>();
            var sessions = host.Services.GetRequiredService<ISessionStore>();
            TrainingMigrationFixture.Same(expected, await TrainingMigrationFixture.Snapshot(store));
            var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
            var stamp = await TrainingFootprint.Stamp(contexts);
            await fixture.Replay(store, sessions);
            Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
            await fixture.EditAfterRestore(store, sessions);
            edited = await TrainingMigrationFixture.Snapshot(store);
        }
        using var finalFactory = new PostgresApiFactory(target.ConnectionString, fixture.Clock, manualAgents: true);
        using var restarted = finalFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        TrainingMigrationFixture.Same(edited, await TrainingMigrationFixture.Snapshot(restarted.Services.GetRequiredService<IRuntimeReader>()));
    }

    private sealed record TrainingFootprint(RuntimeDocument Root, RuntimeLearningRow[] Learning,
        RuntimeLearningControlRow Control, SessionRow[] Sessions, string[] Migrations)
    {
        internal static async Task<TrainingFootprint> Capture(IDbContextFactory<SessionDbContext> contexts)
        {
            await using var db = await contexts.CreateDbContextAsync();
            return new(await db.RuntimeDocuments.AsNoTracking().SingleAsync(), await db.RuntimeLearningRows.AsNoTracking().ToArrayAsync(),
                await db.RuntimeLearningState.AsNoTracking().SingleAsync(), await db.Sessions.AsNoTracking().ToArrayAsync(),
                (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        }
        internal async Task Restore(IDbContextFactory<SessionDbContext> contexts)
        {
            await using var db = await contexts.CreateDbContextAsync();
            Assert.Equal(Migrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.Sessions.ToArrayAsync()); Assert.Empty(await db.RuntimeLearningRows.ToArrayAsync());
            await using var tx = await db.Database.BeginTransactionAsync();
            var root = await db.RuntimeDocuments.SingleAsync(); root.Json = Root.Json; root.Fingerprint = Root.Fingerprint;
            (await db.RuntimeLearningState.SingleAsync()).NextTrainingSequence = Control.NextTrainingSequence;
            db.RuntimeLearningRows.AddRange(Learning.Select(x => new RuntimeLearningRow { Kind = x.Kind, Id = x.Id, Json = x.Json, DueAt = x.DueAt }));
            db.Sessions.AddRange(Sessions.Select(x => SessionRow.FromContract(x.ToContract())));
            await db.SaveChangesAsync(); await tx.CommitAsync();
            Assert.Equal(Root.Fingerprint, (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Fingerprint);
            Assert.Equal(Learning.Length, await db.RuntimeLearningRows.CountAsync());
        }
        internal static async Task<string> Stamp(IDbContextFactory<SessionDbContext> contexts)
        {
            await using var db = await contexts.CreateDbContextAsync();
            // Physical MVCC stamps catch unintended writes even when values are unchanged.
            return await db.Database.SqlQueryRaw<string>("""
                SELECT string_agg(s."Stamp", ',' ORDER BY s."Stamp") AS "Value" FROM (
                  SELECT 'root:' || "Id" || ':' || xmin::text AS "Stamp" FROM "RuntimeDocuments"
                  UNION ALL SELECT "Kind" || ':' || "Id" || ':' || xmin::text FROM "RuntimeLearningRows"
                  UNION ALL SELECT 'control:' || "Id" || ':' || xmin::text FROM "RuntimeLearningState"
                ) s
                """).SingleAsync();
        }
    }
}
