package uk.jarvis.mobile

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.yield
import org.junit.Assert.*
import org.junit.Test

class NoisyRouteRecheckTest {
    @Test fun a_real_disconnect_inside_grace_stops_at_the_end_of_the_window() = runBlocking {
        var now = 1000L; var stops = 0
        val due = CompletableDeferred<Unit>(); val waits = mutableListOf<Long>()
        val route = NoisyRouteRecheck(this,
            { AudioRoutePolicy.noisyRecheckDelayMs(CompanionAudioPhase.LiveVoice, now, 0) },
            { AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, now, 0, false, false) },
            { stops++ }, { waits += it; due.await() })
        route.noisy(); yield()
        assertEquals(listOf(2000L), waits); assertEquals(0, stops)
        now = 3000; due.complete(Unit); yield()
        assertEquals(1, stops)
    }

    @Test fun a_noisy_burst_schedules_one_check_and_a_headset_that_connected_stays_live() = runBlocking {
        var now = 1000L; var connected = false; var stops = 0; var checks = 0
        val due = CompletableDeferred<Unit>()
        val route = NoisyRouteRecheck(this,
            { AudioRoutePolicy.noisyRecheckDelayMs(CompanionAudioPhase.LiveVoice, now, 0) },
            { AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, now, 0, connected, connected) },
            { stops++ }, { checks++; due.await() })
        repeat(5) { route.noisy() }; yield()
        assertEquals(1, checks)
        now = 3000; connected = true; due.complete(Unit); yield()
        assertEquals(0, stops); assertEquals(1, checks)
    }

    @Test fun stopping_voice_cancels_the_old_route_check() = runBlocking {
        var now = 1000L; var stops = 0
        val due = CompletableDeferred<Unit>()
        val route = NoisyRouteRecheck(this,
            { AudioRoutePolicy.noisyRecheckDelayMs(CompanionAudioPhase.LiveVoice, now, 0) },
            { AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, now, 0, false, false) },
            { stops++ }, { due.await() })
        route.noisy(); yield(); route.cancel()
        now = 3000; due.complete(Unit); yield()
        assertEquals(0, stops)
    }

    @Test fun another_own_route_change_moves_the_single_check_to_its_new_boundary() = runBlocking {
        var now = 1000L; var lastSwitch = 0L; var stops = 0
        val due = listOf(CompletableDeferred<Unit>(), CompletableDeferred<Unit>())
        val waits = mutableListOf<Long>()
        val route = NoisyRouteRecheck(this,
            { AudioRoutePolicy.noisyRecheckDelayMs(CompanionAudioPhase.LiveVoice, now, lastSwitch) },
            { AudioRoutePolicy.stopForNoisy(CompanionAudioPhase.LiveVoice, now, lastSwitch, false, false) },
            { stops++ }, { waits += it; due[waits.lastIndex].await() })
        route.noisy(); yield()
        lastSwitch = 2500; now = 3000; due[0].complete(Unit); yield(); yield()
        assertEquals(listOf(2000L, 2500L), waits); assertEquals(0, stops)
        now = 5500; due[1].complete(Unit); yield()
        assertEquals(1, stops)
    }
}
