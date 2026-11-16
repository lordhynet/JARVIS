using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jarvis.Tests;

public sealed class DeviceCommandDeliveryTests
{
    private const string ClaimRoute = "/api/device-commands/claim";
    private static readonly BudgetActor Owner = new("fixture-owner", [DeviceCommandService.ConfirmScope]);

    [Fact]
    public async Task Device_command_claim_requires_pairing_and_only_delivers_approved_commands_for_the_authenticated_device()
    {
        using var host = new JarvisApiFactory();
        var (grantId, secret) = await DeviceCommandTests.SetupAsync(host.Services, "delivery-target");
        var (otherGrant, otherSecret) = await DeviceCommandTests.SetupAsync(host.Services, "delivery-other");
        using var owner = host.CreateAuthorisedClient();
        using var target = host.CreateClient();
        using var other = host.CreateClient();
        using var anonymous = host.CreateClient();
        target.DefaultRequestHeaders.Add("X-Jarvis-Key", secret);
        other.DefaultRequestHeaders.Add("X-Jarvis-Key", otherSecret);
        var request = new ClaimDeviceCommandRequest(Guid.NewGuid());
        using var missingCredential = await anonymous.PostAsJsonAsync(ClaimRoute, request);
        Assert.Equal(HttpStatusCode.Unauthorized, missingCredential.StatusCode);
        using var unpairedOwner = await owner.PostAsJsonAsync(ClaimRoute, request);
        Assert.Equal(HttpStatusCode.Forbidden, unpairedOwner.StatusCode);
        using var invalid = await target.PostAsJsonAsync(ClaimRoute, new ClaimDeviceCommandRequest(Guid.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var service = host.Services.GetRequiredService<DeviceCommandService>();
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var proposed = await service.ProposeAsync("delivery-target", DeviceCommandTests.Request(grantId, DateTimeOffset.UtcNow));
        using var unapproved = await target.PostAsJsonAsync(ClaimRoute, request);
        Assert.Equal(HttpStatusCode.NoContent, unapproved.StatusCode);
        var decision = new ConfirmDeviceCommandRequest(proposed.ArgumentHash, true);
        await service.ConfirmAsync(proposed.Id, decision, Owner);

        using var spoofed = await other.PostAsJsonAsync(ClaimRoute, new { request.RequestId, DeviceId = "delivery-target" });
        Assert.Equal(HttpStatusCode.NoContent, spoofed.StatusCode);
        var delivered = await ClaimAsync(target, request);
        Assert.Equal(proposed.Id, delivered.Id);
        Assert.Equal(proposed.ArgumentHash, delivered.ArgumentHash);
        Assert.Equal(proposed.ArgumentsJson, delivered.ArgumentsJson);
        Assert.Equal(delivered, await ClaimAsync(target, request));
        using var freshClaim = await target.PostAsJsonAsync(ClaimRoute, new ClaimDeviceCommandRequest(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NoContent, freshClaim.StatusCode);
        var status = (await owner.GetFromJsonAsync<DeviceCommandView>($"/api/device-commands/{proposed.Id}", LifeScenarioTests.Json))!;
        Assert.Equal(DeviceCommandStatus.Dispatched, status.Status);
        Assert.Equal(delivered.DispatchedAt, status.DispatchedAt);
        var tooLate = await Assert.ThrowsAsync<DomainException>(() => service.ConfirmAsync(proposed.Id, decision with { Approve = false }, Owner));
        Assert.Equal(409, tooLate.StatusCode);

        var otherCommand = await ApproveAsync(host.Services, "delivery-other", otherGrant);
        Assert.Equal(otherCommand.Id, (await ClaimAsync(other, request)).Id);
        Assert.Equal(delivered, await ClaimAsync(target, request));
    }

    [Fact]
    public async Task Device_command_claim_rechecks_revocation_expiry_and_the_authenticated_credential_binding()
    {
        var clock = new ScenarioClock();
        using var baseHost = new JarvisApiFactory();
        using var host = baseHost.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }));
        const string deviceId = "fenced-delivery";
        var (revokedGrant, secret) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
        var revoked = await ApproveAsync(host.Services, deviceId, revokedGrant);
        var grants = host.Services.GetRequiredService<IDeviceGrantStore>();
        var original = (await grants.GetAsync(revokedGrant))!;
        Assert.True(await grants.RevokeAsync(revokedGrant, clock.GetUtcNow()));
        var grant = await grants.AddAsync(original with { Id = Guid.NewGuid() });
        var expired = await ApproveAsync(host.Services, deviceId, grant.Id);
        clock.Advance(TimeSpan.FromMinutes(3));

        var service = host.Services.GetRequiredService<DeviceCommandService>();
        var reader = host.Services.GetRequiredService<IDeviceCommandReader>();
        var credentials = host.Services.GetRequiredService<DeviceCredentials>();
        var actor = await credentials.AuthenticateAsync(secret, default);
        Assert.NotNull(actor);
        using var device = DeviceCredentialContext.Enter(actor);
        var request = new ClaimDeviceCommandRequest(Guid.NewGuid());
        Assert.Null(await service.ClaimAsync(request));
        Assert.Equal(DeviceCommandStatus.Expired, (await reader.GetAsync(expired.Id))!.StatusAt(clock.GetUtcNow()));
        Assert.Null((await reader.GetAsync(revoked.Id))!.DispatchedAt);

        var canDeliver = await ApproveAsync(host.Services, deviceId, grant.Id);
        var dispatched = await service.ClaimAsync(request);
        Assert.Equal(canDeliver.Id, dispatched!.Id);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(await service.ClaimAsync(request));
        Assert.Equal(DeviceCommandStatus.Expired, (await reader.GetAsync(canDeliver.Id))!.StatusAt(clock.GetUtcNow()));

        var replayable = await ApproveAsync(host.Services, deviceId, grant.Id);
        var replay = new ClaimDeviceCommandRequest(Guid.NewGuid());
        Assert.Equal(replayable.Id, (await service.ClaimAsync(replay))!.Id);
        Assert.True(await grants.RevokeAsync(grant.Id, clock.GetUtcNow()));
        Assert.Null(await service.ClaimAsync(replay));

        var replacement = await credentials.IssueAsync(deviceId, default);
        var stale = await Assert.ThrowsAsync<DomainException>(() => service.ClaimAsync(new(Guid.NewGuid())));
        Assert.Equal(401, stale.StatusCode);
        using var updatedDevice = DeviceCredentialContext.Enter((await credentials.AuthenticateAsync(replacement.Secret, default))!);
        Assert.Null(await service.ClaimAsync(replay));
    }

