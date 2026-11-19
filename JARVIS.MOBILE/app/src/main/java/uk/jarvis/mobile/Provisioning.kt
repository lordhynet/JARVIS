package uk.jarvis.mobile

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.net.URI
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.Locale
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

data class SavedConnection(val url: String, val token: String, val certificateSha256: String? = null)

internal object Provisioning {
    private val addressNumber = Regex("[0-9]+|0x[0-9a-f]+")

    fun parse(encoded: String): SavedConnection {
        require(encoded.length <= 8192) { "Pairing details are too long." }
        val json = JsonParser.parseString(String(Base64.getUrlDecoder().decode(encoded), Charsets.UTF_8)).asJsonObject
        val url = CoreAddress.validate(json.readString("url"))
        val token = json.readString("token")
        require(token.length in 16..512 && token.none { it.isISOControl() }) { "The Core connection key is invalid." }
        val pin = json.get("certificateSha256")
        val certificate = if (pin == null || pin.isJsonNull) null
            else json.readString("certificateSha256").takeUnless { it.isBlank() }?.let(CertificatePin::normalize)
        if (certificate == null) {
            val host = URI(url).host.trimEnd('.').lowercase(Locale.ROOT)
            require(host != "localhost" && !host.endsWith(".localhost") && ':' !in host &&
                host.split('.').any { !addressNumber.matches(it) }) { "Pairing to a local address or IP requires a certificate fingerprint." }
        }
        return SavedConnection(url, token, certificate)
    }

    private fun JsonObject.readString(name: String): String {
        val value = get(name)
        require(value != null && value.isJsonPrimitive && value.asJsonPrimitive.isString) {
            "Pairing field $name must be a string."
        }
        return value.asString
    }
}

internal class CoreCertificateChanged : CertificateException("The Core certificate changed. Pair again from your trusted computer.")

object CertificatePin {
    internal fun trustLabel(pin: String?, connected: Boolean = true) = when {
        pin != null -> "Pinned certificate"
        connected -> "Verified by system certificate"
        else -> "Uses system certificates"
    }
    internal fun failureMessage(error: Throwable): String? =
        generateSequence(error) { it.cause }.filterIsInstance<CoreCertificateChanged>().firstOrNull()?.message

    fun normalize(value: String): String {
        val normalized = value.replace(":", "").lowercase()
        require(normalized.matches(Regex("[0-9a-f]{64}"))) { "The Core certificate fingerprint is invalid." }
        return normalized
    }
    fun sockets(expected: String): SSLSocketFactory {
        return SSLContext.getInstance("TLS").apply { init(null, arrayOf(trustManager(expected)), null) }.socketFactory
    }
    fun trustManager(expected: String): X509TrustManager {
        val pin = normalize(expected).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        return object : X509TrustManager {
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) { throw CertificateException("Client authentication is unsupported.") }
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val leaf = chain?.firstOrNull() ?: throw CertificateException("Core did not present a certificate.")
                leaf.checkValidity()
                if (!MessageDigest.isEqual(pin, MessageDigest.getInstance("SHA-256").digest(leaf.encoded)))
                    throw CoreCertificateChanged()
            }
        }
    }
}
