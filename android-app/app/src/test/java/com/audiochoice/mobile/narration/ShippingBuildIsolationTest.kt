package com.audiochoice.mobile.narration

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Narration must not reach the beta or release builds.
 *
 * The permissions it needs are declared in the experimental manifest alone, deliberately, so shipping
 * builds request exactly what they always have. Anything narration-related that needs one of those
 * permissions therefore has to live in the experimental source set too.
 *
 * This exists because `compileBetaKotlin` does not catch the mistake. Lint runs only when a
 * release-type target is *assembled*, so a file in `src/main` that needs an experimental-only manifest
 * entry compiles green under beta and then fails `lintVitalBeta` at assembly — which is how
 * `NarrationRenderWorker` sat in `src/main` breaking `assembleBeta` while every compile check passed.
 */
class ShippingBuildIsolationTest {

    /**
     * The foreground-service worker belongs to the experimental source set.
     *
     * It declares a typed `ForegroundInfo`, which requires `SystemForegroundService` to carry a
     * matching `foregroundServiceType`. Only the experimental manifest merges that.
     */
    @Test
    fun `the render worker is not in the shared source set`() {
        assertFalse(
            "NarrationRenderWorker is in src/main, so beta and release compile it and " +
                "lintVitalBeta fails at assembly because their manifests declare no dataSync " +
                "foreground service type",
            File(module(), "src/main/java/com/audiochoice/mobile/narration/NarrationRenderWorker.kt")
                .isFile,
        )
        assertTrue(
            "NarrationRenderWorker is not in the experimental source set either, so the " +
                "experimental build has lost it",
            File(
                module(),
                "src/experimental/java/com/audiochoice/mobile/narration/NarrationRenderWorker.kt",
            ).isFile,
        )
    }

    /**
     * The permissions narration needs stay out of the shared manifest.
     *
     * Adding them there would change the permission list of every shipping build for a feature those
     * builds do not contain, which is a thing users see and reasonably object to.
     */
    @Test
    fun `narration permissions are declared only for the experimental build`() {
        val main = File(module(), "src/main/AndroidManifest.xml").readText()
        val experimental = File(module(), "src/experimental/AndroidManifest.xml").readText()

        listOf("FOREGROUND_SERVICE_DATA_SYNC", "FOREGROUND_SERVICE_MEDIA_PROCESSING").forEach {
            assertFalse(
                "$it is declared in the shared manifest, so every shipping build now requests it",
                main.contains(it),
            )
            assertTrue(
                "$it is no longer declared for the experimental build, so its typed foreground " +
                    "worker would be refused at runtime",
                experimental.contains(it),
            )
        }
        assertFalse(
            "the WorkManager foreground service is now typed in the shared manifest",
            main.contains("androidx.work.impl.foreground.SystemForegroundService"),
        )
        assertTrue(
            "the experimental build no longer merges a foreground service type into WorkManager's " +
                "service, which is what makes a typed ForegroundInfo valid",
            experimental.contains("androidx.work.impl.foreground.SystemForegroundService") &&
                experimental.contains("android:foregroundServiceType=\"dataSync|mediaProcessing\""),
        )
    }

    /**
     * Narration stays behind its build flag in shared code.
     *
     * The rest of narration does live in `src/main`, because it needs no extra permission and the
     * experimental build type is created with `initWith(beta)`. What keeps it out of a shipping build
     * is the flag, so the flag has to be tied to the build type rather than left switchable.
     */
    @Test
    fun `narration is gated on the experimental build flag`() {
        val config = File(
            module(),
            "src/main/java/com/audiochoice/mobile/narration/NarrationConfig.kt",
        ).readText()
        assertTrue(
            "narration is no longer gated on the experimental build flag, so it would appear in " +
                "beta and release",
            config.contains("EXPERIMENTAL_BUILD"),
        )
        assertTrue(
            "the build script no longer defines EXPERIMENTAL_BUILD, so the gate reads a field " +
                "that does not exist",
            File(module(), "build.gradle.kts").readText()
                .contains("buildConfigField(\"boolean\", \"EXPERIMENTAL_BUILD\""),
        )
    }

    /** Resolves the `app` module directory from wherever the tests happen to be run. */
    private fun module(): File {
        val candidates = listOf(File("."), File("app"), File(".."), File("../app"))
        val found = candidates.firstOrNull { File(it, "src/main/AndroidManifest.xml").isFile }
        assertTrue(
            "could not locate the app module; this guard would otherwise pass without checking",
            found != null,
        )
        return found!!
    }
}
