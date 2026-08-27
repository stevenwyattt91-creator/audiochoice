package com.audiochoice.mobile.player

import com.audiochoice.contracts.ScanEvent
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The predicate that decides what playback skips and what the reader removes.
 *
 * Both surfaces call this one function, so these tests are what stop audio and
 * text from ever disagreeing about which filters are on.
 */
class PlaybackFilterPredicateTest {

    private fun event(
        id: String = "event-1",
        categoryID: String = "10000000-0000-0000-0000-000000000001",
        groupID: String = "11000000-0000-0000-0000-000000000005",
        stableKey: String = "stable-1",
        aggregateKey: String? = null,
    ) = ScanEvent(
        id = id,
        startTime = 1.0,
        endTime = 2.0,
        categoryID = categoryID,
        groupID = groupID,
        eventID = "11100000-0000-0000-0000-000000000005",
        confidence = .9,
        stableKey = stableKey,
        aggregateKey = aggregateKey,
    )

    private fun isEnabled(
        event: ScanEvent,
        categories: Set<String> = emptySet(),
        groups: Set<String> = emptySet(),
        eventKeys: Set<String> = emptySet(),
        aggregateKeys: Set<String> = emptySet(),
    ) = PlaybackFilterPredicate.isEnabled(event, categories, groups, eventKeys, aggregateKeys)

    @Test
    fun `nothing disabled means enforced`() {
        assertTrue(isEnabled(event()))
    }

    @Test
    fun `disabling any single level switches the event off`() {
        val subject = event()
        assertFalse(isEnabled(subject, categories = setOf(subject.categoryID.lowercase())))
        assertFalse(isEnabled(subject, groups = setOf(subject.groupID.lowercase())))
        assertFalse(isEnabled(subject, eventKeys = setOf("stable-1")))
    }

    /** The server is not consistent about GUID casing, so IDs compare lowercased. */
    @Test
    fun `category and group matching is case insensitive`() {
        val upper = event(
            categoryID = "ABCDEF00-0000-0000-0000-000000000001",
            groupID = "ABCDEF00-0000-0000-0000-000000000002",
        )
        assertFalse(isEnabled(upper, categories = setOf("abcdef00-0000-0000-0000-000000000001")))
        assertFalse(isEnabled(upper, groups = setOf("abcdef00-0000-0000-0000-000000000002")))
    }

    /** A blank stableKey falls back to the event id, matching the taxonomy. */
    @Test
    fun `a blank stable key falls back to the event id`() {
        val blank = event(id = "fallback-id", stableKey = "")
        assertFalse(isEnabled(blank, eventKeys = setOf("fallback-id")))
        assertTrue(isEnabled(blank, eventKeys = setOf("stable-1")))
    }

    @Test
    fun `an aggregate event is switched off by its aggregate key`() {
        val aggregate = event(aggregateKey = "word-hash")
        assertFalse(isEnabled(aggregate, aggregateKeys = setOf("word-hash")))
        assertTrue(isEnabled(aggregate, aggregateKeys = setOf("other-hash")))
    }

    /**
     * Regression: the predicate used to test `aggregateKey == null`, so an event
     * carrying an empty-string key took the aggregate branch even though
     * PlaybackFilterTaxonomy presents it as an individual control. The two now
     * agree, using isNullOrBlank.
     */
    @Test
    fun `a blank aggregate key is not treated as an aggregate`() {
        val blankAggregate = event(aggregateKey = "")
        assertTrue(isEnabled(blankAggregate, aggregateKeys = setOf("")))
    }

    @Test
    fun `an event disabled only at an unrelated level stays enforced`() {
        assertTrue(
            isEnabled(
                event(),
                categories = setOf("99999999-0000-0000-0000-000000000001"),
                groups = setOf("99999999-0000-0000-0000-000000000002"),
                eventKeys = setOf("someone-elses-key"),
                aggregateKeys = setOf("someone-elses-aggregate"),
            ),
        )
    }

    @Test
    fun `enabledScanEvents filters the whole state consistently`() {
        val keep = event(id = "keep", stableKey = "keep-key")
        val drop = event(id = "drop", stableKey = "drop-key")
        val state = PlayerUiState(
            scanEvents = listOf(keep, drop),
            disabledEventKeys = setOf("drop-key"),
        )
        assertTrue(state.enabledScanEvents().map { it.id } == listOf("keep"))
    }
}
