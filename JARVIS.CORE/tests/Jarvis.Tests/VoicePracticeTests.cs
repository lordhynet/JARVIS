using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class VoicePracticeTests
{
    [Fact]
    public async Task Voice_coaching_reuses_catalog_and_authored_prompts_without_saving_or_paid_calls()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No paid calls expected."));
        var tools = host.Services.GetRequiredService<CoreVoiceTools>();
        var session = await Session(host.Services);
        using var catalog = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching_catalog", "{}", session, new HashSet<Guid>(), default));
        Assert.True(catalog.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("point-first", catalog.RootElement.GetRawText());
        var args = JsonSerializer.Serialize(new { exercise_id = "point-first", story_id = (string?)null, story_version = (int?)null, claim_index = (int?)null });
        using var prompt = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching_prompt", args, session, new HashSet<Guid>(), default));
        Assert.True(prompt.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("lead with the outcome", prompt.RootElement.GetRawText());
        var store = host.Services.GetRequiredService<IRuntimeStore>();
        Assert.Empty(await store.ReadAsync(s => s.CoachingAttempts));
        Assert.Empty(await store.ReadAsync(s => s.UsageReservations));
    }

    [Theory]
    [InlineData("story-30")]
    [InlineData("cv-defence")]
    [InlineData("invented-exercise")]
    public async Task Voice_coaching_rejects_missing_saved_sources_and_unknown_exercises(string exercise)
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var tools = host.Services.GetRequiredService<CoreVoiceTools>();
        var args = JsonSerializer.Serialize(new { exercise_id = exercise, story_id = (string?)null, story_version = (int?)null, claim_index = (int?)null });
        using var reply = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching_prompt", args, await Session(host.Services), new HashSet<Guid>(), default));
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Voice_coaching_preserves_saved_claim_provenance_and_rejects_stale_story_versions()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var tools = host.Services.GetRequiredService<CoreVoiceTools>();
        var store = host.Services.GetRequiredService<IRuntimeStore>();
        var now = DateTimeOffset.UtcNow;
        var story = new Story(Guid.NewGuid(), new("Real story", "Project", "Service", CvClaims: ["Measured saved claim"]), 1, now, now);
        await store.UpdateAsync(s => { s.Stories[story.Id] = story; return true; });
        var args = JsonSerializer.Serialize(new { exercise_id = "cv-defence", story_id = story.Id, story_version = 1, claim_index = 0 });
        var session = await Session(host.Services);
        using var valid = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching_prompt", args, session, new HashSet<Guid>(), default));
        Assert.True(valid.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Measured saved claim", valid.RootElement.GetRawText());
        await store.UpdateAsync(s => { s.Stories[story.Id] = story with { Version = 2 }; return true; });
        using var stale = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching_prompt", args, session, new HashSet<Guid>(), default));
        Assert.False(stale.RootElement.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("Measured saved claim", stale.RootElement.GetRawText());
    }

    private static async Task<Guid> Session(IServiceProvider services)
    {
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Voice fixture", SessionStatus.Active, null, null, now, now, []));
        await VoiceToolFixture.OpenAsync(services, id);
        return id;
    }
    private static async Task<Guid> Say(IServiceProvider services, Guid session, string text, string role = "user")
    {
        var id = Guid.NewGuid();
        await services.GetRequiredService<ISessionStore>().UpdateAsync(session, s => s with { Messages = [..s.Messages ?? [], new(id, role, text, DateTimeOffset.UtcNow)] });
        return id;
    }
    [Fact]
    public async Task Voice_practice_uses_canonical_drills_and_verbatim_answers_with_retry_stable_identity()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No paid calls expected."));
        var services = host.Services; var session = await Session(services); var practice = services.GetRequiredService<VoicePracticeService>();
        var start = await Say(services, session, "Let's practise a rapid technical question.");
        var question = await practice.StartAsync(session, start, TrainingMode.RapidDrill, default);
        Assert.Equal(question, await practice.StartAsync(session, start, TrainingMode.RapidDrill, default));
        Assert.DoesNotContain("reference", JsonSerializer.Serialize(question), StringComparison.OrdinalIgnoreCase);
        var source = await Say(services, session, "My first step is to identify the transaction boundary.");
        var saved = await practice.AnswerAsync(session, source, default);
        Assert.Equal(saved, await practice.AnswerAsync(session, source, default));
        Assert.Equal("My first step is to identify the transaction boundary.", saved.CapturedAnswer);
        Assert.NotEmpty(saved.Reference);
        var runtime = services.GetRequiredService<IRuntimeStore>();
        var attempt = Assert.Single(await runtime.ReadAsync(s => s.TrainingAttempts.Values.ToArray()));
        Assert.Equal(session, attempt.SessionId); Assert.Equal(question.DrillId, attempt.DrillId);
        Assert.Empty(attempt.Assessments); Assert.Empty(await runtime.ReadAsync(s => s.UsageReservations));
        // A new service instance resumes the same persisted Core projection.
        var restored = new VoicePracticeService(runtime, services.GetRequiredService<ISessionReader>(), services.GetRequiredService<TrainingDrillService>(), services.GetRequiredService<TrainingService>());
        var current = await restored.CurrentAsync(session, default);
        if (saved.Progress.Status == "Active") Assert.Equal(saved.Progress, current); else Assert.Null(current);
    }
    [Fact]
    public async Task Practice_rejects_start_instruction_generated_text_and_nonlatest_utterance_as_answers()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var services = host.Services; var session = await Session(services); var practice = services.GetRequiredService<VoicePracticeService>();
        var start = await Say(services, session, "Start practice");
        await practice.StartAsync(session, start, TrainingMode.DeepDive, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => practice.AnswerAsync(session, start, default))).StatusCode);
        var assistant = await Say(services, session, "Invented assistant answer", "assistant");
        await Assert.ThrowsAsync<DomainException>(() => practice.AnswerAsync(session, assistant, default));
        var earlier = await Say(services, session, "Earlier thought"); await Say(services, session, "Actually can you clarify?");
        await Assert.ThrowsAsync<DomainException>(() => practice.AnswerAsync(session, earlier, default));
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.TrainingAttempts));
    }
    [Fact]
    public async Task Practice_mode_changes_require_stopping_current_drill_and_do_not_delete_saved_history()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var session = await Session(services); var practice = services.GetRequiredService<VoicePracticeService>();
        var start = await Say(services, session, "Start deep dive"); var original = await practice.StartAsync(session, start, TrainingMode.DeepDive, default);
        var change = await Say(services, session, "Change mode");
        await Assert.ThrowsAsync<DomainException>(() => practice.StartAsync(session, change, TrainingMode.RapidDrill, default));
        var stopped = await practice.CancelAsync(session, change, default); Assert.Equal("Cancelled", stopped.Status);
        var next = await Say(services, session, "Start rapid drill"); var replacement = await practice.StartAsync(session, next, TrainingMode.RapidDrill, default);
        Assert.NotEqual(original.DrillId, replacement.DrillId);
        Assert.Equal(2, await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.TrainingDrills.Count));
    }
    [Fact]
    public async Task Voice_tools_reject_arbitrary_actions_extra_fields_and_utterances_outside_the_live_window()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var session = await Session(services); var tools = services.GetRequiredService<CoreVoiceTools>();
        var source = await Say(services, session, "Start practice");
        foreach (var (name, args, ids) in new[] {
            ("shell", "{}", new HashSet<Guid>()),
            ("core_practice_start", JsonSerializer.Serialize(new { utterance_id = source, mode = "RapidDrill", topic = (string?)null, text = "invented" }), new HashSet<Guid> { source }),
            ("core_practice_start", JsonSerializer.Serialize(new { utterance_id = source, mode = "RapidDrill", topic = (string?)null }), new HashSet<Guid>())
        }) {
            using var result = JsonDocument.Parse(await tools.ExecuteAsync(name, args, session, ids, default));
            Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        }
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.TrainingDrills));
        using var valid = JsonDocument.Parse(await tools.ExecuteAsync("core_practice_start", JsonSerializer.Serialize(new { utterance_id = source, mode = "RapidDrill", topic = (string?)null }), session, new HashSet<Guid> { source }, default));
        Assert.True(valid.RootElement.GetProperty("ok").GetBoolean());
    }
    [Fact]
    public async Task Voice_context_excludes_goals_without_cloud_consent_and_hides_due_answers()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var runtime = services.GetRequiredService<IRuntimeStore>(); var now = DateTimeOffset.UtcNow;
        await runtime.UpdateAsync(s => {
            foreach (var consent in new[] { false, true }) {
                var id = Guid.NewGuid(); s.Goals[id] = new(id, 1, consent ? "Visible goal" : "Private goal", "Brief", "personal-planner", consent, now, now.AddDays(1), "Active", now, now);
            }
            var card = Guid.NewGuid(); s.Learning[card] = new(card, LearningDomain.CSharpDotNet, "Question", "Secret reference answer", 0, 5, now, now); return true;
        });
        var tools = services.GetRequiredService<CoreVoiceTools>();
        var session = await Session(services);
        var goals = await tools.ExecuteAsync("core_context", "{\"section\":\"goals\"}", session, new HashSet<Guid>(), default);
        Assert.Contains("Visible goal", goals); Assert.DoesNotContain("Private goal", goals);
        var due = await tools.ExecuteAsync("core_context", "{\"section\":\"due\"}", session, new HashSet<Guid>(), default);
        Assert.Contains("Question", due); Assert.DoesNotContain("Secret reference answer", due);
    }
    [Fact]
    public async Task Audio_lease_becomes_noncurrent_immediately_when_session_owner_changes()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var session = await Session(services); var store = services.GetRequiredService<ISessionStore>();
        var lease = new AudioLease(session, "original-phone", 77, DateTimeOffset.UtcNow.AddMinutes(1));
        await store.UpdateAsync(session, s => s with { ActiveDeviceId = lease.DeviceId });
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { s.AudioLease = lease; return true; });
        var ownership = services.GetRequiredService<AudioOwnership>(); Assert.True(await ownership.CurrentAsync(lease, default));
        await store.UpdateAsync(session, s => s with { ActiveDeviceId = "new-phone" });
        Assert.False(await ownership.CurrentAsync(lease, default));
        await store.UpdateAsync(session, s => s with { ActiveDeviceId = lease.DeviceId, Status = SessionStatus.Paused });
        Assert.False(await ownership.CurrentAsync(lease, default));
    }
}
