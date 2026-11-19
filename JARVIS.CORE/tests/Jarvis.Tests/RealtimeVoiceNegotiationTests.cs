using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
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
    [Fact]
    public async Task A_fallback_transcript_must_be_fresh_and_unused_on_the_current_fence()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("Provider HTTP call was not expected."));
        var app = host.Services;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var ticket = await StartVoiceModeFixtureAsync(app, ActivityType.General, ct);
        var speech = app.GetRequiredService<VoiceTranscriptService>();
        var previous = Guid.NewGuid();
        await speech.RecordAsync(ticket.Lease.SessionId, previous, ChatRoles.User, "yes", null, ct);
        var captured = app.GetRequiredService<TimeProvider>().GetUtcNow();
        var prompt = captured.AddSeconds(-1);
        var current = Guid.NewGuid();

        var stale = await Assert.ThrowsAsync<DomainException>(() =>
            speech.RecordFallbackAsync(ticket.Lease, current, prompt, prompt, "yes", ct));
        Assert.Contains("fresh spoken yes", stale.Message);
        var replay = await Assert.ThrowsAsync<DomainException>(() =>
            speech.RecordFallbackAsync(ticket.Lease, previous, prompt, captured, "yes", ct));
        Assert.Contains("already used", replay.Message);
        var wrongFence = ticket.Lease with { Fence = ticket.Lease.Fence + 1 };
        var replaced = await Assert.ThrowsAsync<DomainException>(() =>
            speech.RecordFallbackAsync(wrongFence, current, prompt, captured, "yes", ct));
        Assert.Contains("Audio ownership changed", replaced.Message);

        await speech.RecordFallbackAsync(ticket.Lease, current, prompt, captured, "yes", ct);
        var duplicate = await Assert.ThrowsAsync<DomainException>(() =>
            speech.RecordFallbackAsync(ticket.Lease, current, prompt, captured, "yes", ct));
        Assert.Contains("already used", duplicate.Message);
        var turns = await app.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.VoiceTurns.Values.ToArray(), ct);
        Assert.Equal(2, turns.Length);
    }

    [Fact]
    public async Task Automatic_voice_shows_cost_before_entry_and_follows_the_active_drill_at_renewal()
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("Provider HTTP call was not expected."));
        var clock = new WindowClock(30, frozenUtc: true);
        var first = new ProviderSocket();
        var second = new ProviderSocket();
        var providers = new WindowProviders(first, second);
        await using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConfigKeys.VoiceWindowSeconds, "30");
            builder.UseSetting(ConfigKeys.VoiceSeriousDailyGbp, "3");
            ConfigureSerious(builder.UseSetting);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IRealtimeConnectionFactory>(providers);
            });
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        var ticket = await StartVoiceModeFixtureAsync(host.Services, ActivityType.MockInterview, ct);
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket, continuous = true, voiceModeNegotiation = true }, ct);

        var quote = await ReadFrameAsync(socket, "voice_mode_offer", ct);
        Assert.True(quote.GetProperty("starting").GetBoolean());
        Assert.True(quote.GetProperty("maximumGbpForTenMinutes").GetDecimal() > 0);
        Assert.Empty(providers.Models);
        Assert.Empty(await runtime.ReadAsync(state => state.UsageReservations, ct));
        await Send(socket, new { type = "confirm_voice_mode", offerId = Guid.NewGuid(), window = 1 }, ct);
        await Send(socket, new { type = "heartbeat", sequence = 1 }, ct);
        Assert.Equal(ticket.Lease.Fence, (await ReadFrameAsync(socket, "lease", ct)).GetProperty("fence").GetInt64());
        Assert.Empty(providers.Models);
        await Send(socket, new { type = "confirm_voice_mode", offerId = quote.GetProperty("offerId").GetGuid(), window = 1 }, ct);
        Assert.Same(first, await providers.Connected.Reader.ReadAsync(ct));
        Assert.Equal("Serious", (await ReadFrameAsync(socket, "ready", ct)).GetProperty("mode").GetString());
        await ReadFrameAsync(socket, "transcript", ct);

        var expiry = await clock.Scheduled.Reader.ReadAsync(ct);
        var prepare = await clock.Scheduled.Reader.ReadAsync(ct);
        await runtime.UpdateAsync(state =>
        {
            var drill = Guid.NewGuid();
            state.TrainingDrills[drill] = new(drill, ticket.Lease.SessionId, TrainingMode.RapidDrill, null, 1,
                "Active", clock.GetUtcNow(), [], []);
            return true;
        }, ct);
        prepare.Fire();
        Assert.Same(second, await providers.Connected.Reader.ReadAsync(ct));
        Assert.Empty(second.Sent);
        expiry.Fire();
        await ReadFrameAsync(socket, "window_renewing", ct);
        var renewed = await ReadFrameAsync(socket, "ready", ct);
        Assert.Equal("Everyday", renewed.GetProperty("mode").GetString());
        Assert.Equal(ticket.Lease.Fence, renewed.GetProperty("fence").GetInt64());
        Assert.Equal(2, renewed.GetProperty("window").GetInt32());
        Assert.Equal("session.update", Assert.Single(second.Sent).GetProperty("type").GetString());
        await Send(socket, new { type = "stop" }, ct);
        await ReadFrameAsync(socket, "ended", ct);
        await runtime.UpdateAsync(state => { state.TrainingDrills.Clear(); return true; }, ct);

        var nextTicket = await host.Services.GetRequiredService<AudioOwnership>().StartAsync(
            new(ticket.Lease.SessionId, ticket.Lease.DeviceId, true), ct, 30);
        using var next = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(next, new { type = "authenticate", ticket = nextTicket.Ticket, continuous = true, voiceModeNegotiation = true }, ct);
        var nextOffer = await ReadFrameAsync(next, "voice_mode_offer", ct);
        Assert.NotEqual(quote.GetProperty("offerId").GetGuid(), nextOffer.GetProperty("offerId").GetGuid());
        Assert.Equal(2, providers.Models.Count);
        await Send(next, new { type = "stop" }, ct);
        await ReadFrameAsync(next, "ended", ct);
    }

    [Theory]
    [InlineData(true, false, "Yes.", true)]
    [InlineData(false, false, "Yes.", true)]
    [InlineData(false, true, "Yes.", true)]
    [InlineData(false, false, "Yes, but keep Serious.", false)]
    [InlineData(false, false, null, false)]
    public async Task Everyday_fallback_requires_a_fresh_captured_yes(bool seriousDisabled, bool atRenewal, string? recognized, bool continues)
    {
        var requests = 0;
        await using var root = new ProviderFactory(async request =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal("/v1/audio/transcriptions", request.RequestUri!.AbsolutePath);
            var form = Assert.IsType<System.Net.Http.MultipartFormDataContent>(request.Content);
            var wav = await form.Last().ReadAsByteArrayAsync();
            Assert.Equal(3 * VoiceWindowLimits.BytesPerSecond + 44, wav.Length);
            Assert.Equal(VoiceWindowLimits.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { text = recognized }), Encoding.UTF8, "application/json") };
        });
        var clock = new WindowClock(30, frozenUtc: true);
        var providers = new WindowProviders(new ProviderSocket(), new ProviderSocket());
        await using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConfigKeys.VoiceWindowSeconds, "30");
            builder.UseSetting(ConfigKeys.VoiceSeriousDailyGbp, atRenewal ? "3" : "0.001");
            if (!seriousDisabled) ConfigureSerious(builder.UseSetting);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IRealtimeConnectionFactory>(providers);
            });
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        var ticket = await StartVoiceModeFixtureAsync(host.Services, ActivityType.MockInterview, ct);
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket, continuous = true, voiceModeNegotiation = true }, ct);
        if (!seriousDisabled)
        {
            var estimate = await ReadFrameAsync(socket, "voice_mode_offer", ct);
            await Send(socket, new { type = "confirm_voice_mode", offerId = estimate.GetProperty("offerId").GetGuid(), window = 1 }, ct);
        }
        if (atRenewal)
        {
            Assert.Equal("Serious", (await ReadFrameAsync(socket, "ready", ct)).GetProperty("mode").GetString());
            await ReadFrameAsync(socket, "transcript", ct);
            var expiry = await clock.Scheduled.Reader.ReadAsync(ct);
            var prepare = await clock.Scheduled.Reader.ReadAsync(ct);
            host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.VoiceSeriousDailyGbp] = "0.001";
            prepare.Fire();
            expiry.Fire();
            await ReadFrameAsync(socket, "window_renewing", ct);
        }
        var ask = await ReadFrameAsync(socket, "voice_fallback_offer", ct);
        Assert.Contains(seriousDisabled ? "Serious voice is disabled" : "Serious voice daily GBP cap reached", ask.GetProperty("detail").GetString()!);
        var captureId = ask.GetProperty("captureId").GetGuid();
        var chunk = new byte[24000];
        if (recognized is not null) Array.Fill(chunk, (byte)17);
        var audio = Convert.ToBase64String(chunk);
        await Send(socket, new { type = "voice_fallback_played", captureId = Guid.NewGuid() }, ct);
        await Send(socket, new { type = "voice_fallback_audio", captureId, audio }, ct);
        await Send(socket, new { type = "confirm_voice_mode", captureId, transcript = "yes" }, ct);
        await Send(socket, new { type = "heartbeat", sequence = 2 }, ct);
        await ReadFrameAsync(socket, "lease", ct);
        Assert.Equal(0, requests);
        Assert.Equal(atRenewal ? 1 : 0, providers.Models.Count);
        Assert.Equal(atRenewal ? 1 : 0, await runtime.ReadAsync(state => state.UsageReservations.Count, ct));
        await Send(socket, new { type = "voice_fallback_played", captureId }, ct);
        await ReadFrameAsync(socket, "voice_fallback_capture", ct);
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 6; i++) await Send(socket, new { type = "voice_fallback_audio", captureId, audio }, ct);
        if (recognized is not null) await ReadFrameAsync(socket, "voice_fallback_verifying", ct);
        if (continues)
        {
            var ready = await ReadFrameAsync(socket, "ready", ct);
            Assert.Equal("Everyday", ready.GetProperty("mode").GetString());
            Assert.Equal(ticket.Lease.Fence, ready.GetProperty("fence").GetInt64());
            Assert.Equal(atRenewal ? 2 : 1, ready.GetProperty("window").GetInt32());
            if (!atRenewal) await ReadFrameAsync(socket, "transcript", ct);
            var turn = await runtime.ReadAsync(state => state.VoiceTurns[captureId], ct);
            Assert.Equal(recognized, turn.Text);
            Assert.Equal(ticket.Lease.SessionId, turn.SessionId);
            Assert.Equal(atRenewal ? 2 : 1, providers.Models.Count);
            Assert.Equal(ConfigKeys.EverydayRealtimeModel, providers.Models.Last());
            await Send(socket, new { type = "stop" }, ct);
            await ReadFrameAsync(socket, "ended", ct);
        }
        else
        {
            Assert.Equal(seriousDisabled ? "voice_mode_declined" : VoiceEndReasons.SeriousDailyCap,
                (await ReadFrameAsync(socket, "ended", ct)).GetProperty("reason").GetString());
            Assert.Empty(providers.Models);
            Assert.False(await runtime.ReadAsync(state => state.VoiceTurns.ContainsKey(captureId), ct));
        }
        Assert.Equal(recognized is null ? 0 : 1, requests);
    }

    private static void ConfigureSerious(Func<string, string?, Microsoft.AspNetCore.Hosting.IWebHostBuilder> setting)
    {
        var prefix = ConfigKeys.VoiceModes + VoiceMode.Serious + ":";
        setting(prefix + ConfigKeys.Enabled, "true");
        VoiceModePolicyTests.Seed((key, value) => setting(key, value), prefix, ConfigKeys.SeriousRealtimeModel);
    }

    private static async Task<VoiceTicket> StartVoiceModeFixtureAsync(IServiceProvider app, ActivityType activity, CancellationToken ct)
    {
        const string device = "negotiation-phone";
        var devices = app.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new(device, "Negotiation fixture", DeviceKind.AndroidCompanion, new(true, true, true, false, [])));
        await devices.VerifyAsync(device);
        var now = app.GetRequiredService<TimeProvider>().GetUtcNow();
        var id = Guid.NewGuid();
        await app.GetRequiredService<ISessionStore>().AddAsync(new(id, activity, "Voice negotiation", SessionStatus.Active, device, null, now, now, []));
        return await app.GetRequiredService<AudioOwnership>().StartAsync(new(id, device, true), ct, 30);
    }

    private static async Task<JsonElement> ReadFrameAsync(WebSocket socket, string type, CancellationToken ct)
    {
        var result = await RealtimeGateway.ReadAsync(socket, 4096, ct);
        var actual = result.GetProperty("type").GetString();
        var detail = result.TryGetProperty("detail", out var error) ? error.GetString() : "";
        Assert.True(type == actual, $"Expected {type}, received {actual}. {detail}");
        return result;
    }
}
