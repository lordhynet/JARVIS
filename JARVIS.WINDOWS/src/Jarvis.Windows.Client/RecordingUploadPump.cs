using Jarvis.Contracts;

namespace Jarvis.Windows.Client;

public interface IRecordingCaptureTransport
{
    Task<RecordingCaptureResult> SendAsync(RecordingCaptureRequest request, CancellationToken ct);
}
public sealed class RecordingUploadPump(RecordingUploadQueue queue, IRecordingCaptureTransport transport)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<bool> FlushAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            // Release capture ownership promptly, even when audio is still uploading. Gaps stay
            // Incomplete until a later identical finish observes all acknowledged chunks.
            if (queue.State.Stopped) await FinishAsync(ct);
            foreach (var chunk in queue.Pending()) {
                var response = await transport.SendAsync(new("chunk", queue.State.DeviceId, queue.State.RecordingId,
                    Track: chunk.Track, Sequence: chunk.Sequence, Sha256: chunk.Sha256, AudioBase64: chunk.AudioBase64, Discontinuous: queue.State.Discontinuous), ct);
                var ack = response.Acknowledged?.SingleOrDefault(a => a.Track == chunk.Track && a.Sequence == chunk.Sequence && a.Sha256 == chunk.Sha256)
                    ?? throw new InvalidOperationException("Core did not acknowledge this recording chunk. The protected copy was retained.");
                if (response.Recording?.Id != queue.State.RecordingId) throw new InvalidOperationException("Core returned a different recording identity.");
                queue.Acknowledge(ack);
            }
            if (!queue.State.Stopped || queue.Pending(1).Count > 0) return false;
            var finished = await FinishAsync(ct);
            if (finished.Recording?.Id != queue.State.RecordingId || finished.Recording.Status is not ("Complete" or "Incomplete"))
                throw new InvalidOperationException("Core has not accepted the recording end. The protected queue was retained.");
            queue.Clear(); return true;
        } finally { gate.Release(); }
    }
    private Task<RecordingCaptureResult> FinishAsync(CancellationToken ct) => transport.SendAsync(new("finish", queue.State.DeviceId, queue.State.RecordingId,
        FinalCounts: new Dictionary<string, int>(queue.State.Counts), Discontinuous: queue.State.Discontinuous), ct);
}

public sealed class RecordingSampleBuffer(Action<byte[]> complete)
{
    private readonly byte[] buffer = new byte[1_920_000]; private int used;
    public void Add(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0) throw new InvalidOperationException("Recording PCM must contain whole samples.");
        while (!pcm.IsEmpty) {
            var count = Math.Min(buffer.Length - used, pcm.Length); pcm[..count].CopyTo(buffer.AsSpan(used)); used += count; pcm = pcm[count..];
            if (used == buffer.Length) Flush();
        }
    }
    public void Flush()
    {
        if (used == 0) return;
        var wav = RecordingWave.Wrap(buffer.AsSpan(0, used)); buffer.AsSpan(0, used).Clear(); used = 0; complete(wav);
    }
    public void Discard() { Array.Clear(buffer); used = 0; }
}
