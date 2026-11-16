package uk.jarvis.mobile

internal enum class CompanionAudioPhase { Idle, WakeListening, LiveVoice }
internal enum class AudioRouteAction { EnterCommunicationRoute, RestoreDefaultRoute }

/** Keeps local wake detection outside Android's communication route; only a live winner takes it. */
internal object AudioRoutePolicy {
    fun transition(from: CompanionAudioPhase, to: CompanionAudioPhase): List<AudioRouteAction> = when {
        from == to -> emptyList()
        from != CompanionAudioPhase.LiveVoice && to == CompanionAudioPhase.LiveVoice -> listOf(AudioRouteAction.EnterCommunicationRoute)
        from == CompanionAudioPhase.LiveVoice && to != CompanionAudioPhase.LiveVoice -> listOf(AudioRouteAction.RestoreDefaultRoute)
        else -> emptyList()
    }
}
