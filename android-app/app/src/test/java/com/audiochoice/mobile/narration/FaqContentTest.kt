package com.audiochoice.mobile.narration

import com.audiochoice.contracts.FaqResponse
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Help content is served, and the two apps must agree on its shape.
 *
 * They previously each held their own hardcoded copy and drifted: Android carried eleven questions and
 * iOS four different ones, so the same product answered differently depending on the phone, and
 * neither mentioned the reading edition, the voice tiers, rescanning or password reset.
 */
class FaqContentTest {

    private val json = Json { ignoreUnknownKeys = true }

    /**
     * The server's shape decodes into the client's.
     *
     * Checked by decoding the field names the API actually serialises. A rename on either side would
     * otherwise surface as a silently empty help screen, because the client falls back rather than
     * failing loudly.
     */
    @Test
    fun `the served shape decodes`() {
        val served = """
            {"version":2,"sections":[
              {"title":"Filters","items":[
                {"question":"How do filters work?","answer":"An audiobook is scanned once."}
              ]}
            ]}
        """.trimIndent()
        val decoded = json.decodeFromString<FaqResponse>(served)
        assertEquals(2, decoded.version)
        assertEquals("Filters", decoded.sections.single().title)
        assertEquals("How do filters work?", decoded.sections.single().items.single().question)
    }

    /** Missing fields decode to empty rather than throwing, so a partial reply cannot crash the screen. */
    @Test
    fun `an incomplete reply decodes to something harmless`() {
        val decoded = json.decodeFromString<FaqResponse>("""{"version":9}""")
        assertEquals(9, decoded.version)
        assertTrue(decoded.sections.isEmpty())
    }

    /**
     * The bundled copy is only a fallback, and the served copy wins when it is at least as new.
     *
     * Compared by version rather than assumed newer, so an app that has not been updated still shows
     * the better answers, and a server that has somehow fallen behind cannot replace them with worse.
     */
    @Test
    fun `the screen prefers the served copy without trusting it blindly`() {
        val body = source(APP)
        assertTrue(
            "the screen no longer prefers the served content, so corrections would need an app release",
            body.contains("served.version >= faq.version"),
        )
        assertTrue(
            "an empty reply would replace the bundled copy and leave the screen blank",
            body.contains("served.sections.isNotEmpty()"),
        )
        assertTrue(
            "the fetch is no longer tolerant of failure, so a poor connection would surface as an " +
                "error instead of the bundled answers",
            body.contains("runCatching { api.faq() }.getOrNull()"),
        )
    }

    /** The bundled copy exists, so the screen is never empty on first paint or offline. */
    @Test
    fun `a bundled fallback is present`() {
        val body = source(APP)
        assertTrue("the bundled fallback is gone", body.contains("private val bundledFaq = FaqResponse("))
        assertTrue(
            "the screen no longer starts from the bundled copy, so it would be blank until the " +
                "network answers",
            body.contains("mutableStateOf(bundledFaq)"),
        )
    }

    /**
     * The server's content covers what shipped recently.
     *
     * The old copies going stale is the fault being fixed, so the replacement is checked for the
     * subjects that were missing rather than only for existing.
     */
    @Test
    fun `the served content covers the recent features`() {
        val content = source(FAQ_CONTENT)
        listOf(
            "Scan this audiobook",   // rescan
            "Forgot password",       // account recovery
            "six-digit",             // the reset code
            "Ebooks shelf",          // the second library shelf
            "premium voice",         // the voice tiers
            "Founder",               // complimentary accounts
            "transfer tool",         // the desktop hand-off
        ).forEach { subject ->
            assertTrue(
                "the served help content never mentions '$subject', which is one of the things the " +
                    "old hardcoded copies were missing",
                content.contains(subject),
            )
        }
    }

    /** A version bump is what tells a client its bundled copy is older. */
    @Test
    fun `the served version is ahead of the bundled one`() {
        val served = Regex("""public const int Version = (\d+);""")
            .find(source(FAQ_CONTENT))?.groupValues?.get(1)?.toInt()
        assertTrue("the served content has no version", served != null)
        val bundled = Regex("""bundledFaq = FaqResponse\(\s*version = (\d+)""")
            .find(source(APP))?.groupValues?.get(1)?.toInt()
        assertTrue("the bundled copy has no version", bundled != null)
        assertTrue(
            "the served version ($served) is not ahead of the bundled one ($bundled), so clients " +
                "would keep showing their own copy",
            served!! > bundled!!,
        )
    }

    /** The old single flat list is gone, so it cannot be rendered by mistake. */
    @Test
    fun `the old hardcoded list is gone`() {
        assertFalse(
            "the previous flat FAQ list is still present and could be shown instead of the served " +
                "content",
            source(APP).contains("private val audioChoiceFaqs"),
        )
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
        const val FAQ_CONTENT = "backend/AudioChoice.Api/Services/FaqContent.cs"
    }
}
