package uk.jarvis.mobile

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.media.AudioManager
import android.media.ToneGenerator
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.*
import java.math.RoundingMode
import java.text.NumberFormat
import java.util.Locale

class MainActivity : ComponentActivity() {
    private lateinit var model: JarvisViewModel
    private val permissions = AudioPermissionFlow()
    private var tone: ToneGenerator? = null
    private val permission = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { resolvePermissions() }
    private val export = registerForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri -> uri?.let(model::exportLegacy) }
    private var permissionPurposeWake = false
    private var startEveryday = false
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        model = ViewModelProvider(this)[JarvisViewModel::class.java]
        readPairing(intent)
        resumeWakeFromNotification(intent)
        lifecycleScope.launch { repeatOnLifecycle(Lifecycle.State.STARTED) { while (isActive) { delay(5000); model.poll() } } }
        setContent {
            JarvisTheme {
                VoiceCompanion(model, ::requestVoice, ::requestLocalWake, ::stopVoice, ::openPermissions, ::testAudio,
                    { sendVoiceControl(LiveVoiceService.VOICE_MODE, "mode", it) },
                    { sendVoiceControl(LiveVoiceService.CONFIRM_VOICE_MODE, "offerId", it) },
                    { export.launch("jarvis-legacy-local-drafts.json") })
            }
        }
    }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); setIntent(intent); readPairing(intent); resumeWakeFromNotification(intent) }
    private fun resumeWakeFromNotification(intent: Intent?) {
        if (intent?.action != LiveVoiceService.RESUME_WAKE) return
        intent.action = null
        startForegroundService(Intent(this, LiveVoiceService::class.java).setAction(LiveVoiceService.WAKE_START))
    }
    private fun readPairing(intent: Intent?) { intent?.getStringExtra("jarvis.provision")?.let { model.offerPairing(it); intent.removeExtra("jarvis.provision") } }
    private fun sendVoiceControl(action: String, key: String, value: String) {
        val voice = LiveVoiceService.state.value
        if (voice.ready || voice.modeOffer?.starting == true)
            startService(Intent(this, LiveVoiceService::class.java).setAction(action).putExtra(key, value))
    }
    private fun requestVoice(everyday: Boolean) {
        if (model.state.value.busy || !model.state.value.connected) return
        permissionPurposeWake = false
        if (!permissions.begin()) { permissions.cancel(); model.reportAudioIssue("The earlier start was cancelled. Tap Talk again when ready."); return }
        startEveryday = everyday
        model.setAudioStarting(true)
        val required = mutableListOf(Manifest.permission.RECORD_AUDIO)
        if (Build.VERSION.SDK_INT >= 33) required += Manifest.permission.POST_NOTIFICATIONS
        val missing = required.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isEmpty()) resolvePermissions() else permission.launch(missing.toTypedArray())
    }
    private fun requestLocalWake() {
        if (model.state.value.busy || !model.state.value.connected) return
        if (Build.VERSION.SDK_INT < 31) { model.reportAudioIssue("Local Hey JARVIS needs Android 12 or newer with an on-device speech recognizer."); return }
        if (!model.state.value.verified) { model.reportAudioIssue("Have the Core owner confirm this phone's microphone and audio setup first."); return }
        if (model.state.value.voice?.ready != true) { model.reportAudioIssue(model.state.value.voice?.detail ?: "Core voice must be ready before enabling wake responses."); return }
        permissionPurposeWake = true
        if (!permissions.begin()) { permissionPurposeWake = false; return }
        model.setAudioStarting(true)
        val required = mutableListOf(Manifest.permission.RECORD_AUDIO)
        if (Build.VERSION.SDK_INT >= 33) required += Manifest.permission.POST_NOTIFICATIONS
        val missing = required.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isEmpty()) resolvePermissions() else permission.launch(missing.toTypedArray())
    }
    private fun resolvePermissions() {
        when (val result = permissions.complete(checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED,
            Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED)) {
            is AudioPermissionResult.Ready -> if (permissionPurposeWake) {
                model.setAudioStarting(false)
                if (permissions.isCurrent(result.generation) && lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED))
                    startForegroundService(Intent(this, LiveVoiceService::class.java).setAction(LiveVoiceService.WAKE_START))
                else model.reportAudioIssue("Local wake start cancelled. Enable it again when ready.")
                permissionPurposeWake = false
            } else {
                val everyday = startEveryday
                model.prepareLive { sessionId ->
                    model.setAudioStarting(false)
                    if (permissions.isCurrent(result.generation) && lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED))
                        startForegroundService(Intent(this, LiveVoiceService::class.java).setAction(LiveVoiceService.START)
                            .putExtra("sessionId", sessionId).putExtra(LiveVoiceService.START_EVERYDAY_EXTRA, everyday))
                    else model.reportAudioIssue("Voice start cancelled. Tap Talk again when ready.")
                }
            }
            is AudioPermissionResult.Denied -> { permissionPurposeWake = false; model.reportAudioIssue(result.message) }
            AudioPermissionResult.Cancelled -> permissionPurposeWake = false
        }
    }
    private fun stopVoice() {
        permissions.cancel(); model.setAudioStarting(false)
        permissionPurposeWake = false
        if (LiveVoiceService.state.value.active || LiveVoiceService.state.value.wakeListening) startService(Intent(this, LiveVoiceService::class.java).setAction(LiveVoiceService.STOP))
    }
    private fun openPermissions() {
        permissions.cancel(); model.setAudioStarting(false)
        startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:$packageName")))
    }
    private fun testAudio() {
        runCatching {
            tone?.release()
            val current = ToneGenerator(AudioManager.STREAM_MUSIC, 35); tone = current; current.startTone(ToneGenerator.TONE_PROP_ACK, 500)
            lifecycleScope.launch { delay(700); if (tone === current) { current.release(); tone = null } }
        }.onFailure { model.reportAudioIssue("Android could not play the audio check. Check earbud connection and volume.") }
    }
    override fun onStop() { permissions.cancel(); model.setAudioStarting(false); super.onStop() }
    override fun onDestroy() { permissions.cancel(); tone?.release(); tone = null; super.onDestroy() }
}

