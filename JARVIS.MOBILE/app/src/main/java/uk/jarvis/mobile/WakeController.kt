package uk.jarvis.mobile

import android.content.Context
import com.google.gson.JsonObject
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import uk.jarvis.mobile.contracts.CoreCompanionRoutes
import uk.jarvis.mobile.contracts.PrivacyStatus
import uk.jarvis.mobile.contracts.WakeCandidateRequest
import uk.jarvis.mobile.contracts.WakeCandidateResponse
import java.io.IOException
import java.util.UUID

internal class WakeController(
    private val context: Context,
    private val scope: CoroutineScope,
    private val lifecycle: VoiceLifecycle,
    private val onListening: () -> Unit,
    private val onArbitrating: () -> Unit,
    private val onTicket: (WakeCandidateResponse, Long) -> Unit,
    private val onRetry: (String) -> Unit,
    private val onUnavailable: (String) -> Unit,
    private val pausedBy: () -> String? = { null },
    private val onPaused: (String) -> Unit = {},
) {
    private val settings = context.getSharedPreferences("installation", 0)
    @Volatile var enabled = false
        private set
    private var detector: LocalWakeDetector? = null
    private var detectorGeneration = 0L
    private var arbitration: Job? = null

    val wasEnabled: Boolean get() = settings.getBoolean("local-wake-enabled", false)

    @Synchronized fun enable() {
        enabled = true
        settings.edit().putBoolean("local-wake-enabled", true).apply()
    }

    @Synchronized fun disable(persist: Boolean = true) {
        enabled = false
        if (persist) settings.edit().putBoolean("local-wake-enabled", false).apply()
        suspendListening()
    }

    @Synchronized fun suspendListening() {
        detectorGeneration++
        detector?.stop()
        detector = null
        arbitration?.cancel()
        arbitration = null
    }

    /** Re-checks lock, call and battery-saver state; pauses or resumes local listening as needed. */
    @Synchronized fun reevaluate() {
        if (!enabled || lifecycle.active) return
        val reason = pausedBy()
        if (reason != null) { if (detector != null || arbitration != null) suspendListening(); onPaused(reason) }
        else if (detector == null && arbitration == null) listen()
    }

    @Synchronized fun listen() {
        if (!enabled) return
        pausedBy()?.let { reason -> suspendListening(); onPaused(reason); return }
        if (!lifecycle.listen()) return
        detector?.stop()
        val current = ++detectorGeneration
        detector = LocalWakeDetector(context, { detected(current) }, { message ->
            val liveListener = synchronized(this) { current == detectorGeneration && enabled }
            if (liveListener) onUnavailable(message)
        }).also { it.start() }
        onListening()
    }

    @Synchronized private fun detected(listener: Long) {
        if (!enabled || listener != detectorGeneration) return
        val generation = lifecycle.beginArbitration() ?: return
        detector = null
        onArbitrating()
        arbitration = scope.launch {
            try {
                val candidate = requestCandidate()
                if (enabled && lifecycle.isArbitrating(generation)) onTicket(candidate, generation)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                if (!enabled || !lifecycle.isArbitrating(generation)) return@launch
                onRetry(when (error) {
                    is CoreHttpException -> if (error.status == 409) "Another device won wake arbitration. Still listening locally."
                        else "Core rejected the wake request: ${error.message}"
                    is IOException -> "Core is unreachable. Local wake remains on; reconnect before trying again."
                    else -> error.message ?: "Wake arbitration failed. Local wake remains on."
                })
                delay(1500)
                if (lifecycle.isArbitrating(generation)) listen()
            }
        }
    }

    private suspend fun requestCandidate(): WakeCandidateResponse {
        val saved = CredentialVault(context).read() ?: error("Reconnect this phone to Core before using local wake.")
        val client = CoreApi(saved.url, saved.token, saved.certificateSha256)
        try {
            val privacy = client.get(CoreCompanionRoutes.PRIVACY_STATUS, PrivacyStatus::class.java)
            client.bindEpoch(privacy.epoch)
            val deviceId = settings.getString("id", "").orEmpty()
            client.send(CoreCompanionRoutes.heartbeat(deviceId), emptyMap<String, String>(), JsonObject::class.java)
            return client.send(CoreCompanionRoutes.VOICE_WAKE_CANDIDATES,
                WakeCandidateRequest(UUID.randomUUID().toString(), deviceId, true), WakeCandidateResponse::class.java)
        } finally { client.invalidate() }
    }
}
