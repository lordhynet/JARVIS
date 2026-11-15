package uk.jarvis.mobile

/** Transport recovery only. Never retains or replays microphone audio. */
internal class VoiceReconnectPolicy {
    private var attempts = 0
    @Synchronized fun nextDelayMillis(): Long? {
        val delays = longArrayOf(1000, 2000, 4000, 8000, 8000)
        return if (attempts < delays.size) delays[attempts++] else null
    }
    @Synchronized fun connected() { attempts = 0 }
}

/** Three seconds of mono 24 kHz PCM16, irrespective of provider packet size. */
internal class VoicePlaybackBudget(private val limit: Int = 144000) {
    private var bytes = 0
    @Synchronized fun reserve(count: Int): Boolean {
        if (count <= 0 || count % 2 != 0 || count > limit - bytes) return false
        bytes += count; return true
    }
    @Synchronized fun release(count: Int) { bytes = (bytes - count).coerceAtLeast(0) }
    @Synchronized fun clear() { bytes = 0 }
}
