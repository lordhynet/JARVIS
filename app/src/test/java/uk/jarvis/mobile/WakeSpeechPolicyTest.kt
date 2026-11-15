package uk.jarvis.mobile

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class WakeSpeechPolicyTest {
    @Test fun closes_after_eight_seconds_without_speech() {
        val timeout = WakeSpeechTimeout()
        assertFalse(timeout.shouldClose(100, false))
        assertFalse(timeout.shouldClose(8_099, false))
        assertTrue(timeout.shouldClose(8_100, false))
    }

    @Test fun speech_before_deadline_keeps_the_session_open() {
        val timeout = WakeSpeechTimeout()
        assertFalse(timeout.shouldClose(0, false))
        assertFalse(timeout.shouldClose(5_000, true))
        assertFalse(timeout.shouldClose(20_000, false))
    }

    @Test fun onset_requires_three_consecutive_speech_level_frames() {
        val detector = WakeSpeechOnsetDetector()
        val silence = ShortArray(480)
        val speech = ShortArray(480) { 2_000 }
        assertFalse(detector.observe(silence, silence.size))
        assertFalse(detector.observe(speech, speech.size))
        assertFalse(detector.observe(speech, speech.size))
        assertTrue(detector.observe(speech, speech.size))
        assertFalse(detector.observe(silence, silence.size))
    }
}
