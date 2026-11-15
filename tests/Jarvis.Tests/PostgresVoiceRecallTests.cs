using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Voice_recall_resumes_after_restart_and_parallel_ratings_schedule_once()
    {
        await using var database = await TestDatabase.CreateAsync();
        Guid sessionId, cardId, ratingId = Guid.NewGuid(), recallId;
        using (var first = new PostgresApiFactory(database.ConnectionString))
        {
            using var client = first.AuthorisedClient(); sessionId = (await CreateSession(client)).Id;
            cardId = (await first.Services.GetRequiredService<LearningService>().CreateAsync(new(LearningDomain.French, "Hello?", "Bonjour"), default)).Id;
            var sessions = first.Services.GetRequiredService<ISessionStore>(); var start = Guid.NewGuid(); var answer = Guid.NewGuid();
            await sessions.UpdateAsync(sessionId, s => s with { Messages = [new(start, "user", "Review my cards", DateTimeOffset.UtcNow)] });
            var service = first.Services.GetRequiredService<VoiceRecallService>();
            recallId = (await service.RunAsync(sessionId, "start", start, "None", default))!.RecallId;
            await sessions.UpdateAsync(sessionId, s => s with { Messages = [..s.Messages!, new(answer, "user", "Bonjour", DateTimeOffset.UtcNow)] });
            await service.RunAsync(sessionId, "reveal", answer, "None", default);
            await sessions.UpdateAsync(sessionId, s => s with { Messages = [..s.Messages!, new(ratingId, "user", "Hard", DateTimeOffset.UtcNow)] });
        }
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var x = a.AuthorisedClient(); using var y = b.AuthorisedClient();
        var left = a.Services.GetRequiredService<VoiceRecallService>(); var right = b.Services.GetRequiredService<VoiceRecallService>();
        var resumed = await left.RunAsync(sessionId, "current", Guid.Empty, "None", default);
        Assert.Equal(recallId, resumed!.RecallId); Assert.Equal("Rating", resumed.Status); Assert.Equal("Bonjour", resumed.Reference);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? left : right).RunAsync(sessionId, "rate", ratingId, "Hard", default)));
        Assert.Single(results.Select(r => r!.NextDueAt).Distinct());
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var z = restarted.AuthorisedClient();
        var saved = Assert.Single(await restarted.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.Reviews.Values.ToArray()));
        Assert.Equal(cardId, saved.Event.LearningItemId); Assert.Equal(ReviewRating.Hard, saved.Event.Rating);
        Assert.Equal("not_measured", saved.Event.TimingSource);
        var done = Assert.Single((await restarted.Services.GetRequiredService<IPrivacyReader>().ExportAsync(DateTimeOffset.UtcNow)).VoiceRecalls!);
        Assert.Equal("Completed", done.Status); Assert.Equal(ratingId, done.RatingUtteranceId);
    }
}
