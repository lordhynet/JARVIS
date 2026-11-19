package uk.jarvis.mobile

import kotlinx.coroutines.CancellationException

internal class VoiceStartCheckedFailure(message: String, cause: Exception) : Exception(message, cause)

/** Recheck only unpaid connection metadata; never replay a failed voice preparation. */
internal suspend fun <T> startWithConnectionCheck(
    start: suspend () -> T,
    check: suspend () -> String,
    accessRevoked: (Exception) -> Boolean,
): T {
    try { return start() }
    catch (error: CancellationException) { throw error }
    catch (error: Exception) {
        if (accessRevoked(error)) throw error
        val status = check() // Cancellation and revocation during the check propagate unchanged.
        throw VoiceStartCheckedFailure("${error.message ?: "Voice did not start."} $status Tap the circle again when ready.", error)
    }
}
