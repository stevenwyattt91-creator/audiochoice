package com.audiochoice.mobile.player

import com.audiochoice.contracts.ScanEvent

/**
 * The single definition of "is this filter still switched on".
 *
 * This condition was previously written out twice, verbatim: once in
 * PlayerViewModel's skip planner and once in the reader's masking pass. Any
 * change had to be made in both places or audio and text would disagree about
 * what is filtered, so it lives here and both callers delegate.
 */
object PlaybackFilterPredicate {

    /**
     * An event is enforced unless the listener disabled it at some level:
     * its category, its group, the individual event, or the aggregate control
     * (a censored word, or a cluster of nearby events) it belongs to.
     *
     * Category and group IDs are compared lowercased because the server is not
     * consistent about GUID casing. Event and aggregate keys are compared as-is:
     * the scanner emits lowercase hex, and normalising them here would silently
     * change which previously saved choices still match.
     */
    fun isEnabled(
        event: ScanEvent,
        disabledCategoryIDs: Set<String>,
        disabledGroupIDs: Set<String>,
        disabledEventKeys: Set<String>,
        disabledAggregateKeys: Set<String>,
    ): Boolean {
        if (event.categoryID.lowercase() in disabledCategoryIDs) return false
        if (event.groupID.lowercase() in disabledGroupIDs) return false
        if (event.stableKey.ifBlank { event.id } in disabledEventKeys) return false
        // isNullOrBlank rather than == null: an empty aggregate key means "no
        // aggregate", and treating it as one made a blank key match a disabled
        // set that could never legitimately contain it. PlaybackFilterTaxonomy
        // already classified blank keys as individual events, so this brings the
        // runtime predicate in line with the control the listener actually sees.
        val aggregateKey = event.aggregateKey
        if (!aggregateKey.isNullOrBlank() && aggregateKey in disabledAggregateKeys) return false
        return true
    }
}

/** Events still being enforced for the currently open book. */
fun PlayerUiState.enabledScanEvents(): List<ScanEvent> = scanEvents.filter { event ->
    PlaybackFilterPredicate.isEnabled(
        event = event,
        disabledCategoryIDs = disabledCategoryIDs,
        disabledGroupIDs = disabledGroupIDs,
        disabledEventKeys = disabledEventKeys,
        disabledAggregateKeys = disabledAggregateKeys,
    )
}
