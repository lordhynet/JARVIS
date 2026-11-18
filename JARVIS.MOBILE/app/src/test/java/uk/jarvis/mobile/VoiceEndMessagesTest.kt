package uk.jarvis.mobile

import org.junit.Assert.assertEquals
import org.junit.Test

class VoiceEndMessagesTest {
    @Test fun spoken_goodbye_has_a_friendly_close() {
        assertEquals("Bye for now.", VoiceEndMessages.forReason("owner_closed"))
    }

    @Test fun silence_explains_why_voice_stopped() {
        assertEquals("Stopped after a quiet minute.", VoiceEndMessages.forReason("idle"))
    }

    @Test fun unknown_and_missing_reasons_fall_back_to_a_plain_stop() {
        assertEquals("Voice stopped. Your conversation is saved.", VoiceEndMessages.forReason("stopped"))
        assertEquals("Voice stopped. Your conversation is saved.", VoiceEndMessages.forReason(null))
    }
}
