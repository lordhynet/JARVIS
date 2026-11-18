using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jarvis.Tests;

public sealed class AgentActionTests
{
    private static readonly BudgetActor Owner = new("fixture-owner", [AgentToolGateway.ManageScope]);

    [Fact]
    public async Task Real_planner_output_enters_gateway_and_creates_a_governed_device_proposal()
    {
        Guid grantId = default, deviceGrantId = default; var httpCalls = 0;
        using var root = new ProviderFactory(_ => {
            httpCalls++;
            var text = JsonSerializer.Serialize(new { schemaVersion = 1, summary = "Propose the requested kitchen light action", drills = Array.Empty<string>(), sourceIds = Array.Empty<Guid>(),
                actions = new[] { new { grantId, deviceGrantId, arguments = JsonSerializer.Deserialize<JsonElement>(DeviceCommandTests.Arguments) } } });
            var json = JsonSerializer.Serialize(new { status = "completed", usage = new { input_tokens = 10, output_tokens = 10 }, output = new[] {
                new { type = "message", content = new[] { new { type = "output_text", text } } }
            } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        });
        using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        (deviceGrantId, _) = await DeviceCommandTests.SetupAsync(host.Services, "planner-action-target", true);
        var runtime = host.Services.GetRequiredService<AgentRuntime>(); var run = await runtime.StartAsync(new(Guid.NewGuid(), "personal-plan", true, "Turn on the kitchen light"), default);
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) grantId = (await host.Services.GetRequiredService<AgentToolGateway>().GrantAsync(Guid.NewGuid(), run.Run.Id,
            BuiltInDeviceCapabilities.HomeAssistantTurn, 0, 4, true, Owner)).Id;
        var task = (await runtime.ClaimAsync(default))!;
        await host.Services.GetRequiredService<IAgentExecutor>().ExecuteAsync(task, default);
        var action = Assert.Single(await host.Services.GetRequiredService<AgentActionService>().ListAsync());
        Assert.Equal("AwaitingReceipt", action.Status); Assert.Equal(task.Id, action.Proposal.ParentTaskId); Assert.Equal(1, httpCalls);
        var store = host.Services.GetRequiredService<IRuntimeStore>(); Assert.Single(await store.ReadAsync(s => s.UsageReservations));
        Assert.Equal(2, await store.ReadAsync(s => s.AgentToolInvocations.Count));
    }

    [Fact]
    public async Task Proposal_requires_exact_approval_then_waits_for_a_signed_success_receipt()
    {
        using var f = await Fixture.Create();
        var pending = await f.Propose();
        Assert.Equal("AwaitingApproval", pending.Status); Assert.Null(await f.Commands.GetAsync(pending.Proposal.Id));
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) {
            await Assert.ThrowsAsync<DomainException>(() => f.Actions.ApproveAsync(pending.Proposal.Id, new string('0', 64), Owner, default));
            await f.Actions.ApproveAsync(pending.Proposal.Id, pending.ArgumentsHash, Owner, default);
        }
        Assert.Equal(DeviceCommandStatus.Pending, (await f.Commands.GetAsync(pending.Proposal.Id))!.Status);
        Assert.Equal(AgentTaskStatus.AwaitingReceipt, await f.Store.ReadAsync(s => s.AgentTasks[pending.Proposal.Id].Status));
        await f.Actions.RefreshAsync(default);
        Assert.Equal(AgentTaskStatus.AwaitingReceipt, await f.Store.ReadAsync(s => s.AgentTasks[pending.Proposal.Id].Status));
        using var actor = DeviceCredentialContext.Enter((await f.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(f.Secret, default))!);
        var delivery = (await f.Services.GetRequiredService<DeviceCommandService>().ClaimAsync(new(Guid.NewGuid())))!;
        Assert.Equal(pending.Proposal.Id, delivery.Id);
        var receiptService = f.Services.GetRequiredService<DeviceReceiptService>();
        foreach (var kind in new[] { DeviceReceiptKind.Acknowledged, DeviceReceiptKind.Succeeded }) {
            var signed = DeviceReceiptTests.Sign(delivery.Id, delivery.DispatchRequestId, kind, """{"accepted":true}""", f.Secret);
            await receiptService.RecordAsync(VerifiedDeviceReceipt.Verify(delivery.Id, signed, f.Secret));
        }
        await f.Actions.RefreshAsync(default);
        Assert.Equal(AgentTaskStatus.Succeeded, await f.Store.ReadAsync(s => s.AgentTasks[delivery.Id].Status));
        Assert.Null(await f.Services.GetRequiredService<DeviceCommandService>().ClaimAsync(new(Guid.NewGuid())));
    }

