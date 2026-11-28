using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// Generated-practice lineage: what the realtime model was shown, with re-checkable versions (review R4 item 1). Off by default.
public sealed class PracticeLineageTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
    private static Story Story(int version = 1) => new(Guid.NewGuid(), new StoryContent("Migration rescue", "s", "t", "a", "r", "decisions", "trade-offs", "result", "metrics"), version, At, At);
    private static LearningItem Card(string prompt = "Explain isolation levels") => new(Guid.NewGuid(), LearningDomain.Databases, prompt, "answer", 1, 5, At, At);

    [Fact]
    public void A_manifest_is_current_only_while_every_source_is_at_the_version_the_model_saw()
    {
        var s = new RuntimeState(); var story = Story(); var card = Card(); var session = Guid.NewGuid();
        s.Stories[story.Id] = story; s.Learning[card.Id] = card;
        var manifest = PracticeLineage.Record(s, session, [new(PracticeLineage.Story, story.Id, "1"), new(PracticeLineage.Learning, card.Id, PracticeLineage.CardVersion(card))], At);
        Assert.True(PracticeLineage.Current(s, manifest));
        s.Stories[story.Id] = story with { Version = 2 };
        Assert.False(PracticeLineage.Current(s, manifest));
        s.Stories[story.Id] = story; s.Learning[card.Id] = card with { Answer = "edited" };
        Assert.False(PracticeLineage.Current(s, manifest));
        s.Learning.Remove(card.Id);
        Assert.False(PracticeLineage.Current(s, manifest));
    }

    [Fact]
    public void Deleted_stories_and_unverifiable_manifests_are_never_current()
    {
        var s = new RuntimeState(); var story = Story(); s.Stories[story.Id] = story; var session = Guid.NewGuid();
        var manifest = PracticeLineage.Record(s, session, [new(PracticeLineage.Story, story.Id, "1")], At);
        s.Stories.Remove(story.Id);
        Assert.False(PracticeLineage.Current(s, manifest));
        Assert.False(PracticeLineage.Current(s, PracticeLineage.Record(new RuntimeState(), session, [], At, unverifiable: true)));
    }

    [Fact]
    public void Recording_accumulates_never_forgets_and_unverifiable_is_sticky()
    {
        var s = new RuntimeState(); var session = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        PracticeLineage.Record(s, session, [new(PracticeLineage.Story, a, "1")], At);
        PracticeLineage.Record(s, session, [], At, unverifiable: true);
        var manifest = PracticeLineage.Record(s, session, [new(PracticeLineage.Story, a, "2"), new(PracticeLineage.Story, b, "1")], At);
        Assert.Equal(3, manifest.Sources.Count); Assert.True(manifest.Unverifiable);
        Assert.True(PracticeLineage.Exposes(manifest, PracticeLineage.Story, a));
    }

    [Fact]
    public void Unknown_kinds_are_ignored_and_overflow_becomes_unverifiable_rather_than_truncated()
    {
        var s = new RuntimeState(); var session = Guid.NewGuid();
        Assert.Empty(PracticeLineage.Record(s, session, [new("context-section", Guid.NewGuid(), "x"), new(PracticeLineage.Story, Guid.Empty, "1")], At).Sources);
        var many = Enumerable.Range(0, PracticeLineage.MaximumSources + 1).Select(_ => new PracticeContextSource(PracticeLineage.Story, Guid.NewGuid(), "1"));
        var manifest = PracticeLineage.Record(s, session, many, At);
        Assert.True(manifest.Unverifiable); Assert.Equal(PracticeLineage.MaximumSources, manifest.Sources.Count);
    }

    [Fact]
    public void Lineage_JSON_holds_only_IDs_and_versions()
    {
        var s = new RuntimeState(); var card = Card("SECRET prompt text"); s.Learning[card.Id] = card;
        var manifest = PracticeLineage.Record(s, Guid.NewGuid(), [new(PracticeLineage.Learning, card.Id, PracticeLineage.CardVersion(card))], At);
        Assert.DoesNotContain("SECRET", PracticeLineage.LineageJson(manifest));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(manifest));
    }

    [Fact]
    public void Context_selection_never_places_generated_cards_back_into_model_context()
    {
        var plain = Card("isolation"); var generated = Card("isolation") with { GeneratedQuestionId = Guid.NewGuid() };
        var selected = ContextSelector.Select([], [plain, generated], "isolation", At.AddDays(1));
        Assert.Contains(selected.Due, c => c.Id == plain.Id); Assert.DoesNotContain(selected.Due, c => c.Id == generated.Id);
    }

    [Fact]
    public void Retention_and_erasure_remove_a_conversations_manifest()
    {
        var s = new RuntimeState(); var session = Guid.NewGuid();
        PracticeLineage.Record(s, session, [new(PracticeLineage.Story, Guid.NewGuid(), "1")], At);
        Assert.True(s.PracticeContextManifests.ContainsKey(session));
        // Erasure builds a fresh state, so it drops every manifest.
        Assert.Empty(PrivacyData.EmptyAfterErase(s).PracticeContextManifests);
        // Retention retiring the conversation removes its manifest (the same transaction that retires the session).
        PrivacyData.RetainRuntime(s, new RetentionPolicy(Enabled: true), At, new([session], [], [], [], [], [], [], [], [], []));
        Assert.False(s.PracticeContextManifests.ContainsKey(session)); Assert.Contains(session, s.RetiredSessions);
    }
}

