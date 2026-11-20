using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    private static RuntimeLearningRow HistoryRow(LearningReview review, bool uppercase = false) => new() {
        Kind = nameof(RuntimeState.Reviews), Id = review.RequestId,
        Json = uppercase ? JsonSerializer.Serialize(review).Replace(review.Event.LearningItemId.ToString(), review.Event.LearningItemId.ToString().ToUpperInvariant()) : JsonSerializer.Serialize(review)
    };

    [PostgresFact]
    public async Task Learning_history_postgres_is_async_narrow_exact_and_available_without_a_card() {
        await using var database = await TestDatabase.CreateAsync(); var capture = new QueryReadCapture();
        using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new QueryContextFactory(database.ConnectionString, capture));
        }));
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner);
        var reader = host.Services.GetRequiredService<IRuntimeReader>();
        var card = Guid.NewGuid(); var at = LearningHistoryFixture.At;
        var first = LearningHistoryFixture.Review(card, at.AddTicks(1));
        var middle = LearningHistoryFixture.Review(card, at.AddTicks(7).ToOffset(TimeSpan.FromHours(5.5)));
        var last = LearningHistoryFixture.Review(card, at.AddTicks(9).ToOffset(TimeSpan.FromHours(-4)));
        var tie = LearningHistoryFixture.Review(card, last.Event.ReviewedAt.ToOffset(TimeSpan.Zero));
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync()) {
            db.RuntimeLearningRows.AddRange(HistoryRow(first), HistoryRow(middle, true), HistoryRow(last), HistoryRow(tie),
                new() { Kind = "Reviews", Id = Guid.NewGuid(), Json = "null" },
                new() { Kind = "Reviews", Id = Guid.NewGuid(), Json = JsonSerializer.Serialize(new { Event = new { LearningItemId = Guid.NewGuid() }, FuturePrivateField = "unrelated future payload" }) },
                new() { Kind = "Learning", Id = Guid.NewGuid(), Json = "null" });
            await db.SaveChangesAsync();
        }
        capture.Begin(); var result = await reader.ReadLearningHistoryAsync(card); capture.AssertNarrow();
        Assert.Equal(4, result.Count); Assert.Equal(first.Event.Id, result[^1].Id); Assert.Equal(middle.Event.Id, result[2].Id);
        Assert.Equal(new[] { last.Event.Id, tie.Event.Id }.Order(), result.Take(2).Select(x => x.Id).Order());
        Assert.Equal(middle.Event.ReviewedAt.Ticks, result[2].ReviewedAt.Ticks); Assert.Equal(TimeSpan.FromHours(5.5), result[2].ReviewedAt.Offset);
        Assert.Contains(capture.Commands, x => x.Contains("lower(", StringComparison.Ordinal) && x.Contains("LearningItemId", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, x => x.Contains("FOR UPDATE", StringComparison.Ordinal) || x.Contains(card.ToString(), StringComparison.Ordinal));
        Assert.All(capture.Commands, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        capture.Enabled = false;
        var endpoint = await owner.GetFromJsonAsync<ReviewEvent[]>($"/api/learning/{card}/reviews", LifeScenarioTests.Json);
        Assert.Equal(result.Select(x => x.Id).Order(), endpoint!.Select(x => x.Id).Order());
        Assert.Empty(await reader.ReadLearningHistoryAsync(Guid.NewGuid())); Assert.Empty(await reader.ReadLearningHistoryAsync(Guid.Empty));
        using (PrivacyContext.Enter(1)) Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => reader.ReadLearningHistoryAsync(card))).StatusCode);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadLearningHistoryAsync(card, cancel.Token));
        await using var verify = await contexts.CreateDbContextAsync();
        Assert.Equal(7, await verify.RuntimeLearningRows.CountAsync()); Assert.Equal(1, (await verify.RuntimeLearningState.SingleAsync()).NextTrainingSequence);
    }

    [PostgresFact]
    public async Task Learning_history_postgres_rejects_selected_request_identity_corruption() {
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        var reader = host.Services.GetRequiredService<IRuntimeReader>(); var card = Guid.NewGuid(); var row = HistoryRow(LearningHistoryFixture.Review(card, LearningHistoryFixture.At));
        row.Id = Guid.NewGuid();
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        db.RuntimeLearningRows.Add(row); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLearningHistoryAsync(card));
    }

    [PostgresFact]
    public async Task Learning_history_index_down_up_preserves_existing_json_and_legacy_unrelated_rows() {
        await using var database = await TestDatabase.CreateAsync(); using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var card = Guid.NewGuid(); var review = LearningHistoryFixture.Review(card, LearningHistoryFixture.At.AddTicks(7));
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        db.RuntimeLearningRows.AddRange(HistoryRow(review), new() { Kind = "Reviews", Id = Guid.NewGuid(), Json = "null" }); await db.SaveChangesAsync();
        var before = await db.RuntimeLearningRows.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Json).ToArrayAsync();
        async Task<bool> IndexExists() => await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS(SELECT 1 FROM pg_indexes WHERE schemaname='public' AND indexname='IX_RuntimeLearningRows_ReviewCard') AS "Value"
            """).SingleAsync();
        Assert.True(await IndexExists());
        await db.Database.GetService<IMigrator>().MigrateAsync("20261120104352_ForgeProtectedRecords"); Assert.False(await IndexExists());
        Assert.Equal(review.Event, Assert.Single(await host.Services.GetRequiredService<IRuntimeReader>().ReadLearningHistoryAsync(card)));
        await db.Database.MigrateAsync(); Assert.True(await IndexExists());
        Assert.Equal(before, await db.RuntimeLearningRows.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Json).ToArrayAsync());
    }
}
