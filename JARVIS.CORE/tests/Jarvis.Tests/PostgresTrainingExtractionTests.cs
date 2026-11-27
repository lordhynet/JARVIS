using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    private const string BeforeTrainingRows = "20261120171901_LearningCreationReceipts";
    private static TrainingAttempt TrainingRowFixture(Guid id) => new(id, Guid.NewGuid(), QuestionBank.All[0].Id, 1,
        TrainingMode.RapidDrill, 1, 1, false, TrainingMigrationFixture.At, [new(1, "Synthetic migration answer", "user", TrainingMigrationFixture.At)], []);
    private static async Task SetTrainingCold(SessionDbContext db, string json) => await db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE \"RuntimeDocuments\" SET \"Json\"=CAST({json} AS jsonb),\"Fingerprint\"='' WHERE \"Id\"=1");

    [PostgresFact]
    public async Task Canonical_training_up_down_reup_replay_and_post_up_edits_preserve_current_state()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture(); string expected;
        using (var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true))
        using (var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            var store = host.Services.GetRequiredService<IRuntimeStore>(); await fixture.Seed(store, host.Services.GetRequiredService<ISessionStore>());
            expected = await TrainingMigrationFixture.Snapshot(store);
        }
        await using var db = SchemaContext(database.ConnectionString); var migrator = db.Database.GetService<IMigrator>();
        var coldBefore = JsonNode.Parse((await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json)!;
        Assert.Null(coldBefore["TrainingAttempts"]); Assert.Null(coldBefore["TrainingOperations"]);
        await migrator.MigrateAsync(BeforeTrainingRows);
        var cold = JsonNode.Parse((await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json)!;
        var restored = JsonSerializer.Deserialize<RuntimeState>(cold.ToJsonString())!;
        TrainingMigrationFixture.Same(JsonSerializer.Serialize(JsonSerializer.Deserialize<RuntimeState>(expected)!.TrainingAttempts), JsonSerializer.Serialize(restored.TrainingAttempts));
        Assert.Null(restored.TrainingOperations[fixture.NullOperation].Fingerprint);
        cold["TrainingAttempts"]![fixture.Submit.RequestId.ToString()]!["UnchangedRawSentinel"] = "preserve raw migration payload";
        await SetTrainingCold(db, cold.ToJsonString()); await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        var raw = await db.RuntimeTrainingRows.AsNoTracking().SingleAsync(x => x.Kind == "TrainingAttempts" && x.Id == fixture.Submit.RequestId);
        Assert.Contains("UnchangedRawSentinel", raw.Json); Assert.Contains("+05:30", raw.Json);
        var stamp = await TrainingFootprint.Stamp(new TrainingContextFactory(database.ConnectionString, new()));
        using (var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true))
        using (var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>();
            TrainingMigrationFixture.Same(expected, await TrainingMigrationFixture.Snapshot(store)); await fixture.Replay(store, sessions);
            Assert.Equal(stamp, await TrainingFootprint.Stamp(host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>()));
            Assert.Equal(raw.Json, (await db.RuntimeTrainingRows.AsNoTracking().SingleAsync(x => x.Kind == raw.Kind && x.Id == raw.Id)).Json);
            await fixture.EditAfterRestore(store, sessions); expected = await TrainingMigrationFixture.Snapshot(store);
        }
        await migrator.MigrateAsync(BeforeTrainingRows);
        var afterDown = JsonSerializer.Deserialize<RuntimeState>((await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json)!;
        var current = JsonSerializer.Deserialize<RuntimeState>(expected)!;
        TrainingMigrationFixture.Same(JsonSerializer.Serialize(current.TrainingAttempts), JsonSerializer.Serialize(afterDown.TrainingAttempts));
        TrainingMigrationFixture.Same(JsonSerializer.Serialize(current.TrainingOperations), JsonSerializer.Serialize(afterDown.TrainingOperations));
        Assert.Equal(104, (await db.RuntimeLearningState.AsNoTracking().SingleAsync()).NextTrainingSequence);
        Assert.Equal("", (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Fingerprint);
        await db.Database.MigrateAsync();
        using var finalRoot = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var finalHost = finalRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        TrainingMigrationFixture.Same(expected, await TrainingMigrationFixture.Snapshot(finalHost.Services.GetRequiredService<IRuntimeStore>()));
    }

    [PostgresFact]
    public async Task Training_up_rejects_bad_source_shapes_identities_and_duplicate_uuid_keys_atomically()
    {
        await using var database = await TestDatabase.CreateAsync(initializeSchema: false); await using var db = SchemaContext(database.ConnectionString);
        await db.Database.GetService<IMigrator>().MigrateAsync(BeforeTrainingRows);
        var id = Guid.Parse("aaaaaaaa-1234-4567-89ab-aaaaaaaaaaaa"); var attempt = JsonSerializer.SerializeToNode(TrainingRowFixture(id))!;
        var mismatched = attempt.DeepClone(); mismatched["Id"] = Guid.NewGuid().ToString();
        var badTime = attempt.DeepClone(); badTime["SubmittedAt"] = "not-a-time";
        var badMember = attempt.DeepClone(); badMember["Answers"] = new JsonArray((JsonNode?)null);
        JsonObject[] bad = [new() { ["TrainingAttempts"] = null }, new() { ["TrainingOperations"] = new JsonArray() },
            new() { ["TrainingAttempts"] = new JsonObject { [id.ToString()] = mismatched } },
            new() { ["TrainingAttempts"] = new JsonObject { [id.ToString()] = badTime } },
            new() { ["TrainingAttempts"] = new JsonObject { [id.ToString()] = badMember } },
            new() { ["TrainingAttempts"] = new JsonObject { [id.ToString()] = attempt.DeepClone(), [id.ToString().ToUpperInvariant()] = attempt.DeepClone() } },
            new() { ["TrainingOperations"] = new JsonObject { [id.ToString()] = new JsonObject { ["AttemptId"] = id.ToString(), ["Kind"] = "submit", ["Fingerprint"] = 42 } } }];
        foreach (var source in bad) {
            source["Unrelated"] = "keep synthetic sentinel"; await SetTrainingCold(db, source.ToJsonString());
            var before = (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json;
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
            Assert.Equal(before, (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json);
            Assert.DoesNotContain("20261127024702_CanonicalTrainingRows", await db.Database.GetAppliedMigrationsAsync());
            Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('\"RuntimeTrainingRows\"') IS NOT NULL AS \"Value\"").SingleAsync());
        }
    }

    [PostgresFact]
    public async Task Training_down_refuses_unknown_rows_bad_identity_and_conflicting_cold_copies_without_mutation()
    {
        foreach (var failure in new[] { "unknown", "identity", "conflict", "missing-root" }) {
            await using var database = await TestDatabase.CreateAsync(); await using var db = SchemaContext(database.ConnectionString);
            var id = Guid.NewGuid(); var attempt = TrainingRowFixture(id);
            db.RuntimeTrainingRows.Add(new() { Kind = failure == "unknown" ? "Unrecognized" : "TrainingAttempts", Id = id,
                Json = JsonSerializer.Serialize(failure == "identity" ? attempt with { Id = Guid.NewGuid() } : attempt) }); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            if (failure == "conflict") await SetTrainingCold(db, JsonSerializer.Serialize(new { TrainingAttempts = new Dictionary<Guid, TrainingAttempt> { [id] = attempt with { Revision = 999 } } }));
            if (failure == "missing-root") await db.RuntimeDocuments.ExecuteDeleteAsync();
            var before = await db.RuntimeTrainingRows.AsNoTracking().Select(x => x.Json).SingleAsync();
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.GetService<IMigrator>().MigrateAsync(BeforeTrainingRows));
            Assert.Equal(before, await db.RuntimeTrainingRows.AsNoTracking().Select(x => x.Json).SingleAsync());
            Assert.Contains("20261127024702_CanonicalTrainingRows", await db.Database.GetAppliedMigrationsAsync());
        }
    }

    [PostgresFact]
    public async Task Training_lazy_reads_skip_cold_and_learning_and_detached_identity_snapshots_materialize_the_unit()
    {
        await using var database = await TestDatabase.CreateAsync(); var capture = new TrainingCommandCapture();
        using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new TrainingContextFactory(database.ConnectionString, capture));
        }));
        var reader = host.Services.GetRequiredService<IRuntimeReader>(); var id = Guid.NewGuid();
        await using var db = SchemaContext(database.ConnectionString);
        db.RuntimeTrainingRows.Add(new() { Kind = "TrainingAttempts", Id = id, Json = JsonSerializer.Serialize(TrainingRowFixture(id)) }); await db.SaveChangesAsync();
        await SetTrainingCold(db, "{\"Goals\":[]}"); capture.Enabled = true;
        Assert.Equal(1, await reader.ReadAsync(s => s.TrainingAttempts.Count));
        Assert.Contains(capture.Commands, s => s.Contains("RuntimeTrainingRows", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, s => s.Contains("RuntimeLearningRows", StringComparison.Ordinal) || s.Contains("RuntimeDocuments", StringComparison.Ordinal) && s.Contains("\"Json\"", StringComparison.Ordinal));
        capture.Enabled = false; await SetTrainingCold(db, "{}");
        RuntimeState? escaped = null; await reader.ReadAsync(s => { escaped = s; return s.Devices.Count; });
        Assert.False(escaped!.AreTrainingPropertiesLoaded); Assert.Throws<InvalidOperationException>(() => escaped.TrainingAttempts.Count);
        var detached = await reader.ReadAsync(s => s); Assert.True(detached.AreTrainingPropertiesLoaded); Assert.Single(detached.TrainingAttempts);
    }

    [PostgresFact]
    public async Task Training_export_retention_and_bulk_erasure_use_only_the_canonical_rows()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); await fixture.Seed(store, host.Services.GetRequiredService<ISessionStore>());
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var now = DateTimeOffset.Parse("2026-12-01T12:00:00Z");
        var export = await privacy.ExportAsync(now); Assert.NotEmpty(export.Training);
        Assert.Equal(await store.ReadAsync(s => s.TrainingAttempts.Values.Count(a => a.DeletedAt is null)), export.Training.Count);
        var policy = new RetentionPolicy(Enabled: true, AnswerDays: 1); var preview = await privacy.PreviewAsync(policy, now);
        Assert.True(preview.Eligible.TrainingAnswers > 0);
        var job = await privacy.RetainAsync(new(Guid.NewGuid(), now, PrivacyData.PolicyHash(policy), PrivacyData.RetentionPhrase), policy, now);
        Assert.Equal(preview.Eligible.TrainingAnswers, job.Removed.TrainingAnswers);
        Assert.Empty((await privacy.ExportAsync(now)).Training);
        var status = await privacy.StatusAsync(); var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), PrivacyData.Hash("synthetic-erasure-identity"), now);
        Assert.True(erased.Erased); await using var db = SchemaContext(database.ConnectionString); Assert.Empty(await db.RuntimeTrainingRows.ToArrayAsync());
        var cold = JsonNode.Parse((await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json)!; Assert.Null(cold["TrainingAttempts"]); Assert.Null(cold["TrainingOperations"]);
    }

    [PostgresFact]
    public async Task Training_empty_after_image_and_post_save_rollback_preserve_atomic_unit_boundaries()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = SchemaContext(database.ConnectionString); var id = Guid.NewGuid();
        var before = (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json;
        await using (var tx = await db.Database.BeginTransactionAsync()) {
            await PostgresPrivacyGate.LockAsync(db, default);
            await PostgresRuntimeStore.UpdateInTransactionAsync(db, s => {
                s.TrainingAttempts[id] = TrainingRowFixture(id); s.TrainingOperations[id] = new("submit", id, "synthetic-fingerprint");
                s.NextTrainingSequence = 2; return Task.FromResult(true);
            }, System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.GetTimestamp(), default);
            Assert.Equal(2, await db.RuntimeTrainingRows.CountAsync()); Assert.Equal(2, (await db.RuntimeLearningState.SingleAsync()).NextTrainingSequence);
            await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear(); Assert.Empty(await db.RuntimeTrainingRows.ToArrayAsync()); Assert.Equal(1, (await db.RuntimeLearningState.SingleAsync()).NextTrainingSequence);
        Assert.Equal(before, (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json); db.ChangeTracker.Clear();
        db.RuntimeTrainingRows.Add(new() { Kind = "TrainingAttempts", Id = id, Json = JsonSerializer.Serialize(TrainingRowFixture(id)) }); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var unloaded = new RuntimeState(); var adapter = new PostgresRuntimeTrainingState(db, unloaded);
        adapter.Apply(new RuntimeState()); await db.SaveChangesAsync(); Assert.Empty(await db.RuntimeTrainingRows.ToArrayAsync());
    }

    private sealed class TrainingCommandCapture : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public ConcurrentQueue<string> Commands { get; } = new();
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result) {
            if (Enabled) Commands.Enqueue(command.CommandText); return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default) {
            if (Enabled) Commands.Enqueue(command.CommandText); return ValueTask.FromResult(result);
        }
    }
    private sealed class TrainingContextFactory(string connection, TrainingCommandCapture capture) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() => new(new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(connection).AddInterceptors(capture).Options);
        public Task<SessionDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
