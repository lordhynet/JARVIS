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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Scoped_correction_matches_default_and_replays_current_state_without_writes()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>();
        await fixture.Seed(store, sessions); var memory = new InMemoryRuntimeStore(); memory.ReplacePrivacyData(await store.ReadAsync(s => s));
        var normal = new TrainingService(memory, new InMemorySessionStore(), fixture.Clock); var scoped = new TrainingService(store, sessions, fixture.Clock);
        fixture.Clock.Now = TrainingMigrationFixture.At.AddDays(1);
        var revision = await store.ReadAsync(s => s.TrainingAttempts[fixture.Submit.RequestId].Revision);
        var request = new CorrectTrainingAnswerRequest(Guid.NewGuid(), revision, "Résumé — scoped correction with offset provenance.");
        Assert.Equal(JsonSerializer.Serialize(await normal.CorrectAsync(fixture.Submit.RequestId, request, default)),
            JsonSerializer.Serialize(await scoped.CorrectAsync(fixture.Submit.RequestId, request, default)));
        TrainingMigrationFixture.Same(await TrainingMigrationFixture.Snapshot(memory), await TrainingMigrationFixture.Snapshot(store));
        var next = request with { RequestId = Guid.NewGuid(), ExpectedRevision = revision + 1, Text = "A later correction remains current on replay." };
        await scoped.CorrectAsync(fixture.Submit.RequestId, next, default);
        var stamp = await TrainingFootprint.Stamp(host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>());
        var replay = await scoped.CorrectAsync(fixture.Submit.RequestId, request, default);
        Assert.Equal(revision + 2, replay.Revision); Assert.Equal(next.Text, replay.CurrentAnswer);
        Assert.Equal(stamp, await TrainingFootprint.Stamp(host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>()));
        Assert.Equal(103, await store.ReadAsync(s => s.NextTrainingSequence));
        var purged = await Assert.ThrowsAsync<DomainException>(() => scoped.CorrectAsync(fixture.Purged.Attempt, fixture.Purged.Correction, default));
        Assert.Equal("training_operation_purged", purged.Code);
    }

    [PostgresFact]
    public async Task Scoped_correction_uses_async_question_queries_and_ignores_unrequested_poison_payloads()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture(); var capture = new CorrectionCapture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new CorrectionContextFactory(database.ConnectionString, capture));
        }));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        await using var db = SchemaContext(database.ConnectionString); var unrelated = Guid.NewGuid();
        var poison = JsonSerializer.SerializeToNode(TrainingRowFixture(unrelated))!; poison["QuestionId"] = Guid.NewGuid().ToString(); poison["Answers"] = "unrequested invalid body";
        db.RuntimeTrainingRows.Add(new() { Kind = "TrainingAttempts", Id = unrelated, Json = poison.ToJsonString() });
        db.RuntimeLearningRows.Add(new() { Kind = "TrainingBaselines", Id = Guid.NewGuid(), Json = "null" });
        await db.SaveChangesAsync();
        var review = await db.RuntimeLearningRows.SingleAsync(r => r.Kind == "Reviews" && r.Id == fixture.Later.RequestId);
        var reviewJson = JsonNode.Parse(review.Json)!; reviewJson["Result"] = "unrequested invalid replay result"; review.Json = reviewJson.ToJsonString(); await db.SaveChangesAsync();
        await SetTrainingCold(db, "{\"Goals\":[]}"); var rootBefore = (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json;
        var request = new CorrectTrainingAnswerRequest(Guid.NewGuid(), 3, "Targeted correction succeeds without unrelated hydration.");
        capture.Enabled = true;
        var result = await new TrainingService(store, sessions, fixture.Clock).CorrectAsync(fixture.Submit.RequestId, request, default);
        Assert.Equal(4, result.Revision);
        Assert.Contains(capture.Commands, s => s.Contains("(\"Json\"->>'QuestionId')::uuid", StringComparison.Ordinal));
        Assert.Contains(capture.Commands, s => s.Contains("EventJson", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, s => s.Contains("RuntimeAudioState", StringComparison.Ordinal) || s.Contains("RuntimeLearningState", StringComparison.Ordinal) ||
            s.Contains("RuntimeDocuments", StringComparison.Ordinal) && s.Contains("\"Json\"", StringComparison.Ordinal) || s.Contains("->'Result'", StringComparison.Ordinal));
        capture.Enabled = false; Assert.Equal(rootBefore, (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json);
        TrainingMigrationFixture.Same(poison.ToJsonString(), (await db.RuntimeTrainingRows.AsNoTracking().SingleAsync(r => r.Id == unrelated)).Json);
        Assert.Equal(103, (await db.RuntimeLearningState.AsNoTracking().SingleAsync()).NextTrainingSequence);
        capture.Commands.Clear(); capture.Enabled = true;
        await new TrainingService(store, sessions, fixture.Clock).CorrectAsync(fixture.Submit.RequestId, request, default);
        Assert.DoesNotContain(capture.Commands, s => s.Contains("RuntimeLearningRows", StringComparison.Ordinal));
    }

    [PostgresTheory]
    [InlineData("missing-baseline")] [InlineData("removed-card")] [InlineData("target-identity")] [InlineData("review-identity")]
    public async Task Scoped_correction_rebuilds_only_from_a_live_baseline_and_refuses_selected_identity_faults_atomically(string scenario)
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        await using var db = SchemaContext(database.ConnectionString);
        if (scenario == "missing-baseline") await db.RuntimeLearningRows.Where(r => r.Kind == "TrainingBaselines" && r.Id == fixture.Question).ExecuteDeleteAsync();
        if (scenario == "removed-card") await db.RuntimeLearningRows.Where(r => r.Kind == "Learning" && r.Id == fixture.Question).ExecuteDeleteAsync();
        if (scenario == "target-identity") {
            var row = await db.RuntimeTrainingRows.SingleAsync(r => r.Kind == "TrainingAttempts" && r.Id == fixture.Submit.RequestId);
            var json = JsonNode.Parse(row.Json)!; json["Id"] = Guid.NewGuid().ToString(); row.Json = json.ToJsonString(); await db.SaveChangesAsync();
        }
        if (scenario == "review-identity") {
            var row = await db.RuntimeLearningRows.SingleAsync(r => r.Kind == "Reviews" && r.Id == fixture.Later.RequestId);
            var json = JsonNode.Parse(row.Json)!; json["RequestId"] = Guid.NewGuid().ToString(); row.Json = json.ToJsonString(); await db.SaveChangesAsync();
        }
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var before = await TrainingFootprint.Stamp(contexts);
        var service = new TrainingService(store, sessions, fixture.Clock); var request = new CorrectTrainingAnswerRequest(Guid.NewGuid(), 3, "Source-checked correction.");
        if (scenario == "removed-card") {
            await service.CorrectAsync(fixture.Submit.RequestId, request, default);
            Assert.True(await db.RuntimeLearningRows.AnyAsync(r => r.Kind == "Learning" && r.Id == fixture.Question));
        } else {
            if (scenario == "missing-baseline") Assert.Equal("training_baseline_missing", (await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(fixture.Submit.RequestId, request, default))).Code);
            else await Assert.ThrowsAsync<InvalidDataException>(() => service.CorrectAsync(fixture.Submit.RequestId, request, default));
            Assert.Equal(before, await TrainingFootprint.Stamp(contexts));
        }
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scoped_correction_serializes_same_or_conflicting_receipts_and_concurrent_manual_review(bool conflict)
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        var service = new TrainingService(store, sessions, fixture.Clock); var request = new CorrectTrainingAnswerRequest(Guid.NewGuid(), 3, "First concurrent correction.");
        async Task<object> Correct(CorrectTrainingAnswerRequest r) { try { return await service.CorrectAsync(fixture.Submit.RequestId, r, default); } catch (DomainException e) { return e; } }
        var results = await Task.WhenAll(Correct(request), Correct(conflict ? request with { Text = "Conflicting concurrent correction." } : request));
        Assert.Equal(conflict ? 1 : 2, results.OfType<TrainingAttempt>().Count());
        if (conflict) Assert.Equal(409, Assert.Single(results.OfType<DomainException>()).StatusCode);
        await using var db = SchemaContext(database.ConnectionString);
        Assert.Equal(1, await db.RuntimeTrainingRows.CountAsync(r => r.Kind == "TrainingOperations" && r.Id == request.RequestId));
        Assert.Equal(4, await store.ReadAsync(s => s.TrainingAttempts[fixture.Submit.RequestId].Revision));
        var correction = request with { RequestId = Guid.NewGuid(), ExpectedRevision = 4, Text = "Correction beside a manual recall." };
        await Task.WhenAll((Task)service.CorrectAsync(fixture.Submit.RequestId, correction, default),
            new LearningService(store, fixture.Clock).ReviewAsync(fixture.Question, new(Guid.NewGuid(), ReviewRating.Again, 2), default));
        await store.ReadAsync(s => {
            var expected = TrainingProjection.Project(fixture.Question, s.TrainingBaselines[fixture.Question], s.TrainingBaselineReviews[fixture.Question], s.Reviews.Values, s.TrainingManualSequences, s.TrainingAttempts.Values);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(s.Learning[fixture.Question])); Assert.Equal(104, s.NextTrainingSequence); return true;
        });
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scoped_correction_refuses_stale_or_erased_epochs_before_mutation(bool erased)
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        await using var db = SchemaContext(database.ConnectionString); if (erased) await db.Database.ExecuteSqlRawAsync("UPDATE \"PrivacyControls\" SET \"Erased\"=true WHERE \"Id\"=1");
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var before = await TrainingFootprint.Stamp(contexts);
        using (PrivacyContext.Enter(erased ? 0 : 99)) {
            var e = await Assert.ThrowsAsync<DomainException>(() => new TrainingService(store, sessions, fixture.Clock)
                .CorrectAsync(fixture.Submit.RequestId, new(Guid.NewGuid(), 3, "Refused epoch correction."), default));
            Assert.Equal(erased ? 503 : 409, e.StatusCode);
        }
        Assert.Equal(before, await TrainingFootprint.Stamp(contexts));
    }

    [PostgresFact]
    public async Task Scoped_correction_rolls_back_target_receipt_and_card_after_a_post_save_failure()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture(); var failure = new CorrectionSaveFailure();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new CorrectionContextFactory(database.ConnectionString, new(), failure));
        }));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var before = await TrainingFootprint.Stamp(contexts);
        failure.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TrainingService(store, sessions, fixture.Clock)
            .CorrectAsync(fixture.Submit.RequestId, new(Guid.NewGuid(), 3, "Rolled back correction."), default));
        Assert.Equal(1, failure.Failures); Assert.Equal(before, await TrainingFootprint.Stamp(contexts));
    }

    private sealed class CorrectionCapture : DbCommandInterceptor
    {
        public bool Enabled { get; set; } public ConcurrentQueue<string> Commands { get; } = new();
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result) {
            if (Enabled) throw new InvalidOperationException("Scoped correction SQL must be asynchronous."); return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default) {
            if (Enabled) Commands.Enqueue(command.CommandText); return ValueTask.FromResult(result);
        }
    }
    private sealed class CorrectionSaveFailure : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } public int Failures { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) {
            if (Enabled) { Failures++; throw new InvalidOperationException("Synthetic post-save correction failure."); } return ValueTask.FromResult(result);
        }
    }
    private sealed class CorrectionContextFactory(string connection, CorrectionCapture capture, CorrectionSaveFailure? failure = null) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() {
            var options = new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(connection).AddInterceptors(capture);
            if (failure is not null) options.AddInterceptors(failure); return new(options.Options);
        }
        public Task<SessionDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
