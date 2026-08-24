package com.audiochoice.mobile.player

import com.audiochoice.contracts.ScanEvent
import org.junit.Assert.assertEquals
import org.junit.Test

class PlaybackFilterTaxonomyTest {
    @Test
    fun repeatedProfanityIsOneUserFacingControlPerDistinctWord() {
        val events = listOf(
            profanityEvent("1", 1.0, "first-word", "f•••"),
            profanityEvent("2", 2.0, "first-word", "f•••"),
            profanityEvent("3", 3.0, "second-word", "s•••"),
        )

        val profanity = PlaybackFilterTaxonomy.available(events).single()
        val controls = profanity.children.single().events

        assertEquals(2, PlaybackFilterTaxonomy.controlCount(events))
        assertEquals(2, controls.size)
        assertEquals(listOf(2, 1), controls.sortedByDescending { it.count }.map { it.count })
    }

    @Test
    fun alcoholAndDrugOccurrencesAreOneControlEach() {
        val events = listOf(
            substanceEvent("1", 1.0, "41000000-0000-0000-0000-000000000001", "alcohol", "Alcohol use"),
            substanceEvent("2", 2.0, "41000000-0000-0000-0000-000000000001", "alcohol", "Alcohol use"),
            substanceEvent("3", 3.0, "41000000-0000-0000-0000-000000000004", "drugs", "Drug use"),
            substanceEvent("4", 4.0, "41000000-0000-0000-0000-000000000004", "drugs", "Drug use"),
        )

        assertEquals(2, PlaybackFilterTaxonomy.controlCount(events))
    }

    private fun substanceEvent(
        id: String,
        startTime: Double,
        groupID: String,
        aggregateKey: String,
        display: String,
    ) = ScanEvent(
        id = id,
        startTime = startTime,
        endTime = startTime + 0.5,
        categoryID = "40000000-0000-0000-0000-000000000001",
        groupID = groupID,
        eventID = "41100000-0000-0000-0000-000000000001",
        confidence = 1.0,
        aggregateKey = aggregateKey,
        aggregateDisplay = display,
    )

    private fun profanityEvent(
        id: String,
        startTime: Double,
        aggregateKey: String,
        display: String,
    ) = ScanEvent(
        id = id,
        startTime = startTime,
        endTime = startTime + 0.5,
        categoryID = "20000000-0000-0000-0000-000000000001",
        groupID = "21000000-0000-0000-0000-000000000001",
        eventID = "21100000-0000-0000-0000-000000000001",
        confidence = 1.0,
        aggregateKey = aggregateKey,
        aggregateDisplay = display,
    )
}
