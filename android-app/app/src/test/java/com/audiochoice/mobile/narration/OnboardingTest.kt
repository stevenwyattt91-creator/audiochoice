package com.audiochoice.mobile.narration

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * The first-run tour covers the four things a new listener has to find.
 *
 * It previously showed two slides on Android and three different ones on iOS, and neither mentioned
 * the transfer tool or the reading edition — so the two features a listener is least likely to
 * discover on their own were the two the tour left out.
 */
class OnboardingTest {

    @Test
    fun `both platforms show the same four steps`() {
        assertEquals(
            "Android no longer shows four onboarding steps",
            4,
            Regex("""Triple\(""").findAll(guide()).count(),
        )
        assertEquals(
            "iOS no longer shows four onboarding steps",
            4,
            Regex("""OnboardingPage\(""").findAll(source(IOS_ONBOARDING)).count(),
        )
    }

    /**
     * Each step names where the thing lives.
     *
     * A tour that describes a feature without saying which control opens it is a tour someone has to
     * take twice.
     */
    @Test
    fun `each step names the control it describes`() {
        val android = guide()
        val ios = source(IOS_ONBOARDING)
        listOf(
            "Import" to "the import action",
            "transfer tool" to "the desktop hand-off",
            "shield" to "the filter control in the player",
            "Ebooks shelf" to "the second library shelf",
        ).forEach { (needle, what) ->
            assertTrue("Android onboarding never mentions $what", android.contains(needle))
            assertTrue("iOS onboarding never mentions $what", ios.contains(needle))
        }
    }

    /** The tour is shown once per account, and only after it is dismissed is that recorded. */
    @Test
    fun `the tour is remembered per account`() {
        val app = source(APP)
        assertTrue(
            "the completion flag is no longer keyed by account, so a second listener on the same " +
                "device would never see the tour",
            app.contains("\"completed_\${user.id}\""),
        )
        assertTrue(
            "the tour no longer records that it finished, so it would reappear every launch",
            app.contains("putBoolean(onboardingKey, true)"),
        )
    }

    /** Nothing in the tour promises a capability the beta build does not have. */
    @Test
    fun `the tour does not promise the premium voice`() {
        // The reading edition is experimental-only today, but the tour ships in every build, so it
        // describes reading and being read to without naming the paid voice as something available.
        listOf(guide(), source(IOS_ONBOARDING)).forEach { text ->
            assertTrue(
                "the tour names the premium voice, which a beta listener cannot reach",
                !text.contains("premium voice"),
            )
        }
    }

    private fun guide(): String {
        val app = source(APP)
        val start = app.indexOf("private fun FirstRunGuide(")
        assertTrue("FirstRunGuide was not found", start >= 0)
        val end = app.indexOf("\n}", app.indexOf("val slide = slides[page]", start))
        assertTrue("the end of FirstRunGuide was not found", end > start)
        return app.substring(start, end)
    }

    private fun source(relativePath: String): String {
        val file = listOf(
            File(relativePath),
            File("app/$relativePath"),
            File("../app/$relativePath"),
            File("../../$relativePath"),
            File("../$relativePath"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate $relativePath; this guard would otherwise pass without checking",
            file != null,
        )
        return file!!.readText()
    }

    private companion object {
        const val APP = "src/main/java/com/audiochoice/mobile/ui/AudioChoiceApp.kt"
        const val IOS_ONBOARDING = "ios-app/AudioChoice/OnboardingScreen.swift"
    }
}
