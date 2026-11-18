using System.Text.Json;
using Jarvis.Api;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Travel;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class RealtimeProtocolTests
{
    [Fact]
    public async Task Travel_alert_changes_wait_for_the_next_voice_window_and_never_expose_saved_route_names()
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call expected."));
        var first = new ProviderSocket(); var second = new ProviderSocket(); var providers = new WindowProviders(first, second); var clock = new WindowClock(30, frozenUtc: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<TimeProvider>(clock); s.AddSingleton<IRealtimeConnectionFactory>(providers); }));
        var store = host.Services.GetRequiredService<ITravelStore>(); var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TravelPolicy.Zone);
        var route = TravelRig.Route(local.AddMinutes(-10).ToString("HH:mm"), local.AddMinutes(40).ToString("HH:mm"), days: Enum.GetValues<DayOfWeek>());
        await store.UpdateAsync((s, _) => { s.Routes[route.Id] = route; return true; });
        var ticket = await LockedVoiceBoundaryTests.StartAsync(host.Services, locked: true, windowSeconds: 30);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = timeout.Token;
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket, continuous = true }, ct);
        Assert.Same(first, await providers.Connected.Reader.ReadAsync(ct)); Assert.Equal("ready", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        var expiry = await clock.Scheduled.Reader.ReadAsync(ct); var preparation = await clock.Scheduled.Reader.ReadAsync(ct);
        Assert.DoesNotContain("Public TfL data", Assert.Single(first.Sent, e => e.GetProperty("type").GetString() == "session.update").GetProperty("session").GetProperty("instructions").GetString());
        preparation.Fire(); Assert.Same(second, await providers.Connected.Reader.ReadAsync(ct)); Assert.Empty(second.Sent);
        await store.UpdateAsync((s, _) => { var status = new TravelStatus("jubilee", "Jubilee", 6, "Severe Delays", "Signal failure", clock.GetUtcNow());
            s.Lines["jubilee"] = new("jubilee", Confirmed: status, LastSeenAt: clock.GetUtcNow()); return true; });
        Assert.Single(first.Sent, e => e.GetProperty("type").GetString() == "session.update"); Assert.Empty(second.Sent);
        await Send(socket, new { type = "heartbeat", sequence = 1, locked = true }, ct); Assert.Equal("lease", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        expiry.Fire(); Assert.Equal("window_renewing", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        Assert.Equal("ready", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        var prompt = Assert.Single(second.Sent, e => e.GetProperty("type").GetString() == "session.update").GetProperty("session").GetProperty("instructions").GetString()!;
        Assert.Contains("jubilee: Severe Delays", prompt); Assert.DoesNotContain(route.Name, prompt);
        await Send(socket, new { type = "stop" }, ct); Assert.Equal("ended", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
    }
}
