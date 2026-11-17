package uk.jarvis.mobile

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

object JarvisColors {
    val Night = Color(0xFF0C121D)
    val Panel = Color(0xFF182231)
    val Orange = Color(0xFFFFB66C)
    val Mint = Color(0xFF90DCC7)
    val Lavender = Color(0xFFC3B4F3)
    val Muted = Color(0xFF9DA9BB)
}

@Composable
fun JarvisTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(primary = JarvisColors.Orange, onPrimary = JarvisColors.Night,
            secondary = JarvisColors.Mint, tertiary = JarvisColors.Lavender, background = JarvisColors.Night,
            surface = JarvisColors.Panel, onSurface = Color(0xFFECEFF5)),
        shapes = Shapes(small = RoundedCornerShape(14.dp), medium = RoundedCornerShape(22.dp), large = RoundedCornerShape(28.dp)),
        content = content
    )
}

@Composable
fun Wordmark(connected: Boolean, onSettings: (() -> Unit)?, settingsOpen: Boolean) {
    Row(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 22.dp, vertical = 16.dp),
        horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
        Column {
            Text("J A R V I S", fontWeight = FontWeight.Bold, fontSize = 19.sp)
            Text("YOUR PERSONAL COMPANION", color = JarvisColors.Muted, fontSize = 9.sp, letterSpacing = 1.3.sp)
        }
        if (onSettings != null) TextButton(onClick = onSettings) { Text(if (settingsOpen) "Back" else "Settings") }
        else Surface(color = JarvisColors.Mint.copy(alpha = .10f), shape = CircleShape) {
            Text(if (connected) "●  Connected" else "●  Ready to pair", color = JarvisColors.Mint, fontSize = 11.sp,
                modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp))
        }
    }
}

@Composable
private fun CoreVisual(ready: Boolean) {
    val scale by animateFloatAsState(if (ready) 1f else .94f, label = "Core paired state")
    Box(Modifier.fillMaxWidth().height(210.dp), contentAlignment = Alignment.Center) {
        Canvas(Modifier.size(210.dp).graphicsLayer { scaleX = scale; scaleY = scale }.semantics { contentDescription = "JARVIS connection" }) {
            val center = Offset(size.width / 2, size.height / 2)
            val radius = size.width * .26f
            drawCircle(Brush.radialGradient(listOf(JarvisColors.Orange.copy(alpha = .18f), Color.Transparent)), size.width / 2, center)
            drawCircle(JarvisColors.Orange.copy(alpha = .12f), radius * 1.65f, center, style = Stroke(1.dp.toPx()))
            drawCircle(JarvisColors.Muted.copy(alpha = .16f), radius * 1.3f, center, style = Stroke(1.dp.toPx()))
            drawCircle(Brush.linearGradient(listOf(Color(0xFFFAE7CD), JarvisColors.Orange, Color(0xFF9C5A3C))), radius, center)
            drawCircle(JarvisColors.Night.copy(alpha = .85f), radius * .77f, center)
            drawCircle(JarvisColors.Orange.copy(alpha = .35f), radius * .5f, center, style = Stroke(1.dp.toPx()))
            drawCircle(JarvisColors.Mint, 4.dp.toPx(), Offset(center.x + radius * 1.35f, center.y - radius * .5f))
        }
        Text("J", fontSize = 46.sp, fontWeight = FontWeight.Light, color = JarvisColors.Orange)
    }
}

@Composable
fun PairingScreen(state: ScreenState, pair: () -> Unit, manual: (String, String) -> Unit) {
    var advanced by remember { mutableStateOf(false) }
    var url by remember { mutableStateOf("") }
    var key by remember { mutableStateOf("") }
    CoreVisual(state.pairingOffered)
    Text("Your Core.\nIn your earbuds.", fontSize = 34.sp, lineHeight = 40.sp, fontWeight = FontWeight.SemiBold)
    Text("One audio connection to JARVIS.\nYour conversations and features stay on Core.", color = JarvisColors.Muted, fontSize = 16.sp, lineHeight = 24.sp)
    Surface(shape = RoundedCornerShape(28.dp), color = JarvisColors.Panel, modifier = Modifier.fillMaxWidth().animateContentSize()) {
        Column(Modifier.padding(22.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Surface(color = JarvisColors.Mint.copy(alpha = .14f), shape = RoundedCornerShape(14.dp)) {
                    Text(if (state.pairingOffered) "✓" else "⌁", color = JarvisColors.Mint, fontSize = 25.sp, modifier = Modifier.padding(12.dp))
                }
                Column {
                    Text(if (state.pairingOffered) "Your computer is ready" else "Let's bring your Core along", fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
                    Text(if (state.pairingOffered) "Secure pairing · no link to type" else "Pair from your trusted computer", color = JarvisColors.Muted, fontSize = 12.sp)
                }
            }
            if (state.pairingOffered) {
                state.certificateTrust?.let { Text(it, color = JarvisColors.Muted, fontSize = 14.sp) }
                Button(onClick = pair, enabled = !state.busy, modifier = Modifier.fillMaxWidth().height(54.dp), shape = RoundedCornerShape(16.dp)) {
                    Text(if (state.busy) "Connecting…" else "Connect to my Core  →", fontWeight = FontWeight.SemiBold)
                }
            } else Text("Keep this app open while your computer sends its pairing details. You won't need to copy an address or key.", color = JarvisColors.Muted, lineHeight = 22.sp)
            TextButton(onClick = { advanced = !advanced }) { Text(if (advanced) "Hide manual setup ↑" else "Already have a server?  ↓", fontSize = 12.sp) }
            AnimatedVisibility(advanced) {
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    OutlinedTextField(url, { url = it }, label = { Text("HTTPS server address") }, modifier = Modifier.fillMaxWidth(), singleLine = true)
                    OutlinedTextField(key, { key = it }, label = { Text("Core connection key") }, visualTransformation = PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth(), singleLine = true)
                    OutlinedButton(onClick = { manual(url, key); key = "" }, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) { Text("Connect with these details") }
                }
            }
        }
    }
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
        Text("◉  Live audio", color = JarvisColors.Muted, fontSize = 12.sp)
        Text("⌁  Core connection", color = JarvisColors.Muted, fontSize = 12.sp)
    }
    Text("You control when JARVIS listens. Your AI keys stay on your computer.", color = JarvisColors.Muted, fontSize = 12.sp, lineHeight = 18.sp)
}


