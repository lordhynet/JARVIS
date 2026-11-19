using System.Text.Json;
using Jarvis.Api;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed partial class RealtimeProtocolTests
{
    [Fact]
    public async Task Unlock_rebuilds_personal_context_only_when_the_next_fenced_window_activates()
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call"));
        var first = new ProviderSocket(); var second = new ProviderSocket();
        var providers = new WindowProviders(first, second);
        var clock = new WindowClock(30, frozenUtc: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => {
            s.AddSingleton<TimeProvider>(clock); s.AddSingleton<IRealtimeConnectionFactory>(providers);
        }));
        var ticket = await LockedVoiceBoundaryTests.StartAsync(host.Services, locked: true, windowSeconds: 30);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var ct = timeout.Token;
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket, continuous = true }, ct);
        Assert.Same(first, await providers.Connected.Reader.ReadAsync(ct));
        var initial = await RealtimeGateway.ReadAsync(socket, 4096, ct);
        Assert.Equal("ready", initial.GetProperty("type").GetString()); Assert.True(initial.GetProperty("locked").GetBoolean());
        first.Transcript("LOCKED_INJECTION_MARKER", "locked-untrusted-turn");
        first.AssistantTranscript("LOCKED_REPLY_MARKER");
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        while (await runtime.ReadAsync(s => s.VoiceTurns.Count, ct) < 2) await Task.Delay(10, ct);
        var expiry = await clock.Scheduled.Reader.ReadAsync(ct); var preparation = await clock.Scheduled.Reader.ReadAsync(ct);
        await Send(socket, new { type = "heartbeat", sequence = 1, locked = false }, ct);
        Assert.Equal("lease", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        Assert.True((await runtime.ReadAsync(s => s.AudioLease))!.Locked);
        preparation.Fire(); Assert.Same(second, await providers.Connected.Reader.ReadAsync(ct)); Assert.Empty(second.Sent);
        expiry.Fire();
        Assert.Equal("window_renewing", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        var ready = await RealtimeGateway.ReadAsync(socket, 4096, ct); Assert.Equal(2, ready.GetProperty("window").GetInt32());
        Assert.False(ready.GetProperty("locked").GetBoolean());
        Assert.False((await runtime.ReadAsync(s => s.AudioLease))!.Locked);
        var prompt = Assert.Single(second.Sent).GetProperty("session").GetProperty("instructions").GetString()!;
        Assert.Contains("Private task marker", prompt); Assert.Contains("Private conversation marker", prompt);
        Assert.DoesNotContain("LOCKED_INJECTION_MARKER", prompt); Assert.DoesNotContain("LOCKED_REPLY_MARKER", prompt);
        Assert.All(await runtime.ReadAsync(s => s.VoiceTurns.Values.ToArray(), ct), turn => Assert.True(turn.Restricted));
        using var dashboard = host.CreateClient(); dashboard.DefaultRequestHeaders.Add(CoreHeaders.ApiKey, JarvisApiFactory.TestKey);
        var audit = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonElement>(dashboard, $"/api/sessions/{ticket.Lease.SessionId}", ct);
        Assert.Contains(audit.GetProperty("messages").EnumerateArray(), m => m.GetProperty("text").GetString() == "LOCKED_INJECTION_MARKER" && m.GetProperty("restricted").GetBoolean());
        await Send(socket, new { type = "stop" }, ct);
        Assert.Equal("ended", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Locked_window_omits_saved_conversation_task_and_practice_context_and_personal_tools()
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call"));
        var provider = new ProviderSocket();
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider)));
        var ticket = await LockedVoiceBoundaryTests.StartAsync(host.Services);
        host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.VoicePersona] = "Private persona marker";
        await host.Services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => {
            var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            s.Learning[id] = new(id, LearningDomain.French, "Private learning prompt marker", "Private learning answer marker", 1, 1, now.AddDays(-1), now);
            return true;
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), timeout.Token);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket }, timeout.Token);
        Assert.Equal("ready", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        var setup = Assert.Single(provider.Sent, x => x.GetProperty("type").ValueEquals("session.update")).GetProperty("session");
        var prompt = setup.GetProperty("instructions").GetString()!;
        Assert.DoesNotContain("Private conversation marker", prompt); Assert.DoesNotContain("Private task marker", prompt);
        Assert.DoesNotContain("Private persona marker", prompt); Assert.DoesNotContain("Private learning", prompt);
        Assert.Contains("device is locked", prompt);
        Assert.Equal(new[] { CoreHomeVoiceTool.Name, CoreVoiceTools.TravelName, CoreWindowsVoiceTool.Name }, setup.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order().ToArray());
        await Send(socket, new { type = "stop" }, timeout.Token);
        Assert.Equal("ended", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Newly_locked_or_missing_Windows_report_stops_personal_stream_with_specific_reason(bool explicitReport)
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call"));
        var provider = new ProviderSocket();
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider)));
        var ticket = await LockedVoiceBoundaryTests.StartAsync(host.Services, locked: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), timeout.Token);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket }, timeout.Token);
        Assert.Equal("ready", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        await Send(socket, explicitReport ? (object)new { type = "heartbeat", sequence = 1, locked = true } : new { type = "heartbeat", sequence = 1 }, timeout.Token);
        var error = await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal(VoiceEndReasons.DeviceLocked, error.GetProperty("code").GetString());
        var ended = await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token);
        Assert.Equal(VoiceEndReasons.DeviceLocked, ended.GetProperty("reason").GetString());
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        while (await runtime.ReadAsync(s => s.AudioLease) is not null) await Task.Delay(20, timeout.Token);
        Assert.Null(await runtime.ReadAsync(s => s.AudioLease));
        Assert.Equal(1, provider.Connections);
    }
}
