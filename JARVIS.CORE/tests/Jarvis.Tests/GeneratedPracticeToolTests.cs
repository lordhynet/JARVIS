using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// core_practice_generate through the real DI host. Fixture route only; no provider is called.
public sealed class GeneratedPracticeToolTests
{
    private static readonly ModelRoute Route = new("openai", "realtime-fixture", "Realtime", "policy-1", "rates-1", new DateOnly(2026, 11, 1), 0, 0, 100);

    private static string Args(Guid utterance, string prompt = "Design a URL shortener: what do you clarify first?", string outline = "requirements") =>
        JsonSerializer.Serialize(new { utterance_id = utterance, outline_key = outline, title = "Clarifying requirements", prompt,
            reference = "Scale, latency, durability and abuse.", follow_ups = new[] { "What if writes spike?" } });

    private static async Task<(IServiceProvider Services, Guid Session, Guid Utterance)> Ready(ProviderFactory host, bool enabled)
    {
        var app = enabled ? host.WithWebHostBuilder(b => b.UseSetting(PracticeGenerationOptions.ConfigKey, "true")) : host;
        var services = app.Services; var store = services.GetRequiredService<IRuntimeStore>();
        var session = (await services.GetRequiredService<Jarvis.Orchestrator.Sessions.SessionService>().CreateAsync(new(ActivityType.General, "Practice fixture", null))).Id;
        await VoiceToolFixture.OpenAsync(services, session);
        var utterance = Guid.NewGuid(); var story = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
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
        if (enabled)
            await services.GetRequiredService<GeneratedPracticeService>().TryStartAsync(Guid.NewGuid(), TrainingMode.RapidDrill, LearningDomain.SystemDesign,
                new(session, utterance, 0, GeneratedPracticeService.RouteRevision(Route), GeneratedPracticeService.Provenance(Route)));
        return (services, session, utterance);
    }

    private static Task<string> Call(IServiceProvider services, Guid session, Guid utterance, string args, ModelRoute? route, IReadOnlySet<Guid>? window = null)
    {
        using var scope = PrivacyContext.Enter(0);
        return services.GetRequiredService<CoreVoiceTools>().ExecuteAsync(CoreVoiceTools.GenerateName, args, session,
            window ?? new HashSet<Guid> { utterance }, CancellationToken.None, utterance, generationRoute: route);
    }

    private static bool Ok(string json) => JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean();

    [Fact]
    public async Task The_tool_is_unavailable_and_unadvertised_unless_enabled_with_a_prepared_route()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (off, session, utterance) = await Ready(host, enabled: false);
        Assert.Contains("not available", await Call(off, session, utterance, Args(utterance), Route));
        var (on, s2, u2) = await Ready(host, enabled: true);
        Assert.Contains("not available", await Call(on, s2, u2, Args(u2), route: null));
        var names = (IEnumerable<object> defs) => defs.Select(d => JsonSerializer.SerializeToElement(d).GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain(CoreVoiceTools.GenerateName, names(CoreVoiceTools.DefinitionsFor(false)));
        Assert.DoesNotContain(CoreVoiceTools.Capabilities, c => c.Key.Contains(CoreVoiceTools.GenerateName));
        Assert.Contains(CoreVoiceTools.GenerateName, names(CoreVoiceTools.DefinitionsFor(false, generation: true)));
        Assert.DoesNotContain(CoreVoiceTools.GenerateName, names(CoreVoiceTools.DefinitionsFor(true, generation: true)));
        Assert.Empty(off.GetRequiredService<InMemoryRuntimeStore>().GeneratedQuestionsSnapshot());
    }

    [Fact]
    public async Task A_current_call_stores_the_question_with_core_provenance_and_replays_idempotently()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session, utterance) = await Ready(host, enabled: true);
        var first = await Call(services, session, utterance, Args(utterance), Route);
        Assert.True(Ok(first), first); Assert.Contains("AI-generated", first);
        var row = Assert.Single(services.GetRequiredService<InMemoryRuntimeStore>().GeneratedQuestionsSnapshot());
        Assert.Equal(GeneratedPracticeService.Provenance(Route), row.ProvenanceJson);
        var again = await Call(services, session, utterance, Args(utterance), Route);
        Assert.True(JsonDocument.Parse(again).RootElement.GetProperty("result").GetProperty("replayed").GetBoolean());
        Assert.False(Ok(await Call(services, session, utterance, Args(utterance, prompt: "Something else"), Route)));
        Assert.Single(services.GetRequiredService<InMemoryRuntimeStore>().GeneratedQuestionsSnapshot());
    }

    [Fact]
    public async Task Uncaptured_utterances_other_routes_and_oversized_arguments_store_nothing()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session, utterance) = await Ready(host, enabled: true);
        Assert.Contains("capture", await Call(services, session, utterance, Args(utterance), Route, window: new HashSet<Guid>()));
        Assert.False(Ok(await Call(services, session, utterance, Args(utterance), Route with { RateCardRevision = "rates-2" })));
        Assert.Contains("limit", await Call(services, session, utterance, Args(utterance, prompt: new string('x', CoreVoiceTools.GenerateArgumentLimit)), Route));
        var tooMany = JsonSerializer.Serialize(new { utterance_id = utterance, outline_key = "requirements", title = "t", prompt = "p", reference = "r", follow_ups = new[] { "a", "b", "c" } });
        Assert.Contains("Follow-ups", await Call(services, session, utterance, tooMany, Route));
        Assert.Empty(services.GetRequiredService<InMemoryRuntimeStore>().GeneratedQuestionsSnapshot());
    }
}
