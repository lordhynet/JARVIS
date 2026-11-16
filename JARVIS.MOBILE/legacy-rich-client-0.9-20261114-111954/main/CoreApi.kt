package uk.jarvis.mobile

import com.google.gson.Gson
import com.google.gson.JsonParser
import com.google.gson.reflect.TypeToken
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.ByteArrayOutputStream
import java.net.HttpURLConnection
import java.net.URI
import java.util.UUID
import javax.net.ssl.HttpsURLConnection

object CoreAddress {
    fun validate(raw: String): String {
        val uri = URI(raw.trim())
        require(uri.scheme == "https" && !uri.host.isNullOrBlank() && uri.userInfo == null && uri.rawQuery == null && uri.fragment == null) {
            "Use your trusted HTTPS Core address, for example https://jarvis.your-private-network.example"
        }
        require(uri.path.isNullOrEmpty() || uri.path == "/") { "Use the server root address without a path." }
        return raw.trim().trimEnd('/')
    }
}

class CoreHttpException(val status: Int, message: String) : java.io.IOException(message)
internal fun coreResponseLimit(path: String, method: String): Int = if (path == "/api/goals" && method == "GET") 3_000_000 else 1_048_576

class CoreApi(rawUrl: String, private val token: String, certificateSha256: String? = null) {
    private val privacy = PrivacyBoundary()
    fun bindEpoch(epoch: Long) = privacy.bind(epoch)
    fun invalidate() = privacy.invalidate()
    private val base = CoreAddress.validate(rawUrl)
    fun uses(connection: SavedConnection): Boolean = base == CoreAddress.validate(connection.url) && token == connection.token
    private val pinnedSockets = certificateSha256?.let { CertificatePin.sockets(it) }
    private val gson = Gson()
    private suspend fun exchange(path: String, body: Any?, method: String, audio: File? = null): String = withContext(Dispatchers.IO) {
        val responseLimit = coreResponseLimit(path, method)
        val privacyStatus = path == "/api/privacy/status"
        if (!privacyStatus) {
            check(privacy.epoch != null) { "Reconnect to verify Core privacy before accessing data." }
            privacy.verify(privacy.epoch)
        }
        val connection = URI(base + path).toURL().openConnection() as HttpURLConnection
        try {
            // Exact trusted certificate pin; normal hostname verification remains enabled.
            if (connection is HttpsURLConnection && pinnedSockets != null) connection.sslSocketFactory = pinnedSockets
            connection.instanceFollowRedirects = false
            connection.connectTimeout = 10_000; connection.readTimeout = 60_000; connection.requestMethod = method
            connection.setRequestProperty("X-Jarvis-Key", token)
            connection.setRequestProperty("Accept", "application/json")
            if (!privacyStatus) connection.setRequestProperty("X-Jarvis-Data-Epoch", privacy.epoch.toString())
            if (audio != null) {
                val boundary = "jarvis-" + UUID.randomUUID()
                connection.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
                connection.doOutput = true
                connection.outputStream.use { out ->
                    out.write("--$boundary\r\nContent-Disposition: form-data; name=\"audio\"; filename=\"answer.m4a\"\r\nContent-Type: audio/mp4\r\n\r\n".toByteArray())
                    audio.inputStream().use { it.copyTo(out) }
                    out.write("\r\n--$boundary--\r\n".toByteArray())
                }
            } else if (body != null) {
                connection.setRequestProperty("Content-Type", "application/json"); connection.doOutput = true
                connection.outputStream.use { it.write(gson.toJson(body).toByteArray(Charsets.UTF_8)) }
            }
            val code = connection.responseCode
            val stream = if (code in 200..299) connection.inputStream else connection.errorStream
            val bytes = stream?.use { input ->
                val result = ByteArrayOutputStream(); val buffer = ByteArray(8192)
                while (result.size() <= responseLimit) { val count = input.read(buffer); if (count < 0) break; result.write(buffer, 0, count) }
                result.toByteArray()
            } ?: byteArrayOf()
            check(bytes.size <= responseLimit) { "Core response exceeds this phone's safe download limit." }
            val text = bytes.toString(Charsets.UTF_8)
            val responseEpoch = connection.getHeaderField("X-Jarvis-Data-Epoch")?.toLongOrNull()
            val privacyError = if (code !in 200..299) runCatching {
                val json = JsonParser.parseString(text).asJsonObject
                json.get("coreErased")?.asBoolean == true || json.get("coreEpochChanged")?.asBoolean == true
            }.getOrDefault(false) else false
            if (code in 200..299 || privacyError) privacy.verify(responseEpoch, privacyError)
            if (privacyStatus && code in 200..299) {
                val status = gson.fromJson(text, uk.jarvis.mobile.contracts.PrivacyStatus::class.java)
                privacy.verify(status.epoch, status.erased)
            }
            if (code !in 200..299) {
                val detail = runCatching { JsonParser.parseString(text).asJsonObject.get("error")?.asString }.getOrNull()
                throw CoreHttpException(code, if (code == 409) "Conflict: reload the latest server version and reconcile your draft. ${detail.orEmpty()}"
                    else detail ?: "Core returned HTTP $code. Check your connection and credentials.")
            }
            text
        } finally { connection.disconnect() }
    }
    suspend fun <T> get(path: String, type: Class<T>): T = gson.fromJson(exchange(path, null, "GET"), type)
    suspend fun <T> list(path: String, type: Class<T>): List<T> = gson.fromJson(exchange(path, null, "GET"), TypeToken.getParameterized(List::class.java, type).type)
    suspend fun <T> send(path: String, body: Any, type: Class<T>, method: String = "POST"): T = gson.fromJson(exchange(path, body, method), type)
    suspend fun delete(path: String) { exchange(path, null, "DELETE") }
    suspend fun transcribe(file: File): String = JsonParser.parseString(exchange("/api/voice/transcribe", null, "POST", file)).asJsonObject.get("text").asString
}
