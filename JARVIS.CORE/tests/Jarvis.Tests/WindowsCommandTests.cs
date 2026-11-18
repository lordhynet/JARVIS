using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class WindowsCommandTests
{
    [Fact]
    public async Task Windows_delivery_is_authenticated_and_a_replayed_dispatch_is_never_redelivered()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var (grant, secret) = await SetupAsync(host.Services);
        var commands = host.Services.GetRequiredService<DeviceCommandService>(); var now = DateTimeOffset.UtcNow;
        DeviceCommandView proposal;
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) proposal = await commands.ProposeAsync("windows-command-fixture", new(Guid.NewGuid(), grant, "{\"action\":\"pause\"}", now.AddMinutes(2)));
        using var actor = DeviceCredentialContext.Enter((await host.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(secret, default))!);
        var claim = new ClaimDeviceCommandRequest(Guid.NewGuid()); var delivery = (await commands.ClaimAsync(claim))!;
        Assert.Equal(proposal.Id, delivery.Id); Assert.True(DeviceCommandProof.Verify(delivery, "windows-command-fixture", secret));
        Assert.False(DeviceCommandProof.Verify(delivery with { ArgumentsJson = "{\"action\":\"play\"}" }, "windows-command-fixture", secret));
        Assert.Null(await commands.ClaimAsync(claim)); Assert.Null(await commands.ClaimAsync(new(Guid.NewGuid())));
        var ack = DeviceReceiptTests.Sign(delivery.Id, delivery.DispatchRequestId, DeviceReceiptKind.Acknowledged, "{\"accepted\":true}", secret);
        Assert.Equal(DeviceCommandProof.ReceiptSignature(delivery.Id, ack, secret), ack.Signature);
        var recorded = await host.Services.GetRequiredService<DeviceReceiptService>().RecordAsync(VerifiedDeviceReceipt.Verify(delivery.Id, ack, secret));
        Assert.False(recorded.Late);
    }
    [Fact]
    public async Task Grant_revoked_between_delivery_and_acknowledgement_refuses_execution_permission()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var (grant, secret) = await SetupAsync(host.Services); var commands = host.Services.GetRequiredService<DeviceCommandService>();
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) await commands.ProposeAsync("windows-command-fixture", new(Guid.NewGuid(), grant, "{\"action\":\"pause\"}", DateTimeOffset.UtcNow.AddMinutes(2)));
        using var actor = DeviceCredentialContext.Enter((await host.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(secret, default))!);
        var delivery = (await commands.ClaimAsync(new(Guid.NewGuid())))!;
        await host.Services.GetRequiredService<IDeviceGrantStore>().RevokeAsync(grant, DateTimeOffset.UtcNow);
        var request = DeviceReceiptTests.Sign(delivery.Id, delivery.DispatchRequestId, DeviceReceiptKind.Acknowledged, "{\"accepted\":true}", secret);
        var error = await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<DeviceReceiptService>().RecordAsync(VerifiedDeviceReceipt.Verify(delivery.Id, request, secret)));
        Assert.Equal(403, error.StatusCode);
    }
    [Fact]
    public void Registry_restricts_ids_bounds_and_read_only_name_search()
    {
        var catalog = new DeviceCapabilityCatalog(WindowsDeviceCapabilities.All);
        var absent = new DeviceCapabilityCatalog(BuiltInDeviceCapabilities.All);
        Assert.False(absent.Permits(WindowsCommandProtocol.Media, WindowsCommandProtocol.Version, "{\"allowedActions\":[\"pause\"]}", "{\"action\":\"pause\"}"));
        Assert.False(absent.HasValidConstraints(WindowsCommandProtocol.Media, WindowsCommandProtocol.Version, "{\"allowedActions\":[\"pause\"]}"));
        Assert.False(catalog.Permits(WindowsCommandProtocol.App, WindowsCommandProtocol.Version, "{\"allowedIds\":[\"editor\"]}", "{\"appId\":\"cmd.exe\"}"));
        Assert.False(catalog.Permits(WindowsCommandProtocol.Files, WindowsCommandProtocol.Version, "{\"allowedIds\":[\"cv\"]}", "{\"folderId\":\"cv\",\"name\":\"../outside\"}"));
        Assert.True(catalog.Permits(WindowsCommandProtocol.Files, WindowsCommandProtocol.Version, "{\"allowedIds\":[\"cv\"]}", "{\"folderId\":\"cv\",\"name\":\"resume\"}"));
        Assert.Equal(DeviceCapabilityRisk.ReadOnly, catalog.Get(WindowsCommandProtocol.Files, WindowsCommandProtocol.Version).Risk);
        Assert.False(catalog.Permits(WindowsCommandProtocol.Volume, WindowsCommandProtocol.Version, "{\"allowedActions\":[\"up\"],\"maximumSteps\":2}", "{\"action\":\"up\",\"steps\":3}"));
        Assert.False(catalog.HasValidConstraints(WindowsCommandProtocol.App, WindowsCommandProtocol.Version, "{\"allowedIds\":[\"../outside\"]}"));
    }
    [Fact]
    public async Task Locked_voice_allows_only_captured_media_or_lock_and_hides_aliases()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var (_, secret) = await SetupAsync(host.Services, locked: true); var store = host.Services.GetRequiredService<IRuntimeStore>();
        var session = Guid.NewGuid(); var source = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await store.UpdateAsync(s => { s.AudioLease = new(session, "windows-command-fixture", 10, now.AddMinutes(3), Locked: true);
            s.VoiceTurns[source] = new(source, session, ChatRoles.User, "Pause the music", null, now, Restricted: true); return true; });
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var tool = host.Services.GetRequiredService<CoreWindowsVoiceTool>();
        var listed = JsonSerializer.Serialize(await tool.RunAsync("list", session, Guid.Empty, _ => Task.FromResult(true), default));
        Assert.DoesNotContain("allowedActions", listed); Assert.DoesNotContain("Aliases", listed);
        var proposed = JsonSerializer.SerializeToElement(await tool.RunAsync("control", session, source, _ => Task.FromResult(true), default));
        Assert.Equal("awaiting_confirmation", proposed.GetProperty("status").GetString());
        var commandId = proposed.GetProperty("commandId").GetGuid();
        await store.UpdateAsync(s => { s.VoiceTurns[source] = s.VoiceTurns[source] with { Text = "Volume up" }; return true; });
        var denied = await Assert.ThrowsAsync<DomainException>(() => tool.RunAsync("control", session, source, _ => Task.FromResult(true), default)); Assert.Equal(403, denied.StatusCode);
        await store.UpdateAsync(s => { s.VoiceTurns[source] = s.VoiceTurns[source] with { Text = "Pause the music" }; return true; });
        var saved = (await host.Services.GetRequiredService<IDeviceCommandReader>().GetAsync(commandId))!;
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) await host.Services.GetRequiredService<DeviceCommandService>().ConfirmAsync(commandId, new(saved.ArgumentHash, true), new("fixture-owner", [DeviceCommandService.ConfirmScope]));
        await store.UpdateAsync(s => { s.AudioLease = s.AudioLease! with { Fence = 11 }; return true; });
        using var actor = DeviceCredentialContext.Enter((await host.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(secret, default))!);
        Assert.Null(await host.Services.GetRequiredService<DeviceCommandService>().ClaimAsync(new(Guid.NewGuid())));
    }
    internal static async Task<(Guid Grant, string Secret)> SetupAsync(IServiceProvider services, bool locked = false)
    {
        var devices = services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new("windows-command-fixture", "Windows fixture", DeviceKind.WindowsAgent,
            new(true, true, false, false, [CompanionVoiceProtocol.NativeWindows, CompanionVoiceProtocol.AudioOnly])));
        var secret = (await services.GetRequiredService<DeviceCredentials>().IssueAsync("windows-command-fixture", default)).Secret;
        await devices.VerifyAsync("windows-command-fixture");
        var grant = await services.GetRequiredService<DeviceGrantService>().GrantAsync("windows-command-fixture", new(WindowsCommandProtocol.Media, WindowsCommandProtocol.Version,
            "{\"allowedActions\":[\"play\",\"pause\",\"next\"]}", DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid(), "Windows action fixture", AllowWhenLocked: locked), new("fixture-owner", [DeviceGrantService.ManageScope]));
        return (grant.Id, secret);
    }
}