    [Fact]
    public async Task Device_command_claim_never_replays_a_capability_with_that_policy()
    {
        using var host = new JarvisApiFactory();
        const string deviceId = "single-delivery";
        var (grantId, secret) = await DeviceCommandTests.SetupAsync(host.Services, deviceId);
        var proposal = await ApproveAsync(host.Services, deviceId, grantId);
        var descriptor = Assert.Single(BuiltInDeviceCapabilities.All) with { IdempotencyRule = DeviceIdempotencyRule.NeverReplay };
        var service = new DeviceCommandService(new DeviceCapabilityCatalog([descriptor]),
            host.Services.GetRequiredService<ICoreTransaction>(), TimeProvider.System);
        var actor = await host.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(secret, default);
        using var paired = DeviceCredentialContext.Enter(actor!);
        var request = new ClaimDeviceCommandRequest(Guid.NewGuid());
        Assert.Equal(proposal.Id, (await service.ClaimAsync(request))!.Id);
        Assert.Null(await service.ClaimAsync(request));
        Assert.Null(await service.ClaimAsync(new(Guid.NewGuid())));
    }

    internal static async Task<DeviceCommandView> ApproveAsync(IServiceProvider services, string deviceId, Guid grantId)
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var service = services.GetRequiredService<DeviceCommandService>();
        var request = DeviceCommandTests.Request(grantId, services.GetRequiredService<TimeProvider>().GetUtcNow());
        var proposal = await service.ProposeAsync(deviceId, request);
        return await service.ConfirmAsync(proposal.Id, new(proposal.ArgumentHash, true), Owner);
    }

    private static async Task<DeliveredDeviceCommand> ClaimAsync(HttpClient client, ClaimDeviceCommandRequest request)
    {
        using var response = await client.PostAsJsonAsync(ClaimRoute, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("credentialHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirmedBy", body, StringComparison.OrdinalIgnoreCase);
        return System.Text.Json.JsonSerializer.Deserialize<DeliveredDeviceCommand>(body, LifeScenarioTests.Json)!;
    }
}
