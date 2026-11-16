package uk.jarvis.mobile

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import com.google.gson.Gson
import java.io.File
import java.security.KeyStore
import java.security.MessageDigest
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey

data class PracticeSnapshot(val schemaVersion: Int = 1, val state: ScreenState, val writes: List<PracticeWrite>, val coreEpoch: Long? = null,
    val drillDrafts: Map<String, String> = emptyMap(), val answerDrafts: Map<String, String> = emptyMap())

/** Atomic encrypted local data, excluded from device backup and scoped to the exact Core identity. */
class PracticeStore(context: Context, url: String, token: String) {
    companion object {
        private val lock = Any()
        private var generation = 0L
        fun eraseAll(context: Context) = synchronized(lock) {
            generation++
            context.noBackupFilesDir.listFiles()?.filter { Regex("practice-[a-f0-9]{64}\\.bin(\\.(bak|new))?").matches(it.name) }?.forEach {
                check(it.delete() || !it.exists()) { "Local practice data could not be erased. Try again before signing out." }
            }
        }
    }
    private val epoch = synchronized(lock) { generation }
    private val identity = MessageDigest.getInstance("SHA-256").digest((CoreAddress.validate(url) + "\n" + token).toByteArray())
        .joinToString("") { "%02x".format(it) }
    private val file = AtomicFile(File(context.noBackupFilesDir, "practice-$identity.bin"))
    private val gson = Gson()
    private var lastWrite = 0L
    private var closed = false
    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey("jarvis-practice", null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder("jarvis-practice", KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
        }.generateKey()
    }
    fun read(): PracticeSnapshot? = synchronized(lock) {
        if (!file.baseFile.exists() && !File(file.baseFile.path + ".bak").exists()) return@synchronized null
        try {
            val bytes = file.openRead().use { input ->
                val out = java.io.ByteArrayOutputStream(); val buffer = ByteArray(8192)
                while (out.size() <= 4_000_000) { val count = input.read(buffer); if (count < 0) break; out.write(buffer, 0, count) }
                out.toByteArray()
            }
            check(bytes.size in 29..4_000_000)
            val snapshot = gson.fromJson(PracticeCipher.open(bytes, key(), identity).toString(Charsets.UTF_8), PracticeSnapshot::class.java)
            check(snapshot.schemaVersion == 1); PracticeQueue(snapshot.writes)
            snapshot
        } catch (_: Exception) { error("Encrypted practice data could not be read. It has not been overwritten. Reconnect with the original Core identity or explicitly forget local data.") }
    }
    fun write(snapshot: PracticeSnapshot, sequence: Long) = synchronized(lock) {
        if (closed || epoch != generation || sequence <= lastWrite) return@synchronized
        val plain = gson.toJson(snapshot).toByteArray(Charsets.UTF_8)
        check(plain.size <= 3_900_000) { "Local practice cache is full. Export or remove old local data before continuing." }
        val encrypted = PracticeCipher.seal(plain, key(), identity)
        val out = file.startWrite()
        try { out.write(encrypted); file.finishWrite(out); lastWrite = sequence } catch (error: Exception) { file.failWrite(out); throw error }
    }
    fun close() = synchronized(lock) { closed = true }
    fun clear() = synchronized(lock) {
        closed = true; file.delete()
        check(!file.baseFile.exists() && !File(file.baseFile.path + ".bak").exists() && !File(file.baseFile.path + ".new").exists()) {
            "Local practice data could not be erased. Retry local cleanup."
        }
    }
}
