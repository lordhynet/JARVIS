package uk.jarvis.mobile

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** One dark ground, one accent; red only for recording and ending. Text contrast stays at or above 4.5:1. */
object JarvisColors {
    val Ink = Color(0xFF07090A)
    val Line = Color(0xFF1B2427)
    val Text = Color(0xFFEEF2F3)
    val Muted = Color(0xFF8D999E)
    val Mint = Color(0xFF5EE6B8)
    val MintDeep = Color(0xFF0D1615)
    val Amber = Color(0xFFF2B66D)
    val Red = Color(0xFFFF6B6B)
}

@Composable
fun JarvisTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(primary = JarvisColors.Mint, onPrimary = JarvisColors.Ink, secondary = JarvisColors.Amber,
            background = JarvisColors.Ink, surface = JarvisColors.Ink, onSurface = JarvisColors.Text, onBackground = JarvisColors.Text,
            error = JarvisColors.Red, outline = JarvisColors.Line),
        shapes = Shapes(small = RoundedCornerShape(14.dp), medium = RoundedCornerShape(22.dp), large = RoundedCornerShape(28.dp)),
        content = content
    )
}

@Composable
fun Wordmark(connected: Boolean, mode: String?, onSettings: (() -> Unit)?, settingsOpen: Boolean) {
    Row(Modifier.fillMaxWidth().statusBarsPadding().padding(start = 24.dp, end = 12.dp, top = 8.dp, bottom = 8.dp).heightIn(min = 48.dp),
        horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp),
            modifier = Modifier.semantics(mergeDescendants = true) { stateDescription = if (connected) "Connected" else "Not connected" }) {
            Box(Modifier.size(7.dp).clip(CircleShape).background(if (connected) JarvisColors.Mint else JarvisColors.Muted))
            Text("JARVIS", fontWeight = FontWeight.Medium, fontSize = 15.sp, letterSpacing = 4.sp, color = JarvisColors.Text)
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (mode != null && !settingsOpen) Text(mode, color = JarvisColors.Muted, fontSize = 12.sp, modifier = Modifier.padding(end = 4.dp))
            if (onSettings != null) TextButton(onClick = onSettings, modifier = Modifier.heightIn(min = 48.dp)) {
                Text(if (settingsOpen) "Done" else "Settings", color = JarvisColors.Muted, fontSize = 13.sp)
            }
        }
    }
}

enum class OrbState { Ready, Unavailable, Connecting, Listening, WakeListening }

/** The main control: tap to talk, tap again to stop. Calm rings breathe only while the microphone is live. */
@Composable
fun VoiceOrb(state: OrbState, enabled: Boolean, onClick: () -> Unit) {
    val live = state == OrbState.Listening || state == OrbState.Connecting
    val pulse = rememberInfiniteTransition(label = "orb")
    val breath by pulse.animateFloat(0f, 1f, infiniteRepeatable(tween(1800, easing = LinearEasing), RepeatMode.Reverse), label = "breath")
    val accent by animateColorAsState(when (state) {
        OrbState.Unavailable -> JarvisColors.Muted
        OrbState.WakeListening -> JarvisColors.Amber
        else -> JarvisColors.Mint
    }, label = "orb colour")
    val label = when (state) {
        OrbState.Ready -> "Ready"; OrbState.Unavailable -> "Not ready"; OrbState.Connecting -> "Connecting"
        OrbState.Listening -> "Listening"; OrbState.WakeListening -> "Say “Hey JARVIS”"
    }
    Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(24.dp)) {
        Box(Modifier.size(248.dp).clip(CircleShape)
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = if (live) "End conversation" else "Talk to JARVIS"; stateDescription = label },
            contentAlignment = Alignment.Center) {
            Canvas(Modifier.fillMaxSize()) {
                val center = Offset(size.width / 2, size.height / 2)
                val core = size.width * .28f
                val spread = if (live) breath * 10.dp.toPx() else 0f
                drawCircle(accent.copy(alpha = .10f), size.width / 2 - 1.dp.toPx(), center, style = Stroke(1.dp.toPx()))
                drawCircle(accent.copy(alpha = .22f), core * 1.4f + spread, center, style = Stroke(1.dp.toPx()))
                if (live) drawCircle(accent, core, center) else {
                    drawCircle(JarvisColors.MintDeep, core, center)
                    drawCircle(accent, core, center, style = Stroke(1.5.dp.toPx()))
                    drawCircle(accent, 5.dp.toPx(), center)
                }
            }
            if (live) Row(horizontalArrangement = Arrangement.spacedBy(5.dp), verticalAlignment = Alignment.CenterVertically) {
                for (height in listOf(.3f, .65f, 1f, .55f, .25f))
                    Box(Modifier.width(4.dp).height((14 + 40 * height * (if (state == OrbState.Listening) .6f + .4f * breath else .4f)).dp)
                        .clip(RoundedCornerShape(2.dp)).background(JarvisColors.Ink))
            }
        }
        Text(label, fontSize = 30.sp, fontWeight = FontWeight.Light, color = JarvisColors.Text)
    }
}

