package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files
import java.util.Base64
import java.util.UUID
import javax.crypto.KeyGenerator

class RecordingTests {
    @Test fun explicit_visible_unlocked_consented_start_is_required_and_network_loss_does_not_stop_capture() {
        for (missing in 0..5) {
            val values = MutableList(6) { true }; values[missing] = false
            val control = RecordingCaptureControl()
            assertThrows(IllegalStateException::class.java) { control.begin(values[0], values[1], values[2], values[3], values[4], values[5]) }
            assertFalse(control.recordingIndicator)
        }
        val control = RecordingCaptureControl(); control.begin(true, true, true, true, true, true)
        assertTrue(control.recordingIndicator); assertFalse(control.allowsWake)
        control.captured(); control.networkLost()
        assertTrue(control.microphoneActive); assertFalse(control.allowsWake)
        control.stopped(); assertFalse(control.microphoneActive); assertTrue(control.allowsWake)
        control.networkLost(); assertEquals(RecordingPhase.Pending, control.phase)
        control.finished(); assertEquals(RecordingPhase.Idle, control.phase)
    }

    @Test fun protected_queue_replays_only_unacknowledged_chunks_and_preserves_identity_on_restart() {
        val root = Files.createTempDirectory("jarvis-recording-test-").toFile()
        try {
            val key = KeyGenerator.getInstance("AES").apply { init(256) }.generateKey()
            val binding = RecordingQueue.binding("https://core.example", "phone", 4)
            val queue = RecordingQueue.create(root, key, RecordingQueueState(UUID.randomUUID().toString(), "phone", 4, binding, startedAt = 1000))
            val wav = RecordingWav.wrap(ByteArray(32000) { (it % 73).toByte() }, 32000)
            queue.add(wav); queue.add(wav)
            val pending = queue.pending(); assertEquals(listOf(0, 1), pending.map { it.sequence })
            assertArrayEquals(wav, Base64.getDecoder().decode(pending[0].audioBase64))
            assertThrows(IllegalStateException::class.java) { queue.acknowledge(0, "BAD") }
            assertEquals(2, queue.pending().size)
            queue.acknowledge(0, pending[0].sha256)
            val encrypted = root.walkTopDown().first { it.name == "unknown-001.aes" }.readBytes()
            assertFalse(encrypted.toString(Charsets.UTF_8).contains(pending[1].audioBase64))
            val resumed = RecordingQueue.load(root, key).single()
            assertTrue(resumed.state.stopped); assertTrue(resumed.state.discontinuous)
            assertEquals(2, resumed.state.counts.getValue("unknown")); assertEquals(1, resumed.pending().single().sequence)
            resumed.acknowledge(1, pending[1].sha256); resumed.clear()
            assertTrue(root.walkTopDown().none { it.isFile })
        } finally {
            check(root.canonicalPath.startsWith(java.io.File(requireNotNull(System.getProperty("java.io.tmpdir"))).canonicalPath + java.io.File.separator))
            root.deleteRecursively()
        }
    }

    @Test fun queue_authentication_rejects_altered_ciphertext_and_wav_has_bounded_pcm_format() {
        val root = Files.createTempDirectory("jarvis-recording-test-").toFile()
        try {
            val key = KeyGenerator.getInstance("AES").apply { init(256) }.generateKey()
            val queue = RecordingQueue.create(root, key, RecordingQueueState(UUID.randomUUID().toString(), "phone", 1,
                RecordingQueue.binding("https://core.example", "phone", 1), startedAt = 1000))
            val wav = RecordingWav.wrap(ByteArray(1_920_000), 1_920_000)
            assertEquals(1_920_044, wav.size); assertEquals("RIFF", wav.copyOfRange(0, 4).toString(Charsets.US_ASCII))
            assertThrows(IllegalArgumentException::class.java) { RecordingWav.wrap(ByteArray(3), 3) }
            queue.add(wav); queue.stop(false)
            val file = root.walkTopDown().first { it.name == "unknown-000.aes" }; val corrupt = file.readBytes()
            corrupt[corrupt.lastIndex] = (corrupt.last().toInt() xor 1).toByte(); file.writeBytes(corrupt)
            assertThrows(javax.crypto.AEADBadTagException::class.java) { queue.pending() }
        } finally {
            check(root.canonicalPath.startsWith(java.io.File(requireNotNull(System.getProperty("java.io.tmpdir"))).canonicalPath + java.io.File.separator))
            root.deleteRecursively()
        }
    }
}
