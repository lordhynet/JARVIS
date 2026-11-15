package uk.jarvis.mobile

import org.junit.Assert.assertEquals
import org.junit.Test

class AudioRoutePolicyTest {
    @Test fun wake_listening_never_enters_communication_mode_or_selects_sco() {
        assertEquals(emptyList<AudioRouteAction>(), AudioRoutePolicy.transition(CompanionAudioPhase.Idle, CompanionAudioPhase.WakeListening))
        assertEquals(emptyList<AudioRouteAction>(), AudioRoutePolicy.transition(CompanionAudioPhase.WakeListening, CompanionAudioPhase.WakeListening))
    }

    @Test fun only_live_voice_enters_communication_route_and_returning_to_wake_restores_default() {
        assertEquals(listOf(AudioRouteAction.EnterCommunicationRoute),
            AudioRoutePolicy.transition(CompanionAudioPhase.WakeListening, CompanionAudioPhase.LiveVoice))
        assertEquals(listOf(AudioRouteAction.RestoreDefaultRoute),
            AudioRoutePolicy.transition(CompanionAudioPhase.LiveVoice, CompanionAudioPhase.WakeListening))
        assertEquals(listOf(AudioRouteAction.RestoreDefaultRoute),
            AudioRoutePolicy.transition(CompanionAudioPhase.LiveVoice, CompanionAudioPhase.Idle))
        assertEquals(emptyList<AudioRouteAction>(), AudioRoutePolicy.transition(CompanionAudioPhase.Idle, CompanionAudioPhase.Idle))
    }
}
