package uk.jarvis.mobile

import java.util.Base64
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class ProvisioningTest {
    private val url = "https://jarvis.example.ts.net"
    private val token = "companion-test-credential"

    @Test fun tailnetPairingCanUseSystemTls() {
        val connection = Provisioning.parse(payload(url))
        assertEquals(url, connection.url)
        assertEquals(token, connection.token)
        assertNull(connection.certificateSha256)
        assertNull(Provisioning.parse(payload(url, "null")).certificateSha256)
    }

    @Test fun existingUsbPairingRetainsItsCertificatePin() {
        val fingerprint = List(32) { "AB" }.joinToString(":")
        val connection = Provisioning.parse(payload("https://127.0.0.1:5186", "\"$fingerprint\""))
        assertEquals("ab".repeat(32), connection.certificateSha256)
    }

    @Test fun invalidPinsCannotFallBackToSystemTls() {
        listOf("\"\"", "\"ab\"", "true", "[]").forEach { invalid ->
            assertThrows(IllegalArgumentException::class.java) { Provisioning.parse(payload(url, invalid)) }
        }
    }

    @Test fun rejectsUntrustedAddressShapesAndMalformedCredentials() {
        assertThrows(IllegalArgumentException::class.java) { Provisioning.parse(payload("http://jarvis.example.ts.net")) }
        assertThrows(IllegalArgumentException::class.java) { Provisioning.parse(payload("$url/path")) }
        assertThrows(IllegalArgumentException::class.java) {
            Provisioning.parse(encode("""{"url":"$url","token":1234567890123456}"""))
        }
        assertThrows(IllegalArgumentException::class.java) {
            Provisioning.parse(encode("""{"url":"$url","token":"$token\r"}"""))
        }
    }

    private fun payload(address: String, pin: String? = null): String {
        val certificate = pin?.let { ""","certificateSha256":$it""" }.orEmpty()
        return encode("""{"url":"$address","token":"$token"$certificate}""")
    }

    private fun encode(json: String): String =
        Base64.getUrlEncoder().withoutPadding().encodeToString(json.toByteArray(Charsets.UTF_8))
}
