package uk.jarvis.mobile

import android.Manifest
import android.content.Intent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.IntentFilter
import android.media.AudioManager
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.speech.tts.TextToSpeech
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.*
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.*
import uk.jarvis.mobile.contracts.*
import java.util.Locale

class MainActivity : ComponentActivity(), TextToSpeech.OnInitListener {
    private lateinit var model: JarvisViewModel
    private var speech: TextToSpeech? = null
    private val audioPermissions = AudioPermissionFlow()
    private val noisy = object : BroadcastReceiver() { override fun onReceive(context: Context?, intent: Intent?) { speech?.stop() } }
    private val permission = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { resolveAudioPermissions() }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        model = ViewModelProvider(this)[JarvisViewModel::class.java]
        readPairing(intent)
        speech = TextToSpeech(this, this)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(noisy, IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY), RECEIVER_NOT_EXPORTED)
        else registerReceiver(noisy, IntentFilter(AudioManager.ACTION_AUDIO_BECOMING_NOISY))
        lifecycleScope.launch { repeatOnLifecycle(Lifecycle.State.STARTED) { while (isActive) { delay(5000); model.poll() } } }
        setContent {
            JarvisTheme {
                JarvisApp(model, ::requestRecording, ::stopCapture, ::speak, ::requestLive, ::openPermissionSettings)
            }
        }
    }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); readPairing(intent) }
    private fun readPairing(intent: Intent?) {
        intent?.getStringExtra("jarvis.provision")?.let { model.offerPairing(it); intent.removeExtra("jarvis.provision") }
    }
    override fun onInit(status: Int) { if (status == TextToSpeech.SUCCESS) speech?.language = Locale.UK }
    private fun speak(text: String) {
        if (!RecordingService.state.value.recording) speech?.speak(text, TextToSpeech.QUEUE_FLUSH, null, "jarvis-reply")
    }
    private fun requestRecording() = requestAudio(AudioStartMode.Record)
    private fun requestLive() = requestAudio(AudioStartMode.Live)
    private fun openPermissionSettings() {
        audioPermissions.cancel(); model.setAudioStarting(false)
        startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:$packageName")))
    }
    private fun requestAudio(mode: AudioStartMode) {
        if (model.state.value.busy || !model.state.value.connected) { model.reportAudioIssue("Wait for Core to finish connecting or saving, then tap Start again."); return }
        if (!audioPermissions.begin(mode)) { audioPermissions.cancel(); model.reportAudioIssue("The earlier audio request was cancelled. Tap Start again when ready."); return }
        model.setAudioStarting(true)
        speech?.stop()
        if (mode == AudioStartMode.Record && LiveVoiceService.state.value.active) startService(Intent(this, LiveVoiceService::class.java).setAction(LiveVoiceService.STOP))
        if (mode == AudioStartMode.Live && RecordingService.state.value.recording) startService(Intent(this, RecordingService::class.java).setAction(RecordingService.DISCARD))
        val required = mutableListOf(Manifest.permission.RECORD_AUDIO)
        if (Build.VERSION.SDK_INT >= 33) required += Manifest.permission.POST_NOTIFICATIONS
        val missing = required.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isEmpty()) resolveAudioPermissions() else permission.launch(missing.toTypedArray())
    }
    private fun resolveAudioPermissions() {
        when (val result = audioPermissions.complete(
            checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED,
            Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        )) {
            is AudioPermissionResult.Ready -> {
                val start: (String?) -> Unit = { sessionId ->
                    model.setAudioStarting(false)
                    if (audioPermissions.isCurrent(result.request) && lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) {
                        if (result.request.mode == AudioStartMode.Record) startForegroundService(Intent(this, RecordingService::class.java))
                        else startForegroundService(Intent(this, LiveVoiceService::class.java).setAction(LiveVoiceService.START).putExtra("sessionId", sessionId))
                    } else model.reportAudioIssue("Audio start was cancelled. Return to JARVIS and tap Start when ready.")
                    Unit
                }
                if (result.request.mode == AudioStartMode.Record) model.claimAudio { start(null) }
                else model.prepareLive { id -> start(id) }
            }
            is AudioPermissionResult.Denied -> model.reportAudioIssue(result.message)
            AudioPermissionResult.Cancelled -> Unit
        }
    }
    private fun stopCapture(keep: Boolean) {
        audioPermissions.cancel()
        model.setAudioStarting(false)
        speech?.stop()
        if (LiveVoiceService.state.value.active) startService(Intent(this, LiveVoiceService::class.java).setAction(LiveVoiceService.STOP))
        if (RecordingService.state.value.recording)
            startService(Intent(this, RecordingService::class.java).setAction(if (keep) RecordingService.FINISH else RecordingService.DISCARD))
        else if (!keep) RecordingService.clearFile()
    }
    override fun onStop() { audioPermissions.cancel(); model.setAudioStarting(false); super.onStop() }
    override fun onDestroy() { audioPermissions.cancel(); model.setAudioStarting(false); speech?.stop(); speech?.shutdown(); unregisterReceiver(noisy); super.onDestroy() }
}

