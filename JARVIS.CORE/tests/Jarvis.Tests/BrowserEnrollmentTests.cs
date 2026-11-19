using System.Net;
using System.Net.Http.Json;
using Jarvis.Api;
using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Jarvis.Tests;

public sealed class BrowserEnrollmentTests
{
    internal static async Task<IssuedDeviceCredential> Enrol(WebApplicationFactory<Program> host, string? id = null)
    {
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner);
        using var response = await owner.PostAsJsonAsync(BrowserOwnerAccess.EnrollmentRoute,
            new RegisterDeviceRequest(id ?? "browser-" + Guid.NewGuid().ToString("N"), "Browser fixture", DeviceKind.WindowsAgent, new(true, true, true, false, [VoiceAccessRules.BrowserCompanionTag])));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<IssuedDeviceCredential>())!;
    }
    internal static HttpClient Browser(WebApplicationFactory<Program> host, IssuedDeviceCredential credential)
    {
        var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", credential.Secret); return client;
    }
    internal static async Task<IssuedOwnerElevation> Elevate(HttpClient browser, params string[] scopes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BrowserOwnerAccess.ElevationRoute) { Content = JsonContent.Create(new OwnerElevationRequest(scopes)) };
        request.Headers.Add("X-Jarvis-Owner-Key", TestOwnerManagement.Key);
        using var response = await browser.SendAsync(request); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<IssuedOwnerElevation>())!;
    }
    [Fact]
    public async Task Legacy_is_accepted_by_default_and_refused_only_after_the_migration_flag()
    {
        using var host = new JarvisApiFactory(); var credential = await Enrol(host); using var browser = Browser(host, credential); using var legacy = host.CreateAuthorisedClient();
        using var before = await legacy.GetAsync("/api/sessions"); Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.RequireEnrolledBrowser] = "true";
        using var rejected = await legacy.GetAsync("/api/sessions"); Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var accepted = await browser.GetAsync("/api/sessions"); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var devices = await browser.GetFromJsonAsync<DeviceView[]>("/api/devices", LifeScenarioTests.Json); Assert.Contains(devices!, d => d.Device.Registration.DeviceId == credential.DeviceId);
        using var impersonation = await browser.PostAsJsonAsync("/api/voice/prepare", new CompanionVoiceRequest(Guid.NewGuid(), "another-device", true));
        Assert.Equal(HttpStatusCode.Forbidden, impersonation.StatusCode);
        host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.RequireEnrolledBrowser] = "false";
        using var restored = await legacy.GetAsync("/api/sessions"); Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }
    [Fact]
    public async Task Browser_elevation_expires_and_raw_owner_header_cannot_replace_it()
    {
        var clock = new BrowserClock(); using var root = new JarvisApiFactory();
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock)));
        var credential = await Enrol(host); using var browser = Browser(host, credential); TestOwnerManagement.Authorize(browser);
        using var raw = await browser.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, raw.StatusCode);
        var elevation = await Elevate(browser, "privacy.manage"); browser.DefaultRequestHeaders.Remove("X-Jarvis-Owner-Key");
        Assert.Equal(clock.Now.AddMinutes(5), elevation.ExpiresAt); browser.DefaultRequestHeaders.Add(BrowserOwnerAccess.ElevationHeader, elevation.Token);
        using var exported = await browser.GetAsync("/api/privacy/export"); exported.EnsureSuccessStatusCode();
        var text = await exported.Content.ReadAsStringAsync(); Assert.DoesNotContain(credential.Secret, text); Assert.DoesNotContain(elevation.Token, text); Assert.DoesNotContain(PrivacyData.Hash(elevation.Token), text);
        using var otherScope = await browser.GetAsync("/api/device-grants"); Assert.Equal(HttpStatusCode.Forbidden, otherScope.StatusCode);
        clock.Now = elevation.ExpiresAt;
        using var expired = await browser.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        using var ordinary = await browser.GetAsync("/api/sessions"); ordinary.EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task Elevation_is_bound_to_the_browser_and_owner_credential_and_unknown_scopes_are_refused()
    {
        using var host = new JarvisApiFactory(); var a = await Enrol(host); var b = await Enrol(host);
        using var first = Browser(host, a); using var second = Browser(host, b); var elevation = await Elevate(first, "privacy.manage");
        second.DefaultRequestHeaders.Add(BrowserOwnerAccess.ElevationHeader, elevation.Token);
        using var stolen = await second.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);
        first.DefaultRequestHeaders.Add(BrowserOwnerAccess.ElevationHeader, elevation.Token);
        using var invalidScopeRequest = new HttpRequestMessage(HttpMethod.Post, BrowserOwnerAccess.ElevationRoute) { Content = JsonContent.Create(new OwnerElevationRequest(["*"])) };
        invalidScopeRequest.Headers.Add("X-Jarvis-Owner-Key", TestOwnerManagement.Key);
        using var invalid = await first.SendAsync(invalidScopeRequest); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.OwnerKey] = "rotated-test-owner-credential-for-browser-elevation";
        using var rotated = await first.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, rotated.StatusCode);
    }
    [Fact]
    public async Task Native_browser_metadata_does_not_grant_owner_and_revocation_clears_browser_authority()
    {
        using var host = new JarvisApiFactory(); var services = host.Services;
        await services.GetRequiredService<DeviceService>().RegisterAsync(new("tagged-native", "Tagged fixture", DeviceKind.WindowsAgent, new(true, true, true, false, [VoiceAccessRules.BrowserCompanionTag])));
        var native = await services.GetRequiredService<DeviceCredentials>().IssueAsync("tagged-native", default); using var companion = Browser(host, native); TestOwnerManagement.Authorize(companion);
        using var denied = await companion.GetAsync("/api/sessions"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var deniedElevation = await companion.PostAsJsonAsync(BrowserOwnerAccess.ElevationRoute, new OwnerElevationRequest(["privacy.manage"])); Assert.Equal(HttpStatusCode.Forbidden, deniedElevation.StatusCode);
        var credential = await Enrol(host); using var browser = Browser(host, credential); var elevation = await Elevate(browser, "privacy.manage");
        browser.DefaultRequestHeaders.Add(BrowserOwnerAccess.ElevationHeader, elevation.Token);
        await services.GetRequiredService<DeviceCredentials>().RevokeAsync(credential.DeviceId, default);
        using var revoked = await browser.GetAsync("/api/sessions"); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        var replacement = await Enrol(host, credential.DeviceId); using var reconnect = Browser(host, replacement);
        reconnect.DefaultRequestHeaders.Add(BrowserOwnerAccess.ElevationHeader, elevation.Token);
        using var stale = await reconnect.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
    }
    [Fact]
    public async Task Legacy_hub_ticket_respects_the_migration_flag_and_browser_tickets_respect_revocation()
    {
        using var host = new JarvisApiFactory(); var credential = await Enrol(host); using var legacy = host.CreateAuthorisedClient(); using var browser = Browser(host, credential);
        async Task<string> Ticket(HttpClient client) {
            using var response = await client.PostAsJsonAsync("/api/hubs/ticket", new { }); response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("ticket").GetString()!;
        }
        var old = await Ticket(legacy); host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.RequireEnrolledBrowser] = "true";
        var tickets = host.Services.GetRequiredService<HubTickets>(); await Assert.ThrowsAsync<DomainException>(() => tickets.ConsumeAsync(old, default));
        var current = await Ticket(browser); var identity = await tickets.ConsumeIdentityAsync(current, default);
        Assert.True(identity.Actor.IsOwnerBrowser); Assert.Equal(credential.DeviceId, identity.Actor.DeviceId); Assert.Equal(PrivacyData.Hash(credential.Secret), identity.Fingerprint);
        var pending = await Ticket(browser); await host.Services.GetRequiredService<DeviceCredentials>().RevokeAsync(credential.DeviceId, default);
        await Assert.ThrowsAsync<DomainException>(() => tickets.ConsumeAsync(pending, default));
    }
    [Fact]
    public async Task Erasure_and_reopen_do_not_restore_old_browser_or_elevation_authority()
    {
        using var host = new JarvisApiFactory(); var credential = await Enrol(host); using var browser = Browser(host, credential);
        var elevation = await Elevate(browser, "privacy.manage"); browser.DefaultRequestHeaders.Add(BrowserOwnerAccess.ElevationHeader, elevation.Token);
        using var owner = host.CreateAuthorisedClient(); var status = (await owner.GetFromJsonAsync<PrivacyStatus>("/api/privacy/status"))!;
        using var erased = await browser.PostAsJsonAsync("/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase)); erased.EnsureSuccessStatusCode();
        var erasedStatus = (await erased.Content.ReadFromJsonAsync<PrivacyStatus>())!;
        using var unchangedKey = await owner.PostAsJsonAsync("/api/privacy/reopen", new ReopenCoreRequest(erasedStatus.Revision, PrivacyData.ReopenPhrase)); Assert.Equal(HttpStatusCode.Conflict, unchangedKey.StatusCode);
        const string rotated = "rotated-test-core-key-for-browser-recovery"; host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.CoreApiKey] = rotated;
        using var recovery = host.CreateClient(); recovery.DefaultRequestHeaders.Add("X-Jarvis-Key", rotated); TestOwnerManagement.Authorize(recovery);
        using var reopened = await recovery.PostAsJsonAsync("/api/privacy/reopen", new ReopenCoreRequest(erasedStatus.Revision, PrivacyData.ReopenPhrase)); reopened.EnsureSuccessStatusCode();
        using var stale = await browser.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }
    [Fact]
    public async Task Enrolled_browser_receives_its_status_events_with_legacy_authentication_disabled()
    {
        using var host = new JarvisApiFactory(); var credential = await Enrol(host); using var browser = Browser(host, credential);
        host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.RequireEnrolledBrowser] = "true";
        await using var connection = new HubConnectionBuilder().WithUrl(new Uri(host.Server.BaseAddress, "/hubs/status"), options => {
            options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler(); options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult<string?>(credential.Secret);
        }).Build();
        var received = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<System.Text.Json.JsonElement>(HubMethods.SessionEvent, evt => received.TrySetResult(evt.GetProperty("sessionId").GetGuid()));
        await connection.StartAsync(); Assert.Equal("pong", await connection.InvokeAsync<string>("Ping"));
        using var created = await browser.PostAsJsonAsync("/api/sessions", new CreateSessionRequest(ActivityType.General, "Enrolled browser event", null)); created.EnsureSuccessStatusCode();
        Assert.NotEqual(Guid.Empty, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
    private sealed class BrowserClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
