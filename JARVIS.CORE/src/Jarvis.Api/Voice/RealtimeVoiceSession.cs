using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Api.Integrations;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Intelligence;

namespace Jarvis.Api.Voice;

internal sealed class RealtimeVoiceSession(RealtimeVoiceClient client, AudioLease lease, VoiceWindowLimits window,
    int number, bool continuous, AudioOwnership ownership, IRealtimeConnectionFactory connections,
    ManagedAi ai, UsageBudget budget, IRuntimeStore runtime, ISessionStore sessions, TimeProvider clock,
    CoreVoiceTools tools, VoiceTranscriptService transcripts, AudioFenceChanges fenceChanges)
{
    private enum Phase { Starting, Ready, EndingBeforeReady, Ending }

    private const int OutputTokens = 256;
    private const int ConversationTokens = 4096;
    private const int PrefetchSeconds = 25;
    private const int PrefetchPromptAllowanceBytes = 1800;
    private const int CurrentTaskByteLimit = 500;

    private readonly SemaphoreSlim clientSend = new(1);
    private readonly SemaphoreSlim providerSend = new(1);
    private readonly TaskCompletionSource cleared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource limitReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource timeout = null!;
    private WebSocket provider = null!;
    private WebSocket? standby;
    private LiveAudioFence fence = null!;
    private UsageReservation reservation = null!;
    private string model = "";
    private string instructions = "";
    private string persona = "";
    private int reservedPromptBytes;
    private bool dispatched;
    private bool pumpsStopped;
    private RealtimePricing pricing = null!;
    private RealtimeVoiceOutput voiceOutput;
    private RealtimeTurnDetection turnDetection;
    private string configurationEventId = "";
    private Guid sessionId;
    private bool hasDisplay;
    private int phase;
    private int paidPauseRequested;
    private int providerFailed;

    // The provider pump owns response counters and evidence. The runner reads them when draining.
    private readonly HashSet<string> createdIds = [];
    private readonly HashSet<string> handledResponses = [];
    private readonly HashSet<string> usageSeen = [];
    private readonly Dictionary<string, int> speechStarts = [];
    private readonly Dictionary<string, double> speechDurations = [];
    private readonly Dictionary<string, (string Fingerprint, string Output)> toolResults = [];
    private readonly HashSet<Guid> utterances = [];
    private readonly ConcurrentDictionary<string, byte> audioItems = new();
    private Guid latestUtterance;
    private int requested;
    private int created;
    private int completed;
    private int inputTokens;
    private int output;
    private int toolCalls;
    private decimal tokenCost;
    private long firstAudioAt;

    // Only the client pump counts microphone bytes. The runner joins that pump before settlement.
    private long started;
    private long bytes;

    private bool IsEnding => (Phase)Volatile.Read(ref phase) is Phase.EndingBeforeReady or Phase.Ending;
    private bool IsReady => (Phase)Volatile.Read(ref phase) is Phase.Ready or Phase.Ending;

    private void MarkReady()
    {
        var previous = Interlocked.CompareExchange(ref phase, (int)Phase.Ready, (int)Phase.Starting);
        if (previous == (int)Phase.EndingBeforeReady)
            Interlocked.CompareExchange(ref phase, (int)Phase.Ending, (int)Phase.EndingBeforeReady);
    }

    private void BeginEnding()
    {
        var previous = Interlocked.CompareExchange(ref phase, (int)Phase.EndingBeforeReady, (int)Phase.Starting);
        if (previous == (int)Phase.Ready)
            Volatile.Write(ref phase, (int)Phase.Ending);
    }

    private async Task PrepareWindowAsync(CancellationToken ct)
    {
        ai.RequireReady("Voice");
        voiceOutput = ai.RealtimeOutput();
        turnDetection = ai.VoiceTurnDetection();
        Guard.Require(await ownership.CurrentAsync(lease, ct), 409, "Audio ownership expired before voice reservation.");
        persona = ai.RealtimePersona();
        hasDisplay = await runtime.ReadAsync(s => s.Devices.GetValueOrDefault(lease.DeviceId)?.Registration.Capabilities.HasDisplay == true, ct);
        instructions = await ReadInstructionsAsync(ct);
        var voiceRates = ai.RealtimeRates();
        pricing = voiceRates.Pricing;
        model = voiceRates.Model;
        await ReserveWindowAsync(Encoding.UTF8.GetByteCount(instructions) + (number > 1 ? PrefetchPromptAllowanceBytes : 0), ct);
    }

    private async Task<string> ReadInstructionsAsync(CancellationToken ct)
    {
        var session = await sessions.GetAsync(lease.SessionId, ct);
        Guard.Require(session is not null, 404, "Session not found.");
        if (!ReferenceEquals(VoiceConversationMemory.RefreshSummary(session), session))
        {
            session = await sessions.UpdateAsync(session.Id, VoiceConversationMemory.RefreshSummary, ct);
            Guard.Require(session is not null, 404, "Session not found.");
        }
        sessionId = session.Id;
        var recent = VoiceConversationMemory.Recent(session);
        var task = session.CurrentTask is null ? null : VoiceConversationMemory.ClipJsonText(session.CurrentTask, CurrentTaskByteLimit);
        var contextText = JsonSerializer.Serialize(new { session.Activity, CurrentTask = task,
            EarlierTurns = session.VoiceSummary?.EarlierTurns ?? [], Messages = recent });
        var practice = await runtime.ReadAsync(s => ContextSelector.Select(s.Stories.Values, s.Learning.Values,
            task + " " + string.Join(" ", recent.TakeLast(3).Select(x => x.Text)), clock.GetUtcNow(), maxBytes: 5000), ct);
        return RealtimeVoicePrompt.Build(persona, contextText, JsonSerializer.Serialize(practice), continuing: number > 1);
    }

    private async Task ReserveWindowAsync(int promptBytes, CancellationToken ct)
    {
        var toolBytes = JsonSerializer.SerializeToUtf8Bytes(CoreVoiceTools.Definitions).Length;
        // Tool results share the explicitly capped post-instruction conversation context.
        // Reserve that token cap per response, plus conservative prefix/audio overhead.
        var inputBound = promptBytes + toolBytes + ConversationTokens + 5120m;
        var maximum = (inputBound * window.MaxTurns * pricing.MaxInput + window.MaxTurns * OutputTokens * pricing.MaxOutput)
            / 1_000_000m + pricing.Transcription(window.Seconds);
        reservation = await budget.ReserveAsync(Guid.NewGuid(), sessionId, model, maximum, ai.Limits, ct, UsageLane.Voice, ProviderIds.OpenAi);
        reservedPromptBytes = promptBytes;
    }

    private async Task RefreshPreparedPromptAsync(CancellationToken ct)
    {
        Guard.Require(await ownership.CurrentAsync(lease, ct), 409, "Audio ownership expired before voice renewal.");
        instructions = await ReadInstructionsAsync(ct);
        var bytes = Encoding.UTF8.GetByteCount(instructions);
        if (bytes <= reservedPromptBytes) return;
        // The pre-opened socket has received no instructions, audio or response request.
        await budget.SettleAsync(reservation.Id, 0, 0, 0, ct);
        reservation = null!;
        await ReserveWindowAsync(bytes, ct);
    }

    public async Task<(RealtimeVoiceSession? Next, string? Ending)> RunAsync(
        Func<int, RealtimeVoiceSession> createWindow, CancellationToken requestAborted)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        using var preparationStop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        using var inputStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout = lifetime;
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var pumps = new List<Task>(3);
        RealtimeVoiceSession? next = null;
        Task? nextPreparation = null;
        var passedNext = false;
        try
        {
            using var audioFence = new LiveAudioFence(lease, ownership, fenceChanges, clock);
            fence = audioFence;
            var downstream = Task.Run(() => PumpClientAsync(inputStop.Token), timeout.Token);
            pumps.Add(downstream);
            if (reservation is null) await PrepareWindowAsync(timeout.Token);
            else await RefreshPreparedPromptAsync(timeout.Token);
            ai.RequireReady("Voice");
            Guard.Require(!await runtime.ReadPaidAiPausedAsync(timeout.Token), 503, "Paid AI is globally paused.");
            Guard.Require(!await runtime.ReadAsync(s => s.UsageReservations.Values.Any(x => x.ReconciliationRequired), timeout.Token),
                503, "A provider rate or usage mismatch requires reconciliation before further paid work.");

            Guard.Require(await fence.RefreshAsync(timeout.Token), 409, "Audio lease expired before provider dispatch.");
            if (downstream.IsCompleted) return await StopBeforeDispatchAsync(downstream);
            if (standby?.State != WebSocketState.Open)
            {
                standby?.Dispose();
                standby = null;
            }
            provider = standby ?? await ConnectReservedAsync(timeout.Token);
            standby = null;
            Guard.Require(await fence.RefreshAsync(timeout.Token), 409, "Audio lease expired while connecting.");
            if (downstream.IsCompleted) return await StopBeforeDispatchAsync(downstream);
            dispatched = true;
            await ConfigureProviderAsync(model, instructions);
            started = clock.GetTimestamp();
            timeout.CancelAfter(TimeSpan.FromSeconds(window.Seconds + 5));

            var upstream = Task.Run(() => PumpProviderAsync(model, instructions), timeout.Token);
            pumps.Add(upstream);
            var expiry = Task.Delay(TimeSpan.FromSeconds(window.Seconds), clock, timeout.Token);
            var leaseMonitor = MonitorLeaseAsync();
            pumps.Add(leaseMonitor);

            if (continuous)
            {
                next = createWindow(checked(number + 1));
                nextPreparation = PrepareSuccessorAsync(next, preparationStop.Token);
            }
            var finished = await Task.WhenAny(upstream, downstream, expiry, leaseMonitor, limitReached.Task);
            var rollover = finished == expiry || finished == limitReached.Task;
            await finished;
            BeginEnding();
            if (rollover && next is not null) await SendClientAsync(new { type = "window_renewing", window = number });
            else await inputStop.CancelAsync();
            var settled = await DrainAndSettleAsync(upstream);
            if (rollover && next is not null)
            {
                Guard.Require(settled, 502, "The provider did not confirm the completed voice window.");
                using var handoff = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
                handoff.CancelAfter(TimeSpan.FromSeconds(8));
                var prepared = nextPreparation!;
                var outcome = await Task.WhenAny(prepared, downstream, leaseMonitor).WaitAsync(handoff.Token);
                if (outcome == downstream) return await StopBeforeDispatchAsync(downstream);
                if (outcome == leaseMonitor) { await leaseMonitor; return (null, "paid_ai_paused"); }
                try { await prepared; }
                catch (DomainException error) when (error.StatusCode == 429)
                {
                    // The predecessor's reservation can temporarily consume the available balance.
                    var retry = next.PrepareAndConnectAsync(handoff.Token);
                    try { await retry; }
                    catch (OperationCanceledException) when (downstream.IsCompletedSuccessfully) { return (null, "stopped"); }
                }
                if (downstream.IsCompleted) return await StopBeforeDispatchAsync(downstream);
                await StopPumpsAsync(pumps);
                Guard.Require(pumps.All(pump => pump.IsCompleted), 502, "Voice could not transfer to the next window safely.");
                passedNext = true;
                return (next, null);
            }
            return (null, rollover ? "window_complete" : Volatile.Read(ref paidPauseRequested) == 1 ? "paid_ai_paused" : "stopped");
        }
        finally
        {
            await StopPumpsAsync(pumps);
            await ReturnUnusedReservationAsync();
            if (next is not null && !passedNext)
            {
                await preparationStop.CancelAsync();
                try { await nextPreparation!; } catch { /* A preparation failure cannot dispatch provider work. */ }
                await next.ReturnUnusedReservationAsync();
            }
        }
    }

    private async Task PrepareSuccessorAsync(RealtimeVoiceSession next, CancellationToken ct)
    {
        var scheduled = Task.Delay(TimeSpan.FromSeconds(window.Seconds - PrefetchSeconds), clock, ct);
        await Task.WhenAny(scheduled, limitReached.Task).WaitAsync(ct);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromSeconds(8));
        await next.PrepareAndConnectAsync(bound.Token);
    }

    private async Task PrepareAndConnectAsync(CancellationToken ct)
    {
        await PrepareWindowAsync(ct);
        Guard.Require(await ownership.CurrentAsync(lease, ct), 409, "Audio ownership expired before voice renewal.");
        Guard.Require(!await runtime.ReadPaidAiPausedAsync(ct), 503, "Paid AI is globally paused.");
        try { standby = await ConnectReservedAsync(ct); }
        catch (WebSocketException) { /* The active window can finish. Retry once if the successor is still needed. */ }
    }

    private Task<WebSocket> ConnectReservedAsync(CancellationToken ct)
    {
        Guard.Require(reservation is not null, 503, "The voice window has no provider reservation.");
        return connections.ConnectAsync(model, ct);
    }

    private static async Task<(RealtimeVoiceSession? Next, string? Ending)> StopBeforeDispatchAsync(Task downstream)
    {
        await downstream;
        return (null, "stopped");
    }

    private Task ConfigureProviderAsync(string model, string instructions)
    {
        configurationEventId = Guid.NewGuid().ToString("N");
        return SendProviderAsync(new { type = "session.update", event_id = configurationEventId,
            session = new { type = "realtime", model = model, output_modalities = new[] { "audio" },
            instructions, max_output_tokens = OutputTokens, tools = CoreVoiceTools.Definitions,
            truncation = new { type = "retention_ratio", retention_ratio = .8, token_limits = new { post_instructions = ConversationTokens } },
            audio = new { input = new { format = new { type = "audio/pcm", rate = 24000 }, transcription = new { model = pricing.TranscriptionModel },
                turn_detection = turnDetection.ToProviderSettings() },
                output = new { format = new { type = "audio/pcm", rate = 24000 }, voice = voiceOutput.Name, speed = voiceOutput.Speed } } } }, requireAudio: true);
    }

    private async Task PumpProviderAsync(string model, string instructions)
    {
        while (!timeout.IsCancellationRequested && provider.State == WebSocketState.Open)
        {
            var message = await RealtimeGateway.ReadAsync(provider, 1_000_000, timeout.Token);
            switch (message.GetProperty("type").GetString())
            {
                case "session.updated": await HandleReadyAsync(); break;
                case "input_audio_buffer.cleared": cleared.TrySetResult(); break;
                case "response.created": await HandleResponseCreatedAsync(message); break;
                case "response.done": await HandleResponseDoneAsync(message); break;
                case "input_audio_buffer.speech_started": await HandleSpeechStartedAsync(message); break;
                case "input_audio_buffer.speech_stopped": await HandleSpeechStoppedAsync(message); break;
                case "conversation.item.input_audio_transcription.completed": await HandleTranscriptAsync(message, ChatRoles.User); break;
                case "response.output_audio_transcript.done": await HandleTranscriptAsync(message, ChatRoles.Assistant); break;
                case "response.output_audio.delta": await HandleOutputAudioAsync(message); break;
                case "error": await HandleProviderErrorAsync(message, model, instructions); break;
            }
        }
    }

    private async Task HandleReadyAsync()
    {
        if (IsEnding) return;
        Guard.Require(fence.IsCurrent(), 409, "Audio lease expired before the voice session was ready.");
        MarkReady();
        await SendClientAsync(new { type = "ready", fence = lease.Fence, leaseSeconds = 15, windowSeconds = window.Seconds,
            window = number, sampleRate = VoiceWindowLimits.SampleRate, voiceName = voiceOutput.Name, speed = voiceOutput.Speed });
        if (number == 1 && requested == 0) { requested++; await SendProviderAsync(new { type = "response.create" }, requireAudio: true); }
    }

    private async Task HandleResponseCreatedAsync(JsonElement message)
    {
        if (createdIds.Add(message.GetProperty("response").GetProperty("id").GetString()!))
        {
            Interlocked.Increment(ref created);
            Interlocked.Exchange(ref firstAudioAt, clock.GetTimestamp());
        }
        if (created > window.MaxTurns || created > requested)
        {
            await runtime.UpdateAsync(s => { s.UsageReservations[reservation.Id] = s.UsageReservations[reservation.Id] with { ReconciliationRequired = true }; return true; }, timeout.Token);
            throw new DomainException(502, "Provider exceeded its authorized response limit; paid work is paused for reconciliation.");
        }
    }

    private async Task HandleResponseDoneAsync(JsonElement message)
    {
        var response = message.GetProperty("response"); var responseId = response.GetProperty("id").GetString()!;
        Guard.Require(createdIds.Contains(responseId), 502, "Provider completed an unknown response.");
        if (!handledResponses.Add(responseId)) return;
        if (usageSeen.Add(responseId) && response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            try { inputTokens = checked(inputTokens + usage.GetProperty("input_tokens").GetInt32()); output = checked(output + usage.GetProperty("output_tokens").GetInt32()); tokenCost += pricing.Cost(usage); }
            catch (Exception) {
                await runtime.UpdateAsync(s => { s.UsageReservations[reservation.Id] = s.UsageReservations[reservation.Id] with { ReconciliationRequired = true }; return true; }, timeout.Token);
                throw new DomainException(502, "Realtime usage cannot be priced safely; reconcile before further paid work.");
            }
            Interlocked.Increment(ref completed);
        }
        // Only completed, authorized responses can dispatch Core operations.
        if (!IsEnding && response.TryGetProperty("status", out var status) && status.GetString() == "completed" &&
            response.TryGetProperty("output", out var items) && items.ValueKind == JsonValueKind.Array)
            await DispatchCompletedToolsAsync(items);
        if (continuous && requested == window.MaxTurns && completed == requested)
            limitReached.TrySetResult();
    }

    private async Task DispatchCompletedToolsAsync(JsonElement items)
    {
        var hadCalls = false;
        var calls = items.EnumerateArray().Where(x => x.TryGetProperty("type", out var kind) && kind.GetString() == "function_call").Take(CoreVoiceTools.MaxCalls + 1).ToArray();
        Guard.Require(calls.Length <= CoreVoiceTools.MaxCalls, 502, "Provider exceeded the Core tool batch limit.");
        foreach (var call in calls)
        {
            if (IsEnding) break;
            Guard.Require(await ownership.CurrentAsync(lease, timeout.Token) && fence.IsCurrent(), 409,
                "Audio ownership expired before Core tool dispatch.");
            var callId = Guard.Text(call.GetProperty("call_id").GetString(), "Tool call", 100);
            var name = Guard.Text(call.GetProperty("name").GetString(), "Tool name", 80);
            var args = call.GetProperty("arguments").GetString() ?? "";
            var fingerprint = name + ":" + args; string result;
            if (toolResults.TryGetValue(callId, out var prior))
                result = prior.Fingerprint == fingerprint ? prior.Output : CoreVoiceTools.Failure("Tool call ID was reused for different input.");
            else if (toolCalls >= CoreVoiceTools.MaxCalls) result = CoreVoiceTools.Failure("Core tool limit reached for this voice window. Continue in the next window.");
            else
            {
                toolCalls++;
                result = await tools.ExecuteAsync(name, args, sessionId, utterances, timeout.Token, latestUtterance, CurrentAudioAsync);
                toolResults[callId] = (fingerprint, result);
            }
            await SendProviderAsync(new { type = "conversation.item.create", item = new { type = "function_call_output", call_id = callId, output = result } });
            hadCalls = true;
        }
        if (hadCalls && !IsEnding) await RequestResponseAsync();
    }

    private async Task<bool> CurrentAudioAsync(CancellationToken ct) =>
        !IsEnding && await ownership.CurrentAsync(lease, ct) && fence.IsCurrent() && !IsEnding;

    private async Task HandleSpeechStartedAsync(JsonElement message)
    {
        speechStarts[message.GetProperty("item_id").GetString()!] = message.GetProperty("audio_start_ms").GetInt32();
        await SendClientAsync(new { type = "interrupt" });
    }

    private async Task HandleSpeechStoppedAsync(JsonElement message)
    {
        var item = message.GetProperty("item_id").GetString()!;
        if (speechStarts.TryGetValue(item, out var at)) speechDurations[item] = Math.Max(0, message.GetProperty("audio_end_ms").GetInt32() - at) / 1000d;
        if (!IsEnding) await RequestResponseAsync();
    }

    private async Task RequestResponseAsync()
    {
        if (requested < window.MaxTurns)
        {
            requested++;
            await SendProviderAsync(new { type = "response.create" }, requireAudio: true);
        }
        else if (continuous) limitReached.TrySetResult();
        else await SendClientAsync(new { type = "window_limit", detail = "Voice turn limit reached; saved Core progress will resume in the next window." });
    }

    private async Task HandleTranscriptAsync(JsonElement message, string role)
    {
        var item = message.GetProperty("item_id").GetString()!; var text = message.GetProperty("transcript").GetString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var id = ManagedAi.Operation(role + ":" + item, sessionId, reservation.Id); var bounded = text.Length <= 8000 ? text : text[..8000];
            var duration = role == ChatRoles.User && speechDurations.TryGetValue(item, out var seconds) ? (double?)seconds : null;
            await transcripts.RecordAsync(sessionId, id, role, bounded, duration, timeout.Token);
            if (role == ChatRoles.User && utterances.Count < window.MaxTurns * 4 && utterances.Add(id))
            {
                latestUtterance = id;
                await SendProviderAsync(new { type = "conversation.item.create", item = new { type = "message", role = "system", content = new[] {
                    new { type = "input_text", text = $"Core captured the latest user speech. Its utterance_id is {id}. This ID references the user's exact transcript on Core; never read the ID aloud." }
                } } });
            }
            else if (role == ChatRoles.User && !utterances.Contains(id)) latestUtterance = Guid.Empty;
            if (hasDisplay) await SendClientAsync(new { type = "transcript", role, text = bounded, speechSeconds = duration });
        }
    }

    private async Task HandleOutputAudioAsync(JsonElement message)
    {
        var itemId = message.GetProperty("item_id").GetString()!;
        audioItems.TryAdd(itemId, 0);
        await SendClientAsync(new { type = "audio", delta = message.GetProperty("delta").GetString(), itemId }, fence);
    }

    private async Task HandleProviderErrorAsync(JsonElement message, string model, string instructions)
    {
        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && code.ValueEquals("response_cancel_not_active")) return;
            if (!IsReady && !IsEnding && turnDetection.IsSemantic && RejectsTurnDetection(error))
            {
                turnDetection = turnDetection with { Mode = RealtimeTurnDetection.Server };
                await ConfigureProviderAsync(model, instructions);
                return;
            }
        }
        Volatile.Write(ref providerFailed, 1);
        throw new DomainException(502, "Realtime provider rejected the session or an audio event.");
    }

    private bool RejectsTurnDetection(JsonElement error)
    {
        if (error.TryGetProperty("event_id", out var eventId) && eventId.ValueKind != JsonValueKind.Null
            && (eventId.ValueKind != JsonValueKind.String || !eventId.ValueEquals(configurationEventId))) return false;

        if (!error.TryGetProperty("param", out var param) || param.ValueKind != JsonValueKind.String) return false;
        var path = param.GetString()!;
        const string sessionPrefix = "session.";
        const string turnDetectionPath = "audio.input.turn_detection";
        if (path.StartsWith(sessionPrefix, StringComparison.Ordinal)) path = path[sessionPrefix.Length..];
        return path == turnDetectionPath || path.StartsWith(turnDetectionPath + ".", StringComparison.Ordinal);
    }

    private async Task PumpClientAsync(CancellationToken inputToken)
    {
        while (!timeout.IsCancellationRequested && client.State == WebSocketState.Open)
        {
            var message = await client.ReadAsync(inputToken);
            switch (message.GetProperty("type").GetString())
            {
                case "heartbeat": await HandleHeartbeatAsync(message); break;
                case "audio": if (!IsEnding && MatchesWindow(message)) await HandleInputAudioAsync(message); break;
                case "interrupt": if (IsReady && !IsEnding && MatchesWindow(message)) await HandleInterruptAsync(message); break;
                case "stop":
                    if (message.TryGetProperty("reason", out var reason) && reason.GetString() == "no_speech_timeout")
                        CoreTelemetry.WakeSilenceStops.Add(1);
                    return;
                default: throw new DomainException(400, "Unsupported voice client event.");
            }
        }
    }

    private bool MatchesWindow(JsonElement message) => !continuous ||
        message.TryGetProperty("window", out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var requestedWindow) && requestedWindow == number;

    private async Task HandleHeartbeatAsync(JsonElement message)
    {
        var renewed = await ownership.RenewAsync(lease, timeout.Token);
        await SendClientAsync(new { type = "lease", fence = renewed.Fence, leaseSeconds = 15, sequence = message.GetProperty("sequence").GetInt64() });
    }

    private async Task HandleInputAudioAsync(JsonElement message)
    {
        Guard.Require(IsReady, 409, "Voice session is not ready.");
        var audio = message.GetProperty("audio").GetString()!; var pcm = Convert.FromBase64String(audio);
        Guard.Require(pcm.Length is > 0 and <= 24000 && pcm.Length % 2 == 0, 400, "PCM chunks must be aligned mono 24kHz samples.");
        bytes += pcm.Length;
        Guard.Require(bytes <= window.MaxInputBytes && bytes <= VoiceWindowLimits.BytesPerSecond * (clock.GetElapsedTime(started).TotalSeconds + 2), 429, "Voice input exceeded its real-time window.");
        await SendProviderAsync(new { type = "input_audio_buffer.append", audio }, requireAudio: true);
    }

    private async Task HandleInterruptAsync(JsonElement message)
    {
        await SendProviderAsync(new { type = "response.cancel" });
        if (message.TryGetProperty("itemId", out var item) && message.TryGetProperty("playedMs", out var ms))
        {
            var played = ms.GetInt32(); Guard.Require(played is >= 0 and <= 60000, 400, "Invalid playback position.");
            var itemId = Guard.Text(item.GetString(), "Audio item", 200);
            if (!continuous || audioItems.ContainsKey(itemId))
                await SendProviderAsync(new { type = "conversation.item.truncate", item_id = itemId, content_index = 0, audio_end_ms = played });
        }
    }

    private async Task MonitorLeaseAsync()
    {
        using var timer = new PeriodicTimer(LiveAudioFence.RefreshInterval, clock);
        while (await timer.WaitForNextTickAsync(timeout.Token))
        {
            if (await runtime.ReadPaidAiPausedAsync(timeout.Token))
            {
                Interlocked.Exchange(ref paidPauseRequested, 1);
                return;
            }
            Guard.Require(await fence.RefreshAsync(timeout.Token), 409, "Audio ownership expired.");
        }
    }

    private async Task<bool> DrainAndSettleAsync(Task upstream)
    {
        // Drain a provider acknowledgement before settling: queued input cannot create an
        // unaccounted response after this barrier. Missing acknowledgements retain the bound.
        if (!upstream.IsCompleted && provider.State == WebSocketState.Open)
        {
            await SendProviderAsync(new { type = "input_audio_buffer.clear" });
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await cleared.Task.WaitAsync(drain.Token);
                if (Volatile.Read(ref created) != Volatile.Read(ref completed)) await SendProviderAsync(new { type = "response.cancel" });
                while (Volatile.Read(ref created) != Volatile.Read(ref completed)) await Task.Delay(50, drain.Token);
            }
            catch (OperationCanceledException) { Volatile.Write(ref providerFailed, 1); }
        }
        if (Volatile.Read(ref providerFailed) == 0 && cleared.Task.IsCompletedSuccessfully && Volatile.Read(ref created) == Volatile.Read(ref completed))
        {
            var cost = tokenCost + pricing.Transcription((double)bytes / VoiceWindowLimits.BytesPerSecond);
            await budget.SettleAsync(reservation.Id, cost, inputTokens, output, timeout.Token);
            return true;
        }
        return false;
    }

    private async Task SendClientAsync(object value, LiveAudioFence? audioFence = null)
    {
        await clientSend.WaitAsync(timeout.Token);
        try
        {
            if (audioFence is not null)
            {
                if (IsEnding) return;
                Guard.Require(audioFence.IsCurrent(), 409, "Audio lease expired.");
                var latencyStart = Interlocked.Exchange(ref firstAudioAt, 0);
                if (latencyStart != 0) CoreTelemetry.VoiceFirstAudio.Record(clock.GetElapsedTime(latencyStart).TotalSeconds);
            }
            await client.SendAsync(value, timeout.Token);
        }
        finally { clientSend.Release(); }
    }
    private async Task SendProviderAsync(object value, bool requireAudio = false)
    {
        await providerSend.WaitAsync(timeout.Token);
        try
        {
            if (requireAudio)
            {
                if (IsEnding) return;
                Guard.Require(fence.IsCurrent(), 409, "Audio lease expired.");
            }
            await SendAsync(provider, value, timeout.Token);
        }
        finally { providerSend.Release(); }
    }

    private async Task ReturnUnusedReservationAsync()
    {
        standby?.Dispose();
        if (reservation is null || dispatched) return;
        try
        {
            using var budgetRelease = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await budget.SettleAsync(reservation.Id, 0, 0, 0, budgetRelease.Token);
        }
        catch { /* A failed refund retains the conservative reservation. */ }
    }

    private async Task StopPumpsAsync(List<Task> pumps)
    {
        if (pumpsStopped) return;
        pumpsStopped = true;
        await timeout.CancelAsync();
        provider?.Abort();
        try { await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* Every pump is cancelled; unknown provider usage retains its reserved charge. */ }
        provider?.Dispose();
    }

    private static Task SendAsync(WebSocket socket, object value, CancellationToken ct) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, ct);
}
