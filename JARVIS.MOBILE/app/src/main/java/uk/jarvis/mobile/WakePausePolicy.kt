package uk.jarvis.mobile

import android.media.AudioManager

/**
 * When local "Hey JARVIS" listening must pause. Listening resumes automatically once every reason clears.
 * A locked or dark screen pauses it (a pocket is not a conversation), as do phone calls and battery saver.
 */
internal object WakePausePolicy {
    const val CALL = "Hey JARVIS is paused during a call."
    const val LOCKED = "Hey JARVIS is paused while the phone is locked."
    const val BATTERY = "Hey JARVIS is paused while battery saver is on."

    /** [ownCommunicationRoute] is true when JARVIS itself set the communication mode for its earbud route. */
    fun inCall(audioMode: Int, ownCommunicationRoute: Boolean): Boolean =
        audioMode == AudioManager.MODE_IN_CALL || audioMode == AudioManager.MODE_RINGTONE ||
            audioMode == AudioManager.MODE_IN_COMMUNICATION && !ownCommunicationRoute

    fun reason(screenOn: Boolean, locked: Boolean, inCall: Boolean, powerSave: Boolean): String? = when {
        inCall -> CALL
        !screenOn || locked -> LOCKED
        powerSave -> BATTERY
        else -> null
    }
}