@Composable
private fun JarvisApp(model: JarvisViewModel, record: () -> Unit, stop: (Boolean) -> Unit, speak: (String) -> Unit, live: () -> Unit, permissions: () -> Unit) {
    val state by model.state.collectAsStateWithLifecycle()
    val capture by RecordingService.state.collectAsStateWithLifecycle()
    val voice by LiveVoiceService.state.collectAsStateWithLifecycle()
    LaunchedEffect(state.privacyRevoked) { if (state.privacyRevoked) stop(false) }
    var tab by remember { mutableIntStateOf(0) }
    var settings by remember { mutableStateOf(false) }
    var eraseLocal by remember { mutableStateOf(false) }
    if (eraseLocal) AlertDialog(onDismissRequest = { eraseLocal = false }, title = { Text("Erase local practice data?") },
        text = { Text("This erases encrypted drafts, cached results, ${state.pendingWrites.size} queued save(s), and saved connection credentials from this phone. Core history is unchanged. Copy any unsaved text before continuing.") },
        confirmButton = { TextButton(onClick = { stop(false); model.disconnect(); eraseLocal = false }) { Text("Erase local data and sign out") } },
        dismissButton = { TextButton(onClick = { eraseLocal = false }) { Text("Keep local data") } })
    var deleteRun by remember { mutableStateOf<WorkflowRunView?>(null) }
    deleteRun?.let { run -> AlertDialog(onDismissRequest = { deleteRun = null }, title = { Text("Delete this workflow result?") },
        text = { Text("Core removes its saved draft and practice result. Minimal request and spending records remain to prevent duplicate charges.") },
        confirmButton = { TextButton(onClick = { model.deleteRun(run); deleteRun = null }) { Text("Delete result") } },
        dismissButton = { TextButton(onClick = { deleteRun = null }) { Text("Keep result") } }) }
    Scaffold(
        topBar = { Wordmark(state.connected, if (state.connected) { { settings = !settings } } else null, settings) },
        bottomBar = { if (state.connected && !settings) NavigationBar {
            listOf("Today", "Practice", "Stories", "Progress", "More").forEachIndexed { index, title ->
                NavigationBarItem(selected = tab == index, onClick = { tab = index }, icon = { Text(listOf("⌂", "◉", "▤", "↗", "▦")[index]) }, label = { Text(title) })
            }
        } }
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(20.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            if (state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            if (state.notice.isNotEmpty()) Text(state.notice, color = MaterialTheme.colorScheme.primary)
            if (state.audioStarting) OutlinedButton(onClick = { stop(false) }) { Text("Cancel audio start") }
            if (state.connected) OfflinePracticeCard(state, model)
            if (!state.connected || state.pairingOffered) {
                PairingScreen(state, model::acceptPairing) { url, key -> model.connect(url, key) }
                TextButton(onClick = { eraseLocal = true }, enabled = !state.busy) { Text("Forget this phone's local connection and practice data") }
            }
            else if (settings) {
                Text("Your connection", style = MaterialTheme.typography.headlineSmall)
                Text("Privacy checks protect your cached practice. When Core reports an erasure or access change, this phone clears its cached practice and signs out. Offline phones receive changes after reconnecting.")
                Text(state.url)
                Text("Core owns your history and AI keys. This personal test build uses your existing Core API key, encrypted with Android Keystore. It does not yet implement OIDC sign-in.")
                Text("Audio test", style = MaterialTheme.typography.titleLarge)
                OutlinedButton(onClick = permissions) { Text("Open microphone & notification settings") }
                Text("Headset controls while Android routes them to JARVIS: Pause finishes an answer recording for review; Stop discards it. During live voice, Pause/Stop ends capture and Next interrupts the reply. Headset Play never starts the microphone.")
                Button(onClick = { speak("This is your JARVIS speaker test. Please confirm you can hear me.") }) { Text("Play speaker test") }
                Text("Check the microphone in Android settings or a recording app. Confirm only after testing both input and output.")
                OutlinedButton(onClick = model::verifyAudio, enabled = !state.busy) { Text("I tested microphone and heard the speaker") }
                Text("Live voice streams audio after your explicit start. Turn-based recordings are deleted after transcription or on next process launch. Earbud routing must be tested on your device.")
                Text("Paid AI: " + (state.paid?.detail ?: "Checking configuration"))
                OutlinedButton(onClick = { eraseLocal = true }) { Text("Sign out and erase local practice data") }
            } else {
                if (tab == 0 || tab == 1 || voice.active) LiveVoiceCard(state, voice, live, { stop(false) })
                state.selected?.takeIf { tab != 0 }?.let { active ->
                    Card { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("Current conversation", style = MaterialTheme.typography.labelLarge)
                        Text(active.title)
                        Text("${active.activity} · ${active.status}")
                        TextButton(onClick = { tab = 1 }) { Text("Return to conversation") }
                    } }
                }
                when (tab) {
                    0 -> TodayScreen(state, model) { tab = it }
                    1 -> {
                        var coaching by remember { mutableStateOf(false) }
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            FilterChip(!coaching, onClick = { coaching = false }, label = { Text("Technical drills") })
                            FilterChip(coaching, onClick = { coaching = true }, label = { Text("Communication & interview") })
                        }
                        if (coaching) CoachingScreen(state, model) else TrainingScreen(state, model)
                        HorizontalDivider()
                        var conversation by remember(state.selected?.id, state.selected?.currentTask) { mutableStateOf(state.selected != null && state.selected?.currentTask?.startsWith("training-question:") != true) }
                        OutlinedButton(onClick = { conversation = !conversation }) { Text(if (conversation) "Hide conversation tools" else "Open conversation tools") }
                        if (conversation) {
                            Text("Practise deliberately.", style = MaterialTheme.typography.headlineMedium)
                            var mode by remember { mutableStateOf(ActivityType.MockInterview) }
                            listOf(ActivityType.MockInterview, ActivityType.SystemDesign, ActivityType.Communication, ActivityType.LeetCode).chunked(2).forEach { pair ->
                                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) { pair.forEach { activity ->
                                    FilterChip(mode == activity, onClick = { mode = activity }, modifier = Modifier.weight(1f), label = {
                                        Text(when (activity) { ActivityType.MockInterview -> "Interview"; ActivityType.SystemDesign -> "System design"; ActivityType.Communication -> "Speak clearly"; else -> "Coding logic" })
                                    })
                                } }
                            }
                            Button(onClick = { model.createSession(mode) }, enabled = !state.busy) { Text("Start conversation") }
                            state.selected?.let { session ->
                                session.messages.orEmpty().takeLast(40).forEach { message ->
                                    Card { Column(Modifier.padding(14.dp)) {
                                        Text(if (message.role == "assistant") "JARVIS" else "You", fontWeight = FontWeight.Bold)
                                        Text(message.text)
                                        if (message.role == "assistant") TextButton(onClick = { model.claimAudio { speak(message.text) } }) { Text("Read aloud") }
                                    } }
                                }
                                val draft = state.transcript
                                OutlinedTextField(draft, model::editAnswer, label = { Text("Your answer") }, modifier = Modifier.fillMaxWidth(), minLines = 3)
                                Button(onClick = { if (draft.isNotBlank()) model.message(draft) }, enabled = !state.busy && session.status == SessionStatus.Active) { Text("Send answer") }
                                var cloud by remember { mutableStateOf(false) }
                                LaunchedEffect(cloud) { if (!cloud) stop(false) }
                                Row { Checkbox(cloud, onCheckedChange = { cloud = it }); Text("I agree to send this recording to Core's configured transcription provider when I tap Transcribe.", Modifier.weight(1f)) }
                                Text(capture.message)
                                if (capture.recording) {
                                    Text("MICROPHONE ACTIVE", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold)
                                    Button(onClick = { stop(true) }) { Text("Finish recording") }
                                    OutlinedButton(onClick = { stop(false) }) { Text("Stop and discard") }
                                } else {
                                    Button(onClick = record, enabled = cloud && !state.busy && session.status == SessionStatus.Active && capture.file == null) { Text("Record answer · up to 3 minutes") }
                                    if (capture.file != null) {
                                        Button(onClick = model::transcribe, enabled = cloud && !state.busy) { Text("Transcribe and review") }
                                        OutlinedButton(onClick = { stop(false) }) { Text("Discard recording") }
                                    }
                                }
                                if (session.status != SessionStatus.Ended) {
                                    OutlinedButton(onClick = { stop(false); model.transition(if (session.status == SessionStatus.Active) "Paused" else "Active") }, enabled = !state.busy) { Text(if (session.status == SessionStatus.Active) "Save checkpoint and pause" else "Resume") }
                                    OutlinedButton(onClick = { stop(false); model.transition("Ended") }, enabled = !state.busy) { Text("End conversation") }
                                }
                            }
                        }
                    }
                    2 -> StoriesScreen(state, model)
                    3 -> {
                        SessionReviewPanel(state, model)
                        TrainingProgress(state.trainingDashboard)
                        state.trainingDashboard?.recentAttempts?.forEach { attempt ->
                            val title = state.questions.firstOrNull { it.id == attempt.questionId }?.title ?: "Saved practice"
                            TextButton(onClick = { model.openTrainingAttempt(attempt.id); tab = 1 }, enabled = !state.busy) { Text("$title · ${if (attempt.skipped) "skipped" else attempt.contentScore?.let { "$it / 4 self" } ?: "unrated"} →") }
                        }
                        Text("Progress & workflows", style = MaterialTheme.typography.headlineMedium)
                        Text("Paid AI today: $${state.paid?.todayCommittedUsd ?: 0.0} / $${state.paid?.dailyLimitUsd ?: 0.0}")
                        Text("Pending charge estimates: ${state.paid?.uncertainCharges ?: 0}. Unknown charges stay reserved.")
                        var allowBrief by remember { mutableStateOf(false) }
                        Row { Checkbox(allowBrief, { allowBrief = it }); Text("Allow Claude to process selected saved story facts and due-card prompts for this brief.", Modifier.weight(1f)) }
                        Button(onClick = { model.brief(allowBrief) }, enabled = !state.busy && allowBrief && state.paid?.ready == true) { Text("Generate AI interview brief") }
                        if (state.paid?.ready != true) Text("Paid AI is unavailable until configured on the server. Unpaid practice remains available.")
                        state.runs.forEach { run ->
                            Card { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                Text("${run.kind} · ${run.status}", fontWeight = FontWeight.Bold)
                                Text("Attempt ${run.attempt} · revision ${run.revision}")
                                run.errorCode?.let { Text("Processing failed: $it. Check provider setup and usage before requesting new paid work.") }
                                run.result?.let { plan -> plan.gaps.forEach { Text(it) }; plan.steps.forEach { step ->
                                    Text(step.prompt)
                                    state.stories.firstOrNull { it.id == step.resourceId }?.let { Text(it.content.title) }
                                    state.learning.firstOrNull { it.id == step.resourceId }?.let { Text(it.prompt) }
                                } }
                                run.brief?.let { brief ->
                                    Text("AI-generated preparation draft · verify facts", color = MaterialTheme.colorScheme.primary)
                                    Text(brief.summary); brief.drills.forEachIndexed { i, drill -> Text("${i + 1}. $drill") }
                                    Text("Model: ${brief.model} · ${brief.sourceIds.size} saved sources")
                                    TextButton(onClick = { model.claimAudio { speak(brief.summary) } }) { Text("Read brief aloud in active conversation") }
                                }
                                if (run.status in listOf(WorkflowStatus.Queued, WorkflowStatus.Running, WorkflowStatus.RetryScheduled))
                                    OutlinedButton(onClick = { model.cancel(run) }, enabled = !state.busy) { Text("Cancel workflow") }
                                TextButton(onClick = { deleteRun = run }, enabled = !state.busy) { Text("Delete workflow result") }
                            } }
                        }
                        Text("Due learning", style = MaterialTheme.typography.titleLarge)
                        state.learning.forEach { item ->
                            var reveal by remember(item.id) { mutableStateOf(false) }
                            Card { Column(Modifier.padding(16.dp)) {
                                Text(item.prompt); TextButton(onClick = { reveal = !reveal }) { Text(if (reveal) "Hide answer" else "Reveal answer") }
                                if (reveal) { Text(item.answer); ReviewRating.entries.forEach { rating -> TextButton(onClick = { model.review(item, rating) }, enabled = !state.busy) { Text(rating.name) } } }
                            } }
                        }
                    }
                    4 -> {
                        var goals by remember { mutableStateOf(true) }
                        FilterChip(selected = goals, onClick = { goals = true }, label = { Text("Goals") })
                        FilterChip(selected = !goals, onClick = { goals = false }, label = { Text("Programme & agents · all modules") })
                        if (goals) GoalsScreen(state, model) else ModulesScreen(state, model)
                    }
                }
            }
        }
    }
}

