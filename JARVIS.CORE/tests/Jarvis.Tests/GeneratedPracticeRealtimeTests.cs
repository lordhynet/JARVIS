using System.Net.WebSockets;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// The realtime window offers the generated-practice tool, and Core registers its capability, only when the flag is on.
// Fixture provider socket; no paid call.
public sealed partial class RealtimeProtocolTests
{

    private static async Task<string[]> AdvertisedTools(bool enabled)
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call expected."));
        var provider = new ProviderSocket();
        using var host = factory.WithWebHostBuilder(b =>
        {
            if (enabled) b.UseSetting(PracticeGenerationOptions.ConfigKey, "true");
            b.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider));
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), timeout.Token);
        var devices = host.Services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new("test-phone", "Fixture phone", DeviceKind.AndroidCompanion, new(true, true, true, false, [])));
        await devices.VerifyAsync("test-phone"); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.Communication, "Voice fixture", SessionStatus.Active, "test-phone", null, now, now, []));
        var ticket = await host.Services.GetRequiredService<AudioOwnership>().StartAsync(new(id, "test-phone", true), timeout.Token);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket }, timeout.Token);
        Assert.Equal("ready", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        var setup = Assert.Single(provider.Sent, x => x.GetProperty("type").ValueEquals("session.update")).GetProperty("session");
        var names = setup.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(enabled, host.Services.GetRequiredService<ICapabilityCatalog>().Installed.Any(c => c.Key == CoreVoiceTools.GenerateName));
        await Send(socket, new { type = "stop" }, timeout.Token);
        return names;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generated_practice_tool_is_offered_only_when_enabled(bool enabled)
    {
        var names = await AdvertisedTools(enabled);
        Assert.Contains("core_practice_start", names);
        Assert.Equal(enabled, names.Contains(CoreVoiceTools.GenerateName));
    }

    [Fact]
    public void Window_cost_bound_covers_the_generated_practice_tool()
    {
        var withGeneration = JsonSerializer.SerializeToUtf8Bytes(CoreVoiceTools.DefinitionsFor(false, generation: true)).Length;
        var without = JsonSerializer.SerializeToUtf8Bytes(CoreVoiceTools.Definitions).Length;
        Assert.Equal(withGeneration, RealtimeWindowCost.ToolBytesFor(generation: true));
        Assert.Equal(without, RealtimeWindowCost.ToolBytesFor(generation: false));   // flag off: reservation unchanged
        Assert.True(withGeneration > without);
    }
}
