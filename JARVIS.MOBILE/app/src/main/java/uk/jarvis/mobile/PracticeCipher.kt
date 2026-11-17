package uk.jarvis.mobile

import javax.crypto.Cipher
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

object PracticeCipher {
    fun seal(plain: ByteArray, key: SecretKey, identity: String): ByteArray {
        require(plain.size <= 3_900_000)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key); updateAAD(identity.toByteArray(Charsets.UTF_8)) }
        return cipher.iv + cipher.doFinal(plain)
    }
    fun open(bytes: ByteArray, key: SecretKey, identity: String): ByteArray {
        require(bytes.size in 28..4_000_000)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, bytes.copyOfRange(0, 12)))
            updateAAD(identity.toByteArray(Charsets.UTF_8))
        }
        return cipher.doFinal(bytes.copyOfRange(12, bytes.size))
    }
}
