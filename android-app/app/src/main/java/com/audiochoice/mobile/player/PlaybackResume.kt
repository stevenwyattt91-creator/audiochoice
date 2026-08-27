package com.audiochoice.mobile.player

/**
 * Decides which of the three competing records of "where was I" wins when a book
 * is reopened.
 *
 * Extracted from PlayerViewModel so the precedence is testable without a
 * MediaController: getting this wrong silently restarts a listener's book, which
 * is the most damaging small bug this app can have.
 */
object PlaybackResume {

    /**
     * Precedence, highest first:
     *
     * 1. **This session's position.** Already observed in memory, so it is newer
     *    than anything on disk or on the server by definition.
     * 2. **An unsynced local checkpoint.** The dirty flag means the server has
     *    not accepted this value yet, so the server's copy is stale.
     * 3. **The later of the clean local checkpoint and the server value.** A
     *    clean local checkpoint is written on every successful save, so it is at
     *    least as recent as the server value carried on the LibraryBook -- which
     *    may itself have come from a cached library snapshot.
     */
    fun resumePositionMs(
        sessionPositionMs: Long?,
        localPositionMs: Long?,
        localIsDirty: Boolean,
        serverPositionMs: Long,
    ): Long {
        val server = serverPositionMs.coerceAtLeast(0L)
        val local = localPositionMs?.takeIf { it >= 0L }
        return when {
            sessionPositionMs != null -> sessionPositionMs
            localIsDirty && local != null -> local
            local != null -> maxOf(local, server)
            else -> server
        }.coerceAtLeast(0L)
    }

    /** What hydration should settle on, and whether the server needs telling. */
    data class Hydrated(val positionMs: Long, val needsPush: Boolean)

    /**
     * Reconciles the local checkpoint with the position carried on a LibraryBook
     * when the library loads.
     *
     * The rule is that hydration may only ever move a position *forward*. The
     * previous version overwrote the local checkpoint with the server value
     * whenever the local copy was clean, which reset listeners to the start of
     * the book: LibraryViewModel publishes a **cached** snapshot before the
     * network call returns, and a cached book's position is frequently 0. That
     * stale 0 then clobbered both the in-memory session position and the on-disk
     * checkpoint, so every restart lost the user's place.
     *
     * @param localMs the on-disk checkpoint, negative when absent.
     * @param dirty whether the server has yet to accept the local checkpoint.
     * @param serverMs the position carried on the LibraryBook.
     */
    fun hydratedPositionMs(localMs: Long, dirty: Boolean, serverMs: Long): Hydrated {
        val local = localMs.coerceAtLeast(0L)
        val server = serverMs.coerceAtLeast(0L)
        val best = maxOf(local, server)
        // Push when the server is behind, or when it never acknowledged this
        // checkpoint in the first place.
        val needsPush = (dirty && localMs >= 0L) || best > server
        return Hydrated(best, needsPush)
    }
}
