package uk.jarvis.mobile

import android.Manifest
import android.app.KeyguardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.launch
import uk.jarvis.mobile.contracts.*

/** Capture controls only: the owner prepares and manages recordings on Core. */
@Composable
internal fun RecordingControls(beforeCapture: () -> Unit) {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    val scope = rememberCoroutineScope()
    val capture by InterviewRecordingService.state.collectAsStateWithLifecycle()
    var notice by remember { mutableStateOf("") }
    var offer by remember { mutableStateOf<RecordingCaptureOffer?>(null) }
    var authorised by remember { mutableStateOf(false) }
    var storage by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        notice = "Permissions checked. Confirm again, then tap Start recording."
    }
    fun visibleUnlocked(): Boolean = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) &&
        context.getSystemService(KeyguardManager::class.java).let { !it.isDeviceLocked && !it.isKeyguardLocked }
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_STOP) { offer = null; authorised = false; storage = false } }
        lifecycle.addObserver(observer); onDispose { lifecycle.removeObserver(observer) }
    }
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        if (capture.starting || capture.recording) {
            Text("JARVIS is recording", color = JarvisColors.Red)
            Text("Microphone only · Voice and Hey JARVIS are off", color = JarvisColors.Muted)
            Button(onClick = { context.startService(Intent(context, InterviewRecordingService::class.java).setAction(InterviewRecordingService.STOP)) }) { Text("Stop recording") }
        } else {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(enabled = !busy && !capture.uploading, onClick = {
                    if (!visibleUnlocked()) { notice = "Unlock this phone first."; return@TextButton }
                    busy = true; authorised = false; storage = false
                    scope.launch {
                        try {
                            val core = RecordingCoreTransport.connect(context)
                            val result = core.send(RecordingCaptureRequest("poll", core.deviceId))
                            if (!visibleUnlocked()) return@launch
                            offer = result.offers.firstOrNull { it.status == "Prepared" && it.tracks == listOf("unknown") }
                            notice = if (offer == null) "Prepare a recording for this phone in your Core dashboard first." else ""
                        } catch (error: Exception) { notice = error.message ?: "Core is unavailable." }
                        finally { busy = false }
                    }
                }) { Text(if (busy) "Checking…" else "Record interview") }
                TextButton(enabled = !busy && !capture.uploading, onClick = {
                    if (visibleUnlocked()) context.startForegroundService(Intent(context, InterviewRecordingService::class.java).setAction(InterviewRecordingService.RESUME).putExtra(InterviewRecordingService.VISIBLE_START, true))
                    else notice = "Unlock this phone first."
                }) { Text("Resume upload") }
            }
        }
        if (capture.message.isNotBlank()) Text(capture.message, color = JarvisColors.Muted)
        if (notice.isNotBlank()) Text(notice, color = JarvisColors.Amber)
    }
    offer?.let { prepared ->
        AlertDialog(onDismissRequest = { offer = null; authorised = false; storage = false }, title = { Text("Record this interview?") },
            text = { Column {
                Text("Face-to-face interviews only. This records your phone microphone, including nearby voices. It cannot record phone call audio. A notification stays visible while recording, including when locked.")
                Row { Checkbox(checked = authorised, onCheckedChange = { authorised = it }); Text(CoreRecordingProtocol.CONSENT) }
                Row { Checkbox(checked = storage, onCheckedChange = { storage = it }); Text("I consent to encrypted storage in my Core for my own practice. Audio is removed after 30 days.") }
            } },
            confirmButton = { TextButton(enabled = authorised && storage, onClick = {
                if (!visibleUnlocked()) { notice = "Unlock this phone and confirm again."; offer = null; return@TextButton }
                val required = mutableListOf(Manifest.permission.RECORD_AUDIO)
                if (Build.VERSION.SDK_INT >= 33) required += Manifest.permission.POST_NOTIFICATIONS
                val missing = required.filter { context.checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
                if (missing.isNotEmpty()) { permission.launch(missing.toTypedArray()); return@TextButton }
                beforeCapture()
                context.startForegroundService(Intent(context, InterviewRecordingService::class.java).setAction(InterviewRecordingService.START)
                    .putExtra(InterviewRecordingService.RECORDING_ID, prepared.id).putExtra(InterviewRecordingService.AUTHORISED, true)
                    .putExtra(InterviewRecordingService.STORAGE, true).putExtra(InterviewRecordingService.VISIBLE_START, true))
                offer = null; authorised = false; storage = false
            }) { Text("Start recording") } },
            dismissButton = { TextButton(onClick = { offer = null; authorised = false; storage = false }) { Text("Cancel") } })
    }
}
