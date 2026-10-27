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

    // Real production case: a 124-second, 0.95-confidence "Complete sex scenes" event shared
    // an aggregateKey with four much shorter "Suggestive dialogue"/"Nudity" events from the
    // same passage. Picking the first chronological subgroup for the whole aggregate picked
    // Suggestive dialogue (groupID 000...001) over Complete sex scenes (000...006), and the
    // listener never saw a Complete sex scenes control for that passage at all -- only the
    // lesser labels. A listener must always be able to find and turn off the complete-scene
    // control for a passage, regardless of how many lesser labels happen to overlap it.
    @Test
    fun completeSceneNeverLosesTheAggregateTieBreakToALesserLabel() {
        val events = listOf(
            // Chronologically first, and would have won the old "first wins" tie-break.
            sexualEvent("1", 19881.86, "11000000-0000-0000-0000-000000000001", "scene"),
            // The real scene event: starts at the same instant but runs far longer.
            sexualEvent("2", 19881.86, "11000000-0000-0000-0000-000000000006", "scene"),
            sexualEvent("3", 19899.18, "11000000-0000-0000-0000-000000000001", "scene"),
            sexualEvent("4", 19904.18, "11000000-0000-0000-0000-000000000001", "scene"),
            sexualEvent("5", 19914.32, "11000000-0000-0000-0000-000000000001", "scene"),
        )

        val parents = PlaybackFilterTaxonomy.available(events)
        val childLabels = parents.flatMap { it.children }.map { it.label }

        assertEquals(listOf("Complete sex scenes"), childLabels)
    }

    // Sexual violence (groupID 000...007) gets the same protection as Complete sex scenes,
    // and the two never suppress each other when both happen to be present -- whichever of
    // the two is chronologically first wins, since both are already "never suppressed".
    @Test
    fun sexualViolenceAlsoNeverLosesTheAggregateTieBreak() {
        val events = listOf(
            sexualEvent("1", 100.0, "11000000-0000-0000-0000-000000000003", "scene"),
            sexualEvent("2", 100.0, "11000000-0000-0000-0000-000000000007", "scene"),
        )

        val parents = PlaybackFilterTaxonomy.available(events)
        val childLabels = parents.flatMap { it.children }.map { it.label }

        assertEquals(listOf("Sexual violence (rape / sexual assault)"), childLabels)
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
