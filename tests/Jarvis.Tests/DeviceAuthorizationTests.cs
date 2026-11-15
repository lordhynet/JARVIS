using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class DeviceAuthorizationTests
{
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
