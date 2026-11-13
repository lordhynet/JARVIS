using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jarvis.Tests;

public sealed class ScenarioClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan time) => _now += time;
}

public sealed class ScenarioHome : IHomeControl
{
    public int Calls;
    public bool LoseResponse;
    public async Task<string> ExecuteAsync(string entityId, string action, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        await Task.Delay(20, ct);
        if (LoseResponse) throw new HttpRequestException("Simulated lost response");
        return "Simulated device accepted action";
    }
}

public sealed class ScenarioConversation : IConversationProvider
{
    public int Calls;
    public Task<string> ReplyAsync(ConversationSession session, string text, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult("Simulation: explain your approach to " + text);
    }
}

public sealed class ScenarioFactory : WebApplicationFactory<Program>
{
    public ScenarioClock Clock { get; } = new();
    public ScenarioHome Home { get; } = new();
    public ScenarioConversation Conversation { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jarvis:ApiKey", JarvisApiFactory.TestKey);
        builder.UseSetting("Jarvis:StorageProvider", "InMemory");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(Clock);
            s.RemoveAll<IHomeControl>(); s.AddSingleton<IHomeControl>(Home);
            s.RemoveAll<IConversationProvider>(); s.AddSingleton<IConversationProvider>(Conversation);
        });
    }
    public HttpClient Client()
    {
        var client = CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); return client;
    }
}

public sealed class LifeScenarioTests
{
    [Fact]
    public async Task Interrupted_home_action_becomes_uncertain_without_reexecution()
    {
        using var host = new ScenarioFactory(); using var client = host.Client(); var session = await Session(client);
        var tool = await Post<AuditedTool>(client, "/api/tools/home", new HomeActionRequest(Guid.NewGuid(), session.Id, "light.desk", "turn_on"));
        await host.Services.GetRequiredService<IRuntimeStore>().UpdateAsync(s =>
        {
            s.Tools[tool.Invocation.Id] = tool with { Invocation = tool.Invocation with { Status = ToolInvocationStatus.Running } };
            return true;
        });
        host.Clock.Advance(TimeSpan.FromMinutes(8));
        await host.Services.GetRequiredService<ToolService>().RecoverInterruptedAsync(default);
        var recovered = await Post<AuditedTool>(client, $"/api/tools/{tool.Invocation.Id}/confirm", new { approve = true });
        Assert.Equal(ToolInvocationStatus.Uncertain, recovered.Invocation.Status); Assert.Equal(0, host.Home.Calls);
    }

