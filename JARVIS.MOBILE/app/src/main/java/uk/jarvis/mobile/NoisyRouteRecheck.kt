package uk.jarvis.mobile

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Coalesces an own-route NOISY burst into one cancellable check at the grace boundary. */
internal class NoisyRouteRecheck(
    private val scope: CoroutineScope,
    private val remainingDelayMs: () -> Long?,
    private val shouldStop: () -> Boolean,
    private val stop: () -> Unit,
    private val wait: suspend (Long) -> Unit = { delay(it) },
) {
    private var pending: Job? = null
    private var generation = 0L

    fun noisy() = evaluate(null)

    private fun evaluate(expected: Long?) {
        var stopNow = false
        var scheduled: Job? = null
        synchronized(this) {
            if (expected != null && generation != expected) return
            val remaining = remainingDelayMs()
            if (shouldStop()) {
                cancel()
                stopNow = true
            } else if (remaining != null && pending?.isActive != true) {
                val current = generation
                scheduled = scope.launch(start = CoroutineStart.LAZY) {
                    wait(remaining)
                    synchronized(this@NoisyRouteRecheck) {
                        if (generation != current) return@launch
                        pending = null
                    }
                    evaluate(current) // A newer route change may have moved the grace boundary.
                }
                pending = scheduled
            }
        }
        // Route events/timer callbacks run on Main. Never call the service stop
        // path while holding this monitor: it also takes wake/audio locks.
        scheduled?.start()
        if (stopNow) stop()
    }

    @Synchronized fun cancel() {
        generation++
        pending?.cancel()
        pending = null
    }
}
