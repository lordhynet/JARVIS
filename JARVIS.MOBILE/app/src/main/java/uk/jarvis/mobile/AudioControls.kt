package uk.jarvis.mobile

internal sealed class AudioPermissionResult {
    data class Ready(val generation: Long) : AudioPermissionResult()
    data class Denied(val message: String) : AudioPermissionResult()
    data object Cancelled : AudioPermissionResult()
}
internal class AudioPermissionFlow {
    private var generation = 0L
    private var pending = false
    fun begin(): Boolean { if (pending) return false; generation++; pending = true; return true }
    fun complete(microphone: Boolean, notifications: Boolean): AudioPermissionResult {
        if (!pending) return AudioPermissionResult.Cancelled
        pending = false
        return when {
            !microphone -> AudioPermissionResult.Denied("Allow microphone access in Android settings, then tap Talk again.")
            !notifications -> AudioPermissionResult.Denied("Allow notifications so Stop controls remain available, then tap Talk again.")
            else -> AudioPermissionResult.Ready(generation)
        }
    }
    fun isCurrent(value: Long) = generation == value
    fun cancel() { generation++; pending = false }
}
internal enum class HeadsetCommand { None, StopLive, InterruptLive }
internal object HeadsetControls {
    fun handles(code: Int) = code in setOf(79, 85, 86, 87, 126, 127)
    fun command(code: Int, down: Boolean, repeat: Int, active: Boolean): HeadsetCommand {
        if (!active || !down || repeat != 0) return HeadsetCommand.None
        return when (code) { 79, 85, 86, 127 -> HeadsetCommand.StopLive; 87 -> HeadsetCommand.InterruptLive; else -> HeadsetCommand.None }
    }
}
