package com.audiochoice.mobile.player

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Resume precedence. A regression here silently restarts a listener's audiobook,
 * so every branch is pinned.
 */
class PlaybackResumeTest {

    @Test
    fun `this session's position always wins`() {
        assertEquals(
            5_000L,
            PlaybackResume.resumePositionMs(
                sessionPositionMs = 5_000L,
                localPositionMs = 1_000L,
                localIsDirty = true,
                serverPositionMs = 9_000L,
            ),
        )
    }

    @Test
    fun `an unsynced local checkpoint beats the server`() {
        assertEquals(
            1_000L,
            PlaybackResume.resumePositionMs(
                sessionPositionMs = null,
                localPositionMs = 1_000L,
                localIsDirty = true,
                serverPositionMs = 9_000L,
            ),
        )
    }

    /**
     * The server value can arrive from a cached library snapshot, so a clean local
     * checkpoint is not automatically older.
     */
    @Test
    fun `a clean local checkpoint takes whichever is later`() {
        assertEquals(
            9_000L,
            PlaybackResume.resumePositionMs(
                sessionPositionMs = null,
                localPositionMs = 1_000L,
                localIsDirty = false,
                serverPositionMs = 9_000L,
            ),
        )
        assertEquals(
            9_000L,
            PlaybackResume.resumePositionMs(
                sessionPositionMs = null,
                localPositionMs = 9_000L,
                localIsDirty = false,
                serverPositionMs = 1_000L,
            ),
        )
    }

    @Test
    fun `falls back to the server with no local checkpoint`() {
        assertEquals(
            7_000L,
            PlaybackResume.resumePositionMs(
                sessionPositionMs = null,
                localPositionMs = null,
                localIsDirty = false,
                serverPositionMs = 7_000L,
            ),
        )
    }

    /** SharedPreferences uses -1 as "absent", which must not be read as a position. */
    @Test
    fun `a negative local position is treated as absent`() {
        assertEquals(
            7_000L,
            PlaybackResume.resumePositionMs(
                sessionPositionMs = null,
                localPositionMs = -1L,
                localIsDirty = true,
                serverPositionMs = 7_000L,
            ),
        )
    }

    @Test
    fun `never returns a negative position`() {
        assertEquals(
            0L,
            PlaybackResume.resumePositionMs(
                sessionPositionMs = null,
                localPositionMs = null,
                localIsDirty = false,
                serverPositionMs = -500L,
            ),
        )
        assertEquals(
            0L,
            PlaybackResume.resumePositionMs(
                sessionPositionMs = -20L,
                localPositionMs = null,
                localIsDirty = false,
                serverPositionMs = 0L,
            ),
        )
    }

    @Test
    fun `a fresh book with nothing recorded starts at zero`() {
        assertEquals(
            0L,
            PlaybackResume.resumePositionMs(
                sessionPositionMs = null,
                localPositionMs = -1L,
                localIsDirty = false,
                serverPositionMs = 0L,
            ),
        )
    }

    /**
     * The exact reported bug: pause partway in, close the app, reopen and the book
     * restarted. Hydration ran against a cached library snapshot carrying position
     * 0 and overwrote a good clean checkpoint with it.
     */
    @Test
    fun `hydration never rewinds a clean checkpoint to a stale server zero`() {
        val hydrated = PlaybackResume.hydratedPositionMs(
            localMs = 184_000L,
            dirty = false,
            serverMs = 0L,
        )
        assertEquals(184_000L, hydrated.positionMs)
        assertTrue("the server is behind and must be corrected", hydrated.needsPush)
    }

    @Test
    fun `hydration adopts a server position that is further along`() {
        val hydrated = PlaybackResume.hydratedPositionMs(
            localMs = 5_000L,
            dirty = false,
            serverMs = 90_000L,
        )
        assertEquals(90_000L, hydrated.positionMs)
        assertFalse("the server already holds the winning value", hydrated.needsPush)
    }

    @Test
    fun `hydration pushes an unsynced checkpoint`() {
        val hydrated = PlaybackResume.hydratedPositionMs(
            localMs = 12_000L,
            dirty = true,
            serverMs = 12_000L,
        )
        assertEquals(12_000L, hydrated.positionMs)
        assertTrue("dirty means the server never acknowledged it", hydrated.needsPush)
    }

    @Test
    fun `hydration with no local checkpoint trusts the server and stays quiet`() {
        val hydrated = PlaybackResume.hydratedPositionMs(
            localMs = -1L,
            dirty = false,
            serverMs = 42_000L,
        )
        assertEquals(42_000L, hydrated.positionMs)
        assertFalse(hydrated.needsPush)
    }

    /**
     * A released MediaController reports position 0 while still being non-null,
     * so the checkpoint written on the way out of the app recorded 0. Resume must
     * not treat that as the listener's place even though it is marked unsynced.
     * The transport guard in PlayerViewModel stops it being written at all; this
     * pins the precedence that made it so destructive.
     */
    @Test
    fun `a dirty zero checkpoint does not beat a real server position`() {
        assertEquals(
            184_000L,
            PlaybackResume.hydratedPositionMs(
                localMs = 0L,
                dirty = true,
                serverMs = 184_000L,
            ).positionMs,
        )
    }

    /** A brand new book must not generate a pointless zero-position write. */
    @Test
    fun `hydration of an untouched book is a no-op`() {
        val hydrated = PlaybackResume.hydratedPositionMs(
            localMs = -1L,
            dirty = false,
            serverMs = 0L,
        )
        assertEquals(0L, hydrated.positionMs)
        assertFalse(hydrated.needsPush)
    }
}
