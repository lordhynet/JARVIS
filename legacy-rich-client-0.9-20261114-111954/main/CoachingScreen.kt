package uk.jarvis.mobile

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import uk.jarvis.mobile.contracts.*
import java.time.LocalDate

@Composable
fun CoachingScreen(state: ScreenState, model: JarvisViewModel) {
    var area by remember { mutableStateOf(CoachingArea.Communication) }
    var selected by remember { mutableStateOf<CoachingExercise?>(null) }
    var story by remember { mutableStateOf<Story?>(null) }
    var claim by remember { mutableStateOf<Int?>(null) }
    Text("Clearer answers. Real stories.", style = MaterialTheme.typography.headlineMedium)
    Text("Pause → point → supporting detail → stop", color = MaterialTheme.colorScheme.primary)
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) { CoachingArea.entries.forEach { item ->
        FilterChip(area == item, onClick = { area = item; selected = null; story = null; claim = null }, label = { Text(item.name) })
    } }
    var exerciseMenu by remember { mutableStateOf(false) }
    Box {
        OutlinedButton(onClick = { exerciseMenu = true }) { Text(selected?.title ?: "Choose an exercise") }
        DropdownMenu(exerciseMenu, onDismissRequest = { exerciseMenu = false }) {
            state.exercises.filter { it.area == area }.forEach { exercise -> DropdownMenuItem(text = { Text(exercise.title) }, onClick = { selected = exercise; story = null; claim = null; exerciseMenu = false }) }
        }
    }
    selected?.let { exercise ->
        if (exercise.requiresStory) {
            var storyMenu by remember { mutableStateOf(false) }
            Box {
                OutlinedButton(onClick = { storyMenu = true }) { Text(story?.content?.title ?: "Choose a real saved story") }
                DropdownMenu(storyMenu, onDismissRequest = { storyMenu = false }) {
                    state.stories.forEach { item -> DropdownMenuItem(text = { Text(item.content.title) }, onClick = { story = item; claim = null; storyMenu = false }) }
                }
            }
            if (state.stories.isEmpty()) Text("Add a real story in Story Bank first. JARVIS does not invent your experience.")
        }
        if (exercise.requiresClaim) {
            val claims = story?.content?.cvClaims.orEmpty()
            if (claims.isEmpty()) Text("Add a defensible CV claim to this story first.")
            claims.forEachIndexed { index, text -> FilterChip(claim == index, onClick = { claim = index }, label = { Text(text) }) }
        }
        Button(onClick = { model.chooseCoach(exercise, story, claim) }, enabled = !state.busy && (!exercise.requiresStory || story != null) && (!exercise.requiresClaim || claim != null)) { Text("Open coaching prompt") }
    }
    val exercise = state.coachAttempt?.exercise ?: state.coachPrompt?.exercise
    exercise?.let { current ->
        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(current.title, style = MaterialTheme.typography.titleLarge)
            Text(state.coachPrompt?.question ?: current.prompt)
            Text(current.framework.joinToString(" → "), color = MaterialTheme.colorScheme.primary)
            current.targetSeconds?.let { Text("Practice target: $it seconds. This target is not a measured duration.", style = MaterialTheme.typography.bodySmall) }
            OutlinedTextField(state.coachDraft, model::editCoach, label = { Text("Your answer") }, minLines = 5, modifier = Modifier.fillMaxWidth(), enabled = !state.busy)
            OutlinedTextField(state.coachDuration, model::editCoachDuration, label = { Text("Elapsed seconds · optional, reported by you") }, modifier = Modifier.fillMaxWidth(), singleLine = true)
            Text("Leave timing blank for typed answers. A reported duration includes pauses; the resulting pace estimate is not an acoustic measurement.", style = MaterialTheme.typography.bodySmall)
            if (state.coachAttempt == null) Button(onClick = model::saveCoach, enabled = !state.busy && state.coachDraft.isNotBlank() && state.coachPrompt != null) { Text("Save & review text") }
            state.coachAttempt?.let { view ->
                if (!view.sourceCurrent) Text("Source story changed. Start a fresh prompt with the current facts.", color = MaterialTheme.colorScheme.error)
                OutlinedButton(onClick = model::correctCoach, enabled = !state.busy && view.sourceCurrent && state.coachDraft.isNotBlank()) { Text("Save correction") }
                Text("TEXT OBSERVATIONS · ${view.analysis.ruleVersion}", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
                AiFeedbackButton(state, model, false)
                view.aiAssessment?.let { ai -> Text("AI feedback · ${ai.model}"); Text(ai.summary); ai.followUps.forEach { Text("• $it") }; ai.scores.forEach { Text("${it.dimension}: ${it.value?.toString() ?: "unavailable"} · ${it.rationale}") } }
                Text("${view.analysis.wordCount} words · ${view.analysis.fillerCandidates} filler candidates")
                Text(view.analysis.approximateWordsPerMinute?.let { "Approximate $it words/minute · reported elapsed time" } ?: "Pace, pauses and confidence: unavailable from text alone.")
                view.analysis.observations.forEach { observation ->
                    Text(observation.detail)
                    if (observation.evidence.isNotEmpty()) Text("Text evidence: ${observation.evidence.joinToString(" · ")}", style = MaterialTheme.typography.bodySmall)
                }
                Text("Try next", fontWeight = FontWeight.Bold)
                view.analysis.nextDrills.forEach { Text("• $it") }
                Text("Follow-up questions", fontWeight = FontWeight.Bold)
                current.followUps.forEach { Text("• $it") }
                var history by remember(view.attempt.id) { mutableStateOf(false) }
                TextButton(onClick = { history = !history }) { Text(if (history) "Hide saved versions" else "Inspect saved versions") }
                if (history) view.attempt.answers.forEach { answer -> Text("Version ${answer.version}\n${answer.text}") }
                var delete by remember { mutableStateOf(false) }
                TextButton(onClick = { delete = true }) { Text("Delete saved answer") }
                if (delete) AlertDialog(onDismissRequest = { delete = false }, title = { Text("Delete this coaching answer?") }, text = { Text("All its saved versions are removed from Core. Minimal retry metadata remains.") }, confirmButton = { TextButton(onClick = { delete = false; model.deleteCoach() }) { Text("Delete") } }, dismissButton = { TextButton(onClick = { delete = false }) { Text("Keep") } })
                OutlinedButton(onClick = model::freshCoach, enabled = !state.busy) { Text("New answer · choose prompt if needed") }
            }
        } }
    }
    if (state.coachHistory.isNotEmpty()) Text("Saved in this session", style = MaterialTheme.typography.titleMedium)
    state.coachHistory.forEach { attempt -> TextButton(onClick = { model.openCoach(attempt.id) }, enabled = !state.busy) { Text("${attempt.title} · ${attempt.wordCount} words →") } }
}

