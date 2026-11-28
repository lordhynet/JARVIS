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

// core_practice_start choosing generated practice only after authored questions are exhausted. Fixture route; no provider.
public sealed class GeneratedPracticeStartTests
{
    private static readonly ModelRoute Route = new("openai", "realtime-fixture", "Realtime", "policy-1", "rates-1", new DateOnly(2026, 11, 1), 0, 0, 100);

    private static async Task<(IServiceProvider Services, Guid Session)> Ready(ProviderFactory host, bool exhausted)
    {
        var services = host.WithWebHostBuilder(b => b.UseSetting(PracticeGenerationOptions.ConfigKey, "true")).Services;
        var session = (await services.GetRequiredService<SessionService>().CreateAsync(new(ActivityType.General, "Practice fixture", null))).Id;
        await VoiceToolFixture.OpenAsync(services, session);
        var story = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s =>
        {
            s.Stories[story] = new(story, new("Payments", "Bank", "Transfers"), 1, now, now);
            PracticeLineage.Record(s, session, [new(PracticeLineage.Story, story, "1")], now);
            if (exhausted)
                foreach (var q in QuestionBank.All.Where(q => q.Domain == LearningDomain.SystemDesign))
                {
                    var a = Guid.NewGuid();
                    s.TrainingAttempts[a] = new(a, Guid.NewGuid(), q.Id, q.Version, TrainingMode.RapidDrill, 1, 1, false, now.AddDays(-1), [new(1, "answer", "voice", now)], []);
                }
            return true;
        });
        return (services, session);
    }

    private static async Task<Guid> Say(IServiceProvider services, Guid session, string text)
    {
        var id = Guid.NewGuid();
        await services.GetRequiredService<VoiceTranscriptService>().RecordAsync(session, id, ChatRoles.User, text, 30, default,
            restricted: false, timing: new(30, 6, SpeechDelivery.PcmQuietMethod));
        return id;
    }

    private static Task<string> Tool(IServiceProvider services, Guid session, string name, object args, Guid utterance, ModelRoute? route)
    {
        using var scope = PrivacyContext.Enter(0);
        return services.GetRequiredService<CoreVoiceTools>().ExecuteAsync(name, JsonSerializer.Serialize(args), session,
            new HashSet<Guid> { utterance }, CancellationToken.None, utterance, generationRoute: route);
    }

    private static async Task<(Guid Utterance, JsonElement Result)> Start(IServiceProvider services, Guid session, ModelRoute? route,
        string mode = "RapidDrill", string topic = "SystemDesign")
    {
        var utterance = await Say(services, session, "Can we practise " + topic + "?");
        var json = await Tool(services, session, "core_practice_start", new { utterance_id = utterance, mode, topic }, utterance, route);
        var root = JsonDocument.Parse(json).RootElement;
        return (utterance, root.GetProperty("ok").GetBoolean() ? root.GetProperty("result").Clone() : root.Clone());
    }

    private static Task<TrainingDrill[]> Drills(IServiceProvider services, Guid session) =>
        services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.TrainingDrills.Values.Where(d => d.SessionId == session).ToArray());

    [Fact]
    public async Task Once_authored_questions_are_exhausted_a_named_topic_starts_generated_practice_awaiting_a_question()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session) = await Ready(host, exhausted: true);
        var (utterance, result) = await Start(services, session, Route);
        Assert.Equal("requirements", result.GetProperty("awaitingOutline").GetString());
        var drill = Assert.Single(await Drills(services, session));
        Assert.NotNull(drill.Generated); Assert.Equal(VoicePracticeService.Operation("start", session, utterance), drill.Id);
        Assert.Equal(utterance, drill.Generated.Pending!.TriggerUtteranceId);
    }

    [Theory]
    [InlineData(false, true)]   // authored questions remain
    [InlineData(true, false)]   // no live route
    public async Task Otherwise_the_authored_start_runs_unchanged(bool exhausted, bool withRoute)
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session) = await Ready(host, exhausted);
        var (_, result) = await Start(services, session, withRoute ? Route : null);
        Assert.False(string.IsNullOrEmpty(result.GetProperty("prompt").GetString()));
        Assert.Null(Assert.Single(await Drills(services, session)).Generated);
    }

    [Fact]
    public async Task Resuming_while_awaiting_rebinds_the_need_so_an_older_generator_response_is_refused()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session) = await Ready(host, exhausted: true);
        var (first, _) = await Start(services, session, Route);
        var (second, _) = await Start(services, session, Route);
        var drill = Assert.Single(await Drills(services, session));
        Assert.Equal(second, drill.Generated!.Pending!.TriggerUtteranceId);
        var stale = await Tool(services, session, CoreVoiceTools.GenerateName, new { utterance_id = first, outline_key = "requirements", title = "t",
            prompt = "Old prompt", reference = "Old reference", follow_ups = Array.Empty<string>() }, first, Route);
        Assert.Contains("out of date", stale);
        var current = await Tool(services, session, CoreVoiceTools.GenerateName, new { utterance_id = second, outline_key = "requirements", title = "t",
            prompt = "Current prompt", reference = "Current reference", follow_ups = Array.Empty<string>() }, second, Route);
        Assert.Contains("\"ok\":true", current);
    }

    [Fact]
    public async Task A_different_practice_while_generated_practice_is_active_is_refused()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session) = await Ready(host, exhausted: true);
        await Start(services, session, Route);
        var (_, other) = await Start(services, session, Route, mode: "DeepDive");
        Assert.Contains("different practice", other.GetProperty("error").GetString());
        Assert.Single(await Drills(services, session));
    }

    [Fact]
    public async Task An_active_authored_drill_is_resumed_by_the_authored_path_not_replaced()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session) = await Ready(host, exhausted: true);
        var (_, authored) = await Start(services, session, route: null);
        var (_, resumed) = await Start(services, session, Route);
        Assert.Equal(authored.GetProperty("drillId").GetGuid(), resumed.GetProperty("drillId").GetGuid());
        Assert.Null(Assert.Single(await Drills(services, session)).Generated);
    }
}
