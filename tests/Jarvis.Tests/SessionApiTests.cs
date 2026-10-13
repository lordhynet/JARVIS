using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Contracts;

namespace Jarvis.Tests;

public sealed class SessionApiTests(JarvisApiFactory factory) : IClassFixture<JarvisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Health_is_public_and_reports_ok()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Sessions_reject_requests_without_a_key()
    {
        var response = await factory.CreateClient().GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sessions_reject_a_wrong_key()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", "wrong");

        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_then_read_returns_the_same_session()
    {
        var client = factory.CreateAuthorisedClient();

        var create = await client.PostAsJsonAsync("/api/sessions",
            new CreateSessionRequest(ActivityType.MockInterview, null, "dell-laptop"), Json);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ConversationSession>(Json);
        Assert.NotNull(created);
        Assert.Equal("Mock interview", created.Title);
        Assert.Equal(SessionStatus.Active, created.Status);
        Assert.Equal("dell-laptop", created.ActiveDeviceId);
        Assert.Equal($"/api/sessions/{created.Id}", create.Headers.Location?.OriginalString);

        var read = await client.GetFromJsonAsync<ConversationSession>($"/api/sessions/{created.Id}", Json);
        Assert.NotNull(read);
        Assert.Equal(created.Id, read.Id);
        Assert.Equal(ActivityType.MockInterview, read.Activity);
    }

    [Fact]
    public async Task Reading_an_unknown_session_returns_404()
    {
        var response = await factory.CreateAuthorisedClient().GetAsync($"/api/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Checkpoints_are_numbered_in_order_and_update_current_task()
    {
        var client = factory.CreateAuthorisedClient();
        var session = await CreateAsync(client, ActivityType.LeetCode);

        await client.PostAsJsonAsync($"/api/sessions/{session.Id}/checkpoints",
            new AddCheckpointRequest("Explained two-pointer approach", "Two Sum II"), Json);
        var second = await client.PostAsJsonAsync($"/api/sessions/{session.Id}/checkpoints",
            new AddCheckpointRequest("Discussed O(n) complexity", null), Json);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var updated = await second.Content.ReadFromJsonAsync<ConversationSession>(Json);
        Assert.NotNull(updated);
        Assert.Equal([1, 2], updated.Checkpoints.Select(c => c.Sequence));
        Assert.Equal("Two Sum II", updated.CurrentTask);
    }

    [Fact]
    public async Task Checkpoint_without_summary_is_rejected()
    {
        var client = factory.CreateAuthorisedClient();
        var session = await CreateAsync(client, ActivityType.General);

        var response = await client.PostAsJsonAsync($"/api/sessions/{session.Id}/checkpoints",
            new AddCheckpointRequest("   ", null), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Checkpoint_on_unknown_session_returns_404()
    {
        var response = await factory.CreateAuthorisedClient().PostAsJsonAsync(
            $"/api/sessions/{Guid.NewGuid()}/checkpoints", new AddCheckpointRequest("x", null), Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Title_longer_than_limit_is_rejected()
    {
        var response = await factory.CreateAuthorisedClient().PostAsJsonAsync("/api/sessions",
            new CreateSessionRequest(ActivityType.General, new string('a', 121), null), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<ConversationSession> CreateAsync(HttpClient client, ActivityType activity)
    {
        var response = await client.PostAsJsonAsync("/api/sessions", new CreateSessionRequest(activity, null, null), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConversationSession>(Json))!;
    }
}
