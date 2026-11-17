package uk.jarvis.mobile

import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer

internal object WakePhraseMatcher {
    fun matches(text: String): Boolean = Regex("\\bhey\\s+jarvis\\b", RegexOption.IGNORE_CASE)
        .containsMatchIn(text.replace(Regex("[^\\p{L}\\p{N}]+"), " ").trim())
}

/** Uses Android's on-device speech service only; there is no cloud recognizer fallback. */
internal class LocalWakeDetector(
    context: Context,
    private val onWake: () -> Unit,
    private val onUnavailable: (String) -> Unit,
) {
    private val app = context.applicationContext
    private val main = Handler(Looper.getMainLooper())
    private var recognizer: SpeechRecognizer? = null
    @Volatile private var stopped = true
    @Volatile private var triggered = false

    fun start() = main.post {
        if (!stopped) return@post
        stopped = false; triggered = false
        if (Build.VERSION.SDK_INT < 31 || !SpeechRecognizer.isOnDeviceRecognitionAvailable(app)) {
            stopped = true; onUnavailable("This Android device has no local speech recognizer. No cloud fallback was started."); return@post
        }
        try {
            recognizer = SpeechRecognizer.createOnDeviceSpeechRecognizer(app).also { it.setRecognitionListener(listener) }
            listen()
        } catch (_: RuntimeException) {
            stopNow(); onUnavailable("Android could not start its on-device wake recognizer.")
        }
    }

    fun stop() = main.post { stopNow() }

    private fun listen() {
        if (stopped || triggered) return
        val intent = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_LANGUAGE, "en-GB")
            putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
            putExtra(RecognizerIntent.EXTRA_PREFER_OFFLINE, true)
        }
        try { recognizer?.startListening(intent) } catch (_: RuntimeException) { restart(1000) }
    }

    private fun inspect(bundle: Bundle?) {
        val phrase = bundle?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION).orEmpty().any(WakePhraseMatcher::matches)
        if (!phrase || stopped || triggered) return
        triggered = true
        runCatching { recognizer?.cancel() }; runCatching { recognizer?.destroy() }; recognizer = null
        onWake()
    }

    private fun restart(delay: Long) {
        if (!stopped && !triggered) main.postDelayed({ listen() }, delay)
    }

    private fun stopNow() {
        stopped = true
        main.removeCallbacksAndMessages(null)
        runCatching { recognizer?.cancel() }; runCatching { recognizer?.destroy() }; recognizer = null
    }

    private val listener = object : RecognitionListener {
        override fun onReadyForSpeech(params: Bundle?) = Unit
        override fun onBeginningOfSpeech() = Unit
        override fun onRmsChanged(rmsdB: Float) = Unit
        override fun onBufferReceived(buffer: ByteArray?) = Unit
        override fun onEndOfSpeech() = Unit
        override fun onError(error: Int) {
            if (error == SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS || error == SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED || error == SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE) {
                stopNow(); onUnavailable("Android's local recognizer cannot use this language or microphone permission.")
            } else restart(if (error == SpeechRecognizer.ERROR_RECOGNIZER_BUSY) 1500 else 700)
        }
        override fun onResults(results: Bundle?) { inspect(results); restart(200) }
        override fun onPartialResults(partialResults: Bundle?) = inspect(partialResults)
        override fun onEvent(eventType: Int, params: Bundle?) = Unit
    }
}
