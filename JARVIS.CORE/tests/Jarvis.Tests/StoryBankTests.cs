using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class StoryBankTests
{
    private static StoryContent Draft(string title = "FX transaction module") =>
        new(title, "Alternative Bank", "FX service", PersonalOwnership: "I implemented timeout reconciliation.",
            FollowUpQuestions: ["How did you prevent duplicates?"], CvClaims: ["Built FX processing"]);

    [Fact]
    public async Task Story_lifecycle_preserves_fields_and_rejects_stale_edits_and_deletes()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateAuthorisedClient();
        var request = new CreateStoryRequest(Guid.NewGuid(), Draft());
        var created = await LifeScenarioTests.Post<Story>(client, "/api/stories", request);
        var retry = await LifeScenarioTests.Post<Story>(client, "/api/stories", request);
        Assert.Equal(created.Id, retry.Id);
        Assert.Equal(created.CreatedAt, retry.CreatedAt);
        Assert.Single((await client.GetFromJsonAsync<List<Story>>("/api/stories"))!);
        using var update = await client.PutAsJsonAsync($"/api/stories/{created.Id}", new UpdateStoryRequest(1, Draft("FX recovery")));
        update.EnsureSuccessStatusCode();
        var edited = (await update.Content.ReadFromJsonAsync<Story>())!;
        Assert.Equal(2, edited.Version);
        Assert.Equal(created.CreatedAt, edited.CreatedAt);
        Assert.Equal(request.Content.PersonalOwnership, edited.Content.PersonalOwnership);
        Assert.Equal(request.Content.FollowUpQuestions, edited.Content.FollowUpQuestions);
        Assert.Equal(request.Content.CvClaims, edited.Content.CvClaims);
        using var stale = await client.PutAsJsonAsync($"/api/stories/{created.Id}", new UpdateStoryRequest(1, Draft()));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var staleDelete = await client.DeleteAsync($"/api/stories/{created.Id}?expectedVersion=1");
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        using var delete = await client.DeleteAsync($"/api/stories/{created.Id}?expectedVersion=2");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var missing = await client.GetAsync($"/api/stories/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Concurrent_edits_allow_only_one_winner()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateAuthorisedClient();
        var story = await LifeScenarioTests.Post<Story>(client, "/api/stories", new CreateStoryRequest(Guid.NewGuid(), Draft()));
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            client.PutAsJsonAsync($"/api/stories/{story.Id}", new UpdateStoryRequest(1, Draft($"Revision {i}")))));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Invalid_content_and_reused_ids_do_not_change_saved_story()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateAuthorisedClient();
        var id = Guid.NewGuid();
        var story = await LifeScenarioTests.Post<Story>(client, "/api/stories", new CreateStoryRequest(id, Draft()));
        foreach (var invalid in new StoryContent?[] { null, Draft(" "), Draft() with { Problem = new string('x', 8001) },
            Draft() with { CvClaims = [null!] }, Draft() with { FollowUpQuestions = Enumerable.Repeat("Why?", 31).ToArray() } })
        {
            using var response = await client.PostAsJsonAsync("/api/stories", new CreateStoryRequest(Guid.NewGuid(), invalid!));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var conflict = await client.PostAsJsonAsync("/api/stories", new CreateStoryRequest(id, Draft("Different story")));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var saved = await client.GetFromJsonAsync<Story>($"/api/stories/{id}");
        Assert.Equal(story.Content.Title, saved!.Content.Title);
        Assert.Equal(1, saved.Version);
        using var anonymous = host.CreateClient();
        using var unauthorised = await anonymous.GetAsync("/api/stories");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorised.StatusCode);
    }

    [Fact]
    public void Old_runtime_documents_default_to_an_empty_story_bank()
    {
        var state = JsonSerializer.Deserialize<RuntimeState>("{\"Learning\":{},\"Devices\":{}}")!;
        Assert.Empty(state.Stories);
    }
}
