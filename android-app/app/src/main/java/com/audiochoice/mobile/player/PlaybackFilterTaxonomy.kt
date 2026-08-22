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

    private val definitions = buildMap {
        fun add(c: Int, labels: List<String>, parent: String) = labels.forEachIndexed { index, label ->
            put(group(c, index + 1), Definition(parent, label))
        }
        add(1, listOf("Suggestive dialogue", "Sexual references", "Nudity", "Implied sexual activity", "Explicit sexual activity", "Complete sex scenes"), "Sexual Content")
        add(2, listOf("Mild profanity", "Strong profanity", "Sexual profanity", "Slurs / derogatory language"), "Profanity")
        put(group(3, 3), Definition("Violence", "Graphic violence / gore"))
        put(group(3, 4), Definition("Violence", "Torture"))
        put(group(3, 6), Definition("Violence", "Violence involving children"))
        put(group(3, 7), Definition("Violence", "Violence involving animals"))
        add(4, listOf("Alcohol use", "Intoxication", "Drug references", "Drug use", "Drug abuse / overdose"), "Drugs & Alcohol")
        add(5, listOf("Religious profanity", "Blasphemous statements"), "Blasphemy")
        add(6, listOf("Self-harm references", "Suicidal thoughts", "Suicide attempt", "Depiction of self-harm / suicide"), "Self-Harm & Suicide")
    }

    fun available(events: List<ScanEvent>): List<PlaybackFilterParent> = events
        .groupBy { it.groupID.lowercase() }
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

    /**
     * A control is what a listener can independently turn on or off. Repeated
     * profanity keeps every timestamp for playback, but is one word control.
     */
    fun controlCount(events: List<ScanEvent>): Int =
        available(events).sumOf { parent -> parent.children.sumOf { it.events.size } }
}