@Composable
fun SessionReviewPanel(state: ScreenState, model: JarvisViewModel) {
    if (state.selected != null) {
        OutlinedButton(onClick = model::sessionReview, enabled = !state.busy) { Text("Review current session") }
        state.review?.let { review -> Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(review.title, style = MaterialTheme.typography.titleLarge)
            Text("${review.technicalAttempts} technical answers · ${review.coachingAttempts} coaching answers · ${review.skippedQuestions} skipped")
            Text(review.assessmentSource, style = MaterialTheme.typography.bodySmall)
            review.selfRatedDomains.forEach { Text("${it.domain}: ${"%.1f".format(it.meanScore)} / 4 self-rated") }
            review.textObservations.forEach { Text(it.detail) }
            Text("Next practice", fontWeight = FontWeight.Bold)
            review.nextDrills.forEach { Text("• $it") }
        } } }
    }
}

@Composable
fun ModulesScreen(state: ScreenState, model: JarvisViewModel) {
    Text("One product. Shared progress.", style = MaterialTheme.typography.headlineMedium)
    Text("Available features and remaining work are listed separately for every module.")
    TextButton(onClick = model::refresh, enabled = !state.busy) { Text("Refresh modules & programme") }
    AgentPanel(state, model)
    state.programme?.let { view ->
        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Your 12-week programme", style = MaterialTheme.typography.titleLarge)
            Text(view.progressSource, style = MaterialTheme.typography.bodySmall)
            val programme = view.programme
            if (programme == null) {
                var date by remember { mutableStateOf(LocalDate.now().toString()) }
                OutlinedTextField(date, { date = it.take(10) }, label = { Text("Start date · yyyy-MM-dd") })
                Button(onClick = { model.startProgramme(date) }, enabled = !state.busy) { Text("Start programme") }
            } else {
                Text(when { view.currentWeek == 0 -> "Starts ${programme.localStartDate}"; view.currentWeek > 12 -> "Twelve-week calendar completed"; else -> "Week ${view.currentWeek} of 12" })
                Text("${programme.checkIns.size} weekly self check-ins · ${programme.timeZone}")
                var week by remember(view.currentWeek) { mutableIntStateOf(view.currentWeek.coerceIn(1, 12)) }
                view.weeks.forEach { item ->
                    var expanded by remember(item.number, week) { mutableStateOf(item.number == week) }
                    TextButton(onClick = { expanded = !expanded; week = item.number }) { Text("Week ${item.number} · ${item.focus}${if (programme.checkIns.any { it.week == item.number }) " ✓" else ""}") }
                    if (expanded) item.tasks.forEach { Text("• $it") }
                }
                var note by remember(programme.id, programme.revision) { mutableStateOf("") }
                OutlinedTextField(note, { note = it.take(1000) }, label = { Text("Week $week reflection · optional") }, modifier = Modifier.fillMaxWidth())
                Button(onClick = { model.checkIn(week, note) }, enabled = !state.busy && week <= view.currentWeek && programme.checkIns.none { it.week == week }) { Text("Save weekly self check-in") }
                programme.checkIns.forEach { Text("Week ${it.week}: ${it.note.ifBlank { "Checked in" }}") }
                var delete by remember { mutableStateOf(false) }
                TextButton(onClick = { delete = true }) { Text("Delete programme & reflections") }
                if (delete) AlertDialog(onDismissRequest = { delete = false }, title = { Text("Delete this programme?") }, text = { Text("This removes its reflection notes. Your separate practice answers remain.") }, confirmButton = { TextButton(onClick = { delete = false; model.deleteProgramme() }) { Text("Delete") } }, dismissButton = { TextButton(onClick = { delete = false }) { Text("Keep") } })
            }
        } }
    }
    state.modules.forEach { module ->
        var expanded by remember(module.id) { mutableStateOf(false) }
        OutlinedCard(onClick = { expanded = !expanded }, modifier = Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(module.title, style = MaterialTheme.typography.titleMedium)
            Text(module.status, color = MaterialTheme.colorScheme.primary)
            if (expanded) {
                module.available.forEach { Text("Available · $it") }
                module.remaining.forEach { Text("Pending · $it", style = MaterialTheme.typography.bodySmall) }
            }
        } }
    }
}
