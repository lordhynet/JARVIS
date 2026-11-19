using Jarvis.Api;
using Jarvis.Api.Voice;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Intelligence;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class RealtimeProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_greeting_first_window_waits_for_speech_and_keeps_greeting_out_of_history(bool localGreeting)
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call."));
        var provider = new ProviderSocket();
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider)));
        var ticket = await LockedVoiceBoundaryTests.StartAsync(host.Services, locked: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var ct = timeout.Token;
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket, localGreeting }, ct);
        var ready = await RealtimeGateway.ReadAsync(socket, 4096, ct); Assert.Equal("ready", ready.GetProperty("type").GetString());
        provider.Transcript("User speech after greeting", "first-real-user-turn");
        while (true) {
            bool captured; lock (provider.Sent) captured = provider.Sent.Any(m => m.GetProperty("type").ValueEquals("conversation.item.create"));
            if (captured) break; await Task.Delay(10, ct);
        }
        System.Text.Json.JsonElement[] sent; lock (provider.Sent) sent = provider.Sent.ToArray();
        Assert.Equal(localGreeting ? 0 : 1, sent.Count(m => m.GetProperty("type").ValueEquals("response.create")));
        if (localGreeting) Assert.Contains("Wait for speech and never re-greet", sent[0].GetProperty("session").GetProperty("instructions").GetString());
        Assert.DoesNotContain(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.VoiceTurns.Values.ToArray(), ct), turn => turn.Text.StartsWith("Hi,", StringComparison.Ordinal));
        await Send(socket, new { type = "stop" }, ct); Assert.Equal("ended", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
    }
}
