using System.Collections;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Cqrs.Imports;
using Jarvis.Orchestrator.Cqrs.Stories;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class PrepReadinessSelectionTests
{
    internal static readonly DateTimeOffset At = PrepPackCardSafetyTests.At;
    internal static Story Thin(string title, string decision = "Kafka consumer") => new(Guid.NewGuid(), new(title, "Fixture", "Fixture", TechnicalDecisions: decision), 1, At, At);
    internal static Story Strong(string title, string decision = "Kafka consumer") => new(Guid.NewGuid(), new(title, "Fixture", "Fixture",
        Context: "Retries could duplicate the recorded payment.", PersonalOwnership: "I designed and implemented the safe retry boundary.",
        TechnicalDecisions: decision, TradeOffs: "I chose a unique key instead of a distributed lock because it was simpler.",
        Result: "Duplicate charges stopped.", Metrics: "Not measured at the time.", FailureModes: "A provider could time out after charging.",
        ChangeToday: "I would add reconciliation alerts earlier."), 1, At, At);
    private static ImportedDocument Advert(string text) => new(Guid.NewGuid(), "job-advert", "Fixture role", text, "fixture-hash", 1, At, []);

    [Fact]
    public void Alphabetical_drafts_cannot_hide_an_available_ready_match()
    {
        var strong = Strong("Z ready example"); var stories = new[] { Thin("A draft"), Thin("B draft"), Thin("C draft"), strong };
        var pack = InterviewPrepPackBuilder.Build(Advert("Kafka"), stories, new Dictionary<Guid, int> { [strong.Id] = 1 });
        var requirement = Assert.Single(pack.Requirements);
        Assert.False(requirement.NeedsWork); Assert.Contains(requirement.Stories, s => s.StoryId == strong.Id);
        Assert.Equal(strong.Id, requirement.Stories[0].StoryId); Assert.Equal(3, requirement.Stories.Count);
    }

    [Fact]
    public void Readiness_checks_only_matching_stories_once_across_competencies()
    {
        var matched = Strong("Matched example", "Kafka consumer with a SQL database"); var unrelated = Thin("Unrelated journal", "Kept a garden journal.");
        var counts = new CountingQuotes(); var pack = InterviewPrepPackBuilder.Build(Advert("Kafka and SQL"), [matched, unrelated], counts);
        Assert.Equal(2, pack.Requirements.Count); Assert.Equal(new[] { matched.Id }, counts.Reads);
        Assert.All(pack.Requirements, r => Assert.Equal(matched.Id, Assert.Single(r.Stories).StoryId));
    }

    [Fact]
    public void Equal_score_matches_have_stable_title_and_uuid_order_under_permutation()
    {
        var a = Thin("Same title") with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var b = a with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") }; var advert = Advert("Kafka");
        var first = InterviewPrepPackBuilder.Build(advert, [b, a]); var second = InterviewPrepPackBuilder.Build(advert, [a, b]);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(new[] { a.Id, b.Id }, Assert.Single(first.Requirements).Stories.Select(s => s.StoryId));
    }

    [Fact]
    public async Task New_readiness_reorders_questions_refuses_stale_selection_and_reuses_original_receipt()
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); using var epoch = PrivacyContext.Enter(0); var clock = new ForgeClock { Now = At };
        var csharp = Strong("CSharp ready", ".NET thread-pool changes"); var sql = Thin("Database draft", "SQL indexing");
        await store.UpdateAsync(s => { s.Stories[csharp.Id] = csharp; s.Stories[sql.Id] = sql; return true; });
        var advert = Advert("C# and .NET with SQL"); var displayed = await PrepPackCardSafetyTests.Seed(store, advert);
        Assert.True(displayed.Requirements.Single(r => r.Key == "databases").NeedsWork);
        var cards = new PrepPackCardCommands(store, clock); var first = await cards.HandleAsync(new(advert.Id, PrepPackCardSafetyTests.Pick(displayed, 0)));
        await new StoryCommandHandlers(store, clock).HandleAsync(new UpdateStory(sql.Id, new(1, Strong("Database ready", "SQL indexing").Content)));
        var current = await new ImportQueryHandlers(store).HandleAsync(new GetImportPrepPack(advert.Id));
        Assert.NotEqual(displayed.Fingerprint, current.Fingerprint);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => cards.HandleAsync(new(advert.Id, PrepPackCardSafetyTests.Pick(displayed, 0))))).StatusCode);
        var repeatedIndex = current.LikelyQuestions.ToList().IndexOf(displayed.LikelyQuestions[0]); Assert.True(repeatedIndex > 0);
        var replay = await cards.HandleAsync(new(advert.Id, PrepPackCardSafetyTests.Pick(current, repeatedIndex)));
        Assert.Equal((0, 1), (replay.Created, replay.Existing)); Assert.Equal(first.Cards[0].Id, Assert.Single(replay.Cards).Id);
    }

    [Fact]
    public async Task Prep_and_story_queries_share_current_quote_counts_and_remove_purged_source_influence()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateAuthorisedClient();
        var story = await LifeScenarioTests.Post<Story>(client, "/api/stories", new CreateStoryRequest(Guid.NewGuid(), Strong("Owned example", ".NET retry boundary").Content));
        var cv = await LifeScenarioTests.Post<ImportedDocument>(client, "/api/imports", new ImportDocumentRequest(Guid.NewGuid(), "cv", "Fixture CV", ".NET retry ownership", true));
        await LifeScenarioTests.Post<SourceClaim>(client, $"/api/imports/{cv.Id}/claims", new AttachSourceClaimRequest(Guid.NewGuid(), story.Id, story.Version, cv.Revision, 0, cv.Text.Length));
        var advert = await LifeScenarioTests.Post<ImportedDocument>(client, "/api/imports", new ImportDocumentRequest(Guid.NewGuid(), "job-advert", "Fixture role", ".NET", true));
        async Task AssertAgreement(int quotes) {
            var pack = (await client.GetFromJsonAsync<InterviewPrepPack>($"/api/imports/{advert.Id}/prep-pack", LifeScenarioTests.Json))!;
            var one = (await client.GetFromJsonAsync<StoryReadiness>($"/api/stories/{story.Id}/readiness", LifeScenarioTests.Json))!;
            var all = (await client.GetFromJsonAsync<List<StoryReadiness>>("/api/stories/readiness", LifeScenarioTests.Json))!;
            var match = Assert.Single(Assert.Single(pack.Requirements).Stories);
            Assert.Equal(quotes, one.SourceQuotes); Assert.Equal((one.Score, one.Level), (match.Readiness, match.Level));
            Assert.Equal(one.Score, Assert.Single(all).Score); Assert.Contains("text completeness", pack.Note);
        }
        await AssertAgreement(1);
        using var removed = await client.DeleteAsync($"/api/imports/{cv.Id}?revision={cv.Revision}"); removed.EnsureSuccessStatusCode();
        await AssertAgreement(0);
    }

    private sealed class CountingQuotes : IReadOnlyDictionary<Guid, int>
    {
        public List<Guid> Reads { get; } = [];
        public int this[Guid key] => 0; public IEnumerable<Guid> Keys => []; public IEnumerable<int> Values => []; public int Count => 0;
        public bool ContainsKey(Guid key) => false;
        public bool TryGetValue(Guid key, out int value) { Reads.Add(key); value = 0; return false; }
        public IEnumerator<KeyValuePair<Guid, int>> GetEnumerator() => Enumerable.Empty<KeyValuePair<Guid, int>>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
