using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    private sealed class QueryReadCapture : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public ConcurrentQueue<string> Commands { get; } = new();
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) {
            if (Enabled) throw new InvalidOperationException("Narrow learning reads must execute asynchronously."); return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) {
            if (Enabled) Commands.Enqueue(command.CommandText); return ValueTask.FromResult(result);
        }
        public void Begin() { Commands.Clear(); Enabled = true; }
        public void AssertNarrow() {
            Assert.Contains(Commands, x => x.Contains("RuntimeLearningRows", StringComparison.Ordinal));
            Assert.DoesNotContain(Commands, x => x.Contains("RuntimeDocuments", StringComparison.Ordinal) || x.Contains("RuntimeAudioState", StringComparison.Ordinal) || x.Contains("RuntimeLearningState", StringComparison.Ordinal) || x.Contains("RuntimeTrainingRows", StringComparison.Ordinal));
        }
    }
    private sealed class QueryContextFactory(string connection, QueryReadCapture capture) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() => new(new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(connection).AddInterceptors(capture).Options);
        public Task<SessionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private static LearningItem QueryCard(Guid id, DateTimeOffset due) => new(id, LearningDomain.French, "Synthetic query prompt", "Synthetic answer", 0, 5, due, due.AddDays(-1));
    private static async Task AddQueryCard(SessionDbContext db, LearningItem card, bool legacyRounded = false)
    {
        var json = JsonSerializer.Serialize(card);
        if (legacyRounded) await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"RuntimeLearningRows\" (\"Kind\",\"Id\",\"Json\",\"DueAt\") VALUES ('Learning',{card.Id},CAST({json} AS jsonb),CAST(CAST({json} AS jsonb)->>'DueAt' AS timestamptz))");
        else { db.RuntimeLearningRows.Add(new() { Kind = "Learning", Id = card.Id, Json = json, DueAt = card.DueAt.ToUniversalTime() }); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
    }

    [PostgresFact]
    public async Task Indexed_learning_reads_are_async_and_skip_unrelated_histories_and_future_payloads()
    {
        await using var database = await TestDatabase.CreateAsync(); var capture = new QueryReadCapture();
        using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new QueryContextFactory(database.ConnectionString, capture));
        }));
        using var client = host.CreateClient(); var reader = host.Services.GetRequiredService<IRuntimeReader>();
        var cutoff = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero); var card = QueryCard(Guid.NewGuid(), cutoff.AddDays(-1).AddTicks(7).ToOffset(TimeSpan.FromHours(5.5)));
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync()) {
            await AddQueryCard(db, card);
            db.RuntimeLearningRows.AddRange(new RuntimeLearningRow { Kind = "Reviews", Id = Guid.NewGuid(), Json = "null" },
                new RuntimeLearningRow { Kind = "Learning", Id = Guid.NewGuid(), Json = "null", DueAt = cutoff.AddDays(1) });
            await db.SaveChangesAsync();
        }
        capture.Begin(); Assert.Equal(card, await reader.ReadLearningItemAsync(card.Id)); capture.AssertNarrow();
        capture.Begin(); Assert.Equal(card, Assert.Single(await reader.ReadLearningItemsAsync(true, cutoff))); capture.AssertNarrow();
        capture.Begin(); var page = await reader.ReadDueLearningPageAsync(cutoff, 1);
        Assert.Equal(card, Assert.Single(page.Items)); Assert.Equal(1, page.CandidatesRead); Assert.Null(page.Next); capture.AssertNarrow();
        Assert.Contains(capture.Commands, x => x.Contains("LIMIT", StringComparison.Ordinal));
        capture.Enabled = false;
    }

    [PostgresFact]
    public async Task Indexed_learning_pages_keep_exact_due_precision_and_advance_through_empty_candidate_pages()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        using var client = host.CreateClient(); var reader = host.Services.GetRequiredService<IRuntimeReader>();
        var cutoff = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero).AddTicks(7);
        var future = QueryCard(Guid.Parse("00000000-0000-0000-0000-000000000001"), cutoff.AddTicks(1));
        var due = QueryCard(Guid.Parse("00000000-0000-0000-0000-000000000002"), cutoff.ToOffset(TimeSpan.FromHours(5.5)));
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync()) {
            await AddQueryCard(db, future); await AddQueryCard(db, due, legacyRounded: true);
        }
        var first = await reader.ReadDueLearningPageAsync(cutoff, 1);
        Assert.Empty(first.Items); Assert.NotNull(first.Next); Assert.Equal(2, first.CandidatesRead);
        var second = await reader.ReadDueLearningPageAsync(cutoff, 1, first.Next);
        Assert.Equal(due, Assert.Single(second.Items)); Assert.Null(second.Next);
        Assert.Equal(TimeSpan.FromHours(5.5), second.Items[0].DueAt.Offset);
        Assert.Equal(cutoff.UtcTicks, second.Items[0].DueAt.UtcTicks);
        Assert.Equal(due, Assert.Single(await reader.ReadLearningItemsAsync(true, cutoff)));
        Assert.Equal(new[] { due, future }, await reader.ReadLearningItemsAsync(false, cutoff));
    }

    [PostgresFact]
    public async Task Indexed_learning_pages_are_privacy_fenced_and_reads_do_not_rewrite_rows()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        using var client = host.CreateClient(); var reader = host.Services.GetRequiredService<IRuntimeReader>();
        var cutoff = DateTimeOffset.UtcNow; var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync()) { for (var i = 0; i < 3; i++) await AddQueryCard(db, QueryCard(Guid.NewGuid(), cutoff.AddDays(-1))); }
        async Task<string> Stamp() { await using var db = await contexts.CreateDbContextAsync(); return await db.Database.SqlQueryRaw<string>("SELECT string_agg(xmin::text,',' ORDER BY \"Id\") AS \"Value\" FROM \"RuntimeLearningRows\"").SingleAsync(); }
        var before = await Stamp(); var first = await reader.ReadDueLearningPageAsync(cutoff, 1); Assert.NotNull(first.Next);
        Assert.Null(await reader.ReadLearningItemAsync(Guid.NewGuid())); await reader.ReadLearningItemsAsync(false, cutoff); Assert.Equal(before, await Stamp());
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        await privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), new string('b', 64));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => reader.ReadDueLearningPageAsync(cutoff, 1, first.Next))).StatusCode);
        using (PrivacyContext.Enter(status.Epoch)) Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => reader.ReadLearningItemAsync(Guid.NewGuid()))).StatusCode);
        Assert.Empty((await reader.ReadDueLearningPageAsync(cutoff, 1)).Items);
    }

    [PostgresFact]
    public async Task Indexed_learning_missing_stamp_refuses_paging_but_legacy_lists_preserve_canonical_values()
    {
        await using var database = await TestDatabase.CreateAsync(); using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        using var client = host.CreateClient(); var card = QueryCard(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1));
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync()) {
            db.RuntimeLearningRows.Add(new() { Kind = "Learning", Id = card.Id, Json = JsonSerializer.Serialize(card), DueAt = null }); await db.SaveChangesAsync();
        }
        var reader = host.Services.GetRequiredService<IRuntimeReader>();
        Assert.Equal(card, Assert.Single(await reader.ReadLearningItemsAsync(true, DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadDueLearningPageAsync(DateTimeOffset.UtcNow, 1));
    }
}
