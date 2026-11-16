package uk.jarvis.mobile

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import uk.jarvis.mobile.contracts.*

private fun domainLabel(domain: LearningDomain) = when (domain) {
    LearningDomain.CSharpDotNet -> "C# & .NET"
    LearningDomain.DistributedSystems -> "Distributed systems"
    LearningDomain.ApiSecurity -> "API & security"
    LearningDomain.ProductionDevOps -> "Production & DevOps"
    LearningDomain.PaymentsFintech -> "Payments & fintech"
    LearningDomain.SystemDesign -> "System design"
    else -> domain.name
}

@Composable
fun TrainingScreen(state: ScreenState, model: JarvisViewModel) {
    var domain by remember { mutableStateOf(LearningDomain.CSharpDotNet) }
    var mode by remember { mutableStateOf(TrainingMode.RapidDrill) }
    Text("Build your recall.", style = MaterialTheme.typography.headlineMedium)
    Text("16 focused questions · 8 domains", color = MaterialTheme.colorScheme.primary)
    Text("Answer first, compare with the reference, then assess your recall. This practice works without an AI key.")
    GuidedTrainingCard(state, model)
    TextButton(onClick = model::refresh, enabled = !state.busy) { Text("Refresh saved practice") }
    val due = state.questions.filter { question -> state.learning.any { it.id == question.id } }
    if (due.isNotEmpty()) {
        Text("Ready to revisit", style = MaterialTheme.typography.titleMedium)
        due.forEach { question -> AssistChip(onClick = { if (!state.busy) { domain = question.domain; mode = TrainingMode.SpacedReview; model.chooseDrill(question) } }, label = { Text("Recall: ${question.title}") }) }
    }
    state.questions.map { it.domain }.distinct().chunked(2).forEach { pair ->
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) { pair.forEach { item ->
            FilterChip(domain == item, onClick = { domain = item }, label = { Text(domainLabel(item)) }, modifier = Modifier.weight(1f))
        } }
    }
    state.questions.filter { it.domain == domain }.forEach { question ->
        OutlinedCard(onClick = { if (!state.busy) model.chooseDrill(question) }, modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(question.title, style = MaterialTheme.typography.titleMedium)
                Text(if (state.drillQuestion?.id == question.id) "Selected · continue below" else "Open drill →", color = MaterialTheme.colorScheme.primary)
            }
        }
    }
    state.drillQuestion?.let { question ->
        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(domainLabel(question.domain), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
            Text(question.title, style = MaterialTheme.typography.titleLarge)
            listOf(TrainingMode.RapidDrill, TrainingMode.ExplainSimply, TrainingMode.SpacedReview).forEach { option -> FilterChip(mode == option, onClick = { mode = option }, label = {
                Text(when (option) { TrainingMode.RapidDrill -> "Rapid drill"; TrainingMode.ExplainSimply -> "Explain simply"; else -> "Recall review" })
            }) }
            Text(when (mode) { TrainingMode.RapidDrill -> "Aim for a concise 20–40 second explanation. Timing is not measured from text."
                TrainingMode.ExplainSimply -> "Explain this to a junior colleague, defining any jargon."
                else -> "Recall the answer before looking at the reference." })
            Text(question.prompt, fontWeight = FontWeight.SemiBold)
            val attempt = state.drillAttempt
            OutlinedTextField(state.drillDraft, model::editDrill, label = { Text("Your answer") }, minLines = 5, modifier = Modifier.fillMaxWidth(), enabled = !state.busy && attempt?.skipped != true)
            if (attempt == null) {
                Button(onClick = { model.saveDrill(mode) }, enabled = !state.busy && state.drillDraft.isNotBlank()) { Text("Save answer") }
                TextButton(onClick = { model.saveDrill(mode, true) }, enabled = !state.busy) { Text("Skip without scoring") }
            } else {
                Text(if (attempt.skipped) "Skipped · no mastery penalty" else "Saved · answer v${attempt.textRevision} · record v${attempt.revision}")
                if (!attempt.skipped) {
                    OutlinedButton(onClick = model::correctDrill, enabled = !state.busy && state.drillDraft.isNotBlank() && state.drillDraft.trim() != attempt.currentAnswer) { Text("Save correction & clear assessment") }
                    var reference by remember(attempt.id) { mutableStateOf(false) }
                    TextButton(onClick = { reference = !reference }) { Text(if (reference) "Hide reference" else "Compare with reference") }
                    if (reference) {
                        Text(question.reference)
                        Text("Follow-up prompts", fontWeight = FontWeight.Bold)
                        question.followUps.forEach { Text("• $it") }
                        Text("Source: ${question.sourceUrl}", style = MaterialTheme.typography.bodySmall)
                    }
                    HorizontalDivider()
                    Text("SELF ASSESSMENT", color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.labelLarge)
                    AiFeedbackButton(state, model, true)
                    Text("Your rating, not an AI evaluation. Choose technical accuracy: 0 = missed, 1 = limited, 2 = partial, 3 = strong, 4 = complete.")
                    var score by remember(attempt.id, attempt.revision) { mutableStateOf<Int?>(null) }
                    var evidence by remember(attempt.id, attempt.revision) { mutableStateOf("") }
                    var rationale by remember(attempt.id, attempt.revision) { mutableStateOf("") }
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) { (0..4).forEach { value ->
                        FilterChip(score == value, onClick = { score = value }, label = { Text("$value") })
                    } }
                    TextButton(onClick = { score = null }) { Text("Leave unrated") }
                    OutlinedTextField(evidence, { evidence = it.take(512) }, label = { Text("Exact excerpt from your saved answer") }, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(rationale, { rationale = it.take(512) }, label = { Text("Why this rating?") }, modifier = Modifier.fillMaxWidth())
                    Button(onClick = { model.assessDrill(score, evidence, rationale) }, enabled = !state.busy && state.drillDraft.trim() == attempt.currentAnswer && (score == null || rationale.isNotBlank()) && (score !in listOf(3, 4) || evidence.isNotBlank())) { Text("Save self-assessment") }
                    attempt.canonicalAssessment?.let { Text("Saved ${it.source} rating: ${it.contentScore?.toString() ?: "unrated"} / 4 · ${it.rubricVersion}") }
                    Text("${attempt.answers.size} answer version(s) · ${attempt.assessments.size} assessment version(s)", style = MaterialTheme.typography.bodySmall)
                    var history by remember(attempt.id) { mutableStateOf(false) }
                    TextButton(onClick = { history = !history }) { Text(if (history) "Hide saved versions" else "Inspect saved versions") }
                    if (history) {
                        attempt.answers.forEach { version -> Text("Answer v${version.revision} · ${version.source}\n${version.text}") }
                        attempt.assessments.forEach { assessment ->
                            Text("${assessment.source} assessment v${assessment.version} · answer v${assessment.textRevision} · ${if (assessment.id == attempt.canonicalAssessmentId) "current" else "historical"}")
                            assessment.scores.forEach { item -> Text("${item.dimension}: ${item.value?.toString() ?: "unrated"}\n${item.evidence.orEmpty()}\n${item.rationale.orEmpty()}") }
                        }
                    }
                    state.trainingCards.firstOrNull { it.id == question.id }?.let { Text("Next review: ${it.dueAt}", style = MaterialTheme.typography.bodySmall) }
                }
                OutlinedButton(onClick = model::freshDrill, enabled = !state.busy) { Text("Start another attempt") }
                var confirmDelete by remember { mutableStateOf(false) }
                TextButton(onClick = { confirmDelete = true }, enabled = !state.busy) { Text("Delete saved attempt") }
                if (confirmDelete) AlertDialog(onDismissRequest = { confirmDelete = false }, title = { Text("Delete this attempt?") }, text = { Text("Its saved answer and assessments will be removed. Core recalculates the review schedule.") }, confirmButton = { TextButton(onClick = { confirmDelete = false; model.deleteDrill() }) { Text("Delete") } }, dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Keep") } })
            }
        } }
    }
}

@Composable
fun TrainingProgress(dashboard: TrainingDashboard?) {
    dashboard?.let { value ->
        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Technical practice that counts.", style = MaterialTheme.typography.titleLarge)
            Text("${value.completedAttempts} technical answers · ${value.practiceDays} practice days · ${value.dueQuestions} due")
            Text("${value.skippedAttempts} skipped · calendar: ${value.timeZone}", style = MaterialTheme.typography.bodySmall)
            Text("Rated domains · weakest first", fontWeight = FontWeight.SemiBold)
            if (value.weakDomains.isEmpty()) Text("Save a self-assessment to begin tracking recall.")
            value.weakDomains.forEach { item ->
                Text("${domainLabel(item.domain)} · ${"%.1f".format(item.meanScore)} / 4 · ${item.ratedQuestions} questions · ${item.assessmentSource}")
                LinearProgressIndicator(progress = { (item.meanScore / 4).toFloat().coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
            }
        } }
    }
}
