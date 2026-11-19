package uk.jarvis.mobile

import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout

/** Actual candidate admission path, independent of Android so its races can be tested with fake Core calls. */
internal class WakeCandidateAdmission<T>(
    private val eligible: () -> Boolean,
    private val request: suspend () -> T,
    private val release: suspend (T) -> Unit,
    private val accept: (T) -> Unit,
) {
    suspend fun run(): Boolean {
        if (!eligible()) return false
        val candidate = request()
        if (!eligible()) {
            // Stopping the recognizer cancels arbitration. Still release a received but unused ticket.
            withContext(NonCancellable) { runCatching { withTimeout(2000) { release(candidate) } } }
            return false
        }
        accept(candidate)
        return true
    }
}
