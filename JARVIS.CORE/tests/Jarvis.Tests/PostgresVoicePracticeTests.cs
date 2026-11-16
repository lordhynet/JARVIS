using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Voice_practice_resumes_after_restart_and_parallel_hosts_save_one_canonical_answer()
    {
        await using var database = await TestDatabase.CreateAsync();
        Guid sessionId, startId = Guid.NewGuid(), answerId = Guid.NewGuid(), drillId;
        using (var first = new PostgresApiFactory(database.ConnectionString))
        {
            using var client = first.AuthorisedClient();
            var session = await CreateSession(client); sessionId = session.Id;
            var sessions = first.Services.GetRequiredService<ISessionStore>();
            await sessions.UpdateAsync(sessionId, s => s with { Messages = [new(startId, "user", "Start a deep dive.", DateTimeOffset.UtcNow)] });
            var drill = await first.Services.GetRequiredService<VoicePracticeService>().StartAsync(sessionId, startId, TrainingMode.DeepDive, default);
            drillId = drill.DrillId;
            await sessions.UpdateAsync(sessionId, s => s with { Messages = [..s.Messages!, new(answerId, "user", "I would identify the boundary and then check its failure behavior.", DateTimeOffset.UtcNow)] });
        }
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var x = a.AuthorisedClient(); using var y = b.AuthorisedClient();
        var left = a.Services.GetRequiredService<VoicePracticeService>(); var right = b.Services.GetRequiredService<VoicePracticeService>();
        Assert.Equal(drillId, (await left.CurrentAsync(sessionId, default))!.DrillId);
        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? left : right).AnswerAsync(sessionId, answerId, default)));
        Assert.Single(answers.Select(answer => answer.AnswerId).Distinct());
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var z = restarted.AuthorisedClient();
        var progress = await restarted.Services.GetRequiredService<VoicePracticeService>().CurrentAsync(sessionId, default);
        Assert.NotNull(progress); Assert.Equal(drillId, progress.DrillId); Assert.Equal(1, progress.Answered);
        var saved = Assert.Single(await restarted.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.TrainingAttempts.Values.ToArray()));
        Assert.Equal("I would identify the boundary and then check its failure behavior.", saved.CurrentAnswer);
        Assert.Equal(sessionId, saved.SessionId);
    }
}
