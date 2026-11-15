package uk.jarvis.mobile

import kotlin.math.sqrt

/** Three consecutive speech-like frames suppress the wake-start silence timeout. */
internal class WakeSpeechOnsetDetector(
    private val rmsThreshold: Double = 750.0,
    private val consecutiveFramesRequired: Int = 3,
) {
    private var consecutiveVoicedFrames = 0

    fun observe(samples: ShortArray, count: Int): Boolean {
        val boundedCount = count.coerceIn(0, samples.size)
        if (boundedCount == 0) { consecutiveVoicedFrames = 0; return false }
        var squares = 0.0
        for (index in 0 until boundedCount) {
            val sample = samples[index].toDouble()
            squares += sample * sample
        }
        if (sqrt(squares / boundedCount) >= rmsThreshold) consecutiveVoicedFrames++ else consecutiveVoicedFrames = 0
        return consecutiveVoicedFrames >= consecutiveFramesRequired
    }
}

/** Monotonic, bounded first-speech window used only for sessions started by local wake. */
internal class WakeSpeechTimeout(private val timeoutMillis: Long = 8_000) {
    private var firstAudioAt: Long? = null
    private var speechObserved = false

    fun shouldClose(nowMillis: Long, speechDetected: Boolean): Boolean {
        if (firstAudioAt == null) firstAudioAt = nowMillis
        if (speechDetected) speechObserved = true
        return !speechObserved && nowMillis - firstAudioAt!! >= timeoutMillis
    }
}
