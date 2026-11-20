using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
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
    // Deliberately uses IRuntimeStore's default review method: the previous cohesive callback path.
    private sealed class CallbackReviewStore(IRuntimeStore inner) : IRuntimeStore
    {
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => inner.ReadAsync(read, ct);
        public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default) => inner.UpdateAsync(update, ct);
    }

    private static Task SeedReviewFixture(IRuntimeStore store, RuntimeState fixture) => store.UpdateAsync(s => {
        s.Learning = fixture.Learning; s.Reviews = fixture.Reviews; s.TrainingBaselines = fixture.TrainingBaselines;
        s.TrainingBaselineReviews = fixture.TrainingBaselineReviews; s.TrainingManualSequences = fixture.TrainingManualSequences;
        s.NextTrainingSequence = fixture.NextTrainingSequence; return true;
    });

    [PostgresFact]
    public async Task Scoped_review_matches_callback_schedule_and_preserves_replay_after_deletion_and_restart()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = LearningFixture(); var id = fixture.Learning.Keys.Single();
        var clock = new ForgeClock { Now = fixture.Learning[id].CreatedAt.AddDays(1) }; var request = new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Hard, 2.125);
        LearningItem result;
        using (var original = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true))
        using (var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            var store = host.Services.GetRequiredService<IRuntimeStore>(); await SeedReviewFixture(store, fixture);
            IRuntimeStore memory = new InMemoryRuntimeStore(); await SeedReviewFixture(memory, fixture);
            var expected = await new LearningService(memory, clock).ReviewAsync(id, request, default);
            result = await host.Services.GetRequiredService<LearningService>().ReviewAsync(id, request, default);
            Assert.Equal(expected, result); Assert.Equal(expected.DueAt.Offset, result.DueAt.Offset);
            var state = await store.ReadAsync(s => s);
            Assert.Equal(43, state.TrainingManualSequences[request.RequestId]); Assert.Equal(44, state.NextTrainingSequence);
            Assert.Equal(fixture.TrainingBaselines[id], state.TrainingBaselines[id]);
            Assert.Equal(fixture.TrainingBaselineReviews[id], state.TrainingBaselineReviews[id]);
            Assert.Equal(TrainingProjection.Manual(fixture.Learning[id], request.Rating, clock.Now), result);
            await store.UpdateAsync(s => { s.Learning.Remove(id); return true; });
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        var learning = restarted.Services.GetRequiredService<LearningService>(); clock.Now = clock.Now.AddDays(5);
        Assert.Equal(result, await learning.ReviewAsync(id, request, default));
        foreach (var conflict in new[] { request with { Rating = ReviewRating.Easy }, request with { ResponseSeconds = 3 } })
            Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => learning.ReviewAsync(id, conflict, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => learning.ReviewAsync(Guid.NewGuid(), request, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => learning.ReviewAsync(id, request with { RequestId = Guid.NewGuid() }, default))).StatusCode);
    }

    [PostgresFact]
    public async Task Scoped_review_serializes_two_hosts_and_callback_writers_without_duplicate_sequences()
    {
        await using var database = await TestDatabase.CreateAsync(); var fixture = LearningFixture(); var id = fixture.Learning.Keys.Single(); var clock = new ForgeClock();
        using var first = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        using var second = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        var a = first.Services.GetRequiredService<IRuntimeStore>(); var b = second.Services.GetRequiredService<IRuntimeStore>();
        await SeedReviewFixture(a, fixture);
        var request = new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Again, 2);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => new LearningService(i % 2 == 0 ? a : b, clock).ReviewAsync(id, request, default)));
        Assert.All(results, x => Assert.Equal(results[0], x));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => new LearningService(i % 2 == 0 ? a : new CallbackReviewStore(b), clock)
            .ReviewAsync(id, request with { RequestId = Guid.NewGuid() }, default)));
        var state = await a.ReadAsync(s => s);
        Assert.Equal(10, state.Reviews.Count); Assert.Equal(52, state.NextTrainingSequence);
        Assert.Equal(Enumerable.Range(42, 10).Select(x => (long)x), state.TrainingManualSequences.Values.Order());
    }

    [PostgresFact]
    public async Task Scoped_review_overflow_and_duplicate_sequence_roll_back_all_rows()
    {
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var fixture = LearningFixture(); var id = fixture.Learning.Keys.Single();
        fixture.NextTrainingSequence = long.MaxValue; await SeedReviewFixture(store, fixture);
        var learning = host.Services.GetRequiredService<LearningService>(); var request = new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Good, 1);
        var before = LearningJson(await store.ReadAsync(s => s));
        await Assert.ThrowsAsync<OverflowException>(() => learning.ReviewAsync(id, request, default));
        Assert.Equal(before, LearningJson(await store.ReadAsync(s => s)));
        await store.UpdateAsync(s => { s.NextTrainingSequence = 43; s.TrainingManualSequences[request.RequestId] = 41; return true; });
        before = LearningJson(await store.ReadAsync(s => s));
        await Assert.ThrowsAsync<DbUpdateException>(() => learning.ReviewAsync(id, request, default));
        Assert.Equal(before, LearningJson(await store.ReadAsync(s => s)));
    }

    [PostgresFact]
    public async Task Scoped_review_honors_erasure_and_stale_epoch_even_for_replay()
    {
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var fixture = LearningFixture(); await SeedReviewFixture(store, fixture);
        var learning = host.Services.GetRequiredService<LearningService>(); var id = fixture.Learning.Keys.Single(); var request = new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Hard, 2);
        await learning.ReviewAsync(id, request, default);
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DomainException>(() => learning.ReviewAsync(id, request, default));
        await privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), new string('b', 64));
        using (PrivacyContext.Enter(status.Epoch))
            Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => learning.ReviewAsync(id, request, default))).StatusCode);
        Assert.Empty(await store.ReadAsync(s => s.Reviews)); Assert.Empty(await store.ReadAsync(s => s.Learning));
    }

    private sealed class ReviewCommandCapture : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public ConcurrentQueue<string> Commands { get; } = new();
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) {
            if (Enabled) throw new InvalidOperationException("Scoped review must use async commands."); return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) {
            if (Enabled) Commands.Enqueue(command.CommandText); return ValueTask.FromResult(result);
        }
    }
    private sealed class ReviewContextFactory(string connection, ReviewCommandCapture capture) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() => new(new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(connection).AddInterceptors(capture).Options);
        public Task<SessionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    [PostgresFact]
    public async Task Scoped_review_reads_only_keyed_rows_and_leaves_unrelated_stamps_unchanged()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var fixture = LearningFixture(); var id = fixture.Learning.Keys.Single(); var store = host.Services.GetRequiredService<IRuntimeStore>();
        await SeedReviewFixture(store, fixture); var capture = new ReviewCommandCapture(); var factory = new ReviewContextFactory(database.ConnectionString, capture);
        async Task<string[]> Stamps() {
            await using var db = await factory.CreateDbContextAsync();
            return await db.Database.SqlQueryRaw<string>("""
                SELECT 'document:'||xmin::text AS "Value" FROM "RuntimeDocuments"
                UNION ALL SELECT "Kind"||':'||"Id"::text||':'||xmin::text FROM "RuntimeLearningRows"
                WHERE "Kind"!='Learning' ORDER BY "Value"
                """).ToArrayAsync();
        }
        var before = await Stamps(); var clock = new ForgeClock();
        var service = new LearningService(new PostgresRuntimeStore(factory), clock); var request = new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Again, 2);
        capture.Enabled = true; await service.ReviewAsync(id, request, default); capture.Enabled = false;
        Assert.Contains(capture.Commands, x => x.Contains("FOR UPDATE", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, x => x.Contains("RuntimeAudioState", StringComparison.Ordinal) || x.Contains("RuntimeHot", StringComparison.Ordinal));
        var selects = capture.Commands.Where(x => x.StartsWith("SELECT", StringComparison.Ordinal) && x.Contains("RuntimeLearningRows", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, selects.Length);
        Assert.All(selects, x => { Assert.Contains("WHERE", x); Assert.Contains("\"Kind\"", x); Assert.Contains("\"Id\" =", x); });
        var after = await Stamps(); Assert.All(before, x => Assert.Contains(x, after));
        capture.Commands.Clear(); capture.Enabled = true; await service.ReviewAsync(id, request, default); capture.Enabled = false;
        Assert.Single(capture.Commands, x => x.Contains("RuntimeLearningRows", StringComparison.Ordinal));
        Assert.Equal(after, await Stamps());
    }

    [PostgresFact]
    public async Task Scoped_review_without_baseline_keeps_sequence_control_and_rejects_voice_receipt_reuse()
    {
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var fixture = LearningFixture(); var id = fixture.Learning.Keys.Single();
        fixture.TrainingBaselines.Clear(); var receipt = fixture.Reviews.Values.Single();
        fixture.Reviews[receipt.RequestId] = receipt with { Event = receipt.Event with { TimingSource = "voice_measured" } };
        await SeedReviewFixture(store, fixture); var learning = host.Services.GetRequiredService<LearningService>();
        await learning.ReviewAsync(id, new(Guid.NewGuid(), ReviewRating.Again, 2), default);
        Assert.Equal(43, await store.ReadAsync(s => s.NextTrainingSequence));
        Assert.Single(await store.ReadAsync(s => s.TrainingManualSequences));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => learning.ReviewAsync(id,
            new(receipt.RequestId, receipt.Event.Rating, receipt.Event.ResponseTime.TotalSeconds), default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => learning.ReviewAsync(id,
            new(Guid.Empty, ReviewRating.Again, 2), default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => learning.ReviewAsync(id,
            new(Guid.NewGuid(), ReviewRating.Again, double.NaN), default))).StatusCode);
    }
}
