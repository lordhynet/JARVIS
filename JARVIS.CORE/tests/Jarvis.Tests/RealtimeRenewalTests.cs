using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Jarvis.Api;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class RealtimeProtocolTests
{
    [Theory]
    [InlineData(30, 4)]
    [InlineData(60, 8)]
    [InlineData(61, 9)]
    [InlineData(300, 40)]
    [InlineData(600, 80)]
    public void Voice_window_scales_the_audio_and_response_cap(int seconds, int turns)
    {
        var window = new VoiceWindowLimits(seconds);
        Assert.Equal(turns, window.MaxTurns);
        Assert.Equal(48000 * seconds, window.MaxInputBytes);
    }

    [Fact]
    public void A_ticket_written_before_window_configuration_keeps_its_original_duration()
    {
        var ticket = new VoiceTicketRecord("fixture-hash", new AudioLease(Guid.NewGuid(), "phone", 1, DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow, 300);
        var previous = JsonSerializer.SerializeToNode(ticket)!.AsObject();
        Assert.True(previous.Remove(nameof(VoiceTicketRecord.WindowSeconds)));
        Assert.Equal(60, previous.Deserialize<VoiceTicketRecord>()!.WindowSeconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Voice_prepares_successor_in_background_and_dispatches_only_after_handoff(bool continueVoice)
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call expected."));
        var first = new ProviderSocket { EmitAudio = true };
        var second = new ProviderSocket();
        var providers = new WindowProviders(first, second);
        var clock = new WindowClock();
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConfigKeys.VoiceWindowSeconds, "30");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IRealtimeConnectionFactory>(providers);
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        const string device = "renewal-phone";
        var devices = host.Services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new(device, "Renewal fixture", DeviceKind.AndroidCompanion, new(true, true, true, false, [])));
        await devices.VerifyAsync(device);
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        await host.Services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Voice continuity fixture",
            SessionStatus.Active, device, null, now, now, []));
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        using var response = await http.PostAsJsonAsync("/api/voice/tickets", new VoiceStartRequest(id, device, true), ct);
        response.EnsureSuccessStatusCode();
        var issued = (await response.Content.ReadFromJsonAsync<VoiceTicket>(ct))!;
        Assert.Equal(30, issued.WindowSeconds);
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = issued.Ticket, continuous = true }, ct);
        Assert.Same(first, await providers.Connected.Reader.ReadAsync(ct));
        var initialReady = await RealtimeGateway.ReadAsync(socket, 4096, ct);
        Assert.Equal("ready", initialReady.GetProperty("type").GetString());
        Assert.Equal(1, initialReady.GetProperty("window").GetInt32());
        Assert.Equal(30, initialReady.GetProperty("windowSeconds").GetInt32());
        Assert.Equal("audio", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        Assert.Equal("audio", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        Assert.Equal("transcript", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        var expiry = await clock.Scheduled.Reader.ReadAsync(ct);
        var preparation = await clock.Scheduled.Reader.ReadAsync(ct);
        Assert.Equal(TimeSpan.FromSeconds(30), expiry.Delay);
        Assert.Equal(TimeSpan.FromSeconds(20), preparation.Delay);
        preparation.Fire();
        Assert.Same(second, await providers.Connected.Reader.ReadAsync(ct));
        Assert.Empty(second.Sent);
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        var reserved = await runtime.ReadAsync(s => s.UsageReservations.Values.ToArray(), ct);
        Assert.Equal(2, reserved.Length);
        Assert.All(reserved, item => Assert.Null(item.ActualUsd));

        if (continueVoice)
        {
            const string latest = "This turn arrived after the next connection was prepared.";
            first.Transcript(latest);
            Assert.Equal(latest, (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("text").GetString());
            expiry.Fire();
            Assert.Equal("window_renewing", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
            var nextReady = await RealtimeGateway.ReadAsync(socket, 4096, ct);
            Assert.Equal("ready", nextReady.GetProperty("type").GetString());
            Assert.Equal(2, nextReady.GetProperty("window").GetInt32());
            Assert.Equal(issued.Lease.Fence, nextReady.GetProperty("fence").GetInt64());
            var configure = Assert.Single(second.Sent);
            var instructions = configure.GetProperty("session").GetProperty("instructions").GetString()!;
            Assert.Contains("never re-greet", instructions);
            Assert.Contains(latest, instructions);

            await Send(socket, new { type = "audio", window = 1, audio = "AAA=" }, ct);
            await Send(socket, new { type = "heartbeat", sequence = 1 }, ct);
            Assert.Equal("lease", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
            Assert.False(second.AudioUploads.Reader.TryRead(out _));
            await Send(socket, new { type = "audio", window = 2, audio = "AAA=" }, ct);
            Assert.True(await second.AudioUploads.Reader.ReadAsync(ct));
        }

        await Send(socket, new { type = "stop" }, ct);
        Assert.Equal("stopped", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("reason").GetString());
        Assert.Null(await runtime.ReadAudioLeaseAsync(ct));
        var final = await runtime.ReadAsync(s => s.UsageReservations.Values.ToArray(), ct);
        Assert.Equal(2, final.Length);
        Assert.All(final, item =>
        {
            Assert.NotNull(item.ActualUsd);
            Assert.False(item.ReconciliationRequired);
        });
        if (!continueVoice)
        {
            Assert.Empty(second.Sent);
            Assert.Contains(final, item => item.ActualUsd == 0);
            Assert.Equal(WebSocketState.Aborted, second.State);
        }
    }

    private sealed class WindowProviders(params ProviderSocket[] sockets) : IRealtimeConnectionFactory
    {
        private int opened;
        public readonly Channel<ProviderSocket> Connected = Channel.CreateUnbounded<ProviderSocket>();
        public Task<WebSocket> ConnectAsync(string model, CancellationToken ct)
        {
            var socket = sockets[Interlocked.Increment(ref opened) - 1];
            Connected.Writer.TryWrite(socket);
            return Task.FromResult<WebSocket>(socket);
        }
    }

    private sealed class WindowClock : TimeProvider
    {
        public readonly Channel<WindowTimer> Scheduled = Channel.CreateUnbounded<WindowTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != TimeSpan.FromSeconds(20) && dueTime != TimeSpan.FromSeconds(30))
                return base.CreateTimer(callback, state, dueTime, period);
            var timer = new WindowTimer(callback, state, dueTime);
            Scheduled.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class WindowTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        public TimeSpan Delay => delay;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
