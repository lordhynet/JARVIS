package uk.jarvis.mobile

internal sealed interface VoicePhase {
    data object Idle : VoicePhase
    data object WakeListening : VoicePhase
    data object Arbitrating : VoicePhase
    data class Live(val ready: Boolean) : VoicePhase
    data object Reconnecting : VoicePhase
    data object Stopping : VoicePhase
}

/** Owns the generation shared by wake arbitration and live transport callbacks. */
internal class VoiceLifecycle {
    @Volatile var phase: VoicePhase = VoicePhase.Idle
        private set
    private var generation = 0L

    val active: Boolean get() = phase.let { it is VoicePhase.Live || it == VoicePhase.Reconnecting }
    val ready: Boolean get() = (phase as? VoicePhase.Live)?.ready == true
    val mayStartLive: Boolean get() = phase.let { it == VoicePhase.Idle || it == VoicePhase.WakeListening || it == VoicePhase.Arbitrating }

    @Synchronized fun listen(): Boolean {
        if (active || phase == VoicePhase.Stopping) return false
        phase = VoicePhase.WakeListening
        return true
    }

    @Synchronized fun beginArbitration(): Long? {
        if (phase != VoicePhase.WakeListening) return null
        phase = VoicePhase.Arbitrating
        return ++generation
    }

    @Synchronized fun isArbitrating(current: Long): Boolean = generation == current && phase == VoicePhase.Arbitrating

    @Synchronized fun beginLive(): Long? {
        if (!mayStartLive) return null
        phase = VoicePhase.Live(ready = false)
        return ++generation
    }

    @Synchronized fun isLive(current: Long): Boolean = generation == current && active

    @Synchronized fun markReady(current: Long): Boolean {
        if (!isLive(current)) return false
        phase = VoicePhase.Live(ready = true)
        return true
    }

    @Synchronized fun pauseWindow(current: Long): Boolean {
        if (!isLive(current)) return false
        if (phase is VoicePhase.Live) phase = VoicePhase.Live(ready = false)
        return true
    }

    @Synchronized fun beginReconnect(current: Long): Boolean {
        if (!isLive(current)) return false
        phase = VoicePhase.Reconnecting
        return true
    }

    @Synchronized fun beginStop(): Boolean {
        if (phase == VoicePhase.Stopping) return false
        generation++
        phase = VoicePhase.Stopping
        return true
    }

    @Synchronized fun finishStop(keepWake: Boolean) {
        if (phase == VoicePhase.Stopping)
            phase = if (keepWake) VoicePhase.WakeListening else VoicePhase.Idle
    }
}
