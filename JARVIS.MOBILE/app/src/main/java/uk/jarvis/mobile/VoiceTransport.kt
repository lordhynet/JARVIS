package uk.jarvis.mobile

import android.os.SystemClock
import android.util.Base64
import com.google.gson.Gson
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import kotlinx.coroutines.*
import okhttp3.*
import uk.jarvis.mobile.contracts.*
import java.io.IOException
import java.math.BigDecimal
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

data class VoiceCostOffer(val id: String, val poundsForTenMinutes: BigDecimal, val starting: Boolean = false)

internal class VoiceTransport(
    private val scope: CoroutineScope,
    private val lifecycle: VoiceLifecycle,
    private val listener: Listener,
) {
    interface Listener {
        suspend fun stopCapture(current: Long)
        fun ready(current: Long, window: WebSocket, voiceName: String?, mode: String?)
        fun modeOffer(offer: VoiceCostOffer)
        fun modeNotice(message: String, queuedMode: String? = null)
        fun modeRenewing()
        fun seriousUnavailable(message: String)
        fun fallbackPrompt(detail: String, prompt: String, finished: (Boolean) -> Unit)
        fun fallbackCapture(current: Long, window: WebSocket)
        fun fallbackVerifying()
        fun renewed()
        fun audio(current: Long, window: WebSocket, itemId: String, bytes: ByteArray)
        fun interrupt(send: Boolean = true)
        fun pauseWindow()
        fun reconnecting(message: String? = null)
        fun stopped(message: String)
    }

    private val gson = Gson()
    private val retry = VoiceReconnectPolicy()
    private var http: OkHttpClient? = null
    private var core: CoreApi? = null
    private var connection: SavedConnection? = null
    private var sessionId = ""
    private var deviceId = ""
    private var pendingTicket: VoiceTicket? = null
    private var startEveryday = false
    @Volatile private var socket: WebSocket? = null
    @Volatile private var lease: AudioLease? = null
    @Volatile private var providerWindow = -1
    @Volatile private var authenticated: WebSocket? = null
    @Volatile private var pendingQuote: VoiceCostOffer? = null
    @Volatile private var fallbackOffer: String? = null
    @Volatile private var fallbackCapture: String? = null
    private val leaseDeadline = AtomicLong(0)
    private val pings = ConcurrentHashMap<Long, Long>()
    private var sequence = 0L
    private var renewWanted = false
    private var reconnectJob: Job? = null
    // Conflated: any number of "network is back" signals wake at most one waiting retry.
    private val networkBack = kotlinx.coroutines.channels.Channel<Unit>(kotlinx.coroutines.channels.Channel.CONFLATED)

    /** Called when Android reports a usable default network; skips the rest of the current backoff wait. */
    fun networkAvailable() { networkBack.trySend(Unit) }

    @Synchronized fun start(session: String, ticket: VoiceTicket?, everyday: Boolean = false) {
        sessionId = session
        pendingTicket = ticket
        startEveryday = everyday
        retry.connected()
    }

    suspend fun prepare(saved: SavedConnection, pairingId: String, current: Long): Boolean {
        val client = CoreApi(saved.url, saved.token, saved.certificateSha256)
        val privacy = client.get(CoreCompanionRoutes.PRIVACY_STATUS, PrivacyStatus::class.java)
        val builder = OkHttpClient.Builder().readTimeout(0, TimeUnit.MILLISECONDS)
            .pingInterval(10, TimeUnit.SECONDS).connectTimeout(10, TimeUnit.SECONDS)
        saved.certificateSha256?.let { builder.sslSocketFactory(CertificatePin.sockets(it), CertificatePin.trustManager(it)) }
        synchronized(this) {
            if (!lifecycle.isLive(current)) {
                client.invalidate()
                return false
            }
            client.bindEpoch(privacy.epoch)
            connection = saved
            core = client
            deviceId = pairingId
            http = builder.build()
        }
        return true
    }

    suspend fun connect(current: Long) {
        val saved = connection ?: return
        scope.launch { heartbeat(current) }
        scope.launch {
            while (lifecycle.isLive(current)) {
                delay(250)
                if (lifecycle.isLive(current) && lifecycle.ready && !leaseIsCurrent()) reconnect(current)
            }
        }
        connectRecovering(saved, current)
    }

    fun leaseIsCurrent(): Boolean = SystemClock.elapsedRealtime() < leaseDeadline.get()
    fun isWindow(current: Long, window: WebSocket): Boolean = lifecycle.isLive(current) && socket === window

    fun sendAudio(current: Long, window: WebSocket, bytes: ByteArray) {
        if (!isWindow(current, window) || !leaseIsCurrent()) return
        val captureId = fallbackCapture
        val number = providerWindow
        if (captureId == null && (number < 0 || !lifecycle.ready || pendingQuote?.starting == true)) return
        if (window.queueSize() > 256000) {
            listener.stopped("Network cannot keep up with live audio.")
            return
        }
        val audio = Base64.encodeToString(bytes, Base64.NO_WRAP)
        val frame = if (captureId == null) mapOf("type" to "audio", "window" to number, "audio" to audio)
            else mapOf("type" to "voice_fallback_audio", "captureId" to captureId, "audio" to audio)
        window.send(gson.toJson(frame))
    }

    fun interrupt(itemId: String, playedMs: Long) {
        val number = providerWindow
        if (number >= 0) socket?.send(gson.toJson(mapOf("type" to "interrupt", "window" to number, "itemId" to itemId, "playedMs" to playedMs)))
    }

    @Synchronized fun requestMode(mode: String) = sendMode(mapOf("type" to "voice_mode", "mode" to mode))

    @Synchronized fun confirmMode(offerId: String) = sendMode(mapOf("type" to "confirm_voice_mode", "offerId" to offerId))

    private fun sendMode(frame: Map<String, String>) {
        val number = providerWindow
        val quote = pendingQuote?.takeIf { it.starting }
        if (number <= 0 || !leaseIsCurrent() || (!lifecycle.ready && quote == null)) return
        val scoped = if (quote == null) frame else frame + ("offerId" to quote.id)
        socket?.send(gson.toJson(scoped + ("window" to number)))
    }

    private suspend fun heartbeat(current: Long) {
        while (lifecycle.isLive(current)) {
            delay(4000)
            if (!lifecycle.isLive(current)) break
            val currentSocket = socket ?: continue
            if (authenticated !== currentSocket) continue
            val id = ++sequence
            pings[id] = SystemClock.elapsedRealtime()
            currentSocket.send(gson.toJson(mapOf("type" to "heartbeat", "sequence" to id)))
            if (pings.size > 8) { reconnect(current); continue }
            try { core?.send(CoreCompanionRoutes.heartbeat(deviceId), emptyMap<String, String>(), JsonObject::class.java) }
            catch (e: CoreHttpException) { if (lifecycle.isLive(current)) listener.stopped(e.message ?: "Core rejected device access.") }
            catch (error: IOException) {
                val changed = CertificatePin.failureMessage(error)
                if (changed != null && lifecycle.isLive(current)) listener.stopped(changed)
                else reconnect(current)
            }
            catch (e: Exception) { if (lifecycle.isLive(current)) listener.stopped(e.message ?: "Core privacy access changed.") }
        }
    }

    private suspend fun connectWindow(saved: SavedConnection, current: Long) {
        listener.stopCapture(current)
        val at = SystemClock.elapsedRealtime()
        val client: CoreApi
        val existing: VoiceTicket?
        val request: VoiceStartRequest
        synchronized(this) {
            if (!lifecycle.isLive(current)) return
            client = core ?: return
            existing = pendingTicket
            pendingTicket = null
            request = VoiceStartRequest(sessionId, deviceId, true)
        }
        val ticket = existing ?: client.send(CoreCompanionRoutes.VOICE_TICKETS, request, VoiceTicket::class.java)
        val established = CompletableDeferred<Unit>()
        val url = saved.url.replaceFirst("https://", "wss://") + CoreCompanionRoutes.VOICE_STREAM
        val window = synchronized(this) {
            if (!lifecycle.pauseWindow(current)) return
            lease = ticket.lease
            resetMode()
            leaseDeadline.set(at + 15000)
            pings.clear()
            val websocket = http?.newWebSocket(Request.Builder().url(url).build(), WindowListener(current, ticket, established))
                ?: return
            socket = websocket
            websocket
        }
        try { withTimeout(retry.attemptBudgetMillis(55000)) { established.await() } }
        catch (error: Exception) { window.cancel(); throw error }
    }

    private inner class WindowListener(
        private val current: Long, private val ticket: VoiceTicket, private val established: CompletableDeferred<Unit>,
    ) : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            synchronized(this@VoiceTransport) {
                if (!isWindow(current, webSocket)) {
                    webSocket.close(1000, "Cancelled")
                    return
                }
                val frame = mutableMapOf<String, Any>("type" to "authenticate", "ticket" to ticket.ticket,
                    "continuous" to true, "voiceModeNegotiation" to true)
                if (startEveryday) frame["startEveryday"] = true
                if (webSocket.send(gson.toJson(frame))) authenticated = webSocket
            }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (!isWindow(current, webSocket)) return
            try {
                val message = JsonParser.parseString(text).asJsonObject
                when (message.get("type").asString) {
                    "ready" -> if (synchronized(this@VoiceTransport) { isWindow(current, webSocket) && lifecycle.markReady(current) }) {
                        clearChoices()
                        providerWindow = message.get("window")?.asInt ?: 0
                        startEveryday = false
                        retry.connected()
                        established.complete(Unit)
                        val voiceName = message.get("voiceName")?.takeIf { it.isJsonPrimitive && it.asJsonPrimitive.isString }?.asString
                        val mode = message.get("mode")?.asString?.takeIf { it == "Everyday" || it == "Serious" }
                        listener.ready(current, webSocket, voiceName, mode)
                    }
                    "window_renewing" -> {
                        clearChoices()
                        providerWindow = -1
                        listener.modeRenewing()
                    }
                    "voice_mode_offer" -> {
                        val starting = message.get("starting")?.asBoolean == true
                        val number = message.get("window")?.asInt ?: 0
                        val maximum = message.get("maximumGbpForTenMinutes").asBigDecimal
                        if (number > 0 && (starting || number == providerWindow) && maximum.signum() > 0) {
                            val offer = VoiceCostOffer(message.get("offerId").asString, maximum, starting)
                            if (starting) providerWindow = number
                            pendingQuote = offer
                            listener.modeOffer(offer)
                        }
                    }
                    "voice_mode_queued" -> if (message.get("window")?.asInt == providerWindow && providerWindow > 0) {
                        val mode = message.get("mode").asString
                        listener.modeNotice("$mode voice queued for the next renewal.", mode)
                    }
                    "voice_mode_rejected" -> if (message.get("window")?.asInt == providerWindow && providerWindow > 0) {
                        listener.modeNotice(message.get("detail").asString)
                    }
                    "voice_fallback_offer" -> {
                        clearChoices()
                        providerWindow = -1
                        val id = message.get("captureId").asString
                        fallbackOffer = id
                        listener.fallbackPrompt(message.get("detail").asString, message.get("prompt").asString) { played ->
                            if (isWindow(current, webSocket) && id == fallbackOffer && leaseIsCurrent()) {
                                val type = if (played) "voice_fallback_played" else "voice_fallback_cancelled"
                                webSocket.send(gson.toJson(mapOf("type" to type, "captureId" to id)))
                            }
                        }
                    }
                    "voice_fallback_capture" -> if (message.get("captureId").asString == fallbackOffer) {
                        fallbackCapture = fallbackOffer
                        listener.fallbackCapture(current, webSocket)
                    }
                    "voice_fallback_verifying" -> if (message.get("captureId").asString == fallbackOffer) {
                        fallbackCapture = null
                        listener.fallbackVerifying()
                    }
                    "lease" -> pings.remove(message.get("sequence").asLong)?.let { sent ->
                        leaseDeadline.set(sent + message.get("leaseSeconds").asLong * 1000)
                        listener.renewed()
                    }
                    "interrupt" -> listener.interrupt()
                    "audio" -> if (lifecycle.ready && leaseIsCurrent()) {
                        val delta = message.get("delta").asString
                        check(delta.length <= 1280000)
                        listener.audio(current, webSocket, message.get("itemId").asString, Base64.decode(delta, Base64.DEFAULT))
                    }
                    "transcript" -> Unit
                    "error" -> {
                        val detail = message.get("detail").asString
                        if (message.get("code")?.takeUnless { it.isJsonNull }?.asString == "serious_voice_unavailable")
                            listener.seriousUnavailable(detail)
                        else listener.stopped(detail)
                    }
                    "window_limit" -> {
                        renewWanted = true
                        lifecycle.pauseWindow(current)
                        listener.pauseWindow()
                        webSocket.send("{\"type\":\"stop\"}")
                    }
                    "ended" -> onEnded(webSocket, message.get("reason").asString)
                }
            } catch (_: Exception) {
                if (isWindow(current, webSocket)) listener.stopped("Invalid live-voice event. Audio stopped.")
            }
        }

        private fun onEnded(webSocket: WebSocket, reason: String) {
            synchronized(this@VoiceTransport) {
                if (!isWindow(current, webSocket)) return
                lifecycle.pauseWindow(current)
                socket = null
            }
            listener.interrupt(send = false)
            webSocket.close(1000, "Window ended")
            if (lifecycle.isLive(current) && (reason == "window_complete" || renewWanted)) {
                renewWanted = false
                listener.reconnecting("Renewing budgeted voice window…")
                reconnect(current, immediate = true)
            } else if (lifecycle.isLive(current)) listener.stopped(VoiceEndMessages.forReason(reason))
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            val wasEstablished = established.isCompleted
            established.completeExceptionally(IOException("Live connection failed", t))
            if (wasEstablished && isWindow(current, webSocket)) {
                val changed = CertificatePin.failureMessage(t)
                if (changed != null) listener.stopped(changed) else reconnect(current)
            }
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            val wasEstablished = established.isCompleted
            established.completeExceptionally(IOException("Live connection closed"))
            if (wasEstablished && isWindow(current, webSocket)) {
                if (code == 1000 || code == 1008) listener.stopped("Core ended voice ownership. Start again when ready.")
                else reconnect(current)
            }
        }
    }

    private suspend fun connectRecovering(saved: SavedConnection, current: Long, initialDelay: Boolean = false) {
        var wait = initialDelay
        while (lifecycle.isLive(current)) {
            if (wait) {
                val backoff = retry.nextDelayMillis() ?: error(VoiceRecovery.EXHAUSTED)
                listener.reconnecting(VoiceRecovery.waiting((backoff + 999) / 1000))
                while (networkBack.tryReceive().isSuccess) Unit // Only a signal that arrives during this wait counts.
                withTimeoutOrNull(backoff) { networkBack.receive() }
            }
            if (!lifecycle.isLive(current)) return
            try { connectWindow(saved, current); return }
            catch (e: CancellationException) { if (e !is TimeoutCancellationException) throw e; wait = true }
            catch (error: IOException) {
                // Gateway hiccups and dropped networks retry; revoked keys, owner refusals and certificate changes stop.
                if (!VoiceRecovery.retryable(error)) throw error
                wait = true
            }
            // A missing ready frame can leave a live lease on Core.
            val drain = remainingLeaseMillis()
            if (drain > 0 && lifecycle.isLive(current)) delay(drain)
        }
    }

    @Synchronized private fun reconnect(current: Long, immediate: Boolean = false) {
        if (reconnectJob?.isActive == true || !lifecycle.beginReconnect(current)) return
        listener.reconnecting()
        listener.pauseWindow()
        val old = socket
        socket = null
        resetMode()
        old?.cancel()
        pings.clear()
        val saved = connection ?: return
        reconnectJob = scope.launch {
            try {
                val drain = if (immediate) 250L else remainingLeaseMillis()
                if (drain > 0) delay(drain)
                connectRecovering(saved, current, initialDelay = !immediate)
            } catch (e: Exception) {
                if (lifecycle.isLive(current)) listener.stopped(CertificatePin.failureMessage(e) ?: e.message ?: "Voice reconnection failed.")
            }
        }
    }

    private fun remainingLeaseMillis(): Long = (leaseDeadline.get() - SystemClock.elapsedRealtime()).coerceIn(0, 15000)

    @Synchronized fun stop(reason: String?): Release? {
        val stopped = lease
        val client = core
        lease = null
        pendingTicket = null
        resetMode()
        renewWanted = false
        val frame = if (reason == "no_speech_timeout") gson.toJson(mapOf("type" to "stop", "reason" to reason)) else "{\"type\":\"stop\"}"
        socket?.send(frame)
        socket?.close(1000, "Stopped")
        socket = null
        reconnectJob?.cancel()
        reconnectJob = null
        pings.clear()
        http?.dispatcher?.executorService?.shutdown()
        http?.connectionPool?.evictAll()
        http = null
        core = null
        connection = null
        return if (stopped != null && client != null) Release(client, stopped) else null
    }

    private fun clearChoices() {
        pendingQuote = null
        fallbackOffer = null
        fallbackCapture = null
    }

    private fun resetMode() {
        authenticated = null
        providerWindow = -1
        clearChoices()
    }

    class Release(private val client: CoreApi, private val lease: AudioLease) {
        fun afterCapture(capture: Job?): Job {
            // The service has stopped playback. Acknowledge once after capture exits.
            val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
            return scope.launch {
                try { withTimeout(3000) { capture?.join(); client.send(CoreCompanionRoutes.VOICE_LOCAL_RELEASE, lease, Boolean::class.javaObjectType) } }
                catch (_: Exception) { /* Core retains the original drain expiry if acknowledgement is lost. */ }
                finally { scope.cancel() }
            }
        }
    }
}
