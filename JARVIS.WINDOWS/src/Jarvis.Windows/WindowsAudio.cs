using System.Media;
using System.Runtime.InteropServices;
using global::Windows.Foundation;
using global::Windows.Media;
using global::Windows.Media.Audio;
using global::Windows.Media.Capture;
using global::Windows.Media.MediaProperties;
using global::Windows.Media.Render;
using global::Windows.Media.SpeechSynthesis;
using Jarvis.Windows.Client;

namespace Jarvis.Windows;

[ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess { void GetBuffer(out byte* buffer, out uint capacity); }

internal sealed class WindowsAudio : ICompanionAudio
{
    private readonly SemaphoreSlim setup = new(1);
    private readonly object playback = new();
    private readonly Queue<(string Item, byte[] Bytes)> queue = new();
    private AudioGraph? graph;
    private AudioDeviceInputNode? microphone;
    private AudioFrameOutputNode? captured;
    private AudioFrameInputNode? rendered;
    private AudioDeviceOutputNode? speaker;
    private volatile bool started;
    private volatile bool faulted;
    private int offset, buffered;
    private string? item;
    private long playedSamples;
    public event Action<byte[]>? Captured;
    public event Action? Failed;

    public async Task StartAsync(CancellationToken ct)
    {
        await setup.WaitAsync(ct);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct); opening.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            if (started) return;
            PackageIdentity.Require();
            if (faulted) { DisposeGraph(); faulted = false; }
            if (graph is null) {
                var format = AudioEncodingProperties.CreatePcm(24000, 1, 32); format.Subtype = MediaEncodingSubtypes.Float;
                var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Speech) {
                    EncodingProperties = format, DesiredRenderDeviceAudioProcessing = global::Windows.Media.AudioProcessing.Default
                }).AsTask(opening.Token);
                if (created.Status != AudioGraphCreationStatus.Success) throw new InvalidOperationException("Windows audio output is unavailable.");
                graph = created.Graph;
                if (graph.EncodingProperties.SampleRate != 24000 || graph.EncodingProperties.ChannelCount != 1 || graph.EncodingProperties.BitsPerSample != 32)
                    throw new InvalidOperationException("Windows could not provide 24 kHz mono audio.");
                var input = await graph.CreateDeviceInputNodeAsync(MediaCategory.Speech, format).AsTask(opening.Token);
                if (input.Status != AudioDeviceNodeCreationStatus.Success) throw new InvalidOperationException("Microphone access is unavailable. Check Windows privacy settings.");
                microphone = input.DeviceInputNode; captured = graph.CreateFrameOutputNode(format); microphone.AddOutgoingConnection(captured);
                var output = await graph.CreateDeviceOutputNodeAsync().AsTask(opening.Token);
                if (output.Status != AudioDeviceNodeCreationStatus.Success) throw new InvalidOperationException("Windows audio output is unavailable.");
                speaker = output.DeviceOutputNode; rendered = graph.CreateFrameInputNode(format); rendered.AddOutgoingConnection(speaker);
                graph.QuantumStarted += CaptureQuantum;
                rendered.QuantumStarted += RenderQuantum;
            }
            opening.Token.ThrowIfCancellationRequested(); started = true; graph.Start();
        } catch { DisposeGraph(); throw; }
        finally { setup.Release(); }
    }
    private unsafe void CaptureQuantum(AudioGraph sender, object args)
    {
        try { CaptureFrame(); }
        catch { started = false; faulted = true; Failed?.Invoke(); }
    }
    private unsafe void CaptureFrame()
    {
        if (!started || captured is null) return;
        using var frame = captured.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        ((IMemoryBufferByteAccess)reference).GetBuffer(out var bytes, out var capacity);
        var length = Math.Min(buffer.Length, capacity);
        if (length == 0 || length % 4 != 0 || length > 48000) return;
        var pcm = new byte[length / 2]; var floats = (float*)bytes;
        for (var i = 0; i < pcm.Length / 2; i++) {
            var value = float.IsFinite(floats[i]) ? Math.Clamp(floats[i], -1f, 1f) : 0f;
            var sample = (short)(value * 32767);
            pcm[i * 2] = (byte)sample; pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        Captured?.Invoke(pcm);
    }
    public void Play(string itemId, ReadOnlySpan<byte> pcm)
    {
        if (!started || pcm.Length == 0 || pcm.Length % 2 != 0) return;
        lock (playback) {
            if (buffered + pcm.Length > 192000) throw new IOException("Audio playback cannot keep up.");
            queue.Enqueue((itemId, pcm.ToArray())); buffered += pcm.Length;
        }
    }
    private unsafe void RenderQuantum(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
    {
        try { RenderFrame(sender, args); }
        catch { started = false; faulted = true; Failed?.Invoke(); }
    }
    private unsafe void RenderFrame(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
    {
        if (!started || args.RequiredSamples == 0) return;
        using var frame = new AudioFrame((uint)(args.RequiredSamples * sizeof(float)));
        using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
        using (var reference = buffer.CreateReference()) {
            ((IMemoryBufferByteAccess)reference).GetBuffer(out var bytes, out var capacity);
            var count = Math.Min(args.RequiredSamples, (int)capacity / sizeof(float)); var floats = (float*)bytes;
            lock (playback) {
                for (var i = 0; i < count; i++) {
                    floats[i] = 0;
                    if (!queue.TryPeek(out var head)) continue;
                    if (item != head.Item) { item = head.Item; playedSamples = 0; }
                    var sample = (short)(head.Bytes[offset] | head.Bytes[offset + 1] << 8);
                    floats[i] = sample / 32768f; offset += 2; buffered -= 2; playedSamples++;
                    if (offset == head.Bytes.Length) { queue.Dequeue(); offset = 0; }
                }
            }
        }
        sender.AddFrame(frame);
    }
    public PlaybackPosition? Interrupt()
    {
        lock (playback) {
            var position = item is null ? (PlaybackPosition?)null : new(item, playedSamples * 1000 / 24000);
            queue.Clear(); offset = buffered = 0; item = null; playedSamples = 0;
            try { rendered?.Reset(); } catch { /* Graph teardown already stops playback. */ }
            return position;
        }
    }
    public void Stop() { started = false; try { graph?.Stop(); } catch { } Interrupt(); }
    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        using var speaking = CancellationTokenSource.CreateLinkedTokenSource(ct); speaking.CancelAfter(TimeSpan.FromSeconds(10));
        using var synthesizer = new SpeechSynthesizer();
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(speaking.Token);
        using var input = stream.AsStreamForRead();
        using var player = new SoundPlayer(input);
        using var cancel = speaking.Token.Register(player.Stop);
        await Task.Run(player.PlaySync, speaking.Token); speaking.Token.ThrowIfCancellationRequested();
    }
    private void DisposeGraph()
    {
        Stop();
        if (graph is not null) graph.QuantumStarted -= CaptureQuantum;
        if (rendered is not null) rendered.QuantumStarted -= RenderQuantum;
        microphone?.Dispose(); captured?.Dispose(); rendered?.Dispose(); speaker?.Dispose(); graph?.Dispose();
        microphone = null; captured = null; rendered = null; speaker = null; graph = null;
    }
    public ValueTask DisposeAsync()
    {
        DisposeGraph(); setup.Dispose();
        return ValueTask.CompletedTask;
    }
}
