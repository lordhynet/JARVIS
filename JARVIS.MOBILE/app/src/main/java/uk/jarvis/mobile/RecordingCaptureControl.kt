package uk.jarvis.mobile

internal enum class RecordingPhase { Idle, Starting, Capturing, Uploading, Pending }
internal class RecordingCaptureControl {
    @Volatile var phase = RecordingPhase.Idle
        private set
    val microphoneActive get() = phase == RecordingPhase.Capturing
    val recordingIndicator get() = phase == RecordingPhase.Starting || phase == RecordingPhase.Capturing
    val allowsWake get() = !recordingIndicator
    @Synchronized fun begin(visible: Boolean, unlocked: Boolean, authorised: Boolean, storageConsent: Boolean, microphone: Boolean, notifications: Boolean) {
        check(phase == RecordingPhase.Idle && visible && unlocked && authorised && storageConsent && microphone && notifications) { "Unlock this phone, allow microphone/notifications and confirm recording authorization first." }
        phase = RecordingPhase.Starting
    }
    @Synchronized fun captured() { check(phase == RecordingPhase.Starting); phase = RecordingPhase.Capturing }
    @Synchronized fun stopped() { phase = RecordingPhase.Uploading }
    @Synchronized fun networkLost() { if (phase == RecordingPhase.Uploading) phase = RecordingPhase.Pending }
    @Synchronized fun uploading() { check(!recordingIndicator); phase = RecordingPhase.Uploading }
    @Synchronized fun finished() { phase = RecordingPhase.Idle }
}
