using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Forge_week_postgres_counts_exact_local_window_and_catalogue_provenance_without_writes()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock { Now = DateTimeOffset.Parse("2027-03-28T12:00:00Z") };
        using var root = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => { b.UseSetting("Forge:Enabled", "true"); b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()); });
        using var epoch = PrivacyContext.Enter(0); var store = host.Services.GetRequiredService<IRuntimeStore>();
        await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        var start = DateTimeOffset.Parse("2027-03-22T00:00:00Z"); var end = DateTimeOffset.Parse("2027-03-28T23:00:00Z");
        await store.UpdateAsync(s => {
            foreach (var at in new[] { start.AddTicks(-1), start, clock.Now, clock.Now.AddTicks(1), end }) {
                var card = new LearningItem(Guid.NewGuid(), LearningDomain.French, "PRIVATE_WEEK_SQL", "PRIVATE_WEEK_SQL", 1, 5, at, start);
                var review = new LearningReview(Guid.NewGuid(), new(Guid.NewGuid(), card.Id, ReviewRating.Good, TimeSpan.FromSeconds(1), at), card);
                s.Learning[card.Id] = card; s.Reviews[review.RequestId] = review;
            }
            foreach (var exercise in new[] { "point-first", "recruiter", "unknown-exercise" }) {
                var a = WeekSqlAnswer(exercise, clock.Now); s.CoachingAttempts[a.Id] = a;
            }
            var goal = ForgeTodayReviewTests.Goal("PRIVATE_WEEK_GOAL", "forge.wealth", 0); s.Goals[goal.Id] = goal; return true;
        });
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TodayStamp(contexts);
        var service = host.Services.GetRequiredService<ForgeTodayService>(); var week = await service.BuildWeekAsync(clock.Now); Assert.NotNull(week);
        Assert.Equal(new DateOnly(2027, 3, 22), week.From); Assert.Equal(new DateOnly(2027, 3, 28), week.To);
        Assert.Equal(clock.Now, week.AsOf); Assert.Equal(2, week.Reviews); Assert.Equal(3, week.CoachingAnswers);
        Assert.Equal(1, week.UnclassifiedCoachingAnswers); Assert.Null(week.PracticeCoverage);
        Assert.Equal(1, week.Divisions.Single(d => d.Key == "forge.voice").CoachingAnswers);
        Assert.Equal(1, week.Divisions.Single(d => d.Key == "forge.career").CoachingAnswers);
        Assert.DoesNotContain("PRIVATE_WEEK", JsonSerializer.Serialize(week));
        Assert.Equal(JsonSerializer.Serialize(week), JsonSerializer.Serialize(await service.BuildWeekAsync(clock.Now)));
        Assert.Equal(stamp, await TodayStamp(contexts));
    }

    [PostgresFact]
    public async Task Forge_week_postgres_canonical_coaching_deletion_removes_credit_without_new_history()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock { Now = ForgeTodayReviewTests.Now };
        using var root = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => { b.UseSetting("Forge:Enabled", "true"); b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()); });
        using var epoch = PrivacyContext.Enter(0); var store = host.Services.GetRequiredService<IRuntimeStore>();
        await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        var answer = WeekSqlAnswer("point-first", clock.Now);
        await store.UpdateAsync(s => { s.CoachingAttempts[answer.Id] = answer; return true; });
        var service = host.Services.GetRequiredService<ForgeTodayService>(); Assert.Equal(1, (await service.BuildWeekAsync(clock.Now))!.CoachingAnswers);
        Assert.True(await host.Services.GetRequiredService<CoachingService>().DeleteAsync(answer.Id, 1, default));
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TodayStamp(contexts);
        var week = await service.BuildWeekAsync(clock.Now); Assert.Equal(0, week!.CoachingAnswers); Assert.Empty(week.Divisions); Assert.Null(week.PracticeCoverage);
        Assert.Equal(stamp, await TodayStamp(contexts));
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Forge_week_postgres_refuses_stale_or_erased_epochs(bool erased)
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock { Now = ForgeTodayReviewTests.Now };
        using var root = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => { b.UseSetting("Forge:Enabled", "true"); b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()); });
        using (PrivacyContext.Enter(0)) await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        await using var db = SchemaContext(database.ConnectionString); if (erased) await db.Database.ExecuteSqlRawAsync("UPDATE \"PrivacyControls\" SET \"Erased\"=true WHERE \"Id\"=1");
        using (PrivacyContext.Enter(erased ? 0 : 99))
            Assert.Equal(erased ? 503 : 409, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<ForgeTodayService>().BuildWeekAsync(clock.Now))).StatusCode);
    }

    private static CoachingAttempt WeekSqlAnswer(string exercise, DateTimeOffset at) => new(Guid.NewGuid(), Guid.NewGuid(), exercise, 1,
        null, null, null, 1, at, [new(1, "PRIVATE_WEEK_SQL_ANSWER", null, at)]);
}
