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
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

internal class VoiceTransport(
    private val scope: CoroutineScope,
    private val lifecycle: VoiceLifecycle,
    private val listener: Listener,
) {
    interface Listener {
        suspend fun stopCapture(current: Long)
        fun ready(current: Long, window: WebSocket, voiceName: String?)
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
    @Volatile private var socket: WebSocket? = null
    @Volatile private var lease: AudioLease? = null
    @Volatile private var providerWindow = -1
    private val leaseDeadline = AtomicLong(0)
    private val pings = ConcurrentHashMap<Long, Long>()
    private var sequence = 0L
    private var renewWanted = false
    private var reconnectJob: Job? = null

    @Synchronized fun start(session: String, ticket: VoiceTicket?) {
        sessionId = session
        pendingTicket = ticket
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
        connectRecovering(saved, current)
        scope.launch { heartbeat(current) }
        scope.launch {
            while (lifecycle.isLive(current)) {
                delay(250)
                if (lifecycle.isLive(current) && lifecycle.ready && !leaseIsCurrent()) reconnect(current)
            }
        }
    }

    fun leaseIsCurrent(): Boolean = SystemClock.elapsedRealtime() < leaseDeadline.get()
    fun isWindow(current: Long, window: WebSocket): Boolean = lifecycle.isLive(current) && socket === window

    fun sendAudio(current: Long, window: WebSocket, bytes: ByteArray) {
        if (!isWindow(current, window) || !lifecycle.ready || !leaseIsCurrent()) return
        val number = providerWindow
        if (number < 0) return
        if (window.queueSize() > 256000) {
            listener.stopped("Network cannot keep up with live audio.")
            return
        }
        window.send(gson.toJson(mapOf("type" to "audio", "window" to number, "audio" to Base64.encodeToString(bytes, Base64.NO_WRAP))))
    }

    fun interrupt(itemId: String, playedMs: Long) {
        val number = providerWindow
        if (number >= 0) socket?.send(gson.toJson(mapOf("type" to "interrupt", "window" to number, "itemId" to itemId, "playedMs" to playedMs)))
    }

    private suspend fun heartbeat(current: Long) {
        while (lifecycle.isLive(current)) {
            delay(4000)
            if (!lifecycle.isLive(current)) break
            if (!lifecycle.ready) continue
            val id = ++sequence
            pings[id] = SystemClock.elapsedRealtime()
            socket?.send(gson.toJson(mapOf("type" to "heartbeat", "sequence" to id)))
            if (pings.size > 8) { reconnect(current); continue }
            try { core?.send(CoreCompanionRoutes.heartbeat(deviceId), emptyMap<String, String>(), JsonObject::class.java) }
            catch (e: CoreHttpException) { if (lifecycle.isLive(current)) listener.stopped(e.message ?: "Core rejected device access.") }
            catch (_: IOException) { reconnect(current) }
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
            providerWindow = -1
            leaseDeadline.set(at + 15000)
            pings.clear()
            val websocket = http?.newWebSocket(Request.Builder().url(url).build(), WindowListener(current, ticket, established))
                ?: return
            socket = websocket
            websocket
        }
        try { withTimeout(12000) { established.await() } }
        catch (error: Exception) { window.cancel(); throw error }
    }

    private inner class WindowListener(
        private val current: Long, private val ticket: VoiceTicket, private val established: CompletableDeferred<Unit>,
    ) : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            synchronized(this@VoiceTransport) {
                if (isWindow(current, webSocket)) webSocket.send(gson.toJson(mapOf("type" to "authenticate", "ticket" to ticket.ticket, "continuous" to true)))
                else webSocket.close(1000, "Cancelled")
            }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (!isWindow(current, webSocket)) return
            try {
                val message = JsonParser.parseString(text).asJsonObject
                when (message.get("type").asString) {
                    "ready" -> if (synchronized(this@VoiceTransport) { isWindow(current, webSocket) && lifecycle.markReady(current) }) {
                        providerWindow = message.get("window")?.asInt ?: 0
                        retry.connected()
                        established.complete(Unit)
                        val voiceName = message.get("voiceName")?.takeIf { it.isJsonPrimitive && it.asJsonPrimitive.isString }?.asString
                        listener.ready(current, webSocket, voiceName)
                    }
                    "window_renewing" -> providerWindow = -1
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
                    "error" -> listener.stopped(message.get("detail").asString)
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
            } else if (lifecycle.isLive(current)) listener.stopped("Live voice stopped. Completed turns are saved in Core.")
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            val wasEstablished = established.isCompleted
            established.completeExceptionally(IOException("Live connection failed", t))
            if (wasEstablished && isWindow(current, webSocket)) reconnect(current)
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
                val backoff = retry.nextDelayMillis() ?: error("Connection recovery exhausted. Start voice again when your network is ready.")
                listener.reconnecting("Reconnecting to Core in ${backoff / 1000}s. Microphone is off.")
                delay(backoff)
            }
            if (!lifecycle.isLive(current)) return
            try { connectWindow(saved, current); return }
            catch (e: CoreHttpException) { throw e }
            catch (e: CancellationException) { if (e !is TimeoutCancellationException) throw e; wait = true }
            catch (_: IOException) { wait = true }
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
        old?.cancel()
        pings.clear()
        val saved = connection ?: return
        reconnectJob = scope.launch {
            try {
                val drain = if (immediate) 250L else remainingLeaseMillis()
                if (drain > 0) delay(drain)
                connectRecovering(saved, current, initialDelay = !immediate)
            } catch (e: Exception) {
                if (lifecycle.isLive(current)) listener.stopped(e.message ?: "Voice reconnection failed.")
            }
        }
    }

    private fun remainingLeaseMillis(): Long = (leaseDeadline.get() - SystemClock.elapsedRealtime()).coerceIn(0, 15000)

    @Synchronized fun stop(reason: String?): Release? {
        val stopped = lease
        val client = core
        lease = null
        pendingTicket = null
        providerWindow = -1
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

    class Release(private val client: CoreApi, private val lease: AudioLease) {
        fun afterCapture(capture: Job?) {
            // The service has stopped playback. Acknowledge once after capture exits.
            val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
            scope.launch {
                try { withTimeout(3000) { capture?.join(); client.send(CoreCompanionRoutes.VOICE_LOCAL_RELEASE, lease, Boolean::class.javaObjectType) } }
                catch (_: Exception) { /* Core retains the original drain expiry if acknowledgement is lost. */ }
                finally { scope.cancel() }
            }
        }
    }
}
