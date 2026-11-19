using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using Jarvis.Contracts;
using Jarvis.Windows.Client;

namespace Jarvis.Windows;

internal sealed class InterviewRecorder : IAsyncDisposable
{
    private readonly Connection connection; private readonly CoreClient core; private readonly string root;
    private readonly CancellationTokenSource lifetime = new(); private readonly SemaphoreSlim end = new(1, 1);
    private WasapiInterviewCapture? capture; private RecordingUploadQueue? queue; private RecordingUploadPump? pump;
    private Channel<(string Track, byte[] Audio)>? chunks; private Task? staging, uploading, maximum;
    private string? deviceId; private bool disposed; private int privacyErased;
    public bool IsRecording { get; private set; }
    public event Action<string>? Changed;
    public event Action? StopRequested;
    public InterviewRecorder(Connection connection)
    {
        this.connection = connection; core = new(connection);
        root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis.Windows", "recordings");
    }
    public async Task<IReadOnlyList<RecordingCaptureOffer>> OffersAsync(CancellationToken ct)
    {
        deviceId = await RefreshAsync(ct);
        var offers = await core.CaptureRecordingAsync(new("poll", deviceId), ct);
        if (!offers.Ready) throw new InvalidOperationException("Encrypted recording storage is not configured on Core.");
        return offers.Offers.Where(o => o.Status == "Prepared" && o.Tracks.SequenceEqual(new[] { "user", "interviewer" })).ToArray();
    }
    public async Task StartAsync(Guid id, CancellationToken ct)
    {
        if (Volatile.Read(ref privacyErased) != 0 || IsRecording || queue is not null || new WindowsLockState().IsLocked || deviceId is null || core.Epoch is null)
            throw new InvalidOperationException("Unlock this laptop and finish any pending recording before starting another.");
        var result = await core.CaptureRecordingAsync(new("start", deviceId, id, false, true, true), ct);
        var recording = result.Recording ?? throw new InvalidOperationException("Core did not accept recording capture.");
        try {
        if (recording.Id != id || !recording.Tracks.SequenceEqual(new[] { "user", "interviewer" })) throw new InvalidOperationException("Core returned unexpected recording tracks.");
        var currentQueue = RecordingUploadQueue.Create(root, new DpapiProtector(), new(id, deviceId, core.Epoch.Value, RecordingUploadQueue.Binding(connection, deviceId, core.Epoch.Value),
            recording.Tracks, new() { ["user"] = 0, ["interviewer"] = 0 }, recording.StartedAt ?? DateTimeOffset.UtcNow));
        queue = currentQueue; pump = new(currentQueue, core);
        var channel = Channel.CreateBounded<(string Track, byte[] Audio)>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait }); chunks = channel;
        staging = Task.Run(async () => {
            await foreach (var chunk in channel.Reader.ReadAllAsync(lifetime.Token)) {
                try { if (Volatile.Read(ref privacyErased) == 0) currentQueue.Add(chunk.Track, chunk.Audio); }
                catch { Changed?.Invoke("Recording storage could not keep up. The interview is incomplete."); StopRequested?.Invoke(); throw; }
                finally { CryptographicOperations.ZeroMemory(chunk.Audio); }
            }
        });
        capture = new((track, bytes) => {
            if (Volatile.Read(ref privacyErased) != 0) { CryptographicOperations.ZeroMemory(bytes); throw new IOException("Recording stopped after Core erasure."); }
            if (!channel.Writer.TryWrite((track, bytes))) { CryptographicOperations.ZeroMemory(bytes); throw new IOException("Protected recording staging is full."); }
        }, () => StopRequested?.Invoke());
        IsRecording = true; Changed?.Invoke("JARVIS is recording — microphone and system playback");
        try { await capture.StartAsync(ct); }
        catch { await StopAsync(true); throw; }
        uploading = Task.Run(UploadAsync);
        var remaining = Math.Clamp((queue.State.StartedAt.AddHours(3) - DateTimeOffset.UtcNow).TotalSeconds, 0, 10800);
        maximum = Task.Run(async () => { try { await Task.Delay(TimeSpan.FromSeconds(remaining), lifetime.Token); StopRequested?.Invoke(); } catch (OperationCanceledException) { } });
        } catch {
            if (IsRecording) await StopAsync(true);
            if (queue is null) try { await core.CaptureRecordingAsync(new("finish", deviceId, id, FinalCounts: new Dictionary<string, int> { ["user"] = 0, ["interviewer"] = 0 }, Discontinuous: true), CancellationToken.None); } catch { }
            throw;
        }
    }
    private async Task UploadAsync()
    {
        var failures = 0;
        while (!lifetime.IsCancellationRequested && queue is not null && pump is not null) {
            try {
                if (await pump.FlushAsync(lifetime.Token)) { Changed?.Invoke("Recording stored on Core. Protected temporary audio removed."); queue = null; return; }
                failures = 0;
            }
            catch (CoreRejectedException error) when ((int)error.Status is >= 400 and < 500 && error.Status != HttpStatusCode.RequestTimeout || error.CoreErased) {
                if (error.CoreErased) ClearOriginQueues();
                Changed?.Invoke(error.CoreErased ? "Core data was erased; protected recording copies removed." : "Core refused this recording upload. Capture stopped; protected audio retained. Review it on Core before resuming.");
                StopRequested?.Invoke(); return;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception) { failures++; if (!IsRecording) Changed?.Invoke("Recording upload pending. Protected audio remains on this laptop."); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures + 1, 5)))), lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
    }
    public async Task StopAsync(bool damaged = false)
    {
        await end.WaitAsync();
        try {
            if (!IsRecording) return;
            var discontinuous = damaged;
            try { if (capture is not null) await capture.StopAsync(); }
            catch { discontinuous = true; }
            discontinuous |= capture?.Discontinuous == true; capture?.Dispose(); capture = null;
            chunks?.Writer.TryComplete();
            try { if (staging is not null) await staging; } catch { discontinuous = true; }
            if (chunks is not null) while (chunks.Reader.TryRead(out var abandoned)) CryptographicOperations.ZeroMemory(abandoned.Audio);
            IsRecording = false;
            queue?.Stop(discontinuous); Changed?.Invoke(discontinuous ? "Recording stopped with missing audio; protected upload pending." : "Recording stopped; finishing protected upload.");
            if (uploading is null && queue is not null) uploading = Task.Run(UploadAsync);
        } finally { end.Release(); }
    }
    public async Task ResumeUploadsAsync(CancellationToken ct)
    {
        if (queue is not null || IsRecording) throw new InvalidOperationException("This recording upload is already running.");
        deviceId = await RefreshAsync(ct); if (core.Epoch is null) return;
        var binding = RecordingUploadQueue.Binding(connection, deviceId, core.Epoch.Value);
        foreach (var pending in RecordingUploadQueue.Load(root, new DpapiProtector())) {
            if (pending.State.DeviceId != deviceId) continue;
            if (pending.State.Epoch != core.Epoch && pending.State.Binding == RecordingUploadQueue.Binding(connection, deviceId, pending.State.Epoch)) { pending.Clear(); continue; }
            if (pending.State.Binding != binding) continue;
            var uploader = new RecordingUploadPump(pending, core);
            while (!await uploader.FlushAsync(ct)) ct.ThrowIfCancellationRequested();
        }
        Changed?.Invoke("Protected recording uploads checked.");
    }
    private async Task<string> RefreshAsync(CancellationToken ct)
    {
        try { return await core.RefreshAsync(ct); }
        catch (Exception error) when (core.PrivacyChanged || error is CoreRejectedException { CoreErased: true }) {
            ClearOriginQueues();
            throw;
        }
    }
    private void ClearOriginQueues()
    {
        // First serialize with the active queue's staging writer, then clear older jobs
        // for this origin as well. Ordinary credential refusal must retain them.
        Volatile.Write(ref privacyErased, 1); StopRequested?.Invoke();
        var active = queue; queue = null;
        RecordingUploadQueue.EraseOrigin(root, new DpapiProtector(), connection, active);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; await StopAsync(); lifetime.Cancel();
        foreach (var task in new[] { uploading, maximum }) if (task is not null) try { await task; } catch (OperationCanceledException) { }
        core.Dispose(); lifetime.Dispose(); end.Dispose();
    }
}
