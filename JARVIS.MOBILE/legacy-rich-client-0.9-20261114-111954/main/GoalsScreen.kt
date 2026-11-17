package uk.jarvis.mobile

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import uk.jarvis.mobile.contracts.PersonalGoal
import java.time.ZoneId

private val goalNames = mapOf("practice-reminder" to "Local practice reminder", "interview-preparation" to "Interview preparation",
    "daily-coaching" to "Daily coaching", "career-plan" to "Career plan", "personal-plan" to "Personal plan", "research-review" to "Research and review")

@Composable
private fun GoalChoice(label: String, value: String, choices: List<String>, enabled: Boolean = true, change: (String) -> Unit) {
    var expanded by remember(label) { mutableStateOf(false) }
    Box {
        OutlinedButton(onClick = { expanded = true }, enabled = enabled, modifier = Modifier.fillMaxWidth()) { Text("$label: ${goalNames[value] ?: value} ▾") }
        DropdownMenu(expanded, onDismissRequest = { expanded = false }) {
            choices.forEach { option -> DropdownMenuItem(text = { Text(goalNames[option] ?: option) }, onClick = { change(option); expanded = false }) }
        }
    }
}

@Composable
private fun GoalDetails(goal: PersonalGoal) {
    val zone = ZoneId.systemDefault()
    Text(goal.title, style = MaterialTheme.typography.titleMedium)
    Text("${goal.status} · ${goalNames[goal.workflow] ?: goal.workflow}")
    SelectionContainer { Text(goal.brief) }
    Text("Review: ${goalDisplayTime(goal.nextReviewAt, zone)} · ${zone.id}", style = MaterialTheme.typography.bodySmall)
    Text("Deadline: ${goalDisplayTime(goal.deadline, zone)}", style = MaterialTheme.typography.bodySmall)
    Text(if (goal.allowCloudProcessing && goal.workflow != "practice-reminder") "Cloud processing allowed for this goal" else "Local processing only", style = MaterialTheme.typography.bodySmall)
    if (goal.lastRunId != null) Text("Review workflow created · inspect it under Programme & agents", style = MaterialTheme.typography.bodySmall)
}

