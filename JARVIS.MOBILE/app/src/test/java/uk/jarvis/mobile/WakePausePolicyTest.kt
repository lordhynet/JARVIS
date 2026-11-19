package uk.jarvis.mobile

import android.media.AudioManager
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class WakePausePolicyTest {
    @Test fun listens_only_when_unlocked_with_the_screen_on_no_call_and_no_battery_saver() {
        assertNull(WakePausePolicy.reason(screenOn = true, locked = false, inCall = false, powerSave = false))
    }

    @Test fun locking_or_screen_off_pauses_listening() {
        assertEquals(WakePausePolicy.LOCKED, WakePausePolicy.reason(screenOn = true, locked = true, inCall = false, powerSave = false))
        assertEquals(WakePausePolicy.LOCKED, WakePausePolicy.reason(screenOn = false, locked = false, inCall = false, powerSave = false))
    }

    @Test fun a_call_takes_priority_over_other_reasons() {
        assertEquals(WakePausePolicy.CALL, WakePausePolicy.reason(screenOn = false, locked = true, inCall = true, powerSave = true))
    }

    @Test fun battery_saver_pauses_listening() {
        assertEquals(WakePausePolicy.BATTERY, WakePausePolicy.reason(screenOn = true, locked = false, inCall = false, powerSave = true))
    }

    @Test fun phone_and_ringing_calls_count_but_jarvis_own_earbud_route_does_not() {
        assertTrue(WakePausePolicy.inCall(AudioManager.MODE_IN_CALL, ownCommunicationRoute = true))
        assertTrue(WakePausePolicy.inCall(AudioManager.MODE_RINGTONE, ownCommunicationRoute = false))
        assertTrue(WakePausePolicy.inCall(AudioManager.MODE_IN_COMMUNICATION, ownCommunicationRoute = false)) // another app's call
        assertFalse(WakePausePolicy.inCall(AudioManager.MODE_IN_COMMUNICATION, ownCommunicationRoute = true)) // JARVIS's own route
        assertFalse(WakePausePolicy.inCall(AudioManager.MODE_NORMAL, ownCommunicationRoute = false))
    }
}
