using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
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
    public async Task Learning_overview_reads_metadata_and_recent_events_without_cold_or_replay_payloads()
    {
        await using var database = await TestDatabase.CreateAsync(); var capture = new QueryReadCapture();
        using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new QueryContextFactory(database.ConnectionString, capture));
        }));
        var reader = host.Services.GetRequiredService<IRuntimeReader>();
        var factory = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        var now = DateTimeOffset.Parse("2026-11-27T09:00:00Z").AddTicks(7); var since = now.AddDays(-7);
        var first = QueryCard(Guid.NewGuid(), now.AddTicks(-1));
        var second = QueryCard(Guid.NewGuid(), now.AddHours(24).AddTicks(-1).ToOffset(TimeSpan.FromHours(5.5))) with { Domain = LearningDomain.Databases };
        var last = QueryCard(Guid.NewGuid(), now.AddDays(7));
        var reviews = new[] { LearningHistoryFixture.Review(first.Id, since), LearningHistoryFixture.Review(first.Id, since.AddTicks(1)),
            LearningHistoryFixture.Review(second.Id, now), LearningHistoryFixture.Review(first.Id, now.AddTicks(1)),
            LearningHistoryFixture.Review(Guid.NewGuid(), now) };
        reviews[1] = reviews[1] with { Event = reviews[1].Event with { Rating = ReviewRating.Again } };
        reviews[2] = reviews[2] with { Event = reviews[2].Event with { Rating = ReviewRating.Hard } };
        await using (var db = await factory.CreateDbContextAsync()) {
            foreach (var card in new[] { first, second, last }) await AddQueryCard(db, card);
            foreach (var review in reviews) db.RuntimeLearningRows.Add(new() { Kind = "Reviews", Id = review.RequestId,
                Json = JsonSerializer.Serialize(new { review.RequestId, review.Event, Result = "unrequested replay body" }) });
            db.RuntimeLearningRows.Add(new() { Kind = "TrainingBaselines", Id = Guid.NewGuid(), Json = "null" });
            await db.SaveChangesAsync();
        }
        capture.Enabled = true;
        var actual = await reader.ReadLearningOverviewAsync(now);
        var expected = LearningOverviewBuilder.Build([first, second, last], reviews.Select(r => r.Event), now);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(2, actual.Reviewed7Days); Assert.Equal(0.5, actual.RecallRate7Days);
        capture.AssertNarrow();
        Assert.Contains(capture.Commands, q => q.Contains("jsonb_build_object", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, q => q.Contains("->'Result'", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Learning_overview_rejects_mismatched_selected_row_identities()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        var reader = host.Services.GetRequiredService<IRuntimeReader>();
        var factory = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        var now = DateTimeOffset.UtcNow; var card = QueryCard(Guid.NewGuid(), now);
        await using var db = await factory.CreateDbContextAsync();
        var bad = new RuntimeLearningRow { Kind = "Learning", Id = Guid.NewGuid(), Json = JsonSerializer.Serialize(card), DueAt = now };
        db.RuntimeLearningRows.Add(bad); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLearningOverviewAsync(now));
        db.RuntimeLearningRows.Remove(bad); await db.SaveChangesAsync();
        await AddQueryCard(db, card);
        var review = LearningHistoryFixture.Review(card.Id, now);
        db.RuntimeLearningRows.Add(new() { Kind = "Reviews", Id = Guid.NewGuid(), Json = JsonSerializer.Serialize(review) }); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLearningOverviewAsync(now));
    }

    [PostgresFact]
    public async Task Learning_overview_waits_for_privacy_lock_and_rejects_erased_state()
    {
        await using var database = await TestDatabase.CreateAsync(); var capture = new QueryReadCapture();
        using var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new QueryContextFactory(database.ConnectionString, capture));
        }));
        var reader = host.Services.GetRequiredService<IRuntimeReader>();
        var factory = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await using (var tx = await db.Database.BeginTransactionAsync()) {
            await db.Database.ExecuteSqlRawAsync("SELECT \"Id\" FROM \"PrivacyControls\" WHERE \"Id\"=1 FOR UPDATE");
            capture.Enabled = true;
            var pending = reader.ReadLearningOverviewAsync(DateTimeOffset.UtcNow);
            for (var i = 0; i < 200 && !capture.Commands.Any(q => q.Contains("FOR SHARE", StringComparison.Ordinal)); i++) await Task.Delay(10);
            Assert.Contains(capture.Commands, q => q.Contains("FOR SHARE", StringComparison.Ordinal)); Assert.False(pending.IsCompleted);
            await tx.CommitAsync(); Assert.Equal(0, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Cards);
        }
        await db.Database.ExecuteSqlRawAsync("UPDATE \"PrivacyControls\" SET \"Erased\"=true WHERE \"Id\"=1");
        await Assert.ThrowsAsync<DomainException>(() => reader.ReadLearningOverviewAsync(DateTimeOffset.UtcNow));
    }
}
