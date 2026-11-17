package uk.jarvis.mobile

import android.content.Context
import android.util.AtomicFile
import java.io.File
import java.security.KeyStore
import java.security.MessageDigest
import javax.crypto.SecretKey

/** Old editor caches are preserved encrypted, never interpreted or replayed by the audio companion. */
internal object LegacyDraftRecovery {
    private fun identity(connection: SavedConnection) = MessageDigest.getInstance("SHA-256")
        .digest((CoreAddress.validate(connection.url) + "\n" + connection.token).toByteArray()).joinToString("") { "%02x".format(it) }
    private fun file(context: Context, connection: SavedConnection) = File(context.noBackupFilesDir, "practice-${identity(connection)}.bin")
    fun available(context: Context, connection: SavedConnection): Boolean {
        val file = file(context, connection); return file.exists() || File(file.path + ".bak").exists()
    }
    fun export(context: Context, connection: SavedConnection): ByteArray {
        val encrypted = AtomicFile(file(context, connection)).openRead().use { stream ->
            val out = java.io.ByteArrayOutputStream(); val buffer = ByteArray(8192)
            while (out.size() <= 4_000_000) { val count = stream.read(buffer); if (count < 0) break; out.write(buffer, 0, count) }
            out.toByteArray()
        }
        check(encrypted.size <= 4_000_000) { "Legacy cache exceeds its safe recovery limit." }
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val key = store.getKey("jarvis-practice", null) as? SecretKey ?: error("The original device recovery key is unavailable.")
        return PracticeCipher.open(encrypted, key, identity(connection))
    }
    fun eraseIdentity(context: Context, connection: SavedConnection) {
        val base = file(context, connection)
        for (suffix in listOf("", ".bak", ".new")) { val target = File(base.path + suffix); check(target.delete() || !target.exists()) { "Legacy local files could not be removed." } }
    }
    fun eraseAll(context: Context) {
        context.noBackupFilesDir.listFiles()?.filter { Regex("practice-[a-f0-9]{64}\\.bin(\\.(bak|new))?").matches(it.name) }?.forEach {
            check(it.delete() || !it.exists()) { "Legacy local files could not be removed." }
        }
    }
}
