using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
    public enum RenewalAction { Stop, Continue, MoreContext, BudgetDenied, LeaseExpired, GbpBudgetDenied }

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
    [InlineData(30, RenewalAction.Stop)]
    [InlineData(300, RenewalAction.Continue)]
    [InlineData(30, RenewalAction.MoreContext)]
    [InlineData(30, RenewalAction.BudgetDenied)]
    [InlineData(30, RenewalAction.GbpBudgetDenied)]
    [InlineData(30, RenewalAction.LeaseExpired)]
    public async Task Voice_prepares_successor_in_background_and_dispatches_only_after_handoff(int windowSeconds, RenewalAction action)
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call expected."));
        var first = new ProviderSocket { EmitAudio = true };
        var second = new ProviderSocket();
        var providers = new WindowProviders(first, second);
        var clock = new WindowClock(windowSeconds);
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConfigKeys.VoiceWindowSeconds, windowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // This positive-flow fixture needs room for overlapping five-minute reservations,
            // including the growing installed tool schemas. Budget-denial cases override it below.
            builder.UseSetting(ConfigKeys.DailyLimitUsd, "3");
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
        var sessions = host.Services.GetRequiredService<ISessionStore>();
        var initial = Enumerable.Range(1, 30).Select(i => new ChatMessage(Guid.NewGuid(), ChatRoles.User,
            $"Earlier detail {i:00}.", now)).ToArray();
        await sessions.AddAsync(new(id, ActivityType.General, "Voice continuity fixture",
            SessionStatus.Active, device, null, now, now, [], Messages: initial));
        const string authoredAnswer = "Hidden answer marker ZXC987.";
        const string authoredQuestion = "What is a rollback?";
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        var card = Guid.NewGuid();
        await runtime.UpdateAsync(s =>
        {
            s.Learning[card] = new(card, LearningDomain.French, authoredQuestion, authoredAnswer, 1, 1, now.AddDays(-1), now);
            return true;
        }, ct);
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        using var response = await http.PostAsJsonAsync("/api/voice/tickets", new VoiceStartRequest(id, device, true), ct);
        response.EnsureSuccessStatusCode();
        var issued = (await response.Content.ReadFromJsonAsync<VoiceTicket>(ct))!;
        Assert.Equal(windowSeconds, issued.WindowSeconds);
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = issued.Ticket, continuous = true }, ct);
        Assert.Same(first, await providers.Connected.Reader.ReadAsync(ct));
        var initialReady = await RealtimeGateway.ReadAsync(socket, 4096, ct);
        Assert.Equal("ready", initialReady.GetProperty("type").GetString());
        Assert.Equal(1, initialReady.GetProperty("window").GetInt32());
        Assert.Equal(windowSeconds, initialReady.GetProperty("windowSeconds").GetInt32());
        Assert.Equal("audio", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        Assert.Equal("audio", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        Assert.Equal("transcript", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        var expiry = await clock.Scheduled.Reader.ReadAsync(ct);
        var preparation = await clock.Scheduled.Reader.ReadAsync(ct);
        Assert.Equal(TimeSpan.FromSeconds(windowSeconds), expiry.Delay);
        Assert.Equal(TimeSpan.FromSeconds(windowSeconds - 25), preparation.Delay);
        var firstPrompt = first.Sent.First(frame => frame.GetProperty("type").ValueEquals("session.update"))
            .GetProperty("session").GetProperty("instructions").GetString()!;
        Assert.Contains(authoredQuestion, firstPrompt);
        Assert.DoesNotContain(authoredAnswer, firstPrompt);
        Assert.Contains("Earlier detail 10.", firstPrompt);
        Assert.DoesNotContain("Earlier detail 01.", firstPrompt);
        var firstReservation = Assert.Single(await runtime.ReadAsync(s => s.UsageReservations.Values.ToArray(), ct));
        var toolBytes = JsonSerializer.SerializeToUtf8Bytes(CoreVoiceTools.Definitions).Length;
        var turns = new VoiceWindowLimits(windowSeconds).MaxTurns;
        Assert.Equal(turns * (Encoding.UTF8.GetByteCount(firstPrompt) + toolBytes + 4096m + 5120m + 256m) / 1_000_000m
            + windowSeconds / 60m * .02m, firstReservation.ReservedUsd);
        preparation.Fire();
        Assert.Same(second, await providers.Connected.Reader.ReadAsync(ct));
        Assert.Empty(second.Sent);
        var reserved = await runtime.ReadAsync(s => s.UsageReservations.Values.ToArray(), ct);
        Assert.Equal(2, reserved.Length);
        Assert.All(reserved, item => Assert.Null(item.ActualUsd));

        if (action == RenewalAction.LeaseExpired)
        {
            clock.Advance(TimeSpan.FromSeconds(16));
            var failure = await RealtimeGateway.ReadAsync(socket, 4096, ct);
            Assert.Equal("error", failure.GetProperty("type").GetString());
            Assert.Contains("Audio ownership expired", failure.GetProperty("detail").GetString());
            await AssertStoppedWithoutDispatchAsync();
            return;
        }
        if (action != RenewalAction.Stop)
        {
            if (action is RenewalAction.MoreContext or RenewalAction.BudgetDenied or RenewalAction.GbpBudgetDenied)
            {
                var extra = Enumerable.Range(1, 28).Select(i => new ChatMessage(Guid.NewGuid(), ChatRoles.User,
                    $"Extra item {i:00}. " + new string('a', 500), now));
                await sessions.UpdateAsync(id, current => current with { Messages = [.. current.Messages!, .. extra] }, ct);
                if (action == RenewalAction.BudgetDenied)
                    host.Services.GetRequiredService<IConfiguration>()[ConfigKeys.DailyLimitUsd] = "0.001";
                if (action == RenewalAction.GbpBudgetDenied) await runtime.UpdateAsync(s => {
                    var id = Guid.NewGuid();
                    s.GbpCommitments[id] = s.GbpCommitments.Values.First() with { ReservationId = id, ReservedGbp = 10m, AccountedGbp = 10m };
                    return true;
                }, ct);
            }
            const string latest = "This turn arrived after the next connection was prepared.";
            first.Transcript(latest);
            Assert.Equal(latest, (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("text").GetString());
            expiry.Fire();
            Assert.Equal("window_renewing", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
            var nextReady = await RealtimeGateway.ReadAsync(socket, 4096, ct);
            if (action is RenewalAction.BudgetDenied or RenewalAction.GbpBudgetDenied)
            {
                Assert.Equal("error", nextReady.GetProperty("type").GetString());
                Assert.Contains("budget exhausted", nextReady.GetProperty("detail").GetString()!);
                if (action == RenewalAction.GbpBudgetDenied) {
                    Assert.Equal(VoiceEndReasons.GbpBudgetCeiling, nextReady.GetProperty("code").GetString());
                    Assert.Equal(VoiceEndReasons.GbpBudgetCeiling, (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("reason").GetString());
                }
                await AssertStoppedWithoutDispatchAsync();
                return;
            }
            Assert.Equal("ready", nextReady.GetProperty("type").GetString());
            Assert.Equal(2, nextReady.GetProperty("window").GetInt32());
            Assert.Equal(issued.Lease.Fence, nextReady.GetProperty("fence").GetInt64());
            var configure = Assert.Single(second.Sent);
            var instructions = configure.GetProperty("session").GetProperty("instructions").GetString()!;
            Assert.Contains("never re-greet", instructions);
            Assert.Contains(latest, instructions);
            Assert.DoesNotContain(authoredAnswer, instructions);
            var saved = await sessions.GetAsync(id, ct);
            Assert.Equal(id, saved!.Id);
            Assert.NotNull(saved.VoiceSummary);
            const string section = "Conversation context";
            var contextStart = instructions.IndexOf(section, StringComparison.Ordinal) + section.Length;
            var contextEnd = instructions.IndexOf("Practice context", contextStart, StringComparison.Ordinal);
            using var context = JsonDocument.Parse(instructions[contextStart..contextEnd].Trim());
            Assert.Equal(saved.VoiceSummary.EarlierTurns,
                context.RootElement.GetProperty("EarlierTurns").Deserialize<VoiceMemoryTurn[]>()!);

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
        Assert.Equal(action == RenewalAction.MoreContext ? 3 : 2, final.Length);
        Assert.All(final, item =>
        {
            Assert.NotNull(item.ActualUsd);
            Assert.False(item.ReconciliationRequired);
        });
        if (action is RenewalAction.Stop or RenewalAction.MoreContext)
        {
            Assert.Contains(final, item => item.Id != firstReservation.Id && item.ActualUsd == 0);
            if (action == RenewalAction.Stop)
            {
                Assert.Empty(second.Sent);
                Assert.Equal(WebSocketState.Aborted, second.State);
            }
        }

        async Task AssertStoppedWithoutDispatchAsync()
        {
            Assert.Equal(WebSocketMessageType.Close, (await socket.ReceiveAsync(new ArraySegment<byte>(new byte[128]), ct)).MessageType);
            Assert.Empty(second.Sent);
            Assert.Null(await runtime.ReadAudioLeaseAsync(ct));
            var endedBeforeDispatch = await runtime.ReadAsync(s => s.UsageReservations.Values.ToArray(), ct);
            Assert.Equal(2, endedBeforeDispatch.Length);
            Assert.Contains(endedBeforeDispatch, entry => entry.Id != firstReservation.Id && entry.ActualUsd == 0);
        }
    }

    private sealed class WindowProviders(params ProviderSocket[] sockets) : IRealtimeConnectionFactory
    {
        private int opened;
        public readonly Channel<ProviderSocket> Connected = Channel.CreateUnbounded<ProviderSocket>();
        public readonly ConcurrentQueue<string> Models = new();
        public Task<WebSocket> ConnectAsync(string model, CancellationToken ct)
        {
            var socket = sockets[Interlocked.Increment(ref opened) - 1];
            Models.Enqueue(model);
            Connected.Writer.TryWrite(socket);
            return Task.FromResult<WebSocket>(socket);
        }
    }

    private sealed class WindowClock(int windowSeconds, bool frozenUtc = false) : TimeProvider
    {
        private readonly DateTimeOffset initialUtc = DateTimeOffset.UtcNow;
        private long offsetTicks;
        public readonly Channel<WindowTimer> Scheduled = Channel.CreateUnbounded<WindowTimer>();
        public override DateTimeOffset GetUtcNow() => (frozenUtc ? initialUtc : base.GetUtcNow()).AddTicks(Interlocked.Read(ref offsetTicks));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref offsetTicks, duration.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != TimeSpan.FromSeconds(windowSeconds - 25) && dueTime != TimeSpan.FromSeconds(windowSeconds))
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
