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
import com.google.gson.Gson
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import okhttp3.WebSocket
import uk.jarvis.mobile.contracts.VoiceTicket

data class LiveVoiceState(val active: Boolean = false, val ready: Boolean = false, val message: String = "", val wakeListening: Boolean = false)

class LiveVoiceService : Service() {
    companion object {
        const val START = "uk.jarvis.mobile.LIVE_START"
        const val STOP = "uk.jarvis.mobile.LIVE_STOP"
        const val INTERRUPT = "uk.jarvis.mobile.LIVE_INTERRUPT"
        const val WAKE_START = "uk.jarvis.mobile.LOCAL_WAKE_START"
        private const val WAKE_GENERATION = "wakeGeneration"
        const val RESUME_WAKE = "uk.jarvis.mobile.RESUME_LOCAL_WAKE"
        private val mutable = MutableStateFlow(LiveVoiceState())
        val state = mutable.asStateFlow()
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val gson = Gson()
    private val lifecycle = VoiceLifecycle()
    private lateinit var routes: AudioRouteController
    private lateinit var wakeController: WakeController
    private var capture: AudioRecord? = null
    private var playback: AudioTrack? = null
    private var mediaSession: MediaSession? = null
    private var wakeLock: PowerManager.WakeLock? = null
    @Volatile private var wakeInitiatedWindow = false
    @Volatile private var wakeSpeechObserved = false
    @Volatile private var wakeSpeechDeadlineElapsed: Long? = null
    private var playbackItem: String? = null
    private var itemStartSamples = 0L
    private var itemSamples = 0L
    private var writtenSamples = 0L
    private var soundQueue = Channel<Pair<String, ByteArray>>(64)
    private var playbackBudget = VoicePlaybackBudget()
    private var captureJob: Job? = null
    private var initializationJob: Job? = null

    private val transport = VoiceTransport(scope, lifecycle, object : VoiceTransport.Listener {
        override suspend fun stopCapture(current: Long) = stopCaptureAndJoin(current)
        override fun ready(current: Long, window: WebSocket) = startCapture(current, window)
        override fun renewed() { wakeLock?.let { if (it.isHeld) it.release(); it.acquire(20000) } }
        override fun audio(current: Long, window: WebSocket, itemId: String, bytes: ByteArray) =
            queuePlayback(current, window, itemId, bytes)
        override fun interrupt(send: Boolean) = this@LiveVoiceService.interrupt(send)
        override fun pauseWindow() { runCatching { capture?.stop() }; this@LiveVoiceService.interrupt(false) }
        override fun reconnecting(message: String?) {
            mutable.value = mutable.value.copy(ready = false, message = message ?: mutable.value.message)
        }
        override fun stopped(message: String) = stopVoice(message)
    })

    private val routeChanged = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) { stopVoice("Audio route changed. Reconnect when your headphones are ready.") }
    }
    override fun onBind(intent: Intent?) = null
    override fun onCreate() {
        super.onCreate()
        routes = AudioRouteController(this) { stopVoice("Audio focus was lost; live voice stopped.") }
        wakeController = WakeController(this, scope, lifecycle,
            onListening = {
                routes.transition(CompanionAudioPhase.WakeListening)
                mutable.value = LiveVoiceState(message = "LOCAL WAKE LISTENING · on-device recognition", wakeListening = true)
            },
            onArbitrating = { mutable.value = LiveVoiceState(message = "Hey JARVIS detected locally. Asking Core to arbitrate this device…") },
            onTicket = { candidate, generation ->
                startService(Intent(this, LiveVoiceService::class.java).setAction(START)
                    .putExtra("sessionId", candidate.sessionId.toString()).putExtra("voiceTicket", gson.toJson(candidate.voice))
                    .putExtra(WAKE_GENERATION, generation))
            },
            onRetry = { mutable.value = LiveVoiceState(message = it, wakeListening = true) },
            onUnavailable = { stopVoice(it, disableWake = true) })
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(routeChanged, IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY), RECEIVER_NOT_EXPORTED)
        else registerReceiver(routeChanged, IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY))
        getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel("live-voice", "Active live voice", NotificationManager.IMPORTANCE_LOW))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (WakeRestartPolicy.action(intent != null, wakeController.wasEnabled)) {
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
            START -> if (lifecycle.mayStartLive) {
                if (intent.hasExtra(WAKE_GENERATION) && !lifecycle.isArbitrating(intent.getLongExtra(WAKE_GENERATION, -1)))
                    return if (wakeController.enabled) START_STICKY else START_NOT_STICKY
                val sessionId = intent.getStringExtra("sessionId").orEmpty()
                wakeController.suspendListening()
                mutable.value = mutable.value.copy(wakeListening = false)
                val wakeTicket = intent.getStringExtra("voiceTicket")
                val ticket = wakeTicket?.let { runCatching { gson.fromJson(it, VoiceTicket::class.java) }.getOrNull() }
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
                            when (HeadsetControls.command(event.keyCode, event.action == KeyEvent.ACTION_DOWN, event.repeatCount, lifecycle.active)) {
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
                val current = lifecycle.beginLive() ?: return START_NOT_STICKY
                soundQueue = Channel(64)
                playbackBudget = VoicePlaybackBudget()
                transport.start(sessionId, ticket)
                mutable.value = LiveVoiceState(active = true, message = "Connecting securely to Core…")
                wakeLock = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "jarvis:live-voice")
                    .apply { setReferenceCounted(false); acquire(20000) }
                initializationJob = scope.launch {
                    try { initialize(current) }
                    catch (error: Exception) { if (lifecycle.isLive(current)) stopVoice(error.message ?: "Live voice failed.") }
                }
            }
        }
        return if (wakeController.enabled) START_STICKY else START_NOT_STICKY
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
        if (lifecycle.active || wakeController.enabled) return
        if (Build.VERSION.SDK_INT < 31 || !android.speech.SpeechRecognizer.isOnDeviceRecognitionAvailable(this)) {
            mutable.value = LiveVoiceState(message = "This device has no supported local wake recognizer. No cloud fallback is available."); stopSelf(); return
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED ||
            Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            mutable.value = LiveVoiceState(message = "Microphone and notification permissions are required for local wake listening."); stopSelf(); return
        }
        wakeController.enable()
        val stop = PendingIntent.getService(this, 201, Intent(this, LiveVoiceService::class.java).setAction(STOP), PendingIntent.FLAG_IMMUTABLE)
        val notification = Notification.Builder(this, "live-voice").setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("JARVIS local wake detection is active")
            .setContentText("On-device listening only. Saying Hey JARVIS connects to Core and starts live voice.").setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop listening", stop).build()).build()
        try { startForeground(43, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE) }
        catch (_: RuntimeException) { stopVoice("Android could not start the microphone foreground service.", disableWake = true); return }
        wakeController.listen()
    }

    private suspend fun initialize(current: Long) {
        val connection = CredentialVault(this).read() ?: error("Connect this phone to Core first.")
        val deviceId = getSharedPreferences("installation", 0).getString("id", "").orEmpty()
        if (!transport.prepare(connection, deviceId, current)) return
        routes.enterLive { lifecycle.isLive(current) }
        if (!lifecycle.isLive(current)) return
        val minimum = AudioTrack.getMinBufferSize(24000, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
        playback = AudioTrack.Builder().setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
            .setAudioFormat(AudioFormat.Builder().setSampleRate(24000).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(maxOf(minimum, 9600)).setTransferMode(AudioTrack.MODE_STREAM).build().also { it.play() }
        transport.connect(current)
        val queue = soundQueue; val queueBudget = playbackBudget
        scope.launch {
            for ((item, bytes) in queue) {
                queueBudget.release(bytes.size)
                if (!lifecycle.ready || !lifecycle.isLive(current) || !transport.leaseIsCurrent()) continue
                val track = playback ?: continue
                if (playbackItem != item) { playbackItem = item; itemStartSamples = writtenSamples; itemSamples = 0 }
                itemSamples += bytes.size / 2
                val written = track.write(bytes, 0, bytes.size, AudioTrack.WRITE_BLOCKING)
                if (written < 0) { stopVoice("Audio playback failed."); break }; writtenSamples += written / 2
            }
        }
    }

    private fun startCapture(current: Long, window: WebSocket) {
        if (!transport.isWindow(current, window) || !lifecycle.ready) return
        mutable.value = mutable.value.copy(ready = true, message = "LIVE · microphone active · use Stop or headset Pause to finish.")
        captureJob = scope.launch {
            try { captureAudio(current, window) }
            catch (error: Exception) {
                if (lifecycle.ready && transport.isWindow(current, window)) stopVoice(error.message ?: "Microphone failed.")
            }
        }
    }

    private suspend fun stopCaptureAndJoin(current: Long) {
        if (!lifecycle.isLive(current)) return
        val stopped = captureJob
        runCatching { capture?.stop() }
        stopped?.cancelAndJoin()
        if (captureJob === stopped) captureJob = null
    }

    private fun queuePlayback(current: Long, window: WebSocket, item: String, bytes: ByteArray) {
        val queue = soundQueue
        val budget = playbackBudget
        if (!transport.isWindow(current, window) || !lifecycle.ready || !transport.leaseIsCurrent()) return
        if (!budget.reserve(bytes.size)) {
            if (transport.isWindow(current, window)) stopVoice("Playback exceeded the twenty-second audio buffer. Use shorter turns.")
        }
        else if (!queue.trySend(item to bytes).isSuccess) {
            budget.release(bytes.size)
            if (transport.isWindow(current, window)) stopVoice("Playback queue is full.")
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
        try {
        if (!lifecycle.ready || !transport.isWindow(current, window)) return
        recorder.startRecording(); val samples = ShortArray(rate / 50)
        val speechOnset = WakeSpeechOnsetDetector()
        while (lifecycle.ready && transport.isWindow(current, window)) {
            val count = recorder.read(samples, 0, samples.size, AudioRecord.READ_BLOCKING); if (count <= 0) break
            if (!lifecycle.ready || !transport.leaseIsCurrent()) continue
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
            transport.sendAudio(current, window, pcm)
        }
        if (lifecycle.ready && transport.isWindow(current, window)) stopVoice("Microphone capture stopped. Reconnect to resume.")
        } finally {
            runCatching { recorder.stop() }; runCatching { recorder.release() }
            localEcho?.release()
            if (capture === recorder) capture = null
        }
    }

    private fun interrupt(send: Boolean = true) {
        val track = playback
        val item = playbackItem
        if (send && lifecycle.ready && item != null && track != null) {
            val played = ((track.playbackHeadPosition.toLong() and 0xffffffffL) - itemStartSamples).coerceIn(0, itemSamples) / 24
            transport.interrupt(item, played)
        }
        while (true) { val queued = soundQueue.tryReceive().getOrNull() ?: break; playbackBudget.release(queued.second.size) }
        if (track != null) runCatching { track.pause(); track.flush(); writtenSamples = 0; itemStartSamples = 0; itemSamples = 0; playbackItem = null; track.play() }
    }

    private fun stopVoice(message: String, disableWake: Boolean = false, wakeStopReason: String? = null) {
        if (disableWake) wakeController.disable() else wakeController.suspendListening()
        val keepWake = wakeController.enabled && !disableWake
        val nextRoute = if (keepWake) CompanionAudioPhase.WakeListening else CompanionAudioPhase.Idle
        if (lifecycle.phase == VoicePhase.Idle && !keepWake && mutable.value.message == message && routes.phase == nextRoute) return
        if (!lifecycle.beginStop()) return
        val release = transport.stop(wakeStopReason)
        val stoppedCapture = captureJob
        initializationJob?.cancel(); initializationJob = null
        runCatching { capture?.stop() }; captureJob?.cancel(); captureJob = null
        runCatching { playback?.stop() }; playback?.release(); playback = null
        release?.afterCapture(stoppedCapture)
        soundQueue.close()
        wakeInitiatedWindow = false; wakeSpeechObserved = false; wakeSpeechDeadlineElapsed = null
        routes.leaveLive(nextRoute)
        mediaSession?.release()
        mediaSession = null
        wakeLock?.let { if (it.isHeld) it.release() }
        wakeLock = null
        mutable.value = LiveVoiceState(message = message, wakeListening = keepWake)
        lifecycle.finishStop(keepWake)
        if (keepWake) {
            val stop = PendingIntent.getService(this, 201, Intent(this, LiveVoiceService::class.java).setAction(STOP), PendingIntent.FLAG_IMMUTABLE)
            val notification = Notification.Builder(this, "live-voice").setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setContentTitle("JARVIS local wake detection is active")
                .setContentText("On-device listening only. Saying Hey JARVIS connects to Core and starts live voice.").setOngoing(true)
                .addAction(Notification.Action.Builder(null, "Stop listening", stop).build()).build()
            startForeground(43, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
            wakeController.listen()
        } else { stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() }
    }

    override fun onDestroy() {
        if (lifecycle.active) stopVoice("Android stopped the live session. Reconnect to resume.", disableWake = true)
        wakeController.disable(persist = false)
        routes.leaveLive(CompanionAudioPhase.Idle)
        scope.cancel()
        unregisterReceiver(routeChanged)
        super.onDestroy()
    }
}
