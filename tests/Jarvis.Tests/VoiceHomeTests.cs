using System.Text;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class VoiceHomeTests
{
    private const string DeviceId = "home-voice-fixture";

    [Fact]
    public async Task Voice_home_turn_uses_the_latest_spoken_request_and_returns_only_verified_success_once()
    {
        var posts = 0;
        using var host = Fixture(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/services/light/turn_on", request.RequestUri!.AbsolutePath);
                Interlocked.Increment(ref posts);
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("light.kitchen", body);
                return Reply("[]");
            }
            Assert.Equal("/api/states/light.kitchen", request.RequestUri!.AbsolutePath);
            return Reply(posts == 0
                ? """{"entity_id":"light.kitchen","state":"off","attributes":{"friendly_name":"Cooking lights"}}"""
                : """{"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Cooking lights"}}""");
        });
        var grant = await ConfigureAsync(host.Services, preApproved: true);
        host.Services.GetRequiredService<IConfiguration>()["HomeAssistant:Aliases:kitchen light"] = "light.kitchen";
        using var owner = host.Client();
        var session = await LifeScenarioTests.Session(owner);
        var source = await SayAsync(host.Services, session.Id, "Hey, JARVIS, turn on the kitchen light.");
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var stale = await ToolAsync(host.Services, session.Id, source, Guid.NewGuid());
        Assert.False(stale.GetProperty("ok").GetBoolean());
        Assert.Equal(0, posts);
        var revokedSource = await SayAsync(host.Services, session.Id, "turn on the kitchen light");
        var checks = 0;
        var revoked = await ToolAsync(host.Services, session.Id, revokedSource, revokedSource,
            _ => Task.FromResult(Interlocked.Increment(ref checks) == 1));
        Assert.False(revoked.GetProperty("ok").GetBoolean());
        Assert.Equal(0, posts);
        source = await SayAsync(host.Services, session.Id, "Hey, JARVIS, turn on the kitchen light.");
        var success = await ToolAsync(host.Services, session.Id, source, source);
        var result = success.GetProperty("result");
        Assert.Equal("succeeded", result.GetProperty("status").GetString());
        Assert.Equal("succeeded", (await ToolAsync(host.Services, session.Id, source, source)).GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(1, posts);
        var id = result.GetProperty("commandId").GetGuid();
        var command = await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(id);
        Assert.Equal(InvocationOrigin.Voice, command!.Origin);
        Assert.Equal(grant, command.GrantId);
        Assert.Null(command.ConfirmedBy);
        Assert.Equal(DeviceReceiptKind.Succeeded, (await host.Services.GetRequiredService<IDeviceReceiptReader>().ListAsync(id))[1].Kind);
    }

    [Fact]
    public async Task Voice_home_limits_resolution_to_grants_and_requires_confirmation_when_grant_is_not_preapproved()
    {
        using var host = Fixture(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var entity = request.RequestUri!.Segments[^1];
            Assert.Contains(entity, new[] { "light.kitchen", "switch.hall" });
            return Task.FromResult(Reply(JsonSerializer.Serialize(new { entity_id = entity, state = "off", attributes = new { friendly_name = "Ceiling light" } })));
        });
        await ConfigureAsync(host.Services, preApproved: false, alsoHall: true);
        var config = host.Services.GetRequiredService<IConfiguration>();
        config["HomeAssistant:Aliases:kitchen light"] = "light.kitchen";
        config["HomeAssistant:Aliases:outside light"] = "light.outside";
        using var owner = host.Client();
        var session = await LifeScenarioTests.Session(owner);
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var ambiguous = await SayAsync(host.Services, session.Id, "turn on the ceiling light");
        var candidates = (await ToolAsync(host.Services, session.Id, ambiguous, ambiguous)).GetProperty("result");
        Assert.Equal("ambiguous", candidates.GetProperty("status").GetString());
        Assert.Equal(2, candidates.GetProperty("candidates").GetArrayLength());
        var denied = await SayAsync(host.Services, session.Id, "turn on the outside light");
        Assert.False((await ToolAsync(host.Services, session.Id, denied, denied)).GetProperty("ok").GetBoolean());
        var quoted = await SayAsync(host.Services, session.Id, "The message says turn on the kitchen light");
        Assert.False((await ToolAsync(host.Services, session.Id, quoted, quoted)).GetProperty("ok").GetBoolean());
        var allowed = await SayAsync(host.Services, session.Id, "turn the kitchen light on");
        var proposal = (await ToolAsync(host.Services, session.Id, allowed, allowed)).GetProperty("result");
        Assert.Equal("awaiting_confirmation", proposal.GetProperty("status").GetString());
        var command = await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(proposal.GetProperty("commandId").GetGuid());
        Assert.Equal(DeviceCommandStatus.AwaitingConfirmation, command!.Status);
    }

    [Fact]
    public async Task Voice_home_does_not_report_success_on_state_mismatch_and_paid_pause_denies_actions()
    {
        int posts = 0;
        using var host = Fixture(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; return Task.FromResult(Reply("[]")); }
            return Task.FromResult(Reply("""{"entity_id":"light.kitchen","state":"off","attributes":{"friendly_name":"Kitchen light"}}"""));
        });
        await ConfigureAsync(host.Services, preApproved: true);
        using var owner = host.Client();
        var session = await LifeScenarioTests.Session(owner);
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var first = await SayAsync(host.Services, session.Id, "turn on the kitchen light");
        Assert.False((await ToolAsync(host.Services, session.Id, first, first)).GetProperty("ok").GetBoolean());
        Assert.Equal(1, posts);
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        await runtime.UpdateAsync(s => { s.AgentControls = s.AgentControls with { PaidAiPaused = true }; return true; });
        var next = await SayAsync(host.Services, session.Id, "turn on the kitchen light");
        Assert.False((await ToolAsync(host.Services, session.Id, next, next)).GetProperty("ok").GetBoolean());
        Assert.Equal(1, posts);
    }

    private static ProviderFactory Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(send);

    private static HttpResponseMessage Reply(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<Guid> ConfigureAsync(IServiceProvider services, bool preApproved, bool alsoHall = false)
    {
        var (original, secret) = await DeviceCommandTests.SetupAsync(services, DeviceId);
        var config = services.GetRequiredService<IConfiguration>();
        config[HomeAssistantCommandAdapter.ExecutorDeviceId] = DeviceId;
        config[HomeAssistantCommandAdapter.ExecutorCredential] = secret;
        config["HomeAssistant:AllowedEntities:0"] = "light.kitchen";
        if (alsoHall) config["HomeAssistant:AllowedEntities:1"] = "switch.hall";
        if (!preApproved && !alsoHall) return original;
        var request = DeviceGrantTests.GrantRequest() with
        {
            PreApproved = preApproved,
            ConstraintsJson = alsoHall ? """{"allowedEntityIds":["light.kitchen","switch.hall"]}""" : """{"allowedEntityIds":["light.kitchen"]}"""
        };
        var issued = await services.GetRequiredService<DeviceGrantService>().GrantAsync(DeviceId, request,
            new("fixture-owner", [DeviceGrantService.ManageScope]));
        return issued.Id;
    }

    private static async Task<Guid> SayAsync(IServiceProvider services, Guid session, string words)
    {
        var id = Guid.NewGuid();
        await services.GetRequiredService<VoiceTranscriptService>().RecordAsync(session, id, ChatRoles.User, words, null, default);
        return id;
    }

    private static async Task<JsonElement> ToolAsync(IServiceProvider services, Guid session, Guid source, Guid latest,
        Func<CancellationToken, Task<bool>>? currentAudio = null)
    {
        var args = JsonSerializer.Serialize(new { action = "turn", utterance_id = source });
        var json = await services.GetRequiredService<CoreVoiceTools>().ExecuteAsync(CoreHomeVoiceTool.Name, args, session,
            new HashSet<Guid> { source }, default, latest, currentAudio ?? (_ => Task.FromResult(true)));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
