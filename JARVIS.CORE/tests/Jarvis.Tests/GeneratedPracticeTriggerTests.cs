using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs.Stories;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// Lineage purge triggers: editing or deleting a story shown to the realtime model purges generated practice from those
// conversations in the same transaction (review R4 item 3). In-memory store, synthetic data.
public sealed class GeneratedPracticeTriggerTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => At; }
    private static StoryContent Content(string title = "Payments migration") =>
        new(title, "Acme", "Ledger", "context", "problem", "constraints", "ownership", "decisions", "trade-offs", "result", "metrics");

    private sealed record World(InMemoryRuntimeStore Store, Guid Story, Guid Exposed, Guid Other, Guid ExposedQuestion, Guid OtherQuestion);

    private static async Task<World> Build()
    {
        var store = new InMemoryRuntimeStore(); var story = Guid.NewGuid(); var exposed = Guid.NewGuid(); var other = Guid.NewGuid();
        await store.UpdateAsync(s =>
        {
            s.Stories[story] = new(story, Content(), 1, At, At);
            PracticeLineage.Record(s, exposed, [new(PracticeLineage.Story, story, "1")], At);
            PracticeLineage.Record(s, other, [new(PracticeLineage.Story, Guid.NewGuid(), "1")], At);
            return true;
        });
        async Task<Guid> Generate(Guid session)
        {
            var op = Guid.NewGuid();
            var id = await store.UpdateWithGeneratedQuestionsAsync(new(session, [op], []), (_, unit) => unit.Add(GeneratedQuestionStorePortTests.Write(session, op)));
            await store.UpdateAsync(s => { s.Learning[id] = new(id, LearningDomain.SystemDesign, "Generated prompt", "AI reference", 1, 5, At, At, GeneratedQuestionId: id); return true; });
            return id;
        }
        return new(store, story, exposed, other, await Generate(exposed), await Generate(other));
    }

    private static GeneratedQuestionRecord Row(InMemoryRuntimeStore store, Guid id) => store.GeneratedQuestionsSnapshot().Single(r => r.Question.Id == id);

    [Fact]
    public async Task Editing_a_shown_story_purges_that_conversations_generated_practice_and_nothing_else()
    {
        var w = await Build();
        var updated = await new StoryCommandHandlers(w.Store, new FixedClock()).HandleAsync(new UpdateStory(w.Story, new(1, Content("Edited title"))));
        Assert.Equal(2, updated.Version);
        var purged = Row(w.Store, w.ExposedQuestion);
        Assert.Null(purged.Question.Prompt); Assert.Equal("", purged.Fingerprint); Assert.Equal("[]", purged.LineageJson);
        Assert.False(await w.Store.ReadAsync(s => s.Learning.ContainsKey(w.ExposedQuestion)));
        Assert.True((await w.Store.ReadAsync(s => s.PracticeContextManifests[w.Exposed])).Unverifiable);
        // An unrelated conversation is untouched.
        Assert.NotNull(Row(w.Store, w.OtherQuestion).Question.Prompt);
        Assert.True(await w.Store.ReadAsync(s => s.Learning.ContainsKey(w.OtherQuestion)));
        Assert.False((await w.Store.ReadAsync(s => s.PracticeContextManifests[w.Other])).Unverifiable);
    }

    [Fact]
    public async Task Deleting_a_shown_story_purges_that_conversations_generated_practice()
    {
        var w = await Build();
        await new StoryCommandHandlers(w.Store, new FixedClock()).HandleAsync(new DeleteStory(w.Story, 1));
        Assert.False(await w.Store.ReadAsync(s => s.Stories.ContainsKey(w.Story)));
        Assert.Null(Row(w.Store, w.ExposedQuestion).Question.Prompt);
        Assert.NotNull(Row(w.Store, w.OtherQuestion).Question.Prompt);
    }

    [Fact]
    public async Task A_rejected_edit_purges_nothing()
    {
        var w = await Build();
        await Assert.ThrowsAsync<DomainException>(() => new StoryCommandHandlers(w.Store, new FixedClock()).HandleAsync(new UpdateStory(w.Story, new(99, Content("x")))));
        Assert.NotNull(Row(w.Store, w.ExposedQuestion).Question.Prompt);
        Assert.True(await w.Store.ReadAsync(s => s.Learning.ContainsKey(w.ExposedQuestion)));
        Assert.False((await w.Store.ReadAsync(s => s.PracticeContextManifests[w.Exposed])).Unverifiable);
    }

    [Fact]
    public async Task With_no_recorded_lineage_story_edits_behave_exactly_as_before()
    {
        var store = new InMemoryRuntimeStore(); var story = Guid.NewGuid();
        await store.UpdateAsync(s => { s.Stories[story] = new(story, Content(), 1, At, At); return true; });
        var updated = await new StoryCommandHandlers(store, new FixedClock()).HandleAsync(new UpdateStory(story, new(1, Content("Edited"))));
        Assert.Equal(("Edited", 2), (updated.Content.Title, updated.Version));
        Assert.Empty(store.GeneratedQuestionsSnapshot());
        Assert.Empty(await store.ReadAsync(s => s.PracticeContextManifests));
    }

    [Fact]
    public void Affected_finds_only_conversations_whose_manifest_exposed_the_source()
    {
        var s = new RuntimeState(); var story = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        PracticeLineage.Record(s, a, [new(PracticeLineage.Story, story, "1")], At);
        PracticeLineage.Record(s, b, [new(PracticeLineage.Learning, story, "x")], At);   // same ID, different kind
        Assert.Equal(new HashSet<Guid> { a }, GeneratedPracticeTriggers.Affected(s, PracticeLineage.Story, story));
    }
}

