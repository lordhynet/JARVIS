package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test

class CompanionPolicyTest {
    @Test fun only_voice_device_and_privacy_transport_routes_are_available() {
        for (path in listOf("/api/privacy/status", "/api/voice/status", "/api/devices")) assertTrue(CompanionRoutes.allowed(path, "GET"))
        for (path in listOf("/api/devices", "/api/voice/prepare", "/api/voice/tickets", "/api/devices/android-123/heartbeat")) assertTrue(CompanionRoutes.allowed(path, "POST"))
        assertFalse(CompanionRoutes.allowed("/api/devices/android-123/verify", "POST"))
    }
    @Test fun feature_pages_and_business_commands_cannot_creep_into_the_phone_client() {
        for (path in listOf("/api/goals", "/api/stories", "/api/imports", "/api/training/attempts", "/api/coaching/attempts", "/api/agents/runs", "/api/sessions", "/api/voice/transcribe"))
            for (method in listOf("GET", "POST", "PATCH", "PUT", "DELETE")) assertFalse("$method $path", CompanionRoutes.allowed(path, method))
        assertFalse(CompanionRoutes.allowed("/api/devices/a/../goals/heartbeat", "POST"))
        assertFalse(CompanionRoutes.allowed("/api/voice/prepare?key=secret", "POST"))
    }
    @Test fun permission_denials_cannot_issue_voice_start() {
        val flow = AudioPermissionFlow(); flow.begin()
        assertTrue(flow.complete(false, true) is AudioPermissionResult.Denied)
        assertEquals(AudioPermissionResult.Cancelled, flow.complete(true, true))
        flow.begin(); assertTrue(flow.complete(true, false) is AudioPermissionResult.Denied)
    }
    @Test fun cancelled_or_backgrounded_starts_reject_late_core_preparation() {
        val flow = AudioPermissionFlow(); assertTrue(flow.begin()); assertFalse(flow.begin())
        val ready = flow.complete(true, true) as AudioPermissionResult.Ready
        assertTrue(flow.isCurrent(ready.generation)); flow.cancel(); assertFalse(flow.isCurrent(ready.generation))
        assertEquals(AudioPermissionResult.Cancelled, flow.complete(true, true))
    }
    @Test fun headset_controls_stop_or_interrupt_but_never_start_capture() {
        for (key in listOf(79, 85, 86, 127)) assertEquals(HeadsetCommand.StopLive, HeadsetControls.command(key, true, 0, true))
        assertEquals(HeadsetCommand.InterruptLive, HeadsetControls.command(87, true, 0, true))
        assertEquals(HeadsetCommand.None, HeadsetControls.command(126, true, 0, true))
        for (key in listOf(79, 85, 86, 87, 126, 127)) {
            assertEquals(HeadsetCommand.None, HeadsetControls.command(key, false, 0, true))
            assertEquals(HeadsetCommand.None, HeadsetControls.command(key, true, 2, true))
            assertEquals(HeadsetCommand.None, HeadsetControls.command(key, true, 0, false))
        }
    }
    @Test fun new_activity_has_no_pending_microphone_permission_request() {
        assertEquals(AudioPermissionResult.Cancelled, AudioPermissionFlow().complete(true, true))
    }
}