public sealed class PracticeLineageToolGatingTests
{
    private static async Task<(ProviderFactory Root, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host, Guid Session)> Create(bool enabled)
    {
        var root = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var host = root.WithWebHostBuilder(b => { if (enabled) b.UseSetting(PracticeGenerationOptions.ConfigKey, "true"); });
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Voice fixture", SessionStatus.Active, null, null, now, now, []));
        await VoiceToolFixture.OpenAsync(host.Services, id);
        return (root, host, id);
    }
    private static Task<string> Run(IServiceProvider services, Guid session, string name, object args) =>
        services.GetRequiredService<CoreVoiceTools>().ExecuteAsync(name, JsonSerializer.Serialize(args), session, new HashSet<Guid>(), default);
    private static Task<PracticeContextManifest?> Manifest(IServiceProvider services, Guid session) =>
        services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.PracticeContextManifests.GetValueOrDefault(session));

    [Fact]
    public async Task While_disabled_no_tool_records_any_lineage()
    {
        var (root, host, session) = await Create(enabled: false);
        using (root) using (host)
        {
            await Run(host.Services, session, "core_context", new { section = "goals" });
            await Run(host.Services, session, "core_story_search", new { query = "migration" });
            Assert.Null(await Manifest(host.Services, session));
            Assert.False(host.Services.GetRequiredService<PracticeGenerationOptions>().Enabled);
        }
    }

    [Fact]
    public async Task When_enabled_a_personal_context_tool_marks_the_conversation_unverifiable()
    {
        var (root, host, session) = await Create(enabled: true);
        using (root) using (host)
        {
            await Run(host.Services, session, "core_context", new { section = "goals" });
            Assert.True((await Manifest(host.Services, session))!.Unverifiable);
        }
    }

    [Fact]
    public async Task When_enabled_story_search_records_the_exact_story_versions_it_returned()
    {
        var (root, host, session) = await Create(enabled: true);
        using (root) using (host)
        {
            var story = new Story(Guid.NewGuid(), new StoryContent("Payments migration", "s", "t", "a", "r", "decisions", "trade-offs", "result", "metrics"), 4,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await host.Services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { s.Stories[story.Id] = story; return true; });
            await Run(host.Services, session, "core_story_search", new { query = "payments migration" });
            var manifest = await Manifest(host.Services, session);
            Assert.False(manifest!.Unverifiable);
            Assert.Contains(new PracticeContextSource(PracticeLineage.Story, story.Id, "4"), manifest.Sources);
        }
    }
}
