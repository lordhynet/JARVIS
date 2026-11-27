using System.Text;
using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Devices;
using Jarvis.Orchestrator.Cqrs.Integrations;
using Jarvis.Orchestrator.Cqrs.Learning;
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
    public async Task Forge_today_postgres_exact_boundary_and_protected_labels_are_read_only()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock { Now = ForgeTodayReviewTests.Now };
        using var root = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => { b.UseSetting("Forge:Enabled", "true"); b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()); });
        using var epoch = PrivacyContext.Enter(0); var store = host.Services.GetRequiredService<IRuntimeStore>();
        await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        var goal = ForgeTodayReviewTests.Goal("PRIVATE_FINANCIAL_SENTINEL", "forge.wealth", 7);
        await store.UpdateAsync(s => {
            s.Goals[goal.Id] = goal;
            foreach (var at in new[] { ForgeTodayReviewTests.End.AddTicks(-1), ForgeTodayReviewTests.End, ForgeTodayReviewTests.End.AddTicks(1) }) {
                var id = Guid.NewGuid(); s.Learning[id] = new(id, LearningDomain.French, "Fixture", "Fixture", 1, 5, at, clock.Now);
            }
            return true;
        });
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TodayStamp(contexts);
        var service = host.Services.GetRequiredService<ForgeTodayService>(); var result = await service.BuildAsync(clock.Now); Assert.NotNull(result);
        Assert.Equal(1, result.DueReviews); Assert.False(result.Scheduled); Assert.Equal(new[] { "French review", "WEALTH goal" }, result.Top.Select(t => t.Label));
        Assert.DoesNotContain(goal.Title, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await service.BuildAsync(clock.Now)));
        Assert.Equal(stamp, await TodayStamp(contexts));
    }

    [PostgresFact]
    public async Task Forge_today_postgres_and_voice_share_minimised_projection_without_writes()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock { Now = ForgeTodayReviewTests.Now };
        using var root = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => { b.UseSetting("Forge:Enabled", "true"); b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()); });
        using var epoch = PrivacyContext.Enter(0); var store = host.Services.GetRequiredService<IRuntimeStore>();
        await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        await store.UpdateAsync(s => { var goal = ForgeTodayReviewTests.Goal("Fixture technical work", "forge.tech", 0); s.Goals[goal.Id] = goal; return true; });
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TodayStamp(contexts);
        var service = host.Services.GetRequiredService<ForgeTodayService>(); var direct = await service.BuildAsync(clock.Now);
        var voice = await new VoiceBriefingService(new EmptyTodayCalendar(), new EmptyTodayLearning(), new EmptyTodayCommands(), store, clock, null, service).BuildAsync();
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(JsonSerializer.Serialize(direct, web), JsonSerializer.Serialize(voice.Forge, web));
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(voice, web)) <= VoiceBriefingService.MaximumBytes);
        Assert.Equal(stamp, await TodayStamp(contexts));
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Forge_today_postgres_refuses_stale_or_erased_epoch_without_publishing(bool erased)
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new ForgeClock { Now = ForgeTodayReviewTests.Now };
        using var root = new PostgresApiFactory(database.ConnectionString, clock, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => { b.UseSetting("Forge:Enabled", "true"); b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()); });
        using (PrivacyContext.Enter(0)) await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        await using var db = SchemaContext(database.ConnectionString); if (erased) await db.Database.ExecuteSqlRawAsync("UPDATE \"PrivacyControls\" SET \"Erased\"=true WHERE \"Id\"=1");
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TodayStamp(contexts);
        using (PrivacyContext.Enter(erased ? 0 : 99)) {
            Assert.Equal(erased ? 503 : 409, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<ForgeTodayService>().BuildAsync(clock.Now))).StatusCode);
        }
        Assert.Equal(stamp, await TodayStamp(contexts));
    }

    private static async Task<string> TodayStamp(IDbContextFactory<SessionDbContext> contexts)
    {
        var runtime = await TrainingFootprint.Stamp(contexts);
        await using var db = await contexts.CreateDbContextAsync();
        var programme = await db.Database.SqlQueryRaw<string>("SELECT string_agg(\"Id\"::text || ':' || xmin::text, ',' ORDER BY \"Id\") AS \"Value\" FROM \"ForgeProgrammes\"").SingleAsync();
        return runtime + ":programme:" + programme;
    }
    private sealed class EmptyTodayCalendar : IQueryHandler<GetCalendar, IReadOnlyList<CalendarEntry>>
    {
        public Task<IReadOnlyList<CalendarEntry>> HandleAsync(GetCalendar query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CalendarEntry>>([]);
    }
    private sealed class EmptyTodayLearning : IQueryHandler<ListLearningItems, IReadOnlyList<LearningItem>>
    {
        public Task<IReadOnlyList<LearningItem>> HandleAsync(ListLearningItems query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LearningItem>>([]);
    }
    private sealed class EmptyTodayCommands : IQueryHandler<ListDeviceCommandsForReview, IReadOnlyList<DeviceCommandView>>
    {
        public Task<IReadOnlyList<DeviceCommandView>> HandleAsync(ListDeviceCommandsForReview query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeviceCommandView>>([]);
    }
}
