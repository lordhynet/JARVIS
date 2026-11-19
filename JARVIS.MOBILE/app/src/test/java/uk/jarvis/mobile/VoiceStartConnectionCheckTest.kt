package uk.jarvis.mobile

import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class VoiceStartConnectionCheckTest {
    @Test fun a_failed_start_rechecks_once_and_reports_the_result_without_replaying_preparation() = runBlocking {
        var starts = 0; var checks = 0
        try {
            startWithConnectionCheck<String>({ starts++; throw IOException("Core restarted.") },
                { checks++; "Connected to Core. Voice is ready." }, { false })
            fail("The owner must tap again before another start")
        } catch (error: VoiceStartCheckedFailure) {
            assertTrue(error.message!!.contains("Connected to Core. Voice is ready."))
            assertTrue(error.message!!.contains("Core restarted."))
        }
        assertEquals(1, starts); assertEquals(1, checks)
    }

    @Test fun successful_start_does_not_recheck() = runBlocking {
        val value = startWithConnectionCheck({ "prepared" }, { fail("No recheck needed"); "" }, { false })
        assertEquals("prepared", value)
    }

    @Test fun cancelled_or_revoked_start_never_rechecks() = runBlocking {
        for (failure in listOf(CancellationException("cancelled"), IllegalStateException("revoked"))) {
            try {
                startWithConnectionCheck<String>({ throw failure }, { fail("Do not retry revoked or cancelled access"); "" }, { it === failure })
                fail("Must propagate original failure")
            } catch (error: Exception) { assertSame(failure, error) }
        }
    }

    @Test fun a_failed_or_cancelled_recheck_propagates_without_another_start() = runBlocking {
        for (failure in listOf(IOException("Still offline"), CancellationException("stopped"))) {
            var starts = 0; var checks = 0
            try {
                startWithConnectionCheck<String>({ starts++; throw IOException("Start failed") },
                    { checks++; throw failure }, { false })
                fail("Recheck failure must be visible")
            } catch (error: Exception) { assertSame(failure, error) }
            assertEquals(1, starts); assertEquals(1, checks)
        }
    }
}
