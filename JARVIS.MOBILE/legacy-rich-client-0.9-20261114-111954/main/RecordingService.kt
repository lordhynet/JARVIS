package uk.jarvis.mobile

import android.Manifest
import android.app.*
import android.content.*
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.*
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.*
import android.view.KeyEvent
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File

data class CaptureState(val recording: Boolean = false, val file: File? = null, val message: String = "")

class RecordingService : Service() {
    companion object {
        const val FINISH = "uk.jarvis.mobile.FINISH"
        const val DISCARD = "uk.jarvis.mobile.DISCARD"
        private val mutable = MutableStateFlow(CaptureState())
        val state = mutable.asStateFlow()
        fun clearFile() { mutable.value.file?.delete(); mutable.value = CaptureState() }
    }
    private var recorder: MediaRecorder? = null
    private var output: File? = null
    private lateinit var audio: AudioManager
    private var focus: AudioFocusRequest? = null
    private var mediaSession: MediaSession? = null
    private val stopGate = CaptureStopGate()
    private val handler = Handler(Looper.getMainLooper())
    private val noisy = object : BroadcastReceiver() { override fun onReceive(context: Context?, intent: Intent?) { end(false, "Audio route changed. Recording stopped; start again when ready.") } }

    override fun onCreate() {
        super.onCreate()
        audio = getSystemService(AudioManager::class.java)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(noisy, IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY), RECEIVER_NOT_EXPORTED)
        else registerReceiver(noisy, IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY))
        getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel("voice", "Active voice recording", NotificationManager.IMPORTANCE_LOW))
    }
    override fun onBind(intent: Intent?) = null
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            FINISH -> end(true, "Recording ready. Transcribe to send this audio to Core's configured provider.")
            DISCARD -> end(false, "Recording discarded.")
            else -> if (recorder == null && !stopGate.stopped) start()
        }
        return START_NOT_STICKY
    }
    @Suppress("DEPRECATION")
    private fun start() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            mutable.value = CaptureState(message = "Microphone permission is required. Allow it in Android app settings, then start again."); stopSelf(); return
        }
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            mutable.value = CaptureState(message = "Allow notifications for dependable background Stop controls."); stopSelf(); return
        }
        val stop = PendingIntent.getService(this, 1, Intent(this, RecordingService::class.java).setAction(DISCARD), PendingIntent.FLAG_IMMUTABLE)
        val finish = PendingIntent.getService(this, 2, Intent(this, RecordingService::class.java).setAction(FINISH), PendingIntent.FLAG_IMMUTABLE)
        val open = PendingIntent.getActivity(this, 3, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        mediaSession = MediaSession(this, "JARVIS answer recording").apply {
            setCallback(object : MediaSession.Callback() {
                override fun onPause() { if (recorder != null) end(true, "Finished with headset controls. Review before transcription.") }
                override fun onStop() { if (recorder != null) end(false, "Discarded with headset controls.") }
                override fun onMediaButtonEvent(intent: Intent): Boolean {
                    val event = intent.mediaKey() ?: return super.onMediaButtonEvent(intent)
                    if (!HeadsetControls.handles(event.keyCode)) return super.onMediaButtonEvent(intent)
                    when (HeadsetControls.command(false, event.keyCode, event.action == KeyEvent.ACTION_DOWN, event.repeatCount, recorder != null)) {
                        HeadsetCommand.FinishRecording -> onPause()
                        HeadsetCommand.DiscardRecording -> onStop()
                        else -> Unit
                    }
                    return true
                }
            })
            setPlaybackState(PlaybackState.Builder().setActions(PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_STOP or PlaybackState.ACTION_PLAY_PAUSE)
                .setState(PlaybackState.STATE_PLAYING, 0, 1f).build())
            isActive = true
        }
        val notification = Notification.Builder(this, "voice").setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("JARVIS microphone active").setContentText("Recording your answer. Up to three minutes.")
            .setContentIntent(open).setOngoing(true).addAction(android.R.drawable.ic_media_pause, "Finish", finish)
            .addAction(android.R.drawable.ic_delete, "Stop and discard", stop)
            .setStyle(Notification.MediaStyle().setMediaSession(mediaSession!!.sessionToken).setShowActionsInCompactView(0, 1)).build()
        try {
            startForeground(42, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
            focus = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_EXCLUSIVE)
                .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                .setOnAudioFocusChangeListener { change -> if (change != AudioManager.AUDIOFOCUS_GAIN) end(false, "Audio interrupted. Recording was discarded; resume explicitly.") }.build()
            check(audio.requestAudioFocus(focus!!) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED) { "Audio focus is unavailable." }
            output = File.createTempFile("answer-", ".m4a", cacheDir)
            recorder = (if (Build.VERSION.SDK_INT >= 31) MediaRecorder(this) else MediaRecorder()).apply {
                setAudioSource(MediaRecorder.AudioSource.VOICE_RECOGNITION)
                setOutputFormat(MediaRecorder.OutputFormat.MPEG_4); setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
                setAudioSamplingRate(16000); setAudioChannels(1); setAudioEncodingBitRate(64000)
                setOutputFile(output!!.absolutePath); prepare(); start()
            }
            mutable.value = CaptureState(recording = true, message = "Microphone active. Finish when ready; Stop discards.")
            handler.postDelayed({ end(true, "Three-minute limit reached. Review or discard this recording.") }, 180_000)
        } catch (_: Exception) { end(false, "Microphone could not start. Check permission and other audio apps.") }
    }
    private fun end(keep: Boolean, message: String) {
        if (!stopGate.take()) return
        handler.removeCallbacksAndMessages(null)
        var valid = keep && recorder != null
        try { recorder?.stop() } catch (_: Exception) { valid = false }
        recorder?.release(); recorder = null
        focus?.let { audio.abandonAudioFocusRequest(it) }; focus = null
        mediaSession?.release(); mediaSession = null
        val saved = if (valid && (output?.length() ?: 0) > 0) output else null
        if (saved == null) output?.delete()
        output = null
        mutable.value = CaptureState(file = saved, message = if (keep && saved == null) "No usable audio was captured. Start again when ready." else message)
        stopForeground(STOP_FOREGROUND_REMOVE); stopSelf()
    }
    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        recorder?.let { runCatching { it.stop() }; it.release(); output?.delete(); mutable.value = CaptureState(message = "Recording interrupted. Repeat your answer.") }
        focus?.let { audio.abandonAudioFocusRequest(it) }
        mediaSession?.release(); mediaSession = null
        unregisterReceiver(noisy)
        super.onDestroy()
    }
}
