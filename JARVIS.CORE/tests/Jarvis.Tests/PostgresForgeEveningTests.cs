using System.Text.Json;
using Jarvis.Contracts;
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
    [PostgresTheory]
    [InlineData("2027-03-27T23:30:00Z", "2027-03-28", "2027-03-28T23:00:00Z")]
    [InlineData("2027-10-30T22:30:00Z", "2027-10-31", "2027-11-01T00:00:00Z")]
    public async Task Forge_evening_postgres_exact_dst_windows_and_minimised_output_are_read_only(string atText, string dateText, string endText)
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock { Now = DateTimeOffset.Parse(atText) }; var end = DateTimeOffset.Parse(endText);
        using var root = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => { b.UseSetting("Forge:Enabled", "true"); b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()); });
        using var epoch = PrivacyContext.Enter(0); var store = host.Services.GetRequiredService<IRuntimeStore>();
        await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        await store.UpdateAsync(s => {
            foreach (var time in new[] { end.AddTicks(-1), end, end.AddTicks(1) }) {
                var card = new LearningItem(Guid.NewGuid(), LearningDomain.French, "PRIVATE_EVENING_SQL", "PRIVATE_EVENING_SQL", 1, 5, time, clock.Now);
                s.Learning[card.Id] = card;
            }
            foreach (var time in new[] { clock.Now, clock.Now.AddTicks(1) }) {
                var item = new LearningItem(Guid.NewGuid(), LearningDomain.Communication, "PRIVATE_EVENING_SQL", "PRIVATE_EVENING_SQL", 1, 5, end.AddDays(1), clock.Now);
                var review = new LearningReview(Guid.NewGuid(), new(Guid.NewGuid(), item.Id, ReviewRating.Good, TimeSpan.Zero, time), item);
                s.Reviews[review.RequestId] = review;
            }
            var goal = ForgeTodayReviewTests.Goal("PRIVATE_EVENING_SQL", "forge.wealth", 0); s.Goals[goal.Id] = goal; return true;
        });
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TodayStamp(contexts);
        var service = host.Services.GetRequiredService<ForgeTodayService>(); var evening = await service.BuildEveningAsync(clock.Now); Assert.NotNull(evening);
        Assert.Equal(DateOnly.Parse(dateText), evening.TomorrowDate); Assert.Equal(1, evening.Tomorrow.DueReviews); Assert.False(evening.Tomorrow.Scheduled);
        Assert.Equal(1, evening.Today.Reviews); Assert.Equal(clock.Now, evening.Today.AsOf); Assert.Equal(evening.Today.From, evening.Today.To);
        Assert.DoesNotContain("PRIVATE_EVENING_SQL", JsonSerializer.Serialize(evening));
        Assert.Equal(JsonSerializer.Serialize(evening), JsonSerializer.Serialize(await service.BuildEveningAsync(clock.Now)));
        Assert.Equal(stamp, await TodayStamp(contexts));
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Forge_evening_postgres_refuses_stale_or_erased_epoch(bool erased)
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock { Now = ForgeTodayReviewTests.Now };
        using var root = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => { b.UseSetting("Forge:Enabled", "true"); b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()); });
        using (PrivacyContext.Enter(0)) await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        await using var db = SchemaContext(database.ConnectionString); if (erased) await db.Database.ExecuteSqlRawAsync("UPDATE \"PrivacyControls\" SET \"Erased\"=true WHERE \"Id\"=1");
        using (PrivacyContext.Enter(erased ? 0 : 99))
            Assert.Equal(erased ? 503 : 409, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<ForgeTodayService>().BuildEveningAsync(clock.Now))).StatusCode);
    }
}
