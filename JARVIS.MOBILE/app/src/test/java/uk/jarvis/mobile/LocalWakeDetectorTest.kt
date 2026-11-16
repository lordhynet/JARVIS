package uk.jarvis.mobile

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LocalWakeDetectorTest {
    @Test fun matchesWakePhraseAcrossCaseAndPunctuation() {
        assertTrue(WakePhraseMatcher.matches("Hey JARVIS"))
        assertTrue(WakePhraseMatcher.matches("hey, Jarvis!"))
        assertFalse(WakePhraseMatcher.matches("I said hello to Jarvis once"))
    }

    @Test fun rejectsPartialOrUnrelatedPhrases() {
        assertFalse(WakePhraseMatcher.matches("hey"))
        assertFalse(WakePhraseMatcher.matches("Jarvis"))
        assertFalse(WakePhraseMatcher.matches("Hey Marvin"))
    }
}
