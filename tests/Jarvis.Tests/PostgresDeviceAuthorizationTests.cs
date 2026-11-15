using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Device_credentials_survive_restart_and_revocation_is_seen_by_another_host()
    {
        await using var database = await TestDatabase.CreateAsync(); IssuedDeviceCredential issued;
        using (var first = new PostgresApiFactory(database.ConnectionString))
        {
            using var owner = first.AuthorisedClient();
            await first.Services.GetRequiredService<DeviceService>().RegisterAsync(new("phone", "Fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, ["audio-only"])));
            using var response = await owner.PostAsJsonAsync("/api/devices/phone/credential", new { }); response.EnsureSuccessStatusCode();
            issued = (await response.Content.ReadFromJsonAsync<IssuedDeviceCredential>())!;
        }
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var client = a.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", issued.Secret);
        using var accepted = await client.GetAsync("/api/privacy/status"); accepted.EnsureSuccessStatusCode();
        using var ownerB = b.AuthorisedClient(); using var revoked = await ownerB.DeleteAsync("/api/devices/phone/credential"); revoked.EnsureSuccessStatusCode();
        using var denied = await client.GetAsync("/api/privacy/status"); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var export = await ownerB.GetAsync("/api/privacy/export"); export.EnsureSuccessStatusCode();
        var text = await export.Content.ReadAsStringAsync(); Assert.DoesNotContain(PrivacyData.Hash(issued.Secret), text);
    }
}
