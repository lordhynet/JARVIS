using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class CoachingScenarioTests
{
    internal static async Task<Guid> Session(IServiceProvider services)
    {
        var session = (await services.GetRequiredService<SessionService>().CreateAsync(new(ActivityType.MockInterview, "Scenario fixture", null))).Id;
        await VoiceToolFixture.OpenAsync(services, session); return session;
    }
    internal static async Task<Guid> Say(IServiceProvider services, Guid session, string text, bool restricted = false)
    {
        var id = Guid.NewGuid(); await services.GetRequiredService<VoiceTranscriptService>().RecordAsync(session, id, ChatRoles.User, text, 30, default,
            restricted: restricted, timing: new(30, 6, SpeechDelivery.PcmQuietMethod)); return id;
    }
    private static async Task<JsonElement> Tool(IServiceProvider services, Guid session, string action, Guid source, string? exercise = null, VoiceMode mode = VoiceMode.Everyday)
    {
        var json = await services.GetRequiredService<CoreVoiceTools>().ExecuteAsync("core_coaching", JsonSerializer.Serialize(new { action, utterance_id = source,
            exercise_id = exercise, story_id = (Guid?)null, story_version = (int?)null, claim_index = (int?)null }), session, new HashSet<Guid> { source }, default,
            source, _ => Task.FromResult(true), mode);
        using var result = JsonDocument.Parse(json); return result.RootElement.Clone();
    }
    [Theory]
    [InlineData("scenario-panel")]
    [InlineData("scenario-recruiter")]
    [InlineData("scenario-presentation")]
    [InlineData("scenario-hostile-qa")]
    public async Task Authored_scenario_runs_one_question_per_captured_answer_then_returns_evidence_metrics_and_rubric(string key)
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("Authored coaching must not call a paid provider."));
        using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false")); var services = host.Services;
        var session = await Session(services); var start = await Say(services, session, "Start " + key);
        var question = await Tool(services, session, "start", start, key); Assert.True(question.GetProperty("ok").GetBoolean(), question.ToString());
        Assert.Equal(1, question.GetProperty("result").GetProperty("questionNumber").GetInt32());
        var definition = CoachingScenarios.Get(key); Assert.Contains(definition.Turns[0].Question, question.ToString());
        for (var i = 0; i < definition.Turns.Count; i++) {
            var answer = await Say(services, session, "My point is clear because I chose an alternative. I owned the constraint and we measured the evidence. Next I recommend a source check for unknown failure and trade-off.");
            question = await Tool(services, session, "answer", answer); Assert.True(question.GetProperty("ok").GetBoolean(), question.ToString());
            Assert.Equal(i == 2 ? "Completed" : "Question", question.GetProperty("result").GetProperty("status").GetString());
            var replay = await Tool(services, session, "answer", answer); Assert.True(replay.GetProperty("ok").GetBoolean());
            if (i < 2) Assert.Contains(definition.Turns[i + 1].Question, question.ToString());
        }
        var scenario = question.GetProperty("result").GetProperty("scenario"); Assert.Equal(3, scenario.GetProperty("answers").GetInt32());
        Assert.Equal(3, scenario.GetProperty("evidence").GetArrayLength()); Assert.Contains("not an independent competence grade", scenario.GetProperty("assessmentKind").GetString());
        Assert.Equal(3, scenario.GetProperty("delivery")[0].GetProperty("turns").GetInt32());
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
        Assert.Equal(3, await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.CoachingAttempts.Count));
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var debriefs = (await owner.GetFromJsonAsync<CoachingScenarioDebrief[]>($"/api/coaching/sessions/{session}/scenario-debriefs"))!;
        Assert.Equal(3, Assert.Single(debriefs).Answers);
        if (key == "scenario-panel") {
            var attempt = debriefs[0].Evidence[0];
            await services.GetRequiredService<CoachingService>().CorrectAsync(attempt.AttemptId, new(Guid.NewGuid(), attempt.Revision, "A typed replacement with different words."), default);
            var revised = (await owner.GetFromJsonAsync<CoachingScenarioDebrief[]>($"/api/coaching/sessions/{session}/scenario-debriefs"))!;
            Assert.Null(revised[0].Evidence[0].VoiceTurnId); Assert.Equal(2, revised[0].Delivery[0].Turns);
            await services.GetRequiredService<CoachingService>().DeleteAsync(attempt.AttemptId, attempt.Revision + 1, default);
            var unavailable = (await owner.GetFromJsonAsync<CoachingScenarioDebrief[]>($"/api/coaching/sessions/{session}/scenario-debriefs"))!;
            Assert.Equal("SourceUnavailable", unavailable[0].Status); Assert.Empty(unavailable[0].Scores); Assert.Empty(unavailable[0].Delivery);
        }
    }
    [Fact]
    public async Task Scenario_is_a_registry_entry_and_disabling_cancels_active_practice_without_erasing_answers()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false")); var services = host.Services;
        var session = await Session(services); var source = await Say(services, session, "Start panel interview");
        Assert.True((await Tool(services, session, "start", source, "scenario-panel")).GetProperty("ok").GetBoolean());
        var answer = await Say(services, session, "I chose an alternative because we had a constraint."); await Tool(services, session, "answer", answer);
        var registry = services.GetRequiredService<IDomainRegistry>(); var domain = await registry.PreviewAsync("scenario-panel", null, default);
        Assert.Equal("scenario-panel", domain.Version.Manifest.Configuration!["coachingScenarioId"]);
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) await registry.DisableAsync("scenario-panel", new(Guid.NewGuid(), domain.Definition.Revision), new("owner", ["domain.manage"]), default);
        var store = services.GetRequiredService<IRuntimeStore>(); Assert.Equal("Cancelled", await store.ReadAsync(s => s.VoiceCoachings.Values.Single().Status));
        Assert.Single(await store.ReadAsync(s => s.CoachingAttempts.Values.ToArray()));
        var restart = await Say(services, session, "Start panel interview"); Assert.False((await Tool(services, session, "start", restart, "scenario-panel")).GetProperty("ok").GetBoolean());
    }
    [Fact]
    public async Task Cheap_scenarios_refuse_serious_mode_and_restricted_speech()
    {
        using var host = new JarvisApiFactory(); var services = host.Services; var session = await Session(services);
        var source = await Say(services, session, "Start recruiter screen");
        var refused = await Tool(services, session, "start", source, "scenario-recruiter", VoiceMode.Serious); Assert.False(refused.GetProperty("ok").GetBoolean()); Assert.Contains("Everyday", refused.ToString());
        source = await Say(services, session, "Start recruiter screen", true);
        Assert.False((await Tool(services, session, "start", source, "scenario-recruiter")).GetProperty("ok").GetBoolean());
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.VoiceCoachings));
    }
    [Fact]
    public async Task Catalogue_stays_within_voice_bound_and_unknown_scenarios_cannot_enter_a_manifest()
    {
        using var host = new JarvisApiFactory(); var session = await Session(host.Services);
        var catalog = await host.Services.GetRequiredService<CoreVoiceTools>().ExecuteAsync("core_coaching_catalog", "{}", session, new HashSet<Guid>(), default);
        Assert.Contains("scenario-panel", catalog); Assert.DoesNotContain("scenario-panel-turn-2", catalog);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(catalog) <= CoreVoiceTools.MaxOutputBytes);
        Assert.Throws<DomainException>(() => DomainPolicy.Validate(DomainRegistryTests.Education() with { Configuration = new Dictionary<string, string> { ["coachingScenarioId"] = "uninstalled" } }));
    }
}
