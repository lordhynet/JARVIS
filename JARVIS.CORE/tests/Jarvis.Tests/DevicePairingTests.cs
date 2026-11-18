using System.Net;
using System.Net.Http.Json;
using Jarvis.Api;
using Jarvis.Api.Endpoints;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class DevicePairingTests
{
    private sealed record Exchanged(string DeviceId, string Credential);

    private static async Task<IssuedPairingCode> Code(WebApplicationFactory<Program> host, string? deviceId = null)
    {
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner);
        using var response = await owner.PostAsJsonAsync(CoreRoutes.Devices + DeviceEndpoints.PairingCodesSuffix, new CreatePairingCodeRequest(deviceId));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuedPairingCode>(LifeScenarioTests.Json))!;
    }

    private static Task<HttpResponseMessage> Exchange(WebApplicationFactory<Program> host, string code, string phone) =>
        host.CreateClient().PostAsJsonAsync(DeviceEndpoints.PairingExchangeRoute, new PairingExchangeRequest(code, phone, "Android S22"));

    [Fact]
    public async Task A_scanned_code_gives_the_phone_its_own_scoped_key_once()
    {
        using var host = new JarvisApiFactory();
        var code = await Code(host);
        Assert.StartsWith(DevicePairing.Prefix, code.Code);
        Assert.True(code.ExpiresAt - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5));

        using var response = await Exchange(host, code.Code, "android-0f3c9a1e-77aa");
        response.EnsureSuccessStatusCode();
        var issued = (await response.Content.ReadFromJsonAsync<Exchanged>(LifeScenarioTests.Json))!;
        Assert.Equal("android-0f3c9a1e-77aa", issued.DeviceId);
        Assert.StartsWith(DeviceCredentials.Prefix, issued.Credential);
        Assert.NotEqual(JarvisApiFactory.TestKey, issued.Credential);

        // The phone's key reaches companion routes and sees only itself, never owner management.
        using var phone = host.CreateClient(); phone.DefaultRequestHeaders.Add("X-Jarvis-Key", issued.Credential);
        var devices = await phone.GetFromJsonAsync<DeviceView[]>("/api/devices", LifeScenarioTests.Json);
        var self = Assert.Single(devices!);
        Assert.Equal(DeviceKind.AndroidCompanion, self.Device.Registration.Kind);
        Assert.Contains("audio-only", self.Device.Registration.Capabilities.Extra);
        Assert.False(self.Device.CapabilitiesVerified);
        using var ownerOnly = await phone.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.Forbidden, ownerOnly.StatusCode);

        using var reused = await Exchange(host, code.Code, "android-0f3c9a1e-77aa");
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Fact]
    public async Task Expired_wrong_and_malformed_codes_are_refused_before_registration()
    {
        using var host = new JarvisApiFactory();
        var code = await Code(host);
        await host.Services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => {
            foreach (var key in s.DevicePairingCodes.Keys.ToArray())
                s.DevicePairingCodes[key] = s.DevicePairingCodes[key] with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
            return true;
        });
        using var expired = await Exchange(host, code.Code, "android-expired-0001");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var wrong = await Exchange(host, DevicePairing.Prefix + new string('A', 43), "android-wrong-00001");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var malformed = await Exchange(host, "not-a-code", "android-malformed-01");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var fresh = await Code(host);
        using var badPhone = await Exchange(host, fresh.Code, "../owner");
        Assert.Equal(HttpStatusCode.BadRequest, badPhone.StatusCode);
        Assert.DoesNotContain(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.Devices.Keys.ToArray()),
            id => id.Contains("expired") || id.Contains("wrong") || id.Contains("malformed"));
    }

    [Fact]
    public async Task A_paired_phone_can_only_be_paired_again_from_its_own_row()
    {
        using var host = new JarvisApiFactory();
        const string phoneId = "android-repair-0001";
        using (var first = await Exchange(host, (await Code(host)).Code, phoneId)) first.EnsureSuccessStatusCode();

        // A general code cannot take over a phone that already has an active key.
        using var takeover = await Exchange(host, (await Code(host)).Code, phoneId);
        Assert.Equal(HttpStatusCode.Conflict, takeover.StatusCode);

        var old = await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.DeviceCredentials[phoneId].SecretHash);
        using var again = await Exchange(host, (await Code(host, phoneId)).Code, "android-ignored-0001");
        again.EnsureSuccessStatusCode();
        var issued = (await again.Content.ReadFromJsonAsync<Exchanged>(LifeScenarioTests.Json))!;
        Assert.Equal(phoneId, issued.DeviceId);
        Assert.NotEqual(old, await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.DeviceCredentials[phoneId].SecretHash));
    }

    [Fact]
    public async Task Pairing_codes_need_the_owner_and_only_target_audio_companions()
    {
        using var host = new JarvisApiFactory();
        using var anonymous = await host.CreateClient().PostAsJsonAsync(CoreRoutes.Devices + DeviceEndpoints.PairingCodesSuffix, new CreatePairingCodeRequest(null));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var browser = await BrowserEnrollmentTests.Enrol(host);
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner);
        using var wrongKind = await owner.PostAsJsonAsync(CoreRoutes.Devices + DeviceEndpoints.PairingCodesSuffix, new CreatePairingCodeRequest(browser.DeviceId));
        Assert.Equal(HttpStatusCode.Conflict, wrongKind.StatusCode);
    }
}
