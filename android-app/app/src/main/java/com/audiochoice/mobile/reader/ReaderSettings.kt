package com.audiochoice.mobile.reader

import kotlinx.serialization.Serializable

@Serializable
enum class ReaderTheme { LIGHT, SEPIA, DARK }

/**
 * Typeface for the reading edition.
 *
 * [OPEN_DYSLEXIC] is the same typeface Kindle offers for the same reason: weighted
 * letter bottoms and deliberately asymmetric shapes make it harder to rotate or
 * transpose similar characters. Bundled rather than downloaded so it works offline
 * and cannot disappear.
 */
@Serializable
enum class ReaderFont { SYSTEM, OPEN_DYSLEXIC }

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
    val font: ReaderFont = ReaderFont.SYSTEM,
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

        fun fontLabel(font: ReaderFont): String = when (font) {
            ReaderFont.SYSTEM -> "Standard"
            ReaderFont.OPEN_DYSLEXIC -> "OpenDyslexic"
        }

        /**
         * OpenDyslexic sets a larger x-height with heavy letter bottoms, so lines sit
         * visually closer than the same measurement in the default face. A little
         * extra leading keeps the weighted baselines from crowding.
         */
        fun lineHeightFactor(font: ReaderFont): Float = when (font) {
            ReaderFont.SYSTEM -> 1f
            ReaderFont.OPEN_DYSLEXIC -> 1.12f
        }
    }
}

/** Where the listener last stopped reading, as a LazyColumn anchor. */
@Serializable
data class ReaderPosition(
    val paragraphIndex: Int = 0,
    val scrollOffset: Int = 0,
)
