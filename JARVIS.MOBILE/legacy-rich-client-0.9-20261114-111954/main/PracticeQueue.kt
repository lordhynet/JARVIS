package uk.jarvis.mobile

import com.google.gson.JsonParser
import java.util.UUID

data class PracticeWrite(val id: String, val path: String, val method: String, val json: String,
    val status: String = "Pending", val detail: String = "")

/** Only explicitly requested, idempotent, unpaid practice writes may enter this queue. */
class PracticeQueue(initial: List<PracticeWrite> = emptyList()) {
    private val entries = initial.toMutableList()
    init { require(entries.size <= 50); entries.forEach(::validate); require(entries.map { it.id }.distinct().size == entries.size) }
    fun list(): List<PracticeWrite> = entries.toList()
    fun prepare(path: String, method: String, json: String): PracticeWrite {
        val input = JsonParser.parseString(json).asJsonObject.deepCopy().apply { remove("requestId") }
        // An uncertain acknowledgement must not mint a new operation identity after restart.
        entries.firstOrNull { it.path == path && it.method == method &&
            JsonParser.parseString(it.json).asJsonObject.deepCopy().apply { remove("requestId") } == input }?.let { return it }
        check(entries.size < 50) { "Offline save queue is full. Retry or reconcile existing saves first." }
        val id = UUID.randomUUID().toString()
        val body = input.apply { addProperty("requestId", id) }.toString()
        return PracticeWrite(id, path, method, body).also { validate(it); entries.add(it) }
    }
    fun acknowledge(id: String) { entries.removeAll { it.id == id } }
    fun block(id: String, detail: String) { val index = entries.indexOfFirst { it.id == id }; if (index >= 0) entries[index] = entries[index].copy(status = "Conflict", detail = detail.take(500)) }
    companion object {
        fun validate(write: PracticeWrite) {
            UUID.fromString(write.id)
            require(write.status in listOf("Pending", "Conflict") && write.detail.length <= 500 && write.json.toByteArray().size <= 100_000)
            val guid = "[0-9a-fA-F-]{36}"
            require((write.method == "POST" && (write.path == "/api/training/drills" || write.path == "/api/training/attempts" || write.path == "/api/coaching/attempts" ||
                Regex("/api/training/attempts/$guid/assessments").matches(write.path))) ||
                (write.method == "PATCH" && (Regex("/api/training/attempts/$guid/answer").matches(write.path) || Regex("/api/coaching/attempts/$guid").matches(write.path))))
            val body = JsonParser.parseString(write.json).asJsonObject
            require(body.get("requestId")?.asString == write.id)
        }
    }
}
