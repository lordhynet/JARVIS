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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
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
        lifecycleScope.launch { repeatOnLifecycle(Lifecycle.State.STARTED) { while (isActive) { model.poll(); delay(2000) } } }
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
    private fun readPairing(intent: Intent?) {
        intent?.getStringExtra("jarvis.provision")?.let { model.offerPairing(it); intent.removeExtra("jarvis.provision") }
        intent?.data?.takeIf { it.scheme == "jarvis" && it.host == "pair" }?.let { model.offerQrPairing(it.toString()); intent.data = null }
    }
    private fun sendVoiceControl(action: String, key: String, value: String) {
        val voice = LiveVoiceService.state.value
        if (voice.ready || voice.modeOffer?.starting == true)
            startService(Intent(this, LiveVoiceService::class.java).setAction(action).putExtra(key, value))
    }
    private fun requestVoice(everyday: Boolean) {
        if (InterviewRecordingService.state.value.let { it.starting || it.recording }) return
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
        if (InterviewRecordingService.state.value.let { it.starting || it.recording }) return
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
        tone?.release(); tone = null
        permissions.cancel(); model.setAudioStarting(false)
        permissionPurposeWake = false
        if (LiveVoiceService.state.value.active || LiveVoiceService.state.value.wakeListening) startService(Intent(this, LiveVoiceService::class.java).setAction(LiveVoiceService.STOP))
    }
    private fun openPermissions() {
        permissions.cancel(); model.setAudioStarting(false)
        startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:$packageName")))
    }
    private fun testAudio() {
        if (InterviewRecordingService.state.value.let { it.starting || it.recording }) return
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
    val recording by InterviewRecordingService.state.collectAsStateWithLifecycle()
    val recordingActive = recording.starting || recording.recording
    var settings by remember { mutableStateOf(false) }
    var consent by remember(state.url) { mutableStateOf(false) }
    var forget by remember { mutableStateOf(false) }
    var recover by remember { mutableStateOf(false) }
    var confirmLocalWake by remember { mutableStateOf(false) }
    LaunchedEffect(state.privacyRevoked) { if (state.privacyRevoked) stop() }
    LaunchedEffect(voice.active) { if (!voice.active) consent = false }
    val haptic = LocalHapticFeedback.current
    LaunchedEffect(voice.active) { if (voice.active) haptic.performHapticFeedback(HapticFeedbackType.LongPress) }
    val voiceReady = state.verified && state.voice?.ready == true && !state.offline
    val canStart = consent && voiceReady && !state.busy && !state.audioStarting && !recordingActive
    // A tap without cloud-voice consent explains what is needed instead of silently doing nothing.
    var consentNudge by remember { mutableStateOf(false) }
    LaunchedEffect(consent) { if (consent) consentNudge = false }
    val needsConsent = !consent && voiceReady && !state.busy && !state.audioStarting && !recordingActive
    Scaffold(containerColor = JarvisColors.Ink, topBar = {
        Wordmark(state.connected, if (state.connected && !state.pairingOffered) voice.mode else null,
            if (state.connected && !state.pairingOffered) { { settings = !settings } } else null, settings)
    }) { padding ->
        Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 24.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)) {
            if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth(), color = JarvisColors.Mint, trackColor = JarvisColors.Line)
            if (state.notice.isNotBlank()) Text(state.notice, color = JarvisColors.Amber, fontSize = 14.sp)
            if (state.connected && !recordingActive) {
                for (offer in state.handoffs?.offers.orEmpty().filter { it.incoming }) {
                    Text("Continue on phone?", color = JarvisColors.Mint)
                    Text("Accepting continues this conversation with normal cloud voice processing.", color = JarvisColors.Muted)
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Button(onClick = { model.acceptHandoff(offer) { start(true) } }, enabled = !state.busy && !voice.active) { Text("Accept and continue") }
                        OutlinedButton(onClick = { model.declineHandoff(offer) }, enabled = !state.busy) { Text("Decline") }
                    }
                }
                for (offer in state.handoffs?.offers.orEmpty().filter { !it.incoming }) {
                    Text("Waiting for the other device. Source audio stops after acceptance.", color = JarvisColors.Muted)
                    TextButton(onClick = { model.declineHandoff(offer) }, enabled = !state.busy) { Text("Cancel transfer") }
                }
                if (voice.active && state.handoffs?.offers.isNullOrEmpty()) {
                    var destinations by remember { mutableStateOf(false) }
                    TextButton(onClick = { destinations = !destinations }) { Text("Continue on another device") }
                    if (destinations) for (target in state.handoffs?.targets.orEmpty())
                        TextButton(onClick = { model.sendHandoff(target); destinations = false }, enabled = !state.busy) { Text(target.name) }
                }
            }
            if (state.connected || recordingActive) RecordingControls(stop)
            if (recordingActive) {
                Text("Voice and connection changes are paused until recording stops.", color = JarvisColors.Muted)
            } else if (!state.connected || state.pairingOffered) {
                PairingScreen(state, model::acceptPairing) { url, key -> model.connect(url, key) }
                if (state.connected || state.legacyDrafts) TextButton(onClick = { forget = true }) { Text("Forget this phone", color = JarvisColors.Red) }
            } else if (settings) {
                Text("Settings", fontSize = 26.sp, fontWeight = FontWeight.Light)
                SectionLabel("Connection")
                SettingRow(state.url.removePrefix("https://"), if (state.offline) "Not reachable" else state.certificateTrust ?: "Connected") {
                    TextButton(onClick = model::refresh, enabled = !state.busy, modifier = Modifier.heightIn(min = 48.dp)) { Text("Check") }
                }
                SectionLabel("Voice")
                SettingRow("Hey JARVIS", if (voice.wakeListening) "Listening on this phone only" else "Off") {
                    Switch(checked = voice.wakeListening, enabled = !recordingActive && !voice.active && !state.busy && voiceReady,
                        onCheckedChange = { on -> if (on) confirmLocalWake = true else stop() })
                }
                SettingRow("Finish by saying", "“Thanks, JARVIS” · “That's all” · “Bye”")
                SettingRow("Earbuds and microphone", if (state.verified) "Confirmed" else "Play the test sound, then confirm") {
                    TextButton(onClick = model::verifyAudio, enabled = !state.busy && !voice.active, modifier = Modifier.heightIn(min = 48.dp)) { Text(if (state.verified) "Recheck" else "Confirm") }
                }
                SectionLabel("Audio")
                SettingRow("Test sound", "Plays a short tone in your earbuds") {
                    TextButton(onClick = test, enabled = !recordingActive && !voice.active, modifier = Modifier.heightIn(min = 48.dp)) { Text("Play") }
                }
                SettingRow("Permissions", "Microphone and notifications") {
                    TextButton(onClick = appPermissions, enabled = !voice.active, modifier = Modifier.heightIn(min = 48.dp)) { Text("Open") }
                }
                SettingRow("Earbud buttons", "Pause ends · Next interrupts the reply")
                if (state.legacyDrafts) SettingRow("Old app drafts", "Kept encrypted on this phone") {
                    TextButton(onClick = { recover = true }, enabled = !voice.active, modifier = Modifier.heightIn(min = 48.dp)) { Text("Export") }
                }
                TextButton(onClick = { forget = true }, enabled = !voice.active, modifier = Modifier.padding(top = 12.dp).heightIn(min = 48.dp)) {
                    Text("Forget this phone", color = JarvisColors.Red)
                }
            } else {
                Spacer(Modifier.height(24.dp))
                val orb = when {
                    voice.active -> if (voice.ready) OrbState.Listening else OrbState.Connecting
                    voice.wakeListening -> OrbState.WakeListening
                    voiceReady -> OrbState.Ready
                    else -> OrbState.Unavailable
                }
                Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                    VoiceOrb(orb, enabled = voice.active || state.audioStarting || canStart || needsConsent) {
                        when {
                            voice.active || state.audioStarting -> stop()
                            !consent -> consentNudge = true
                            else -> start(voice.canStartEveryday)
                        }
                    }
                }
                val hint = when {
                    voice.active && voice.message.isNotBlank() -> voice.message
                    voice.active -> "Say “thanks, JARVIS” to finish"
                    state.audioStarting -> "Starting… tap to cancel"
                    consentNudge && !consent -> "Turn on Voice processing below, then tap the circle"
                    state.offline -> "Waiting for JARVIS"
                    !state.verified -> "Confirm your earbuds in Settings first"
                    state.voice?.ready != true -> state.voice?.detail ?: "Checking voice…"
                    voice.message.isNotBlank() -> voice.message
                    voice.wakeListening -> "Or tap to talk now"
                    else -> "Tap, or say “Hey JARVIS”"
                }
                Text(hint, color = JarvisColors.Muted, fontSize = 15.sp, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
                if (voice.active) {
                    voice.mode?.let { mode ->
                        val target = if ((voice.queuedMode ?: mode) == "Serious") "Everyday" else "Serious"
                        TextButton(onClick = { requestMode(target) }, modifier = Modifier.align(Alignment.CenterHorizontally).heightIn(min = 48.dp)) {
                            Text("Switch to $target", color = JarvisColors.Muted)
                        }
                    }
                    voice.modeOffer?.let { offer ->
                        val maximum = NumberFormat.getCurrencyInstance(Locale.UK).format(offer.poundsForTenMinutes.setScale(2, RoundingMode.CEILING))
                        Text("Serious voice costs up to $maximum per 10 minutes.", textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            Button(onClick = { confirmMode(offer.id) }, modifier = Modifier.weight(1f).heightIn(min = 48.dp)) { Text("Use Serious") }
                            if (offer.starting) OutlinedButton(onClick = { requestMode("Everyday") }, modifier = Modifier.weight(1f).heightIn(min = 48.dp)) { Text("Stay Everyday") }
                        }
                    }
                    if (voice.modeNotice.isNotBlank()) Text(voice.modeNotice, color = JarvisColors.Muted, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
                } else if (!state.audioStarting) {
                    Row(Modifier.fillMaxWidth().heightIn(min = 56.dp).semantics(mergeDescendants = true) {}, verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Column(Modifier.weight(1f)) {
                            Text("Voice processing", fontSize = 15.sp)
                            Text("Allow for this conversation", fontSize = 13.sp, color = JarvisColors.Muted)
                        }
                        Switch(checked = consent, onCheckedChange = { consent = it })
                    }
                }
            }
        }
    }
    if (confirmLocalWake) {
        AlertDialog(onDismissRequest = { confirmLocalWake = false }, title = { Text("Turn on Hey JARVIS?") },
        text = { Text("This phone listens for \u201cHey JARVIS\u201d itself, with a notification showing. Nothing is sent until it hears you. It uses some battery, and Android may pause it.") },
        confirmButton = { TextButton(onClick = { confirmLocalWake = false; enableWake() }) { Text("Turn on") } },
        dismissButton = { TextButton(onClick = { confirmLocalWake = false }) { Text("Cancel") } })
    }
    if (forget) {
        AlertDialog(onDismissRequest = { forget = false }, title = { Text("Forget this phone?") },
        text = { Text("Removes this phone's key and old drafts. Your conversations stay on JARVIS.") },
        confirmButton = { TextButton(onClick = { stop(); model.disconnect(); forget = false }) { Text("Forget") } },
        dismissButton = { TextButton(onClick = { forget = false }) { Text("Keep connection") } })
    }
    if (recover) {
        AlertDialog(onDismissRequest = { recover = false }, title = { Text("Export legacy drafts?") },
        text = { Text("Choose where to save a JSON copy of old private editor drafts and unconfirmed requests. This does not send or replay them to Core.") },
        confirmButton = { TextButton(onClick = { export(); recover = false }) { Text("Choose export file") } },
        dismissButton = { TextButton(onClick = { recover = false }) { Text("Cancel") } })
    }
}

