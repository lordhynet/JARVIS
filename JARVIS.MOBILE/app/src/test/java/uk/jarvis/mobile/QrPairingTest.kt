package uk.jarvis.mobile

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class QrPairingTest {
    private val code = "jpc1_" + "a".repeat(43)
    private fun link(address: String, value: String = code) =
        "jarvis://pair?u=" + java.net.URLEncoder.encode(address, "UTF-8") + "&c=" + value

    @Test fun parses_a_dashboard_link_for_a_private_dns_name() {
        val offer = QrPairing.parse(link("https://jarvis.tail1234.ts.net"))
        assertEquals("https://jarvis.tail1234.ts.net", offer.url)
        assertEquals(code, offer.code)
        assertEquals("jarvis.tail1234.ts.net", offer.host)
    }

    @Test fun refuses_other_schemes_hosts_and_malformed_codes() {
        assertThrows(IllegalArgumentException::class.java) { QrPairing.parse("https://pair?u=x&c=$code") }
        assertThrows(IllegalArgumentException::class.java) { QrPairing.parse("jarvis://other?u=https%3A%2F%2Fjarvis.example&c=$code") }
        assertThrows(IllegalArgumentException::class.java) { QrPairing.parse(link("https://jarvis.example", "jpc1_short")) }
        assertThrows(IllegalArgumentException::class.java) { QrPairing.parse("jarvis://pair?c=$code") }
    }

    @Test fun refuses_plain_http_ip_and_local_addresses_without_a_pin() {
        assertThrows(IllegalArgumentException::class.java) { QrPairing.parse(link("http://jarvis.example")) }
        assertThrows(IllegalArgumentException::class.java) { QrPairing.parse(link("https://192.168.1.20")) }
        assertThrows(IllegalArgumentException::class.java) { QrPairing.parse(link("https://localhost")) }
    }

    @Test fun request_carries_the_code_and_phone_identity_only() {
        val offer = QrPairing.parse(link("https://jarvis.example"))
        val body = QrPairing.request(offer, "android-1234abcd", "Android S22")
        assertTrue(body.contains(code)); assertTrue(body.contains("android-1234abcd"))
    }

    @Test fun accepts_only_a_scoped_phone_key_from_core() {
        val offer = QrPairing.parse(link("https://jarvis.example"))
        val key = "jdv1_" + "0".repeat(64)
        val (saved, id) = QrPairing.response(offer, """{"deviceId":"android-1234abcd","credential":"$key"}""")
        assertEquals("https://jarvis.example", saved.url); assertEquals(key, saved.token); assertNull(saved.certificateSha256)
        assertEquals("android-1234abcd", id)
        assertThrows(IllegalArgumentException::class.java) {
            QrPairing.response(offer, """{"deviceId":"android-1234abcd","credential":"master-key-value-1234"}""")
        }
    }

    @Test fun explains_expired_and_reused_codes_plainly() {
        assertEquals("This code expired or was already used. Show a new one on your computer.", QrPairing.message(401))
    }
}
