package com.audiochoice.mobile.player

internal data class FilterWindow(
    val startSeconds: Double,
    val endSeconds: Double,
)

/**
 * Finds the end of the complete connected block of enabled filters at (or just
 * ahead of) the current playback position. Keeping this calculation stateless
 * means rewinding into the same event always produces the same skip again.
 */
internal object FilterSkipPlanner {
    fun targetSeconds(
        positionSeconds: Double,
        windows: List<FilterWindow>,
        lookAheadSeconds: Double,
    ): Double? {
        val valid = windows
            .asSequence()
            .filter { it.startSeconds.isFinite() && it.endSeconds.isFinite() }
            .filter { it.endSeconds > it.startSeconds && it.endSeconds > positionSeconds }
            .sortedBy { it.startSeconds }
            .toList()

        var target = valid
            .filter { it.startSeconds <= positionSeconds + lookAheadSeconds }
            .maxOfOrNull { it.endSeconds }
            ?: return null

        // If enabled events overlap, land beyond the entire connected range,
        // rather than skipping a short event and landing inside a larger scene.
        var expanded: Boolean
        do {
            expanded = false
            valid.forEach { window ->
                if (window.startSeconds <= target && window.endSeconds > target) {
                    target = window.endSeconds
                    expanded = true
                }
            }
        } while (expanded)

        return target
    }
}
