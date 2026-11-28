using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

// The generation loop's gates, against the real in-memory store port. Synthetic content; no provider.
public sealed class GeneratedPracticeServiceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = At; public override DateTimeOffset GetUtcNow() => Now; }
    private const string Provenance = """{"source":"ai-realtime","route":"openai/fixture/r1"}""";

    private sealed record World(InMemoryRuntimeStore Store, GeneratedPracticeService Service, Clock Clock, Guid Session, Guid Story);

    private static async Task<World> Build(bool enabled = true, bool exhausted = true, bool unverifiable = false)
    {
        var store = new InMemoryRuntimeStore(); var clock = new Clock(); var session = Guid.NewGuid(); var story = Guid.NewGuid();
        await store.UpdateAsync(s =>
        {
            s.Stories[story] = new(story, new("Payments", "Bank", "Transfers"), 1, At, At);
            PracticeLineage.Record(s, session, [new(PracticeLineage.Story, story, "1")], At, unverifiable);
            if (exhausted)
                foreach (var q in QuestionBank.All.Where(q => q.Domain == LearningDomain.SystemDesign))
                {
                    var a = Guid.NewGuid();
                    s.TrainingAttempts[a] = new(a, Guid.NewGuid(), q.Id, q.Version, TrainingMode.RapidDrill, 1, 1, false, At.AddDays(-1), [new(1, "answer", "voice", At)], []);
                }
            return true;
        });
        return new(store, new GeneratedPracticeService(store, new(enabled), clock), clock, session, story);
    }

    private static GenerationContext Context(World w, Guid? utterance = null, long epoch = 0, string route = "openai/fixture/r1") =>
        new(w.Session, utterance ?? Utterance, epoch, route, Provenance);
    private static readonly Guid Utterance = Guid.NewGuid();
    private static GeneratedQuestionText Text(string outline = "requirements", string prompt = "Design a URL shortener: what do you clarify first?") =>
        new(outline, "Clarifying requirements", prompt, "Scale, latency, durability and abuse.", ["What if writes spike?"]);

    private static async Task<TrainingDrill> Started(World w, TrainingMode mode = TrainingMode.RapidDrill) =>
        (await w.Service.TryStartAsync(Guid.NewGuid(), mode, LearningDomain.SystemDesign, Context(w)))!;

    [Fact]
    public async Task While_disabled_nothing_starts_or_generates()
    {
        var w = await Build(enabled: false);
        Assert.Null(await w.Service.TryStartAsync(Guid.NewGuid(), TrainingMode.RapidDrill, LearningDomain.SystemDesign, Context(w)));
        Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => w.Service.GenerateAsync(Context(w), Guid.NewGuid(), Text()))).StatusCode);
        Assert.Empty(w.Store.GeneratedQuestionsSnapshot());
    }

    [Fact]
    public async Task Authored_questions_come_first_and_unsupported_styles_never_start_generation()
    {
        var w = await Build(exhausted: false);
        Assert.Null(await w.Service.TryStartAsync(Guid.NewGuid(), TrainingMode.RapidDrill, LearningDomain.SystemDesign, Context(w)));
        var x = await Build();
        Assert.Null(await x.Service.TryStartAsync(Guid.NewGuid(), TrainingMode.ArchitectureChallenge, LearningDomain.SystemDesign, Context(x)));
    }

    [Fact]
    public async Task Starting_awaits_the_least_practised_outline_entry_and_replays_by_request()
    {
        var w = await Build(); var request = Guid.NewGuid();
        var drill = (await w.Service.TryStartAsync(request, TrainingMode.RapidDrill, LearningDomain.SystemDesign, Context(w)))!;
        Assert.True(GeneratedDrillFlow.AwaitingQuestion(drill)); Assert.Equal("requirements", drill.Generated!.Pending!.OutlineKey);
        Assert.Equal(drill.Id, (await w.Service.TryStartAsync(request, TrainingMode.RapidDrill, LearningDomain.SystemDesign, Context(w)))!.Id);
    }

    [Fact]
    public async Task A_current_generation_stores_the_question_with_lineage_and_provenance_and_adds_the_step()
    {
        var w = await Build(); await Started(w);
        var result = await w.Service.GenerateAsync(Context(w), Guid.NewGuid(), Text());
        Assert.False(result.Replayed);
        var row = Assert.Single(w.Store.GeneratedQuestionsSnapshot());
        Assert.Equal(result.QuestionId, row.Question.Id); Assert.Equal("requirements", row.Question.OutlineKey);
        Assert.Contains(w.Story.ToString(), row.LineageJson); Assert.Equal(Provenance, row.ProvenanceJson);
        var step = Assert.Single(result.Drill.Steps); Assert.Equal(result.QuestionId, step.GeneratedQuestionId);
    }

    public static TheoryData<string> StaleContexts => new() { "utterance", "epoch", "route", "outline" };

    [Theory]
    [MemberData(nameof(StaleContexts))]
    public async Task A_response_for_a_stale_capture_or_wrong_outline_stores_nothing(string stale)
    {
        var w = await Build(); await Started(w);
        var context = stale switch { "utterance" => Context(w, utterance: Guid.NewGuid()), "epoch" => Context(w, epoch: 3), "route" => Context(w, route: "other"), _ => Context(w) };
        var error = await Assert.ThrowsAsync<DomainException>(() => w.Service.GenerateAsync(context, Guid.NewGuid(), stale == "outline" ? Text(outline: "scaling") : Text()));
        Assert.Equal(409, error.StatusCode); Assert.Empty(w.Store.GeneratedQuestionsSnapshot());
    }

    [Fact]
    public async Task Unverifiable_or_changed_context_refuses_generation()
    {
        var w = await Build(unverifiable: true); await Started(w);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => w.Service.GenerateAsync(Context(w), Guid.NewGuid(), Text()))).StatusCode);
        var x = await Build(); await Started(x);
        await x.Store.UpdateAsync(s => { s.Stories[x.Story] = s.Stories[x.Story] with { Version = 2 }; return true; });
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => x.Service.GenerateAsync(Context(x), Guid.NewGuid(), Text()))).StatusCode);
        Assert.Empty(x.Store.GeneratedQuestionsSnapshot());
    }

    [Fact]
    public async Task Identical_replay_returns_the_frozen_question_and_changed_content_conflicts()
    {
        var w = await Build(); await Started(w); var op = Guid.NewGuid();
        var first = await w.Service.GenerateAsync(Context(w), op, Text());
        var again = await w.Service.GenerateAsync(Context(w), op, Text());
        Assert.True(again.Replayed); Assert.Equal(first.QuestionId, again.QuestionId);
        Assert.Single(w.Store.GeneratedQuestionsSnapshot());
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => w.Service.GenerateAsync(Context(w), op, Text(prompt: "Different")))).StatusCode);
    }

    [Fact]
    public async Task A_second_generator_for_the_same_need_is_refused()
    {
        var w = await Build(); await Started(w);
        await w.Service.GenerateAsync(Context(w), Guid.NewGuid(), Text());
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => w.Service.GenerateAsync(Context(w), Guid.NewGuid(), Text()))).StatusCode);
        Assert.Single(w.Store.GeneratedQuestionsSnapshot());
    }

    [Fact]
    public async Task The_hourly_limit_is_checked_in_the_same_transaction()
    {
        var w = await Build(); await Started(w);
        for (var i = 0; i < GeneratedPracticeService.HourlyLimit; i++)
        {
            var op = Guid.NewGuid();
            await w.Store.UpdateWithGeneratedQuestionsAsync(new(w.Session, [op], []), (_, unit) => unit.Add(GeneratedQuestionStorePortTests.Write(w.Session, op, at: At)));
        }
        Assert.Equal(429, (await Assert.ThrowsAsync<DomainException>(() => w.Service.GenerateAsync(Context(w), Guid.NewGuid(), Text()))).StatusCode);
    }

    [Fact]
    public async Task A_retired_conversation_cannot_start_or_generate()
    {
        var w = await Build(); await Started(w);
        await w.Store.UpdateAsync(s => s.RetiredSessions.Add(w.Session));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => w.Service.GenerateAsync(Context(w), Guid.NewGuid(), Text()))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() =>
            w.Service.TryStartAsync(Guid.NewGuid(), TrainingMode.DeepDive, LearningDomain.SystemDesign, Context(w)))).StatusCode);
    }
}
