using System.Diagnostics;
using System.Runtime.InteropServices;
using global::Windows.Media.Control;
using Jarvis.Contracts;
using Jarvis.Windows.Client;

namespace Jarvis.Windows;

internal sealed class WindowsActions(WindowsActionSettings settings, ILockState locks, Func<string, CancellationToken, Task> notify) : IWindowsActionPlatform
{
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LockWorkStation();
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyboardInput Keyboard; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    public async Task<WindowsActionResult> ExecuteAsync(string capability, WindowsActionArguments arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (locks.IsLocked && !WindowsCommandProtocol.AllowedLocked(capability)) throw new InvalidOperationException("Workstation is locked.");
        switch (capability) {
            case WindowsCommandProtocol.App:
                if (!settings.Apps.TryGetValue(arguments.TargetId!, out var app)) throw new InvalidOperationException("App ID is not locally allowed.");
                using (SafeWindowsPaths.OpenAllowedApp(app)) {
                    ct.ThrowIfCancellationRequested(); if (locks.IsLocked) throw new InvalidOperationException("Workstation locked.");
                    using var process = Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app.Path)! });
                    return new(process is not null, "Windows accepted launch of the owner-allowed app; its UI state was not inspected.");
                }
            case WindowsCommandProtocol.Url:
                if (!settings.Urls.TryGetValue(arguments.TargetId!, out var address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
                    throw new InvalidOperationException("URL ID is not locally allowed.");
                ct.ThrowIfCancellationRequested(); if (locks.IsLocked) throw new InvalidOperationException("Workstation locked.");
                using (Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })) { }
                return new(true, "Windows accepted the exact owner-allowed HTTPS URL; page content was not inspected.");
            case WindowsCommandProtocol.Media:
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
                var session = manager.GetCurrentSession(); if (session is null) return new(false, "No controllable media session is available.");
                ct.ThrowIfCancellationRequested();
                var accepted = arguments.Action switch { "play" => await session.TryPlayAsync().AsTask(ct), "pause" => await session.TryPauseAsync().AsTask(ct), "next" => await session.TrySkipNextAsync().AsTask(ct), _ => false };
                return new(accepted, accepted ? "The media session accepted the requested control." : "The media session refused the requested control.");
            case WindowsCommandProtocol.Volume:
                if (arguments.Action == "mute") return new(SystemMute.Set(locks, ct), "Windows default playback endpoint mute-state result.");
                if (IntPtr.Size != 8) throw new PlatformNotSupportedException("The reviewed volume executor requires a 64-bit process.");
                var key = arguments.Action switch { "up" => (ushort)0xAF, "down" => (ushort)0xAE, _ => throw new InvalidOperationException("Unknown volume action.") };
                var inputs = Enumerable.Range(0, arguments.Steps).SelectMany(_ => new[] { new Input { Type = 1, Keyboard = new() { Key = key } }, new Input { Type = 1, Keyboard = new() { Key = key, Flags = 2 } } }).ToArray();
                ct.ThrowIfCancellationRequested(); if (locks.IsLocked) throw new InvalidOperationException("Workstation locked.");
                return new(SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length, "Windows volume-key dispatch result; the final volume level was not measured.");
            case WindowsCommandProtocol.Lock:
                if (locks.IsLocked) return new(true, "The local conservative lock guard already reports locked.");
                ct.ThrowIfCancellationRequested(); return new(LockWorkStation(), "Windows lock request initiated; final OS lock state requires the normal lock notification.");
            case WindowsCommandProtocol.Notification:
                await notify(arguments.Message!, ct); return new(true, "Notification submitted to this computer; user visibility was not measured.");
            case WindowsCommandProtocol.Files:
                if (!settings.Folders.TryGetValue(arguments.TargetId!, out var folder)) throw new InvalidOperationException("Folder ID is not locally allowed.");
                var names = await Task.Run(() => SafeWindowsPaths.FindNames(folder, arguments.Name!, ct, () => !locks.IsLocked), ct);
                ct.ThrowIfCancellationRequested(); if (locks.IsLocked) throw new InvalidOperationException("Search results were discarded after locking.");
                return new(true, "Bounded filename matches only; no contents or paths were returned.", names);
            default: throw new InvalidOperationException("Windows capability is not installed.");
        }
    }
}

internal sealed class WindowsCommandPump : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly CoreClient core;
    private readonly Task running;
    public WindowsCommandPump(Connection connection, WindowsActionSettings settings, IWindowsActionPlatform platform, ILockState locks, string journalPath, Action<string> status)
    {
        if (!connection.EnableGovernedActions || connection.DeviceId is null || connection.PrivacyEpoch is null) throw new InvalidOperationException("Explicit action opt-in and verified enrolment are required.");
        _ = settings; core = new(connection);
        var binding = DeviceCommandProof.CredentialHash(connection.Origin.AbsoluteUri + ":" + connection.DeviceId + ":" + connection.PrivacyEpoch + ":" + connection.Credential);
        var executor = new WindowsCommandExecutor(connection.DeviceId, connection.Credential, core, platform, locks,
            new(journalPath, binding, new DpapiProtector()), TimeProvider.System);
        running = Run();
        async Task Run() {
            try {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
                while (await timer.WaitForNextTickAsync(stop.Token)) {
                    await core.RefreshAsync(stop.Token); await executor.FlushAsync(stop.Token);
                    var command = await core.ClaimCommandAsync(Guid.NewGuid(), stop.Token);
                    if (command is not null) { await executor.ExecuteAsync(command, stop.Token); status("Laptop command result recorded with Core."); }
                }
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch { status("Laptop actions stopped. Check Core and the protected command journal before enabling again."); }
        }
    }
    public async ValueTask DisposeAsync() { await stop.CancelAsync(); await running; core.Dispose(); stop.Dispose(); }
}
