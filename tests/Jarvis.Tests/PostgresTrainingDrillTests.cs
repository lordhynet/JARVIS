using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Guided_drills_fence_competing_hosts_and_resume_after_restart()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var x = a.AuthorisedClient(); using var y = b.AuthorisedClient(); var session = await CreateSession(x);
        var request = new StartTrainingDrillRequest(Guid.NewGuid(), session.Id, TrainingMode.CodeReview);
        var started = await Task.WhenAll(LifeScenarioTests.Post<TrainingDrillView>(x, "/api/training/drills", request), LifeScenarioTests.Post<TrainingDrillView>(y, "/api/training/drills", request));
        Assert.Equal(started[0].Drill.Steps, started[1].Drill.Steps);
        var step = started[0].Drill.Steps[0];
        var answer = new SubmitTrainingAnswerRequest(Guid.NewGuid(), session.Id, step.QuestionId, 1, TrainingMode.CodeReview, "This call blocks while I/O is pending.", DrillId: request.RequestId, StepIndex: 0);
        var responses = await Task.WhenAll(x.PostAsJsonAsync("/api/training/attempts", answer, LifeScenarioTests.Json),
            y.PostAsJsonAsync("/api/training/attempts", answer with { RequestId = Guid.NewGuid() }, LifeScenarioTests.Json));
        try { Assert.Single(responses, r => r.IsSuccessStatusCode); Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict); }
        finally { foreach (var response in responses) response.Dispose(); }
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var client = restarted.AuthorisedClient();
        var restored = (await client.GetFromJsonAsync<TrainingDrillView>($"/api/training/drills/{request.RequestId}", LifeScenarioTests.Json))!;
        Assert.Single(restored.Drill.AttemptIds); Assert.Single(restored.Answers); Assert.Equal("Active", restored.Drill.Status);
        Assert.NotEmpty(restored.Drill.Steps[1].CodeSnippet!);
        var next = restored.Drill.Steps[1];
        await LifeScenarioTests.Post<TrainingAttempt>(client, "/api/training/attempts", new SubmitTrainingAnswerRequest(Guid.NewGuid(), session.Id, next.QuestionId, 1,
            TrainingMode.CodeReview, "Use asynchronous I/O and test cancellation and concurrency.", DrillId: request.RequestId, StepIndex: 1));
        var complete = (await client.GetFromJsonAsync<TrainingDrillView>($"/api/training/drills/{request.RequestId}", LifeScenarioTests.Json))!;
        Assert.Equal("Completed", complete.Drill.Status); Assert.Equal(2, complete.Answers.Count);
        var history = (await client.GetFromJsonAsync<List<TrainingDrillView>>($"/api/training/sessions/{session.Id}/drills", LifeScenarioTests.Json))!;
        Assert.Empty(Assert.Single(history).Answers); Assert.Equal(2, history[0].Drill.AttemptIds.Count);
    }
}
