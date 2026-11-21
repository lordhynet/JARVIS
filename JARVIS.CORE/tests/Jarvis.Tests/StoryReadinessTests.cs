using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;

namespace Jarvis.Tests;

public sealed class StoryReadinessTests
{
    private static Story Story(StoryContent content) => new(Guid.NewGuid(), content, 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static string Status(StoryReadiness r, string key) => r.Checks.Single(x => x.Key == key).Status;

    [Fact]
    public void A_bare_draft_is_a_draft_with_targeted_follow_ups()
    {
        var r = StoryReadinessChecker.Check(Story(new StoryContent("Retry fix", "Acme", "Checkout")), 0);
        Assert.Equal("draft", r.Level); Assert.Equal(6, r.Score); // only the half-credit evidence check
        Assert.Equal(9, r.Checks.Count);
        Assert.All(r.Checks.Where(x => x.Key != "evidence"), x => Assert.Equal("missing", x.Status));
        Assert.Equal("weak", Status(r, "evidence"));
        Assert.Equal(StoryReadinessChecker.MaxFollowUps, r.FollowUps.Count);
        Assert.Equal("What was the situation, and what problem had to be solved?", r.FollowUps[0]);
    }

    [Fact]
    public void A_complete_story_with_a_source_quote_is_interview_ready()
    {
        var r = StoryReadinessChecker.Check(Story(new StoryContent("Idempotent payments", "Acme", "Checkout",
            Context: "Customers were charged twice during provider timeouts.", Problem: "Retries were not idempotent.",
            PersonalOwnership: "I designed and shipped the idempotency key and led the rollout.",
            TechnicalDecisions: "Store an idempotency key per payment intent in PostgreSQL.",
            TradeOffs: "I chose a database unique key instead of a distributed lock because it was simpler to operate.",
            Result: "Duplicate charges stopped.", Metrics: "Duplicates fell from 40 to 0 per month.",
            FailureModes: "A provider could still time out after charging; we reconcile nightly.",
            ChangeToday: "I would add the reconciliation alert earlier.")), sourceQuotes: 1);
        Assert.Equal(100, r.Score); Assert.Equal("interview-ready", r.Level);
        Assert.Empty(r.FollowUps); Assert.All(r.Checks, x => Assert.Equal("ready", x.Status));
    }

    [Fact]
    public void Team_only_ownership_and_comparison_free_trade_offs_are_weak()
    {
        var r = StoryReadinessChecker.Check(Story(new StoryContent("Migration", "Acme", "Ledger",
            PersonalOwnership: "We migrated the ledger as a team over two sprints.", TradeOffs: "It was a difficult migration with many steps.")), 0);
        Assert.Equal("weak", Status(r, "ownership")); Assert.Equal("weak", Status(r, "trade-offs"));
        Assert.Contains("What exactly did you personally own or decide, as opposed to the team?", r.FollowUps);
    }

    [Fact]
    public void An_honest_not_measured_counts_and_advice_never_asks_for_invented_numbers()
    {
        var honest = StoryReadinessChecker.Check(Story(new StoryContent("Caching", "Acme", "Search", Metrics: "Not measured at the time.")), 0);
        Assert.Equal("ready", Status(honest, "measurement"));
        var vague = StoryReadinessChecker.Check(Story(new StoryContent("Caching", "Acme", "Search", Metrics: "Much faster")), 0);
        Assert.Equal("weak", Status(vague, "measurement"));
        Assert.Contains("Never invent a number", vague.Checks.Single(x => x.Key == "measurement").Advice);
    }

    [Fact]
    public async Task Readiness_endpoints_are_owner_only_and_list_least_ready_first()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient();
        var bare = await LifeScenarioTests.Post<Story>(client, "/api/stories", new CreateStoryRequest(Guid.NewGuid(), new StoryContent("Bare", "Acme", "Svc")));
        var richer = await LifeScenarioTests.Post<Story>(client, "/api/stories", new CreateStoryRequest(Guid.NewGuid(), new StoryContent("Richer", "Acme", "Svc",
            Context: "The nightly batch kept missing its window.", PersonalOwnership: "I rewrote the scheduler.", Result: "It finishes on time.")));
        var one = (await client.GetFromJsonAsync<StoryReadiness>($"/api/stories/{richer.Id}/readiness", LifeScenarioTests.Json))!;
        Assert.Equal(richer.Id, one.StoryId); Assert.Equal(richer.Version, one.Version);
        var all = (await client.GetFromJsonAsync<List<StoryReadiness>>("/api/stories/readiness", LifeScenarioTests.Json))!;
        Assert.Equal(new[] { bare.Id, richer.Id }, all.Select(x => x.StoryId).ToArray());
        using (var missing = await client.GetAsync($"/api/stories/{Guid.NewGuid()}/readiness")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var anonymous = factory.CreateClient();
        using var refused = await anonymous.GetAsync("/api/stories/readiness");
        Assert.False(refused.IsSuccessStatusCode);
    }
}
