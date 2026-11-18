using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class SensitiveVoiceHomeTests
{
    private const string Executor = "sensitive-home-fixture";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sensitive_voice_lock_stays_a_proposal_until_owner_confirms_its_exact_hash(bool locked)
    {
        var posts = 0;
        using var host = new ProviderFactory(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/services/lock/unlock", request.RequestUri!.AbsolutePath);
                posts++;
                return Task.FromResult(Reply("[]"));
            }
            return Task.FromResult(Reply(State("lock.front", posts == 0 ? "locked" : "unlocked",
                """{"friendly_name":"Front door"}""")));
        });
        var (_, secret) = await ConfigureAsync(host.Services, BuiltInDeviceCapabilities.HomeAssistantLockSet, "lock.front", "unlock", locked);
        using var owner = host.Client();
        var session = await LifeScenarioTests.Session(owner);
        if (locked) await host.Services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => {
            s.AudioLease = new(session.Id, "locked-room", 1, DateTimeOffset.UtcNow.AddMinutes(1), Locked: true); return true;
        });
        var source = await VoiceHomeTests.SayAsync(host.Services, session.Id, "unlock the front door");
        Guid id;
        using (CoreInvocationContext.Enter(InvocationOrigin.Voice))
        {
            var result = await VoiceHomeTests.ToolAsync(host.Services, session.Id, source, source, action: "lock");
            Assert.Equal("awaiting_confirmation", result.GetProperty("result").GetProperty("status").GetString());
            id = result.GetProperty("result").GetProperty("commandId").GetGuid();
        }
        var executor = host.Services.GetRequiredService<HomeAssistantCommandAdapter>();
        Assert.False(await executor.DispatchNextAsync(default));
        Assert.Equal(0, posts);

        using var companion = host.CreateClient();
        companion.DefaultRequestHeaders.Add("X-Jarvis-Key", secret);
        using var denied = await companion.GetAsync("/api/device-commands");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var queue = await owner.GetFromJsonAsync<DeviceCommandView[]>("/api/device-commands", LifeScenarioTests.Json);
        var pending = Assert.Single(queue!);
        Assert.Equal(id, pending.Id);
        Assert.Equal(DeviceCommandStatus.AwaitingConfirmation, pending.Status);
        Assert.Equal("""{"entityId":"lock.front","action":"unlock"}""", pending.ArgumentsJson);
        using var wrong = await owner.PostAsJsonAsync($"/api/device-commands/{id}/confirm", new ConfirmDeviceCommandRequest("changed", true));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        await ApproveAsync(owner, pending);

        using (CoreInvocationContext.Enter(InvocationOrigin.Voice))
        {
            var repeated = await VoiceHomeTests.ToolAsync(host.Services, session.Id, source, source, action: "lock");
            Assert.Equal("unverified", repeated.GetProperty("result").GetProperty("status").GetString());
        }
        Assert.Equal(0, posts);
        Assert.True(await executor.DispatchNextAsync(default));
        Assert.False(await executor.DispatchNextAsync(default));
        Assert.Equal(1, posts);
        await AssertOutcomeAsync(host.Services, id, DeviceCommandStatus.Succeeded, DeviceReceiptKind.Succeeded);
    }

    [Fact]
    public async Task Sensitive_cover_confirms_position_after_movement_and_posts_only_once()
    {
        var posts = 0;
        var readbacks = 0;
        using var host = new ProviderFactory(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/services/cover/set_cover_position", request.RequestUri!.AbsolutePath);
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(40, body.GetProperty("position").GetInt32());
                posts++;
                return Reply("[]");
            }
            if (posts == 0) return Reply(State("cover.blind", "closed", """{"current_position":0,"friendly_name":"Bedroom blind"}"""));
            readbacks++;
            return Reply(readbacks == 1 ? State("cover.blind", "opening", """{"current_position":40}""") :
                State("cover.blind", "open", """{"current_position":40}"""));
        });
        await ConfigureAsync(host.Services, BuiltInDeviceCapabilities.HomeAssistantCoverSet, "cover.blind", "set_cover_position");
        using var owner = host.Client();
        var pending = await VoiceProposalAsync(host, owner, "set the bedroom blind position to 40 percent", "cover");
        Assert.Equal("""{"entityId":"cover.blind","action":"set_cover_position","position":40}""", pending.ArgumentsJson);
        Assert.Equal(0, posts);
        await ApproveAsync(owner, pending);
        Assert.True(await host.Services.GetRequiredService<HomeAssistantCommandAdapter>().DispatchNextAsync(default));
        Assert.Equal(1, posts);
        Assert.Equal(2, readbacks);
        await AssertOutcomeAsync(host.Services, pending.Id, DeviceCommandStatus.Succeeded, DeviceReceiptKind.Succeeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sensitive_alarm_keeps_configured_code_out_of_command_history_and_requires_observed_mode(bool confirms)
    {
        const string fixtureCode = "fixture-protected-code";
        var posts = 0;
        using var host = new ProviderFactory(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/services/alarm_control_panel/alarm_arm_away", request.RequestUri!.AbsolutePath);
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(fixtureCode, body.GetProperty("code").GetString());
                posts++;
                return Reply("[]");
            }
            return Reply(State("alarm_control_panel.house", posts == 0 ? "disarmed" : confirms ? "armed_away" : "unavailable",
                """{"friendly_name":"House alarm"}"""));
        });
        await ConfigureAsync(host.Services, BuiltInDeviceCapabilities.HomeAssistantAlarmSet, "alarm_control_panel.house", "alarm_arm_away");
        host.Services.GetRequiredService<IConfiguration>()["HomeAssistant:ActionCodes:alarm_control_panel.house"] = fixtureCode;
        using var owner = host.Client();
        var pending = await VoiceProposalAsync(host, owner, "arm the house alarm in away mode", "alarm");
        Assert.DoesNotContain(fixtureCode, pending.ArgumentsJson);
        Assert.Equal(0, posts);
        await ApproveAsync(owner, pending);
        var work = host.Services.GetRequiredService<HomeAssistantCommandAdapter>();
        if (confirms) Assert.True(await work.DispatchNextAsync(default));
        else Assert.Equal(502, (await Assert.ThrowsAsync<DomainException>(() => work.DispatchNextAsync(default))).StatusCode);
        Assert.Equal(1, posts);
        await AssertOutcomeAsync(host.Services, pending.Id, confirms ? DeviceCommandStatus.Succeeded : DeviceCommandStatus.Failed,
            confirms ? DeviceReceiptKind.Succeeded : DeviceReceiptKind.Failed);
        Assert.All(await host.Services.GetRequiredService<IDeviceReceiptReader>().ListAsync(pending.Id),
            receipt => Assert.DoesNotContain(fixtureCode, receipt.PayloadJson));
    }

    private static async Task<(Guid Id, string Secret)> ConfigureAsync(IServiceProvider services, string capability, string entity, string action, bool locked = false)
    {
        var (_, secret) = await DeviceCommandTests.SetupAsync(services, Executor);
        var config = services.GetRequiredService<IConfiguration>();
        config[HomeAssistantCommandAdapter.ExecutorDeviceId] = Executor;
        config[HomeAssistantCommandAdapter.ExecutorCredential] = secret;
        config["HomeAssistant:AllowedEntities:0"] = entity;
        var grants = services.GetRequiredService<DeviceGrantService>();
        var grant = DeviceGrantTests.GrantRequest() with
        {
            CapabilityId = capability,
            AllowWhenLocked = locked,
            ConstraintsJson = JsonSerializer.Serialize(new { allowedEntityIds = new[] { entity }, allowedActions = new[] { action } })
        };
        var actor = new BudgetActor("fixture-owner", [DeviceGrantService.ManageScope]);
        var error = await Assert.ThrowsAsync<DomainException>(() => grants.GrantAsync(Executor, grant with { PreApproved = true }, actor));
        Assert.Equal(400, error.StatusCode);
        return ((await grants.GrantAsync(Executor, grant, actor)).Id, secret);
    }

    private static async Task<DeviceCommandView> VoiceProposalAsync(ProviderFactory host, HttpClient owner, string speech, string action)
    {
        var session = await LifeScenarioTests.Session(owner);
        var source = await VoiceHomeTests.SayAsync(host.Services, session.Id, speech);
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var result = await VoiceHomeTests.ToolAsync(host.Services, session.Id, source, source, action: action);
        Assert.Equal("awaiting_confirmation", result.GetProperty("result").GetProperty("status").GetString());
        var id = result.GetProperty("result").GetProperty("commandId").GetGuid();
        return (await owner.GetFromJsonAsync<DeviceCommandView>($"/api/device-commands/{id}", LifeScenarioTests.Json))!;
    }

    private static async Task ApproveAsync(HttpClient owner, DeviceCommandView command)
    {
        using var confirmed = await owner.PostAsJsonAsync($"/api/device-commands/{command.Id}/confirm",
            new ConfirmDeviceCommandRequest(command.ArgumentHash, true));
        confirmed.EnsureSuccessStatusCode();
        Assert.Equal(DeviceCommandStatus.Pending, (await confirmed.Content.ReadFromJsonAsync<DeviceCommandView>(LifeScenarioTests.Json))!.Status);
    }

    private static async Task AssertOutcomeAsync(IServiceProvider services, Guid id, DeviceCommandStatus status, DeviceReceiptKind kind)
    {
        Assert.Equal(status, (await services.GetRequiredService<IDeviceCommandReader>().GetAsync(id))!.Status);
        var receipt = Assert.Single(await services.GetRequiredService<IDeviceReceiptReader>().ListAsync(id), x => x.Kind != DeviceReceiptKind.Acknowledged);
        Assert.Equal(kind, receipt.Kind);
        Assert.False(receipt.Late);
    }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string State(string entity, string state, string attributes) =>
        $$"""{"entity_id":"{{entity}}","state":"{{state}}","attributes":{{attributes}}}""";
}