@Composable
fun GoalsScreen(state: ScreenState, model: JarvisViewModel) {
    var delete by remember { mutableStateOf<PersonalGoal?>(null) }
    var discard by remember { mutableStateOf(false) }
    var replace by remember { mutableStateOf<Boolean?>(null) }
    var filter by remember { mutableStateOf("All") }
    var limit by remember(filter) { mutableIntStateOf(25) }
    Text("Your goals", style = MaterialTheme.typography.headlineMedium)
    Text("Each saved version can create one review workflow when due. Updating progress sets its next review. Paused, completed and cancelled goals do not start new work.")
    Text("Reviews run on Core, under its pause controls and budgets. Goal changes require a connection; local drafts are encrypted on this phone.", style = MaterialTheme.typography.bodySmall)
    OutlinedButton(onClick = model::refreshGoals, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text(if (state.pendingGoalSave != null) "Check Core for unconfirmed save" else "Refresh goals from Core") }
    if (state.goalDraft == null && state.pendingGoalSave == null) Button(onClick = model::newGoal, enabled = !state.busy) { Text("New goal") }
    state.goalDraft?.let { draft ->
        val pending = state.pendingGoalSave != null
        val editable = !state.busy && !pending
        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(if (draft.expectedRevision == null) "New goal draft" else "Update goal progress", style = MaterialTheme.typography.titleLarge)
            OutlinedTextField(draft.title, { value -> model.updateGoalDraft { it.copy(title = value.take(120)) } }, label = { Text("Goal title") }, enabled = editable, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(draft.brief, { value -> model.updateGoalDraft { it.copy(brief = value.take(2000)) } }, label = { Text("Context and next step") }, enabled = editable, minLines = 3, modifier = Modifier.fillMaxWidth())
            GoalChoice("Workflow", draft.workflow, goalWorkflows, editable) { value -> model.updateGoalDraft { it.copy(workflow = value, allowCloudProcessing = if (value == "practice-reminder") false else it.allowCloudProcessing) } }
            GoalChoice("Goal state", draft.status, goalStates, editable) { value -> model.updateGoalDraft { it.copy(status = value) } }
            Text("Times use ${draft.timeZone}. Example: 2026-12-01 09:00. Active reviews due in the past can start immediately.", style = MaterialTheme.typography.bodySmall)
            OutlinedTextField(draft.reviewLocal, { value -> model.updateGoalDraft { it.copy(reviewLocal = value.take(32)) } }, label = { Text("Next review · yyyy-MM-dd HH:mm") }, enabled = editable, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(draft.deadlineLocal, { value -> model.updateGoalDraft { it.copy(deadlineLocal = value.take(32)) } }, label = { Text("Deadline · yyyy-MM-dd HH:mm") }, enabled = editable, singleLine = true, modifier = Modifier.fillMaxWidth())
            if (draft.workflow == "practice-reminder") Text("Local reminders do not call paid providers.")
            else {
                Row {
                    Checkbox(draft.allowCloudProcessing, { value -> model.updateGoalDraft { it.copy(allowCloudProcessing = value) } }, enabled = editable)
                    Text("Allow this goal's reviews to send the goal context and selected sources to configured cloud providers, subject to Core budgets.", Modifier.weight(1f))
                }
                Text(if (requiresGoalCloud(draft.workflow)) "This workflow requires cloud consent." else "Leave unchecked for a local, deterministic practice review.", style = MaterialTheme.typography.bodySmall)
            }
            if (state.goalMessage.isNotBlank()) Text(state.goalMessage, color = MaterialTheme.colorScheme.primary)
            if (pending) {
                Text("Save not yet confirmed. Fields are locked to preserve the exact request and avoid duplicate goals.")
                state.pendingGoalSave?.let { request -> SelectionContainer { Text("Unconfirmed goal: ${request.title}\n${request.brief}") } }
                OutlinedButton(onClick = model::retryGoalSave, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("Retry the same goal save") }
            } else {
                Button(onClick = model::saveGoal, enabled = !state.busy && !state.offline && !state.goalConflict, modifier = Modifier.fillMaxWidth()) { Text("Save goal to Core") }
                if (state.offline) Text("Offline edits stay on this phone. Refresh Core when reachable before saving this goal.")
                if (state.goalConflict && state.goalServerVersion == null) Text("This goal cannot currently be updated. Keep or copy your draft; saving to an absent/deleted goal is blocked.")
                OutlinedButton(onClick = { discard = true }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("Discard local goal draft") }
            }
        } }
    }
    state.goalServerVersion?.takeIf { state.goalConflict }?.let { latest ->
        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Current Core version · compare with your draft", style = MaterialTheme.typography.titleMedium)
            GoalDetails(latest)
            OutlinedButton(onClick = { replace = true }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("Keep my draft; use latest version") }
            TextButton(onClick = { replace = false }, enabled = !state.busy) { Text("Replace draft with Core version") }
        } }
    }
    if (state.goalDraft == null && state.goalMessage.isNotBlank()) Text(state.goalMessage, color = MaterialTheme.colorScheme.primary)
    GoalChoice("Show goals", filter, listOf("All") + goalStates) { filter = it }
    val visible = state.goals.filter { filter == "All" || it.status == filter }
    Text("Saved goals · ${visible.size}", style = MaterialTheme.typography.titleLarge)
    if (visible.isEmpty()) Text(if (state.offline) "No cached goals in this view. Connect and refresh to check Core." else "No saved goals in this view. Start with one specific next step.")
    visible.take(limit).forEach { goal ->
        key(goal.id) { Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            GoalDetails(goal)
            OutlinedButton(onClick = { model.editGoal(goal) }, enabled = !state.busy && state.goalDraft == null && state.pendingGoalSave == null, modifier = Modifier.fillMaxWidth()) { Text("Update goal progress") }
            TextButton(onClick = { delete = goal }, enabled = !state.busy && !state.offline && state.pendingGoalSave == null) { Text("Delete goal context") }
        } } }
    }
    if (visible.size > limit) TextButton(onClick = { limit += 25 }) { Text("Show more goals · ${visible.size - limit} remaining") }
    if (discard) AlertDialog(onDismissRequest = { discard = false }, title = { Text("Discard this local draft?") },
        text = { Text("Unsaved goal text and date edits on this phone will be removed. The saved Core goal is unchanged.") },
        confirmButton = { TextButton(onClick = { model.discardGoalDraft(); discard = false }) { Text("Discard local draft") } },
        dismissButton = { TextButton(onClick = { discard = false }) { Text("Keep editing") } })
    replace?.let { keep -> AlertDialog(onDismissRequest = { replace = null }, title = { Text(if (keep) "Keep your draft for the new version?" else "Replace your local draft?") },
        text = { Text(if (keep) "All your draft fields, including cloud consent and dates, will be kept. Review them before saving over the current Core version." else "Your unsaved fields will be replaced by the displayed Core version.") },
        confirmButton = { TextButton(onClick = { model.useLatestGoal(keep); replace = null }) { Text(if (keep) "Keep draft for review" else "Replace draft") } },
        dismissButton = { TextButton(onClick = { replace = null }) { Text("Keep comparing") } }) }
    delete?.let { goal -> AlertDialog(onDismissRequest = { delete = null }, title = { Text("Delete this goal?") },
        text = { Text("Delete “${goal.title}” from Core and stop its linked review work? Its context and linked review task results are removed. A changed goal must be reviewed again before deletion.") },
        confirmButton = { TextButton(onClick = { model.deleteGoal(goal); delete = null }) { Text("Delete goal context") } },
        dismissButton = { TextButton(onClick = { delete = null }) { Text("Keep goal") } }) }
}
