package uk.jarvis.mobile

/** Short, plain status lines for Core's voice end reasons. Core decides when a conversation ends. */
internal object VoiceEndMessages {
    const val OWNER_CLOSED = "owner_closed"
    const val IDLE = "idle"

    fun forReason(reason: String?): String = when (reason) {
        OWNER_CLOSED -> "Bye for now."
        IDLE -> "Stopped after a quiet minute."
        "voice_mode_declined" -> "Voice stopped. Everyday voice needs a spoken yes after the prompt."
        else -> "Voice stopped. Your conversation is saved."
    }
}
