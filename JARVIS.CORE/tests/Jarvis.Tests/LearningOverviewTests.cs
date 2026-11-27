using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Cqrs.Learning;

namespace Jarvis.Tests;

public sealed class LearningOverviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static LearningItem Card(LearningDomain domain, DateTimeOffset due) =>
        new(Guid.NewGuid(), domain, "Prompt", "Answer", 1, 5, due, Now.AddDays(-30));
    private static ReviewEvent Review(LearningItem card, ReviewRating rating, DateTimeOffset at) =>
        new(Guid.NewGuid(), card.Id, rating, TimeSpan.FromSeconds(5), at);

    [Fact]
    public void Forecast_buckets_due_cards_by_24_hour_windows_with_overdue_in_today()
    {
        var overdue = Card(LearningDomain.French, Now.AddDays(-3));
        var soon = Card(LearningDomain.French, Now.AddHours(23));
        var tomorrow = Card(LearningDomain.Databases, Now.AddHours(25));
        var sixth = Card(LearningDomain.Databases, Now.AddDays(6.5));
        var later = Card(LearningDomain.Databases, Now.AddDays(7));
        var overview = LearningOverviewBuilder.Build([overdue, soon, tomorrow, sixth, later], [], Now);

        Assert.Equal(new[] { 2, 1, 0, 0, 0, 0, 1 }, overview.Forecast.ToArray());
        Assert.Equal(5, overview.Cards); Assert.Equal(1, overview.DueNow);
        Assert.Null(overview.RecallRate7Days); Assert.Null(overview.Focus); // no reviews: no invented weakness
        var databases = overview.Domains.Single(d => d.Domain == LearningDomain.Databases);
        Assert.Equal(3, databases.Cards); Assert.Equal(0, databases.DueNow); Assert.Equal(2, databases.DueNext7Days);
        Assert.Equal(LearningDomain.French, overview.Domains[0].Domain); // most due now first
    }

    [Fact]
    public void Recent_reviews_give_rating_counts_recall_rate_and_a_focus_domain()
    {
        var french = Card(LearningDomain.French, Now.AddDays(2));
        var sql = Card(LearningDomain.Databases, Now.AddDays(2));
        var reviews = new[]
        {
            Review(french, ReviewRating.Again, Now.AddDays(-1)), Review(french, ReviewRating.Again, Now.AddDays(-2)),
            Review(french, ReviewRating.Good, Now.AddHours(-1)), Review(sql, ReviewRating.Again, Now.AddDays(-3)),
            Review(sql, ReviewRating.Easy, Now.AddDays(-1)),
            Review(sql, ReviewRating.Again, Now.AddDays(-8)),                              // outside the window
            new ReviewEvent(Guid.NewGuid(), Guid.NewGuid(), ReviewRating.Again, TimeSpan.Zero, Now.AddHours(-2)) // card gone
        };
        var overview = LearningOverviewBuilder.Build([french, sql], reviews, Now);

        Assert.Equal(5, overview.Reviewed7Days); Assert.Equal(3, overview.Again7Days);
        Assert.Equal(0, overview.Hard7Days); Assert.Equal(1, overview.Good7Days); Assert.Equal(1, overview.Easy7Days);
        Assert.Equal(0.4, overview.RecallRate7Days);
        Assert.Equal(LearningDomain.French, overview.Focus);
        var fr = overview.Domains.Single(d => d.Domain == LearningDomain.French);
        Assert.Equal(3, fr.Reviewed7Days); Assert.Equal(2, fr.Again7Days); Assert.Equal(0.333, fr.RecallRate7Days);
    }

    [Fact]
    public async Task Overview_endpoint_reflects_created_and_reviewed_cards()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateAuthorisedClient();
        var empty = (await client.GetFromJsonAsync<LearningOverview>("/api/learning/overview", LifeScenarioTests.Json))!;
        Assert.Equal(0, empty.Cards); Assert.Empty(empty.Domains); Assert.Equal(LearningOverviewBuilder.ForecastDays, empty.Forecast.Count);

        var card = await LifeScenarioTests.Post<LearningItem>(client, "/api/learning", new CreateLearningRequest(LearningDomain.French, "Bonjour?", "Hello"));
        await LifeScenarioTests.Post<LearningItem>(client, $"/api/learning/{card.Id}/reviews", new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Again, 4));
        var overview = (await client.GetFromJsonAsync<LearningOverview>("/api/learning/overview", LifeScenarioTests.Json))!;
        Assert.Equal(1, overview.Cards); Assert.Equal(1, overview.Reviewed7Days); Assert.Equal(1, overview.Again7Days);
        Assert.Equal(LearningDomain.French, overview.Focus);
        Assert.Equal(LearningDomain.French, Assert.Single(overview.Domains).Domain);
    }

    [Fact]
    public void Metadata_projection_preserves_tick_boundaries_offsets_and_hard_recall()
    {
        var now = Now.AddTicks(7); var since = now.AddDays(-7);
        var card = Card(LearningDomain.Databases, now.AddHours(24).AddTicks(-1).ToOffset(TimeSpan.FromHours(5.5)));
        var events = new[] { Review(card, ReviewRating.Again, since), Review(card, ReviewRating.Again, since.AddTicks(1)),
            Review(card, ReviewRating.Hard, now), Review(card, ReviewRating.Easy, now.AddTicks(1)) };
        var full = LearningOverviewBuilder.Build([card], events, now);
        var metadata = LearningOverviewBuilder.BuildMetadata([new(card.Id, card.Domain, card.DueAt)], events, now);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(full), System.Text.Json.JsonSerializer.Serialize(metadata));
        Assert.Equal(2, metadata.Reviewed7Days); Assert.Equal(1, metadata.Again7Days); Assert.Equal(1, metadata.Hard7Days);
        Assert.Equal(0.5, metadata.RecallRate7Days); Assert.Equal(1, metadata.Forecast[0]);
    }

    [Fact]
    public void Unknown_selected_metadata_fails_closed()
    {
        var card = Card(LearningDomain.French, Now);
        Assert.Throws<InvalidDataException>(() => LearningOverviewBuilder.BuildMetadata([new(card.Id, (LearningDomain)999, Now)], [], Now));
        Assert.Throws<InvalidDataException>(() => LearningOverviewBuilder.Build([card], [Review(card, (ReviewRating)999, Now)], Now));
    }

    private sealed class OverviewReader : IRuntimeReader
    {
        public DateTimeOffset? Seen { get; private set; }
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => throw new InvalidOperationException("Broad read used.");
        public Task<LearningOverview> ReadLearningOverviewAsync(DateTimeOffset now, CancellationToken ct = default) {
            Seen = now; return Task.FromResult(LearningOverviewBuilder.Build([], [], now));
        }
    }
    [Fact]
    public async Task Query_uses_the_explicit_read_port()
    {
        var reader = new OverviewReader(); var before = DateTimeOffset.UtcNow;
        var result = await new LearningQueryHandlers(reader, TimeProvider.System).HandleAsync(new GetLearningOverview());
        Assert.Equal(reader.Seen, result.AsOf); Assert.True(reader.Seen >= before && reader.Seen <= DateTimeOffset.UtcNow);
    }
}
