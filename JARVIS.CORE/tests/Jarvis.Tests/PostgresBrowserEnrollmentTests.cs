using System.Net;
using Jarvis.Api.Security;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Enrolled_browser_and_bound_owner_elevation_survive_restart_and_revoke_atomically()
    {
        await using var database = await TestDatabase.CreateAsync(); IssuedDeviceCredential credential; IssuedOwnerElevation elevation;
        using (var first = new PostgresApiFactory(database.ConnectionString)) {
            credential = await BrowserEnrollmentTests.Enrol(first); using var browser = BrowserEnrollmentTests.Browser(first, credential);
            elevation = await BrowserEnrollmentTests.Elevate(browser, "privacy.manage");
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var connected = BrowserEnrollmentTests.Browser(restarted, credential);
        connected.DefaultRequestHeaders.Add(BrowserOwnerAccess.ElevationHeader, elevation.Token);
        using var export = await connected.GetAsync("/api/privacy/export"); export.EnsureSuccessStatusCode();
        await restarted.Services.GetRequiredService<DeviceCredentials>().RevokeAsync(credential.DeviceId, default);
        using var revoked = await connected.GetAsync("/api/privacy/export"); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }
}
