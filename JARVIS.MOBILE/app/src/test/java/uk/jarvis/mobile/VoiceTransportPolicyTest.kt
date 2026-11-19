package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test

class VoiceTransportPolicyTest {
    private class FakeClock(var now: Long = 100_000L) { fun read() = now }

    @Test fun recovery_backs_off_and_resets_only_after_connection() {
        val clock = FakeClock()
        val policy = VoiceReconnectPolicy(clock::read) { 0.5 } // middle of the jitter range
        assertEquals(listOf(1000L, 2000L, 4000L, 8000L), (1..4).map { policy.nextDelayMillis().also { d -> clock.now += d!! } })
        policy.connected(); assertEquals(1000L, policy.nextDelayMillis())
    }

    @Test fun a_thirty_second_drop_still_has_attempts_left() {
        val clock = FakeClock()
        val policy = VoiceReconnectPolicy(clock::read) { 0.5 }
        var waited = 0L
        while (waited < 30_000) { val delay = policy.nextDelayMillis(); assertNotNull(delay); waited += delay!! + 1000; clock.now += delay + 1000 }
        assertNotNull("Recovery must still be possible after 30 s offline", policy.nextDelayMillis())
    }

    @Test fun recovery_stops_inside_one_minute_window() {
        val clock = FakeClock()
        val policy = VoiceReconnectPolicy(clock::read) { 1.0 }
        while (true) { val delay = policy.nextDelayMillis() ?: break; clock.now += delay + 2000 }
        assertTrue(clock.now - 100_000L <= VoiceReconnectPolicy.WINDOW_MILLIS)
        assertNull(policy.nextDelayMillis())
        assertTrue(policy.attemptBudgetMillis(55_000) >= VoiceReconnectPolicy.MINIMUM_ATTEMPT_MILLIS)
    }

    @Test fun jitter_stays_within_twenty_percent() {
        assertEquals(800L, VoiceReconnectPolicy({ 0L }) { 0.0 }.nextDelayMillis())
        assertEquals(1200L, VoiceReconnectPolicy({ 0L }) { 1.0 }.nextDelayMillis())
    }

    @Test fun one_attempt_never_outlasts_the_recovery_window() {
        val clock = FakeClock()
        val policy = VoiceReconnectPolicy(clock::read) { 0.5 }
        assertEquals(55_000L, policy.attemptBudgetMillis(55_000))
        policy.nextDelayMillis(); clock.now += 40_000
        assertEquals(20_000L, policy.attemptBudgetMillis(55_000))
    }

    @Test fun only_network_and_gateway_failures_are_retried() {
        assertTrue(VoiceRecovery.retryable(java.io.IOException("offline")))
        assertTrue(VoiceRecovery.retryable(CoreHttpException(502, "gateway")))
        assertTrue(VoiceRecovery.retryable(CoreHttpException(504, "gateway timeout")))
        for (status in listOf(401, 403, 409, 429, 503)) assertFalse("HTTP $status must stop", VoiceRecovery.retryable(CoreHttpException(status, "final")))
        assertFalse(VoiceRecovery.retryable(java.io.IOException("cert", CoreCertificateChanged())))
        assertFalse(VoiceRecovery.retryable(IllegalStateException("bug")))
    }
    @Test fun playback_is_bounded_by_pcm_duration_not_packet_count() {
        val budget = VoicePlaybackBudget()
        assertFalse(budget.reserve(960002)); assertFalse(budget.reserve(3)); assertFalse(budget.reserve(0))
        repeat(10) { assertTrue(budget.reserve(48000)) }; assertTrue(budget.reserve(480000)); assertFalse(budget.reserve(2))
        budget.release(48000); assertTrue(budget.reserve(48000))
        budget.clear(); assertTrue(budget.reserve(960000)); assertFalse(budget.reserve(2))
    }
}
