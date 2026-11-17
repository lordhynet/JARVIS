package uk.jarvis.mobile
import org.junit.Assert.*
import org.junit.Test

class CertificatePinTest {
    @Test fun acceptsExactSha256FingerprintAndNormalizesDisplayFormat() {
        val bytes = List(32) { "AB" }.joinToString(":")
        assertEquals("ab".repeat(32), CertificatePin.normalize(bytes))
    }
    @Test fun rejectsEmptyTruncatedAndNonHexPins() {
        listOf("", "ab".repeat(31), "zz".repeat(32), "ab".repeat(33)).forEach {
            assertThrows(IllegalArgumentException::class.java) { CertificatePin.normalize(it) }
        }
    }
}
