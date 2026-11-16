package uk.jarvis.mobile

import android.Manifest
import android.app.*
import android.content.*
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.*
import android.media.audiofx.AcousticEchoCanceler
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.*
import android.view.KeyEvent
import android.util.Base64
import com.google.gson.Gson
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import okhttp3.*
import uk.jarvis.mobile.contracts.*
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import java.io.IOException

data class LiveVoiceState(val active: Boolean = false, val ready: Boolean = false, val message: String = "", val wakeListening: Boolean = false)

class LiveVoiceService : Service() {
    companion object {
        const val START = "uk.jarvis.mobile.LIVE_START"
        const val STOP = "uk.jarvis.mobile.LIVE_STOP"
        const val INTERRUPT = "uk.jarvis.mobile.LIVE_INTERRUPT"
        const val WAKE_START = "uk.jarvis.mobile.LOCAL_WAKE_START"
        const val RESUME_WAKE = "uk.jarvis.mobile.RESUME_LOCAL_WAKE"
        private val mutable = MutableStateFlow(LiveVoiceState())
        val state = mutable.asStateFlow()
    }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val gson = Gson()
    private var socket: WebSocket? = null
    private var transport: OkHttpClient? = null
    private var capture: AudioRecord? = null
    private var playback: AudioTrack? = null
    private var echo: AcousticEchoCanceler? = null
    private lateinit var audio: AudioManager
    private var mediaSession: MediaSession? = null
    private var focus: AudioFocusRequest? = null
    private var audioPhase = CompanionAudioPhase.Idle
    private var wake: PowerManager.WakeLock? = null
    @Volatile private var active = false
    @Volatile private var wakeEnabled = false
    @Volatile private var wakeInitiatedWindow = false
    @Volatile private var wakeSpeechObserved = false
    @Volatile private var wakeSpeechDeadlineElapsed: Long? = null
    @Volatile private var ready = false
    private var wakeDetector: LocalWakeDetector? = null
    private var pendingWakeTicket: VoiceTicket? = null
    private var generation = 0L
    private var sessionId = ""
    private var deviceId = ""
    private var client: CoreApi? = null
    private val leaseDeadline = AtomicLong(0)
    private val pings = ConcurrentHashMap<Long, Long>()
    private var sequence = 0L
    private var renewWanted = false
    private var playbackItem: String? = null
    private var itemStartSamples = 0L
    private var itemSamples = 0L
    private var writtenSamples = 0L
    private var soundQueue = Channel<Pair<String, ByteArray>>(64)
    private var playbackBudget = VoicePlaybackBudget()
    private val reconnectPolicy = VoiceReconnectPolicy()
    private var reconnectJob: Job? = null
    private var captureJob: Job? = null
    private var initializationJob: Job? = null
    private var savedConnection: SavedConnection? = null
    @Volatile private var currentLease: AudioLease? = null
    private val routeChanged = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) { stopVoice("Audio route changed. Reconnect when your headphones are ready.") }
    }
    override fun onBind(intent: Intent?) = null
    override fun onCreate() {
        super.onCreate(); audio = getSystemService(AudioManager::class.java)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(routeChanged, IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY), RECEIVER_NOT_EXPORTED)
        else registerReceiver(routeChanged, IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY))
        getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel("live-voice", "Active live voice", NotificationManager.IMPORTANCE_LOW))
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val wakeWasEnabled = getSharedPreferences("installation", 0).getBoolean("local-wake-enabled", false)
        when (WakeRestartPolicy.action(intent != null, wakeWasEnabled)) {
            WakeRestartAction.OfferResume -> {
                showTapToResumeNotification()
                stopSelf(startId)
                return START_NOT_STICKY
            }
            WakeRestartAction.Stop -> { stopSelf(startId); return START_NOT_STICKY }
            WakeRestartAction.HandleCommand -> Unit
        }
        when (intent?.action) {
            STOP -> stopVoice("Live voice and local wake detection stopped.", disableWake = true)
            INTERRUPT -> interrupt()
            WAKE_START -> startWakeListening()
            START -> if (!active) {
                sessionId = intent.getStringExtra("sessionId").orEmpty()
                wakeDetector?.stop(); wakeDetector = null
                mutable.value = mutable.value.copy(wakeListening = false)
                val wakeTicket = intent.getStringExtra("voiceTicket")
                pendingWakeTicket = wakeTicket?.let { runCatching { gson.fromJson(it, VoiceTicket::class.java) }.getOrNull() }
                wakeInitiatedWindow = wakeTicket != null; wakeSpeechObserved = false; wakeSpeechDeadlineElapsed = null
                if (sessionId.isBlank() || checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) { stopVoice("Microphone permission and an active session are required."); return START_NOT_STICKY }
                if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) { stopVoice("Allow notifications for visible live-voice Stop controls."); return START_NOT_STICKY }
                val stop = PendingIntent.getService(this, 201, Intent(this, LiveVoiceService::class.java).setAction(STOP), PendingIntent.FLAG_IMMUTABLE)
                val interruptIntent = PendingIntent.getService(this, 202, Intent(this, LiveVoiceService::class.java).setAction(INTERRUPT), PendingIntent.FLAG_IMMUTABLE)
                mediaSession = MediaSession(this, "JARVIS live voice").apply {
                    setCallback(object : MediaSession.Callback() {
                        override fun onPause() { stopVoice("Paused with headset controls.", disableWake = true) }
                        override fun onStop() { stopVoice("Stopped with headset controls.", disableWake = true) }
                        override fun onSkipToNext() { interrupt() }
                        override fun onMediaButtonEvent(intent: Intent): Boolean {
                            val event = intent.mediaKey() ?: return super.onMediaButtonEvent(intent)
                            if (!HeadsetControls.handles(event.keyCode)) return super.onMediaButtonEvent(intent)
                            when (HeadsetControls.command(event.keyCode, event.action == KeyEvent.ACTION_DOWN, event.repeatCount, active)) {
                                HeadsetCommand.StopLive -> onStop()
                                HeadsetCommand.InterruptLive -> onSkipToNext()
                                else -> Unit
                            }
                            return true
                        }
                    })
                    setPlaybackState(PlaybackState.Builder().setActions(PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_STOP or PlaybackState.ACTION_PLAY_PAUSE or PlaybackState.ACTION_SKIP_TO_NEXT).setState(PlaybackState.STATE_PLAYING, 0, 1f).build())
                    isActive = true
                }
                val notification = Notification.Builder(this, "live-voice").setSmallIcon(android.R.drawable.ic_btn_speak_now)
                    .setContentTitle("JARVIS live voice is active").setContentText("Microphone streams only after Core confirms the audio lease.").setOngoing(true)
                    .addAction(Notification.Action.Builder(null, "Interrupt", interruptIntent).build()).addAction(Notification.Action.Builder(null, "Stop", stop).build())
                    .setStyle(Notification.MediaStyle().setMediaSession(mediaSession!!.sessionToken)).build()
                try { startForeground(43, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE) }
                catch (_: RuntimeException) { stopVoice("Android could not start the microphone foreground service."); return START_NOT_STICKY }
                soundQueue = Channel(64); playbackBudget = VoicePlaybackBudget(); reconnectPolicy.connected()
                active = true; mutable.value = LiveVoiceState(active = true, message = "Connecting securely to Core…"); generation++
                wake = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "jarvis:live-voice").apply { setReferenceCounted(false); acquire(20000) }
                val current = generation
                initializationJob = scope.launch { try { initialize(current) } catch (e: Exception) { if (active && current == generation) stopVoice(e.message ?: "Live voice failed.") } }
            }
        }
        return if (wakeEnabled) START_STICKY else START_NOT_STICKY
    }
    private fun showTapToResumeNotification() {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            mutable.value = LiveVoiceState(message = "Wake paused after Android restarted JARVIS. Open the app and allow notifications to resume.")
            return
        }
        val resume = PendingIntent.getActivity(this, 203,
            Intent(this, MainActivity::class.java).setAction(RESUME_WAKE)
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val notification = Notification.Builder(this, "live-voice")
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("JARVIS wake listening paused")
            .setContentText("Tap to resume wake")
            .setContentIntent(resume)
            .setAutoCancel(true)
            .build()
        getSystemService(NotificationManager::class.java).notify(44, notification)
        mutable.value = LiveVoiceState(message = "Wake paused after Android restarted JARVIS. Tap the notification to resume.")
    }
    private fun startWakeListening() {
        if (active || wakeEnabled) return
        if (Build.VERSION.SDK_INT < 31 || !android.speech.SpeechRecognizer.isOnDeviceRecognitionAvailable(this)) {
            mutable.value = LiveVoiceState(message = "This device has no supported local wake recognizer. No cloud fallback is available."); stopSelf(); return
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED ||
            Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            mutable.value = LiveVoiceState(message = "Microphone and notification permissions are required for local wake listening."); stopSelf(); return
        }
        wakeEnabled = true; getSharedPreferences("installation", 0).edit().putBoolean("local-wake-enabled", true).apply()
        val stop = PendingIntent.getService(this, 201, Intent(this, LiveVoiceService::class.java).setAction(STOP), PendingIntent.FLAG_IMMUTABLE)
        val notification = Notification.Builder(this, "live-voice").setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("JARVIS local wake detection is active")
            .setContentText("On-device listening only. Saying Hey JARVIS connects to Core and starts live voice.").setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop listening", stop).build()).build()
        try { startForeground(43, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE) }
        catch (_: RuntimeException) { stopVoice("Android could not start the microphone foreground service.", disableWake = true); return }
        startWakeDetector()
    }
    private fun startWakeDetector() {
        if (!wakeEnabled || active) return
        wakeDetector?.stop()
        transitionAudioPhase(CompanionAudioPhase.WakeListening)
        wakeDetector = LocalWakeDetector(this, { onWakeDetected() }, { message -> stopVoice(message, disableWake = true) }).also { it.start() }
        mutable.value = LiveVoiceState(message = "LOCAL WAKE LISTENING · on-device recognition", wakeListening = true)
    }
    private fun onWakeDetected() {
        if (!wakeEnabled || active) return
        wakeDetector = null; mutable.value = LiveVoiceState(message = "Hey JARVIS detected locally. Asking Core to arbitrate this device…")
        scope.launch {
            try {
                val saved = CredentialVault(this@LiveVoiceService).read() ?: error("Reconnect this phone to Core before using local wake.")
                val candidateClient = CoreApi(saved.url, saved.token, saved.certificateSha256)
                val privacy = candidateClient.get("/api/privacy/status", PrivacyStatus::class.java); candidateClient.bindEpoch(privacy.epoch)
                deviceId = getSharedPreferences("installation", 0).getString("id", "").orEmpty()
                candidateClient.send("/api/devices/$deviceId/heartbeat", emptyMap<String, String>(), JsonObject::class.java)
                val candidate = candidateClient.send("/api/voice/wake-candidates", WakeCandidateRequest(java.util.UUID.randomUUID().toString(), deviceId, true), WakeCandidateResponse::class.java)
                candidateClient.invalidate()
                sessionId = candidate.sessionId.toString(); pendingWakeTicket = candidate.voice
                startService(Intent(this@LiveVoiceService, LiveVoiceService::class.java).setAction(START)
                    .putExtra("sessionId", sessionId).putExtra("voiceTicket", gson.toJson(candidate.voice)))
            } catch (error: Exception) {
                if (!wakeEnabled) return@launch
                val message = when (error) {
                    is CoreHttpException -> if (error.status == 409) "Another device won wake arbitration. Still listening locally." else "Core rejected the wake request: ${error.message}"
                    is java.io.IOException -> "Core is unreachable. Local wake remains on; reconnect before trying again."
                    else -> error.message ?: "Wake arbitration failed. Local wake remains on."
                }
                mutable.value = LiveVoiceState(message = message, wakeListening = true)
                delay(1500); if (wakeEnabled && !active) startWakeDetector()
            }
        }
    }
    private suspend fun initialize(current: Long) {
        val connection = CredentialVault(this).read() ?: error("Connect this phone to Core first.")
        savedConnection = connection
        client = CoreApi(connection.url, connection.token, connection.certificateSha256)
        val privacy = client!!.get("/api/privacy/status", PrivacyStatus::class.java)
        if (!active || generation != current) return
        client!!.bindEpoch(privacy.epoch)
        deviceId = getSharedPreferences("installation", 0).getString("id", "").orEmpty()
        val builder = OkHttpClient.Builder().readTimeout(0, TimeUnit.MILLISECONDS).pingInterval(10, TimeUnit.SECONDS).connectTimeout(10, TimeUnit.SECONDS)
        connection.certificateSha256?.let { builder.sslSocketFactory(CertificatePin.sockets(it), CertificatePin.trustManager(it)) }
        transport = builder.build()
        focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT).setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
            .setOnAudioFocusChangeListener { change -> if (change < 0) stopVoice("Audio focus was lost; live voice stopped.") }.build()
        check(audio.requestAudioFocus(focus!!) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED) { "Another app currently owns audio focus." }
        transitionAudioPhase(CompanionAudioPhase.LiveVoice)
        if (Build.VERSION.SDK_INT >= 31) {
            val headset = audio.availableCommunicationDevices.firstOrNull { it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO || it.type == AudioDeviceInfo.TYPE_BLE_HEADSET }
            headset?.let { check(audio.setCommunicationDevice(it)) { "Android could not select the communication headset." } }
        } else if (audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS).any {
            it.type == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP || it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO
        }) {
            selectLegacyHeadset()
        }
        if (!active || generation != current) return
        val minimum = AudioTrack.getMinBufferSize(24000, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
        playback = AudioTrack.Builder().setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
            .setAudioFormat(AudioFormat.Builder().setSampleRate(24000).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(maxOf(minimum, 9600)).setTransferMode(AudioTrack.MODE_STREAM).build().also { it.play() }
        connectRecovering(connection, current)
        scope.launch {
            while (active && current == generation) {
                delay(4000)
                if (!active || current != generation) break
                if (!ready) continue
                val id = ++sequence; pings[id] = SystemClock.elapsedRealtime(); socket?.send(gson.toJson(mapOf("type" to "heartbeat", "sequence" to id)))
                if (pings.size > 8) { reconnect(current); continue }
                try { client?.send("/api/devices/$deviceId/heartbeat", emptyMap<String, String>(), JsonObject::class.java) }
                catch (e: CoreHttpException) { stopVoice(e.message ?: "Core rejected device access.") }
                catch (_: IOException) { reconnect(current) }
                catch (e: Exception) { if (active && current == generation) stopVoice(e.message ?: "Core privacy access changed.") }
            }
        }
        scope.launch { while (active && current == generation) { delay(250); if (active && current == generation && ready && SystemClock.elapsedRealtime() >= leaseDeadline.get()) reconnect(current) } }
        val queue = soundQueue; val queueBudget = playbackBudget
        scope.launch {
            for ((item, bytes) in queue) {
                queueBudget.release(bytes.size)
                if (!ready || !active || generation != current || SystemClock.elapsedRealtime() >= leaseDeadline.get()) continue
                val track = playback ?: continue
                if (playbackItem != item) { playbackItem = item; itemStartSamples = writtenSamples; itemSamples = 0 }
                itemSamples += bytes.size / 2
                val written = track.write(bytes, 0, bytes.size, AudioTrack.WRITE_BLOCKING)
                if (written < 0) { stopVoice("Audio playback failed."); break }; writtenSamples += written / 2
            }
        }
    }
    private suspend fun connectWindow(connection: SavedConnection, current: Long) {
        stopCapture()
        val at = SystemClock.elapsedRealtime()
        val ticket = pendingWakeTicket?.also { pendingWakeTicket = null }
            ?: client!!.send("/api/voice/tickets", VoiceStartRequest(sessionId, deviceId, true), VoiceTicket::class.java)
        if (!active || generation != current) return
        currentLease = ticket.lease
        leaseDeadline.set(at + 15000); pings.clear(); ready = false
        val established = CompletableDeferred<Unit>()
        val queue = soundQueue; val queueBudget = playbackBudget
        val url = connection.url.replaceFirst("https://", "wss://") + "/voice-stream"
        socket = transport!!.newWebSocket(Request.Builder().url(url).build(), object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) { if (active && generation == current) webSocket.send(gson.toJson(mapOf("type" to "authenticate", "ticket" to ticket.ticket))) else webSocket.close(1000, "Cancelled") }
            override fun onMessage(webSocket: WebSocket, text: String) {
                if (!active || generation != current || socket !== webSocket) return
                try {
                    val message = JsonParser.parseString(text).asJsonObject
                    when (message.get("type").asString) {
                        "ready" -> { ready = true; reconnectPolicy.connected(); established.complete(Unit); mutable.value = mutable.value.copy(ready = true, message = "LIVE · microphone active · use Stop or headset Pause to finish."); captureJob = scope.launch { try { captureAudio(current, webSocket) } catch (e: Exception) { if (active && ready && socket === webSocket) stopVoice(e.message ?: "Microphone failed.") } } }
                        "lease" -> pings.remove(message.get("sequence").asLong)?.let { sent ->
                            leaseDeadline.set(sent + message.get("leaseSeconds").asLong * 1000)
                            wake?.let { if (it.isHeld) it.release(); it.acquire(20000) }
                        }
                        "interrupt" -> interrupt()
                        "audio" -> if (ready && SystemClock.elapsedRealtime() < leaseDeadline.get()) {
                            val delta = message.get("delta").asString; check(delta.length <= 1280000)
                            val bytes = Base64.decode(delta, Base64.DEFAULT)
                            if (!queueBudget.reserve(bytes.size)) stopVoice("Playback exceeded the twenty-second audio buffer. Use shorter turns.")
                            else if (!queue.trySend(Pair(message.get("itemId").asString, bytes)).isSuccess) { queueBudget.release(bytes.size); stopVoice("Playback queue is full.") }
                        }
                        "transcript" -> Unit // Canonical transcripts stay on Core, not in a phone feature cache.
                        "error" -> stopVoice(message.get("detail").asString)
                        "window_limit" -> { renewWanted = true; ready = false; runCatching { capture?.stop() }; interrupt(false); webSocket.send("{\"type\":\"stop\"}") }
                        "ended" -> {
                            ready = false; interrupt(false); socket = null; webSocket.close(1000, "Window ended")
                            if (active && (message.get("reason").asString == "window_complete" || renewWanted)) {
                                renewWanted = false; mutable.value = mutable.value.copy(ready = false, message = "Renewing budgeted voice window…")
                                reconnect(current, immediate = true)
                            } else stopVoice("Live voice stopped. Completed turns are saved in Core.")
                        }
                    }
                } catch (_: Exception) { if (active && generation == current && socket === webSocket) stopVoice("Invalid live-voice event. Audio stopped.") }
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                val wasEstablished = established.isCompleted
                established.completeExceptionally(IOException("Live connection failed", t))
                if (wasEstablished && socket === webSocket && active) reconnect(current)
            }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                val wasEstablished = established.isCompleted
                established.completeExceptionally(IOException("Live connection closed"))
                if (wasEstablished && socket === webSocket && active) {
                    if (code == 1000 || code == 1008) stopVoice("Core ended voice ownership. Start again when ready.") else reconnect(current)
                }
            }
        })
        try { withTimeout(12000) { established.await() } }
        catch (e: Exception) { socket?.cancel(); throw e }
    }
    private suspend fun stopCapture() {
        runCatching { capture?.stop() }
        captureJob?.cancelAndJoin(); captureJob = null
    }
    private suspend fun connectRecovering(connection: SavedConnection, current: Long, initialDelay: Boolean = false) {
        var wait = initialDelay
        while (active && generation == current) {
            if (wait) {
                val backoff = reconnectPolicy.nextDelayMillis() ?: error("Connection recovery exhausted. Start voice again when your network is ready.")
                mutable.value = mutable.value.copy(ready = false, message = "Reconnecting to Core in ${backoff / 1000}s. Microphone is off.")
                delay(backoff)
            }
            if (!active || generation != current) return
            try { connectWindow(connection, current); return }
            catch (e: CoreHttpException) { throw e } // Ownership, credentials, consent and budget failures require a fresh user action.
            catch (e: CancellationException) { if (e !is TimeoutCancellationException) throw e; wait = true }
            catch (_: IOException) { wait = true }
            // A lost ready frame can leave the previous ticket live on Core. Never overlap that lease.
            val drain = (leaseDeadline.get() - SystemClock.elapsedRealtime()).coerceIn(0, 15000)
            if (drain > 0 && active && generation == current) delay(drain)
        }
    }
    @Synchronized private fun reconnect(current: Long, immediate: Boolean = false) {
        if (!active || generation != current || reconnectJob?.isActive == true) return
        ready = false; mutable.value = mutable.value.copy(ready = false)
        runCatching { capture?.stop() }; interrupt(false)
        val old = socket; socket = null; old?.cancel(); pings.clear()
        val connection = savedConnection ?: return
        reconnectJob = scope.launch {
            try {
                // Normal ended frames acknowledge Core's release; unacknowledged disconnects must drain.
                val drain = if (immediate) 250L else (leaseDeadline.get() - SystemClock.elapsedRealtime()).coerceIn(0, 15000)
                if (drain > 0) delay(drain)
                connectRecovering(connection, current, initialDelay = !immediate)
            }
            catch (e: Exception) { if (active && generation == current) stopVoice(e.message ?: "Voice reconnection failed.") }
        }
    }
    @android.annotation.SuppressLint("MissingPermission")
    private fun captureAudio(current: Long, window: WebSocket) {
        val rate = if (AudioRecord.getMinBufferSize(24000, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT) > 0) 24000 else 48000
        val recorder = AudioRecord.Builder().setAudioSource(MediaRecorder.AudioSource.VOICE_COMMUNICATION)
            .setAudioFormat(AudioFormat.Builder().setSampleRate(rate).setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(maxOf(AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT), rate / 5)).build()
        check(recorder.state == AudioRecord.STATE_INITIALIZED) { "Android could not initialize the microphone." }; capture = recorder
        val localEcho = if (AcousticEchoCanceler.isAvailable()) AcousticEchoCanceler.create(recorder.audioSessionId)?.also { it.enabled = true } else null
        echo = localEcho
        try {
        if (!active || !ready || generation != current || socket !== window) return
        recorder.startRecording(); val samples = ShortArray(rate / 50)
        val speechOnset = WakeSpeechOnsetDetector()
        while (active && ready && generation == current && socket === window) {
            val count = recorder.read(samples, 0, samples.size, AudioRecord.READ_BLOCKING); if (count <= 0) break
            if (!ready || SystemClock.elapsedRealtime() >= leaseDeadline.get()) continue
            if (wakeInitiatedWindow && !wakeSpeechObserved) {
                val now = SystemClock.elapsedRealtime()
                val detected = speechOnset.observe(samples, count)
                if (detected) { wakeSpeechObserved = true; wakeSpeechDeadlineElapsed = null }
                else {
                    val deadline = wakeSpeechDeadlineElapsed ?: (now + 8_000).also { wakeSpeechDeadlineElapsed = it }
                    if (now >= deadline) {
                        stopVoice("No speech heard after wake. Local wake remains active.", wakeStopReason = "no_speech_timeout")
                        break
                    }
                }
            }
            val step = rate / 24000; val pcm = ByteArray(count / step * 2)
            for (i in 0 until count / step) { val value = if (step == 1) samples[i].toInt() else (samples[i * 2].toInt() + samples[i * 2 + 1].toInt()) / 2; pcm[i * 2] = value.toByte(); pcm[i * 2 + 1] = (value shr 8).toByte() }
            val ws = socket ?: continue
            if (ws !== window) break
            if (ws.queueSize() > 256000) { stopVoice("Network cannot keep up with live audio."); break }
            ws.send(gson.toJson(mapOf("type" to "audio", "audio" to Base64.encodeToString(pcm, Base64.NO_WRAP))))
        }
        if (active && ready && generation == current && socket === window) stopVoice("Microphone capture stopped. Reconnect to resume.")
        } finally {
            runCatching { recorder.stop() }; runCatching { recorder.release() }
            localEcho?.release()
            if (capture === recorder) { capture = null; echo = null }
        }
    }
    private fun interrupt(send: Boolean = true) {
        val track = playback
        if (send && ready && playbackItem != null && track != null) {
            val played = ((track.playbackHeadPosition.toLong() and 0xffffffffL) - itemStartSamples).coerceIn(0, itemSamples) / 24
            socket?.send(gson.toJson(mapOf("type" to "interrupt", "itemId" to playbackItem, "playedMs" to played)))
        }
        while (true) { val queued = soundQueue.tryReceive().getOrNull() ?: break; playbackBudget.release(queued.second.size) }
        if (track != null) runCatching { track.pause(); track.flush(); writtenSamples = 0; itemStartSamples = 0; itemSamples = 0; playbackItem = null; track.play() }
    }
    private fun stopVoice(message: String, disableWake: Boolean = false, wakeStopReason: String? = null) {
        if (disableWake) {
            wakeEnabled = false; getSharedPreferences("installation", 0).edit().putBoolean("local-wake-enabled", false).apply()
            wakeDetector?.stop(); wakeDetector = null
        }
        val keepWake = wakeEnabled && !disableWake
        val nextAudioPhase = if (keepWake) CompanionAudioPhase.WakeListening else CompanionAudioPhase.Idle
        if (!active && !keepWake && mutable.value.message == message && audioPhase == nextAudioPhase) return
        val stoppedLease = currentLease; currentLease = null; pendingWakeTicket = null; val stoppedClient = client; val stoppedCapture = captureJob
        active = false; ready = false; generation++
        val stopFrame = if (wakeStopReason == "no_speech_timeout") gson.toJson(mapOf("type" to "stop", "reason" to wakeStopReason)) else "{\"type\":\"stop\"}"
        socket?.send(stopFrame); socket?.close(1000, "Stopped"); socket = null
        reconnectJob?.cancel(); reconnectJob = null
        initializationJob?.cancel(); initializationJob = null
        runCatching { capture?.stop() }; captureJob?.cancel(); captureJob = null
        runCatching { playback?.stop() }; playback?.release(); playback = null
        if (stoppedLease != null && stoppedClient != null) {
            // Acknowledge only after capture has exited and playback has stopped. One bounded attempt, no replay.
            val acknowledgement = CoroutineScope(SupervisorJob() + Dispatchers.IO)
            acknowledgement.launch {
                try { withTimeout(3000) { stoppedCapture?.join(); stoppedClient.send("/api/voice/local/release", stoppedLease, Boolean::class.javaObjectType) } }
                catch (_: Exception) { /* Core retains the original drain expiry if acknowledgement is lost. */ }
                finally { acknowledgement.cancel() }
            }
        }
        soundQueue.close(); focus?.let { audio.abandonAudioFocusRequest(it) }; focus = null
        wakeInitiatedWindow = false; wakeSpeechObserved = false; wakeSpeechDeadlineElapsed = null
        transitionAudioPhase(nextAudioPhase)
        mediaSession?.release(); mediaSession = null; transport?.dispatcher?.executorService?.shutdown(); transport?.connectionPool?.evictAll(); transport = null
        wake?.let { if (it.isHeld) it.release() }; wake = null
        mutable.value = LiveVoiceState(message = message, wakeListening = keepWake)
        if (keepWake) {
            val stop = PendingIntent.getService(this, 201, Intent(this, LiveVoiceService::class.java).setAction(STOP), PendingIntent.FLAG_IMMUTABLE)
            val notification = Notification.Builder(this, "live-voice").setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setContentTitle("JARVIS local wake detection is active")
                .setContentText("On-device listening only. Saying Hey JARVIS connects to Core and starts live voice.").setOngoing(true)
                .addAction(Notification.Action.Builder(null, "Stop listening", stop).build()).build()
            startForeground(43, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
            startWakeDetector()
        } else { stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() }
    }
    override fun onDestroy() {
        if (active) stopVoice("Android stopped the live session. Reconnect to resume.", disableWake = true)
        wakeEnabled = false; wakeDetector?.stop(); wakeDetector = null
        transitionAudioPhase(CompanionAudioPhase.Idle)
        scope.cancel(); unregisterReceiver(routeChanged); super.onDestroy()
    }
    private fun transitionAudioPhase(next: CompanionAudioPhase) {
        for (action in AudioRoutePolicy.transition(audioPhase, next)) when (action) {
            AudioRouteAction.EnterCommunicationRoute -> audio.mode = AudioManager.MODE_IN_COMMUNICATION
            AudioRouteAction.RestoreDefaultRoute -> {
                if (Build.VERSION.SDK_INT >= 31) audio.clearCommunicationDevice()
                else { @Suppress("DEPRECATION") runCatching { audio.isBluetoothScoOn = false; audio.stopBluetoothSco() } }
                audio.mode = AudioManager.MODE_NORMAL
            }
        }
        audioPhase = next
    }
    @Suppress("DEPRECATION")
    private suspend fun selectLegacyHeadset() {
        val connected = CompletableDeferred<Unit>()
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                if (intent?.getIntExtra(AudioManager.EXTRA_SCO_AUDIO_STATE, -1) == AudioManager.SCO_AUDIO_STATE_CONNECTED)
                    connected.complete(Unit)
            }
        }
        registerReceiver(receiver, IntentFilter(AudioManager.ACTION_SCO_AUDIO_STATE_UPDATED))
        try {
            audio.startBluetoothSco()
            withTimeout(8000) { connected.await() }
            audio.isBluetoothScoOn = true
        } finally { unregisterReceiver(receiver) }
    }
}
