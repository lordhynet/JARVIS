using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class DeviceGrantTests
{
    [Fact]
    public async Task Owner_grants_installed_capability_bound_to_current_device_credential_and_revokes_it()
    {
        using var host = new JarvisApiFactory();
        const string deviceId = "grant-phone";
        await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new(deviceId, "Grant fixture", DeviceKind.AndroidCompanion,
            new(true, true, false, false, [])));
        using var owner = host.CreateAuthorisedClient();
        var issued = await Post<IssuedDeviceCredential>(owner, $"/api/devices/{deviceId}/credential", new { });
        using var phone = host.CreateClient(); phone.DefaultRequestHeaders.Add("X-Jarvis-Key", issued.Secret);
        var request = new GrantDeviceCapabilityRequest("homeassistant.turn", "1.0.0", "{}", DateTimeOffset.UtcNow.AddDays(30));
        var grant = await Post<DeviceCapabilityGrantView>(owner, $"/api/devices/{deviceId}/grants", request);
        Assert.Equal(deviceId, grant.DeviceId);
        Assert.Equal(request.CapabilityId, grant.CapabilityId);
        Assert.DoesNotContain("CredentialHash", System.Text.Json.JsonSerializer.Serialize(grant));
        var store = host.Services.GetRequiredService<IDeviceGrantStore>();
        Assert.Equal(grant.Id, (await store.FindActiveAsync(deviceId, request.CapabilityId, request.CapabilityVersion,
            PrivacyData.Hash(issued.Secret), DateTimeOffset.UtcNow))!.Id);

        using var forbidden = await phone.PostAsJsonAsync($"/api/devices/{deviceId}/grants", request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var revoked = await owner.DeleteAsync($"/api/device-grants/{grant.Id}"); revoked.EnsureSuccessStatusCode();
        Assert.Null(await store.FindActiveAsync(deviceId, request.CapabilityId, request.CapabilityVersion,
            PrivacyData.Hash(issued.Secret), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Credential_rotation_revokes_grants_and_expired_grants_never_match()
    {
        using var host = new JarvisApiFactory(); const string deviceId = "rotating-grant-phone";
        await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new(deviceId, "Grant fixture", DeviceKind.AndroidCompanion,
            new(true, true, false, false, [])));
        using var owner = host.CreateAuthorisedClient();
        var issued = await Post<IssuedDeviceCredential>(owner, $"/api/devices/{deviceId}/credential", new { });
        var request = new GrantDeviceCapabilityRequest("homeassistant.turn", "1.0.0", "{}", DateTimeOffset.UtcNow.AddDays(30));
        var grant = await Post<DeviceCapabilityGrantView>(owner, $"/api/devices/{deviceId}/grants", request);
        var store = host.Services.GetRequiredService<IDeviceGrantStore>();
        await Post<IssuedDeviceCredential>(owner, $"/api/devices/{deviceId}/credential", new { });
        Assert.Null(await store.FindActiveAsync(deviceId, request.CapabilityId, request.CapabilityVersion,
            PrivacyData.Hash(issued.Secret), DateTimeOffset.UtcNow));
        Assert.NotNull((await store.GetAsync(grant.Id))!.RevokedAt);

        var expired = new DeviceGrant(Guid.NewGuid(), deviceId, request.CapabilityId, request.CapabilityVersion,
            "{}", "fixture-hash", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        await store.AddAsync(expired);
        Assert.Null(await store.FindActiveAsync(deviceId, request.CapabilityId, request.CapabilityVersion,
            "fixture-hash", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Grant_rejects_unknown_capability_and_malformed_constraints()
    {
        using var host = new JarvisApiFactory(); const string deviceId = "invalid-grant-phone";
        await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new(deviceId, "Grant fixture", DeviceKind.AndroidCompanion,
            new(true, true, false, false, [])));
        using var owner = host.CreateAuthorisedClient();
        await Post<IssuedDeviceCredential>(owner, $"/api/devices/{deviceId}/credential", new { });
        using var unknown = await owner.PostAsJsonAsync($"/api/devices/{deviceId}/grants",
            new GrantDeviceCapabilityRequest("unknown.action", "1.0.0", "{}", DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var malformed = await owner.PostAsJsonAsync($"/api/devices/{deviceId}/grants",
            new GrantDeviceCapabilityRequest("homeassistant.turn", "1.0.0", "[]", DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    private static async Task<T> Post<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(LifeScenarioTests.Json))!;
    }
}
