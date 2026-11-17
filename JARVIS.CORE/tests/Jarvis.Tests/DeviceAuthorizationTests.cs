using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class DeviceAuthorizationTests
{
    [Fact]
    public async Task Companion_can_acknowledge_own_stopped_lease_but_not_another_devices_lease()
    {
        using var host = new JarvisApiFactory(); var issued = await Enrol(host); using var phone = Companion(host, issued);
        var services = host.Services; var devices = services.GetRequiredService<DeviceService>();
        await devices.VerifyAsync("phone"); var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        await services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Fixture", SessionStatus.Active, "phone", null, now, now, []));
        var audio = services.GetRequiredService<AudioOwnership>(); var ticket = await audio.StartAsync(new(id, "phone", true), default);
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { AudioFencing.Revoke(s); return true; });
        using var wrong = await phone.PostAsJsonAsync("/api/voice/local/release", ticket.Lease with { DeviceId = "another" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        using var own = await phone.PostAsJsonAsync("/api/voice/local/release", ticket.Lease); own.EnsureSuccessStatusCode();
        Assert.True(await own.Content.ReadFromJsonAsync<bool>());
        Assert.Null(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AudioDrainUntil));
    }
    private static async Task<IssuedDeviceCredential> Enrol(JarvisApiFactory host)
    {
        await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new("phone", "Audio companion", DeviceKind.AndroidCompanion, new(true, true, false, false, ["audio-only"])));
        using var owner = host.CreateAuthorisedClient();
        var response = await owner.PostAsJsonAsync("/api/devices/phone/credential", new { }); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuedDeviceCredential>())!;
    }
    private static HttpClient Companion(JarvisApiFactory host, IssuedDeviceCredential issued)
    {
        var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", issued.Secret); return client;
    }

    [Theory]
    [InlineData("GET", "/api/privacy/export")]
    [InlineData("POST", "/api/privacy/erase")]
    [InlineData("POST", "/api/privacy/reopen")]
    [InlineData("POST", "/api/privacy/retention/apply")]
    [InlineData("POST", "/api/devices/phone/verify")]
    [InlineData("POST", "/api/devices")]
    [InlineData("POST", "/api/devices/victim/heartbeat")]
    [InlineData("POST", "/api/tools/home")]
    [InlineData("PUT", "/api/agents/controls")]
    [InlineData("GET", "/api/sessions")]
    [InlineData("GET", "/hubs/status/negotiate")]
    [InlineData("POST", "/api/devices/phone/credential")]
    public async Task Companion_is_denied_every_non_companion_surface(string method, string path)
    {
        using var host = new JarvisApiFactory(); var issued = await Enrol(host);
        using var client = Companion(host, issued);
        // Even an owner header cannot turn a companion actor into a browser administrator.
        TestOwnerManagement.Authorize(client);
        using var request = new HttpRequestMessage(new(method), path) { Content = JsonContent.Create(new { }) };
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Device_is_bound_to_credential_and_only_its_own_registration_is_returned()
    {
        using var host = new JarvisApiFactory(); var issued = await Enrol(host);
        await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new("victim", "Other device", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        using var client = Companion(host, issued);
        using var status = await client.GetAsync("/api/privacy/status"); status.EnsureSuccessStatusCode();
        var devices = (await client.GetFromJsonAsync<DeviceView[]>("/api/devices", LifeScenarioTests.Json))!;
        Assert.Equal("phone", Assert.Single(devices).Device.Registration.DeviceId);
        using var heartbeat = await client.PostAsJsonAsync("/api/devices/phone/heartbeat", new { }); heartbeat.EnsureSuccessStatusCode();
        using var spoof = await client.PostAsJsonAsync("/api/voice/prepare", new CompanionVoiceRequest(Guid.NewGuid(), "victim", true));
        Assert.Equal(HttpStatusCode.Forbidden, spoof.StatusCode);
        using var spoofTicket = await client.PostAsJsonAsync("/api/voice/tickets", new VoiceStartRequest(Guid.NewGuid(), "victim", true));
        Assert.Equal(HttpStatusCode.Forbidden, spoofTicket.StatusCode);
        Assert.Empty(await host.Services.GetRequiredService<ISessionStore>().ListAsync());
        using var owner = host.CreateAuthorisedClient();
        var export = await owner.GetStringAsync("/api/privacy/export");
        Assert.DoesNotContain(issued.Secret, export); Assert.DoesNotContain(PrivacyData.Hash(issued.Secret), export);
        using var overwrite = await owner.PostAsJsonAsync("/api/devices", new RegisterDeviceRequest("phone", "Imposter", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        Assert.Equal(HttpStatusCode.Conflict, overwrite.StatusCode);
    }

    [Fact]
    public async Task Paired_device_can_discover_code_installed_descriptors_without_executing_or_granting_them()
    {
        using var host = new JarvisApiFactory(); var issued = await Enrol(host); using var phone = Companion(host, issued);
        var descriptors = await phone.GetFromJsonAsync<DeviceCapabilityDescriptor[]>("/api/device-capabilities", LifeScenarioTests.Json);
        Assert.Equal(7, descriptors!.Length);
        var descriptor = Assert.Single(descriptors, x => x.Id == BuiltInDeviceCapabilities.HomeAssistantTurn);
        Assert.Equal("homeassistant.turn", descriptor.Id);
        Assert.Equal("1.0.0", descriptor.Version);
        Assert.Equal(DeviceCapabilityRisk.Reversible, descriptor.Risk);
        Assert.Equal(DeviceConfirmationPolicy.Owner, descriptor.ConfirmationPolicy);
        Assert.Empty(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.Tools));
    }

    [Fact]
    public async Task Scoped_device_credential_can_claim_its_own_wake_candidate_but_not_another_device()
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("Wake arbitration must not call a provider."));
        var services = factory.Services; var deviceId = "wake-credential-phone";
        var devices = services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new(deviceId, "Wake fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, ["audio-only"])));
        await devices.VerifyAsync(deviceId);
        using var owner = factory.Client();
        using var issuedResponse = await owner.PostAsJsonAsync($"/api/devices/{deviceId}/credential", new { });
        issuedResponse.EnsureSuccessStatusCode();
        var issued = (await issuedResponse.Content.ReadFromJsonAsync<IssuedDeviceCredential>())!;
        using var companion = factory.CreateClient(); companion.DefaultRequestHeaders.Add("X-Jarvis-Key", issued.Secret);

        using var ownResponse = await companion.PostAsJsonAsync("/api/voice/wake-candidates",
            new WakeCandidateRequest(Guid.NewGuid(), deviceId, true));
        ownResponse.EnsureSuccessStatusCode();
        var winner = (await ownResponse.Content.ReadFromJsonAsync<WakeCandidateResponse>())!;
        Assert.Equal(deviceId, winner.Voice.Lease.DeviceId);
        Assert.NotEmpty(winner.Voice.Ticket);

        using var spoofedResponse = await companion.PostAsJsonAsync("/api/voice/wake-candidates",
            new WakeCandidateRequest(Guid.NewGuid(), "another-device", true));
        Assert.Equal(HttpStatusCode.Forbidden, spoofedResponse.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/privacy/export")]
    [InlineData("POST", "/api/privacy/erase")]
    [InlineData("POST", "/api/privacy/reopen")]
    [InlineData("POST", "/api/privacy/retention/apply")]
    [InlineData("POST", "/api/devices/phone/verify")]
    [InlineData("POST", "/api/devices/phone/credential")]
    [InlineData("POST", "/api/tools/00000000-0000-0000-0000-000000000001/confirm")]
    public async Task Sensitive_operations_require_separate_header_and_reject_query_owner_keys(string method, string path)
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        using var request = new HttpRequestMessage(new(method), path + "?X-Jarvis-Owner-Key=" + TestOwnerManagement.Key) { Content = JsonContent.Create(new { }) };
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Owner_step_up_cannot_be_bypassed_with_route_casing_or_trailing_slashes()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        using var response = await client.GetAsync("/API/PRIVACY/EXPORT/"); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Revocation_invalidates_rest_tickets_live_lease_and_stale_inflight_credentials()
    {
        using var host = new JarvisApiFactory(); var issued = await Enrol(host); var services = host.Services;
        var credentials = services.GetRequiredService<DeviceCredentials>(); var actor = (await credentials.AuthenticateAsync(issued.Secret, default))!;
        await services.GetRequiredService<DeviceService>().VerifyAsync("phone");
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Fixture", SessionStatus.Active, "phone", null, now, now, []));
        var ownership = services.GetRequiredService<AudioOwnership>();
        var ticket = await ownership.StartAsync(new(id, "phone", true), default);
        using var owner = host.CreateAuthorisedClient(); using var revoked = await owner.DeleteAsync("/api/devices/phone/credential"); revoked.EnsureSuccessStatusCode();
        Assert.False(await ownership.CurrentAsync(ticket.Lease, default));
        await Assert.ThrowsAsync<DomainException>(() => ownership.ConsumeAsync(ticket.Ticket, default));
        using var client = Companion(host, issued); using var denied = await client.GetAsync("/api/privacy/status"); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var rotated = await credentials.IssueAsync("phone", default);
        using (DeviceCredentialContext.Enter(actor))
            await Assert.ThrowsAsync<DomainException>(() => ownership.StartAsync(new(id, "phone", true), default));
        using var current = Companion(host, rotated); using var accepted = await current.GetAsync("/api/privacy/status"); accepted.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Erasure_invalidates_companion_authentication_instead_of_looking_like_a_network_outage()
    {
        using var host = new JarvisApiFactory(); var issued = await Enrol(host);
        using var owner = host.CreateAuthorisedClient();
        var status = (await owner.GetFromJsonAsync<PrivacyStatus>("/api/privacy/status"))!;
        using var erased = await owner.PostAsJsonAsync("/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase));
        erased.EnsureSuccessStatusCode();
        using var phone = Companion(host, issued); using var response = await phone.GetAsync("/api/privacy/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"coreErased\":true", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.Contains("X-Jarvis-Data-Epoch"));
    }
}
