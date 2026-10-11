package com.audiochoice.mobile.player

import com.audiochoice.contracts.ScanEvent
import org.junit.Assert.assertEquals
import org.junit.Test

class PlaybackFilterTaxonomyTest {
    // Profanity and substance-use controls were removed along with their taxonomy entries
    // when this deployment was permanently narrowed to sexual content only (see the backend's
    // ContentTaxonomy.EnforcedLabels remarks and this file's own definitions). An event in
    // either category legitimately produces zero visible controls now, since
    // PlaybackFilterTaxonomy.available() has no definition for their groupIDs any more --
    // that is the intended outcome, not something to keep testing for the old behavior.

    @Test
    fun oneAggregateAcrossSexualSubgroupsIsOneControl() {
        val events = listOf(
            sexualEvent("1", 14.50, "11000000-0000-0000-0000-000000000002", "passage"),
            sexualEvent("2", 14.50, "11000000-0000-0000-0000-000000000003", "passage"),
        )

        assertEquals(1, PlaybackFilterTaxonomy.controlCount(events))
    }

    private fun sexualEvent(id: String, startTime: Double, groupID: String, aggregateKey: String) =
        ScanEvent(
            id = id,
            startTime = startTime,
            endTime = startTime + 1,
            categoryID = "10000000-0000-0000-0000-000000000001",
            groupID = groupID,
            eventID = "11100000-0000-0000-0000-000000000001",
            confidence = 1.0,
            aggregateKey = aggregateKey,
            aggregateDisplay = "Related sexual content in a continuous passage",
        )

}
