package uk.jarvis.mobile

internal enum class CompanionAudioPhase { Idle, WakeListening, LiveVoice }
internal enum class AudioRouteAction { EnterCommunicationRoute, RestoreDefaultRoute }

/** Keeps local wake detection outside Android's communication route; only a live winner takes it. */
internal object AudioRoutePolicy {
    const val SWITCH_GRACE_MS = 3000L

    /** Android can report A2DP becoming noisy while our own SCO route is taking over. */
    fun stopForNoisy(phase: CompanionAudioPhase, nowMs: Long, lastSwitchMs: Long?,
        selectedCommunicationHeadset: Boolean, availableCommunicationHeadset: Boolean): Boolean {
        if (phase != CompanionAudioPhase.LiveVoice) return false
        if (lastSwitchMs != null && nowMs - lastSwitchMs in 0 until SWITCH_GRACE_MS) return false
        return !selectedCommunicationHeadset && !availableCommunicationHeadset
    }

    fun transition(from: CompanionAudioPhase, to: CompanionAudioPhase): List<AudioRouteAction> = when {
        from == to -> emptyList()
        from != CompanionAudioPhase.LiveVoice && to == CompanionAudioPhase.LiveVoice -> listOf(AudioRouteAction.EnterCommunicationRoute)
        from == CompanionAudioPhase.LiveVoice && to != CompanionAudioPhase.LiveVoice -> listOf(AudioRouteAction.RestoreDefaultRoute)
        else -> emptyList()
    }
}
