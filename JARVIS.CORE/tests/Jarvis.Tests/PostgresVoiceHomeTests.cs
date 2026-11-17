using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Voice_home_grant_persists_explicit_authority_and_dispatch_enforces_device_and_entity_limits()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString);
        var services = host.Services;
        const string deviceId = "voice-home-quota";
        var (old, secret) = await DeviceCommandTests.SetupAsync(services, deviceId);
        var grants = services.GetRequiredService<IDeviceGrantStore>();
        Assert.False((await grants.GetAsync(old))!.PreApproved);
        var request = DeviceGrantTests.GrantRequest() with { PreApproved = true };
        var grantService = services.GetRequiredService<DeviceGrantService>();
        var owner = new BudgetActor("fixture-owner", [DeviceGrantService.ManageScope]);
        var grant = await grantService.GrantAsync(deviceId, request, owner);
        Assert.True((await grants.GetAsync(grant.Id))!.PreApproved);
        var tampered = await Assert.ThrowsAsync<DomainException>(() => grantService.GrantAsync(deviceId, request with { PreApproved = false }, owner));
        Assert.Equal(409, tampered.StatusCode);
        var now = DateTimeOffset.UtcNow;
        var transaction = services.GetRequiredService<ICoreTransaction>();
        await transaction.ExecuteAsync(async writes =>
        {
            for (var index = 0; index < 20; index++)
            {
                await writes.SaveDeviceCommandAsync(new(Guid.NewGuid(), grant.Id, "other-" + index, "homeassistant.turn", "1.0.0",
                    PrivacyData.Hash(secret), DeviceCommandTests.Arguments, PrivacyData.Hash(DeviceCommandTests.Arguments),
                    InvocationOrigin.Agent, DeviceCommandStatus.Failed, now, now.AddMinutes(1), DispatchedAt: now, HomeEntityId: "light.kitchen"));
                await writes.SaveDeviceCommandAsync(new(Guid.NewGuid(), grant.Id, deviceId, "homeassistant.turn", "1.0.0",
                    PrivacyData.Hash(secret), DeviceCommandTests.Arguments, PrivacyData.Hash(DeviceCommandTests.Arguments),
                    InvocationOrigin.Agent, DeviceCommandStatus.Failed, now, now.AddMinutes(1), DispatchedAt: now, HomeEntityId: "light.other"));
            }
            Assert.False(await writes.HasHomeDispatchCapacityAsync(deviceId, "light.unused", now.AddMinutes(-1), 20));
            Assert.False(await writes.HasHomeDispatchCapacityAsync("unused-device", "light.kitchen", now.AddMinutes(-1), 20));
            Assert.True(await writes.HasHomeDispatchCapacityAsync("unused-device", "light.unused", now.AddMinutes(-1), 20));
            return true;
        });
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var commands = services.GetRequiredService<DeviceCommandService>();
        var proposal = await commands.ProposeAsync(deviceId, DeviceCommandTests.Request(grant.Id, now));
        Assert.Equal(DeviceCommandStatus.Pending, proposal.Status);
        var actor = await services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(secret, default);
        using var paired = DeviceCredentialContext.Enter(actor!);
        Assert.Null(await commands.ClaimAsync(new(Guid.NewGuid())));
        var limited = await Assert.ThrowsAsync<DomainException>(() => commands.ClaimVoiceHomeCommandAsync(proposal.Id));
        Assert.Equal(429, limited.StatusCode);
        Assert.Equal(DeviceCommandStatus.Cancelled, (await services.GetRequiredService<IDeviceCommandReader>().GetAsync(proposal.Id))!.Status);
    }
}
