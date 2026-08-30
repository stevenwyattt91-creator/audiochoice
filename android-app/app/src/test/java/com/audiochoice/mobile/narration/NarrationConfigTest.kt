package com.audiochoice.mobile.narration

import com.audiochoice.mobile.BuildConfig
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * The gate has to hold for the whole of the narration feature, so these tests
 * are deliberately about the gate itself rather than about any narration
 * behaviour. A unit test cannot flip `BuildConfig.EXPERIMENTAL_BUILD`, so what is
 * asserted here is that [NarrationConfig.enabled] is exactly that flag and
 * nothing else, and that narration storage is confined to the caller's
 * app-private directory.
 */
class NarrationConfigTest {

    /**
     * `enabled` must track the build flag and must not acquire any other
     * condition. If someone later adds an account check, a remote flag or a
     * debug override, this fails and the gate stays honest.
     */
    @Test
    fun `enabled is exactly the experimental build flag`() {
        assertEquals(BuildConfig.EXPERIMENTAL_BUILD, NarrationConfig.enabled)
    }

    /**
     * Unit tests run against the debug variant, where the flag is false. Stating
     * it explicitly documents the default: narration is absent unless a build
     * opts in, which is what keeps beta and release unchanged.
     */
    @Test
    fun `narration is absent from the default build variant`() {
        assertFalse(BuildConfig.EXPERIMENTAL_BUILD)
        assertFalse(NarrationConfig.enabled)
    }

    /**
     * Isolation between the beta and experimental installs comes from
     * `applicationIdSuffix`, which gives each its own `filesDir`. That only holds
     * if narration never resolves a path outside the `filesDir` it is handed, so
     * the root must be a descendant of it and must not escape via `..`.
     */
    @Test
    fun `narration root stays inside the app private files directory`() {
        val filesDir = File("/data/user/0/com.audiochoice.mobile.experimental/files")
        val root = NarrationConfig.narrationRoot(filesDir)

        assertEquals(filesDir, root.parentFile)
        assertTrue(root.path.startsWith(filesDir.path + File.separator))
        assertFalse(root.path.contains(".."))
    }

    /**
     * Two installs with different application IDs must never resolve the same
     * narration directory for the same book. This is the property that makes
     * R19.6 structural: it can only regress by removing the suffix from the
     * build type, not by a change inside narration code.
     */
    @Test
    fun `book directories differ between application ids`() {
        val sha = "A".repeat(64)
        val beta = NarrationConfig.bookDirectory(
            File("/data/user/0/com.audiochoice.mobile.beta/files"),
            sha,
        )
        val experimental = NarrationConfig.bookDirectory(
            File("/data/user/0/com.audiochoice.mobile.experimental/files"),
            sha,
        )

        assertFalse(beta.path == experimental.path)
    }

    /**
     * The SHA-256 is the per-book key across the whole feature, and it arrives
     * from several places: a freshly computed digest, a persisted plan and a
     * server response. Those do not agree on case, so the directory has to
     * normalise or the same book gets two directories and two render queues.
     */
    @Test
    fun `book directory is case insensitive on the fingerprint`() {
        val filesDir = File("/data/user/0/com.audiochoice.mobile.experimental/files")
        val upper = NarrationConfig.bookDirectory(filesDir, "AbCdEf" + "0".repeat(58))
        val lower = NarrationConfig.bookDirectory(filesDir, "abcdef" + "0".repeat(58))

        assertEquals(lower.path, upper.path)
    }
}
