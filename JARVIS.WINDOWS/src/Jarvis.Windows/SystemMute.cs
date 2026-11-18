using System.Runtime.InteropServices;
using Jarvis.Windows.Client;

namespace Jarvis.Windows;

// Only the fixed default playback endpoint and an explicit true mute state are exposed.
internal static class SystemMute
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private sealed class Enumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice { [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value); }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVolume {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr value);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr value);
        [PreserveSig] int GetChannelCount(out uint value);
        [PreserveSig] int SetMasterVolumeLevel(float value, IntPtr context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float value, IntPtr context);
        [PreserveSig] int GetMasterVolumeLevel(out float value);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float value);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float value, IntPtr context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float value, IntPtr context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float value);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float value);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool value, IntPtr context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool value);
    }
    public static bool Set(ILockState locks, CancellationToken ct)
    {
        object? enumerator = null, device = null, control = null;
        try {
            enumerator = new Enumerator();
            Marshal.ThrowExceptionForHR(((IEnumerator)enumerator).GetDefaultAudioEndpoint(0, 1, out var endpoint)); device = endpoint;
            var id = typeof(IVolume).GUID; Marshal.ThrowExceptionForHR(endpoint.Activate(ref id, 23, IntPtr.Zero, out var activated)); control = activated;
            ct.ThrowIfCancellationRequested(); if (locks.IsLocked) throw new InvalidOperationException("Workstation locked before muting.");
            var volume = (IVolume)control; Marshal.ThrowExceptionForHR(volume.SetMute(true, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted)); return muted;
        } finally {
            if (control is not null) Marshal.ReleaseComObject(control);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }
}
