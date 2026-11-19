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

    @Test fun accepts_common_on_device_mishearings_only_at_the_start() {
        for (heard in listOf("Hey Jervis", "hey javis", "Hi Jarvis", "a Jarvis", "OK Jarvis", "Hey Jar vis", "hay Jarvas", "Hey Jarvis's"))
            assertTrue(heard, WakePhraseMatcher.matches(heard))
        for (heard in listOf("The TV said hey Jervis", "I told Jarvis hi", "Hey Jarvisworth", "Hey Travis", "hey Mavis", "Jervis"))
            assertFalse(heard, WakePhraseMatcher.matches(heard))
        assertTrue(WakePhraseMatcher.biasing.contains("Hey JARVIS"))
    }

    @Test fun accepts_what_the_s24_actually_heard() {
        for (heard in listOf("Hello Jarvis", "Ajabes", "Heyjarvis", "ajarvis", "Hello, Jarvis! What's on today?"))
            assertTrue(heard, WakePhraseMatcher.matches(heard))
        for (heard in listOf("How are you today Jarvis", "Jabes", "Ajabesworth", "Hello Marvin"))
            assertFalse(heard, WakePhraseMatcher.matches(heard))
    }

    @Test fun accepts_a_question_after_the_wake_phrase_without_requiring_a_pause() {
        assertTrue(WakePhraseMatcher.matches("Hey JARVIS, what's on today?"))
        assertTrue(WakePhraseMatcher.matches("Hey, Jarvis! Continue on my phone."))
    }
}
