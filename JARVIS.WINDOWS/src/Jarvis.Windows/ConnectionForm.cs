using global::Windows.ApplicationModel;
using Jarvis.Windows.Client;

namespace Jarvis.Windows;

internal static class StartupPreference
{
    public const string TaskId = "JarvisStartup";
    public static async Task SetAsync(bool enabled)
    {
        PackageIdentity.Require();
        var task = await StartupTask.GetAsync(TaskId);
        if (!enabled) { task.Disable(); return; }
        var state = await task.RequestEnableAsync();
        if (state != StartupTaskState.Enabled) throw new InvalidOperationException("Windows has not enabled startup. Check Settings > Apps > Startup.");
    }
}

internal sealed class ConnectionForm : Form
{
    private readonly TextBox origin = new() { Dock = DockStyle.Fill };
    private readonly TextBox credential = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 69 };
    private readonly TextBox pin = new() { Dock = DockStyle.Fill, MaxLength = 64 };
    private readonly CheckBox consent = new() { Text = "Allow live microphone audio to Core and its configured AI provider after I wake or push to talk.", AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly CheckBox startup = new() { Text = "Start with Windows and listen locally after sign-in", AutoSize = true };
    private readonly Label feedback = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly Button save = new() { Text = "Verify and save", AutoSize = true };
    public Connection? Result { get; private set; }
    public ConnectionForm(Connection? current)
    {
        Text = "JARVIS connection"; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(20), ColumnCount = 1, Width = 550 };
        void Row(Control control) { control.Margin = new Padding(0, 0, 0, 10); layout.Controls.Add(control); }
        Row(new Label { Text = "First create and confirm an audio-only WindowsAgent on Core. Paste only that device's scoped credential here.", AutoSize = true, MaximumSize = new Size(510, 0) });
        Row(new Label { Text = "Core HTTPS address", AutoSize = true }); Row(origin);
        Row(new Label { Text = "Windows device credential (jdv1_…)", AutoSize = true }); Row(credential);
        Row(new Label { Text = "Loopback certificate SHA-256 pin (leave empty for system-trusted DNS)", AutoSize = true }); Row(pin);
        Row(consent); Row(startup); Row(feedback);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(save); var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true }; buttons.Controls.Add(cancel); Row(buttons);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        origin.Text = current?.Origin.AbsoluteUri ?? "https://zero.tail8d9014.ts.net/";
        credential.Text = current?.Credential ?? ""; pin.Text = current?.CertificateSha256 ?? "";
        consent.Checked = current?.AllowCloudProcessing ?? false; startup.Checked = current?.ListenOnStartup ?? false;
        save.Click += async (_, _) => {
            save.Enabled = false; cancel.Enabled = false; ControlBox = false;
            try {
                if (!consent.Checked) throw new InvalidOperationException("Live voice needs explicit consent. Cancel to leave listening off.");
                var value = new Connection(new Uri(origin.Text.Trim(), UriKind.Absolute), credential.Text.Trim(), string.IsNullOrWhiteSpace(pin.Text) ? null : pin.Text.Trim(), AllowCloudProcessing: true);
                value.Validate(); feedback.Text = "Verifying this device with Core…";
                using var client = new CoreClient(value); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var id = await client.RefreshAsync(timeout.Token);
                await StartupPreference.SetAsync(startup.Checked);
                Result = value with { DeviceId = id, PrivacyEpoch = client.Epoch, ListenOnStartup = startup.Checked };
                DialogResult = DialogResult.OK; Close();
            }
            catch (InvalidOperationException error) { feedback.Text = error.Message; }
            catch { feedback.Text = "Verification failed. Check HTTPS, the device credential, owner confirmation and Core's locked-voice protocol version."; }
            finally { save.Enabled = true; cancel.Enabled = true; ControlBox = true; }
        };
    }
}
