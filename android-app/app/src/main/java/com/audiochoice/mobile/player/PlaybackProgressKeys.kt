package com.audiochoice.mobile.player

/**
 * The device-local playback checkpoint written on every progress save.
 *
 * These keys are shared by PlayerViewModel (which writes them) and
 * ProgressSyncWorker (which drains the unsynced ones), so the two cannot drift
 * apart and leave a listener's position stranded on the device.
 */
internal object PlaybackProgressKeys {
    const val PREFERENCES_NAME = "audiochoice_playback"
    const val LAST_BOOK_ID = "last_open_book_id"

    private const val POSITION_PREFIX = "position_ms_"
    private const val DIRTY_PREFIX = "position_dirty_"

    fun positionKey(bookID: String): String = "$POSITION_PREFIX$bookID"

    fun dirtyKey(bookID: String): String = "$DIRTY_PREFIX$bookID"

    fun isDirtyKey(key: String): Boolean = key.startsWith(DIRTY_PREFIX)

    fun bookIDFromDirtyKey(key: String): String = key.removePrefix(DIRTY_PREFIX)
}
