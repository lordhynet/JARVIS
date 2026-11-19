using Jarvis.Contracts;

namespace Jarvis.Windows;

internal sealed partial class TrayCompanion
{
    private InterviewRecorder? recorder; private Form? recordingBanner; private bool closingRecordingBanner;
    private void PushToTalkUnlessRecording() { if (recorder?.IsRecording != true) controller?.PushToTalk(); }
    private InterviewRecorder Recorder()
    {
        if (connection is null) throw new InvalidOperationException("Connect this laptop with its scoped device credential first.");
        if (recorder is not null) return recorder;
        var current = new InterviewRecorder(connection); recorder = current;
        current.Changed += text => Post(() => { if (ReferenceEquals(recorder, current)) SetRecordingIndicator(text); });
        current.StopRequested += () => Post(() => Run(() => StopInterviewAsync(true)));
        return current;
    }
    private async Task StartInterviewAsync()
    {
        if (new WindowsLockState().IsLocked) throw new InvalidOperationException("Unlock this laptop before starting a recording.");
        var current = Recorder(); var offers = await current.OffersAsync(CancellationToken.None);
        if (offers.Count != 1) throw new InvalidOperationException("Prepare one recording for this laptop in the owner dashboard first.");
        if (MessageBox.Show(RecordingProtocol.Consent + "\n\nI consent to encrypted storage in my Core for my own practice. Audio is removed after 30 days.\n\nThis records your microphone and all default system playback as separate tracks. Close other audio apps. JARVIS stays silent. Continue?",
            "Record this interview?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (new WindowsLockState().IsLocked) throw new InvalidOperationException("The laptop locked before capture could start.");
        if (controller is not null) await controller.StopAsync();
        ShowRecordingBanner();
        try { await current.StartAsync(offers[0].Id, CancellationToken.None); }
        catch { CloseRecordingBanner(); throw; }
    }
    private async Task StopInterviewAsync(bool damaged = false)
    {
        if (recorder is null) return;
        await recorder.StopAsync(damaged); CloseRecordingBanner();
    }
    private async Task ResumeInterviewUploadsAsync()
    {
        if (new WindowsLockState().IsLocked) throw new InvalidOperationException("Unlock this laptop before resuming recording uploads.");
        await Recorder().ResumeUploadsAsync(CancellationToken.None);
    }
    private void SetRecordingIndicator(string text)
    {
        status.Text = recorder?.IsRecording == true ? "JARVIS is recording" : "Recording upload"; detail.Text = text;
        tray.Text = recorder?.IsRecording == true ? "JARVIS is recording" : "JARVIS — recording upload";
        tray.Icon = recorder?.IsRecording == true ? SystemIcons.Error : SystemIcons.Information;
    }
    private void ShowRecordingBanner()
    {
        CloseRecordingBanner();
        recordingBanner = new Form { Text = "JARVIS is recording", Width = 440, Height = 170, TopMost = true, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedToolWindow };
        var text = new Label { Text = "JARVIS is recording\nMicrophone and system playback. JARVIS stays silent.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        var button = new Button { Text = "Stop recording", Dock = DockStyle.Bottom, Height = 42 }; button.Click += (_, _) => Run(() => StopInterviewAsync());
        recordingBanner.Controls.Add(text); recordingBanner.Controls.Add(button);
        recordingBanner.FormClosing += (_, e) => { if (!closingRecordingBanner && recorder?.IsRecording == true) { e.Cancel = true; Run(() => StopInterviewAsync()); } };
        recordingBanner.Show();
    }
    private void CloseRecordingBanner()
    {
        if (recordingBanner is null) return; closingRecordingBanner = true;
        try { recordingBanner.Close(); recordingBanner.Dispose(); recordingBanner = null; } finally { closingRecordingBanner = false; }
    }
}
