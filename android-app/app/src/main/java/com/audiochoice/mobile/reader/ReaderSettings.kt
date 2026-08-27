package com.audiochoice.mobile.reader

import kotlinx.serialization.Serializable

@Serializable
enum class ReaderTheme { LIGHT, SEPIA, DARK }

/**
 * Reading preferences. These are device-wide rather than per-book: a listener's
 * preferred text size and theme should not reset every time they open a
 * different audiobook.
 *
 * Reading *position* is per-book and lives alongside the other per-fingerprint
 * keys in LocalAudioStore.
 */
@Serializable
data class ReaderSettings(
    val fontScale: Float = 1f,
    /** Sepia matches the paper palette the reader shipped with. */
    val theme: ReaderTheme = ReaderTheme.SEPIA,
    val marginScale: Float = 1f,
    /**
     * Highlight and scroll the text to match audio position, and allow tapping a
     * paragraph to seek. On by default: following along is the reason to pair a
     * reading edition with an audiobook at all.
     */
    val followAudio: Boolean = true,
) {
    companion object {
        val FONT_SCALES = listOf(0.85f, 1f, 1.15f, 1.35f)
        val MARGIN_SCALES = listOf(0.6f, 1f, 1.5f)

        fun fontScaleLabel(scale: Float): String = when {
            scale <= 0.85f -> "Small"
            scale <= 1f -> "Medium"
            scale <= 1.15f -> "Large"
            else -> "Extra large"
        }

        fun marginScaleLabel(scale: Float): String = when {
            scale <= 0.6f -> "Narrow"
            scale <= 1f -> "Normal"
            else -> "Wide"
        }
    }
}

/** Where the listener last stopped reading, as a LazyColumn anchor. */
@Serializable
data class ReaderPosition(
    val paragraphIndex: Int = 0,
    val scrollOffset: Int = 0,
)
