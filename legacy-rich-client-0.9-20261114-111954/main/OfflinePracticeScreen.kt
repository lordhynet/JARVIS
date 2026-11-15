package uk.jarvis.mobile

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

@Composable
fun OfflinePracticeCard(state: ScreenState, model: JarvisViewModel) {
    var expanded by remember { mutableStateOf(false) }
    var discard by remember { mutableStateOf<String?>(null) }
    if (!state.offline && state.pendingWrites.isEmpty()) return
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(if (state.offline) "Offline practice" else "Offline saves", style = MaterialTheme.typography.titleMedium)
        Text("Drafts and cached practice data are encrypted on this phone. Cached results may be stale. Paid AI and audio are never replayed by this queue.")
        if (state.offline) Text("Remote erasure and access changes cannot reach this phone while offline. Core privacy is checked again when you reconnect.")
        Text("${state.pendingWrites.size} pending or conflicted saves")
        OutlinedButton(onClick = { expanded = !expanded }) { Text(if (expanded) "Hide queued saves" else "Review queued saves") }
        OutlinedButton(onClick = model::reloadPractice, enabled = !state.busy) { Text("Load latest server versions; keep drafts") }
        if (expanded) state.pendingWrites.forEach { operation ->
            HorizontalDivider()
            Text("${operation.method} ${operation.path}", style = MaterialTheme.typography.labelLarge)
            Text(operation.status + if (operation.detail.isNotBlank()) ": ${operation.detail}" else " · waiting for your retry")
            var show by remember(operation.id) { mutableStateOf(false) }
            TextButton(onClick = { show = !show }) { Text(if (show) "Hide preserved payload" else "Show preserved payload for comparison") }
            if (show) SelectionContainer { Text(operation.json, style = MaterialTheme.typography.bodySmall) }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = { model.retryPractice(operation.id) }, enabled = !state.busy && operation.status == "Pending") { Text("Retry same save") }
                TextButton(onClick = { discard = operation.id }, enabled = !state.busy) { Text("Discard operation") }
            }
        }
    } }
    discard?.let { id -> AlertDialog(onDismissRequest = { discard = null }, title = { Text("Discard queued save?") },
        text = { Text("This removes only the local operation and its preserved payload. It does not undo a server save. Compare or copy the payload before discarding if you need it.") },
        confirmButton = { TextButton(onClick = { model.discardPractice(id); discard = null }) { Text("Discard operation") } },
        dismissButton = { TextButton(onClick = { discard = null }) { Text("Keep save") } }) }
}
