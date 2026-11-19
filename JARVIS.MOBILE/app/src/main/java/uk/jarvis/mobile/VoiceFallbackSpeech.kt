package uk.jarvis.mobile

import android.content.Context
import android.media.AudioAttributes
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.util.Locale
import java.util.UUID

internal suspend fun playFallbackPrompt(context: Context, text: String): Boolean = withContext(Dispatchers.Main.immediate) {
    val initialized = CompletableDeferred<Int>()
    val speech = TextToSpeech(context) { initialized.complete(it) }
    try {
        withTimeoutOrNull(9000) {
            if (initialized.await() != TextToSpeech.SUCCESS) return@withTimeoutOrNull false
            val voice = speech.voices.orEmpty()
                .filter { it.locale.language == Locale.ENGLISH.language && !it.isNetworkConnectionRequired &&
                    TextToSpeech.Engine.KEY_FEATURE_NOT_INSTALLED !in it.features }
                .maxByOrNull { it.quality } ?: return@withTimeoutOrNull false
            if (speech.setVoice(voice) != TextToSpeech.SUCCESS) return@withTimeoutOrNull false
            speech.setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
                .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
            val utterance = UUID.randomUUID().toString()
            val played = CompletableDeferred<Boolean>()
            speech.setOnUtteranceProgressListener(object : UtteranceProgressListener() {
                override fun onStart(utteranceId: String) = Unit
                override fun onDone(utteranceId: String) { if (utteranceId == utterance) played.complete(true) }
                @Deprecated("Legacy speech engines may report errors here")
                override fun onError(utteranceId: String) { if (utteranceId == utterance) played.complete(false) }
                override fun onError(utteranceId: String, errorCode: Int) { if (utteranceId == utterance) played.complete(false) }
                override fun onStop(utteranceId: String, interrupted: Boolean) { if (utteranceId == utterance) played.complete(false) }
            })
            if (speech.speak(text, TextToSpeech.QUEUE_FLUSH, null, utterance) == TextToSpeech.SUCCESS) played.await() else false
        } ?: false
    } finally {
        speech.stop()
        speech.shutdown()
    }
}
