package com.audiochoice.mobile.narration

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * The iOS player's controls must not sit behind the tab bar.
 *
 * A listener starting a book from their library found the speed, chapter, filter and bookmark row
 * hidden behind the tab bar, and only discovered it by switching to the Player tab. The player is
 * pushed from four places and is a tab's own content in one; the bar overlaps it in the four.
 *
 * It had been patched with a fixed 28-point bottom padding, which is the wrong shape of fix: the
 * clearance needed is the bar's height plus the home indicator, a number that differs by device. So the
 * bar is hidden where the player is pushed, and kept where it is the tab itself.
 *
 * Checked against source because this project has no iOS test target that can import the app, and no
 * simulator SDK is installed to render it.
 */
class PlayerTabBarTest {

    /** Hidden when pushed, visible when it is the tab's own content. */
    @Test
    fun `the tab bar is hidden for a pushed player and kept for the tab root`() {
        val player = source(PLAYER)
        assertTrue(
            "the player no longer controls tab bar visibility, so its bottom controls sit behind " +
                "the bar wherever it is pushed",
            player.contains("toolbar(isTabRoot ? .visible : .hidden, for: .tabBar)"),
        )
        assertTrue(
            "isTabRoot no longer defaults to false, so a newly added push would show the bar over " +
                "the controls and reintroduce the fault silently",
            player.contains("var isTabRoot: Bool = false"),
        )
    }

    /**
     * Exactly one call site is the tab root.
     *
     * More than one would mean somewhere is claiming to be a tab it is not, and the bar would overlap
     * there. None would mean the Player tab hides its own bar, stranding a listener with no way out.
     */
    @Test
    fun `only the player tab declares itself the tab root`() {
        val sites = listOf(PLAYER, LIBRARY_FEATURES)
            .flatMap { path -> Regex("""PlayerScreen\(book:[^)]*\)""").findAll(source(path)).map { it.value } }
        assertTrue("no PlayerScreen call sites were found at all", sites.size >= 4)
        assertEquals(
            "exactly one call site should be the tab root; found ${sites.count { it.contains("isTabRoot: true") }}",
            1,
            sites.count { it.contains("isTabRoot: true") },
        )
    }

    /**
     * The tab root is the Player tab, not something else.
     *
     * Asserted by name so the exemption cannot drift onto a pushed screen, which is where the fault was.
     */
    @Test
    fun `the tab root is the now playing screen`() {
        val player = source(PLAYER)
        val nowPlaying = player.indexOf("struct NowPlayingScreen: View {")
        assertTrue("NowPlayingScreen was not found", nowPlaying > 0)
        val declaration = player.indexOf("isTabRoot: true")
        assertTrue(
            "the tab-root player is declared outside NowPlayingScreen, so a pushed screen is " +
                "claiming to be a tab",
            declaration > nowPlaying,
        )
    }

    private fun source(relativePath: String): String {
        val file = listOf(
            File(relativePath),
            File("../$relativePath"),
            File("../../$relativePath"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate $relativePath; this guard would otherwise pass without checking",
            file != null,
        )
        return file!!.readText()
    }

    private companion object {
        const val PLAYER = "ios-app/AudioChoice/BookAndPlayerScreens.swift"
        const val LIBRARY_FEATURES = "ios-app/AudioChoice/LibraryFeatures.swift"
    }
}
