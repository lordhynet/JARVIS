using System.Net;
using System.Text;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class LockedVoiceBoundaryTests
{
    internal static async Task<VoiceTicket> StartAsync(IServiceProvider services, DeviceKind kind = DeviceKind.WindowsAgent, bool? locked = null,
        int windowSeconds = VoiceWindowLimits.DefaultSeconds, bool browser = false)
    {
        const string device = "locked-fixture";
        var devices = services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new(device, "Locked voice fixture", kind, new(true, true, browser, false,
            browser ? [VoiceAccessRules.BrowserCompanionTag] : ["audio-only"])));
        await devices.VerifyAsync(device);
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        await services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Private title", SessionStatus.Active, device, "Private task marker",
            now, now, [], Messages: [new(Guid.NewGuid(), ChatRoles.User, "Private conversation marker", now)]));
        return await services.GetRequiredService<AudioOwnership>().StartAsync(new(id, device, true, locked), default, windowSeconds);
    }

    [Theory]
    [InlineData(DeviceKind.WindowsAgent, null, true)]
    [InlineData(DeviceKind.WindowsAgent, true, true)]
    [InlineData(DeviceKind.WindowsAgent, false, false)]
    [InlineData(DeviceKind.AndroidCompanion, null, false)]
    public async Task Admission_binds_lock_policy_to_the_lease_with_compatible_defaults(DeviceKind kind, bool? report, bool restricted)
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var ticket = await StartAsync(host.Services, kind, report);
        Assert.Equal(restricted, ticket.Lease.Locked);
        var saved = await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AudioLease);
        Assert.Equal(ticket.Lease, saved);
    }

    [Fact]
    public async Task Existing_owner_registered_desktop_browser_keeps_its_supported_default_on_start_and_renewal()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var ticket = await StartAsync(host.Services, browser: true);
        Assert.False(ticket.Lease.Locked);
        Assert.False((await host.Services.GetRequiredService<AudioOwnership>().RenewAsync(ticket.Lease, default)).Locked);
    }

    [Fact]
    public async Task Unlock_waits_for_Core_window_activation_and_stale_or_forged_lease_metadata_cannot_change_it()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var ticket = await StartAsync(host.Services, locked: true);
        var ownership = host.Services.GetRequiredService<AudioOwnership>();
        var renewed = await ownership.RenewAsync(ticket.Lease with { Locked = false, ReportedLocked = false }, default, locked: false);
        Assert.True(renewed.Locked); Assert.False(renewed.ReportedLocked);
        Assert.True(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => VoiceAccessRules.Restricted(s, ticket.Lease.SessionId)));
        var error = await Assert.ThrowsAsync<DomainException>(() => ownership.ActivateVoiceWindowAsync(ticket.Lease with { Fence = ticket.Lease.Fence + 1 }, default));
        Assert.Equal(409, error.StatusCode);
        Assert.True((await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AudioLease))!.Locked);
        Assert.False((await ownership.ActivateVoiceWindowAsync(ticket.Lease, default)).Locked);
        await ownership.RenewAsync(ticket.Lease, default); // Missing Windows report becomes restricted.
        Assert.True((await ownership.ActivateVoiceWindowAsync(ticket.Lease, default)).Locked);
    }

    [Theory]
    [InlineData("core_context")]
    [InlineData("core_story_search")]
    [InlineData("core_coaching")]
    [InlineData("core_coaching_prompt")]
    [InlineData("core_coaching_correction")]
    [InlineData("core_practice_current")]
    [InlineData("core_practice_start")]
    [InlineData("core_recall")]
    [InlineData("core_domain_propose")]
    [InlineData("core_briefing")]
    public async Task Locked_personal_tool_calls_are_denied_in_Core_even_when_called_directly(string name)
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var ticket = await StartAsync(host.Services);
        var result = await host.Services.GetRequiredService<CoreVoiceTools>().ExecuteAsync(name, "{}", ticket.Lease.SessionId, new HashSet<Guid>(), default);
        Assert.Contains("unavailable while the device is locked", result);
        Assert.DoesNotContain("Private", result);
        Assert.Empty(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.TrainingDrills));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Locked_home_control_requires_explicit_grant_and_keeps_receipt_authority(bool allowWhenLocked)
    {
        var posts = 0; var reads = 0;
        using var host = new ProviderFactory(request => {
            if (request.Method == HttpMethod.Post) { posts++; return Task.FromResult(Reply("[]")); }
            reads++;
            return Task.FromResult(Reply(JsonSerializer.Serialize(new { entity_id = "light.kitchen", state = posts > 0 ? "on" : "off", attributes = new { friendly_name = "Kitchen light" } })));
        });
        var (oldGrant, secret) = await DeviceCommandTests.SetupAsync(host.Services, "locked-home-executor");
        var config = host.Services.GetRequiredService<IConfiguration>();
        config[HomeAssistantCommandAdapter.ExecutorDeviceId] = "locked-home-executor";
        config[HomeAssistantCommandAdapter.ExecutorCredential] = secret;
        config["HomeAssistant:AllowedEntities:0"] = "light.kitchen";
        var request = DeviceGrantTests.GrantRequest() with { PreApproved = true, AllowWhenLocked = allowWhenLocked };
        var grant = await host.Services.GetRequiredService<DeviceGrantService>().GrantAsync("locked-home-executor",
            request, new("fixture-owner", [DeviceGrantService.ManageScope]));
        var ticket = await StartAsync(host.Services);
        var source = await VoiceHomeTests.SayAsync(host.Services, ticket.Lease.SessionId, "turn on the kitchen light");
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var result = await VoiceHomeTests.ToolAsync(host.Services, ticket.Lease.SessionId, source, source);
        Assert.Equal(allowWhenLocked, result.GetProperty("ok").GetBoolean());
        Assert.Equal(allowWhenLocked ? 1 : 0, posts);
        if (!allowWhenLocked) Assert.Equal(0, reads);
        if (allowWhenLocked) Assert.Equal("succeeded", result.GetProperty("result").GetProperty("status").GetString());
        var changed = await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<DeviceGrantService>().GrantAsync("locked-home-executor",
            request with { AllowWhenLocked = !allowWhenLocked }, new("fixture-owner", [DeviceGrantService.ManageScope])));
        Assert.Equal(409, changed.StatusCode);
    }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
