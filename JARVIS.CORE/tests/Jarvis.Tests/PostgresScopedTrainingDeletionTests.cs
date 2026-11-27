using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Scoped_deletion_matches_default_and_preserves_reasoning_financial_metadata_and_receipts()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture(); var other = Guid.NewGuid();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        await store.UpdateAsync(s => { TrainingDeletionDecisionTests.SeedReasoning(s, fixture.Submit.RequestId, other); return true; });
        var before = await store.ReadAsync(s => s.ProviderAttempts[fixture.Submit.RequestId]);
        var memory = new InMemoryRuntimeStore(); memory.ReplacePrivacyData(await store.ReadAsync(s => s));
        fixture.Clock.Now = TrainingMigrationFixture.At.AddDays(1);
        var normal = new TrainingService(memory, new InMemorySessionStore(), fixture.Clock); var scoped = new TrainingService(store, sessions, fixture.Clock);
        Assert.True(await normal.DeleteAsync(fixture.Submit.RequestId, 3, default)); Assert.True(await scoped.DeleteAsync(fixture.Submit.RequestId, 3, default));
        TrainingMigrationFixture.Same(await TrainingMigrationFixture.Snapshot(memory), await TrainingMigrationFixture.Snapshot(store));
        await store.ReadAsync(s => {
            Assert.Equal(before with { Fingerprint = "", SourceDeleted = true }, s.ProviderAttempts[fixture.Submit.RequestId]);
            Assert.DoesNotContain(fixture.Submit.RequestId, s.ProviderResults.Keys); Assert.Contains(other, s.ProviderResults.Keys);
            Assert.Contains(fixture.Submit.RequestId, s.TrainingOperations.Keys); Assert.Equal(103, s.NextTrainingSequence); return true;
        });
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TrainingFootprint.Stamp(contexts);
        Assert.False(await scoped.DeleteAsync(fixture.Submit.RequestId, 0, default)); Assert.False(await scoped.DeleteAsync(Guid.NewGuid(), 0, default));
        Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => scoped.CorrectAsync(fixture.Submit.RequestId, fixture.Correction, default))).StatusCode);
        Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
    }

    [PostgresFact]
    public async Task Scoped_deletion_reads_async_question_inputs_and_skips_history_and_cold_on_noop()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture(); var capture = new CorrectionCapture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new CorrectionContextFactory(database.ConnectionString, capture));
        }));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        await using var db = SchemaContext(database.ConnectionString); var unrelated = Guid.NewGuid();
        var poison = JsonSerializer.SerializeToNode(TrainingRowFixture(unrelated))!; poison["QuestionId"] = Guid.NewGuid().ToString(); poison["Answers"] = "Unrequested invalid body";
        db.RuntimeTrainingRows.Add(new() { Kind = "TrainingAttempts", Id = unrelated, Json = poison.ToJsonString() });
        db.RuntimeLearningRows.Add(new() { Kind = "TrainingBaselines", Id = Guid.NewGuid(), Json = "null" });
        var review = await db.RuntimeLearningRows.SingleAsync(r => r.Kind == "Reviews" && r.Id == fixture.Later.RequestId);
        var json = JsonNode.Parse(review.Json)!; json["Result"] = "Unrequested replay result"; review.Json = json.ToJsonString(); await db.SaveChangesAsync();
        capture.Enabled = true; var service = new TrainingService(store, sessions, fixture.Clock);
        Assert.True(await service.DeleteAsync(fixture.Submit.RequestId, 3, default));
        Assert.Contains(capture.Commands, s => s.Contains("(\"Json\"->>'QuestionId')::uuid", StringComparison.Ordinal));
        Assert.Contains(capture.Commands, s => s.Contains("EventJson", StringComparison.Ordinal));
        Assert.Contains(capture.Commands, s => s.Contains("RuntimeDocuments", StringComparison.Ordinal) && s.Contains("\"Json\"", StringComparison.Ordinal)); // Residual cold reasoning is intentional.
        Assert.DoesNotContain(capture.Commands, s => s.Contains("RuntimeAudioState", StringComparison.Ordinal) || s.Contains("RuntimeLearningState", StringComparison.Ordinal) || s.Contains("->'Result'", StringComparison.Ordinal));
        capture.Enabled = false;
        TrainingMigrationFixture.Same(poison.ToJsonString(), (await db.RuntimeTrainingRows.AsNoTracking().SingleAsync(r => r.Id == unrelated)).Json);
        Assert.Equal(103, (await db.RuntimeLearningState.AsNoTracking().SingleAsync()).NextTrainingSequence);
        await SetTrainingCold(db, "{\"Goals\":[]}"); // Neither no-op may deserialize this unrelated cold fault.
        capture.Commands.Clear(); capture.Enabled = true;
        Assert.False(await service.DeleteAsync(fixture.Submit.RequestId, 0, default)); Assert.False(await service.DeleteAsync(Guid.NewGuid(), 0, default));
        Assert.DoesNotContain(capture.Commands, s => s.Contains("RuntimeLearningRows", StringComparison.Ordinal) ||
            s.Contains("RuntimeDocuments", StringComparison.Ordinal) && s.Contains("\"Json\"", StringComparison.Ordinal));
    }

    [PostgresTheory]
    [InlineData("missing-baseline")] [InlineData("removed-card")] [InlineData("target-identity")]
    [InlineData("review-identity")] [InlineData("stale-revision")] [InlineData("cold-poison")]
    public async Task Scoped_deletion_preserves_baseline_authority_and_refuses_selected_faults_atomically(string scenario)
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        await using var db = SchemaContext(database.ConnectionString);
        if (scenario is "missing-baseline" or "removed-card") await db.RuntimeLearningRows.Where(r => r.Id == fixture.Question && r.Kind == "Learning").ExecuteDeleteAsync();
        if (scenario == "missing-baseline") await db.RuntimeLearningRows.Where(r => r.Id == fixture.Question && r.Kind == "TrainingBaselines").ExecuteDeleteAsync();
        if (scenario == "target-identity") {
            var row = await db.RuntimeTrainingRows.SingleAsync(r => r.Kind == "TrainingAttempts" && r.Id == fixture.Submit.RequestId);
            var json = JsonNode.Parse(row.Json)!; json["Id"] = Guid.NewGuid().ToString(); row.Json = json.ToJsonString(); await db.SaveChangesAsync();
        }
        if (scenario == "review-identity") {
            var row = await db.RuntimeLearningRows.SingleAsync(r => r.Kind == "Reviews" && r.Id == fixture.Later.RequestId);
            var json = JsonNode.Parse(row.Json)!; json["RequestId"] = Guid.NewGuid().ToString(); row.Json = json.ToJsonString(); await db.SaveChangesAsync();
        }
        if (scenario == "cold-poison") await SetTrainingCold(db, "{\"Goals\":[]}");
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TrainingFootprint.Stamp(contexts);
        var service = new TrainingService(store, sessions, fixture.Clock);
        if (scenario is "missing-baseline" or "removed-card") {
            Assert.True(await service.DeleteAsync(fixture.Submit.RequestId, 3, default));
            Assert.Equal(scenario == "removed-card", await db.RuntimeLearningRows.AnyAsync(r => r.Id == fixture.Question && r.Kind == "Learning"));
            Assert.Contains(fixture.Submit.RequestId, await store.ReadAsync(s => s.RetiredReasoningScopes));
        } else {
            if (scenario == "stale-revision") Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => service.DeleteAsync(fixture.Submit.RequestId, 2, default))).StatusCode);
            else if (scenario == "cold-poison") await Assert.ThrowsAsync<JsonException>(() => service.DeleteAsync(fixture.Submit.RequestId, 3, default));
            else await Assert.ThrowsAsync<InvalidDataException>(() => service.DeleteAsync(fixture.Submit.RequestId, 3, default));
            Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
        }
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scoped_deletion_serializes_manual_review_and_competing_correction(bool competingCorrection)
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        var service = new TrainingService(store, sessions, fixture.Clock);
        async Task<object> Delete() { try { return await service.DeleteAsync(fixture.Submit.RequestId, 3, default); } catch (DomainException e) { return e; } }
        async Task<object> Other() {
            try {
                if (competingCorrection) return await service.CorrectAsync(fixture.Submit.RequestId, new(Guid.NewGuid(), 3, "Concurrent correction"), default);
                return await new LearningService(store, fixture.Clock).ReviewAsync(fixture.Question, new(Guid.NewGuid(), ReviewRating.Again, 2), default);
            } catch (DomainException e) { return e; }
        }
        var results = await Task.WhenAll(Delete(), Other());
        if (competingCorrection) {
            var refusal = Assert.Single(results.OfType<DomainException>()); Assert.Contains(refusal.StatusCode, new[] { 404, 409 });
            Assert.Single(results, r => r is bool or TrainingAttempt);
        } else { Assert.DoesNotContain(results, r => r is DomainException); Assert.True(Assert.IsType<bool>(results[0])); }
        await store.ReadAsync(s => {
            var attempt = s.TrainingAttempts[fixture.Submit.RequestId]; Assert.Equal(4, attempt.Revision);
            Assert.Equal(attempt.DeletedAt is not null, s.RetiredReasoningScopes.Contains(attempt.Id));
            var expected = TrainingProjection.Project(fixture.Question, s.TrainingBaselines[fixture.Question], s.TrainingBaselineReviews[fixture.Question], s.Reviews.Values, s.TrainingManualSequences, s.TrainingAttempts.Values);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(s.Learning[fixture.Question]));
            Assert.Equal(competingCorrection ? 103 : 104, s.NextTrainingSequence); return true;
        });
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scoped_deletion_refuses_stale_and_erased_epochs_before_mutation(bool erased)
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        await using var db = SchemaContext(database.ConnectionString); if (erased) await db.Database.ExecuteSqlRawAsync("UPDATE \"PrivacyControls\" SET \"Erased\"=true WHERE \"Id\"=1");
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TrainingFootprint.Stamp(contexts);
        using (PrivacyContext.Enter(erased ? 0 : 99)) {
            Assert.Equal(erased ? 503 : 409, (await Assert.ThrowsAsync<DomainException>(() => new TrainingService(store, sessions, fixture.Clock).DeleteAsync(fixture.Submit.RequestId, 3, default))).StatusCode);
        }
        Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
    }

    [PostgresFact]
    public async Task Scoped_deletion_two_hosts_tombstone_once_and_return_true_then_false()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture();
        using var firstRoot = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var first = firstRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = first.Services.GetRequiredService<IRuntimeStore>(); var sessions = first.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        using var secondRoot = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var second = secondRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var a = new TrainingService(store, sessions, fixture.Clock);
        var b = new TrainingService(second.Services.GetRequiredService<IRuntimeStore>(), second.Services.GetRequiredService<ISessionStore>(), fixture.Clock);
        var results = await Task.WhenAll(a.DeleteAsync(fixture.Submit.RequestId, 3, default), b.DeleteAsync(fixture.Submit.RequestId, 3, default));
        Assert.Single(results, value => value); Assert.Single(results, value => !value);
        await store.ReadAsync(s => { Assert.Equal(4, s.TrainingAttempts[fixture.Submit.RequestId].Revision);
            Assert.NotNull(s.TrainingAttempts[fixture.Submit.RequestId].DeletedAt); Assert.Equal(103, s.NextTrainingSequence); return true; });
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scoped_deletion_rolls_back_tombstone_projection_and_reasoning_after_post_save_failure(bool cancel)
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = new TrainingMigrationFixture(); var failure = new CorrectionSaveFailure();
        using var root = new PostgresApiFactory(database.ConnectionString, fixture.Clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new CorrectionContextFactory(database.ConnectionString, new(), failure));
        }));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var sessions = host.Services.GetRequiredService<ISessionStore>(); await fixture.Seed(store, sessions);
        await store.UpdateAsync(s => { TrainingDeletionDecisionTests.SeedReasoning(s, fixture.Submit.RequestId, Guid.NewGuid()); return true; });
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TrainingFootprint.Stamp(contexts);
        var before = await TrainingMigrationFixture.Snapshot(store); failure.Enabled = true;
        using var cancellation = new CancellationTokenSource(); if (cancel) failure.CancelAfterSave = cancellation;
        var service = new TrainingService(store, sessions, fixture.Clock);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteAsync(fixture.Submit.RequestId, 3, cancellation.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(fixture.Submit.RequestId, 3, cancellation.Token));
        Assert.Equal(1, failure.Failures); Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
        TrainingMigrationFixture.Same(before, await TrainingMigrationFixture.Snapshot(store));
    }
}
