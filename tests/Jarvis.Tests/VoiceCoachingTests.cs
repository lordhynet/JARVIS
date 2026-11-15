using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class VoiceCoachingTests
{
    private static async Task<(Guid Session, VoiceCoachingView Saved)> Answer(IServiceProvider services)
    {
        var session = await Session(services); var service = services.GetRequiredService<VoiceCoachingService>();
        var start = await Say(services, session, "Start point first");
        await Run(service, session, "start", start, "point-first");
        var answer = await Say(services, session, "The release is ready. Recovery was tested.");
        return (session, (await Run(service, session, "answer", answer))!);
    }

    [Fact]
    public async Task Correction_requires_separate_replacement_preserves_history_and_retries_once()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var services = host.Services; var (session, saved) = await Answer(services);
        var service = services.GetRequiredService<VoiceCoachingService>();
        var request = await Say(services, session, "I want to correct that answer");
        await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(session, "answer", request, saved.AttemptId!.Value, 1, default));
        var prepared = await service.CorrectAsync(session, "prepare", request, saved.AttemptId!.Value, 1, default);
        Assert.Equal("Correcting", prepared.Status);
        await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(session, "answer", request, saved.AttemptId.Value, 1, default));
        var replacement = await Say(services, session, "I verified recovery. The release still needs approval.");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.CorrectAsync(session, "answer", replacement, saved.AttemptId.Value, 1, default)));
        Assert.All(results, result => Assert.Equal(2, result.Revision));
        var attempt = Assert.Single(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.CoachingAttempts.Values.ToArray()));
        Assert.Equal(2, attempt.Answers.Count); Assert.Equal(saved.CapturedAnswer, attempt.Answers[0].Text);
        Assert.Equal("I verified recovery. The release still needs approval.", attempt.Answers[1].Text);
        Assert.Null(attempt.Answers[1].ReportedDurationSeconds);
        Assert.Equal(2, (await Run(service, session, "current"))!.Revision);
        await services.GetRequiredService<CoachingService>().DeleteAsync(attempt.Id, attempt.Revision, default);
        await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(session, "answer", replacement, saved.AttemptId.Value, 1, default));
    }

    [Fact]
    public async Task Correction_conflicts_with_newer_text_edits_and_stop_keeps_latest_answer()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var services = host.Services; var (session, saved) = await Answer(services);
        var service = services.GetRequiredService<VoiceCoachingService>();
        var request = await Say(services, session, "Correct my answer");
        await service.CorrectAsync(session, "prepare", request, saved.AttemptId!.Value, 1, default);
        await services.GetRequiredService<CoachingService>().CorrectAsync(saved.AttemptId.Value, new(Guid.NewGuid(), 1, "Newer text edit"), default);
        var replacement = await Say(services, session, "Old voice replacement");
        await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(session, "answer", replacement, saved.AttemptId.Value, 1, default));
        var stopped = await Run(service, session, "stop", replacement);
        Assert.Equal("Completed", stopped!.Status); Assert.Equal("Newer text edit", (await Run(service, session, "current"))!.CapturedAnswer);
        var otherSession = await Session(services); var spoof = await Say(services, otherSession, "Correct it");
        await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(otherSession, "prepare", spoof, saved.AttemptId.Value, 2, default));
    }

    [Fact]
    public async Task Correction_tool_requires_live_speech_and_returns_revision_for_safe_followup()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var services = host.Services; var (session, saved) = await Answer(services);
        var tools = services.GetRequiredService<CoreVoiceTools>(); var request = await Say(services, session, "Please correct it");
        var args = JsonSerializer.Serialize(new { action = "prepare", utterance_id = request, attempt_id = saved.AttemptId, expected_revision = saved.Revision });
        using var denied = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching_correction", args, session, new HashSet<Guid>(), default));
        Assert.False(denied.RootElement.GetProperty("ok").GetBoolean());
        using var ready = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching_correction", args, session, new HashSet<Guid> { request }, default));
        Assert.True(ready.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, ready.RootElement.GetProperty("result").GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task Pending_correction_can_be_stopped_after_answer_deletion_without_returning_deleted_text()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var services = host.Services; var (session, saved) = await Answer(services);
        var service = services.GetRequiredService<VoiceCoachingService>();
        var request = await Say(services, session, "Correct it");
        await service.CorrectAsync(session, "prepare", request, saved.AttemptId!.Value, 1, default);
        await services.GetRequiredService<CoachingService>().DeleteAsync(saved.AttemptId.Value, 1, default);
        var stop = await Say(services, session, "Stop correction");
        var stopped = await Run(service, session, "stop", stop);
        Assert.Equal("Completed", stopped!.Status); Assert.Null(stopped.CapturedAnswer);
        Assert.Equal(stopped, await Run(service, session, "stop", stop));
        var start = await Say(services, session, "Start small talk");
        Assert.Equal("Question", (await Run(service, session, "start", start, "small-talk"))!.Status);
    }
    private static async Task<Guid> Session(IServiceProvider services)
    {
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Coaching fixture", SessionStatus.Active, null, null, now, now, []));
        return id;
    }
    private static async Task<Guid> Say(IServiceProvider services, Guid session, string text, string role = "user")
    {
        var id = Guid.NewGuid();
        await services.GetRequiredService<ISessionStore>().UpdateAsync(session, s => s with { Messages = [..s.Messages ?? [], new(id, role, text, DateTimeOffset.UtcNow)] });
        return id;
    }
    private static Task<VoiceCoachingView?> Run(VoiceCoachingService service, Guid session, string action, Guid utterance = default, string? exercise = null) =>
        service.RunAsync(session, action, utterance, exercise, null, null, null, default);

    [Fact]
    public async Task Coaching_saves_canonical_transcript_once_and_does_not_call_paid_assessment()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No paid calls."));
        var services = host.Services; var session = await Session(services);
        var service = services.GetRequiredService<VoiceCoachingService>();
        var start = await Say(services, session, "Let's practise a workplace update");
        var question = await Run(service, session, "start", start, "point-first");
        var restored = new VoiceCoachingService(services.GetRequiredService<IRuntimeStore>(), services.GetRequiredService<ISessionReader>(), TimeProvider.System);
        Assert.Equal(question, await Run(restored, session, "current"));
        var utterance = await Say(services, session, "The release is ready. We verified recovery. Please approve the rollout.");
        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Run(service, session, "answer", utterance)));
        Assert.Single(answers.Select(x => x!.AttemptId).Distinct());
        Assert.Equal("Completed", answers[0]!.Status);
        var store = services.GetRequiredService<IRuntimeStore>();
        var saved = Assert.Single(await store.ReadAsync(s => s.CoachingAttempts.Values.ToArray()));
        Assert.Equal(session, saved.SessionId); Assert.Equal("point-first", saved.ExerciseId);
        Assert.Equal("The release is ready. We verified recovery. Please approve the rollout.", Assert.Single(saved.Answers).Text);
        Assert.Null(saved.Answers[0].ReportedDurationSeconds);
        Assert.Equal(saved.Id, (await Run(service, session, "current"))!.AttemptId);
        Assert.Empty(await store.ReadAsync(s => s.UsageReservations));
        Assert.Empty(await store.ReadAsync(s => s.AiCoachAssessments));
        await services.GetRequiredService<CoachingService>().DeleteAsync(saved.Id, saved.Revision, default);
        await Assert.ThrowsAsync<DomainException>(() => Run(service, session, "answer", utterance));
    }

    [Fact]
    public async Task Coaching_rejects_start_speech_generated_and_historical_answers_and_requires_stop_to_change()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var services = host.Services; var session = await Session(services); var service = services.GetRequiredService<VoiceCoachingService>();
        var start = await Say(services, session, "Start point first");
        await Run(service, session, "start", start, "point-first");
        await Assert.ThrowsAsync<DomainException>(() => Run(service, session, "answer", start));
        await Assert.ThrowsAsync<DomainException>(() => Run(service, session, "start", start, "small-talk"));
        var generated = await Say(services, session, "Invented answer", "assistant");
        await Assert.ThrowsAsync<DomainException>(() => Run(service, session, "answer", generated));
        var earlier = await Say(services, session, "Earlier answer"); var stop = await Say(services, session, "Stop coaching");
        await Assert.ThrowsAsync<DomainException>(() => Run(service, session, "answer", earlier));
        var cancelled = await Run(service, session, "stop", stop);
        Assert.Equal("Cancelled", cancelled!.Status);
        Assert.Equal(cancelled, await Run(service, session, "stop", stop));
        var next = await Say(services, session, "Practise small talk");
        Assert.NotEqual(cancelled.Id, (await Run(service, session, "start", next, "small-talk"))!.Id);
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.CoachingAttempts));
    }

    [Fact]
    public async Task Coaching_checks_story_versions_and_retention_and_privacy_exports_include_lifecycle()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var services = host.Services; var session = await Session(services); var service = services.GetRequiredService<VoiceCoachingService>();
        var store = services.GetRequiredService<IRuntimeStore>(); var now = DateTimeOffset.UtcNow;
        var story = new Story(Guid.NewGuid(), new("Real story", "Project", "Service"), 1, now, now);
        await store.UpdateAsync(s => { s.Stories[story.Id] = story; return true; });
        var start = await Say(services, session, "Practise my saved story");
        await service.RunAsync(session, "start", start, "story-30", story.Id, 1, null, default);
        await store.UpdateAsync(s => { s.Stories[story.Id] = story with { Version = 2 }; return true; });
        var answer = await Say(services, session, "My contribution was implementing recovery");
        await Assert.ThrowsAsync<DomainException>(() => Run(service, session, "answer", answer));
        Assert.Empty(await store.ReadAsync(s => s.CoachingAttempts));
        Assert.Single((await services.GetRequiredService<IPrivacyReader>().ExportAsync(now)).VoiceCoachings!);
        await Run(service, session, "stop", answer);
        await store.UpdateAsync(s => {
            Assert.Empty(PrivacyData.EmptyAfterErase(s).VoiceCoachings);
            PrivacyData.RetainRuntime(s, new(), now.AddDays(31), new([], [], [], [], [], [], [], []));
            return true;
        });
        Assert.Empty(await store.ReadAsync(s => s.VoiceCoachings));
        await store.UpdateAsync(s => { s.RetiredSessions.Add(session); return true; });
        await Assert.ThrowsAsync<DomainException>(() => Run(service, session, "start", answer, "point-first"));
    }

    [Fact]
    public async Task Coaching_tools_reject_untrusted_text_and_speech_outside_live_window()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var services = host.Services; var session = await Session(services); var tools = services.GetRequiredService<CoreVoiceTools>();
        var source = await Say(services, session, "Start coaching");
        var args = JsonSerializer.Serialize(new { action = "start", utterance_id = source, exercise_id = "point-first", story_id = (Guid?)null, story_version = (int?)null, claim_index = (int?)null });
        using var rejected = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching", args, session, new HashSet<Guid>(), default));
        Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean());
        using var accepted = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching", args, session, new HashSet<Guid> { source }, default));
        Assert.True(accepted.RootElement.GetProperty("ok").GetBoolean());
        var injected = args[..^1] + ",\"text\":\"generated answer\"}";
        using var extra = JsonDocument.Parse(await tools.ExecuteAsync("core_coaching", injected, session, new HashSet<Guid> { source }, default));
        Assert.False(extra.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Coaching_erasure_removes_selection_and_answers_and_blocks_late_replay()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException());
        var services = host.Services; var session = await Session(services); var service = services.GetRequiredService<VoiceCoachingService>();
        var start = await Say(services, session, "Start point first");
        await Run(service, session, "start", start, "point-first");
        var answer = await Say(services, session, "The migration succeeded. Recovery was tested.");
        await Run(service, session, "answer", answer);
        var privacy = services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), PrivacyData.Hash("fixture-owner-key"), DateTimeOffset.UtcNow);
        var export = await privacy.ExportAsync(DateTimeOffset.UtcNow);
        Assert.Empty(export.Coaching); Assert.Empty(export.VoiceCoachings!);
        await Assert.ThrowsAsync<DomainException>(() => Run(service, session, "answer", answer));
    }
}
