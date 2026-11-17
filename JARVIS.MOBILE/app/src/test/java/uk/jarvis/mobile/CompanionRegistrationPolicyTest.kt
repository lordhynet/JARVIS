package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test

class CompanionRegistrationPolicyTest {
    @Test fun scoped_credentials_never_attempt_registration() {
        for (exists in listOf(false, true)) {
            try { CompanionRegistrationPolicy.shouldRegister(true, exists, false); fail("Owner setup must be required") }
            catch (error: CompanionSetupException) { assertTrue(error.message!!.contains("Ask the owner")) }
        }
        assertFalse(CompanionRegistrationPolicy.shouldRegister(true, true, true))
        assertTrue(CompanionRegistrationPolicy.shouldRegister(false, false, false))
    }
}
