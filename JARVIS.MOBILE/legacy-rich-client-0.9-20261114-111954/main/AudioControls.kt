package uk.jarvis.mobile

internal enum class AudioStartMode { Record, Live }
internal data class AudioStartRequest(val mode: AudioStartMode, val generation: Long)
internal sealed class AudioPermissionResult {
    data class Ready(val request: AudioStartRequest) : AudioPermissionResult()
    data class Denied(val message: String) : AudioPermissionResult()
    data object Cancelled : AudioPermissionResult()
}

/** Consent results and delayed Core claims belong to one explicit start gesture. */
internal class AudioPermissionFlow {
    private var generation = 0L
    private var pending: AudioStartRequest? = null
    fun begin(mode: AudioStartMode): Boolean {
        if (pending != null) return false
        pending = AudioStartRequest(mode, ++generation)
        return true
    }
    fun complete(microphone: Boolean, notifications: Boolean): AudioPermissionResult {
        val request = pending ?: return AudioPermissionResult.Cancelled
        pending = null
        return when {
            !microphone -> AudioPermissionResult.Denied("Microphone permission is required. Allow it in Android app settings, then tap Start again.")
            !notifications -> AudioPermissionResult.Denied("Allow JARVIS notifications in Android app settings for visible Stop controls, then tap Start again.")
            else -> AudioPermissionResult.Ready(request)
        }
    }
    fun isCurrent(request: AudioStartRequest): Boolean = request.generation == generation
    fun cancel() { generation++; pending = null }
}

internal enum class HeadsetCommand { None, FinishRecording, DiscardRecording, StopLive, InterruptLive }

/** Android KeyEvent values; only the first key-down may act. Play never starts capture. */
internal object HeadsetControls {
    fun handles(code: Int): Boolean = code in setOf(79, 85, 86, 87, 126, 127)
    fun command(live: Boolean, code: Int, down: Boolean, repeat: Int, active: Boolean): HeadsetCommand {
        if (!active || !down || repeat != 0) return HeadsetCommand.None
        return when (code) {
            79, 85, 127 -> if (live) HeadsetCommand.StopLive else HeadsetCommand.FinishRecording
            86 -> if (live) HeadsetCommand.StopLive else HeadsetCommand.DiscardRecording
            87 -> if (live) HeadsetCommand.InterruptLive else HeadsetCommand.None
            else -> HeadsetCommand.None
        }
    }
}

/** Duplicate stop/finish callbacks must not erase the file retained by the first finish. */
internal class CaptureStopGate {
    var stopped = false
        private set
    fun take(): Boolean {
        if (stopped) return false
        stopped = true
        return true
    }
}
