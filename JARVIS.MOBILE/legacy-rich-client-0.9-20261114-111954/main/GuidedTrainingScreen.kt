package uk.jarvis.mobile

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import uk.jarvis.mobile.contracts.*

@Composable
fun GuidedTrainingCard(state: ScreenState, model: JarvisViewModel) {
    var mode by remember { mutableStateOf(TrainingMode.RapidDrill) }
    var menu by remember { mutableStateOf(false) }
    var cancel by remember { mutableStateOf(false) }
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("Guided drills · nine modes", style = MaterialTheme.typography.titleLarge)
        Box {
            OutlinedButton(onClick = { menu = true }, enabled = !state.busy) { Text(state.drillModes.firstOrNull { it.mode == mode }?.title ?: mode.name) }
            DropdownMenu(menu, onDismissRequest = { menu = false }) {
                state.drillModes.forEach { item -> DropdownMenuItem(text = { Text(item.title) }, onClick = { mode = item.mode; menu = false }) }
            }
        }
        state.drillModes.firstOrNull { it.mode == mode }?.let { Text(it.description) }
        Button(onClick = { model.startGuided(mode) }, enabled = !state.busy && state.drillModes.isNotEmpty()) { Text("Start guided drill") }
        state.guided?.takeIf { it.drill.sessionId == state.selected?.id }?.let { view ->
            val plan = view.drill
            Text("${plan.mode} · ${plan.status}", style = MaterialTheme.typography.titleMedium)
            Text("${plan.attemptIds.size}/${plan.steps.size} stages answered or skipped. Completion is not a mastery grade.")
            if (plan.status == "Active") {
                val step = plan.steps[plan.attemptIds.size]
                Text("Stage ${step.index + 1}: ${step.title}", style = MaterialTheme.typography.titleMedium)
                Text(step.prompt)
                step.suggestedSeconds?.let { Text("Target: 20–${it}s. Text does not measure duration.") }
                step.codeSnippet?.let { code -> SelectionContainer { Text(code, fontFamily = FontFamily.Monospace) } }
                val draft = state.guidedDrafts["${plan.id}:${step.index}"].orEmpty()
                OutlinedTextField(draft, model::editGuided, label = { Text("Guided answer") }, minLines = 5, modifier = Modifier.fillMaxWidth(), enabled = !state.busy)
                Button(onClick = { model.saveGuided() }, enabled = !state.busy && draft.isNotBlank()) { Text("Save guided stage") }
                TextButton(onClick = { model.saveGuided(true) }, enabled = !state.busy) { Text("Skip stage without a score") }
                OutlinedButton(onClick = model::reloadGuided, enabled = !state.busy) { Text("Reload guided progress") }
                TextButton(onClick = { cancel = true }, enabled = !state.busy) { Text("Cancel guided drill") }
            }
            view.answers.filter { !it.skipped }.forEach { answer ->
                var reveal by remember(answer.id) { mutableStateOf(false) }
                TextButton(onClick = { reveal = !reveal }) { Text("Saved stage ${(answer.stepIndex ?: 0) + 1} · ${answer.canonicalAssessment?.source ?: "unrated"}") }
                if (reveal) {
                    SelectionContainer { Text(answer.currentAnswer) }
                    answer.stepIndex?.let { Text("Reference: ${plan.steps[it].reference}") }
                    OutlinedButton(onClick = { model.openTrainingAttempt(answer.id) }, enabled = !state.busy) { Text("Review this saved answer below") }
                }
            }
            state.guidedDrafts.filter { (key, text) -> key.startsWith("${plan.id}:") && (plan.status != "Active" || key != "${plan.id}:${plan.attemptIds.size}") &&
                text.isNotBlank() && text.trim() != view.answers.firstOrNull { it.stepIndex == key.substringAfterLast(':').toIntOrNull() }?.currentAnswer }.forEach { (key, text) ->
                Text("Preserved local draft · stage ${(key.substringAfterLast(':').toIntOrNull() ?: 0) + 1}", style = MaterialTheme.typography.labelLarge)
                Text("The server moved past this stage. Compare this local text with the saved version; it has not been uploaded automatically.")
                SelectionContainer { Text(text) }
            }
        }
        Text("Saved in this session", style = MaterialTheme.typography.labelLarge)
        state.guidedHistory.filter { it.drill.sessionId == state.selected?.id }.forEach { view -> TextButton(onClick = { model.resumeGuided(view) }, enabled = !state.busy) {
            Text("${view.drill.mode} · ${view.drill.status} · ${view.drill.attemptIds.size}/${view.drill.steps.size}")
        } }
    } }
    if (cancel) AlertDialog(onDismissRequest = { cancel = false }, title = { Text("Cancel this drill?") },
        text = { Text("Further stages are blocked. Saved answers and their review history remain available.") },
        confirmButton = { TextButton(onClick = { model.cancelGuided(); cancel = false }) { Text("Cancel drill") } },
        dismissButton = { TextButton(onClick = { cancel = false }) { Text("Keep practising") } })
}
