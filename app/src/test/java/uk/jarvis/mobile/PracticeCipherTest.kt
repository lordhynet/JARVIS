package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test
import javax.crypto.AEADBadTagException
import javax.crypto.KeyGenerator

class PracticeCipherTest {
    private fun key() = KeyGenerator.getInstance("AES").apply { init(256) }.generateKey()
    @Test fun content_is_encrypted_and_each_save_uses_a_fresh_nonce() {
        val key = key(); val plain = "Private interview answer".toByteArray()
        val first = PracticeCipher.seal(plain, key, "core-a"); val second = PracticeCipher.seal(plain, key, "core-a")
        assertFalse(first.contentEquals(second)); assertFalse(first.toString(Charsets.UTF_8).contains("Private interview answer"))
        assertArrayEquals(plain, PracticeCipher.open(first, key, "core-a"))
    }
    @Test fun altered_ciphertext_wrong_keys_and_other_core_identities_are_rejected() {
        val key = key(); val encrypted = PracticeCipher.seal("Draft".toByteArray(), key, "core-a")
        assertThrows(AEADBadTagException::class.java) { PracticeCipher.open(encrypted, key, "core-b") }
        assertThrows(AEADBadTagException::class.java) { PracticeCipher.open(encrypted, key(), "core-a") }
        val changed = encrypted.clone(); changed[changed.lastIndex] = (changed.last().toInt() xor 1).toByte()
        assertThrows(AEADBadTagException::class.java) { PracticeCipher.open(changed, key, "core-a") }
    }
    @Test fun invalid_and_oversized_payloads_are_bounded() {
        assertThrows(IllegalArgumentException::class.java) { PracticeCipher.open(ByteArray(27), key(), "core") }
        assertThrows(IllegalArgumentException::class.java) { PracticeCipher.seal(ByteArray(3_900_001), key(), "core") }
    }
}
