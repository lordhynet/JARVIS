using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ForgeEveningReviewTests
{
    private static readonly DateTimeOffset Now = ForgeTodayReviewTests.Now;
    private static PersonalGoal Goal(string title, string division) => ForgeTodayReviewTests.Goal(title, division, 2);

    [Fact]
    public async Task Evening_context_and_suggestions_share_the_same_runtime_snapshot()
    {
        var reader = new ChangingRuntime();
        var service = new ForgeTodayService(new ForgeTodayReviewTests.AvailabilityFixture(true),
            new ForgeTodayReviewTests.Programmes(ForgeTodayReviewTests.Programme()), reader);
        var evening = await service.BuildEveningAsync(Now); Assert.NotNull(evening);
        Assert.Equal("forge.tech", Assert.Single(evening.Today.Divisions).Key);
        Assert.Equal("TECH: ORIGINAL_SNAPSHOT", Assert.Single(evening.Tomorrow.Top).Label);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task One_evening_uses_one_captured_programme_revision()
    {
        var programmes = new ChangingProgrammes(); IRuntimeStore store = new InMemoryRuntimeStore();
        await store.UpdateAsync(s => { var g = Goal("ORIGINAL_SNAPSHOT", "forge.tech"); s.Goals[g.Id] = g; return true; });
        var evening = await new ForgeTodayService(new ForgeTodayReviewTests.AvailabilityFixture(true), programmes, store).BuildEveningAsync(Now);
        Assert.NotNull(evening); Assert.Equal("active", evening.Tomorrow.Status); Assert.Equal(1, programmes.Reads);
    }

    [Theory]
    [InlineData("2027-03-27T23:30:00Z", "2027-03-28", "2027-03-28T23:00:00Z")]
    [InlineData("2027-10-30T22:30:00Z", "2027-10-31", "2027-11-01T00:00:00Z")]
    public async Task Tomorrow_uses_local_dates_and_exact_following_midnight_across_dst(string nowText, string dateText, string endText)
    {
        var now = DateTimeOffset.Parse(nowText); var end = DateTimeOffset.Parse(endText); IRuntimeStore store = new InMemoryRuntimeStore();
        await store.UpdateAsync(s => {
            foreach (var at in new[] { end.AddTicks(-1), end, end.AddTicks(1) }) { var card = Card(at); s.Learning[card.Id] = card; }
            foreach (var at in new[] { now, now.AddTicks(1) }) {
                var item = Card(at); var review = new LearningReview(Guid.NewGuid(), new(Guid.NewGuid(), item.Id, ReviewRating.Good, TimeSpan.Zero, at), item); s.Reviews[review.RequestId] = review;
            }
            return true;
        });
        var evening = await Service(store).BuildEveningAsync(now); Assert.NotNull(evening);
        Assert.Equal(DateOnly.Parse(dateText), evening.TomorrowDate); Assert.Equal(1, evening.Tomorrow.DueReviews);
        Assert.Equal(1, evening.Today.Reviews); Assert.Equal(now, evening.Today.AsOf); Assert.Equal(evening.Today.From, evening.Today.To);
    }

    [Fact]
    public async Task Future_suggestions_share_today_redaction_and_canonical_order_without_scheduling()
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); var goal = Goal("PRIVATE_EVENING_FINANCE", "forge.wealth");
        await store.UpdateAsync(s => { s.Goals[goal.Id] = goal; var card = Card(Now.AddDays(1)); s.Learning[card.Id] = card; return true; });
        var result = await Service(store).BuildEveningAsync(Now); Assert.NotNull(result);
        Assert.Equal(new[] { "French review", "WEALTH goal" }, result.Tomorrow.Top.Select(x => x.Label)); Assert.False(result.Tomorrow.Scheduled);
        var json = JsonSerializer.Serialize(result); Assert.DoesNotContain(goal.Title, json); Assert.DoesNotContain(goal.Id.ToString(), json);
        Assert.Null(result.Today.ActualMinutes); Assert.Null(result.Today.PlannedMinutes);
    }

    [Theory]
    [InlineData("2026-11-28T12:00:00Z", false, "not-started")]
    [InlineData("2026-12-02T12:00:00Z", true, "paused")]
    [InlineData("2027-08-18T12:00:00Z", false, "graduated")]
    public async Task Tomorrow_respects_programme_status_without_fabricated_tasks(string at, bool paused, string status)
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); var programme = ForgeTodayReviewTests.Programme() with { Paused = paused };
        var result = await new ForgeTodayService(new ForgeTodayReviewTests.AvailabilityFixture(true), new ForgeTodayReviewTests.Programmes(programme), store).BuildEveningAsync(DateTimeOffset.Parse(at));
        Assert.NotNull(result); Assert.Equal(status, result.Tomorrow.Status); Assert.Empty(result.Tomorrow.Top); Assert.False(result.Tomorrow.Scheduled);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Off_or_missing_programme_avoids_runtime_reads(bool enabled)
    {
        var result = await new ForgeTodayService(new ForgeTodayReviewTests.AvailabilityFixture(enabled), new ForgeTodayReviewTests.Programmes(), new ForbiddenRuntime()).BuildEveningAsync(Now);
        Assert.Null(result);
    }

    [Fact]
    public async Task Evening_endpoint_is_authenticated_and_missing_programme_is_404()
    {
        using var host = new ForgeApiFactory(); using var anonymous = host.CreateClient(); using var owner = host.Owner();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/forge/evening")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/forge/evening")).StatusCode);
    }

    private static ForgeTodayService Service(IRuntimeReader runtime) => new(new ForgeTodayReviewTests.AvailabilityFixture(true),
        new ForgeTodayReviewTests.Programmes(ForgeTodayReviewTests.Programme()), runtime);
    private static LearningItem Card(DateTimeOffset due) => new(Guid.NewGuid(), LearningDomain.French, "PRIVATE_EVENING_PROMPT", "PRIVATE_EVENING_ANSWER", 1, 5, due, Now);
    private sealed class ForbiddenRuntime : IRuntimeReader
    {
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected runtime read");
    }

    private sealed class ChangingRuntime : IRuntimeReader
    {
        public int Reads { get; private set; }
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default)
        {
            var s = new RuntimeState(); var g = ++Reads == 1 ? Goal("ORIGINAL_SNAPSHOT", "forge.tech") : Goal("LATER_SNAPSHOT", "forge.social");
            s.Goals[g.Id] = g; return Task.FromResult(read(s));
        }
    }
    private sealed class ChangingProgrammes : IForgeProgrammeReader
    {
        public int Reads { get; private set; }
        public Task<IReadOnlyList<ForgeProgramme>> ListAsync(string ownerScope, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ForgeProgramme>>([ForgeTodayReviewTests.Programme() with { Paused = ++Reads > 1, Revision = Reads }]);
        public Task<ForgeProgramme?> GetAsync(string ownerScope, Guid id, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected keyed read");
    }
}
