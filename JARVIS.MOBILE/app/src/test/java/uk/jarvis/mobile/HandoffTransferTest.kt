package uk.jarvis.mobile
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import uk.jarvis.mobile.contracts.*

class HandoffTransferTest {
    private val offer = CompanionHandoffItem("session", "handoff", "source", "phone", HandoffState.TargetReady, "2099-01-01T00:00:00Z", true)
    @Test fun accepts_only_after_explicit_unlock_and_waits_for_core_source_release() = runBlocking {
        val requests = mutableListOf<CompanionHandoffRequest>()
        HandoffTransfer("phone") { request -> requests += request; CompanionHandoffView(null, listOf(offer.copy(state = if (request.action == "poll") HandoffState.AudioTransferred else HandoffState.TargetReady)), emptyList()) }.accept(offer) { false }
        assertEquals(listOf("ready", "poll"), requests.map { it.action }); assertTrue(requests.all { it.locked == false })
        assertFalse(requests.any { it.action == "complete" }) // Core preparation still owns completion and a new ticket.
    }
    @Test fun locked_accept_does_not_contact_core() = runBlocking {
        var calls = 0
        try { HandoffTransfer("phone") { calls++; error("Must not send") }.accept(offer) { true }; fail("Must refuse") } catch (_: IllegalStateException) { }
        assertEquals(0, calls)
    }
    @Test fun release_acknowledges_only_after_capture_and_playback_stop() = runBlocking {
        val events = mutableListOf<String>()
        HandoffTransfer("source") { request -> assertTrue(request.audioStopped); events += request.action; CompanionHandoffView(null, emptyList(), emptyList()) }
            .release(offer, { events += "stopped-and-drained" }, false)
        assertEquals(listOf("stopped-and-drained", "release"), events)
    }
    @Test fun failed_audio_stop_never_acknowledges_a_transfer() = runBlocking {
        var calls = 0
        try { HandoffTransfer("source") { calls++; error("Must not send") }.release(offer, { error("Capture did not stop") }, false); fail("Must refuse") } catch (_: IllegalStateException) { }
        assertEquals(0, calls)
    }
    @Test fun handoff_routes_do_not_allow_conversation_browsing() {
        assertTrue(CompanionRoutes.allowed(CoreCompanionRoutes.VOICE_HANDOFF, "POST"))
        assertFalse(CompanionRoutes.allowed(CoreCompanionRoutes.VOICE_HANDOFF, "GET"))
        assertFalse(CompanionRoutes.allowed("/api/sessions/session", "GET"))
    }
    @Test fun decline_is_metadata_only_and_remains_safe_while_locked() = runBlocking {
        val requests = mutableListOf<CompanionHandoffRequest>()
        HandoffTransfer("phone") { requests += it; CompanionHandoffView(null, emptyList(), emptyList()) }.decline(offer, true)
        assertEquals(listOf("cancel"), requests.map { it.action }); assertTrue(requests.single().locked == true); assertFalse(requests.single().audioStopped)
    }
    @Test fun a_late_preparation_response_cannot_start_a_different_conversation() {
        var starts = 0
        try { HandoffTransfer.continuePrepared("expected", "other") { starts++ }; fail("Must refuse") } catch (_: IllegalStateException) { }
        assertEquals(0, starts); HandoffTransfer.continuePrepared("expected", "expected") { starts++ }; assertEquals(1, starts)
    }
}
