using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class HomeAssistantCommandAdapterTests
{
    private const string DeviceId = "fixture-home-executor";

    [Fact]
    public async Task Home_assistant_adapter_only_executes_approved_commands_and_receipts_require_observed_state()
    {
        var calls = new List<HttpMethod>();
        using var host = new ProviderFactory(async request =>
        {
            calls.Add(request.Method);
            Assert.Equal("homeassistant.test", request.RequestUri!.Host);
            Assert.Equal("fake-ha-token", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/services/light/turn_on", request.RequestUri.AbsolutePath);
                Assert.Equal("light.kitchen", (await request.Content!.ReadFromJsonAsync<JsonElement>()).GetProperty("entity_id").GetString());
                return Reply("[]");
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/states/light.kitchen", request.RequestUri.AbsolutePath);
            return Reply("""{"entity_id":"light.kitchen","state":"on"}""");
        });
        var grant = await PairAsync(host.Services);
        var service = host.Services.GetRequiredService<DeviceCommandService>();
        var executor = host.Services.GetRequiredService<HomeAssistantCommandAdapter>();
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var proposed = await service.ProposeAsync(DeviceId, DeviceCommandTests.Request(grant, DateTimeOffset.UtcNow));
        Assert.False(await executor.DispatchNextAsync(default));
        Assert.Empty(calls);
        await service.ConfirmAsync(proposed.Id, new(proposed.ArgumentHash, true), new("fixture-owner", [DeviceCommandService.ConfirmScope]));
        Assert.True(await executor.DispatchNextAsync(default));
        Assert.False(await executor.DispatchNextAsync(default));
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, calls);
        var command = await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(proposed.Id);
        Assert.Equal(DeviceCommandStatus.Succeeded, command!.Status);
        var receipts = await host.Services.GetRequiredService<IDeviceReceiptReader>().ListAsync(proposed.Id);
        Assert.Equal(new[] { DeviceReceiptKind.Acknowledged, DeviceReceiptKind.Succeeded }, receipts.Select(x => x.Kind));
        Assert.Contains("reports the entity is on", receipts[1].PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, ToolInvocationStatus.Succeeded, DeviceCommandStatus.Succeeded)]
    [InlineData(false, ToolInvocationStatus.Uncertain, DeviceCommandStatus.Failed)]
    public async Task Home_assistant_legacy_routes_keep_audit_and_never_repeat_an_action_when_using_the_adapter(
        bool matchesState, ToolInvocationStatus toolStatus, DeviceCommandStatus commandStatus)
    {
        var posts = 0;
        var reads = 0;
        using var host = new ProviderFactory(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref posts);
                return Task.FromResult(Reply("[]"));
            }
            Interlocked.Increment(ref reads);
            return Task.FromResult(Reply(matchesState ? """{"entity_id":"light.kitchen","state":"on"}""" :
                """{"entity_id":"light.kitchen","state":"off"}"""));
        });
        var grantId = await PairAsync(host.Services);
        using var owner = host.Client();
        var session = await LifeScenarioTests.Session(owner);
        var request = new HomeActionRequest(Guid.NewGuid(), session.Id, "light.kitchen", "turn_on");
        var proposed = await LifeScenarioTests.Post<AuditedTool>(owner, "/api/tools/home", request);
        Assert.Equal(ToolInvocationStatus.AwaitingConfirmation, proposed.Invocation.Status);
        Assert.Equal(0, posts);
        var path = $"/api/tools/{request.RequestId}/confirm";
        var finished = await LifeScenarioTests.Post<AuditedTool>(owner, path, new { approve = true });
        Assert.Equal(toolStatus, finished.Invocation.Status);
        Assert.Equal(finished, await LifeScenarioTests.Post<AuditedTool>(owner, path, new { approve = true }));
        Assert.Equal(1, posts);
        Assert.Equal(1, reads);
        var audit = Assert.Single((await owner.GetFromJsonAsync<AuditedTool[]>("/api/tools", LifeScenarioTests.Json))!);
        Assert.Equal(finished, audit);
        var command = await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(request.RequestId);
        Assert.Equal(grantId, command!.GrantId);
        Assert.Equal("fixture-owner", command.ConfirmedBy);
        Assert.Equal(commandStatus, command.Status);
        Assert.Equal(proposed.Invocation.ArgumentsJson, command.ArgumentsJson);
        var receipts = (await owner.GetFromJsonAsync<DeviceReceiptView[]>($"/api/device-commands/{request.RequestId}/receipts", LifeScenarioTests.Json))!;
        Assert.Equal(matchesState ? DeviceReceiptKind.Succeeded : DeviceReceiptKind.Failed, receipts[1].Kind);
    }

    [Fact]
    public async Task Home_assistant_configured_legacy_path_requires_a_valid_executor_and_matching_grant()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call was expected."));
        var grantId = await PairAsync(host.Services);
        using var owner = host.Client();
        var session = await LifeScenarioTests.Session(owner);
        var grants = host.Services.GetRequiredService<IDeviceGrantStore>();
        Assert.True(await grants.RevokeAsync(grantId, DateTimeOffset.UtcNow));
        var request = new HomeActionRequest(Guid.NewGuid(), session.Id, "light.kitchen", "turn_on");
        await LifeScenarioTests.Post<AuditedTool>(owner, "/api/tools/home", request);
        var refused = await LifeScenarioTests.Post<AuditedTool>(owner, $"/api/tools/{request.RequestId}/confirm", new { approve = true });
        Assert.Equal(ToolInvocationStatus.Failed, refused.Invocation.Status);
        Assert.Contains("active owner grant", refused.Result!, StringComparison.Ordinal);
        Assert.Null(await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(request.RequestId));

        host.Services.GetRequiredService<IConfiguration>()[HomeAssistantCommandAdapter.ExecutorCredential] = null;
        var second = request with { RequestId = Guid.NewGuid() };
        await LifeScenarioTests.Post<AuditedTool>(owner, "/api/tools/home", second);
        var misconfigured = await LifeScenarioTests.Post<AuditedTool>(owner, $"/api/tools/{second.RequestId}/confirm", new { approve = true });
        Assert.Equal(ToolInvocationStatus.Failed, misconfigured.Invocation.Status);
        Assert.Contains("paired credential", misconfigured.Result!, StringComparison.Ordinal);
    }

    private static async Task<Guid> PairAsync(IServiceProvider services)
    {
        var (grant, secret) = await DeviceCommandTests.SetupAsync(services, DeviceId);
        var configuration = services.GetRequiredService<IConfiguration>();
        configuration[HomeAssistantCommandAdapter.ExecutorDeviceId] = DeviceId;
        configuration[HomeAssistantCommandAdapter.ExecutorCredential] = secret;
        configuration["HomeAssistant:AllowedEntities:0"] = "light.kitchen";
        return grant;
    }

    private static HttpResponseMessage Reply(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