    [Fact]
    public async Task Reversible_preapproval_requires_both_grants_and_cancellation_blocks_later_dispatch()
    {
        using var f = await Fixture.Create(preapproved: true);
        var proposed = await f.Propose(); Assert.Equal("AwaitingReceipt", proposed.Status);
        var repeated = await f.Propose(); Assert.Equal(proposed.Proposal.Id, repeated.Proposal.Id);
        await f.Runtime.CancelAsync(f.Task.RunId, false, default);
        using var actor = DeviceCredentialContext.Enter((await f.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(f.Secret, default))!);
        Assert.Null(await f.Services.GetRequiredService<DeviceCommandService>().ClaimAsync(new(Guid.NewGuid())));
        Assert.Equal(AgentTaskStatus.Cancelled, await f.Store.ReadAsync(s => s.AgentTasks[proposed.Proposal.Id].Status));
    }

    [Fact]
    public async Task Revoked_goal_grant_blocks_an_already_approved_pending_command()
    {
        using var f = await Fixture.Create(preapproved: true); var proposed = await f.Propose();
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) await f.Gateway.RevokeAsync(f.GrantId, Owner);
        using var actor = DeviceCredentialContext.Enter((await f.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(f.Secret, default))!);
        Assert.Null(await f.Services.GetRequiredService<DeviceCommandService>().ClaimAsync(new(Guid.NewGuid())));
        Assert.Equal(DeviceCommandStatus.Pending, (await f.Commands.GetAsync(proposed.Proposal.Id))!.Status);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Voice_approval_requires_unlocked_capture_and_a_reversible_capability(bool locked, bool irreversible, bool refused)
    {
        using var f = await Fixture.Create(irreversible: irreversible); var pending = await f.Propose();
        var session = Guid.NewGuid(); var source = Guid.NewGuid(); var now = f.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        await f.Store.UpdateAsync(s => { s.AudioLease = new(session, "voice-fixture", 1, now.AddMinutes(3));
            s.VoiceTurns[source] = new(source, session, ChatRoles.User, "Approve the first one", null, now); return true; });
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        await f.Actions.VoiceAsync("list", session, Guid.Empty, _ => Task.FromResult(true), default);
        if (locked) await f.Store.UpdateAsync(s => { s.AudioLease = s.AudioLease! with { Locked = true }; return true; });
        if (refused) {
            await Assert.ThrowsAsync<DomainException>(() => f.Actions.VoiceAsync("approve", session, source, _ => Task.FromResult(true), default));
            Assert.Null(await f.Commands.GetAsync(pending.Proposal.Id));
        } else {
            await f.Actions.VoiceAsync("approve", session, source, _ => Task.FromResult(true), default);
            Assert.Equal(DeviceCommandStatus.Pending, (await f.Commands.GetAsync(pending.Proposal.Id))!.Status);
        }
    }

    [Fact]
    public async Task Missing_or_restricted_utterance_never_approves_and_erasure_removes_proposals()
    {
        using var f = await Fixture.Create(); var pending = await f.Propose(); var session = Guid.NewGuid(); var source = Guid.NewGuid();
        var now = f.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        await f.Store.UpdateAsync(s => { s.AudioLease = new(session, "voice-fixture", 1, now.AddMinutes(3));
            s.VoiceTurns[source] = new(source, session, ChatRoles.User, "Approve the first one", null, now, Restricted: true); return true; });
        using (CoreInvocationContext.Enter(InvocationOrigin.Voice)) {
            await f.Actions.VoiceAsync("list", session, Guid.Empty, _ => Task.FromResult(true), default);
            foreach (var id in new[] { source, Guid.NewGuid() }) await Assert.ThrowsAsync<DomainException>(() => f.Actions.VoiceAsync("approve", session, id, _ => Task.FromResult(true), default));
        }
        Assert.Null(await f.Commands.GetAsync(pending.Proposal.Id));
        Assert.Empty(PrivacyData.EmptyAfterErase(await f.Store.ReadAsync(s => s)).AgentActions);
        await f.Runtime.CancelAsync(f.Task.RunId, true, default); Assert.Empty(await f.Actions.ListAsync());
    }

