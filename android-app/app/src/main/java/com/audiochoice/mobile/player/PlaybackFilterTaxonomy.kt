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
    //
    // Narrowed a second time to exactly three real switches, matching the backend's own
    // second narrowing of EnforcedLabels: Nudity (anatomical word only, not general
    // nakedness), Explicit/vulgar scenes (group 9, a genuinely new concept -- graphic,
    // mechanical sexual description only; ordinary romantic/sexual tension, kissing, and
    // non-graphic intimate scenes are no longer flagged at all), and Sexual violence
    // (unchanged). Suggestive dialogue, Sexual references, Implied sexual activity, Explicit
    // sexual activity (the old, broader version), Complete sex scenes, and Kissing are
    // removed from here the same way the five non-sexual categories were removed above -- a
    // book scanned before this change may still carry old events under those groups, and
    // this makes sure none of them render a switch any more either.
    // Explicit group numbers rather than sequential indexing (the prior six-slot
    // forEachIndexed helper, now removed): the real group numbers below must match
    // ContentTaxonomy.Mappings' own GUIDs exactly -- sexual_nudity is group 3,
    // sexual_explicit_vulgar_scene is the newly-added group 9, sexual_violence is group 7.
    // Using 1/2/3 here would silently collide with the old sexual_suggestive_dialogue/
    // sexual_references/sexual_nudity GUIDs instead of the real, current ones.
    private val definitions = mapOf(
        group(1, 3) to Definition("Sexual Content", "Nudity"),
        group(1, 9) to Definition("Sexual Content", "Explicit / vulgar sex scenes"),
        group(1, 7) to Definition("Sexual Content", "Sexual violence (rape / sexual assault)"),
    )

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
    // Updated from group 6 (sexual_complete_scene) to group 9 (sexual_explicit_vulgar_scene)
    // when the taxonomy was narrowed to three labels -- the real risk this guards against
    // (a lesser label winning the aggregate tie-break and hiding the one that actually
    // matters) applies identically to the new label replacing the old one.
    private val neverSuppressedGroupIDs = setOf(
        group(1, 9), // sexual_explicit_vulgar_scene
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
