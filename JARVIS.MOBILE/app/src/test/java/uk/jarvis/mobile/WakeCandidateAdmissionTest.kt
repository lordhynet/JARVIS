package uk.jarvis.mobile

import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class WakeCandidateAdmissionTest {
    @Test fun blocked_before_request_makes_no_core_call() = runBlocking {
        var requests = 0
        val gate = WakeCandidateAdmission({ false }, { requests++; "ticket" },
            { fail("No ticket exists to release") }, { fail("Blocked wake cannot start voice") })
        assertFalse(gate.run())
        assertEquals(0, requests)
    }

    @Test fun recording_start_during_request_releases_the_ticket_and_never_opens_voice() = runBlocking {
        var recording = false; val released = mutableListOf<String>(); val accepted = mutableListOf<String>()
        val gate = WakeCandidateAdmission({ !recording }, { recording = true; "recording-race-ticket" },
            { released += it }, { accepted += it })
        assertFalse(gate.run())
        assertEquals(listOf("recording-race-ticket"), released)
        assertTrue(accepted.isEmpty())
    }

    @Test fun stale_lifecycle_generation_is_released_by_the_production_admission_path() = runBlocking {
        val lifecycle = VoiceLifecycle(); lifecycle.listen()
        val generation = lifecycle.beginArbitration()!!
        var releases = 0; var accepts = 0
        val gate = WakeCandidateAdmission({ lifecycle.isArbitrating(generation) }, {
            lifecycle.beginStop(); lifecycle.finishStop(false); "late-ticket"
        }, { releases++ }, { accepts++ })
        assertFalse(gate.run())
        assertEquals(1, releases)
        assertEquals(0, accepts)
    }

    @Test fun available_current_candidate_is_accepted_once() = runBlocking {
        var requests = 0; var accepts = 0
        val gate = WakeCandidateAdmission({ true }, { requests++; "current-ticket" },
            { fail("An accepted ticket must not be released") }, { assertEquals("current-ticket", it); accepts++ })
        assertTrue(gate.run())
        assertEquals(1, requests)
        assertEquals(1, accepts)
    }

    @Test fun failed_best_effort_release_never_accepts_the_late_candidate() = runBlocking {
        var eligible = true; var releases = 0; var accepts = 0
        val gate = WakeCandidateAdmission({ eligible }, { eligible = false; "late-ticket" },
            { releases++; throw java.io.IOException("Synthetic offline Core") }, { accepts++ })
        assertFalse(gate.run())
        assertEquals(1, releases)
        assertEquals(0, accepts)
    }
}