    [Fact]
    public async Task Manual_retry_is_idempotent_preserves_consent_and_obeys_recorded_cooldown()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var runtime = host.Services.GetRequiredService<AgentRuntime>(); var store = host.Services.GetRequiredService<IRuntimeStore>();
        var run = await runtime.StartAsync(new(Guid.NewGuid(), "daily-coaching", false, "Practise this"), default);
        var task = (await runtime.ClaimAsync(default))!;
        await runtime.RecordRetryAfterAsync(task.Id, TimeSpan.FromMinutes(2), default);
        await runtime.CompleteAsync(task.Id, task.ClaimToken!.Value, "", "synthetic_failure", false, default); await runtime.TickAsync(default);
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var request = new RetryAgentRunRequest(Guid.NewGuid());
        var cooldown = await Assert.ThrowsAsync<DomainException>(() => runtime.RetryAsync(run.Run.Id, request, default));
        Assert.Equal(429, cooldown.StatusCode); Assert.True(cooldown.RetryAfter > TimeSpan.FromSeconds(100));
        await store.UpdateAsync(s => { s.AgentTasks[task.Id] = s.AgentTasks[task.Id] with { AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1) }; return true; });
        var retry = await runtime.RetryAsync(run.Run.Id, request, default);
        Assert.False(retry.Run.AllowCloudProcessing); Assert.Equal(request.RequestId, retry.Run.Id);
        Assert.Equal(retry.Run.Id, (await runtime.RetryAsync(run.Run.Id, request, default)).Run.Id);
        Assert.Equal(2, await store.ReadAsync(s => s.AgentRuns.Count));
    }

    [Fact]
    public async Task Held_run_cannot_be_claimed_before_manual_owner_release()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var runtime = host.Services.GetRequiredService<AgentRuntime>();
        var held = await runtime.StartAsync(new(Guid.NewGuid(), "personal-plan", true, HoldForSetup: true), default);
        Assert.Null(await runtime.ClaimAsync(default)); Assert.Equal(AgentRunStatus.AwaitingSetup, held.Run.Status);
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) await runtime.ReleaseSetupAsync(held.Run.Id, Owner, default);
        Assert.NotNull(await runtime.ClaimAsync(default));
    }

    [Fact]
    public async Task Management_endpoints_require_separate_owner_authorization()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        using var ordinary = host.CreateClient(); ordinary.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner);
        var runtime = host.Services.GetRequiredService<AgentRuntime>(); var held = await runtime.StartAsync(new(Guid.NewGuid(), "personal-plan", true, HoldForSetup: true), default);
        var path = $"/api/agents/runs/{held.Run.Id}";
        var grant = new GrantAgentToolRequest(Guid.NewGuid(), BuiltInDeviceCapabilities.HomeAssistantTurn);
        using var denied = await ordinary.PostAsJsonAsync(path + "/tool-grants", grant); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var configured = await owner.PostAsJsonAsync(path + "/tool-grants", grant); configured.EnsureSuccessStatusCode();
        using var blocked = await ordinary.PostAsJsonAsync(path + "/release", new { }); Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        using var released = await owner.PostAsJsonAsync(path + "/release", new { }); released.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(90, 90)]
    [InlineData(100000, 86400)]
    public void Retry_after_delta_is_bounded_without_automatic_replay(int seconds, int expected)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        var error = Assert.Throws<DomainException>(() => ProviderRetryAfter.RequireSuccess(response, DateTimeOffset.UtcNow));
        Assert.Equal(429, error.StatusCode); Assert.Equal(TimeSpan.FromSeconds(expected), error.RetryAfter);
    }

    private sealed class Fixture(WebApplicationFactory<Program> root, WebApplicationFactory<Program> host, AgentTask task, Guid grant, Guid deviceGrant, string secret) : IDisposable
    {
        public IServiceProvider Services => host.Services;
        public AgentTask Task => task;
        public Guid GrantId => grant;
        public string Secret => secret;
        public IRuntimeStore Store => Services.GetRequiredService<IRuntimeStore>();
        public AgentRuntime Runtime => Services.GetRequiredService<AgentRuntime>();
        public AgentActionService Actions => Services.GetRequiredService<AgentActionService>();
        public AgentToolGateway Gateway => Services.GetRequiredService<AgentToolGateway>();
        public IDeviceCommandReader Commands => Services.GetRequiredService<IDeviceCommandReader>();
        public async Task<AgentActionView> Propose() { using var origin = CoreInvocationContext.Enter(InvocationOrigin.Agent);
            return await Actions.ProposeAsync(task, 0, grant, deviceGrant, DeviceCommandTests.Arguments, default); }
        public static async Task<Fixture> Create(bool preapproved = false, bool irreversible = false)
        {
            var root = new JarvisApiFactory(); var host = root.WithWebHostBuilder(b => {
                b.UseSetting("Jarvis:WorkflowsEnabled", "false");
                if (irreversible) b.ConfigureServices(s => { s.RemoveAll<IDeviceCapabilityCatalog>(); s.AddSingleton<IDeviceCapabilityCatalog>(new DeviceCapabilityCatalog(
                    BuiltInDeviceCapabilities.All.Select(c => c.Id == BuiltInDeviceCapabilities.HomeAssistantTurn ? c with { Risk = DeviceCapabilityRisk.Irreversible, ConfirmationPolicy = DeviceConfirmationPolicy.Owner } : c))); });
            });
            var services = host.Services; var (deviceGrant, secret) = await DeviceCommandTests.SetupAsync(services, "agent-action-fixture", preapproved);
            var runtime = services.GetRequiredService<AgentRuntime>(); var run = await runtime.StartAsync(new(Guid.NewGuid(), "personal-plan", true), default);
            var task = (await runtime.ClaimAsync(default))!;
            using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
            var grant = await services.GetRequiredService<AgentToolGateway>().GrantAsync(Guid.NewGuid(), run.Run.Id, BuiltInDeviceCapabilities.HomeAssistantTurn, 0, 4, preapproved, Owner);
            return new(root, host, task, grant.Id, deviceGrant, secret);
        }
        public void Dispose() { host.Dispose(); root.Dispose(); }
    }
}

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Pending_agent_action_keeps_exact_approval_authority_after_restart()
    {
        await using var database = await TestDatabase.CreateAsync(); AgentActionView pending;
        using (var first = new PostgresApiFactory(database.ConnectionString)) {
            var (deviceGrant, _) = await DeviceCommandTests.SetupAsync(first.Services, "restart-agent-actions");
            var runtime = first.Services.GetRequiredService<AgentRuntime>(); var run = await runtime.StartAsync(new(Guid.NewGuid(), "personal-plan", true), default);
            var task = (await runtime.ClaimAsync(default))!;
            AgentToolGrant grant;
            using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) grant = await first.Services.GetRequiredService<AgentToolGateway>().GrantAsync(Guid.NewGuid(), run.Run.Id,
                BuiltInDeviceCapabilities.HomeAssistantTurn, 0, 4, false, new("fixture-owner", [AgentToolGateway.ManageScope]));
            using (CoreInvocationContext.Enter(InvocationOrigin.Agent)) pending = await first.Services.GetRequiredService<AgentActionService>().ProposeAsync(task, 0, grant.Id, deviceGrant, DeviceCommandTests.Arguments, default);
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString);
        var actions = restarted.Services.GetRequiredService<AgentActionService>(); var restored = Assert.Single(await actions.ListAsync());
        Assert.Equal(pending.ArgumentsHash, restored.ArgumentsHash); Assert.Equal("AwaitingApproval", restored.Status);
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) await actions.ApproveAsync(restored.Proposal.Id, restored.ArgumentsHash, new("fixture-owner", [AgentToolGateway.ManageScope]), default);
        Assert.Equal(DeviceCommandStatus.Pending, (await restarted.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(restored.Proposal.Id))!.Status);
    }
}
