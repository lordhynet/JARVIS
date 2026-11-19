package uk.jarvis.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AudioRoutePolicyTest {
    @Test fun own_a2dp_to_sco_switch_is_ignored_until_the_grace_window_ends() {
        assertFalse(AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, 1800, 1000, false, false))
        assertFalse(AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, 3999, 1000, false, false))
        assertTrue(AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, 4000, 1000, false, false))
    }

    @Test fun a_connected_or_selected_communication_headset_keeps_voice_alive_after_grace() {
        assertFalse(AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, 10000, 1000, true, false))
        assertFalse(AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, 10000, 1000, false, true))
    }

    @Test fun a_real_unplug_after_grace_stops_voice_and_noisy_never_stops_idle_or_local_wake() {
        assertTrue(AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, 10000, 1000, false, false))
        assertTrue(AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, 10000, null, false, false))
        for (phase in listOf(CompanionAudioPhase.Idle, CompanionAudioPhase.WakeListening))
            assertFalse(AudioRoutePolicy.stopForNoisy(phase, 10000, null, false, false))
    }

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
