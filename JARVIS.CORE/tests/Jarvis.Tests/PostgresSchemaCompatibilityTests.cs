using System.Diagnostics;
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
    private const string UnknownMigration = "99991231235959_UnknownSyntheticFixture";
    private static SessionDbContext SchemaContext(string connection) => new(new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(connection).Options);

    [PostgresTheory]
    [InlineData(true)] [InlineData(false)]
    public async Task Schema_startup_refuses_unknown_history_without_mutation_even_with_legacy_auto_migration(bool legacyAutoMigration)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = SchemaContext(database.ConnectionString);
        await AddUnknownMigration(db);
        var before = await SchemaStamp(db);
        using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => {
            b.UseSetting("Jarvis:MigrateOnStartup", legacyAutoMigration.ToString());
            b.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
        });
        var error = Assert.Throws<DomainException>(() => { _ = host.Services; });
        Assert.Equal(503, error.StatusCode); Assert.Equal("schema_unknown_migration", error.Code);
        Assert.Contains("Database applied migrations:", error.Message); Assert.Contains("Build known migrations:", error.Message);
        Assert.Contains(UnknownMigration, error.Message); Assert.DoesNotContain(database.ConnectionString, error.Message);
        Assert.Equal(before, await SchemaStamp(db));
    }

    [PostgresTheory]
    [InlineData(true)] [InlineData(false)]
    public async Task Schema_startup_refuses_pending_history_without_mutation_even_with_legacy_auto_migration(bool legacyAutoMigration)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = SchemaContext(database.ConnectionString);
        await db.Database.GetService<IMigrator>().MigrateAsync("20261120161309_LearningReviewCardIndex");
        var before = await SchemaStamp(db);
        using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using (var host = root.WithWebHostBuilder(b => {
            b.UseSetting("Jarvis:MigrateOnStartup", legacyAutoMigration.ToString());
            b.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
        })) {
            var error = Assert.Throws<DomainException>(() => { _ = host.Services; });
            Assert.Equal(503, error.StatusCode); Assert.Equal("schema_pending_migration", error.Code);
            Assert.Contains("Database applied migrations:", error.Message); Assert.Contains("Build known migrations:", error.Message);
            Assert.Contains("20261120171901_LearningCreationReceipts", error.Message); Assert.DoesNotContain(database.ConnectionString, error.Message);
        }
        Assert.Equal(before, await SchemaStamp(db));
        await PostgresSchemaCompatibility.MigrateAsync(db);
        using var recovered = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        _ = recovered.Services;
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Schema_guard_does_not_bootstrap_an_empty_database_and_explicit_migration_is_idempotent()
    {
        await using var database = await TestDatabase.CreateAsync(initializeSchema: false);
        await using var db = SchemaContext(database.ConnectionString);
        var error = await Assert.ThrowsAsync<DomainException>(() => PostgresSchemaCompatibility.RequireCurrentAsync(db));
        Assert.Equal("schema_pending_migration", error.Code);
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync());
        await PostgresSchemaCompatibility.MigrateAsync(db);
        await PostgresSchemaCompatibility.RequireCurrentAsync(db);
        var stamp = await SchemaStamp(db);
        await PostgresSchemaCompatibility.MigrateAsync(db);
        Assert.Equal(stamp, await SchemaStamp(db));
    }

    [PostgresFact]
    public async Task Schema_explicit_migrator_refuses_unknown_history_before_applying_pending_migrations()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = SchemaContext(database.ConnectionString);
        await db.Database.GetService<IMigrator>().MigrateAsync("20261120161309_LearningReviewCardIndex");
        await AddUnknownMigration(db);
        var before = await SchemaStamp(db);
        await db.Database.OpenConnectionAsync();
        var error = await Assert.ThrowsAsync<DomainException>(() => PostgresSchemaCompatibility.MigrateAsync(db));
        Assert.Equal("schema_unknown_migration", error.Code);
        Assert.Equal(before, await SchemaStamp(db));
        Assert.Equal(System.Data.ConnectionState.Open, db.Database.GetDbConnection().State);
        // A failed guard releases the session lock even on a caller-opened connection.
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"={UnknownMigration}");
        await using var other = SchemaContext(database.ConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await PostgresSchemaCompatibility.MigrateAsync(other, timeout.Token);
        await PostgresSchemaCompatibility.RequireCurrentAsync(other, timeout.Token);
    }

    [PostgresFact]
    public async Task Schema_startup_waits_for_the_designated_migration_lock_and_can_cancel_safely()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(746271590)", holder);
        await command.ExecuteNonQueryAsync();
        try {
            await using var waiting = SchemaContext(database.ConnectionString);
            using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostgresSchemaCompatibility.RequireCurrentAsync(waiting, cancelled.Token));
        }
        finally {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(746271590)", holder);
            await release.ExecuteNonQueryAsync();
        }
        await using var retry = SchemaContext(database.ConnectionString);
        await PostgresSchemaCompatibility.RequireCurrentAsync(retry);
    }

    [PostgresFact]
    public async Task Schema_migrate_only_command_bootstraps_and_exits_without_serving_then_refuses_unknown_history()
    {
        await using var database = await TestDatabase.CreateAsync(initializeSchema: false);
        async Task<int> Invoke() {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("--migrate-only");
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing"; start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
            start.Environment["Jarvis__StorageProvider"] = "Postgres";
            start.Environment["ConnectionStrings__Jarvis"] = database.ConnectionString;
            start.Environment["Jarvis__ApiKey"] = JarvisApiFactory.TestKey;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            try {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await process.WaitForExitAsync(timeout.Token);
                Assert.DoesNotContain("Now listening on", await output, StringComparison.Ordinal);
                _ = await errors; // Do not publish process configuration or raw exception output.
                return process.ExitCode;
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        Assert.Equal(0, await Invoke());
        await using var db = SchemaContext(database.ConnectionString);
        await PostgresSchemaCompatibility.RequireCurrentAsync(db);
        await AddUnknownMigration(db);
        var before = await SchemaStamp(db);
        Assert.NotEqual(0, await Invoke());
        Assert.Equal(before, await SchemaStamp(db));
    }

    private static Task AddUnknownMigration(SessionDbContext db) => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({UnknownMigration}, '11.0.0')
        """);
    private static Task<string> SchemaStamp(SessionDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT string_agg(s."Stamp", '|' ORDER BY s."Stamp") AS "Value" FROM (
          SELECT 'history:' || "MigrationId" || ':' || xmin::text AS "Stamp" FROM "__EFMigrationsHistory"
          UNION ALL SELECT 'root:' || "Id" || ':' || xmin::text || ':' || "Fingerprint" || ':' || "Json"::text FROM "RuntimeDocuments"
          UNION ALL SELECT "Kind" || ':' || "Id" || ':' || xmin::text || ':' || "Json"::text FROM "RuntimeLearningRows"
          UNION ALL SELECT 'control:' || "NextTrainingSequence" || ':' || xmin::text FROM "RuntimeLearningState"
        ) s
        """).SingleAsync();
}
