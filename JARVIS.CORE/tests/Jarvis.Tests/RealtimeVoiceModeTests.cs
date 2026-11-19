using System.Net.WebSockets;
using System.Text.Json;
using Jarvis.Api;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class RealtimeProtocolTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Confirmed_voice_mode_replaces_only_the_next_window_and_never_silently_downgrades(bool disabled, bool capReached)
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider calls expected."));
        var first = new ProviderSocket();
        var standby = new ProviderSocket();
        var serious = new ProviderSocket();
        var providers = new WindowProviders(first, standby, serious, new ProviderSocket());
        var clock = new WindowClock(30, frozenUtc: true);
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConfigKeys.VoiceWindowSeconds, "30");
            builder.UseSetting(ConfigKeys.VoiceSeriousDailyGbp, capReached ? "0.001" : "1");
            if (!disabled)
            {
                var prefix = ConfigKeys.VoiceModes + VoiceMode.Serious + ":";
                builder.UseSetting(prefix + ConfigKeys.Enabled, "true");
                VoiceModePolicyTests.Seed((key, value) => builder.UseSetting(key, value), prefix, ConfigKeys.SeriousRealtimeModel);
            }
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IRealtimeConnectionFactory>(providers);
            });
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        const string device = "voice-mode-fixture";
        var devices = host.Services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new(device, "Voice modes", DeviceKind.AndroidCompanion, new(true, true, true, false, [])));
        await devices.VerifyAsync(device);
        var sessions = host.Services.GetRequiredService<ISessionStore>();
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        var ownership = host.Services.GetRequiredService<AudioOwnership>();
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        var messages = Enumerable.Range(1, 26).Select(i => new ChatMessage(Guid.NewGuid(), ChatRoles.User, $"Previous turn {i:00}.", now)).ToArray();
        await sessions.AddAsync(new(id, ActivityType.MockInterview, "Interview", SessionStatus.Active, device, null, now, now, [], Messages: messages));
        var ticket = await ownership.StartAsync(new(id, device, true), ct, 30);
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket, continuous = true }, ct);
        Assert.Same(first, await providers.Connected.Reader.ReadAsync(ct));
        var initial = await Read("ready");
        Assert.Equal("Everyday", initial.GetProperty("mode").GetString());
        Assert.Equal("Serious", initial.GetProperty("recommendedMode").GetString());
        await Read("transcript");
        var expiry = await clock.Scheduled.Reader.ReadAsync(ct);
        var prepare = await clock.Scheduled.Reader.ReadAsync(ct);
        prepare.Fire();
        Assert.Same(standby, await providers.Connected.Reader.ReadAsync(ct));

        first.AssistantTranscript("Serious mode.");
        Assert.Equal("assistant", (await Read("transcript")).GetProperty("role").GetString());
        first.Transcript("Please don't use serious mode.", "negative-mode-fixture");
        Assert.Equal("user", (await Read("transcript")).GetProperty("role").GetString());
        first.Transcript("Let’s do a proper interview!");
        if (disabled)
        {
            Assert.Contains("Serious voice is disabled", (await Read("voice_mode_rejected")).GetProperty("detail").GetString()!);
            await Read("transcript");
            await Send(socket, new { type = "stop" }, ct);
            await Read("ended");
            Assert.Empty(standby.Sent);
            return;
        }

        var offer = await Read("voice_mode_offer");
        Assert.True(offer.GetProperty("maximumGbpForTenMinutes").GetDecimal() > 0);
        var offerId = offer.GetProperty("offerId").GetGuid();
        await Read("transcript");
        await Send(socket, new { type = "confirm_voice_mode", window = 1, offerId = Guid.NewGuid() }, ct);
        await Read("voice_mode_rejected");
        await Send(socket, new { type = "confirm_voice_mode", window = 1, offerId }, ct);
        Assert.Equal("Serious", (await Read("voice_mode_queued")).GetProperty("mode").GetString());
        Assert.All(first.Sent.Where(frame => frame.GetProperty("type").ValueEquals("session.update")),
            frame => Assert.Equal(initial.GetProperty("model").GetString(), frame.GetProperty("session").GetProperty("model").GetString()));
        Assert.Empty(standby.Sent);
        expiry.Fire();
        await Read("window_renewing");
        if (capReached)
        {
            var error = await Read("error");
            Assert.Equal(VoiceEndReasons.SeriousDailyCap, error.GetProperty("code").GetString());
            Assert.Contains("daily GBP cap reached", error.GetProperty("detail").GetString()!);
            Assert.Equal(VoiceEndReasons.SeriousDailyCap, (await Read("ended")).GetProperty("reason").GetString());
            Assert.Equal(WebSocketMessageType.Close, (await socket.ReceiveAsync(new ArraySegment<byte>(new byte[128]), ct)).MessageType);
            Assert.Equal(2, providers.Models.Count);
            Assert.Null(await runtime.ReadAudioLeaseAsync(ct));
        }
        else
        {
            Assert.Same(serious, await providers.Connected.Reader.ReadAsync(ct));
            var renewed = await Read("ready");
            Assert.Equal(ticket.Lease.Fence, renewed.GetProperty("fence").GetInt64());
            Assert.Equal(2, renewed.GetProperty("window").GetInt32());
            Assert.Equal("Serious", renewed.GetProperty("mode").GetString());
            Assert.Equal(ConfigKeys.SeriousRealtimeModel, renewed.GetProperty("model").GetString());
            Assert.Equal(ConfigKeys.SeriousRealtimeModel, providers.Models.Last());
            var configured = Assert.Single(serious.Sent).GetProperty("session");
            Assert.Equal(ConfigKeys.SeriousRealtimeModel, configured.GetProperty("model").GetString());
            Assert.Contains("Previous turn 06.", configured.GetProperty("instructions").GetString()!);
            Assert.Contains("proper interview", configured.GetProperty("instructions").GetString()!);
            Assert.Contains("never re-greet", configured.GetProperty("instructions").GetString()!);
            await Send(socket, new { type = "stop" }, ct);
            await Read("ended");

            var restarted = await ownership.StartAsync(new(id, device, true), ct, 30);
            using var again = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
            await Send(again, new { type = "authenticate", ticket = restarted.Ticket, continuous = true }, ct);
            var firstNewWindow = await ReadFrameAsync(again, "ready", ct);
            Assert.Equal("Everyday", firstNewWindow.GetProperty("mode").GetString());
            Assert.NotEqual(ticket.Lease.Fence, firstNewWindow.GetProperty("fence").GetInt64());
            await ReadFrameAsync(again, "transcript", ct);
            await Send(again, new { type = "stop" }, ct);
            await ReadFrameAsync(again, "ended", ct);
        }

        Assert.Empty(standby.Sent);
        Assert.Equal(WebSocketState.Aborted, standby.State);
        var (attempts, reservations) = await runtime.ReadAsync(s => (
            s.ProviderAttempts.Values.ToArray(), s.UsageReservations.Values.ToArray()), ct);
        Assert.Contains(attempts, a => a.VoiceMode == VoiceMode.Everyday && a.Outcome == ProviderAttemptOutcome.Refused);
        if (capReached) Assert.DoesNotContain(reservations, entry => entry.VoiceMode == VoiceMode.Serious);
        else
        {
            var entry = Assert.Single(reservations, r => r.VoiceMode == VoiceMode.Serious);
            var attempt = Assert.Single(attempts, a => a.VoiceMode == VoiceMode.Serious);
            Assert.Equal(id, entry.RunId);
            Assert.Equal(entry.Id, attempt.ReservationId);
            Assert.Equal(ConfigKeys.SeriousRealtimeModel, attempt.Route.Model);
            Assert.Equal(ProviderAttemptOutcome.Completed, attempt.Outcome);
        }

        async Task<JsonElement> Read(string type)
        {
            var frame = await RealtimeGateway.ReadAsync(socket, 4096, ct);
            Assert.Equal(type, frame.GetProperty("type").GetString());
            return frame;
        }
    }
}
