package uk.jarvis.mobile

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

@Composable
fun LiveVoiceCard(state: ScreenState, voice: LiveVoiceState, start: () -> Unit, stop: () -> Unit) {
    var consent by remember { mutableStateOf(false) }
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("Your live voice coach", style = MaterialTheme.typography.titleLarge)
        Text(if (voice.active) voice.message else state.voice?.detail ?: "Checking live voice configuration…")
        if (!voice.active) {
            Row { Checkbox(consent, { consent = it }); Text("Allow live audio and selected session context to be processed by OpenAI for this session.", Modifier.weight(1f)) }
            Button(onClick = start, enabled = consent && !state.busy && state.voice?.ready == true) { Text("Start live voice") }
            Text("Test microphone and speaker once in Settings, then use this button. A visible foreground notification and headset Pause/Stop controls remain available.", style = MaterialTheme.typography.bodySmall)
        } else {
            Text(if (voice.ready) "MICROPHONE ACTIVE" else "VOICE CONNECTING", color = MaterialTheme.colorScheme.primary)
            Button(onClick = stop) { Text("Stop live microphone & playback") }
            if (voice.transcript.isNotBlank()) Text(voice.transcript.takeLast(2000))
        }
    } }
}

@Composable
fun AiFeedbackButton(state: ScreenState, model: JarvisViewModel, technical: Boolean) {
    var consent by remember { mutableStateOf(false) }
    Text("AI coaching", style = MaterialTheme.typography.titleMedium)
    Text(state.aiCoach?.detail ?: "Checking AI configuration…", style = MaterialTheme.typography.bodySmall)
    Row { Checkbox(consent, { consent = it }); Text("Allow the saved answer and selected reference context to be processed for AI feedback.", Modifier.weight(1f)) }
    Button(onClick = { model.assessWithAi(technical, consent) }, enabled = !state.busy && consent && state.aiCoach?.ready == true) { Text("Request AI assessment") }
    Text("AI feedback is evidence-checked by Core, but still needs your review. It is separate from measured speech data and self ratings.", style = MaterialTheme.typography.bodySmall)
}

@Composable
fun AgentPanel(state: ScreenState, model: JarvisViewModel) {
    Text("Proactive agents", style = MaterialTheme.typography.titleLarge)
    Text("Scoped workflows, durable schedules and a private notification inbox. Cloud work requires your consent and Core budget readiness.")
    val workflows = listOf("interview-preparation", "daily-coaching", "research-review", "career-plan", "personal-plan", "practice-reminder")
    var kind by remember { mutableStateOf(workflows[0]) }; var menu by remember { mutableStateOf(false) }
    Box { OutlinedButton(onClick = { menu = true }) { Text(kind) }; DropdownMenu(menu, onDismissRequest = { menu = false }) { workflows.forEach { value -> DropdownMenuItem(text = { Text(value) }, onClick = { kind = value; menu = false }) } } }
    var brief by remember { mutableStateOf("") }; var consent by remember { mutableStateOf(false) }; var time by remember { mutableStateOf("09:00") }
    OutlinedTextField(brief, { brief = it.take(2000) }, label = { Text("Scoped goal / preparation brief") }, modifier = Modifier.fillMaxWidth())
    Row { Checkbox(consent, { consent = it }); Text("Allow cloud processing for this workflow's selected sources.", Modifier.weight(1f)) }
    Button(onClick = { model.startAgent(kind, brief, consent) }, enabled = !state.busy) { Text("Start durable workflow") }
    OutlinedTextField(time, { time = it.take(5) }, label = { Text("Daily time · HH:mm · Europe/London") })
    OutlinedButton(onClick = { model.saveDailyAgent(kind, brief.ifBlank { "Daily practice" }.take(120), time, consent) }, enabled = !state.busy) { Text("Save daily schedule") }
    state.agentControls?.let { settings -> OutlinedButton(onClick = { model.setAgentPause(!settings.paused) }, enabled = !state.busy) { Text(if (settings.paused) "Resume paid AI & agents" else "Pause all paid AI & agents") } }
    TextButton(onClick = model::refresh, enabled = !state.busy) { Text("Refresh tasks & inbox") }
    state.notifications.forEach { note -> Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(14.dp)) { Text(note.title); Text(note.createdAt, style = MaterialTheme.typography.bodySmall) } } }
    state.agentSchedules.forEach { schedule -> OutlinedButton(onClick = { model.toggleAgentSchedule(schedule) }, enabled = !state.busy) { Text("${schedule.title} · ${schedule.localTime} · ${if (schedule.enabled) "Disable" else "Enable"}") } }
    state.agentRuns.forEach { view ->
        var expanded by remember(view.run.id) { mutableStateOf(false) }
        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("${view.run.workflow} · ${view.run.status}")
            TextButton(onClick = { expanded = !expanded }) { Text(if (expanded) "Hide specialist tasks" else "Inspect specialist tasks") }
            if (expanded) view.tasks.forEach { task -> Text("${task.role} · ${task.status}"); task.output?.let { Text(it) }; task.errorCode?.let { Text(it) } }
            if (view.run.status in listOf("Queued", "Running")) TextButton(onClick = { model.cancelAgent(view.run.id, false) }, enabled = !state.busy) { Text("Cancel agent run") }
            var delete by remember { mutableStateOf(false) }
            TextButton(onClick = { delete = true }) { Text("Delete artifacts") }
            if (delete) AlertDialog(onDismissRequest = { delete = false }, title = { Text("Delete agent artifacts?") }, text = { Text("The run's private inputs and outputs are removed. Minimal billing/retry metadata remains.") }, confirmButton = { TextButton(onClick = { delete = false; model.cancelAgent(view.run.id, true) }) { Text("Delete") } }, dismissButton = { TextButton(onClick = { delete = false }) { Text("Keep") } })
        } }
    }
}
