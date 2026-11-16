package uk.jarvis.mobile

import org.junit.Assert.assertEquals
import org.junit.Test

class WakeRestartPolicyTest {
    @Test fun sticky_restart_with_wake_enabled_offers_explicit_resume() {
        assertEquals(WakeRestartAction.OfferResume, WakeRestartPolicy.action(hasCommandIntent = false, wakeWasEnabled = true))
    }

    @Test fun explicit_user_command_is_processed_normally() {
        assertEquals(WakeRestartAction.HandleCommand, WakeRestartPolicy.action(hasCommandIntent = true, wakeWasEnabled = true))
    }

    @Test fun restart_without_wake_preference_stops() {
        assertEquals(WakeRestartAction.Stop, WakeRestartPolicy.action(hasCommandIntent = false, wakeWasEnabled = false))
    }
}
