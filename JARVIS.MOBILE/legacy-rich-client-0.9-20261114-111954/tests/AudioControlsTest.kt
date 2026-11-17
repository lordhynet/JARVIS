package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test

class AudioControlsTest {
    @Test fun granted_permissions_keep_the_explicit_requested_mode() {
        for (mode in AudioStartMode.entries) {
            val flow = AudioPermissionFlow(); assertTrue(flow.begin(mode))
            val ready = flow.complete(true, true) as AudioPermissionResult.Ready
            assertEquals(mode, ready.request.mode); assertTrue(flow.isCurrent(ready.request))
        }
    }
    @Test fun microphone_denial_explains_recovery_without_issuing_a_start() {
        val flow = AudioPermissionFlow(); flow.begin(AudioStartMode.Record)
        val denied = flow.complete(false, true) as AudioPermissionResult.Denied
        assertTrue(denied.message.contains("Microphone"))
        assertEquals(AudioPermissionResult.Cancelled, flow.complete(true, true))
    }
    @Test fun notification_denial_explains_visible_stop_controls() {
        val flow = AudioPermissionFlow(); flow.begin(AudioStartMode.Live)
        val denied = flow.complete(true, false) as AudioPermissionResult.Denied
        assertTrue(denied.message.contains("notifications")); assertTrue(denied.message.contains("Stop"))
    }
    @Test fun cancelled_permission_prompt_cannot_start_capture_after_a_late_grant() {
        val flow = AudioPermissionFlow(); flow.begin(AudioStartMode.Live); flow.cancel()
        assertEquals(AudioPermissionResult.Cancelled, flow.complete(true, true))
    }
    @Test fun stop_or_backgrounding_after_permission_invalidates_a_late_core_claim() {
        val flow = AudioPermissionFlow(); flow.begin(AudioStartMode.Record)
        val ready = flow.complete(true, true) as AudioPermissionResult.Ready
        flow.cancel(); assertFalse(flow.isCurrent(ready.request))
    }
    @Test fun newer_gesture_invalidates_old_claim_and_duplicate_prompt_does_not_change_mode() {
        val flow = AudioPermissionFlow(); flow.begin(AudioStartMode.Record)
        assertFalse(flow.begin(AudioStartMode.Live))
        val first = flow.complete(true, true) as AudioPermissionResult.Ready
        assertEquals(AudioStartMode.Record, first.request.mode)
        assertTrue(flow.begin(AudioStartMode.Live)); assertFalse(flow.isCurrent(first.request))
        assertEquals(AudioStartMode.Live, (flow.complete(true, true) as AudioPermissionResult.Ready).request.mode)
    }
    @Test fun recreation_or_duplicate_callback_without_pending_consent_never_starts_audio() {
        assertEquals(AudioPermissionResult.Cancelled, AudioPermissionFlow().complete(true, true))
        val flow = AudioPermissionFlow(); flow.begin(AudioStartMode.Record); flow.complete(true, true)
        assertEquals(AudioPermissionResult.Cancelled, flow.complete(true, true))
    }
    @Test fun headset_pause_finishes_locally_and_stop_discards_recording() {
        for (key in listOf(79, 85, 127)) assertEquals(HeadsetCommand.FinishRecording, HeadsetControls.command(false, key, true, 0, true))
        assertEquals(HeadsetCommand.DiscardRecording, HeadsetControls.command(false, 86, true, 0, true))
        assertEquals(HeadsetCommand.None, HeadsetControls.command(false, 87, true, 0, true))
    }
    @Test fun live_headset_pause_stop_and_next_have_distinct_actions() {
        for (key in listOf(79, 85, 86, 127)) assertEquals(HeadsetCommand.StopLive, HeadsetControls.command(true, key, true, 0, true))
        assertEquals(HeadsetCommand.InterruptLive, HeadsetControls.command(true, 87, true, 0, true))
    }
    @Test fun repeats_keyups_play_and_inactive_sessions_never_start_or_finish_capture() {
        for (live in listOf(false, true)) for (key in listOf(79, 85, 86, 87, 126, 127)) {
            assertEquals(HeadsetCommand.None, HeadsetControls.command(live, key, false, 0, true))
            assertEquals(HeadsetCommand.None, HeadsetControls.command(live, key, true, 1, true))
            assertEquals(HeadsetCommand.None, HeadsetControls.command(live, key, true, 0, false))
        }
        for (live in listOf(false, true)) assertEquals(HeadsetCommand.None, HeadsetControls.command(live, 126, true, 0, true))
        assertFalse(HeadsetControls.handles(24)) // Volume remains controlled by Android.
    }
    @Test fun repeated_finish_or_discard_cannot_replace_the_first_recording_outcome() {
        val gate = CaptureStopGate(); val retained = mutableListOf<String>()
        if (gate.take()) retained.add("recording ready for review")
        if (gate.take()) retained.clear()
        assertEquals(listOf("recording ready for review"), retained)
        assertTrue(gate.stopped); assertFalse(gate.take())
        assertTrue(CaptureStopGate().take())
    }
}
