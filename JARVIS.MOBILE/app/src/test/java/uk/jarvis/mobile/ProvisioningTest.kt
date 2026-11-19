package uk.jarvis.mobile

import com.google.gson.JsonObject
import java.security.KeyStore
import java.security.MessageDigest
import java.util.Base64
import javax.net.ssl.SSLHandshakeException
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager
import org.junit.Assert.*
import org.junit.Test

class ProvisioningTest {
    private val pin = "ab".repeat(32)

    @Test fun publicDnsPairingAcceptsAnAbsentNullOrEmptyPin() {
        for (include in listOf(false, true)) {
            for (value in listOf(null, "", " ")) {
                val offer = Provisioning.parse(offer("https://jarvis.example.org", value, include))
                assertEquals("https://jarvis.example.org", offer.url)
                assertNull(offer.certificateSha256)
            }
        }
    }

    @Test fun onlyDnsHttpsCanOmitThePin() {
        for (url in listOf("http://jarvis.example.org", "https://localhost", "https://localhost.", "https://core.localhost",
            "https://100.64.0.1", "https://[::1]", "https://2130706433", "https://0x7f000001")) {
            assertThrows(IllegalArgumentException::class.java) { Provisioning.parse(offer(url, null, false)) }
        }
    }

    @Test fun explicitPinsRemainPinnedAndInvalidPinsAreRejected() {
        assertEquals(pin, Provisioning.parse(offer("https://localhost:5186", pin.uppercase(), true)).certificateSha256)
        assertEquals(pin, Provisioning.parse(offer("https://jarvis.example.org", pin, true)).certificateSha256)
        assertThrows(IllegalArgumentException::class.java) { Provisioning.parse(offer("https://jarvis.example.org", "invalid", true)) }
    }

    @Test fun aTrustedCertificateWithTheWrongPinStillRequiresRepairing() {
        val trust = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm()).apply { init(null as KeyStore?) }
        val leaf = trust.trustManagers.filterIsInstance<X509TrustManager>().flatMap { it.acceptedIssuers.toList() }
            .first { runCatching { it.checkValidity() }.isSuccess }
        val correct = MessageDigest.getInstance("SHA-256").digest(leaf.encoded).joinToString("") { "%02x".format(it) }
        CertificatePin.trustManager(correct).checkServerTrusted(arrayOf(leaf), leaf.publicKey.algorithm)
        val mismatch = assertThrows(CoreCertificateChanged::class.java) {
            CertificatePin.trustManager(if (correct == pin) "cd".repeat(32) else pin)
                .checkServerTrusted(arrayOf(leaf), leaf.publicKey.algorithm)
        }
        val wrapped = SSLHandshakeException("TLS failed").apply { initCause(mismatch) }
        assertEquals("The Core certificate changed. Pair again from your trusted computer.", CertificatePin.failureMessage(wrapped))
    }

    private fun offer(url: String, value: String?, include: Boolean): String {
        val json = JsonObject().apply {
            addProperty("url", url)
            addProperty("token", "test-pairing-key-12345")
            if (include) addProperty("certificateSha256", value)
        }
        return Base64.getUrlEncoder().withoutPadding().encodeToString(json.toString().toByteArray())
    }
}
