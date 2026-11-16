package uk.jarvis.mobile

import org.junit.Assert.*
import org.junit.Test

class VoiceLifecycleTest {
    @Test fun wakeArbitrationAndReconnectUseTheSameLiveGenerationUntilStop() {
        val lifecycle = VoiceLifecycle()
        assertTrue(lifecycle.listen())
        val arbitration = lifecycle.beginArbitration()!!
        assertEquals(VoicePhase.Arbitrating, lifecycle.phase)
        assertTrue(lifecycle.isArbitrating(arbitration))
        assertNull(lifecycle.beginArbitration())

        val live = lifecycle.beginLive()!!
        assertFalse(lifecycle.isArbitrating(arbitration))
        assertFalse(lifecycle.ready)
        assertTrue(lifecycle.markReady(live))
        assertTrue(lifecycle.ready)
        assertTrue(lifecycle.pauseWindow(live))
        assertFalse(lifecycle.ready)
        assertTrue(lifecycle.beginReconnect(live))
        assertEquals(VoicePhase.Reconnecting, lifecycle.phase)
        assertTrue(lifecycle.isLive(live))
        assertFalse(lifecycle.ready)
        assertTrue(lifecycle.markReady(live))
        assertTrue(lifecycle.ready)
    }

    @Test fun stoppingRejectsOldReadyAndWakeCallbacksAndCanResumeLocalListening() {
        val lifecycle = VoiceLifecycle()
        lifecycle.listen()
        val first = lifecycle.beginArbitration()!!
        assertTrue(lifecycle.beginStop())
        assertFalse(lifecycle.isArbitrating(first))
        assertNull(lifecycle.beginLive())
        assertFalse(lifecycle.beginStop())
        lifecycle.finishStop(keepWake = true)

        val next = lifecycle.beginArbitration()!!
        assertTrue(lifecycle.isArbitrating(next))
        assertFalse(lifecycle.isArbitrating(first))
        val live = lifecycle.beginLive()!!
        assertTrue(lifecycle.beginStop())
        assertFalse(lifecycle.markReady(live))
        assertFalse(lifecycle.beginReconnect(live))
        lifecycle.finishStop(keepWake = false)
        assertEquals(VoicePhase.Idle, lifecycle.phase)
        assertFalse(lifecycle.active)
    }
}
