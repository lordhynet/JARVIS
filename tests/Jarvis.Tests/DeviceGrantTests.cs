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
        var request = GrantRequest();
        var grant = await Post<DeviceCapabilityGrantView>(owner, $"/api/devices/{deviceId}/grants", request);
        Assert.Equal(deviceId, grant.DeviceId);
        Assert.Equal(request.CapabilityId, grant.CapabilityId);
        Assert.Equal(new DeviceGrantAudit(request.RequestId, "fixture-owner", request.Reason!), grant.Audit);
        Assert.True(grant.Effective);
        Assert.DoesNotContain("CredentialHash", System.Text.Json.JsonSerializer.Serialize(grant));
        var replay = await Post<DeviceCapabilityGrantView>(owner, $"/api/devices/{deviceId}/grants",
            request with { ConstraintsJson = """{ "allowedEntityIds": [ "light.kitchen" ] }""" });
        Assert.Equal(grant.Id, replay.Id);
        using var reuse = await owner.PostAsJsonAsync($"/api/devices/{deviceId}/grants",
            request with { Reason = "Changed reason" });
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
        var store = host.Services.GetRequiredService<IDeviceGrantStore>();
        Assert.Equal(grant.Id, (await store.FindActiveAsync(deviceId, request.CapabilityId, request.CapabilityVersion,
            PrivacyData.Hash(issued.Secret), DateTimeOffset.UtcNow))!.Id);

        using var forbidden = await phone.PostAsJsonAsync($"/api/devices/{deviceId}/grants", request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var revoked = await owner.DeleteAsync($"/api/device-grants/{grant.Id}"); revoked.EnsureSuccessStatusCode();
        Assert.Equal(0, await store.PruneAsync(DateTimeOffset.UtcNow));
        var recalled = await Post<DeviceCapabilityGrantView>(owner, $"/api/devices/{deviceId}/grants", request);
        Assert.Equal(grant.Id, recalled.Id);
        Assert.NotNull(recalled.RevokedAt);
        Assert.False(recalled.Effective);
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
        var request = GrantRequest();
        var grant = await Post<DeviceCapabilityGrantView>(owner, $"/api/devices/{deviceId}/grants", request);
        var store = host.Services.GetRequiredService<IDeviceGrantStore>();
        await Post<IssuedDeviceCredential>(owner, $"/api/devices/{deviceId}/credential", new { });
        Assert.Null(await store.FindActiveAsync(deviceId, request.CapabilityId, request.CapabilityVersion,
            PrivacyData.Hash(issued.Secret), DateTimeOffset.UtcNow));
        Assert.NotNull((await store.GetAsync(grant.Id))!.RevokedAt);
        var listed = Assert.Single((await owner.GetFromJsonAsync<DeviceCapabilityGrantView[]>("/api/device-grants", LifeScenarioTests.Json))!);
        Assert.False(listed.Effective);

        var expired = new DeviceGrant(Guid.NewGuid(), deviceId, request.CapabilityId, request.CapabilityVersion,
            request.ConstraintsJson, "fixture-hash", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1),
            GrantedBy: "fixture-owner", Reason: "Expired fixture");
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
            GrantRequest() with { CapabilityId = "unknown.action" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var malformed = await owner.PostAsJsonAsync($"/api/devices/{deviceId}/grants",
            GrantRequest() with { ConstraintsJson = "[]" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var empty = await owner.PostAsJsonAsync($"/api/devices/{deviceId}/grants",
            GrantRequest() with { ConstraintsJson = "{}" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var missingAudit = await owner.PostAsJsonAsync($"/api/devices/{deviceId}/grants",
            GrantRequest() with { RequestId = Guid.Empty, Reason = null });
        Assert.Equal(HttpStatusCode.BadRequest, missingAudit.StatusCode);
    }

    [Fact]
    public async Task Historical_grants_and_stale_credential_bindings_are_listed_as_ineffective()
    {
        using var host = new JarvisApiFactory();
        const string deviceId = "legacy-grant-phone";
        await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new(deviceId, "Grant fixture", DeviceKind.AndroidCompanion,
            new(true, true, false, false, [])));
        using var owner = host.CreateAuthorisedClient();
        var credential = await Post<IssuedDeviceCredential>(owner, $"/api/devices/{deviceId}/credential", new { });
        var store = host.Services.GetRequiredService<IDeviceGrantStore>();
        var request = GrantRequest();
        var now = DateTimeOffset.UtcNow;
        var legacy = new DeviceGrant(Guid.NewGuid(), deviceId, request.CapabilityId, request.CapabilityVersion,
            request.ConstraintsJson, PrivacyData.Hash(credential.Secret), now, now.AddDays(1));
        var stale = legacy with { Id = Guid.NewGuid(), CredentialHash = "stale", GrantedBy = "fixture-owner", Reason = "Previous credential" };
        await store.AddAsync(legacy);
        await store.AddAsync(stale);
        var result = (await owner.GetFromJsonAsync<DeviceCapabilityGrantView[]>("/api/device-grants", LifeScenarioTests.Json))!;
        Assert.Equal(2, result.Length);
        Assert.Null(result.Single(row => row.Id == legacy.Id).Audit);
        Assert.All(result, row => Assert.False(row.Effective));
        Assert.Null(await store.FindActiveAsync(legacy.DeviceId, legacy.CapabilityId, legacy.CapabilityVersion, legacy.CredentialHash, now));
    }

    internal static GrantDeviceCapabilityRequest GrantRequest() =>
        new("homeassistant.turn", "1.0.0", """{"allowedEntityIds":["light.kitchen"]}""", DateTimeOffset.UtcNow.AddDays(30),
            Guid.NewGuid(), "Approved kitchen light");

    private static async Task<T> Post<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(LifeScenarioTests.Json))!;
    }
}
