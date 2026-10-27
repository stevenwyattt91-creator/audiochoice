package com.audiochoice.mobile.player

import com.audiochoice.contracts.ScanEvent

data class PlaybackFilterEvent(
    val key: String,
    val label: String,
    val count: Int,
    val startTime: Double?,
    val aggregate: Boolean,
)
data class PlaybackFilterChild(
    val id: String,
    val label: String,
    val categoryID: String,
    val events: List<PlaybackFilterEvent>,
) { val eventCount: Int get() = events.size }
data class PlaybackFilterParent(
    val id: String,
    val label: String,
    val children: List<PlaybackFilterChild>,
)

object PlaybackFilterTaxonomy {
    private data class Definition(val parentLabel: String, val childLabel: String)

    private fun group(category: Int, group: Int) =
        "${category}1000000-0000-0000-0000-${group.toString().padStart(12, '0')}"

    // Sexual content only, permanently -- see the backend's ContentTaxonomy.EnforcedLabels
    // remarks. Profanity, Violence, Drugs & Alcohol, Blasphemy, and Self-Harm & Suicide are
    // removed here too, not just left to stop appearing on their own: a book scanned before
    // this change may still carry old events in those groups, and this makes sure none of
    // them render a switch even for those, matching the backend's own permanent removal.
    private val definitions = buildMap {
        fun add(c: Int, labels: List<String>, parent: String) = labels.forEachIndexed { index, label ->
            put(group(c, index + 1), Definition(parent, label))
        }
        add(1, listOf("Suggestive dialogue", "Sexual references", "Nudity", "Implied sexual activity", "Explicit sexual activity", "Complete sex scenes", "Sexual violence (rape / sexual assault)", "Kissing"), "Sexual Content")
    }

    // Complete sex scenes and sexual violence are never allowed to lose the aggregate
    // tie-break to a lower-rung label sharing the same aggregateKey (see the real production
    // bug this guards against, below). A real scan found a 124-second, 0.95-confidence
    // "Complete sex scenes" event sharing an aggregateKey with four much shorter nearby
    // "Suggestive dialogue"/"Nudity" events from the same passage -- exactly the overlap this
    // aggregation exists to collapse. Picking "first chronologically" silently hid the scene
    // event entirely (its group lost the tie) while the minor labels kept their own switches,
    // so a listener saw every lesser label for that passage except the one that actually
    // mattered. These two groupIDs are the only ones a listener must always be able to find
    // and turn off for a given passage, however many lesser labels happen to overlap it.
    private val neverSuppressedGroupIDs = setOf(
        group(1, 6), // sexual_complete_scene
        group(1, 7), // sexual_violence
    )

    fun available(events: List<ScanEvent>): List<PlaybackFilterParent> {
        // One aggregate can span several taxonomy subgroups. Show it once, preferring a
        // Complete sex scenes/Sexual violence subgroup over any other sharing the same
        // aggregateKey (see neverSuppressedGroupIDs), then falling back to the first
        // chronological subgroup, while playback retains every event's own range regardless.
        val preferredGroupByKey = mutableMapOf<String, String>()
        events.sortedBy { it.startTime }.forEach { event ->
            event.aggregateKey?.takeIf { it.isNotBlank() }?.let { key ->
                val current = preferredGroupByKey[key]
                val eventIsNeverSuppressed =
                    neverSuppressedGroupIDs.any { it.equals(event.groupID, ignoreCase = true) }
                val currentIsNeverSuppressed =
                    current != null && neverSuppressedGroupIDs.any { it.equals(current, ignoreCase = true) }
                if (current == null || (eventIsNeverSuppressed && !currentIsNeverSuppressed)) {
                    preferredGroupByKey[key] = event.groupID
                }
            }
        }
        val visibleEvents = events.filter { event ->
            event.aggregateKey.isNullOrBlank() ||
                preferredGroupByKey[event.aggregateKey] == event.groupID
        }
        return visibleEvents.groupBy { it.groupID.lowercase() }
        .mapNotNull { (groupID, groupEvents) ->
            val definition = definitions[groupID] ?: return@mapNotNull null
            val categoryID = groupEvents.first().categoryID.lowercase()
            val aggregateEvents = groupEvents.filter { !it.aggregateKey.isNullOrBlank() }
                .groupBy { it.aggregateKey!! }
                .map { (key, values) -> PlaybackFilterEvent(
                    key, values.first().aggregateDisplay ?: "Censored word", values.size, null, true,
                ) }
            val individualEvents = groupEvents.filter { it.aggregateKey.isNullOrBlank() }.map { event ->
                PlaybackFilterEvent(
                    event.stableKey.ifBlank { event.id }, event.safeDescription,
                    1, event.startTime, false,
                )
            }
            definition.parentLabel to PlaybackFilterChild(
                groupID, definition.childLabel, categoryID,
                (aggregateEvents + individualEvents).sortedWith(compareBy({ it.startTime ?: Double.MAX_VALUE }, { it.label })),
            )
        }
        .groupBy({ it.first }, { it.second })
        .map { (label, children) -> PlaybackFilterParent(
            children.first().categoryID, label, children.sortedBy { it.label },
        ) }
        .sortedBy { it.label }
    }

    /**
     * A control is what a listener can independently turn on or off. Repeated
     * profanity keeps every timestamp for playback, but is one word control.
     */
    fun controlCount(events: List<ScanEvent>): Int =
        available(events).sumOf { parent -> parent.children.sumOf { it.events.size } }
}
