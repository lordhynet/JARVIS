using System.Net;
using System.Net.Http.Json;
using Jarvis.Api;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed class BrowserReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Browser_enrollment_cannot_replace_an_existing_native_registration(bool revoked)
    {
        using var host = new JarvisApiFactory(); var services = host.Services;
        var request = new RegisterDeviceRequest("native-collision", "Native laptop", DeviceKind.WindowsAgent, new(true, true, false, true, []));
        var original = await services.GetRequiredService<DeviceService>().RegisterAsync(request);
        if (revoked) { await services.GetRequiredService<DeviceCredentials>().IssueAsync(request.DeviceId, default); await services.GetRequiredService<DeviceCredentials>().RevokeAsync(request.DeviceId, default); }
        using var owner = host.CreateAuthorisedClient();
        using var response = await owner.PostAsJsonAsync("/api/browser/enrollment", request with { Name = "Browser", Capabilities = new(true, true, true, false, [VoiceAccessRules.BrowserCompanionTag]) });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original.Registration), await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => System.Text.Json.JsonSerializer.Serialize(s.Devices[request.DeviceId].Registration)));
    }
    [Fact]
    public async Task Browser_cannot_heartbeat_or_renew_another_devices_audio_even_with_owner_elevation()
    {
        using var host = new JarvisApiFactory(); var credential = await BrowserEnrollmentTests.Enrol(host); using var browser = BrowserEnrollmentTests.Browser(host, credential);
        var services = host.Services; await services.GetRequiredService<DeviceService>().RegisterAsync(new("other-laptop", "Other laptop", DeviceKind.WindowsAgent, new(true, true, false, false, [])));
        var runtime = services.GetRequiredService<IRuntimeStore>(); var lease = new AudioLease(Guid.NewGuid(), "other-laptop", 123, DateTimeOffset.UtcNow.AddMinutes(1), Locked: true);
        await runtime.UpdateAsync(s => { s.AudioLease = lease; return true; });
        var before = await runtime.ReadAsync(s => s.Devices["other-laptop"]);
        var elevation = await BrowserEnrollmentTests.Elevate(browser, "device.manage"); browser.DefaultRequestHeaders.Add("X-Jarvis-Owner-Elevation", elevation.Token);
        using var heartbeat = await browser.PostAsJsonAsync("/api/devices/other-laptop/heartbeat", new { }); Assert.Equal(HttpStatusCode.Forbidden, heartbeat.StatusCode);
        using var renew = await browser.PostAsJsonAsync("/api/voice/local/renew", lease); Assert.Equal(HttpStatusCode.Forbidden, renew.StatusCode);
        Assert.Equal(lease, await runtime.ReadAsync(s => s.AudioLease)); Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), await runtime.ReadAsync(s => System.Text.Json.JsonSerializer.Serialize(s.Devices["other-laptop"])));
    }
    [Fact]
    public void Invalid_browser_migration_flag_is_refused_during_startup()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting(ConfigKeys.RequireEnrolledBrowser, "tru"));
        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient()); Assert.Contains("migration flag", error.ToString());
    }
}