/** A plain settings line: label, one short hint, and an optional trailing control. */
@Composable
fun SettingRow(title: String, hint: String? = null, trailing: @Composable (() -> Unit)? = null) {
    Row(Modifier.fillMaxWidth().heightIn(min = 60.dp).padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(title, fontSize = 15.sp, color = JarvisColors.Text)
            if (hint != null) Text(hint, fontSize = 13.sp, color = JarvisColors.Muted)
        }
        trailing?.invoke()
    }
    HorizontalDivider(color = JarvisColors.Line)
}

@Composable
fun SectionLabel(text: String) = Text(text.uppercase(), fontSize = 12.sp, letterSpacing = .5.sp, color = JarvisColors.Muted,
    modifier = Modifier.padding(top = 20.dp, bottom = 2.dp))

@Composable
fun PairingScreen(state: ScreenState, pair: () -> Unit, manual: (String, String) -> Unit) {
    var advanced by remember { mutableStateOf(false) }
    var url by remember { mutableStateOf("") }
    var key by remember { mutableStateOf("") }
    Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(22.dp)) {
        Box(Modifier.padding(top = 16.dp).size(200.dp).clip(RoundedCornerShape(28.dp)).border(1.dp, JarvisColors.Line, RoundedCornerShape(28.dp)),
            contentAlignment = Alignment.Center) {
            Canvas(Modifier.size(110.dp).semantics { contentDescription = "Scan a QR code" }) {
                val stroke = Stroke(2.dp.toPx()); val w = size.width; val c = w * .22f
                for ((x, y) in listOf(0f to 0f, w - c to 0f, 0f to w - c)) drawRect(JarvisColors.Mint, Offset(x, y), androidx.compose.ui.geometry.Size(c, c), style = stroke)
                drawRect(JarvisColors.Mint, Offset(w * .6f, w * .6f), androidx.compose.ui.geometry.Size(c * .6f, c * .6f))
            }
        }
        Text(if (state.pairingOffered) "Connect this phone" else "Pair with JARVIS", fontSize = 30.sp, fontWeight = FontWeight.Light, color = JarvisColors.Text)
        if (state.pairingOffered) {
            state.pairingHost?.let { Text(it, color = JarvisColors.Muted, fontSize = 15.sp) }
            Button(onClick = pair, enabled = !state.busy, modifier = Modifier.fillMaxWidth().height(56.dp), shape = RoundedCornerShape(28.dp)) {
                Text(if (state.busy) "Connecting…" else "Connect", fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
            }
        } else {
            Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                for ((number, step) in listOf("On your computer, open JARVIS › Devices › Pair a phone", "Scan the code with this phone's camera", "Tap Connect when JARVIS opens").withIndex())
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                        Box(Modifier.size(28.dp).clip(CircleShape).background(JarvisColors.MintDeep), contentAlignment = Alignment.Center) {
                            Text("${number + 1}", color = JarvisColors.Mint, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
                        }
                        Text(step, fontSize = 15.sp, color = JarvisColors.Text)
                    }
            }
            Text("No address or key to type.", color = JarvisColors.Muted, fontSize = 14.sp, textAlign = TextAlign.Center)
        }
        TextButton(onClick = { advanced = !advanced }, modifier = Modifier.heightIn(min = 48.dp)) {
            Text(if (advanced) "Hide details" else "Enter details instead", color = JarvisColors.Muted, fontSize = 14.sp)
        }
        AnimatedVisibility(advanced) {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                OutlinedTextField(url, { url = it }, label = { Text("HTTPS address") }, modifier = Modifier.fillMaxWidth(), singleLine = true)
                OutlinedTextField(key, { key = it }, label = { Text("Phone key") }, visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth(), singleLine = true)
                OutlinedButton(onClick = { manual(url, key); key = "" }, enabled = !state.busy, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) { Text("Connect") }
            }
        }
    }
}
