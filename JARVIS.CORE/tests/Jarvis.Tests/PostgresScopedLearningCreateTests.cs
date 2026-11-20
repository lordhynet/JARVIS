using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    private sealed class CreationCapture : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public ConcurrentQueue<string> Commands { get; } = new();
        public TaskCompletionSource LockAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Sync() { if (Enabled) throw new InvalidOperationException("Scoped creation must use async database commands."); }
        private void Capture(DbCommand command) {
            if (!Enabled) return;
            Commands.Enqueue(command.CommandText);
            if (command.CommandText.Contains("RuntimeDocuments", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)) LockAttempt.TrySetResult();
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) { Sync(); return result; }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) { Sync(); return result; }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result) { Sync(); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Capture(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Capture(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) { Capture(command); return ValueTask.FromResult(result); }
    }
    private sealed class CreationContextFactory(string connection, CreationCapture capture) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() => new(new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(connection).AddInterceptors(capture).Options);
        public Task<SessionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    // Deliberately omits the new port, retaining the default cohesive callback for matched measurements.
    private sealed class CallbackCreationStore(IRuntimeStore inner) : IRuntimeStore
    {
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => inner.ReadAsync(read, ct);
        public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default) => inner.UpdateAsync(update, ct);
    }

    [PostgresFact]
    public async Task Scoped_creation_is_async_preserves_defaults_and_provenance_and_skips_all_histories()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync()) {
            db.RuntimeLearningRows.AddRange(new RuntimeLearningRow { Kind = "Learning", Id = Guid.NewGuid(), Json = "null" },
                new RuntimeLearningRow { Kind = "Reviews", Id = Guid.NewGuid(), Json = "null" },
                new RuntimeLearningRow { Kind = "TrainingBaselines", Id = Guid.NewGuid(), Json = "null" });
            await db.SaveChangesAsync();
        }
        async Task<string[]> Stamps() {
            await using var db = await contexts.CreateDbContextAsync();
            return await db.Database.SqlQueryRaw<string>("""
                SELECT 'document:'||xmin::text AS "Value" FROM "RuntimeDocuments"
                UNION ALL SELECT "Kind"||':'||"Id"::text||':'||xmin::text FROM "RuntimeLearningRows"
                UNION ALL SELECT 'sequence:'||xmin::text FROM "RuntimeLearningState"
                UNION ALL SELECT 'audio:'||xmin::text FROM "RuntimeAudioState" ORDER BY "Value"
                """).ToArrayAsync();
        }
        var before = await Stamps(); var capture = new CreationCapture { Enabled = true };
        var store = new PostgresRuntimeStore(new CreationContextFactory(database.ConnectionString, capture));
        var clock = new ForgeClock { Now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(5.5)).AddTicks(7) };
        var request = new CreateLearningRequest(LearningDomain.InterviewStory, "  Synthetic prompt  ", "  Synthetic answer  ");
        var actual = await new LearningService(store, clock).CreateAsync(request, default); capture.Enabled = false;
        var expected = await new LearningService(new InMemoryRuntimeStore(), clock).CreateAsync(request, default);
        Assert.Equal(expected with { Id = actual.Id }, actual); Assert.NotEqual(Guid.Empty, actual.Id);
        Assert.Equal("Synthetic prompt", actual.Prompt); Assert.Equal(0, actual.Stability); Assert.Equal(5, actual.Difficulty);
        Assert.Equal(clock.Now.Offset, actual.DueAt.Offset); Assert.Equal(clock.Now.Ticks, actual.CreatedAt.Ticks);
        Assert.DoesNotContain(capture.Commands, x => x.Contains("RuntimeAudioState", StringComparison.Ordinal) || x.Contains("RuntimeLearningState", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, x => x.StartsWith("SELECT", StringComparison.Ordinal) && x.Contains("RuntimeLearningRows", StringComparison.Ordinal));
        var after = await Stamps(); Assert.All(before, x => Assert.Contains(x, after));
        var restored = (await ((IRuntimeReader)store).ReadLearningItemAsync(actual.Id))!;
        Assert.Equal(actual, restored); Assert.Equal(clock.Now.Offset, restored.DueAt.Offset); Assert.Equal(clock.Now.Ticks, restored.CreatedAt.Ticks);
    }

    [PostgresFact]
    public async Task Scoped_creation_samples_clock_after_writer_lock_and_cancels_without_partial_card()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        _ = host.Services.GetRequiredService<IRuntimeStore>(); var clock = new ForgeClock();
        async Task Run(bool cancel) {
            await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1 FOR UPDATE", connection, tx)) await command.ExecuteScalarAsync();
            var capture = new CreationCapture { Enabled = true }; var store = new PostgresRuntimeStore(new CreationContextFactory(database.ConnectionString, capture));
            using var stop = new CancellationTokenSource();
            var pending = new LearningService(store, clock).CreateAsync(new(LearningDomain.French, "Synthetic", "Synthetic"), stop.Token);
            await capture.LockAttempt.Task.WaitAsync(TimeSpan.FromSeconds(15));
            clock.Now = clock.Now.AddDays(1).AddTicks(7);
            if (cancel) {
                stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                await tx.CommitAsync();
            } else {
                await tx.CommitAsync(); var card = await pending; Assert.Equal(clock.Now, card.CreatedAt); Assert.Equal(clock.Now.Ticks, card.DueAt.Ticks);
            }
        }
        await Run(false); await Run(true);
        await using var verify = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await verify.RuntimeLearningRows.CountAsync(x => x.Kind == "Learning"));
    }

    [PostgresFact]
    public async Task Scoped_creation_across_hosts_keeps_distinct_ids_and_unrelated_history_control()
    {
        await using var database = await TestDatabase.CreateAsync(); using var first = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var second = new PostgresApiFactory(database.ConnectionString, manualAgents: true); var fixture = LearningFixture();
        var a = first.Services.GetRequiredService<IRuntimeStore>(); var b = second.Services.GetRequiredService<IRuntimeStore>(); await SeedReviewFixture(a, fixture);
        var cards = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => new LearningService(i % 2 == 0 ? a : b, new ForgeClock())
            .CreateAsync(new(LearningDomain.French, "Same input", "Distinct creation"), default)));
        Assert.Equal(8, cards.Select(x => x.Id).Distinct().Count());
        var state = await a.ReadAsync(s => s); Assert.Equal(9, state.Learning.Count); Assert.Single(state.Reviews);
        Assert.Equal(fixture.NextTrainingSequence, state.NextTrainingSequence); Assert.Equal(fixture.TrainingManualSequences, state.TrainingManualSequences);
        Assert.Equal(fixture.TrainingBaselines.Values.Single(), state.TrainingBaselines.Values.Single());
    }

    [PostgresFact]
    public async Task Scoped_creation_invalid_inputs_erasure_and_stale_epochs_persist_nothing()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var service = host.Services.GetRequiredService<LearningService>(); var valid = new CreateLearningRequest(LearningDomain.French, "Prompt", "Answer");
        foreach (var invalid in new[] { valid with { Domain = (LearningDomain)999 }, valid with { Prompt = " " }, valid with { Prompt = new string('a', 2001) }, valid with { Answer = new string('a', 8001) } })
            Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(invalid, default))).StatusCode);
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(valid, default));
        await privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), new string('b', 64));
        using (PrivacyContext.Enter(status.Epoch)) Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(valid, default))).StatusCode);
        Assert.Empty(await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.Learning));
    }
}
