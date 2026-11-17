using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Voice_coaching_resumes_after_restart_and_parallel_hosts_save_one_canonical_answer()
    {
        await using var database = await TestDatabase.CreateAsync();
        Guid sessionId, coachingId; var utterance = Guid.NewGuid();
        using (var first = new PostgresApiFactory(database.ConnectionString))
        {
            using var client = first.AuthorisedClient(); sessionId = (await CreateSession(client)).Id;
            var sessions = first.Services.GetRequiredService<ISessionStore>(); var start = Guid.NewGuid();
            await sessions.UpdateAsync(sessionId, s => s with { Messages = [new(start, "user", "Start point first", DateTimeOffset.UtcNow)] });
            var service = first.Services.GetRequiredService<VoiceCoachingService>();
            coachingId = (await service.RunAsync(sessionId, "start", start, "point-first", null, null, null, default))!.Id;
            await sessions.UpdateAsync(sessionId, s => s with { Messages = [..s.Messages!, new(utterance, "user", "Recovery is verified. The release is ready.", DateTimeOffset.UtcNow)] });
        }
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var x = a.AuthorisedClient(); using var y = b.AuthorisedClient();
        var left = a.Services.GetRequiredService<VoiceCoachingService>(); var right = b.Services.GetRequiredService<VoiceCoachingService>();
        Assert.Equal(coachingId, (await left.RunAsync(sessionId, "current", Guid.Empty, null, null, null, null, default))!.Id);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? left : right)
            .RunAsync(sessionId, "answer", utterance, null, null, null, null, default)));
        Assert.Single(results.Select(r => r!.AttemptId).Distinct());
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var z = restarted.AuthorisedClient();
        var runtime = restarted.Services.GetRequiredService<IRuntimeReader>();
        var saved = Assert.Single(await runtime.ReadAsync(s => s.CoachingAttempts.Values.ToArray()));
        Assert.Equal("Recovery is verified. The release is ready.", Assert.Single(saved.Answers).Text);
        var projection = Assert.Single(await runtime.ReadAsync(s => s.VoiceCoachings.Values.ToArray()));
        Assert.Equal("Completed", projection.Status); Assert.Equal(saved.Id, projection.AttemptId);
        Assert.Empty(await runtime.ReadAsync(s => s.UsageReservations));
        var sessionStore = restarted.Services.GetRequiredService<ISessionStore>(); var request = Guid.NewGuid();
        await sessionStore.UpdateAsync(sessionId, s => s with { Messages = [..s.Messages!, new(request, "user", "Correct my answer", DateTimeOffset.UtcNow)] });
        await left.CorrectAsync(sessionId, "prepare", request, saved.Id, saved.Revision, default);
        using var correctionHost = new PostgresApiFactory(database.ConnectionString); using var c = correctionHost.AuthorisedClient();
        var correction = correctionHost.Services.GetRequiredService<VoiceCoachingService>();
        Assert.Equal("Correcting", (await correction.RunAsync(sessionId, "current", Guid.Empty, null, null, null, null, default))!.Status);
        var replacement = Guid.NewGuid();
        await sessionStore.UpdateAsync(sessionId, s => s with { Messages = [..s.Messages!, new(replacement, "user", "I verified recovery. Rollout awaits approval.", DateTimeOffset.UtcNow)] });
        var corrected = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? correction : right)
            .CorrectAsync(sessionId, "answer", replacement, saved.Id, saved.Revision, default)));
        Assert.All(corrected, result => Assert.Equal(2, result.Revision));
        var final = Assert.Single(await runtime.ReadAsync(s => s.CoachingAttempts.Values.ToArray()));
        Assert.Equal(2, final.Answers.Count); Assert.Equal("I verified recovery. Rollout awaits approval.", final.Answers[1].Text);
    }
}
