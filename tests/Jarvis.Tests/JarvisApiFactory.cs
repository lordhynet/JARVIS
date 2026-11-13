using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Jarvis.Tests;

/// <summary>Boots the real API in memory with a known test key.</summary>
public sealed class JarvisApiFactory : WebApplicationFactory<Program>
{
    public const string TestKey = "test-key-123";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jarvis:ApiKey", TestKey);
        builder.UseSetting("Jarvis:StorageProvider", "InMemory");
    }

    public HttpClient CreateAuthorisedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", TestKey);
        return client;
    }
}
