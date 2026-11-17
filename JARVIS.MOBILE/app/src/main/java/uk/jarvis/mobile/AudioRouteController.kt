package uk.jarvis.mobile

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFocusRequest
import android.media.AudioManager
import android.os.Build
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeout

internal class AudioRouteController(private val context: Context, private val onFocusLost: () -> Unit) {
    private val audio = context.getSystemService(AudioManager::class.java)
    private var focus: AudioFocusRequest? = null
    private var routeGeneration = 0L
    @Volatile var phase = CompanionAudioPhase.Idle
        private set

    suspend fun enterLive(isCurrent: () -> Boolean) {
        val selection = beginLive(isCurrent) ?: return
        selectLegacyHeadset(selection, isCurrent)
    }

    @Synchronized private fun beginLive(isCurrent: () -> Boolean): Long? {
        if (!isCurrent()) return null
        val request = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN_TRANSIENT)
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
                .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
            .setOnAudioFocusChangeListener { change -> if (change < 0) onFocusLost() }.build()
        focus = request
        check(audio.requestAudioFocus(request) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED) { "Another app currently owns audio focus." }
        transition(CompanionAudioPhase.LiveVoice)
        if (Build.VERSION.SDK_INT >= 31) {
            val headset = audio.availableCommunicationDevices.firstOrNull {
                it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO || it.type == AudioDeviceInfo.TYPE_BLE_HEADSET
            }
            headset?.let { check(audio.setCommunicationDevice(it)) { "Android could not select the communication headset." } }
            return null
        }
        return routeGeneration.takeIf {
            audio.getDevices(AudioManager.GET_DEVICES_OUTPUTS).any { device ->
                device.type == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP || device.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO
            }
        }
    }

    @Synchronized fun leaveLive(next: CompanionAudioPhase) {
        focus?.let { audio.abandonAudioFocusRequest(it) }
        focus = null
        transition(next)
    }

    @Suppress("DEPRECATION")
    @Synchronized fun transition(next: CompanionAudioPhase) {
        if (phase != next) routeGeneration++
        for (action in AudioRoutePolicy.transition(phase, next)) when (action) {
            AudioRouteAction.EnterCommunicationRoute -> audio.mode = AudioManager.MODE_IN_COMMUNICATION
            AudioRouteAction.RestoreDefaultRoute -> {
                if (Build.VERSION.SDK_INT >= 31) audio.clearCommunicationDevice()
                else runCatching { audio.isBluetoothScoOn = false; audio.stopBluetoothSco() }
                audio.mode = AudioManager.MODE_NORMAL
            }
        }
        phase = next
    }

    @Suppress("DEPRECATION")
    private suspend fun selectLegacyHeadset(selection: Long, isCurrent: () -> Boolean) {
        val connected = CompletableDeferred<Unit>()
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                if (intent?.getIntExtra(AudioManager.EXTRA_SCO_AUDIO_STATE, -1) == AudioManager.SCO_AUDIO_STATE_CONNECTED)
                    connected.complete(Unit)
            }
        }
        context.registerReceiver(receiver, IntentFilter(AudioManager.ACTION_SCO_AUDIO_STATE_UPDATED))
        try {
            synchronized(this) {
                if (selection != routeGeneration || !isCurrent()) return
                audio.startBluetoothSco()
            }
            withTimeout(8000) { connected.await() }
            synchronized(this) {
                if (selection == routeGeneration && isCurrent()) audio.isBluetoothScoOn = true
            }
        } finally { context.unregisterReceiver(receiver) }
    }
}
