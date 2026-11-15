package uk.jarvis.mobile
import org.junit.Assert.*
import org.junit.Test

class CoreAddressTest {
    @Test fun acceptsTrustedHttpsRoot() { assertEquals("https://jarvis.example", CoreAddress.validate(" https://jarvis.example/ ")) }
    @Test fun rejectsUnsafeOrAmbiguousAddresses() {
        listOf("http://jarvis.example", "https://user:secret@jarvis.example", "https://jarvis.example/?key=x", "https://jarvis.example/path", "file:///secret").forEach {
            assertThrows(IllegalArgumentException::class.java) { CoreAddress.validate(it) }
        }
    }
}
