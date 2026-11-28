using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// Answering AI-generated practice end to end through the voice tools. Fixture route only; no provider call.
public sealed class GeneratedPracticeAnswerTests
{
    private static readonly ModelRoute Route = new("openai", "realtime-fixture", "Realtime", "policy-1", "rates-1", new DateOnly(2026, 11, 1), 0, 0, 100);

    private sealed record World(IServiceProvider Services, Guid Session, Guid Drill, Guid Question)
    {
        public IRuntimeStore Store => Services.GetRequiredService<IRuntimeStore>();
    }

    private static async Task<Guid> Say(IServiceProvider services, Guid session, string text)
    {
        var id = Guid.NewGuid();
        await services.GetRequiredService<VoiceTranscriptService>().RecordAsync(session, id, ChatRoles.User, text, 30, default,
            restricted: false, timing: new(30, 6, SpeechDelivery.PcmQuietMethod));
        return id;
    }

    private static Task<string> Tool(IServiceProvider services, Guid session, string name, object args, Guid utterance, ModelRoute? route = null)
    {
        using var scope = PrivacyContext.Enter(0);
        return services.GetRequiredService<CoreVoiceTools>().ExecuteAsync(name, JsonSerializer.Serialize(args), session,
            new HashSet<Guid> { utterance }, CancellationToken.None, utterance, generationRoute: route);
    }

    private static async Task<World> Asked(ProviderFactory host)
    {
        var services = host.WithWebHostBuilder(b => b.UseSetting(PracticeGenerationOptions.ConfigKey, "true")).Services;
        var store = services.GetRequiredService<IRuntimeStore>();
        var session = (await services.GetRequiredService<SessionService>().CreateAsync(new(ActivityType.General, "Practice fixture", null))).Id;
        await VoiceToolFixture.OpenAsync(services, session);
        var story = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await store.UpdateAsync(s =>
        {
            s.Stories[story] = new(story, new("Payments", "Bank", "Transfers"), 1, now, now);
            PracticeLineage.Record(s, session, [new(PracticeLineage.Story, story, "1")], now);
            foreach (var q in QuestionBank.All.Where(q => q.Domain == LearningDomain.SystemDesign))
            {
                var a = Guid.NewGuid();
                s.TrainingAttempts[a] = new(a, Guid.NewGuid(), q.Id, q.Version, TrainingMode.RapidDrill, 1, 1, false, now.AddDays(-1), [new(1, "answer", "voice", now)], []);
            }
            return true;
        });
        var trigger = await Say(services, session, "Can we practise system design?");
        var drill = (await services.GetRequiredService<GeneratedPracticeService>().TryStartAsync(Guid.NewGuid(), TrainingMode.RapidDrill, LearningDomain.SystemDesign,
            new(session, trigger, 0, GeneratedPracticeService.RouteRevision(Route), GeneratedPracticeService.Provenance(Route))))!;
        var generated = await Tool(services, session, CoreVoiceTools.GenerateName, new { utterance_id = trigger, outline_key = "requirements",
            title = "Clarifying requirements", prompt = "Design a URL shortener: what do you clarify first?", reference = "Scale, latency, durability.", follow_ups = Array.Empty<string>() }, trigger, Route);
        var question = JsonDocument.Parse(generated).RootElement.GetProperty("result").GetProperty("questionId").GetGuid();
        return new(services, session, drill.Id, question);
    }

    private static bool Ok(string json) => JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean();
    private static Guid AnswerId(string json) => JsonDocument.Parse(json).RootElement.GetProperty("result").GetProperty("answerId").GetGuid();