@Composable
private fun ConnectionForm(busy: Boolean, connect: (String, String) -> Unit) {
    var url by remember { mutableStateOf("") }; var token by remember { mutableStateOf("") }
    Text("One JARVIS. Wherever you are.", style = MaterialTheme.typography.headlineMedium)
    Text("Connect to your Core server using a trusted HTTPS address. This phone's localhost is not your Windows server. A private VPN with HTTPS is recommended for away-from-home use.")
    OutlinedTextField(url, { url = it }, label = { Text("HTTPS Core server address") }, modifier = Modifier.fillMaxWidth(), singleLine = true)
    OutlinedTextField(token, { token = it }, label = { Text("Core API key · personal test connection") }, modifier = Modifier.fillMaxWidth(), visualTransformation = PasswordVisualTransformation(), singleLine = true)
    Button(onClick = { connect(url, token); token = "" }, enabled = !busy, modifier = Modifier.fillMaxWidth()) { Text("Connect securely") }
    Text("Your server key is encrypted with Android Keystore. Claude/OpenAI keys are configured only on Core. Microphone permission is requested only when you start recording.")
}

@Composable
private fun StoriesScreen(state: ScreenState, model: JarvisViewModel) {
    var editing by remember { mutableStateOf(false) }; var original by remember { mutableStateOf<Story?>(null) }
    var draft by remember { mutableStateOf(StoryContent()) }; var delete by remember { mutableStateOf<Story?>(null) }
    Text("Your real stories.", style = MaterialTheme.typography.headlineMedium)
    if (editing) {
        Text("Drafts may be incomplete. Add facts you can defend; leave unknown metrics blank.")
        val fields = listOf(
            Triple("Title", draft.title) { value: String -> draft = draft.copy(title = value) },
            Triple("Company / project", draft.companyProject) { value: String -> draft = draft.copy(companyProject = value) },
            Triple("Exact service / feature", draft.serviceFeature) { value: String -> draft = draft.copy(serviceFeature = value) },
            Triple("Context", draft.context) { value: String -> draft = draft.copy(context = value) },
            Triple("Problem", draft.problem) { value: String -> draft = draft.copy(problem = value) },
            Triple("Constraints", draft.constraints) { value: String -> draft = draft.copy(constraints = value) },
            Triple("Personal ownership", draft.personalOwnership) { value: String -> draft = draft.copy(personalOwnership = value) },
            Triple("Technical decisions", draft.technicalDecisions) { value: String -> draft = draft.copy(technicalDecisions = value) },
            Triple("Trade-offs", draft.tradeOffs) { value: String -> draft = draft.copy(tradeOffs = value) },
            Triple("Implementation", draft.implementation) { value: String -> draft = draft.copy(implementation = value) },
            Triple("Collaboration", draft.collaboration) { value: String -> draft = draft.copy(collaboration = value) },
            Triple("Failure modes", draft.failureModes) { value: String -> draft = draft.copy(failureModes = value) },
            Triple("Outcome", draft.result) { value: String -> draft = draft.copy(result = value) },
            Triple("Verified metrics", draft.metrics) { value: String -> draft = draft.copy(metrics = value) },
            Triple("What I would change today", draft.changeToday) { value: String -> draft = draft.copy(changeToday = value) }
        )
        fields.forEach { (label, value, update) -> OutlinedTextField(value, update, label = { Text(label) }, modifier = Modifier.fillMaxWidth()) }
        OutlinedTextField(draft.followUpQuestions.orEmpty().joinToString("\n"), { draft = draft.copy(followUpQuestions = it.lines().filter(String::isNotBlank)) }, label = { Text("Follow-up questions · one per line") }, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(draft.cvClaims.orEmpty().joinToString("\n"), { draft = draft.copy(cvClaims = it.lines().filter(String::isNotBlank)) }, label = { Text("CV claims · one per line") }, modifier = Modifier.fillMaxWidth())
        Button(onClick = { model.saveStory(original?.id, original?.version ?: 0, draft) { editing = false } }, enabled = !state.busy) { Text("Save reviewed story") }
        Text("On a version conflict your draft remains here. Copy it before leaving, reload the server version, then merge your changes.")
        OutlinedButton(onClick = { editing = false }) { Text("Discard local edit") }
    } else {
        Button(onClick = { original = null; draft = StoryContent(); editing = true }) { Text("Add a story") }
        state.stories.forEach { story -> Card { Column(Modifier.padding(16.dp)) {
            Text(story.content.title, style = MaterialTheme.typography.titleLarge)
            Text("${story.content.companyProject} · ${story.content.serviceFeature}")
            Text(story.content.personalOwnership.ifBlank { "Readiness gap: add your personal ownership." })
            TextButton(onClick = { original = story; draft = story.content; editing = true }) { Text("Read and edit · version ${story.version}") }
            TextButton(onClick = { delete = story }) { Text("Delete story") }
        } } }
    }
    delete?.let { story -> AlertDialog(onDismissRequest = { delete = null }, title = { Text("Delete this story?") },
        text = { Text("This removes the canonical story from Core. This action affects every connected device.") },
        confirmButton = { TextButton(onClick = { model.deleteStory(story); delete = null }) { Text("Delete") } },
        dismissButton = { TextButton(onClick = { delete = null }) { Text("Keep story") } }) }
}
