package uk.jarvis.mobile

import kotlinx.coroutines.delay
import kotlinx.coroutines.withTimeout
import uk.jarvis.mobile.contracts.*

/** Transport handshake only. Core selects the conversation and owns every transition. */
internal class HandoffTransfer(private val device: String, private val exchange: suspend (CompanionHandoffRequest) -> CompanionHandoffView) {
    companion object {
        fun continuePrepared(expected: String?, actual: String, start: () -> Unit) {
            check(expected == null || expected == actual) { "The transfer expired or selected conversation changed. Start the handoff again; no voice was started." }
            start()
        }
    }
    suspend fun decline(offer: CompanionHandoffItem, locked: Boolean): CompanionHandoffView =
        exchange(CompanionHandoffRequest(device, "cancel", offer.sessionId, offer.handoffId, locked = locked))
    suspend fun accept(offer: CompanionHandoffItem, locked: () -> Boolean) {
        check(!locked()) { "Unlock this phone before accepting." }
        exchange(CompanionHandoffRequest(device, "ready", offer.sessionId, offer.handoffId, locked = false))
        withTimeout(22000) {
            while (true) {
                check(!locked()) { "Phone locked. Open JARVIS and accept again." }
                val current = exchange(CompanionHandoffRequest(device, "poll", locked = false)).offers.firstOrNull { it.handoffId == offer.handoffId }
                check(current != null) { "Conversation transfer expired or was cancelled. Start again on the source." }
                if (current.state == HandoffState.AudioTransferred) return@withTimeout
                delay(500)
            }
        }
    }
    suspend fun release(offer: CompanionHandoffItem, stopAndDrain: suspend () -> Unit, locked: Boolean) {
        stopAndDrain()
        exchange(CompanionHandoffRequest(device, "release", offer.sessionId, offer.handoffId, locked = locked, audioStopped = true))
    }
}
