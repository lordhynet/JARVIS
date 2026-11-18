using System.Net.WebSockets;
using Jarvis.Api;
using Jarvis.Api.Voice;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class RealtimeProtocolTests
{
    [Fact]
    public async Task Spoken_goodbye_ends_the_conversation_after_the_farewell_and_grace()
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call expected."));
        var provider = new ProviderSocket();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await OpenClosingFixtureAsync(host.Services, host.Server, "goodbye-phone", timeout.Token);

        var said = DateTimeOffset.UtcNow;
        provider.Transcript("Thanks JARVIS, that's all from me, bye!", "goodbye-user");
        var ended = await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token);
        Assert.Equal("ended", ended.GetProperty("type").GetString());
        Assert.Equal(VoiceEndReasons.OwnerClosed, ended.GetProperty("reason").GetString());
        Assert.True(DateTimeOffset.UtcNow - said >= TimeSpan.FromSeconds(2.5), "The farewell grace period was skipped.");
        // The goodbye reuses the reply already requested for that speech: no extra paid response is created.
        Assert.Equal(1, provider.Sent.Count(x => x.GetProperty("type").ValueEquals("response.create")));
        var usage = Assert.Single(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations.Values.ToArray()));
        Assert.NotNull(usage.ActualUsd); Assert.False(usage.ReconciliationRequired);
    }

    [Fact]
    public async Task Speaking_again_during_the_grace_period_keeps_the_conversation_open()
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call expected."));
        var provider = new ProviderSocket();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await OpenClosingFixtureAsync(host.Services, host.Server, "one-more-phone", timeout.Token);

        provider.Transcript("Thanks JARVIS", "thanks-user");
        provider.SpeechStarted("one-more-thing");
        Assert.Equal("interrupt", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        await Task.Delay(TimeSpan.FromSeconds(4), timeout.Token);
        await Send(socket, new { type = "heartbeat", sequence = 1 }, timeout.Token);
        var lease = await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token);
        Assert.True(lease.GetProperty("type").ValueEquals("lease"), lease.GetRawText());
        await Send(socket, new { type = "stop" }, timeout.Token);
        var ended = await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token);
        Assert.Equal("stopped", ended.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_request_that_starts_with_thanks_does_not_end_the_conversation()
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call expected."));
        var provider = new ProviderSocket();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await OpenClosingFixtureAsync(host.Services, host.Server, "request-phone", timeout.Token);

        provider.Transcript("Thank you, and what about Thursday?", "request-user");
        await Task.Delay(TimeSpan.FromSeconds(4), timeout.Token);
        await Send(socket, new { type = "heartbeat", sequence = 1 }, timeout.Token);
        Assert.Equal("lease", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        await Send(socket, new { type = "stop" }, timeout.Token);
        Assert.Equal("stopped", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("reason").GetString());
    }

    private static async Task<WebSocket> OpenClosingFixtureAsync(IServiceProvider services, TestServer server, string deviceId, CancellationToken ct)
    {
        var devices = services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new(deviceId, "Goodbye fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, [])), ct);
        await devices.VerifyAsync(deviceId, ct);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Voice", SessionStatus.Active, deviceId, null, now, now, []), ct);
        var ticket = await services.GetRequiredService<AudioOwnership>().StartAsync(new(id, deviceId, true), ct);
        var socket = await server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket }, ct);
        Assert.Equal("ready", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        return socket;
    }
}
