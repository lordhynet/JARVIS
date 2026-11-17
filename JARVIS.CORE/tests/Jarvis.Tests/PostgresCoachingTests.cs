using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Coaching_and_programme_are_atomic_across_hosts_and_survive_restart()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var a = new PostgresApiFactory(db.ConnectionString); using var b = new PostgresApiFactory(db.ConnectionString);
        using var x = a.AuthorisedClient(); using var y = b.AuthorisedClient(); var session = await CreateSession(x);
        var request = new SaveCoachingAnswerRequest(Guid.NewGuid(), session.Id, "point-first", 1, "We basically completed the work.");
        var saved = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => LifeScenarioTests.Post<CoachingAttemptView>(i % 2 == 0 ? x : y, "/api/coaching/attempts", request)));
        Assert.Single(saved.Select(s => s.Attempt.Id).Distinct());
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? x : y).PatchAsJsonAsync($"/api/coaching/attempts/{request.RequestId}", new CorrectCoachingAnswerRequest(Guid.NewGuid(), 1, $"I owned change {i} and verified the result."), Json)));
        try { Assert.Single(responses, r => r.IsSuccessStatusCode); Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)); }
        finally { foreach (var response in responses) response.Dispose(); }
        var date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var plan = await LifeScenarioTests.Post<ImprovementProgramme>(x, "/api/programme", new StartProgrammeRequest(Guid.NewGuid(), date, "UTC"));
        var check = new ProgrammeCheckInRequest(Guid.NewGuid(), 1, 1, "A reflection, not proof of mastery.");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => LifeScenarioTests.Post<ImprovementProgramme>(i % 2 == 0 ? x : y, $"/api/programme/{plan.Id}/check-ins", check)));
        using var restarted = new PostgresApiFactory(db.ConnectionString); using var client = restarted.AuthorisedClient();
        var answer = (await client.GetFromJsonAsync<CoachingAttemptView>($"/api/coaching/attempts/{request.RequestId}", Json))!;
        Assert.Equal(2, answer.Attempt.Answers.Count); Assert.Equal(0, answer.Analysis.FillerCandidates);
        var programme = (await client.GetFromJsonAsync<ProgrammeView>("/api/programme", Json))!;
        Assert.Single(programme.Programme!.CheckIns);
        using var delete = await client.DeleteAsync($"/api/coaching/attempts/{request.RequestId}?expectedRevision=2"); delete.EnsureSuccessStatusCode();
        var report = (await client.GetFromJsonAsync<SessionReview>($"/api/sessions/{session.Id}/review", Json))!; Assert.Equal(0, report.CoachingAttempts);
    }
}
