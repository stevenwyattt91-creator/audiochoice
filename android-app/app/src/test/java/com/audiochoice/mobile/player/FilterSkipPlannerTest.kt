package com.audiochoice.mobile.player

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class FilterSkipPlannerTest {
    @Test
    fun repeatedEntryAlwaysReturnsTheSameSkipTarget() {
        val windows = listOf(FilterWindow(100.0, 140.0))

        assertEquals(140.0, FilterSkipPlanner.targetSeconds(110.0, windows, 0.0)!!, 0.0)
        assertEquals(140.0, FilterSkipPlanner.targetSeconds(110.0, windows, 0.0)!!, 0.0)
    }

    @Test
    fun overlappingEventsSkipPastTheWholeConnectedScene() {
        val windows = listOf(
            FilterWindow(100.0, 110.0),
            FilterWindow(105.0, 180.0),
            FilterWindow(175.0, 220.0),
        )

        assertEquals(220.0, FilterSkipPlanner.targetSeconds(101.0, windows, 0.0)!!, 0.0)
    }

    @Test
    fun lookAheadCatchesAFilterBeforeAudioReachesIt() {
        val windows = listOf(FilterWindow(50.2, 51.0))

        assertEquals(51.0, FilterSkipPlanner.targetSeconds(50.0, windows, 0.25)!!, 0.0)
        assertNull(FilterSkipPlanner.targetSeconds(50.0, windows, 0.1))
    }
}
