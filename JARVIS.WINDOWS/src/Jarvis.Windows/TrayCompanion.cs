using System.Runtime.InteropServices;
using Jarvis.Contracts;
using Jarvis.Windows.Client;
using Microsoft.Win32;

namespace Jarvis.Windows;

internal sealed class TrayCompanion : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem status = new("Off") { Enabled = false };
    private readonly ToolStripMenuItem detail = new("Left-click the tray icon to turn listening off") { Enabled = false };
    private readonly CredentialVault vault = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jarvis.Windows", "connection.dpapi"), new DpapiProtector());
    private readonly Hotkeys hotkeys;
    private CompanionController? controller;
    private Connection? connection;
    private WindowsCommandPump? commands;
    private bool busy, exiting;

    public TrayCompanion()
    {
        _ = dispatcher.Handle;
        var menu = new ContextMenuStrip();
        menu.Items.Add(status); menu.Items.Add(detail); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Off — stop microphone and audio (Ctrl+Alt+Shift+J)", null, (_, _) => Halt());
        Add(menu, "Listen locally for Hey JARVIS", async () => { if (controller is null) await SetupAsync(); else await controller.ListenAsync(); });
        Add(menu, "Push to talk / interrupt (Ctrl+Alt+J)", () => { controller?.PushToTalk(); return Task.CompletedTask; });
        Add(menu, "Everyday voice next window", () => controller?.RequestModeAsync(VoiceMode.Everyday) ?? Task.CompletedTask);
        Add(menu, "Request Serious voice price…", () => controller?.RequestModeAsync(VoiceMode.Serious) ?? Task.CompletedTask);
        menu.Items.Add(new ToolStripSeparator());
        Add(menu, "Connection and startup…", SetupAsync);
        Add(menu, "Stop laptop actions", async () => { if (commands is not null) { await commands.DisposeAsync(); commands = null; } });
        Add(menu, "Forget this connection", async () => {
            if (MessageBox.Show("Remove this computer's saved connection? Revoke the device on Core if it must lose access.",
                "JARVIS", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await DisconnectAsync(); vault.Clear(); connection = null; await StartupPreference.SetAsync(false);
        });
        Add(menu, "Exit", ExitAsync);
        tray = new NotifyIcon { Text = "JARVIS — Off", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Halt(); };
        hotkeys = new Hotkeys(() => Post(() => controller?.PushToTalk()), () => Post(Halt));
        SystemEvents.SessionSwitch += SessionSwitch;
        Application.ApplicationExit += ApplicationExit;
        Post(() => Run(InitializeAsync));
    }
    private void Add(ContextMenuStrip menu, string text, Func<Task> action) => menu.Items.Add(text, null, (_, _) => Run(action));
    private async void Halt() { try { if (controller is not null) await controller.StopAsync(); } catch { SetState(CompanionState.Unavailable, "Voice stopped. Check the microphone before listening again."); } }
    private void Post(Action action) { if (!exiting && !dispatcher.IsDisposed) try { dispatcher.BeginInvoke(action); } catch (InvalidOperationException) { } }
    private async void Run(Func<Task> action)
    {
        if (busy || exiting) return;
        busy = true;
        try { await action(); }
        catch { if (!exiting) MessageBox.Show("JARVIS could not complete that action. Check the connection, MSIX identity, microphone privacy and English speech resources.", "JARVIS", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { busy = false; }
    }
    private async Task InitializeAsync()
    {
        try { connection = vault.Read(); }
        catch { SetState(CompanionState.Unavailable, "Saved connection cannot be opened. Configure this computer again."); }
        if (connection is not null) {
            Connect(connection);
            if (connection.ListenOnStartup) await controller!.ListenAsync();
        }
        if (!hotkeys.Registered) tray.ShowBalloonTip(5000, "JARVIS hotkeys unavailable", "Another app owns a shortcut. Use the tray controls.", ToolTipIcon.Warning);
    }
    private void Connect(Connection value)
    {
        controller = new(value, new CoreClient(value), new LocalWakeListener(), new WindowsAudio(), new WindowsLockState());
        controller.Changed += (state, text) => Post(() => SetState(state, text));
        controller.Quote += offer => Post(() => Run(async () => {
            if (new WindowsLockState().IsLocked) return;
            if (MessageBox.Show($"Serious voice costs up to £{offer.MaximumGbpForTenMinutes:0.00} for ten minutes. Core's budget limits still apply. Queue it for the next window?",
                "Confirm Serious voice", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                await (controller?.ConfirmModeAsync(offer.Id) ?? Task.CompletedTask);
        }));
        controller.Attach();
        if (value.EnableGovernedActions) {
            try {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis.Windows");
                var settings = WindowsActionSettings.Load(Path.Combine(folder, "actions.json")); var locks = new WindowsLockState();
                Task Notify(string text, CancellationToken ct) {
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Post(() => { try { ct.ThrowIfCancellationRequested(); if (locks.IsLocked) throw new InvalidOperationException("Workstation locked.");
                        tray.ShowBalloonTip(5000, "JARVIS", text, ToolTipIcon.Info); ready.TrySetResult(); } catch (Exception e) { ready.TrySetException(e); } });
                    return ready.Task.WaitAsync(ct);
                }
                commands = new(value, settings, new WindowsActions(settings, locks, Notify), locks, Path.Combine(folder, "commands.dpapi"), text => Post(() => detail.Text = text));
            } catch { detail.Text = "Laptop actions are off: configure the owner allow-lists and protected journal first."; }
        }
    }
    private async Task SetupAsync()
    {
        await DisconnectAsync();
        using var form = new ConnectionForm(connection);
        if (form.ShowDialog() == DialogResult.OK && form.Result is { } saved) {
            vault.Save(saved); connection = saved;
        }
        if (connection is not null) Connect(connection);
    }
    private async Task DisconnectAsync()
    {
        if (commands is not null) { await commands.DisposeAsync(); commands = null; }
        if (controller is not null) { var previous = controller; controller = null; await previous.DisposeAsync(); }
        SetState(CompanionState.Off, "Off");
    }
    private void SetState(CompanionState state, string text)
    {
        status.Text = state switch { CompanionState.ListeningLocally => "Listening locally", CompanionState.Live => "Live with Core", _ => state.ToString() };
        detail.Text = text;
        var tooltip = "JARVIS — " + status.Text;
        tray.Text = tooltip[..Math.Min(tooltip.Length, 63)];
        tray.Icon = state switch { CompanionState.ListeningLocally => SystemIcons.Information, CompanionState.Live => SystemIcons.Shield,
            CompanionState.Connecting => SystemIcons.Information, CompanionState.Unavailable => SystemIcons.Warning, _ => SystemIcons.Application };
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs args) => Post(async () => {
        try { if (controller is not null) await controller.LockChangedAsync(); } catch { Halt(); }
    });
    private async Task ExitAsync()
    {
        await DisconnectAsync(); exiting = true;
        SystemEvents.SessionSwitch -= SessionSwitch; Application.ApplicationExit -= ApplicationExit;
        tray.Visible = false; hotkeys.Dispose(); tray.Dispose(); dispatcher.Dispose(); ExitThread();
    }
    private void ApplicationExit(object? sender, EventArgs args) { tray.Visible = false; hotkeys.Dispose(); }
}

internal sealed class Hotkeys : NativeWindow, IDisposable
{
    private const int PushToTalkId = 1, StopId = 2, WmHotkey = 0x0312;
    private const uint CtrlAltNoRepeat = 0x4003, CtrlAltShiftNoRepeat = 0x4007;
    private readonly Action push, stop;
    public bool Registered { get; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    public Hotkeys(Action push, Action stop)
    {
        this.push = push; this.stop = stop; CreateHandle(new CreateParams());
        var ptt = RegisterHotKey(Handle, PushToTalkId, CtrlAltNoRepeat, (uint)Keys.J);
        var halt = RegisterHotKey(Handle, StopId, CtrlAltShiftNoRepeat, (uint)Keys.J);
        Registered = ptt && halt;
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey) { if (message.WParam == PushToTalkId) push(); else if (message.WParam == StopId) stop(); }
        base.WndProc(ref message);
    }
    public void Dispose() { if (Handle == IntPtr.Zero) return; UnregisterHotKey(Handle, PushToTalkId); UnregisterHotKey(Handle, StopId); DestroyHandle(); }
}