public sealed class GeneratedPracticeRetentionTests
{
    [Fact]
    public async Task Retention_retiring_a_conversation_purges_its_generated_practice_and_spares_active_ones()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var services = host.Services;
        var store = services.GetRequiredService<IRuntimeStore>(); var inner = services.GetRequiredService<InMemoryRuntimeStore>();
        var now = DateTimeOffset.UtcNow; var old = Guid.NewGuid(); var active = Guid.NewGuid();
        var sessions = services.GetRequiredService<Jarvis.Orchestrator.Sessions.ISessionStore>();
        await sessions.AddAsync(new(old, ActivityType.General, "Old practice", SessionStatus.Ended, null, null, now.AddDays(-30), now.AddDays(-30), []));
        await sessions.AddAsync(new(active, ActivityType.General, "Current practice", SessionStatus.Active, null, null, now, now, []));
        async Task<Guid> Generate(Guid session)
        {
            var op = Guid.NewGuid();
            var id = await store.UpdateWithGeneratedQuestionsAsync(new(session, [op], []), (_, unit) => unit.Add(GeneratedQuestionStorePortTests.Write(session, op)));
            await store.UpdateAsync(s => { s.Learning[id] = new(id, LearningDomain.SystemDesign, "Generated prompt", "AI reference", 1, 5, now, now, GeneratedQuestionId: id); return true; });
            return id;
        }
        var retiredQuestion = await Generate(old); var keptQuestion = await Generate(active);

        var privacy = services.GetRequiredService<IPrivacyStore>(); var policy = new RetentionPolicy(Enabled: true, SessionDays: 7);
        var preview = await privacy.PreviewAsync(policy, now);
        await privacy.RetainAsync(new(Guid.NewGuid(), now, preview.PolicyFingerprint, PrivacyData.RetentionPhrase), policy, now);

        var rows = inner.GeneratedQuestionsSnapshot();
        Assert.Null(rows.Single(r => r.Question.Id == retiredQuestion).Question.Prompt);
        Assert.NotNull(rows.Single(r => r.Question.Id == keptQuestion).Question.Prompt);
        Assert.False(await store.ReadAsync(s => s.Learning.ContainsKey(retiredQuestion)));
        Assert.True(await store.ReadAsync(s => s.Learning.ContainsKey(keptQuestion)));
    }
}