@Composable
private fun VoiceCompanion(model: JarvisViewModel, start: (Boolean) -> Unit, enableWake: () -> Unit, stop: () -> Unit,
    appPermissions: () -> Unit, test: () -> Unit, requestMode: (String) -> Unit, confirmMode: (String) -> Unit, export: () -> Unit) {
    val state by model.state.collectAsStateWithLifecycle()
    val voice by LiveVoiceService.state.collectAsStateWithLifecycle()
    var settings by remember { mutableStateOf(false) }
    var consent by remember(state.url) { mutableStateOf(false) }
    var forget by remember { mutableStateOf(false) }
    var recover by remember { mutableStateOf(false) }
    var confirmLocalWake by remember { mutableStateOf(false) }
    LaunchedEffect(state.privacyRevoked) { if (state.privacyRevoked) stop() }
    LaunchedEffect(voice.active) { if (!voice.active) consent = false }
    Scaffold(topBar = { Wordmark(state.connected, if (state.connected) { { settings = !settings } } else null, settings) }) { padding ->
        Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(24.dp), verticalArrangement = Arrangement.spacedBy(20.dp)) {
            if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            if (state.notice.isNotBlank()) Text(state.notice, color = MaterialTheme.colorScheme.primary)
            if (state.audioStarting) OutlinedButton(onClick = stop, modifier = Modifier.fillMaxWidth()) { Text("Cancel voice start") }
            if (!state.connected || state.pairingOffered) {
                PairingScreen(state, model::acceptPairing) { url, key -> model.connect(url, key) }
                TextButton(onClick = { forget = true }) { Text("Forget this phone's connection and legacy data") }
            } else if (settings) {
                Text("Connection & audio", style = MaterialTheme.typography.headlineMedium)
                Text(state.url)
                state.certificateTrust?.let { Text(it) }
                Text("Your phone sends live audio to Core and plays Core's reply. Conversations, practice, memory, CVs and tools stay on Core.")
                Text("For away-from-home use, connect to Core through a persistent private HTTPS/VPN address. A localhost ADB bridge only works while debugging is connected.", style = MaterialTheme.typography.bodySmall)
                OutlinedButton(onClick = model::refresh, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("Check Core connection") }
                OutlinedButton(onClick = appPermissions, enabled = !voice.active, modifier = Modifier.fillMaxWidth()) { Text("Microphone & notification permissions") }
                OutlinedButton(onClick = test, enabled = !voice.active, modifier = Modifier.fillMaxWidth()) { Text("Play earbud audio check") }
                Text("Check your earbud microphone in a call or recorder, then confirm input and output here. This does not start recording.", style = MaterialTheme.typography.bodySmall)
                OutlinedButton(onClick = model::verifyAudio, enabled = !state.busy && !voice.active, modifier = Modifier.fillMaxWidth()) { Text(if (state.verified) "Owner confirmed audio setup" else "Refresh owner audio confirmation") }
                OutlinedButton(onClick = { if (voice.wakeListening) stop() else confirmLocalWake = true }, enabled = !voice.active && !state.busy,
                    modifier = Modifier.fillMaxWidth()) { Text(if (voice.wakeListening) "Disable local Hey JARVIS" else "Enable local Hey JARVIS") }
                Text("Local wake detection uses Android's on-device recognizer and a visible microphone notification. After it hears Hey JARVIS, Core arbitrates which device responds and live audio goes to the configured voice provider. Android may stop background listening; battery use and Bluetooth routing need device testing.", style = MaterialTheme.typography.bodySmall)
                Text("During a conversation, headset Pause/Stop ends microphone and playback; Next interrupts the reply. Start voice explicitly before putting the phone in your pocket.")
                if (state.legacyDrafts) {
                    Text("Encrypted drafts from the earlier feature app are retained for recovery. This audio companion never replays them.")
                    OutlinedButton(onClick = { recover = true }, enabled = !voice.active, modifier = Modifier.fillMaxWidth()) { Text("Export legacy local drafts") }
                }
                TextButton(onClick = { forget = true }, enabled = !voice.active) { Text("Disconnect and erase local legacy data") }
            } else {
                Spacer(Modifier.height(28.dp))
                Text("JARVIS, in your earbuds.", style = MaterialTheme.typography.headlineLarge)
                Text(if (voice.active) voice.message else if (state.offline) "Waiting for Core connection" else "Talk naturally. Core picks up the conversation.", style = MaterialTheme.typography.titleMedium)
                if (voice.active) {
                    Text(if (voice.ready) "MICROPHONE ACTIVE" else "CONNECTING TO CORE", color = MaterialTheme.colorScheme.primary)
                    Button(onClick = stop, modifier = Modifier.fillMaxWidth().heightIn(min = 100.dp), colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)) { Text("Stop microphone & reply", style = MaterialTheme.typography.titleLarge) }
                    voice.mode?.let { mode ->
                        val target = if ((voice.queuedMode ?: mode) == "Serious") "Everyday" else "Serious"
                        OutlinedButton(onClick = { requestMode(target) }, modifier = Modifier.fillMaxWidth()) { Text("$mode mode · switch to $target") }
                    }
                    voice.modeOffer?.let { offer ->
                        val maximum = NumberFormat.getCurrencyInstance(Locale.UK)
                            .format(offer.poundsForTenMinutes.setScale(2, RoundingMode.CEILING))
                        val whenReady = if (offer.starting) "Confirm to continue." else "Confirm to start at the next renewal."
                        Text("Serious voice reserves up to $maximum per 10 minutes. $whenReady")
                        OutlinedButton(onClick = { confirmMode(offer.id) }, modifier = Modifier.fillMaxWidth()) { Text("Confirm Serious voice") }
                        if (offer.starting) OutlinedButton(onClick = { requestMode("Everyday") }, modifier = Modifier.fillMaxWidth()) {
                            Text("Use Everyday voice")
                        }
                    }
                    if (voice.modeNotice.isNotBlank()) Text(voice.modeNotice)
                } else {
                    if (voice.wakeListening) Text("LOCAL WAKE LISTENING · on-device detection", color = MaterialTheme.colorScheme.primary)
                    if (voice.message.isNotBlank()) Text(voice.message)
                    if (state.voice?.ready != true) Text(state.voice?.detail ?: "Checking Core voice readiness…")
                    if (!state.verified) Text("Check your microphone and earbuds, then have the owner confirm this device on Core before starting.")
                    Row(Modifier.fillMaxWidth().semantics(mergeDescendants = true) {}) {
                        Checkbox(consent, { consent = it })
                        Text("Allow Core's voice provider to process live audio and relevant saved conversation/practice context for this conversation.", Modifier.weight(1f))
                    }
                    Button(onClick = { start(voice.canStartEveryday) }, enabled = consent && state.verified && state.voice?.ready == true && !state.busy && !state.offline && !state.audioStarting,
                        modifier = Modifier.fillMaxWidth().heightIn(min = 100.dp)) {
                        Text(if (voice.canStartEveryday) "Start in Everyday voice" else "Talk to JARVIS", style = MaterialTheme.typography.headlineSmall)
                    }
                    OutlinedButton(onClick = { if (voice.wakeListening) stop() else confirmLocalWake = true }, enabled = state.verified && state.voice?.ready == true && !state.busy && !state.audioStarting,
                        modifier = Modifier.fillMaxWidth()) { Text(if (voice.wakeListening) "Disable local Hey JARVIS" else "Enable local Hey JARVIS") }
                    TextButton(onClick = model::refresh, enabled = !state.busy) { Text("Reconnect / check Core") }
                }
                Text("The screen can go off after you start. Voice stops if its Core connection, audio ownership or budget ends.", style = MaterialTheme.typography.bodySmall)
            }
        }
    }
    if (confirmLocalWake) AlertDialog(onDismissRequest = { confirmLocalWake = false }, title = { Text("Enable local Hey JARVIS?") },
        text = { Text("Android will keep a foreground microphone service visible and process wake audio with its on-device recognizer; wake audio is not sent to a cloud speech provider. When it detects Hey JARVIS, Core arbitrates one responding device and this phone starts live audio to your configured AI provider. This requires network access, uses battery, and Android may stop listening. Bluetooth earbud routing must be checked on your phone.") },
        confirmButton = { TextButton(onClick = { confirmLocalWake = false; enableWake() }) { Text("Enable local wake") } },
        dismissButton = { TextButton(onClick = { confirmLocalWake = false }) { Text("Cancel") } })
    if (forget) AlertDialog(onDismissRequest = { forget = false }, title = { Text("Forget this phone's connection?") },
        text = { Text("Removes saved Core credentials and any encrypted legacy editor drafts on this phone. Core data is unchanged.") },
        confirmButton = { TextButton(onClick = { stop(); model.disconnect(); forget = false }) { Text("Forget local connection/data") } },
        dismissButton = { TextButton(onClick = { forget = false }) { Text("Keep connection") } })
    if (recover) AlertDialog(onDismissRequest = { recover = false }, title = { Text("Export legacy drafts?") },
        text = { Text("Choose where to save a JSON copy of old private editor drafts and unconfirmed requests. This does not send or replay them to Core.") },
        confirmButton = { TextButton(onClick = { export(); recover = false }) { Text("Choose export file") } },
        dismissButton = { TextButton(onClick = { recover = false }) { Text("Cancel") } })
}

