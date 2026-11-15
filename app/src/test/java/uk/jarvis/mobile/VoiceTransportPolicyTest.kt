package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test

class VoiceTransportPolicyTest {
    @Test fun recovery_is_bounded_and_resets_only_after_connection() {
        val policy = VoiceReconnectPolicy()
        assertEquals(listOf(1000L, 2000L, 4000L, 8000L, 8000L), (1..5).map { policy.nextDelayMillis() })
        assertNull(policy.nextDelayMillis()); assertNull(policy.nextDelayMillis())
        policy.connected(); assertEquals(1000L, policy.nextDelayMillis())
    }
    @Test fun playback_is_bounded_by_pcm_duration_not_packet_count() {
        val budget = VoicePlaybackBudget()
        assertFalse(budget.reserve(960002)); assertFalse(budget.reserve(3)); assertFalse(budget.reserve(0))
        repeat(10) { assertTrue(budget.reserve(48000)) }; assertTrue(budget.reserve(480000)); assertFalse(budget.reserve(2))
        budget.release(48000); assertTrue(budget.reserve(48000))
        budget.clear(); assertTrue(budget.reserve(960000)); assertFalse(budget.reserve(2))
    }
}
