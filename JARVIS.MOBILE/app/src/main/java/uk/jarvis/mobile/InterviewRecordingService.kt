package uk.jarvis.mobile

import android.Manifest
import android.annotation.SuppressLint
import android.app.*
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.*
import android.os.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import uk.jarvis.mobile.contracts.*
import java.io.File
import java.time.OffsetDateTime

data class InterviewCaptureState(val starting: Boolean = false, val recording: Boolean = false, val uploading: Boolean = false, val message: String = "")

class InterviewRecordingService : Service() {
    companion object {
        const val START = "uk.jarvis.mobile.INTERVIEW_START"
        const val STOP = "uk.jarvis.mobile.INTERVIEW_STOP"
        const val RESUME = "uk.jarvis.mobile.INTERVIEW_RESUME"
        const val RECORDING_ID = "recordingId"
        const val AUTHORISED = "recordingAuthorised"
        const val STORAGE = "recordingStorageConsent"
        const val VISIBLE_START = "recordingVisibleStart"
        private const val CHANNEL = "interview-recording"
        private const val NOTIFICATION = 42
        private val mutable = MutableStateFlow(InterviewCaptureState())
        val state = mutable.asStateFlow()
    }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val control = RecordingCaptureControl()
    @Volatile private var stopRequested = false
    @Volatile private var damaged = false
    @Volatile private var authorizationBlocked = false
    @Volatile private var privacyErased = false
    @Volatile private var stoppingPublished = false
    @Volatile private var destroyed = false
    private var operation: Job? = null
    private var uploader: Job? = null
    private var microphone: AudioRecord? = null
    private var queue: RecordingQueue? = null
    private var transport: RecordingCoreTransport? = null
    private var captureId: String? = null
    private var focus: AudioFocusRequest? = null
    private var captureWakeLock: PowerManager.WakeLock? = null
    private val root get() = File(filesDir, "recordings")
    override fun onBind(intent: Intent?) = null
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val command = intent ?: run { stopSelf(startId); return START_NOT_STICKY }
        if (command.action == STOP) { stopRequested = true; if (operation?.isActive != true && uploader?.isActive != true) stopSelf(startId); return START_NOT_STICKY }
        if (command.action !in listOf(START, RESUME) || operation?.isActive == true || uploader?.isActive == true) return START_NOT_STICKY
        stopRequested = false; damaged = false; authorizationBlocked = false; privacyErased = false; stoppingPublished = false
        try {
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "Interview recording", NotificationManager.IMPORTANCE_LOW).apply { setSound(null, null); enableVibration(false) })
            check(manager.areNotificationsEnabled() && manager.getNotificationChannel(CHANNEL).importance != NotificationManager.IMPORTANCE_NONE) { "Allow recording notifications before starting." }
            if (command.action == START) {
                control.begin(command.getBooleanExtra(VISIBLE_START, false), unlocked(), command.getBooleanExtra(AUTHORISED, false), command.getBooleanExtra(STORAGE, false),
                    checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED,
                    Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED)
                mutable.value = InterviewCaptureState(starting = true, message = "Starting interview recording…")
                foreground(true, "Starting interview recording…")
                operation = scope.launch { startCapture(command.getStringExtra(RECORDING_ID) ?: error("Choose a prepared recording.")) }
            } else {
                check(command.getBooleanExtra(VISIBLE_START, false) && unlocked()) { "Open this phone to resume its protected upload." }
                stoppingPublished = true
                control.uploading(); mutable.value = InterviewCaptureState(uploading = true, message = "Resuming protected recording uploads…")
                foreground(false, "Uploading recorded interview")
                operation = scope.launch { resumeUploads() }
            }
        } catch (error: Exception) { mutable.value = InterviewCaptureState(message = error.message ?: "Recording could not start."); stopSelf() }
        return START_NOT_STICKY
    }
    private fun unlocked(): Boolean = getSystemService(KeyguardManager::class.java).let { !it.isDeviceLocked && !it.isKeyguardLocked }
    private fun callActive(): Boolean = getSystemService(AudioManager::class.java).mode in listOf(AudioManager.MODE_IN_CALL, AudioManager.MODE_IN_COMMUNICATION)
    private fun notification(capturing: Boolean, message: String): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.ic_btn_speak_now).setContentTitle(if (capturing) "JARVIS is recording" else "JARVIS recording upload")
            .setContentText(message).setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true).setVisibility(Notification.VISIBILITY_PUBLIC)
            .apply { if (capturing) addAction(Notification.Action.Builder(null, "Stop recording", PendingIntent.getService(this@InterviewRecordingService, 1,
                Intent(this@InterviewRecordingService, InterviewRecordingService::class.java).setAction(STOP), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)).build()) }.build()
    }
    private fun foreground(capturing: Boolean, message: String) {
        val type = if (!capturing) ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC else if (Build.VERSION.SDK_INT >= 30) ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE else 0
        startForeground(NOTIFICATION, notification(capturing, message), type)
    }
    @SuppressLint("MissingPermission") // Permission, visible owner start and unlocked state are checked before capture.
    private suspend fun startCapture(id: String) {
        var coreStarted = false
        try {
            check(unlocked() && !callActive()) { "Phone call audio cannot be recorded. Use a laptop for calls." }
            val core = RecordingCoreTransport.connect(this); transport = core
            val offers = core.send(RecordingCaptureRequest("poll", core.deviceId))
            check(offers.ready && offers.offers.any { it.id == id && it.status == "Prepared" && it.tracks == listOf("unknown") }) { "Prepare a new recording for this phone in the owner dashboard." }
            if (LiveVoiceService.state.value.active || LiveVoiceService.state.value.wakeListening) startService(Intent(this, LiveVoiceService::class.java).setAction(LiveVoiceService.STOP))
            withTimeout(5000) { while (LiveVoiceService.state.value.active || LiveVoiceService.state.value.wakeListening) delay(50) }
            check(!stopRequested && unlocked() && !callActive()) { "Recording start was cancelled. Unlock this phone to begin capture." }
            val started = core.send(RecordingCaptureRequest("start", core.deviceId, id, false, true, true))
            coreStarted = true; captureId = id
            val recording = started.recording ?: error("Recording start was not acknowledged.")
            check(recording.id == id && recording.tracks == listOf("unknown"))
            val startedAt = recording.startedAt?.let { OffsetDateTime.parse(it).toInstant().toEpochMilli() } ?: System.currentTimeMillis()
            val local = RecordingQueue.create(root, RecordingCipherKey.get(), RecordingQueueState(id, core.deviceId, core.epoch, core.binding, startedAt = startedAt)); queue = local
            check(!stopRequested && unlocked() && !callActive()) { "Recording start was cancelled, or this phone locked or entered a call." }
            val minimum = AudioRecord.getMinBufferSize(16000, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
            check(minimum > 0) { "This phone cannot provide the required microphone format." }
            val format = AudioFormat.Builder().setSampleRate(16000).setChannelMask(AudioFormat.CHANNEL_IN_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build()
            val recorder = AudioRecord.Builder().setAudioSource(MediaRecorder.AudioSource.MIC).setAudioFormat(format).setBufferSizeInBytes(maxOf(minimum * 4, 64000)).build(); microphone = recorder
            check(recorder.state == AudioRecord.STATE_INITIALIZED)
            val audio = getSystemService(AudioManager::class.java)
            focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_EXCLUSIVE).setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION).build())
                .setOnAudioFocusChangeListener({ change -> if (change < 0) { damaged = true; stopRequested = true } }, Handler(Looper.getMainLooper())).build()
            check(audio.requestAudioFocus(focus!!) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED) { "Microphone audio focus is unavailable." }
            captureWakeLock = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Jarvis:InterviewCapture").apply { acquire(10_810_000L) }
            check(!stopRequested) { "Recording start was cancelled." }
            recorder.startRecording(); check(recorder.recordingState == AudioRecord.RECORDSTATE_RECORDING)
            control.captured(); mutable.value = InterviewCaptureState(recording = true, uploading = true, message = "JARVIS is recording")
            foreground(true, "Face-to-face interview. Tap Stop to finish.")
            uploader = scope.launch { upload(local, core); if (!control.microphoneActive) finishService() }
            val buffer = ByteArray(1_920_000); val input = ByteArray(3200); var used = 0; val beginning = SystemClock.elapsedRealtime(); var samplesRead = 0L
            val maximumMillis = (startedAt + 10_800_000 - System.currentTimeMillis()).coerceIn(0, 10_800_000)
            try {
                while (!stopRequested && SystemClock.elapsedRealtime() - beginning < maximumMillis && local.state.counts.getValue("unknown") < 180) {
                    if (callActive()) { damaged = true; break }
                    val read = recorder.read(input, 0, input.size, AudioRecord.READ_BLOCKING)
                    check(read > 0 && read % 2 == 0) { "Microphone capture was interrupted." }
                    samplesRead += read / 2
                    // A stalled reader cannot silently turn dropped audio into a complete interview.
                    check(kotlin.math.abs(samplesRead - (SystemClock.elapsedRealtime() - beginning) * 16) <= 64000) { "Microphone timing was interrupted." }
                    var offset = 0
                    while (offset < read) {
                        val count = minOf(buffer.size - used, read - offset); input.copyInto(buffer, used, offset, offset + count); used += count; offset += count
                        if (used == buffer.size) { val wav = RecordingWav.wrap(buffer, used); try { local.add(wav) } finally { wav.fill(0); buffer.fill(0) }; used = 0 }
                    }
                }
            } catch (error: Exception) { damaged = true; if (error is CancellationException) throw error }
            finally {
                runCatching { recorder.stop() }; recorder.release(); microphone = null
                try { if (used > 0 && !privacyErased) { val wav = RecordingWav.wrap(buffer, used); try { local.add(wav) } finally { wav.fill(0) } } }
                finally { buffer.fill(0); input.fill(0); focus?.let { audio.abandonAudioFocusRequest(it) }; focus = null; releaseCaptureWakeLock() }
            }
            if (privacyErased) { control.finished(); finishService(); return }
            local.stop(damaged || destroyed)
            if (destroyed) return
            control.stopped(); mutable.value = InterviewCaptureState(uploading = true, message = if (damaged) "Recording stopped with missing audio. Protected upload pending." else "Recording stopped. Finishing upload…")
            foreground(false, "Uploading recorded interview")
            stoppingPublished = true
            if (authorizationBlocked) { control.networkLost(); stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() }
            else if (uploader?.isActive != true) uploader = scope.launch { upload(local, core); finishService() }
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            damaged = true; stopRequested = true
            runCatching { microphone?.stop() }; runCatching { microphone?.release() }; microphone = null; releaseCaptureWakeLock()
            focus?.let { getSystemService(AudioManager::class.java).abandonAudioFocusRequest(it) }; focus = null
            if (error is CorePrivacyException && error.eraseLegacy) { privacyErased = true; authorizationBlocked = true; eraseCurrentOriginQueues() }
            else if (!privacyErased) queue?.let { runCatching { it.stop(true) } }
            stoppingPublished = true; control.stopped()
            if (coreStarted && !privacyErased) runCatching { transport?.send(RecordingCaptureRequest("finish", transport!!.deviceId, id, finalCounts = queue?.state?.counts?.toMap() ?: mapOf("unknown" to 0), discontinuous = true)) }
            mutable.value = InterviewCaptureState(message = if (privacyErased) "Core data changed. Protected recording copies removed." else error.message ?: "Recording could not start. Protected audio was retained.")
            stopForeground(STOP_FOREGROUND_REMOVE); stopSelf()
        }
    }
    private suspend fun upload(local: RecordingQueue, core: RecordingCoreTransport) {
        var failures = 0
        while (scope.isActive && !authorizationBlocked) {
            try {
                if (local.state.stopped && !stoppingPublished) { delay(50); continue }
                if (local.state.stopped) core.send(RecordingCaptureRequest("finish", core.deviceId, local.state.recordingId, finalCounts = local.state.counts.toMap(), discontinuous = local.state.discontinuous))
                for (chunk in local.pending()) {
                    val response = core.send(RecordingCaptureRequest("chunk", core.deviceId, chunk.recordingId, track = "unknown", sequence = chunk.sequence, sha256 = chunk.sha256, audioBase64 = chunk.audioBase64, discontinuous = local.state.discontinuous))
                    check(response.recording?.id == chunk.recordingId && response.acknowledged?.any { it.track == "unknown" && it.sequence == chunk.sequence && it.sha256 == chunk.sha256 } == true) { "Upload was not acknowledged. Protected audio was retained." }
                    local.acknowledge(chunk.sequence, chunk.sha256)
                }
                if (local.state.stopped && local.pending(1).isEmpty()) {
                    val ended = core.send(RecordingCaptureRequest("finish", core.deviceId, local.state.recordingId, finalCounts = local.state.counts.toMap(), discontinuous = local.state.discontinuous))
                    check(ended.recording?.id == local.state.recordingId && ended.recording?.status in listOf("Complete", "Incomplete"))
                    local.clear(); queue = null; control.finished(); mutable.value = InterviewCaptureState(message = "Recording stored. Temporary audio removed.")
                    return
                }
                failures = 0; delay(2000)
            } catch (error: Exception) {
                if (error is CancellationException) throw error
                if (error is CorePrivacyException && error.eraseLegacy) {
                    privacyErased = true; authorizationBlocked = true; stopRequested = true; damaged = true
                    // Serialize deletion with queue writes. Capture checks privacyErased again before its tail/manifest.
                    queue?.clear(); eraseCurrentOriginQueues()
                    mutable.value = InterviewCaptureState(recording = control.microphoneActive, message = "Core data changed. Protected recording copies removed.")
                    return
                }
                if (error is CoreHttpException && error.status in 400..499 && error.status != 408) {
                    authorizationBlocked = true; stopRequested = true
                    mutable.value = InterviewCaptureState(recording = control.microphoneActive, message = "Core refused this upload. Capture stopped; protected audio retained. Review the recording on Core before resuming.")
                    return
                }
                failures++; if (!control.microphoneActive) { control.networkLost(); mutable.value = InterviewCaptureState(uploading = true, message = "Upload pending. Protected audio is on this phone.") }
                delay(minOf(30_000L, 2000L shl minOf(failures - 1, 4)))
            }
        }
    }
    private suspend fun resumeUploads() {
        try {
            val core = RecordingCoreTransport.connect(this); transport = core
            for (local in RecordingQueue.load(root, RecordingCipherKey.get())) {
                if (local.state.deviceId != core.deviceId) continue
                if (local.state.epoch != core.epoch && local.state.binding == RecordingQueue.binding(core.connection.url, core.deviceId, local.state.epoch)) { local.clear(); continue }
                if (local.state.binding != core.binding) continue
                queue = local; upload(local, core); if (authorizationBlocked) return
            }
            control.finished(); mutable.value = InterviewCaptureState(message = "Protected uploads checked.")
        } catch (error: Exception) { if (error is CancellationException) throw error; if (error is CorePrivacyException && error.eraseLegacy) eraseCurrentOriginQueues(); mutable.value = InterviewCaptureState(message = error.message ?: "Protected upload remains pending.") }
        finally { stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() }
    }
    private fun eraseCurrentOriginQueues() {
        val url = CredentialVault(this).read()?.url ?: return
        for (local in RecordingQueue.load(root, RecordingCipherKey.get())) if (local.state.binding == RecordingQueue.binding(url, local.state.deviceId, local.state.epoch)) local.clear()
        queue = null
    }
    private fun finishService() { stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() }
    @Synchronized private fun releaseCaptureWakeLock() { captureWakeLock?.let { if (it.isHeld) it.release() }; captureWakeLock = null }
    override fun onTimeout(startId: Int, fgsType: Int) { stopRequested = true; scope.cancel(); stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() }
    override fun onDestroy() {
        destroyed = true; stopRequested = true; scope.cancel(); runCatching { microphone?.stop() }; releaseCaptureWakeLock()
        focus?.let { getSystemService(AudioManager::class.java).abandonAudioFocusRequest(it) }; focus = null
        mutable.value = mutable.value.copy(starting = false, recording = false, uploading = false)
        super.onDestroy()
    }
}
