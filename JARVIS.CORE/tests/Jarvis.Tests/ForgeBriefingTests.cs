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
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ForgeBriefingTests
{
    // 07:30 London on programme day 2 (2 December 2026, GMT).
    private static readonly DateTimeOffset Now = new(2026, 12, 2, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndOfDay = new(2026, 12, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ForgeProgramme Programme(bool paused = false) => new(Guid.NewGuid(), ForgeProgrammePolicy.OwnerScope, 1, 1,
        ForgeCalendar.DefaultStart, ForgeCalendar.DefaultGraduation, ForgeCalendar.DefaultTimeZone, paused, Now.AddDays(-5), Now.AddDays(-5));
    private static PersonalGoal Goal(string title, string division, int deadlineDays = 10) => new(Guid.NewGuid(), 1, title, "Brief",
        "practice-reminder", false, Now.AddHours(-1), EndOfDay.AddDays(deadlineDays), GoalStatus.Active, Now.AddDays(-3), Now.AddDays(-3), DivisionKey: division);
    private static LearningItem Card(LearningDomain domain) => new(Guid.NewGuid(), domain, "Prompt", "Answer", 0, 5, Now.AddHours(-2), Now.AddDays(-9));

    [Fact]
    public void Suggestions_lead_with_due_recall_and_never_speak_protected_goal_titles()
    {
        var programme = Programme();
        var goals = new[] { Goal("Pay off the credit card balance", "forge.wealth", 2), Goal("Build the payment API", "forge.tech", 4) };
        var candidates = ForgePlanCandidates.Build(EndOfDay, [Card(LearningDomain.French), Card(LearningDomain.French)], goals, Now);
        var briefing = ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, Now).Calendar, candidates, goals, 2);

        Assert.Equal("active", briefing.Status); Assert.Equal(2, briefing.Day); Assert.Equal(261, briefing.Days);
        Assert.Equal("Foundation and reset", briefing.Phase); Assert.False(briefing.Scheduled); Assert.Equal(2, briefing.DueReviews);
        Assert.Equal(new[] { "French review", "WEALTH goal", "TECH: Build the payment API" }, briefing.Top.Select(x => x.Label).ToArray());
        Assert.Equal("due recall", briefing.Top[0].Why); Assert.Equal("goal review", briefing.Top[1].Why);
        Assert.DoesNotContain("credit card", JsonSerializer.Serialize(briefing, Web), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_capacity_backed_plan_is_reported_as_scheduled_in_placement_order()
    {
        var programme = Programme();
        var goals = new[] { Goal("Build the payment API", "forge.tech") };
        var candidates = ForgePlanCandidates.Build(EndOfDay, [Card(LearningDomain.Databases)], goals, Now);
        var plan = new ForgeDayPlan(ForgePlanStatus.Planned, new(2026, 12, 2), 1, 1, 120, 65, [
            new("goal." + goals[0].Id.ToString("N"), ForgePlanTaskKind.Practice, Now.AddHours(10), Now.AddHours(11), false),
            new("review.databases", ForgePlanTaskKind.Review, Now.AddHours(1), Now.AddHours(1).AddMinutes(5), false)], []);
        var briefing = ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, Now).Calendar, candidates, goals, 1, plan);
        Assert.True(briefing.Scheduled);
        Assert.Equal(new[] { "Databases review", "TECH: Build the payment API" }, briefing.Top.Select(x => x.Label).ToArray());
        Assert.Equal(new[] { 5, 60 }, briefing.Top.Select(x => x.Minutes).ToArray());
    }

    [Fact]
    public void Before_start_paused_and_graduated_programmes_give_no_suggestions()
    {
        var programme = Programme();
        var none = new ForgeCandidateSet([], 0);
        var before = ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero)).Calendar, none, [], 0);
        Assert.Equal("not-started", before.Status); Assert.Equal(3, before.StartsInDays); Assert.Empty(before.Top);
        var paused = Programme(paused: true);
        var pausedBriefing = ForgeBriefingProjection.Build(paused, ForgeProgrammePolicy.View(paused, Now).Calendar,
            ForgePlanCandidates.Build(EndOfDay, [Card(LearningDomain.French)], [], Now), [], 1);
        Assert.Equal("paused", pausedBriefing.Status); Assert.Empty(pausedBriefing.Top);
        var after = ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, new DateTimeOffset(2027, 9, 1, 9, 0, 0, TimeSpan.Zero)).Calendar, none, [], 0);
        Assert.Equal("graduated", after.Status); Assert.Empty(after.Top);
    }

    // --- Integration with the existing spoken briefing -------------------------------------------------

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Calendars(IReadOnlyList<CalendarEntry> entries) : IQueryHandler<GetCalendar, IReadOnlyList<CalendarEntry>>
    {
        public Task<IReadOnlyList<CalendarEntry>> HandleAsync(GetCalendar query, CancellationToken ct = default) =>
            Task.FromResult(query.Provider == "google" ? entries : (IReadOnlyList<CalendarEntry>)[]);
    }
    private sealed class NoDue : IQueryHandler<ListLearningItems, IReadOnlyList<LearningItem>>
    {
        public Task<IReadOnlyList<LearningItem>> HandleAsync(ListLearningItems query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LearningItem>>([]);
    }
    private sealed class NoCommands : IQueryHandler<ListDeviceCommandsForReview, IReadOnlyList<DeviceCommandView>>
    {
        public Task<IReadOnlyList<DeviceCommandView>> HandleAsync(ListDeviceCommandsForReview query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeviceCommandView>>([]);
    }
    private sealed class Availability(bool on) : IForgeAvailability { public bool Enabled => on; }
    private sealed class Programmes(params ForgeProgramme[] items) : IForgeProgrammeReader
    {
        public Task<ForgeProgramme?> GetAsync(string ownerScope, Guid id, CancellationToken ct = default) => Task.FromResult(items.FirstOrDefault(x => x.Id == id));
        public Task<IReadOnlyList<ForgeProgramme>> ListAsync(string ownerScope, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ForgeProgramme>>(items);
    }

    private static async Task Seed(JarvisApiFactory host, params PersonalGoal[] goals)
    {
        var store = host.Services.GetRequiredService<IRuntimeStore>();
        await store.UpdateAsync(s => {
            foreach (var goal in goals) s.Goals[goal.Id] = goal;
            var card = Card(LearningDomain.French); s.Learning[card.Id] = card;
            return true;
        });
    }

    private static VoiceBriefingService Service(JarvisApiFactory host, IReadOnlyList<CalendarEntry> entries, bool forge, params ForgeProgramme[] programmes) =>
        new(new Calendars(entries), new NoDue(), new NoCommands(), host.Services.GetRequiredService<IRuntimeReader>(), new Clock(Now),
            null, new ForgeTodayService(new Availability(forge), new Programmes(programmes), host.Services.GetRequiredService<IRuntimeReader>()));

    [Fact]
    public async Task Today_endpoint_is_owner_only_forge_gated_and_matches_the_briefing_shape()
    {
        using (var disabled = new ForgeApiFactory(enabled: false)) {
            using var owner = disabled.Owner();
            using var response = await owner.GetAsync("/api/forge/today");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
        using var factory = new ForgeApiFactory(); using var client = factory.Owner();
        using (var none = await client.GetAsync("/api/forge/today"))
            Assert.Equal(System.Net.HttpStatusCode.NotFound, none.StatusCode); // no programme yet
        await LifeScenarioTests.Post<ForgeProgramme>(client, "/api/forge/programmes", new CreateForgeProgrammeRequest(Guid.NewGuid()));
        var today = (await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<ForgeBriefing>(client, "/api/forge/today", LifeScenarioTests.Json))!;
        // The default test clock is 30 November, the day before the programme starts.
        Assert.Equal("not-started", today.Status); Assert.Equal(1, today.StartsInDays); Assert.Empty(today.Top);
        using var anonymous = factory.CreateClient();
        using var refused = await anonymous.GetAsync("/api/forge/today");
        Assert.False(refused.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Briefing_is_unchanged_without_forge_and_adds_a_redacted_block_when_enabled()
    {
        using var host = new JarvisApiFactory();
        await Seed(host, Goal("Clear the overdraft", "forge.wealth", 1));
        var programme = Programme();

        var off = await Service(host, [], forge: false, programme).BuildAsync();
        Assert.Null(off.Forge);
        Assert.DoesNotContain("\"forge\"", JsonSerializer.Serialize(off, Web), StringComparison.Ordinal);
        var noProgramme = await Service(host, [], forge: true).BuildAsync();
        Assert.Null(noProgramme.Forge);

        var on = await Service(host, [], forge: true, programme).BuildAsync();
        Assert.NotNull(on.Forge);
        Assert.Equal(2, on.Forge!.Day); Assert.Equal(1, on.Forge.DueReviews);
        Assert.Equal(new[] { "French review", "WEALTH goal" }, on.Forge.Top.Select(x => x.Label).ToArray());
        var json = JsonSerializer.Serialize(on, Web);
        Assert.DoesNotContain("overdraft", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= VoiceBriefingService.MaximumBytes);
    }

    [Fact]
    public async Task Calendar_entries_are_trimmed_before_the_forge_block_to_stay_within_the_byte_bound()
    {
        using var host = new JarvisApiFactory();
        await Seed(host, Goal(new string('T', 200), "forge.tech"), Goal("Story practice", "forge.career"));
        var entries = Enumerable.Range(0, 5).Select(i => new CalendarEntry(Guid.NewGuid().ToString(), new string('C', 60) + i,
            Now.AddHours(i + 1).ToString("O"), Now.AddHours(i + 2).ToString("O"), false, "google")).ToArray();
        var briefing = await Service(host, entries, forge: true, Programme()).BuildAsync();
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(briefing, Web));
        Assert.True(bytes <= VoiceBriefingService.MaximumBytes, $"{bytes} bytes");
        Assert.NotNull(briefing.Forge);
        Assert.NotEmpty(briefing.Forge!.Top);
        Assert.All(briefing.Forge.Top, x => Assert.True(x.Label.Length <= "TECH: ".Length + ForgeBriefingProjection.GoalLabelLength + 1));
    }
}
