using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Contracts;

namespace Jarvis.Windows.Client;

public enum CompanionState { Off, ListeningLocally, Connecting, Live, Unavailable }
public sealed record ModeOffer(Guid Id, decimal MaximumGbpForTenMinutes);

public sealed class CompanionController(Connection connection, CoreClient core, IWakeListener wake, ICompanionAudio audio,
    ILockState locks, Func<IVoiceSocket>? socketFactory = null, bool enableHandoffs = false) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim localLifecycle = new(1);
    private CancellationTokenSource? active;
    private Task? running;
    private bool wanted;
    private readonly CancellationTokenSource handoffLifetime = new();
    private Task? handoffLoop;
    private bool moving;
    private (Guid Session, string Target, Guid Operation)? pendingHandoff;
    public CompanionHandoffView? Handoffs { get; private set; }
    public event Action<CompanionHandoffItem>? Incoming;
    public async Task SendHandoffAsync(string target, CancellationToken ct = default) {
        if (locks.IsLocked) throw new InvalidOperationException("Unlock before moving a conversation.");
        var device = await core.RefreshAsync(ct);
        var view = await core.HandoffAsync(new(device, Locked: false), ct);
        if (view.ActiveSessionId is not { } id) throw new InvalidOperationException("Start a conversation on this laptop first.");
        if (pendingHandoff is not { } pending || pending.Session != id || pending.Target != target) pendingHandoff = (id, target, Guid.NewGuid());
        Handoffs = await core.HandoffAsync(new(device, "start", id, TargetDeviceId: target, RequestId: pendingHandoff.Value.Operation, Locked: false), ct);
        pendingHandoff = null;
    }
    public async Task AcceptHandoffAsync(CompanionHandoffItem offer, CancellationToken ct = default) {
        if (locks.IsLocked || !connection.AllowCloudProcessing) throw new InvalidOperationException("Unlock and allow normal voice processing before accepting.");
        var device = await core.RefreshAsync(ct);
        await core.HandoffAsync(new(device, "ready", offer.SessionId, offer.HandoffId, Locked: false), ct);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct); wait.CancelAfter(TimeSpan.FromSeconds(22));
        while (true) {
            if (locks.IsLocked) throw new InvalidOperationException("Laptop locked before transfer completed.");
            var view = await core.HandoffAsync(new(device, Locked: false), wait.Token);
            var current = view.Offers.SingleOrDefault(o => o.HandoffId == offer.HandoffId);
            if (current is null) throw new InvalidOperationException("The transfer expired or was cancelled.");
            if (current.State == HandoffState.AudioTransferred) { Begin(true, offer.SessionId); return; }
            await Task.Delay(500, wait.Token);
        }
    }
    public async Task DeclineHandoffAsync(CompanionHandoffItem offer, CancellationToken ct = default) {
        var device = await core.RefreshAsync(ct);
        Handoffs = await core.HandoffAsync(new(device, "cancel", offer.SessionId, offer.HandoffId, Locked: locks.IsLocked), ct);
    }
    private async Task WatchHandoffsAsync() {
        Guid? notified = null; string? device = null; var polls = 0;
        while (!handoffLifetime.IsCancellationRequested) {
            try {
                device ??= await core.RefreshAsync(handoffLifetime.Token);
                if (polls++ % 5 == 0) await core.HeartbeatAsync(device, handoffLifetime.Token);
                var view = await core.HandoffAsync(new(device, Locked: locks.IsLocked), handoffLifetime.Token); Handoffs = view;
                var incoming = view.Offers.FirstOrDefault(o => o.Incoming);
                if (incoming is not null && incoming.HandoffId != notified && !locks.IsLocked) { notified = incoming.HandoffId; Incoming?.Invoke(incoming); }
                if (incoming is null) notified = null;
                var outgoing = view.Offers.FirstOrDefault(o => !o.Incoming && o.State == HandoffState.TargetReady);
                if (outgoing is not null) {
                    lock (gate) moving = true;
                    try { await StopAsync(); await core.HandoffAsync(new(device, "release", outgoing.SessionId, outgoing.HandoffId, Locked: locks.IsLocked, AudioStopped: true), handoffLifetime.Token); }
                    finally { lock (gate) moving = false; }
                }
            } catch (OperationCanceledException) when (handoffLifetime.IsCancellationRequested) { break; }
            catch (CoreRejectedException error) when (error.CoreErased || error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { await StopAsync(); break; }
            catch { /* Metadata polls may recover; no audio or paid retry is started. */ }
            try { await Task.Delay(2000, handoffLifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private int audioFailed;
    private Session? session;
    public event Action<CompanionState, string>? Changed;
    public event Action<ModeOffer>? Quote;

    public void Attach()
    {
        wake.Detected += Detected;
        wake.Unavailable += WakeUnavailable;
        audio.Captured += Captured;
        audio.Failed += AudioFailed;
        if (enableHandoffs && connection.AllowCloudProcessing) handoffLoop = Task.Run(WatchHandoffsAsync);
    }
    private void Detected(string text, bool high) { if (WakeMatcher.Accept(text, high)) Begin(false); }
    private void WakeUnavailable(string _) { lock (gate) wanted = false; Changed?.Invoke(CompanionState.Unavailable, "Local speech is unavailable. Check the English speech resources and microphone privacy."); }
    private void Captured(byte[] pcm) => Volatile.Read(ref session)?.Capture(pcm);
    private void AudioFailed() { lock (gate) { Volatile.Write(ref audioFailed, 1); active?.Cancel(); } }

    public async Task ListenAsync()
    {
        if (!connection.AllowCloudProcessing) throw new InvalidOperationException("Explicit live-voice consent is required in setup.");
        lock (gate) wanted = true;
        await StartLocalAsync();
    }
    private async Task StartLocalAsync()
    {
        await localLifecycle.WaitAsync();
        try {
            lock (gate) { if (!wanted || active is not null) return; }
            await wake.StartAsync(CancellationToken.None);
            bool keep; lock (gate) keep = wanted && active is null;
            if (keep) Changed?.Invoke(CompanionState.ListeningLocally, "Listening locally for Hey JARVIS");
            else await wake.StopAsync();
        }
        catch { lock (gate) wanted = false; Changed?.Invoke(CompanionState.Unavailable, "Local speech is unavailable. Check the English speech resources and microphone privacy."); }
        finally { localLifecycle.Release(); }
    }
    public void PushToTalk() { if (Volatile.Read(ref session) is { } live) _ = live.InterruptAsync(); else Begin(true); }
    public Task RequestModeAsync(VoiceMode mode) => Volatile.Read(ref session)?.ModeAsync(mode) ?? Task.CompletedTask;
    public Task ConfirmModeAsync(Guid id) => Volatile.Read(ref session)?.ConfirmAsync(id) ?? Task.CompletedTask;
    public Task LockChangedAsync() => Volatile.Read(ref session)?.LockChangedAsync() ?? Task.CompletedTask;

    private void Begin(bool manual, Guid? expectedSession = null)
    {
        lock (gate) {
            if (moving || active is not null || !manual && !wanted) return;
            Volatile.Write(ref audioFailed, 0);
            active = new CancellationTokenSource(); var lifetime = active;
            running = Task.Run(() => RunAsync(lifetime, expectedSession));
        }
    }
    private async Task RunAsync(CancellationTokenSource lifetime, Guid? expectedSession)
    {
        WakeCandidateResponse? claimed = null;
        Task? greeting = null;
        var failed = false;
        try {
            await localLifecycle.WaitAsync(lifetime.Token);
            try { await wake.StopAsync(); } finally { localLifecycle.Release(); }
            Changed?.Invoke(CompanionState.Connecting, "Checking audio ownership with Core");
            claimed = await core.ClaimAsync(locks.IsLocked, lifetime.Token);
            if (expectedSession is not null && (locks.IsLocked || claimed.SessionId != expectedSession)) throw new InvalidOperationException("The incoming conversation or lock state changed.");
            greeting = audio.SpeakAsync(core.OwnerDisplayName.Length == 0 ? "Hi." : "Hi, " + core.OwnerDisplayName + ".", lifetime.Token);
            var live = new Session(connection, claimed, core, audio, locks, socketFactory?.Invoke() ?? new VoiceSocket(), greeting, lifetime.Token);
            Volatile.Write(ref session, live);
            live.Changed += detail => Changed?.Invoke(CompanionState.Live, detail);
            live.Quote += offer => Quote?.Invoke(offer);
            await live.RunAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (CoreRejectedException error) when (error.Status == HttpStatusCode.Conflict) {
            Changed?.Invoke(CompanionState.Connecting, "Another device owns audio. This companion stays silent.");
        }
        catch {
            failed = true;
            if (greeting is not null && !lifetime.IsCancellationRequested) {
                try { await greeting; await audio.SpeakAsync("Sorry, I can't reach Core right now.", lifetime.Token); } catch { }
            }
        }
        finally {
            await lifetime.CancelAsync();
            if (greeting is not null) try { await greeting; } catch { }
            Volatile.Write(ref session, null); audio.Stop();
            if (claimed is not null) {
                using var release = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await core.ReleaseAsync(claimed.Voice.Lease, release.Token); } catch { /* The fenced lease expires on Core. */ }
            }
            bool resume;
            lock (gate) { active = null; lifetime.Dispose(); resume = wanted; }
            if (resume) {
                if (failed) Changed?.Invoke(CompanionState.Unavailable, "Voice start failed. Check Core connectivity, device confirmation and budgets.");
                await StartLocalAsync();
            }
            else if (failed || Volatile.Read(ref audioFailed) != 0) Changed?.Invoke(CompanionState.Unavailable, "Voice stopped. Check Core connectivity, device confirmation, microphone and budgets.");
            else Changed?.Invoke(CompanionState.Off, "Off");
        }
    }
    public async Task StopAsync()
    {
        Task? finish;
        lock (gate) { wanted = false; active?.Cancel(); finish = running; }
        audio.Stop(); await localLifecycle.WaitAsync();
        try { await wake.StopAsync(); } finally { localLifecycle.Release(); }
        if (finish is not null) await finish;
        Changed?.Invoke(CompanionState.Off, "Off");
    }
    public async ValueTask DisposeAsync()
    {
        await handoffLifetime.CancelAsync(); if (handoffLoop is not null) await handoffLoop; handoffLifetime.Dispose();
        await StopAsync(); wake.Detected -= Detected; wake.Unavailable -= WakeUnavailable; audio.Captured -= Captured; audio.Failed -= AudioFailed;
        await wake.DisposeAsync(); await audio.DisposeAsync(); core.Dispose(); localLifecycle.Dispose();
    }

    private sealed class Session(Connection connection, WakeCandidateResponse claim, CoreClient core, ICompanionAudio audio,
        ILockState locks, IVoiceSocket socket, Task greeting, CancellationToken cancelled)
    {
        private sealed record CapturedFrame(byte[] Pcm, int Window, string? Fallback);
        private readonly Channel<CapturedFrame> capture = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait });
        private readonly ConcurrentDictionary<long, long> pending = new();
        private long deadline = Stopwatch.GetTimestamp() + (long)(Math.Clamp((claim.Voice.Lease.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 0, 15) * Stopwatch.Frequency);
        private long sequence;
        private int window;
        private int ready;
        private int lockedWindow = 1;
        private int lockInterrupted;
        private string? fallbackCapture;
        private string? offeredFallback;
        private Guid? quote;
        private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancelled);
        private void Cancel() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
        public event Action<string>? Changed;
        public event Action<ModeOffer>? Quote;
        private bool Current => Stopwatch.GetTimestamp() < Interlocked.Read(ref deadline);
        public void Capture(byte[] pcm)
        {
            if (Volatile.Read(ref lockInterrupted) != 0 || locks.IsLocked && Volatile.Read(ref lockedWindow) == 0) {
                Volatile.Write(ref lockInterrupted, 1); Volatile.Write(ref ready, 0); return;
            }
            if (Volatile.Read(ref ready) == 0 || !Current || pcm.Length is <= 0 or > 24000 || pcm.Length % 2 != 0) return;
            if (!capture.Writer.TryWrite(new(pcm, Volatile.Read(ref window), Volatile.Read(ref fallbackCapture)))) Cancel();
        }
        public async Task RunAsync()
        {
            using var life = lifetime;
            Task? heartbeats = null, uploads = null, watchdog = null;
            try {
                using var opening = CancellationTokenSource.CreateLinkedTokenSource(life.Token); opening.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(connection, opening.Token);
                await socket.SendAsync(new { type = "authenticate", ticket = claim.Voice.Ticket, continuous = true, voiceModeNegotiation = true, startEveryday = true, localGreeting = true }, opening.Token);
                heartbeats = HeartbeatsAsync(); uploads = UploadAsync(); watchdog = WatchLeaseAsync();
                while (!life.IsCancellationRequested) await HandleAsync(await socket.ReceiveAsync(life.Token));
            } finally {
                Volatile.Write(ref ready, 0); audio.Stop(); await life.CancelAsync();
                foreach (var task in new[] { heartbeats, uploads, watchdog }) if (task is not null) try { await task; } catch { }
                await socket.DisposeAsync();
            }
        }
        private async Task WatchLeaseAsync()
        {
            try {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (await timer.WaitForNextTickAsync(lifetime.Token)) {
                    if (Current) continue;
                    Volatile.Write(ref ready, 0); audio.Stop(); Cancel(); return;
                }
            } catch { Cancel(); }
        }
        private async Task HeartbeatsAsync()
        {
            try {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
                while (await timer.WaitForNextTickAsync(lifetime.Token)) {
                    if (!Current || pending.Count >= 4) throw new IOException("Audio lease expired.");
                    await PingAsync();
                    await core.HeartbeatAsync(claim.Voice.Lease.DeviceId, lifetime.Token);
                }
            } catch { Cancel(); }
        }
        private async Task PingAsync()
        {
            var id = Interlocked.Increment(ref sequence); pending[id] = Stopwatch.GetTimestamp();
            await socket.SendAsync(new { type = "heartbeat", sequence = id, locked = locks.IsLocked }, lifetime.Token);
        }
        public async Task LockChangedAsync()
        {
            try {
                if (Volatile.Read(ref lockInterrupted) != 0 || locks.IsLocked && Volatile.Read(ref lockedWindow) == 0) {
                    Volatile.Write(ref lockInterrupted, 1); Volatile.Write(ref ready, 0); audio.Stop();
                }
                else if (locks.IsLocked) audio.Interrupt();
                await PingAsync();
            } catch { Cancel(); }
        }
        private async Task UploadAsync()
        {
            try {
                await foreach (var frame in capture.Reader.ReadAllAsync(lifetime.Token)) {
                    if (Volatile.Read(ref lockInterrupted) != 0 || locks.IsLocked && Volatile.Read(ref lockedWindow) == 0) {
                        Volatile.Write(ref lockInterrupted, 1); Volatile.Write(ref ready, 0); continue;
                    }
                    if (Volatile.Read(ref ready) == 0 || !Current) continue;
                    var fallback = Volatile.Read(ref fallbackCapture);
                    if (frame.Window != Volatile.Read(ref window) || frame.Fallback != fallback) continue;
                    await socket.SendAsync(fallback is null ? (object)new { type = "audio", window = frame.Window, audio = Convert.ToBase64String(frame.Pcm) }
                        : new { type = "voice_fallback_audio", captureId = fallback, audio = Convert.ToBase64String(frame.Pcm) }, lifetime.Token);
                }
            } catch { Cancel(); }
        }
        public async Task InterruptAsync()
        {
            try {
                var position = audio.Interrupt();
                if (position is { } played && window > 0 && Current)
                    await socket.SendAsync(new { type = "interrupt", window, itemId = played.ItemId, playedMs = played.Milliseconds }, lifetime.Token);
            } catch { Cancel(); }
        }
        public async Task ModeAsync(VoiceMode mode)
        {
            try { if (window > 0 && Current) await socket.SendAsync(new { type = "voice_mode", window, mode = mode.ToString() }, lifetime.Token); }
            catch { Cancel(); }
        }
        public async Task ConfirmAsync(Guid id)
        {
            try { if (quote == id && window > 0 && Current) { quote = null; await socket.SendAsync(new { type = "confirm_voice_mode", window, offerId = id }, lifetime.Token); } }
            catch { Cancel(); }
        }
        private async Task HandleAsync(JsonElement message)
        {
            switch (message.GetProperty("type").GetString()) {
                case "ready":
                    if (message.GetProperty("fence").GetInt64() != claim.Voice.Lease.Fence || message.GetProperty("sampleRate").GetInt32() != CompanionVoiceProtocol.SampleRate || !Current)
                        throw new IOException("Invalid or expired audio fence.");
                    var next = message.GetProperty("window").GetInt32(); if (next <= window) throw new IOException("Invalid voice window.");
                    window = next; Volatile.Write(ref lockedWindow, message.GetProperty("locked").GetBoolean() ? 1 : 0);
                    if (Volatile.Read(ref lockInterrupted) != 0 || locks.IsLocked && Volatile.Read(ref lockedWindow) == 0) {
                        Volatile.Write(ref lockInterrupted, 1); await LockChangedAsync(); break;
                    }
                    await greeting.WaitAsync(lifetime.Token);
                    fallbackCapture = offeredFallback = null; quote = null; await audio.StartAsync(lifetime.Token);
                    if (Volatile.Read(ref lockInterrupted) != 0 || locks.IsLocked && Volatile.Read(ref lockedWindow) == 0) {
                        Volatile.Write(ref lockInterrupted, 1); await LockChangedAsync(); break;
                    }
                    Volatile.Write(ref ready, 1);
                    Changed?.Invoke(message.GetProperty("mode").GetString() + " voice connected to Core"); break;
                case "window_renewing":
                    Volatile.Write(ref ready, 0); audio.Stop(); quote = null;
                    while (capture.Reader.TryRead(out _)) { }
                    Changed?.Invoke("Renewing the voice window"); break;
                case "lease":
                    var seconds = message.GetProperty("leaseSeconds").GetInt32();
                    if (seconds is <= 0 or > 15 || message.GetProperty("fence").GetInt64() != claim.Voice.Lease.Fence) throw new IOException("Invalid lease duration or fence.");
                    if (pending.TryRemove(message.GetProperty("sequence").GetInt64(), out var sent))
                        Interlocked.Exchange(ref deadline, sent + seconds * Stopwatch.Frequency); break;
                case "audio":
                    if (Volatile.Read(ref ready) == 1 && Current) {
                        if (Volatile.Read(ref lockInterrupted) != 0 || locks.IsLocked && Volatile.Read(ref lockedWindow) == 0) {
                            Volatile.Write(ref lockInterrupted, 1); await LockChangedAsync(); break;
                        }
                        var encoded = message.GetProperty("delta").GetString()!;
                        if (encoded.Length > 1_280_000) throw new IOException("Audio exceeded its bound.");
                        var pcm = Convert.FromBase64String(encoded); if (pcm.Length % 2 != 0) throw new IOException("Invalid PCM audio.");
                        audio.Play(message.GetProperty("itemId").GetString()!, pcm);
                    } break;
                case "interrupt": audio.Interrupt(); break;
                case "voice_mode_offer":
                    var maximum = message.GetProperty("maximumGbpForTenMinutes").GetDecimal();
                    if (maximum <= 0 || !Current || message.GetProperty("window").GetInt32() != window || window <= 0) throw new IOException("Invalid voice estimate.");
                    quote = message.GetProperty("offerId").GetGuid();
                    Quote?.Invoke(new(quote.Value, maximum)); break;
                case "voice_mode_queued": Changed?.Invoke("Voice change queued for the next window"); break;
                case "voice_mode_rejected": Changed?.Invoke("Core declined the voice change"); break;
                case "voice_fallback_offer":
                    Volatile.Write(ref ready, 0); audio.Stop(); fallbackCapture = null;
                    while (capture.Reader.TryRead(out _)) { }
                    var id = message.GetProperty("captureId").GetString()!;
                    if (id.Length is < 1 or > 80 || !Current) throw new IOException("Invalid consent offer.");
                    offeredFallback = id;
                    await greeting.WaitAsync(lifetime.Token);
                    await audio.SpeakAsync("Serious voice is unavailable. Say yes to continue in Everyday voice.", lifetime.Token);
                    await socket.SendAsync(new { type = "voice_fallback_played", captureId = id }, lifetime.Token); break;
                case "voice_fallback_capture":
                    var captureId = message.GetProperty("captureId").GetString();
                    if (captureId is null || captureId != offeredFallback || !Current) throw new IOException("Invalid consent capture.");
                    fallbackCapture = captureId;
                    await audio.StartAsync(lifetime.Token); Volatile.Write(ref ready, 1);
                    Changed?.Invoke("Listening for Everyday voice consent"); break;
                case "voice_fallback_verifying":
                    Volatile.Write(ref ready, 0); audio.Stop(); fallbackCapture = offeredFallback = null; break;
                case "window_limit":
                    Volatile.Write(ref ready, 0); await socket.SendAsync(new { type = "stop" }, lifetime.Token); break;
                case "error": throw new IOException("Core stopped voice. Check the device, privacy and budget status.");
                case "ended": Cancel(); break;
                case "transcript": break; // Audio-only companion; Core owns the transcript.
                default: throw new IOException("Unsupported voice event.");
            }
        }
    }
}
