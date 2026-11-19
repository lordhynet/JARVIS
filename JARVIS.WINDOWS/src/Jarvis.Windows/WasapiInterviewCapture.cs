using System.Diagnostics;
using System.Runtime.InteropServices;
using Jarvis.Windows.Client;

namespace Jarvis.Windows;

// A shared WASAPI client per endpoint: microphone and default-render loopback never mix.
internal sealed class WasapiInterviewCapture(Action<string, byte[]> chunk, Action failed) : IDisposable
{
    private readonly CancellationTokenSource stop = new(); private readonly List<Thread> threads = [];
    private readonly List<Task> completions = []; private long origin; private int damaged;
    public bool Discontinuous => Volatile.Read(ref damaged) != 0;
    public async Task StartAsync(CancellationToken ct)
    {
        origin = Stopwatch.GetTimestamp(); var starts = new List<Task>();
        foreach (var loopback in new[] { false, true }) {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); starts.Add(ready.Task); completions.Add(finished.Task);
            var thread = new Thread(() => Capture(loopback, ready, finished)) { IsBackground = true, Name = loopback ? "JARVIS interview loopback" : "JARVIS interview microphone" };
            thread.SetApartmentState(ApartmentState.MTA); threads.Add(thread); thread.Start();
        }
        try { await Task.WhenAll(starts).WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch { stop.Cancel(); await StopAsync(); throw; }
    }
    private void Capture(bool loopback, TaskCompletionSource ready, TaskCompletionSource finished)
    {
        IRecordingDeviceEnumerator? enumerator = null; IRecordingDevice? device = null; IRecordingAudioClient? audio = null; IRecordingCaptureClient? capture = null;
        var track = loopback ? "interviewer" : "user"; var buffer = new RecordingSampleBuffer(wav => chunk(track, wav)); var next = 0L; var packetSeen = false;
        var origin100ns = origin * (10_000_000d / Stopwatch.Frequency);
        try {
            enumerator = (IRecordingDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
            Check(enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 1, out device)); var iid = typeof(IRecordingAudioClient).GUID;
            Check(device.GetId(out var endpointId));
            Check(device.Activate(ref iid, 23, IntPtr.Zero, out var client)); audio = (IRecordingAudioClient)client;
            var format = new RecordingWaveFormat { Tag = 1, Channels = 1, Rate = 16000, BytesPerSecond = 32000, BlockAlign = 2, Bits = 16 };
            var pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<RecordingWaveFormat>());
            try { Marshal.StructureToPtr(format, pointer, false); var session = Guid.NewGuid();
                Check(audio.Initialize(0, 0x88000000u | (loopback ? 0x00020000u : 0), 1_000_000, 0, pointer, ref session)); }
            finally { Marshal.FreeCoTaskMem(pointer); }
            iid = typeof(IRecordingCaptureClient).GUID; Check(audio.GetService(ref iid, out var service)); capture = (IRecordingCaptureClient)service;
            if (SetThreadExecutionState(0x80000001) == 0) throw new IOException("Windows could not keep recording active while the screen locks.");
            Check(audio.Start()); ready.TrySetResult(); var checkedEndpointAt = Stopwatch.GetTimestamp();
            void Pad(long target)
            {
                target = Math.Clamp(target, 0, 16000L * 10800); var gap = target - next;
                if (gap > 32000) throw new IOException("Recording capture paused; missing audio cannot be treated as a complete interview.");
                if (gap > 0) { buffer.Add(new byte[checked((int)gap * 2)]); next = target; }
            }
            while (true) {
                var ending = stop.IsCancellationRequested || Stopwatch.GetElapsedTime(origin) >= TimeSpan.FromHours(3);
                if (!ending && Stopwatch.GetElapsedTime(checkedEndpointAt) >= TimeSpan.FromSeconds(1)) {
                    Check(enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 1, out var currentEndpoint));
                    try { Check(currentEndpoint.GetId(out var currentId)); if (currentId != endpointId) throw new IOException("Default recording audio device changed."); }
                    finally { Marshal.ReleaseComObject(currentEndpoint); }
                    checkedEndpointAt = Stopwatch.GetTimestamp();
                }
                if (ending) Check(audio.Stop());
                Check(capture.GetNextPacketSize(out var packet));
                while (packet > 0) {
                    Check(capture.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc));
                    try {
                        if (frames > 16000) throw new IOException("WASAPI recording packet exceeded its bound.");
                        var invalidTime = (flags & 4) != 0;
                        if (invalidTime || packetSeen && (flags & 1) != 0) Interlocked.Exchange(ref damaged, 1);
                        var position = invalidTime ? (long)(Stopwatch.GetElapsedTime(origin).TotalSeconds * 16000) - frames : (long)Math.Round((qpc - origin100ns) * .0016);
                        position = Math.Max(0, position); Pad(position);
                        var bytes = new byte[checked((int)frames * 2)];
                        if ((flags & 2) == 0 && data != IntPtr.Zero) Marshal.Copy(data, bytes, 0, bytes.Length);
                        var overlap = Math.Max(0, next - position);
                        if (overlap > 160 && packetSeen) Interlocked.Exchange(ref damaged, 1);
                        if (overlap < frames) { var offset = checked((int)overlap * 2); var length = (int)Math.Min(bytes.Length - offset, (16000L * 10800 - next) * 2);
                            if (length > 0) { buffer.Add(bytes.AsSpan(offset, length)); next += length / 2; } }
                        Array.Clear(bytes); packetSeen = true;
                    } finally { Check(capture.ReleaseBuffer(frames)); }
                    Check(capture.GetNextPacketSize(out packet));
                }
                if (ending) { Pad((long)(Stopwatch.GetElapsedTime(origin).TotalSeconds * 16000)); break; }
                Pad((long)(Stopwatch.GetElapsedTime(origin).TotalSeconds * 16000) - 3200);
                stop.Token.WaitHandle.WaitOne(10);
            }
            buffer.Flush();
        }
        catch (Exception error) {
            Interlocked.Exchange(ref damaged, 1); ready.TrySetException(error); stop.Cancel(); buffer.Discard(); failed();
        }
        finally {
            SetThreadExecutionState(0x80000000);
            foreach (var value in new object?[] { capture, audio, device, enumerator }) if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
            finished.TrySetResult();
        }
    }
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SetThreadExecutionState(uint flags);
    public async Task StopAsync() { stop.Cancel(); await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(5)); }
    public void Dispose() { stop.Cancel(); stop.Dispose(); }
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct RecordingWaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort BlockAlign, Bits, Extra; }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IRecordingDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IRecordingDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingAudioClient
{
    [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, ref Guid session);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
    [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}
[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRecordingCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
