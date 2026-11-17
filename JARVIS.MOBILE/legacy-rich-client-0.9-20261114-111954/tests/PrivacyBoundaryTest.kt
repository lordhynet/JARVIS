package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test

class PrivacyBoundaryTest {
    @Test fun rejected_new_credentials_do_not_match_the_previously_saved_connection() {
        val saved = SavedConnection("https://localhost:5186", "previous-owner-key")
        assertFalse(CoreApi(saved.url, "mistyped-key").uses(saved))
        assertFalse(CoreApi("https://other-core.example", saved.token).uses(saved))
        assertTrue(CoreApi(saved.url + "/", saved.token).uses(saved))
    }
    @Test fun same_generation_accepts_responses_and_reconnects() {
        val boundary = PrivacyBoundary()
        boundary.bind(2); boundary.verify(2); boundary.bind(2)
        assertEquals(2L, boundary.epoch)
    }
    @Test fun changed_generation_rejects_old_and_new_late_responses_permanently() {
        val boundary = PrivacyBoundary(); boundary.bind(2)
        assertThrows(CorePrivacyException::class.java) { boundary.verify(3) }
        assertThrows(CorePrivacyException::class.java) { boundary.verify(2) }
        assertThrows(CorePrivacyException::class.java) { boundary.bind(3) }
    }
    @Test fun erased_status_blocks_even_the_same_generation() {
        val boundary = PrivacyBoundary(); boundary.bind(0)
        assertThrows(CorePrivacyException::class.java) { boundary.verify(0, erased = true) }
        assertThrows(CorePrivacyException::class.java) { boundary.verify(0) }
    }
    @Test fun disconnect_invalidates_inflight_responses() {
        val boundary = PrivacyBoundary(); boundary.bind(0); boundary.invalidate()
        assertThrows(CorePrivacyException::class.java) { boundary.verify(0) }
    }
    @Test fun missing_epoch_is_rejected_instead_of_downgrading_privacy() {
        val boundary = PrivacyBoundary(); boundary.bind(0)
        assertThrows(CorePrivacyException::class.java) { boundary.verify(null) }
    }
    @Test fun legacy_drafts_migrate_only_after_verifying_core_has_never_been_erased() {
        assertTrue(canMigrateLegacyPractice(0))
        assertFalse(canMigrateLegacyPractice(1))
        assertFalse(canMigrateLegacyPractice(2))
    }
    @Test fun encrypted_snapshot_payload_keeps_epoch_and_recognizes_legacy_missing_epoch() {
        val gson = com.google.gson.Gson()
        val snapshot = PracticeSnapshot(state = ScreenState(coachDraft = "Unsent"), writes = emptyList(), coreEpoch = 7)
        val restored = gson.fromJson(gson.toJson(snapshot), PracticeSnapshot::class.java)
        assertEquals(7L, restored.coreEpoch)
        assertEquals("Unsent", restored.state.coachDraft)
        val legacy = com.google.gson.JsonParser.parseString(gson.toJson(snapshot)).asJsonObject.apply { remove("coreEpoch") }
        assertNull(gson.fromJson(legacy, PracticeSnapshot::class.java).coreEpoch)
    }
}
