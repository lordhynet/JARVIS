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
        assertFalse(WakePhraseMatcher.matches("The TV said hey Jarvis"))
        assertFalse(WakePhraseMatcher.matches("Hey Jarvision"))
    }

    @Test fun accepts_a_question_after_the_wake_phrase_without_requiring_a_pause() {
        assertTrue(WakePhraseMatcher.matches("Hey JARVIS, what's on today?"))
        assertTrue(WakePhraseMatcher.matches("Hey, Jarvis! Continue on my phone."))
    }
}
