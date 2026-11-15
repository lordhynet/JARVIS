using System.Net.WebSockets;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class RealtimeProtocolTests
{
    private sealed class ProviderSocket : WebSocket, IRealtimeConnectionFactory
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        public readonly List<JsonElement> Sent = [];
        private WebSocketState state = WebSocketState.Open;
        public int Connections;
        public bool ToolScenario, CancelTool, Coaching, EmitAudio;
        private int responses;
        private Guid captured;
        public readonly Channel<JsonElement> ToolOutputs = Channel.CreateUnbounded<JsonElement>();
        public readonly TaskCompletionSource Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WebSocket> ConnectAsync(string model, CancellationToken ct) { Connections++; return Task.FromResult<WebSocket>(this); }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => CloseAsync(status, description, ct);
        private void Queue(object value) => incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
        public void Answer() {
            Queue(new { type = "conversation.item.input_audio_transcription.completed", item_id = "user-answer", transcript = "I would start by identifying the transaction boundary." });
            Queue(new { type = "input_audio_buffer.speech_stopped", item_id = "user-answer", audio_end_ms = 3000 });
        }
        private void Function(string name) {
            var starting = name == "core_practice_start";
            var args = Coaching ? JsonSerializer.Serialize(new { action = starting ? "start" : "answer", utterance_id = captured,
                exercise_id = starting ? "point-first" : null, story_id = (Guid?)null, story_version = (int?)null, claim_index = (int?)null }) :
                starting ? JsonSerializer.Serialize(new { utterance_id = captured, mode = "RapidDrill" }) : JsonSerializer.Serialize(new { utterance_id = captured });
            if (Coaching) name = "core_coaching";
            var done = new { type = "response.done", response = new { id = "response-" + responses, status = CancelTool ? "cancelled" : "completed",
                output = new[] { new { type = "function_call", call_id = "call-" + responses, name, arguments = args } }, usage = new { input_tokens = 10, output_tokens = 10 } } };
            Queue(done); Queue(done); // Duplicate terminal events must never repeat an action or its follow-up.
            if (CancelTool) Idle.TrySetResult();
        }
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            var message = JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan()); lock (Sent) Sent.Add(message);
            switch (message.GetProperty("type").GetString())
            {
                case "session.update": Queue(new { type = "session.updated" }); break;
                case "response.create":
                    if (ToolScenario) {
                        responses++; Queue(new { type = "response.created", response = new { id = "response-" + responses } });
                        if (responses == 1) Queue(new { type = "conversation.item.input_audio_transcription.completed", item_id = "user-start", transcript = Coaching ? "Please start point-first coaching." : "Please start a rapid technical drill." });
                        else if (responses == 3) Function("core_practice_answer");
                        else { Queue(new { type = "response.done", response = new { id = "response-" + responses, status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 10, output_tokens = 10 } } }); Idle.TrySetResult(); }
                        break;
                    }
                    Queue(new { type = "response.created", response = new { id = "response-test" } });
                    if (EmitAudio) {
                        Queue(new { type = "response.output_audio.delta", delta = "AAA=", item_id = "audio-test" });
                        Queue(new { type = "response.output_audio.delta", delta = "AAA=", item_id = "audio-test" });
                    }
                    Queue(new { type = "response.output_audio_transcript.done", item_id = "item-test", transcript = "Explain one concrete decision." });
                    Queue(new { type = "response.done", response = new { id = "response-test", usage = new { input_tokens = 10, output_tokens = 10 } } }); break;
                case "conversation.item.create":
                    var item = message.GetProperty("item");
                    if (item.GetProperty("type").GetString() == "function_call_output") ToolOutputs.Writer.TryWrite(JsonSerializer.Deserialize<JsonElement>(item.GetProperty("output").GetString()!));
                    else if (ToolScenario) {
                        var text = item.GetProperty("content")[0].GetProperty("text").GetString()!;
                        captured = Guid.Parse(System.Text.RegularExpressions.Regex.Match(text, "[0-9a-f]{8}-[0-9a-f-]{27}").Value);
                        if (responses == 1) Function("core_practice_start");
                    }
                    break;
                case "input_audio_buffer.clear": Queue(new { type = "input_audio_buffer.cleared" }); break;
            }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var bytes = await incoming.Reader.ReadAsync(ct); bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new(bytes.Length, WebSocketMessageType.Text, true);
        }
    }
    private static Task Send(WebSocket socket, object data, CancellationToken ct) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(data), WebSocketMessageType.Text, true, ct);

    [Fact]
    public async Task Voice_stream_rejects_authentication_without_a_ticket_before_provider_dispatch()
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call expected."));
        var provider = new ProviderSocket(); using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), timeout.Token);
        await Send(socket, new { type = "authenticate", ticket = "unknown-ticket" }, timeout.Token);
        var error = await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token);
        Assert.Equal("error", error.GetProperty("type").GetString()); Assert.Equal(0, provider.Connections);
        Assert.Empty(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Voice_protocol_controls_provider_configuration_persists_transcript_and_drains_usage(bool hasDisplay)
    {
        var measurements = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == CoreTelemetry.Name && instrument.Name == "jarvis.voice.first_audio") l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => { Assert.True(value >= 0); Interlocked.Increment(ref measurements); }); listener.Start();
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("No HTTP provider call expected."));
        var provider = new ProviderSocket { EmitAudio = true }; using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), timeout.Token);
        var devices = host.Services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new("test-phone", "Fixture phone", DeviceKind.AndroidCompanion, new(true, true, hasDisplay, false, [])));
        await devices.VerifyAsync("test-phone"); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.Communication, "Voice fixture", SessionStatus.Active, "test-phone", null, now, now, []));
        var ticket = await host.Services.GetRequiredService<AudioOwnership>().StartAsync(new(id, "test-phone", true), timeout.Token);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket }, timeout.Token);
        var ready = await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token); Assert.Equal("ready", ready.GetProperty("type").GetString());
        for (var i = 0; i < 2; i++) Assert.Equal("audio", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        if (hasDisplay) { var transcript = await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token); Assert.Equal("transcript", transcript.GetProperty("type").GetString()); }
        await Send(socket, new { type = "heartbeat", sequence = 1 }, timeout.Token);
        Assert.Equal("lease", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        await Send(socket, new { type = "stop" }, timeout.Token);
        Assert.Equal("ended", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        var settings = provider.Sent.Single(x => x.GetProperty("type").GetString() == "session.update").GetProperty("session");
        Assert.Contains(settings.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "core_practice_start");
        Assert.False(settings.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").GetProperty("create_response").GetBoolean());
        Assert.Equal(24000, settings.GetProperty("audio").GetProperty("input").GetProperty("format").GetProperty("rate").GetInt32());
        var saved = await host.Services.GetRequiredService<ISessionReader>().GetAsync(id);
        Assert.Equal("Explain one concrete decision.", Assert.Single(saved!.Messages!).Text);
        var usage = Assert.Single(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations.Values.ToList()));
        Assert.NotNull(usage.ActualUsd); Assert.False(usage.ReconciliationRequired);
        Assert.Equal(1, measurements);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Voice_tools_dispatch_on_core_once_and_cancelled_calls_do_not_mutate(bool cancelled, bool coaching)
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("No real provider call expected."));
        var provider = new ProviderSocket { ToolScenario = true, CancelTool = cancelled, Coaching = coaching };
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRealtimeConnectionFactory>(provider)));
        var devices = host.Services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new("tool-phone", "Audio fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        await devices.VerifyAsync("tool-phone"); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Tool fixture", SessionStatus.Active, "tool-phone", null, now, now, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ticket = await host.Services.GetRequiredService<AudioOwnership>().StartAsync(new(id, "tool-phone", true), timeout.Token);
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), timeout.Token);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket }, timeout.Token);
        Assert.Equal("ready", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        await provider.Idle.Task.WaitAsync(timeout.Token);
        if (!cancelled) {
            var started = await provider.ToolOutputs.Reader.ReadAsync(timeout.Token); Assert.True(started.GetProperty("ok").GetBoolean());
            Assert.False(started.GetProperty("result").TryGetProperty("reference", out _));
            provider.Answer();
            var saved = await provider.ToolOutputs.Reader.ReadAsync(timeout.Token); Assert.True(saved.GetProperty("ok").GetBoolean());
            Assert.Equal("I would start by identifying the transaction boundary.", saved.GetProperty("result").GetProperty("capturedAnswer").GetString());
        }
        await Send(socket, new { type = "heartbeat", sequence = 1 }, timeout.Token);
        Assert.Equal("lease", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        await Send(socket, new { type = "stop" }, timeout.Token);
        Assert.Equal("ended", (await RealtimeGateway.ReadAsync(socket, 4096, timeout.Token)).GetProperty("type").GetString());
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        Assert.Equal(cancelled || coaching ? 0 : 1, await runtime.ReadAsync(s => s.TrainingDrills.Count));
        Assert.Equal(cancelled || coaching ? 0 : 1, await runtime.ReadAsync(s => s.TrainingAttempts.Count));
        Assert.Equal(!cancelled && coaching ? 1 : 0, await runtime.ReadAsync(s => s.VoiceCoachings.Count));
        Assert.Equal(!cancelled && coaching ? 1 : 0, await runtime.ReadAsync(s => s.CoachingAttempts.Count));
        Assert.False(provider.ToolOutputs.Reader.TryRead(out _));
        var usage = Assert.Single(await runtime.ReadAsync(s => s.UsageReservations.Values.ToArray()));
        Assert.NotNull(usage.ActualUsd); Assert.Equal(cancelled ? 10 : 40, usage.InputTokens);
    }
}
