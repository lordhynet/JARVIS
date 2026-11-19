package uk.jarvis.mobile

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.URI
import java.net.URLDecoder
import java.util.Locale

internal data class QrPairingOffer(val url: String, val code: String) {
    val host: String get() = URI(url).rawAuthority
}

internal class QrPairingException(message: String) : java.io.IOException(message)

/**
 * The owner's dashboard shows `jarvis://pair?u=<Core address>&c=<one-time code>` as a QR code. The phone camera
 * opens JARVIS, and the code is exchanged once for this phone's own scoped key. The shared Core key never reaches it.
 */
internal object QrPairing {
    private val codePattern = Regex("jpc1_[A-Za-z0-9_-]{43}")
    private val addressNumber = Regex("[0-9]+|0x[0-9a-f]+")
    const val EXCHANGE_PATH = "/pair/exchange"

    fun parse(link: String): QrPairingOffer {
        require(link.length <= 2048) { "This pairing code is too long." }
        val uri = URI(link)
        require(uri.scheme == "jarvis" && uri.host == "pair" && uri.rawQuery != null) { "This is not a JARVIS pairing code." }
        val query = uri.rawQuery.split('&').mapNotNull { part ->
            val index = part.indexOf('=')
            if (index <= 0) null else part.substring(0, index) to URLDecoder.decode(part.substring(index + 1), "UTF-8")
        }.groupBy({ it.first }, { it.second })
        val url = CoreAddress.validate(query["u"]?.singleOrNull() ?: throw IllegalArgumentException("The pairing code has no address."))
        val code = query["c"]?.singleOrNull() ?: throw IllegalArgumentException("The pairing code is incomplete.")
        require(codePattern.matches(code)) { "The pairing code is invalid." }
        // Without a pinned certificate the phone relies on system trust, so only a private DNS name is accepted.
        val host = URI(url).host.trimEnd('.').lowercase(Locale.ROOT)
        require(host != "localhost" && !host.endsWith(".localhost") && ':' !in host &&
            host.split('.').any { !addressNumber.matches(it) }) { "Open the dashboard at your private HTTPS name to pair this phone." }
        return QrPairingOffer(url, code)
    }

    fun request(offer: QrPairingOffer, deviceId: String, name: String): String = JsonObject().apply {
        addProperty("code", offer.code); addProperty("deviceId", deviceId); addProperty("name", name.take(120))
    }.toString()

    /** Returns the scoped connection and the device ID Core assigned (an existing phone keeps its identity). */
    fun response(offer: QrPairingOffer, body: String): Pair<SavedConnection, String> {
        val json = JsonParser.parseString(body).asJsonObject
        val credential = json.get("credential")?.takeIf { it.isJsonPrimitive }?.asString.orEmpty()
        val deviceId = json.get("deviceId")?.takeIf { it.isJsonPrimitive }?.asString.orEmpty()
        require(credential.startsWith("jdv1_") && credential.length == 69 && credential.none { it.isISOControl() }) { "Core returned an invalid phone key." }
        require(deviceId.matches(Regex("android-[A-Za-z0-9-]{8,71}"))) { "Core returned an invalid phone identity." }
        return SavedConnection(offer.url, credential, null) to deviceId
    }

    suspend fun exchange(offer: QrPairingOffer, deviceId: String, name: String): Pair<SavedConnection, String> = withContext(Dispatchers.IO) {
        val connection = URI(offer.url + EXCHANGE_PATH).toURL().openConnection() as HttpURLConnection
        try {
            connection.instanceFollowRedirects = false
            connection.connectTimeout = 10_000; connection.readTimeout = 15_000; connection.requestMethod = "POST"
            connection.setRequestProperty("Content-Type", "application/json"); connection.setRequestProperty("Accept", "application/json")
            connection.doOutput = true
            connection.outputStream.use { it.write(request(offer, deviceId, name).toByteArray(Charsets.UTF_8)) }
            val status = connection.responseCode
            val text = (if (status in 200..299) connection.inputStream else connection.errorStream)?.use { input ->
                val out = ByteArrayOutputStream(); val buffer = ByteArray(4096)
                while (true) { val read = input.read(buffer); if (read < 0) break; out.write(buffer, 0, read); require(out.size() <= 65_536) { "Core response was too large." } }
                out.toString(Charsets.UTF_8.name())
            }.orEmpty()
            if (status !in 200..299) throw QrPairingException(message(status))
            response(offer, text)
        } finally { connection.disconnect() }
    }

    fun message(status: Int): String = when (status) {
        401 -> "This code expired or was already used. Show a new one on your computer."
        409 -> "This phone is already paired. Use Pair again on its row in Devices."
        429 -> "Too many tries. Wait a minute, then scan a new code."
        else -> "Pairing failed (HTTP $status). Try a new code."
    }
}
