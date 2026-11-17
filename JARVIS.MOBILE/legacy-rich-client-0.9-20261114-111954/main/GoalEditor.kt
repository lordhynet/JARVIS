package uk.jarvis.mobile

import uk.jarvis.mobile.contracts.PersonalGoal
import uk.jarvis.mobile.contracts.SaveGoalRequest
import java.time.*
import java.time.format.DateTimeFormatter
import java.time.format.ResolverStyle
import java.util.UUID

internal val goalWorkflows = listOf("practice-reminder", "interview-preparation", "daily-coaching", "career-plan", "personal-plan", "research-review")
internal val goalStates = listOf("Active", "Paused", "Completed", "Cancelled")
internal fun requiresGoalCloud(workflow: String) = workflow in listOf("career-plan", "personal-plan", "research-review")
private val goalTimeFormat = DateTimeFormatter.ofPattern("uuuu-MM-dd HH:mm").withResolverStyle(ResolverStyle.STRICT)
internal fun goalLocalTime(value: String, zone: ZoneId): String = OffsetDateTime.parse(value).atZoneSameInstant(zone).format(goalTimeFormat)
internal fun goalDisplayTime(value: String, zone: ZoneId): String = runCatching { goalLocalTime(value, zone) }.getOrDefault("Time unavailable; refresh Core")

data class GoalDraft(
    val id: String = "", val expectedRevision: Int? = null,
    val title: String = "", val brief: String = "", val workflow: String = "practice-reminder",
    val allowCloudProcessing: Boolean = false, val status: String = "Active",
    val reviewLocal: String = "", val deadlineLocal: String = "", val timeZone: String = "UTC",
    val originalReview: String? = null, val originalDeadline: String? = null
) {
    companion object {
        fun fresh(now: Instant = Instant.now(), zone: ZoneId = ZoneId.systemDefault()): GoalDraft {
            val review = now.atZone(zone).toLocalDate().plusDays(1).atTime(9, 0)
            return GoalDraft(id = UUID.randomUUID().toString(), reviewLocal = review.format(goalTimeFormat),
                deadlineLocal = review.plusDays(7).format(goalTimeFormat), timeZone = zone.id)
        }
        fun from(goal: PersonalGoal, zone: ZoneId = ZoneId.systemDefault()) = GoalDraft(
            goal.id, goal.revision, goal.title, goal.brief, goal.workflow, goal.allowCloudProcessing, goal.status,
            goalLocalTime(goal.nextReviewAt, zone), goalLocalTime(goal.deadline, zone), zone.id, goal.nextReviewAt, goal.deadline
        )
    }
    fun request(now: Instant = Instant.now()): SaveGoalRequest {
        UUID.fromString(id)
        require(expectedRevision == null || expectedRevision >= 1) { "Reload the goal's current version before saving." }
        val cleanTitle = title.trim(); val cleanBrief = brief.trim()
        require(cleanTitle.isNotEmpty() && cleanTitle.length <= 120) { "Enter a goal title of up to 120 characters." }
        require(cleanBrief.isNotEmpty() && cleanBrief.length <= 2000) { "Enter goal context and a next step, up to 2,000 characters." }
        require(workflow in goalWorkflows && status in goalStates) { "Choose a supported goal workflow and state." }
        require(!requiresGoalCloud(workflow) || allowCloudProcessing) { "This workflow requires explicit cloud-processing consent." }
        val zone = ZoneId.of(timeZone)
        fun instant(local: String, original: String?): Instant {
            if (original != null && goalLocalTime(original, zone) == local) return OffsetDateTime.parse(original).toInstant()
            val value = try { LocalDateTime.parse(local, goalTimeFormat) } catch (_: Exception) {
                throw IllegalArgumentException("Use yyyy-MM-dd HH:mm for review and deadline, for example 2026-12-01 09:00.")
            }
            val offsets = zone.rules.getValidOffsets(value)
            require(value.year in 1..9999) { "Choose a calendar year between 1 and 9999." }
            require(offsets.size == 1) { "That time falls in a clock-change gap or overlap in $timeZone. Choose another time." }
            return value.toInstant(offsets.single())
        }
        val review = instant(reviewLocal, originalReview); val deadline = instant(deadlineLocal, originalDeadline)
        require(deadline > review) { "The deadline must be after the next review." }
        require(deadline <= now.atOffset(ZoneOffset.UTC).plusYears(5).toInstant()) { "Choose a deadline within five years." }
        require(status != "Active" || deadline > now) { "Active goals need a future deadline. Change the dates or choose a completed/paused state." }
        return SaveGoalRequest(id, expectedRevision, cleanTitle, cleanBrief, workflow,
            allowCloudProcessing && workflow != "practice-reminder", review.toString(), deadline.toString(), status)
    }
}

internal enum class GoalSaveCheck { Confirmed, Retry, Conflict, Missing }

internal fun ScreenState.refreshedGoals(latest: List<PersonalGoal>): ScreenState = copy(
    goals = latest, offline = false,
    goalServerVersion = if (goalConflict) latest.firstOrNull { it.id == goalDraft?.id } else goalServerVersion
)

/** Reconcile a lost acknowledgement without creating a new ID or replaying an edit blindly. */
internal fun checkGoalSave(request: SaveGoalRequest, saved: PersonalGoal?): GoalSaveCheck {
    if (saved == null) return if (request.expectedRevision == null) GoalSaveCheck.Retry else GoalSaveCheck.Missing
    if (saved.status == "Deleted") return GoalSaveCheck.Missing
    if (saved.revision < 1) return GoalSaveCheck.Conflict
    if (saved.id != request.requestId) return GoalSaveCheck.Conflict
    fun sameInstant(a: String, b: String) = runCatching { OffsetDateTime.parse(a).toInstant() == OffsetDateTime.parse(b).toInstant() }.getOrDefault(false)
    val same = saved.title == request.title.trim() && saved.brief == request.brief.trim() && saved.workflow == request.workflow &&
        saved.allowCloudProcessing == request.allowCloudProcessing && saved.status == request.status &&
        sameInstant(saved.nextReviewAt, request.nextReviewAt) && sameInstant(saved.deadline, request.deadline)
    val expected = request.expectedRevision
    if (same && (expected == null || saved.revision == expected + 1)) return GoalSaveCheck.Confirmed
    return if (expected != null && saved.revision == expected) GoalSaveCheck.Retry else GoalSaveCheck.Conflict
}
