package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test

class ConnectionStateTest {
    @Test fun accepted_pairing_leaves_the_pairing_screen_and_preserves_practice_drafts() {
        val state = ScreenState(pairingOffered = true, offline = true, notice = "Reconnecting", coachDraft = "My unsent answer")
            .connectionEstablished("https://localhost:5186", emptyList(), emptyList())
        assertTrue(state.connected)
        assertFalse(state.pairingOffered)
        assertFalse(state.offline)
        assertEquals("https://localhost:5186", state.url)
        assertEquals("", state.notice)
        assertEquals("My unsent answer", state.coachDraft)
    }
}