    [Fact]
    public async Task Missing_server_api_key_fails_closed()
    {
        using var original = new ScenarioFactory(); using var host = original.WithWebHostBuilder(b => b.UseSetting("Jarvis:ApiKey", ""));
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    internal static async Task<T> Post<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body, Json);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode}: {content}");
        return JsonSerializer.Deserialize<T>(content, Json)!;
    }
    internal static Task<ConversationSession> Session(HttpClient client) => Post<ConversationSession>(client, "/api/sessions", new CreateSessionRequest(ActivityType.LeetCode, "Morning practice", null));
    internal static async Task Register(HttpClient client, string id, bool verify = true)
    {
        await Post<RegisteredDevice>(client, "/api/devices", new RegisterDeviceRequest(id, id, DeviceKind.WindowsAgent, new(true, true, true, false, [])));
        if (verify) await Post<RegisteredDevice>(client, $"/api/devices/{id}/verify", new { });
    }

    [Fact]
    public async Task Morning_practice_moves_from_laptop_to_phone_only_after_source_release()
    {
        using var host = new ScenarioFactory(); using var client = host.Client();
        await Register(client, "laptop"); await Register(client, "phone");
        var s = await Session(client); var root = $"/api/sessions/{s.Id}";
        await Post<ConversationSession>(client, root + "/claim", new { deviceId = "laptop" });
        var start = await Post<ConversationSession>(client, root + "/handoff", new StartHandoffRequest("laptop", "phone", "Working on two pointers"));
        Assert.Equal("laptop", start.ActiveDeviceId); Assert.Single(start.Checkpoints);
        var handoffId = start.Handoff!.Id;
        var premature = await client.PostAsJsonAsync(root + "/handoff/complete", new HandoffActionRequest(handoffId, "phone"), Json);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        var ready = await Post<ConversationSession>(client, root + "/handoff/ready", new HandoffActionRequest(handoffId, "phone"));
        Assert.Equal("laptop", ready.ActiveDeviceId);
        var wrongDevice = await client.PostAsJsonAsync(root + "/handoff/release", new HandoffActionRequest(handoffId, "phone"), Json);
        Assert.Equal(HttpStatusCode.Forbidden, wrongDevice.StatusCode);
        var released = await Post<ConversationSession>(client, root + "/handoff/release", new HandoffActionRequest(handoffId, "laptop"));
        Assert.Null(released.ActiveDeviceId);
        var complete = await Post<ConversationSession>(client, root + "/handoff/complete", new HandoffActionRequest(handoffId, "phone"));
        Assert.Equal("phone", complete.ActiveDeviceId); Assert.Equal(HandoffState.Completed, complete.Handoff!.State);
        var retry = await Post<ConversationSession>(client, root + "/handoff/complete", new HandoffActionRequest(handoffId, "phone"));
        Assert.Equal("phone", retry.ActiveDeviceId); Assert.Single(retry.Checkpoints);
    }

    [Fact]
    public async Task Phone_drops_out_during_handoff_timeout_preserves_checkpoint_and_allows_reclaim()
    {
        using var host = new ScenarioFactory(); using var client = host.Client();
        await Register(client, "laptop"); await Register(client, "phone");
        var s = await Session(client); var root = $"/api/sessions/{s.Id}";
        await Post<ConversationSession>(client, root + "/claim", new { deviceId = "laptop" });
        var transfer = await Post<ConversationSession>(client, root + "/handoff", new StartHandoffRequest("laptop", "phone", "Do not lose this work"));
        await Post<ConversationSession>(client, root + "/handoff/ready", new HandoffActionRequest(transfer.Handoff!.Id, "phone"));
        await Post<ConversationSession>(client, root + "/handoff/release", new HandoffActionRequest(transfer.Handoff.Id, "laptop"));
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        await host.Services.GetRequiredService<SessionLifecycleService>().ExpireAsync(default);
        var timedOut = await client.GetFromJsonAsync<ConversationSession>(root, Json);
        Assert.Equal(HandoffState.TimedOut, timedOut!.Handoff!.State); Assert.Null(timedOut.ActiveDeviceId);
        var recovered = await Post<ConversationSession>(client, root + "/claim", new { deviceId = "laptop" });
        Assert.Equal("laptop", recovered.ActiveDeviceId); Assert.Equal("Do not lose this work", Assert.Single(recovered.Checkpoints).Summary);
    }

    [Fact]
    public async Task Unverified_and_offline_devices_cannot_claim_voice_ownership()
    {
        using var host = new ScenarioFactory(); using var client = host.Client();
        await Register(client, "laptop", false); var s = await Session(client);
        var unverified = await client.PostAsJsonAsync($"/api/sessions/{s.Id}/claim", new { deviceId = "laptop" }, Json);
        Assert.Equal(HttpStatusCode.Conflict, unverified.StatusCode);
        await Post<RegisteredDevice>(client, "/api/devices/laptop/verify", new { });
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var offline = await client.PostAsJsonAsync($"/api/sessions/{s.Id}/claim", new { deviceId = "laptop" }, Json);
        Assert.Equal(HttpStatusCode.Conflict, offline.StatusCode);
        await Post<RegisteredDevice>(client, "/api/devices/laptop/heartbeat", new { });
        Assert.Equal("laptop", (await Post<ConversationSession>(client, $"/api/sessions/{s.Id}/claim", new { deviceId = "laptop" })).ActiveDeviceId);
    }

    [Fact]
    public async Task Two_devices_racing_to_claim_have_one_winner()
    {
        using var host = new ScenarioFactory(); using var client = host.Client();
        await Register(client, "one"); await Register(client, "two"); var s = await Session(client);
        var results = await Task.WhenAll(new[] { "one", "two" }.Select(deviceId => client.PostAsJsonAsync($"/api/sessions/{s.Id}/claim", new { deviceId }, Json)));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Pause_and_end_prevent_chat_and_ended_sessions_cannot_resume()
    {
        using var host = new ScenarioFactory(); using var client = host.Client(); var s = await Session(client); var root = $"/api/sessions/{s.Id}";
        await Post<ConversationSession>(client, root + "/status", new SessionTransitionRequest(SessionStatus.Paused));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/messages", new ChatRequest(Guid.NewGuid(), "Hello"), Json)).StatusCode);
        await Post<ConversationSession>(client, root + "/status", new SessionTransitionRequest(SessionStatus.Active));
        await Post<ConversationSession>(client, root + "/status", new SessionTransitionRequest(SessionStatus.Ended));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/status", new SessionTransitionRequest(SessionStatus.Active), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/checkpoints", new AddCheckpointRequest("Late", null), Json)).StatusCode);
        Assert.Equal(0, host.Conversation.Calls);
    }

    [Fact]
    public async Task Retried_chat_is_returned_without_duplicate_model_call_or_transcript()
    {
        using var host = new ScenarioFactory(); using var client = host.Client(); var s = await Session(client);
        var request = new ChatRequest(Guid.NewGuid(), "Two Sum"); var root = $"/api/sessions/{s.Id}/messages";
        await Post<ConversationSession>(client, root, request);
        var retried = await Post<ConversationSession>(client, root, request);
        Assert.Equal(2, retried.Messages!.Count); Assert.Equal(1, host.Conversation.Calls);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root, request with { Text = "Different" }, Json)).StatusCode);
    }

    [Fact]
    public async Task French_review_retry_does_not_double_schedule_and_again_is_due_in_ten_minutes()
    {
        using var host = new ScenarioFactory(); using var client = host.Client();
        var card = await Post<LearningItem>(client, "/api/learning", new CreateLearningRequest(LearningDomain.French, "Hello", "Bonjour"));
        var request = new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Again, 4);
        var first = await Post<LearningItem>(client, $"/api/learning/{card.Id}/reviews", request);
        var retry = await Post<LearningItem>(client, $"/api/learning/{card.Id}/reviews", request);
        Assert.Equal(first, retry); Assert.Equal(host.Clock.GetUtcNow().AddMinutes(10), first.DueAt);
        Assert.Empty((await client.GetFromJsonAsync<List<LearningItem>>("/api/learning?due=true", Json))!);
        host.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Single((await client.GetFromJsonAsync<List<LearningItem>>("/api/learning?due=true", Json))!);
        Assert.Single((await client.GetFromJsonAsync<List<ReviewEvent>>($"/api/learning/{card.Id}/reviews", Json))!);
    }

    [Theory]
    [InlineData(ReviewRating.Hard, 1)] [InlineData(ReviewRating.Good, 1)] [InlineData(ReviewRating.Easy, 4)]
    public async Task Review_ratings_schedule_expected_first_interval(ReviewRating rating, int days)
    {
        using var host = new ScenarioFactory(); using var client = host.Client();
        var card = await Post<LearningItem>(client, "/api/learning", new CreateLearningRequest(LearningDomain.SystemDesign, "Cache tradeoff?", "Staleness vs latency"));
        var reviewed = await Post<LearningItem>(client, $"/api/learning/{card.Id}/reviews", new ReviewLearningRequest(Guid.NewGuid(), rating, 1));
        Assert.Equal(host.Clock.GetUtcNow().AddDays(days), reviewed.DueAt);
    }

    [Fact]
    public async Task Desk_light_requires_confirmation_and_double_click_executes_only_once()
    {
        using var host = new ScenarioFactory(); using var client = host.Client(); var s = await Session(client);
        var tool = await Post<AuditedTool>(client, "/api/tools/home", new HomeActionRequest(Guid.NewGuid(), s.Id, "light.desk", "turn_on"));
        Assert.Equal(0, host.Home.Calls); Assert.Equal(ToolInvocationStatus.AwaitingConfirmation, tool.Invocation.Status);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync($"/api/tools/{tool.Invocation.Id}/confirm", new { approve = true }, Json)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode)); Assert.Equal(1, host.Home.Calls);
        var audit = await client.GetFromJsonAsync<List<AuditedTool>>("/api/tools", Json);
        Assert.Equal(ToolInvocationStatus.Succeeded, Assert.Single(audit!).Invocation.Status);
    }

    [Fact]
    public async Task External_content_and_expired_confirmations_cannot_operate_home()
    {
        using var host = new ScenarioFactory(); using var client = host.Client(); var s = await Session(client);
        var injected = await Post<AuditedTool>(client, "/api/tools/home", new HomeActionRequest(Guid.NewGuid(), s.Id, "light.desk", "turn_on", true));
        var rejected = await Post<AuditedTool>(client, $"/api/tools/{injected.Invocation.Id}/confirm", new { approve = true });
        Assert.Equal(ToolInvocationStatus.Rejected, rejected.Invocation.Status);
        var stale = await Post<AuditedTool>(client, "/api/tools/home", new HomeActionRequest(Guid.NewGuid(), s.Id, "light.desk", "turn_off"));
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await Post<AuditedTool>(client, $"/api/tools/{stale.Invocation.Id}/confirm", new { approve = true });
        Assert.Equal(ToolInvocationStatus.Rejected, expired.Invocation.Status); Assert.Equal(0, host.Home.Calls);
    }

    [Fact]
    public async Task Lost_home_response_is_uncertain_and_never_retried_automatically()
    {
        using var host = new ScenarioFactory(); host.Home.LoseResponse = true; using var client = host.Client(); var s = await Session(client);
        var tool = await Post<AuditedTool>(client, "/api/tools/home", new HomeActionRequest(Guid.NewGuid(), s.Id, "light.desk", "turn_off"));
        var uncertain = await Post<AuditedTool>(client, $"/api/tools/{tool.Invocation.Id}/confirm", new { approve = true });
        Assert.Equal(ToolInvocationStatus.Uncertain, uncertain.Invocation.Status);
        await Post<AuditedTool>(client, $"/api/tools/{tool.Invocation.Id}/confirm", new { approve = true }); Assert.Equal(1, host.Home.Calls);
    }

    [Theory]
    [InlineData("lock.front_door", "turn_on")] [InlineData("light.desk", "toggle")] [InlineData("light../bad", "turn_off")]
    public async Task Unsupported_home_actions_are_rejected(string entity, string action)
    {
        using var host = new ScenarioFactory(); using var client = host.Client(); var s = await Session(client);
        var response = await client.PostAsJsonAsync("/api/tools/home", new HomeActionRequest(Guid.NewGuid(), s.Id, entity, action), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, host.Home.Calls);
    }

    [Theory]
    [InlineData("/api/devices")] [InlineData("/api/learning")] [InlineData("/api/tools")] [InlineData("/api/integrations")] [InlineData("/api/briefing")]
    public async Task New_features_require_authentication(string path)
    {
        using var host = new ScenarioFactory(); using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path + "?access_token=" + JarvisApiFactory.TestKey)).StatusCode);
    }
}