    [Fact]
    public async Task An_answer_is_saved_canonically_labelled_and_the_next_least_practised_need_is_bound_to_it()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var w = await Asked(host);
        var answer = await Say(w.Services, w.Session, "Read and write ratio, latency targets and retention.");
        var json = await Tool(w.Services, w.Session, "core_practice_answer", new { utterance_id = answer }, answer, Route);
        Assert.True(Ok(json), json); Assert.Contains("AI-written", json);
        var attempt = await w.Store.ReadAsync(s => s.TrainingAttempts.Values.Single(a => a.SessionId == w.Session));
        Assert.Equal(w.Question, attempt.QuestionId); Assert.Equal(w.Drill, attempt.DrillId);
        var drill = await w.Store.ReadAsync(s => s.TrainingDrills[w.Drill]);
        Assert.Equal("Active", drill.Status);
        Assert.Equal("data-ownership", drill.Generated!.Pending!.OutlineKey);
        Assert.Equal(answer, drill.Generated.Pending.TriggerUtteranceId);
        var card = await w.Store.ReadAsync(s => s.Learning[w.Question]);   // revision card carries its trusted origin
        Assert.Equal(w.Question, card.GeneratedQuestionId); Assert.Equal(LearningDomain.SystemDesign, card.Domain);
        var replay = await Tool(w.Services, w.Session, "core_practice_answer", new { utterance_id = answer }, answer, Route);
        Assert.Equal(AnswerId(json), AnswerId(replay));
        Assert.Single(await w.Store.ReadAsync(s => s.TrainingAttempts.Values.Where(a => a.SessionId == w.Session).ToArray()));
    }

    [Fact]
    public async Task Without_the_live_route_a_generated_answer_is_refused_and_nothing_is_saved()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var w = await Asked(host);
        var answer = await Say(w.Services, w.Session, "Read and write ratio first.");
        var json = await Tool(w.Services, w.Session, "core_practice_answer", new { utterance_id = answer }, answer, route: null);
        Assert.False(Ok(json)); Assert.Contains("live voice session", json);
        Assert.Empty(await w.Store.ReadAsync(s => s.TrainingAttempts.Values.Where(a => a.SessionId == w.Session).ToArray()));
    }

    [Fact]
    public async Task Reaching_the_hourly_limit_ends_the_drill_with_a_recorded_reason()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var w = await Asked(host);
        for (var i = 1; i < GeneratedPracticeService.HourlyLimit; i++)
        {
            var op = Guid.NewGuid();
            await w.Store.UpdateWithGeneratedQuestionsAsync(new(w.Session, [op], []), (_, unit) => unit.Add(GeneratedQuestionStorePortTests.Write(w.Session, op, at: DateTimeOffset.UtcNow)));
        }
        var answer = await Say(w.Services, w.Session, "Latency targets.");
        Assert.True(Ok(await Tool(w.Services, w.Session, "core_practice_answer", new { utterance_id = answer }, answer, Route)));
        var drill = await w.Store.ReadAsync(s => s.TrainingDrills[w.Drill]);
        Assert.Equal("Completed", drill.Status); Assert.Equal(GeneratedDrillFlow.EndQuota, drill.Generated!.EndReason);
    }

    [Fact]
    public async Task A_removed_question_cannot_receive_an_answer()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var w = await Asked(host);
        await w.Store.UpdateWithGeneratedQuestionsAsync(new(w.Session, [], [w.Question]), (_, unit) => { unit.Purge(new HashSet<Guid> { w.Question }, DateTimeOffset.UtcNow); return true; });
        var answer = await Say(w.Services, w.Session, "Latency targets.");
        var json = await Tool(w.Services, w.Session, "core_practice_answer", new { utterance_id = answer }, answer, Route);
        Assert.False(Ok(json)); Assert.Contains("removed", json);
        Assert.Empty(await w.Store.ReadAsync(s => s.TrainingAttempts.Values.Where(a => a.SessionId == w.Session).ToArray()));
    }

    private static async Task<Guid> Answered(World w)
    {
        var answer = await Say(w.Services, w.Session, "Read and write ratio, latency targets and retention.");
        Assert.True(Ok(await Tool(w.Services, w.Session, "core_practice_answer", new { utterance_id = answer }, answer, Route)));
        await w.Store.UpdateAsync(s => { s.Learning[w.Question] = s.Learning[w.Question] with { DueAt = DateTimeOffset.UtcNow.AddDays(-1) }; return true; });
        return answer;
    }

    [Fact]
    public async Task A_due_generated_card_is_recalled_with_its_unverified_origin_through_the_voice_tool()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var w = await Asked(host); await Answered(w);
        var start = await Say(w.Services, w.Session, "Let's do my review cards.");
        var question = JsonDocument.Parse(await Tool(w.Services, w.Session, "core_recall", new { action = "start", utterance_id = start, rating = "None" }, start)).RootElement;
        Assert.True(question.GetProperty("ok").GetBoolean(), question.ToString());
        var result = question.GetProperty("result");
        Assert.Equal(w.Question, result.GetProperty("cardId").GetGuid());
        Assert.Equal(VoiceRecallView.GeneratedOrigin, result.GetProperty("origin").GetString());
        var said = await Say(w.Services, w.Session, "Requirements first.");
        var reveal = JsonDocument.Parse(await Tool(w.Services, w.Session, "core_recall", new { action = "reveal", utterance_id = said, rating = "None" }, said)).RootElement;
        Assert.Equal("Scale, latency, durability.", reveal.GetProperty("result").GetProperty("reference").GetString());
        Assert.Equal(VoiceRecallView.GeneratedOrigin, reveal.GetProperty("result").GetProperty("origin").GetString());
    }

    [Fact]
    public async Task A_due_generated_card_is_never_selected_into_ordinary_model_context()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var w = await Asked(host); await Answered(w);
        var selected = await w.Store.ReadAsync(s => Jarvis.Orchestrator.Intelligence.ContextSelector.Select(s.Stories.Values, s.Learning.Values,
            "system design requirements", DateTimeOffset.UtcNow, maxBytes: 5000));
        Assert.DoesNotContain(selected.Due, c => c.Id == w.Question);
    }

    [Fact]
    public async Task AI_feedback_is_refused_for_a_generated_answer_before_any_provider_call()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var w = await Asked(host); await Answered(w);
        var attempt = await w.Store.ReadAsync(s => s.TrainingAttempts.Values.Single(a => a.SessionId == w.Session));
        var coach = w.Services.GetRequiredService<Jarvis.Api.Integrations.AiCoach>();
        var error = await Assert.ThrowsAsync<DomainException>(() => coach.TechnicalAsync(attempt.Id,
            new(Guid.NewGuid(), attempt.Revision, attempt.TextRevision, AllowCloudProcessing: true), CancellationToken.None));
        Assert.Equal(409, error.StatusCode); Assert.Contains("AI-generated", error.Message);
    }
}
