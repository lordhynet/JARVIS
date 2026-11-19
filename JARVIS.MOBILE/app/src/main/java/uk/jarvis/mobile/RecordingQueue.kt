package uk.jarvis.mobile

import com.google.gson.Gson
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID
import javax.crypto.SecretKey

internal data class RecordingQueueState(
    val recordingId: String, val deviceId: String, val epoch: Long, val binding: String,
    val counts: MutableMap<String, Int> = mutableMapOf("unknown" to 0), val startedAt: Long,
    val stopped: Boolean = false, val discontinuous: Boolean = false
)
internal data class RecordingQueuedChunk(val recordingId: String, val track: String, val sequence: Int, val sha256: String, val audioBase64: String)
internal class RecordingQueue private constructor(private val folder: File, private val key: SecretKey, initial: RecordingQueueState) {
    @Volatile var state = initial
        private set
    private val gson = Gson()
    private val identity = "jarvis-recording-v1:${initial.binding}:${initial.recordingId}"
    private val stateFile get() = File(folder, "state.aes")
    init { validate(initial); safeDirectory(folder.parentFile.parentFile, folder) }
    private fun chunkFile(sequence: Int): File { require(sequence in 0..179); safeDirectory(folder.parentFile.parentFile, folder); return File(folder, "unknown-${sequence.toString().padStart(3, '0')}.aes") }
    private fun write(file: File, value: Any) {
        val plain = gson.toJson(value).toByteArray(Charsets.UTF_8)
        try {
            val encrypted = PracticeCipher.seal(plain, key, identity)
            val scopeRoot = folder.parentFile!!.parentFile!!
            val existing = scopeRoot.walkTopDown().onEnter { !Files.isSymbolicLink(it.toPath()) }.filter { it.isFile }.sumOf { it.length() }
            check(existing + encrypted.size < 2L * 1024 * 1024 * 1024) { "Protected recording storage is full." }
            check(!Files.isSymbolicLink(file.toPath()))
            val temporary = File(file.path + ".tmp"); check(!Files.isSymbolicLink(temporary.toPath()))
            try { temporary.writeBytes(encrypted); Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE) }
            finally { temporary.delete() }
        } finally { plain.fill(0) }
    }
    private fun <T> read(file: File, type: Class<T>): T {
        check(file.length() <= 4_000_000 && !Files.isSymbolicLink(file.toPath()))
        val plain = PracticeCipher.open(file.readBytes(), key, identity)
        try { return gson.fromJson(plain.toString(Charsets.UTF_8), type) } finally { plain.fill(0) }
    }
    private fun persist() = write(stateFile, state)
    @Synchronized fun add(wav: ByteArray) {
        check(!state.stopped && wav.size in 46..1_920_044)
        val sequence = state.counts.getValue("unknown"); val target = chunkFile(sequence)
        write(target, RecordingQueuedChunk(state.recordingId, "unknown", sequence, hash(wav), Base64.getEncoder().encodeToString(wav)))
        state.counts["unknown"] = sequence + 1; persist()
    }
    @Synchronized fun pending(limit: Int = 4): List<RecordingQueuedChunk> {
        val result = mutableListOf<RecordingQueuedChunk>()
        for (sequence in 0..179) {
            val file = chunkFile(sequence); if (!file.exists()) continue
            val chunk = read(file, RecordingQueuedChunk::class.java)
            check(chunk.recordingId == state.recordingId && chunk.track == "unknown" && chunk.sequence == sequence && chunk.sha256.length == 64)
            result.add(chunk); if (result.size >= limit) break
        }
        return result
    }
    @Synchronized fun acknowledge(sequence: Int, sha: String) {
        val file = chunkFile(sequence); if (!file.exists()) return
        check(read(file, RecordingQueuedChunk::class.java).sha256 == sha) { "The upload acknowledgement did not match. Protected audio was retained." }
        check(file.delete()) { "Acknowledged temporary audio could not be removed." }
    }
    @Synchronized fun stop(discontinuous: Boolean) { state = state.copy(stopped = true, discontinuous = state.discontinuous || discontinuous); persist() }
    @Synchronized fun clear() {
        safeDirectory(folder.parentFile.parentFile, folder)
        for (sequence in 0..179) { val file = chunkFile(sequence); if (file.exists()) check(file.delete()); File(file.path + ".tmp").delete() }
        if (stateFile.exists()) check(stateFile.delete()); File(stateFile.path + ".tmp").delete()
        if (folder.listFiles()?.isEmpty() == true) check(folder.delete())
    }
    companion object {
        fun hash(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02X".format(it) }
        fun binding(url: String, deviceId: String, epoch: Long) = hash("recording-v1:${CoreAddress.validate(url)}:$deviceId:$epoch".toByteArray(Charsets.UTF_8))
        private fun validate(state: RecordingQueueState) {
            UUID.fromString(state.recordingId); require(state.epoch >= 0 && state.deviceId.length in 1..80 && Regex("[A-F0-9]{64}").matches(state.binding))
            require(state.counts.keys == setOf("unknown") && state.counts.getValue("unknown") in 0..180)
        }
        /**
         * Rejects symbolic links from [folder] up to and including the app-owned recording [root]. Directories above the
         * root belong to Android (for example /data/user/0, itself a system symlink on Samsung and many other devices).
         */
        internal fun safeDirectory(root: File, folder: File) {
            val top = root.absoluteFile
            var current: File? = folder.absoluteFile
            while (current != null) {
                check(!Files.isSymbolicLink(current.toPath())) { "Protected recording directories cannot be symbolic links." }
                if (current == top) return
                current = current.parentFile
            }
            error("Protected recording folder is outside its recording root.")
        }
        fun create(root: File, key: SecretKey, state: RecordingQueueState): RecordingQueue {
            validate(state); val folder = File(File(root, state.binding), state.recordingId); safeDirectory(root, folder)
            check(!File(folder, "state.aes").exists()); check(folder.mkdirs() || folder.isDirectory)
            return RecordingQueue(folder, key, state).also { it.persist() }
        }
        fun load(root: File, key: SecretKey): List<RecordingQueue> {
            safeDirectory(root, root); if (!root.exists()) return emptyList()
            val result = mutableListOf<RecordingQueue>()
            for (scope in root.listFiles().orEmpty().filter { it.isDirectory && Regex("[A-F0-9]{64}").matches(it.name) }) {
                safeDirectory(root, scope)
                for (folder in scope.listFiles().orEmpty().filter { it.isDirectory }) {
                    safeDirectory(root, folder); if (runCatching { UUID.fromString(folder.name) }.isFailure) continue
                    val file = File(folder, "state.aes"); if (!file.exists()) continue
                    val identity = "jarvis-recording-v1:${scope.name}:${folder.name}"
                    check(file.length() <= 16384 && !Files.isSymbolicLink(file.toPath()))
                    val plain = PracticeCipher.open(file.readBytes(), key, identity)
                    val saved = try { Gson().fromJson(plain.toString(Charsets.UTF_8), RecordingQueueState::class.java) } finally { plain.fill(0) }
                    validate(saved); check(saved.binding == scope.name && saved.recordingId == folder.name)
                    val stopped = saved.copy(stopped = true, discontinuous = saved.discontinuous || !saved.stopped)
                    val queue = RecordingQueue(folder, key, stopped)
                    for (sequence in 0..179) if (queue.chunkFile(sequence).exists()) queue.state.counts["unknown"] = maxOf(queue.state.counts.getValue("unknown"), sequence + 1)
                    queue.persist(); result.add(queue)
                }
            }
            return result
        }
    }
}

internal object RecordingWav {
    fun wrap(pcm: ByteArray, count: Int): ByteArray {
        require(count in 2..1_920_000 && count % 2 == 0 && count <= pcm.size)
        return ByteBuffer.allocate(count + 44).order(ByteOrder.LITTLE_ENDIAN).apply {
            put("RIFF".toByteArray(Charsets.US_ASCII)); putInt(count + 36); put("WAVEfmt ".toByteArray(Charsets.US_ASCII)); putInt(16)
            putShort(1); putShort(1); putInt(16000); putInt(32000); putShort(2); putShort(16); put("data".toByteArray(Charsets.US_ASCII)); putInt(count); put(pcm, 0, count)
        }.array()
    }
}
