package uk.jarvis.mobile

import com.google.gson.Gson
import org.junit.Assert.*
import org.junit.Test
import uk.jarvis.mobile.contracts.PersonalGoal
import uk.jarvis.mobile.contracts.SaveGoalRequest
import java.time.Instant
import java.time.ZoneId

class GoalEditorTest {
    private val now = Instant.parse("2026-11-14T12:00:00Z")
    private val london = ZoneId.of("Europe/London")
    private fun draft() = GoalDraft.fresh(now, london).copy(title = " Prepare one real example ", brief = " Explain ownership and a trade-off. ")
    private fun saved(request: SaveGoalRequest, revision: Int = 1) = PersonalGoal(id = request.requestId, revision = revision,
        title = request.title, brief = request.brief, workflow = request.workflow, allowCloudProcessing = request.allowCloudProcessing,
        nextReviewAt = request.nextReviewAt, deadline = request.deadline, status = request.status)

    @Test fun new_goal_defaults_to_an_unpaid_future_review_and_keeps_one_id() {
        val draft = draft(); val request = draft.request(now)
        assertEquals("practice-reminder", request.workflow); assertFalse(request.allowCloudProcessing)
        assertEquals("2026-11-15T09:00:00Z", request.nextReviewAt); assertNull(request.expectedRevision)
        assertEquals("Prepare one real example", request.title)
        assertEquals(request.requestId, draft.copy(brief = "Changed before dispatch").request(now).requestId)
        assertEquals(london.id, draft.timeZone)
    }
    @Test fun refreshing_a_conflict_updates_the_comparison_without_overwriting_the_draft_or_pending_body() {
        val draft = draft().copy(expectedRevision = 1, brief = "My unsent draft")
        val request = draft.request(now); val old = saved(request, 2).copy(brief = "Other device")
        val current = old.copy(revision = 3, brief = "Latest Core text")
        val state = ScreenState(goals = listOf(old), goalDraft = draft, goalConflict = true, goalServerVersion = old, pendingGoalSave = request, offline = true)
        val refreshed = state.refreshedGoals(listOf(current))
        assertEquals(current, refreshed.goalServerVersion); assertEquals(draft, refreshed.goalDraft)
        assertEquals(request, refreshed.pendingGoalSave); assertTrue(refreshed.goalConflict); assertFalse(refreshed.offline)
        assertNull(refreshed.refreshedGoals(emptyList()).goalServerVersion)
    }
    @Test fun local_reminders_never_enable_cloud_even_if_an_old_flag_was_present() {
        assertFalse(draft().copy(allowCloudProcessing = true).request(now).allowCloudProcessing)
    }
    @Test fun cloud_only_workflows_need_explicit_consent_but_local_coaching_does_not() {
        for (workflow in listOf("research-review", "career-plan", "personal-plan")) {
            assertThrows(IllegalArgumentException::class.java) { draft().copy(workflow = workflow).request(now) }
            assertTrue(draft().copy(workflow = workflow, allowCloudProcessing = true).request(now).allowCloudProcessing)
        }
        for (workflow in listOf("interview-preparation", "daily-coaching")) assertFalse(draft().copy(workflow = workflow).request(now).allowCloudProcessing)
    }
    @Test fun bad_dates_ranges_and_empty_fields_cannot_create_a_request() {
        val invalid = listOf(draft().copy(title = " "), draft().copy(brief = ""), draft().copy(title = "x".repeat(121)),
            draft().copy(brief = "x".repeat(2001)), draft().copy(workflow = "external-action"), draft().copy(status = "Deleted"),
            draft().copy(reviewLocal = "2026-02-30 09:00"), draft().copy(reviewLocal = "0000-01-01 09:00"), draft().copy(deadlineLocal = "2026-11-15 08:00"),
            draft().copy(deadlineLocal = "2032-01-01 09:00"), draft().copy(reviewLocal = "2026-11-01 09:00", deadlineLocal = "2026-11-02 09:00"))
        for (bad in invalid) assertThrows(IllegalArgumentException::class.java) { bad.request(now) }
    }
    @Test fun completed_goals_can_record_a_past_deadline_without_starting_new_work() {
        val request = draft().copy(status = "Completed", reviewLocal = "2026-11-01 09:00", deadlineLocal = "2026-11-02 09:00").request(now)
        assertEquals("Completed", request.status)
    }
    @Test fun malformed_remote_dates_do_not_crash_goal_cards() {
        assertEquals("Time unavailable; refresh Core", goalDisplayTime("not-a-time", london))
    }
    @Test fun clock_change_gaps_and_ambiguous_local_times_are_rejected() {
        val earlier = Instant.parse("2026-01-01T00:00:00Z")
        for (time in listOf("2027-03-28 01:30", "2026-10-25 01:30")) {
            assertThrows(IllegalArgumentException::class.java) { draft().copy(reviewLocal = time, deadlineLocal = "2027-04-01 09:00").request(earlier) }
        }
    }
    @Test fun existing_precise_or_ambiguous_times_are_preserved_until_explicitly_edited() {
        val goal = saved(draft().request(now)).copy(nextReviewAt = "2027-10-31T01:30:20.1234567+01:00", deadline = "2027-11-01T12:15:30Z")
        val request = GoalDraft.from(goal, london).request(now)
        assertEquals(Instant.parse("2027-10-31T00:30:20.123456700Z"), Instant.parse(request.nextReviewAt))
        assertEquals(Instant.parse(goal.deadline), Instant.parse(request.deadline))
        val changed = GoalDraft.from(goal, london).copy(reviewLocal = "2027-10-31 03:00").request(now)
        assertEquals("2027-10-31T03:00:00Z", changed.nextReviewAt)
    }
    @Test fun draft_zone_stays_fixed_across_device_zone_changes_and_serialization() {
        val original = draft(); val restored = Gson().fromJson(Gson().toJson(original), GoalDraft::class.java)
        assertEquals(original.request(now), restored.request(now))
        val sameGoal = saved(original.request(now))
        assertEquals(original.request(now).nextReviewAt, GoalDraft.from(sameGoal, ZoneId.of("Asia/Tokyo")).request(now).nextReviewAt)
    }
    @Test fun acknowledged_create_is_found_without_minting_a_second_id() {
        val request = draft().request(now)
        assertEquals(GoalSaveCheck.Retry, checkGoalSave(request, null))
        assertEquals(GoalSaveCheck.Confirmed, checkGoalSave(request, saved(request)))
        assertEquals(GoalSaveCheck.Conflict, checkGoalSave(request, saved(request).copy(brief = "Changed on another device")))
    }
    @Test fun lost_update_acknowledgement_matches_only_the_expected_next_revision() {
        val request = draft().copy(expectedRevision = 4).request(now)
        assertEquals(GoalSaveCheck.Retry, checkGoalSave(request, saved(request, 4)))
        assertEquals(GoalSaveCheck.Confirmed, checkGoalSave(request, saved(request, 5)))
        assertEquals(GoalSaveCheck.Conflict, checkGoalSave(request, saved(request, 6)))
        assertEquals(GoalSaveCheck.Conflict, checkGoalSave(request, saved(request, 5).copy(status = "Paused")))
        assertEquals(GoalSaveCheck.Missing, checkGoalSave(request, null))
        assertEquals(GoalSaveCheck.Missing, checkGoalSave(request, saved(request, 5).copy(status = "Deleted")))
        assertEquals(GoalSaveCheck.Conflict, checkGoalSave(request, saved(request, 0)))
    }
    @Test fun reconciliation_compares_instants_and_all_automation_fields() {
        val request = draft().request(now); val goal = saved(request)
        assertEquals(GoalSaveCheck.Confirmed, checkGoalSave(request, goal.copy(nextReviewAt = "2026-11-15T10:00:00+01:00")))
        for (changed in listOf(goal.copy(id = java.util.UUID.randomUUID().toString()), goal.copy(title = "Other"),
            goal.copy(workflow = "daily-coaching"), goal.copy(allowCloudProcessing = true), goal.copy(deadline = "2026-11-23T09:00:00Z"))) {
            assertEquals(GoalSaveCheck.Conflict, checkGoalSave(request, changed))
        }
    }
    @Test fun encrypted_snapshot_payload_keeps_draft_and_exact_unconfirmed_request_after_restart() {
        val draft = draft(); val request = draft.request(now)
        val state = ScreenState(goalDraft = draft, pendingGoalSave = request)
        val snapshot = PracticeSnapshot(state = state, writes = emptyList(), coreEpoch = 0)
        val restored = Gson().fromJson(Gson().toJson(snapshot), PracticeSnapshot::class.java)
        assertEquals(draft, restored.state.goalDraft); assertEquals(request, restored.state.pendingGoalSave)
        assertEquals(request.requestId, restored.state.goalDraft!!.id)
        assertThrows(IllegalArgumentException::class.java) {
            PracticeQueue().prepare("/api/goals", "POST", Gson().toJson(request))
        }
    }
    @Test fun bounded_goal_download_fits_the_full_catalog_even_with_json_escaped_text() {
        val goal = saved(draft().request(now)).copy(title = "<".repeat(120), brief = "<".repeat(2000))
        val bytes = Gson().toJson(List(200) { goal }).toByteArray(Charsets.UTF_8)
        assertTrue(bytes.size > 1_048_576)
        assertTrue(bytes.size < coreResponseLimit("/api/goals", "GET"))
        assertEquals(1_048_576, coreResponseLimit("/api/goals", "POST"))
        assertEquals(1_048_576, coreResponseLimit("/api/sessions", "GET"))
    }
}
