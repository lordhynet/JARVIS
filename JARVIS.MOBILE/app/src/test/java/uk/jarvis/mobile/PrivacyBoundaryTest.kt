package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test

class PrivacyBoundaryTest {
    @org.junit.Test fun credential_revocation_preserves_drafts_but_confirmed_privacy_changes_clear_them() {
        org.junit.Assert.assertFalse(CompanionAccessPolicy.eraseLegacyFor(CoreHttpException(401, "Revoked")))
        org.junit.Assert.assertFalse(CompanionAccessPolicy.eraseLegacyFor(java.io.IOException("Offline")))
        val missing = PrivacyBoundary(); missing.bind(2)
        org.junit.Assert.assertFalse(org.junit.Assert.assertThrows(CorePrivacyException::class.java) { missing.verify(null) }.eraseLegacy)
        val malformed = PrivacyBoundary(); malformed.bind(2)
        org.junit.Assert.assertFalse(org.junit.Assert.assertThrows(CorePrivacyException::class.java) { malformed.verify(-1) }.eraseLegacy)
        val changed = PrivacyBoundary(); changed.bind(2)
        org.junit.Assert.assertTrue(CompanionAccessPolicy.eraseLegacyFor(org.junit.Assert.assertThrows(CorePrivacyException::class.java) { changed.verify(3) }))
        val erased = PrivacyBoundary(); erased.bind(2)
        org.junit.Assert.assertTrue(CompanionAccessPolicy.eraseLegacyFor(org.junit.Assert.assertThrows(CorePrivacyException::class.java) { erased.verify(2, true) }))
    }
    @Test fun changed_generation_permanently_invalidates_old_connection() {
        val boundary = PrivacyBoundary(); boundary.bind(2)
        assertThrows(CorePrivacyException::class.java) { boundary.verify(3) }
        assertThrows(CorePrivacyException::class.java) { boundary.verify(2) }
        assertThrows(CorePrivacyException::class.java) { boundary.bind(3) }
    }
    @Test fun erased_core_and_missing_headers_are_rejected() {
        for (erased in listOf(false, true)) {
            val boundary = PrivacyBoundary(); boundary.bind(0)
            assertThrows(CorePrivacyException::class.java) { boundary.verify(if (erased) 0 else null, erased) }
        }
    }
    @Test fun same_generation_can_continue_but_disconnect_rejects_late_replies() {
        val boundary = PrivacyBoundary(); boundary.bind(2); boundary.verify(2); boundary.invalidate()
        assertThrows(CorePrivacyException::class.java) { boundary.verify(2) }
    }
    @Test fun rejected_new_credentials_do_not_match_saved_connection() {
        val saved = SavedConnection("https://localhost:5186", "saved-owner-key")
        assertTrue(CoreApi(saved.url + "/", saved.token).uses(saved))
        assertFalse(CoreApi(saved.url, "mistyped-key").uses(saved))
        assertFalse(CoreApi("https://other-core.example", saved.token).uses(saved))
    }
}
