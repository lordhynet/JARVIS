using System.Buffers.Binary;
using System.Security.Cryptography;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;

namespace Jarvis.Api.Voice;

// Window-local PCM only. No raw audio is persisted or exported; disposal clears the bounded buffer.
internal sealed class SpeechAudioMeter(int maximumBytes) : IDisposable
{
    private readonly object sync = new();
    private MemoryStream? audio = new();
    public void Append(byte[] pcm)
    {
        lock (sync) {
            if (audio is null) return;
            if (audio.Length + pcm.Length > maximumBytes) { Clear(); return; }
            audio.Write(pcm);
        }
    }
    public SpeechTiming? Measure(int startMs, int endMs)
    {
        lock (sync) {
            if (audio is null || startMs < 0 || endMs <= startMs || endMs - startMs > 180000) return null;
            var start = (long)startMs * 48; var end = (long)endMs * 48;
            if (end > audio.Length || start > int.MaxValue) return null;
            return Analyse(audio.GetBuffer().AsSpan((int)start, (int)(end - start)), 24000);
        }
    }
    internal static SpeechTiming? Analyse(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        if (sampleRate is not (16000 or 24000) || pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length > sampleRate * 2 * 180) return null;
        var frameBytes = sampleRate / 50 * 2; var frames = pcm.Length / frameBytes;
        if (frames == 0) return null;
        var quiet = new bool[frames];
        for (var frame = 0; frame < frames; frame++) {
            double energy = 0; var data = pcm.Slice(frame * frameBytes, frameBytes);
            for (var i = 0; i < data.Length; i += 2) { var sample = BinaryPrimitives.ReadInt16LittleEndian(data[i..]); energy += (double)sample * sample; }
            quiet[frame] = Math.Sqrt(energy / (frameBytes / 2)) / short.MaxValue < .012;
        }
        var first = Array.FindIndex(quiet, q => !q); var last = Array.FindLastIndex(quiet, q => !q);
        if (first < 0) return null; // Silence alone supplies no measured speaking interval.
        var count = 0;
        for (var i = first; i <= last;) {
            if (!quiet[i]) { i++; continue; }
            var at = i; while (i <= last && quiet[i]) i++;
            if (i - at >= 13) count += i - at; // At least 260 ms, excluding leading/trailing silence.
        }
        return new(pcm.Length / (sampleRate * 2d), count / 50d, SpeechDelivery.PcmQuietMethod);
    }
    private void Clear()
    {
        if (audio is null) return;
        CryptographicOperations.ZeroMemory(audio.GetBuffer()); audio.Dispose(); audio = null;
    }
    public void Dispose() { lock (sync) Clear(); }
}
