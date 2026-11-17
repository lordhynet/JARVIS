using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Training_deduplicates_across_hosts_persists_and_recalculates_after_restart()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var a = new PostgresApiFactory(database.ConnectionString);
        using var b = new PostgresApiFactory(database.ConnectionString);
        using var x = a.AuthorisedClient(); using var y = b.AuthorisedClient();
        var session = await CreateSession(x);
        var request = new SubmitTrainingAnswerRequest(Guid.NewGuid(), session.Id, QuestionBank.All[0].Id, 1, TrainingMode.RapidDrill, "Await releases the thread.");
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => LifeScenarioTests.Post<TrainingAttempt>(i % 2 == 0 ? x : y, "/api/training/attempts", request)));
        Assert.Single(results.Select(r => r.Id).Distinct());
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? x : y).PostAsJsonAsync(
            $"/api/training/attempts/{request.RequestId}/assessments", new SelfAssessmentRequest(Guid.NewGuid(), 1, 1,
                [new(AssessmentDimension.TechnicalAccuracy, 3, "Await releases the thread", "Reference matches")]), LifeScenarioTests.Json)));
        try
        {
            Assert.Single(responses, r => r.IsSuccessStatusCode);
            Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        }
        finally { foreach (var response in responses) response.Dispose(); }
        using var restarted = new PostgresApiFactory(database.ConnectionString);
        using var client = restarted.AuthorisedClient();
        var saved = (await client.GetFromJsonAsync<TrainingAttempt>($"/api/training/attempts/{request.RequestId}", LifeScenarioTests.Json))!;
        Assert.Equal("self", saved.CanonicalAssessment!.Source);
        Assert.Equal(3d, saved.CanonicalAssessment.ContentScore);
        Assert.Equal(session.Id, saved.SessionId);
        var reader = restarted.Services.GetRequiredService<IRuntimeReader>();
        Assert.Equal(7, await reader.ReadAsync(s => s.Learning[request.QuestionId].Stability));
        using var correction = await client.PatchAsJsonAsync($"/api/training/attempts/{request.RequestId}/answer",
            new CorrectTrainingAnswerRequest(Guid.NewGuid(), saved.Revision, "A revised answer"), LifeScenarioTests.Json);
        correction.EnsureSuccessStatusCode();
        Assert.Null(await reader.ReadAsync(s => s.Learning[request.QuestionId].MasteryScore));
        Assert.Equal(2, await reader.ReadAsync(s => s.TrainingAttempts[request.RequestId].Answers.Count));
    }
}
