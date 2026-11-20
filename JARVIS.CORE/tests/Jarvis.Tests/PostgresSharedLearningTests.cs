using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    private const string BeforeSharedLearning = "20261120005002_ForgeProgrammes";
    private static RuntimeState LearningFixture()
    {
        var state = new RuntimeState(); var id = Guid.NewGuid(); var request = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 20, 8, 15, 0, TimeSpan.FromHours(5.5)).AddTicks(7);
        var card = new LearningItem(id, LearningDomain.French, "Original prompt", "Original answer", 3.5, 4.5, at.AddDays(3), at,
            2.5, 1, "content-score-v1", "assessor", Guid.NewGuid(), Guid.NewGuid());
        state.Learning[id] = card; state.TrainingBaselines[id] = card;
        state.Reviews[request] = new(request, new(Guid.NewGuid(), id, ReviewRating.Hard, TimeSpan.FromTicks(123456789), at), card);
        state.TrainingBaselineReviews[id] = [request]; state.TrainingBaselineReviews[Guid.NewGuid()] = [];
        state.TrainingManualSequences[request] = 42; state.NextTrainingSequence = 43;
        return state;
    }
    private static string LearningJson(RuntimeState state) => JsonSerializer.Serialize(new {
        state.Learning, state.Reviews, state.TrainingBaselines, state.TrainingBaselineReviews, state.TrainingManualSequences, state.NextTrainingSequence
    });
    private static void SameLearning(RuntimeState expected, RuntimeState actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(LearningJson(expected)), JsonNode.Parse(LearningJson(actual))), "Canonical learning/replay state must be lossless.");

    [PostgresFact]
    public async Task Shared_learning_backfill_downgrade_and_reapply_preserve_current_replay_and_provenance()
    {
        await using var database = await TestDatabase.CreateAsync(); var original = LearningFixture();
        var options = new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using (var db = new SessionDbContext(options)) {
            await db.Database.GetService<IMigrator>().MigrateAsync(BeforeSharedLearning);
            var json = JsonSerializer.Serialize(original);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"RuntimeDocuments\" SET \"Json\"=CAST({json} AS jsonb), \"Fingerprint\"='' WHERE \"Id\"=1");
            await db.Database.MigrateAsync();
            using var cold = JsonDocument.Parse((await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json);
            Assert.False(cold.RootElement.TryGetProperty("Learning", out _)); Assert.False(cold.RootElement.TryGetProperty("Reviews", out _));
            Assert.Equal(6, await db.RuntimeLearningRows.CountAsync());
        }
        RuntimeState after;
        using (var host = new PostgresApiFactory(database.ConnectionString, manualAgents: true)) {
            var store = host.Services.GetRequiredService<IRuntimeStore>(); SameLearning(original, await store.ReadAsync(s => s));
            var id = original.Learning.Keys.Single();
            await store.UpdateAsync(s => {
                s.Learning[id] = s.Learning[id] with { Answer = "Corrected after migration" };
                s.TrainingBaselineReviews[id].Add(Guid.NewGuid()); s.TrainingManualSequences[Guid.NewGuid()] = 43;
                s.NextTrainingSequence = 44; return true;
            });
            after = await store.ReadAsync(s => s);
        }
        await using (var db = new SessionDbContext(options)) {
            await db.Database.GetService<IMigrator>().MigrateAsync(BeforeSharedLearning);
            SameLearning(after, JsonSerializer.Deserialize<RuntimeState>((await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json)!);
            await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        SameLearning(after, await restarted.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s));
    }

    [PostgresFact]
    public async Task Shared_learning_only_changes_one_row_without_loading_or_rewriting_unrelated_cold_state()
    {
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var original = LearningFixture(); var id = original.Learning.Keys.Single();
        await store.UpdateAsync(s => {
            s.Learning = original.Learning; s.Reviews = original.Reviews; s.TrainingBaselines = original.TrainingBaselines;
            s.TrainingBaselineReviews = original.TrainingBaselineReviews; s.TrainingManualSequences = original.TrainingManualSequences;
            s.NextTrainingSequence = original.NextTrainingSequence; return true;
        });
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        async Task<string> Stamp() {
            await using var db = await contexts.CreateDbContextAsync();
            return await db.Database.SqlQueryRaw<string>("""
                SELECT document.xmin::text || ':' || review.xmin::text AS "Value" FROM "RuntimeDocuments" document
                CROSS JOIN "RuntimeLearningRows" review WHERE document."Id"=1 AND review."Kind"='Reviews'
                """).SingleAsync();
        }
        var before = await Stamp();
        await store.UpdateAsync(s => {
            Assert.False(s.AreColdPropertiesLoaded); s.Learning[id] = s.Learning[id] with { Stability = 10 };
            Assert.True(s.AreLearningPropertiesLoaded); Assert.False(s.AreColdPropertiesLoaded); return true;
        });
        Assert.Equal(before, await Stamp());
        await store.UpdateAsync(s => {
            Assert.False(s.AreLearningPropertiesLoaded); s.AgentControls = s.AgentControls with { PaidAiPaused = true };
            Assert.False(s.AreLearningPropertiesLoaded); return true;
        });
        Assert.Equal(before, await Stamp());
        await store.UpdateAsync(s => { s.Learning[id] = s.Learning[id] with { Answer = "Must survive cold hydration" }; s.Goals = []; return true; });
        Assert.Equal("Must survive cold hydration", await store.ReadAsync(s => s.Learning[id].Answer));
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Equal(6, await verify.RuntimeLearningRows.CountAsync());
        Assert.Equal(original.NextTrainingSequence, (await verify.RuntimeLearningState.SingleAsync()).NextTrainingSequence);
    }

    [PostgresFact]
    public async Task Shared_learning_and_session_failure_roll_back_and_review_replay_survives_restart()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock(); Guid cardId; Guid request = Guid.NewGuid(); LearningItem reviewed;
        using (var host = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true)) {
            using var owner = host.AuthorisedClient(); var session = await CreateSession(owner);
            var learning = host.Services.GetRequiredService<LearningService>();
            var card = await learning.CreateAsync(new(LearningDomain.CSharpDotNet, "Prompt", "Answer"), default); cardId = card.Id;
            reviewed = await learning.ReviewAsync(cardId, new(request, ReviewRating.Good, 2), default);
            var transaction = host.Services.GetRequiredService<ICoreTransaction>();
            await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync<bool>(async writes => {
                writes.Runtime.Learning[cardId] = card with { Answer = "Must not commit" }; writes.Runtime.NextTrainingSequence = 999;
                await writes.UpdateSessionAsync(session.Id, s => s with { Title = "Must not commit" }); throw new IOException("Synthetic rollback");
            }));
            Assert.Equal("Answer", await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.Learning[cardId].Answer));
            Assert.Equal(session.Title, (await host.Services.GetRequiredService<Jarvis.Orchestrator.Sessions.ISessionReader>().GetAsync(session.Id))!.Title);
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        Assert.Equal(reviewed, await restarted.Services.GetRequiredService<LearningService>().ReviewAsync(cardId, new(request, ReviewRating.Good, 2), default));
        var contexts = restarted.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await contexts.CreateDbContextAsync();
        Assert.Single(await db.RuntimeLearningRows.Where(x => x.Kind == "Reviews").ToListAsync());
    }

    [PostgresFact]
    public async Task Shared_learning_export_and_erasure_clear_all_replay_collections_and_fence_stale_requests()
    {
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        var original = LearningFixture(); var store = host.Services.GetRequiredService<IRuntimeStore>();
        await store.UpdateAsync(s => {
            s.Learning = original.Learning; s.Reviews = original.Reviews; s.TrainingBaselines = original.TrainingBaselines;
            s.TrainingBaselineReviews = original.TrainingBaselineReviews; s.TrainingManualSequences = original.TrainingManualSequences;
            s.NextTrainingSequence = original.NextTrainingSequence; return true;
        });
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        var export = await privacy.ExportAsync(DateTimeOffset.UtcNow);
        Assert.Equal(original.Learning.Values.Single(), Assert.Single(export.Learning)); Assert.Single(export.Reviews);
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await contexts.CreateDbContextAsync();
        Assert.Empty(await db.RuntimeLearningRows.AsNoTracking().ToListAsync());
        Assert.Equal(1, (await db.RuntimeLearningState.AsNoTracking().SingleAsync()).NextTrainingSequence);
        await privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), new string('b', 64));
        using (PrivacyContext.Enter(status.Epoch)) {
            var error = await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<LearningService>().CreateAsync(new(LearningDomain.French, "Stale", "Stale"), default));
            Assert.Equal(409, error.StatusCode);
        }
        Assert.Empty(await store.ReadAsync(s => s.Learning)); Assert.Empty(await store.ReadAsync(s => s.TrainingBaselineReviews));
    }

    [PostgresFact]
    public async Task Shared_learning_migration_rejects_null_review_members_without_removing_source_json()
    {
        await using var database = await TestDatabase.CreateAsync();
        var source = JsonNode.Parse(JsonSerializer.Serialize(LearningFixture()))!;
        var memberships = source["TrainingBaselineReviews"]!.AsObject();
        memberships[memberships.First().Key] = new JsonArray((JsonNode?)null);
        var options = new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var db = new SessionDbContext(options);
        await db.Database.GetService<IMigrator>().MigrateAsync(BeforeSharedLearning);
        var json = source.ToJsonString();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"RuntimeDocuments\" SET \"Json\"=CAST({json} AS jsonb) WHERE \"Id\"=1");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.MigrateAsync());
        db.ChangeTracker.Clear();
        Assert.True(JsonNode.DeepEquals(source, JsonNode.Parse((await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json)));
        Assert.DoesNotContain("20261120041624_SharedLearningRows", await db.Database.GetAppliedMigrationsAsync());
    }

    [PostgresFact]
    public async Task Shared_learning_migration_rejects_mismatched_id_without_removing_source_json()
    {
        await using var database = await TestDatabase.CreateAsync(); var original = LearningFixture(); var key = original.Learning.Keys.Single();
        original.Learning[key] = original.Learning[key] with { Id = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var db = new SessionDbContext(options); var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeSharedLearning);
        var json = JsonSerializer.Serialize(original);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"RuntimeDocuments\" SET \"Json\"=CAST({json} AS jsonb) WHERE \"Id\"=1");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.MigrateAsync());
        db.ChangeTracker.Clear();
        SameLearning(original, JsonSerializer.Deserialize<RuntimeState>((await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json)!);
        Assert.DoesNotContain("20261120041624_SharedLearningRows", await db.Database.GetAppliedMigrationsAsync());
    }
}
