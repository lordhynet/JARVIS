package uk.jarvis.mobile

/**
 * Transport recovery only. Never retains or replays microphone audio.
 * Backoff 1, 2, 4, 8, 16 s (capped) with ±20% jitter, inside one 60-second recovery window that starts at the
 * first failure. The window, not an attempt count, bounds recovery, so a 30-second Wi-Fi drop still recovers.
 */
internal class VoiceReconnectPolicy(
    private val clock: () -> Long = { android.os.SystemClock.elapsedRealtime() },
    private val random: () -> Double = { Math.random() },
) {
    companion object {
        const val WINDOW_MILLIS = 60_000L
        const val MINIMUM_ATTEMPT_MILLIS = 5_000L
        private val steps = longArrayOf(1000, 2000, 4000, 8000, 16000)
    }
    private var attempts = 0
    private var startedAt: Long? = null

    /** The next wait, or null when the recovery window cannot fit another attempt. */
    @Synchronized fun nextDelayMillis(): Long? {
        val now = clock()
        val start = startedAt ?: now.also { startedAt = it }
        val base = steps[attempts.coerceAtMost(steps.size - 1)]
        val delay = (base * (0.8 + 0.4 * random().coerceIn(0.0, 1.0))).toLong()
        if (now + delay + MINIMUM_ATTEMPT_MILLIS > start + WINDOW_MILLIS) return null
        attempts++
        return delay
    }

    /** How long one connection attempt may take before the recovery window closes (never longer than [cap]). */
    @Synchronized fun attemptBudgetMillis(cap: Long): Long {
        val start = startedAt ?: return cap
        return (start + WINDOW_MILLIS - clock()).coerceIn(MINIMUM_ATTEMPT_MILLIS, cap)
    }

    @Synchronized fun connected() { attempts = 0; startedAt = null }
}

/** Which failures are worth another attempt. Owner decisions, revoked keys and certificate changes are final. */
internal object VoiceRecovery {
    private val transientStatus = setOf(408, 502, 504)
    fun retryable(error: Throwable): Boolean = when {
        CertificatePin.failureMessage(error) != null -> false
        error is CoreHttpException -> error.status in transientStatus
        error is java.io.IOException -> true
        else -> false
    }
    const val EXHAUSTED = "Couldn't reconnect. Tap to start again when you're back online."
    fun waiting(seconds: Long) = "Reconnecting in ${seconds}s… microphone is off."
}

/** Twenty seconds of mono 24 kHz PCM16, irrespective of provider packet size. */
internal class VoicePlaybackBudget(private val limit: Int = 960000) {
    private var bytes = 0
    @Synchronized fun reserve(count: Int): Boolean {
        if (count <= 0 || count % 2 != 0 || count > limit - bytes) return false
        bytes += count; return true
    }
    @Synchronized fun release(count: Int) { bytes = (bytes - count).coerceAtLeast(0) }
    @Synchronized fun clear() { bytes = 0 }
}
