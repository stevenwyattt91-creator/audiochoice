package com.audiochoice.mobile.narration

import com.audiochoice.mobile.BuildConfig
import java.io.File

/**
 * Single source of truth for whether EPUB narration exists in this build.
 *
 * Narration is experimental-only for this release cycle. Every narration entry
 * point -- the library import action, the narration surfaces, the render worker
 * and the backend calls -- is gated on [enabled], and the gate is applied at the
 * composition and registration sites rather than inside each component, so a
 * narration screen cannot be reached by any navigation route in a beta or
 * release build.
 *
 * This is the first consumer of `EXPERIMENTAL_BUILD`. The field has been
 * declared for all three build types in `app/build.gradle.kts` since the
 * experimental type was added, but no Kotlin source read it until now, so there
 * is no prior gating pattern to follow beyond [com.audiochoice.mobile.beta.BetaConfig].
 *
 * Data isolation needs no code here. The `experimental` build type carries
 * `applicationIdSuffix = ".experimental"`, so it has its own `filesDir` and its
 * own DataStore document. A beta or release install on the same device cannot
 * read or alter narration data. [narrationRoot] resolves under that
 * per-application-id directory, which is what makes the isolation structural
 * rather than something a later change could quietly undo.
 */
object NarrationConfig {
    /** The only place `BuildConfig.EXPERIMENTAL_BUILD` is read. */
    val enabled: Boolean get() = BuildConfig.EXPERIMENTAL_BUILD

    /** Directory name under `filesDir` that holds every narrated book's data. */
    const val ROOT_DIRECTORY = "narration"

    /**
     * Root of all narration storage, under the caller's app-private `filesDir`.
     *
     * Deliberately not one of `LocalAudioStore.PURGEABLE_AUDIO_DIRECTORIES`:
     * `purgeOrphanedAudioFiles` walks only those and only recognises files
     * referenced by an `audio_` preference key, so narration audio is outside
     * its reach entirely rather than protected by reference-counting logic added
     * to a path with a history of subtle bugs.
     */
    fun narrationRoot(filesDir: File): File = File(filesDir, ROOT_DIRECTORY)

    /** Per-book narration directory, keyed by the Source_EPUB SHA-256. */
    fun bookDirectory(filesDir: File, sha256: String): File =
        File(narrationRoot(filesDir), sha256.lowercase())
}
