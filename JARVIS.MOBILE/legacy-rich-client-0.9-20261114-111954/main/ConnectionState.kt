package uk.jarvis.mobile

import uk.jarvis.mobile.contracts.ConversationSession

/** Leaving pairing is part of accepting a connection, including reconnects with a cache. */
internal fun ScreenState.connectionEstablished(
    url: String,
    sessions: List<ConversationSession>,
    pendingWrites: List<PracticeWrite>
): ScreenState = copy(
    connected = true,
    pairingOffered = false,
    busy = true,
    offline = false,
    url = url,
    sessions = sessions,
    pendingWrites = pendingWrites,
    notice = ""
)
