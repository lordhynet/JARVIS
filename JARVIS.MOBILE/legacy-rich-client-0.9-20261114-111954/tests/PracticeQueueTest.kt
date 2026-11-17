package uk.jarvis.mobile

import com.google.gson.Gson
import com.google.gson.JsonParser
import com.google.gson.reflect.TypeToken
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class PracticeQueueTest {
    private fun body(text: String = "My own answer", revision: Int = 1) = """{"requestId":"${UUID.randomUUID()}","text":"$text","expectedRevision":$revision}"""
    @Test fun lost_acknowledgement_and_process_restart_keep_the_same_operation_identity() {
        val queue = PracticeQueue(); val first = queue.prepare("/api/training/attempts", "POST", body())
        val gson = Gson(); val records: List<PracticeWrite> = gson.fromJson(gson.toJson(queue.list()), object : TypeToken<List<PracticeWrite>>() {}.type)
        val restored = PracticeQueue(records)
        assertEquals(first, restored.prepare(first.path, first.method, body()))
        assertEquals(first.id, JsonParser.parseString(first.json).asJsonObject.get("requestId").asString)
        assertEquals(1, restored.list().size)
        restored.acknowledge(first.id)
        assertNotEquals(first.id, restored.prepare(first.path, first.method, body()).id)
    }
    @Test fun edited_payload_and_changed_revision_never_reuse_an_old_identity() {
        val queue = PracticeQueue(); val path = "/api/training/attempts/${UUID.randomUUID()}/answer"
        val first = queue.prepare(path, "PATCH", body())
        val edited = queue.prepare(path, "PATCH", body("A correction"))
        val rebased = queue.prepare(path, "PATCH", body("A correction", 2))
        assertEquals(3, listOf(first.id, edited.id, rebased.id).distinct().size)
    }
    @Test fun guided_creation_and_stage_identity_survive_restart_without_admitting_paid_routes() {
        val queue = PracticeQueue(); val drill = queue.prepare("/api/training/drills", "POST", body("CodeReview"))
        val restored = PracticeQueue(queue.list()); assertEquals(drill.id, restored.prepare(drill.path, "POST", body("CodeReview")).id)
        val stage = """{"requestId":"","drillId":"${UUID.randomUUID()}","stepIndex":0,"text":"My review"}"""
        val first = restored.prepare("/api/training/attempts", "POST", stage)
        assertEquals(first.id, PracticeQueue(restored.list()).prepare(first.path, "POST", stage).id)
        assertNotEquals(first.id, restored.prepare(first.path, "POST", stage.replace("\"stepIndex\":0", "\"stepIndex\":1")).id)
    }
    @Test fun conflicts_survive_restart_and_are_not_silently_rebased() {
        val queue = PracticeQueue(); val pending = queue.prepare("/api/coaching/attempts", "POST", body())
        queue.block(pending.id, "Source story changed")
        val restored = PracticeQueue(queue.list()); val same = restored.prepare(pending.path, pending.method, body())
        assertEquals("Conflict", same.status); assertEquals(pending.json, same.json); assertEquals("Source story changed", same.detail)
    }
    @Test fun paid_ai_external_actions_and_non_idempotent_routes_are_excluded() {
        listOf("/api/voice/transcribe", "/api/voice/tickets", "/api/sessions", "/api/stories", "/api/workflows/interview-briefs",
            "/api/agents/runs", "/api/training/attempts/${UUID.randomUUID()}/ai-assessments", "/api/training/attempts/../ai-assessments").forEach { path ->
            assertThrows(IllegalArgumentException::class.java) { PracticeQueue().prepare(path, "POST", body()) }
        }
        assertThrows(IllegalArgumentException::class.java) { PracticeQueue().prepare("/api/coaching/attempts", "DELETE", body()) }
    }
    @Test fun capacity_bound_does_not_drop_unknown_outcomes() {
        val queue = PracticeQueue()
        repeat(50) { queue.prepare("/api/training/attempts", "POST", body("Answer $it")) }
        assertThrows(IllegalStateException::class.java) { queue.prepare("/api/training/attempts", "POST", body("Overflow")) }
        assertEquals(50, queue.list().size)
        assertEquals(queue.list().first(), queue.prepare("/api/training/attempts", "POST", body("Answer 0")))
    }
}
