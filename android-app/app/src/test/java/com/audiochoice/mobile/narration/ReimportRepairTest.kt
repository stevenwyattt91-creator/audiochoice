package com.audiochoice.mobile.narration

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Re-importing has to be able to repair a book missing from the library.
 *
 * Text on the device with no library entry is a real state, not a contradiction: it is what a failed
 * or rolled-back registration leaves behind, and the owner of one reported exactly its symptom — an
 * ebook they could not reach, and re-importing did nothing.
 *
 * The cause was treating stored Book_Text as proof of library membership. The import would report the
 * book as already present, return before registering it, and leave the one state that nothing else
 * could fix either.
 *
 * Checked against source because the coordinator needs a `Context`, a `ContentResolver` and an API,
 * and this project has no Robolectric to supply them.
 */
class ReimportRepairTest {

    /** Membership is the library record. Leftover narration data is not membership. */
    @Test
    fun `stored book text is not treated as library membership`() {
        val body = coordinatorSource()
        val check = body.substring(
            body.indexOf("isAlreadyInLibrary = {"),
            body.indexOf("persistSourceLocation ="),
        )
        assertFalse(
            "stored Book_Text counts as being in the library again, so a book missing from the " +
                "library can never be re-registered and re-importing cannot repair it",
            check.contains("store.bookText("),
        )
        assertTrue(
            "the library record is no longer consulted at all",
            check.contains("localAudio.find(sha256) != null"),
        )
    }

    /**
     * The duplicate protection that condition was for is not lost.
     *
     * Registration is an upsert keyed by the same hash, so repeating it cannot create a second
     * entry. That is what makes dropping the extra condition safe rather than a trade.
     */
    @Test
    fun `registration remains an idempotent upsert keyed by hash`() {
        assertTrue(
            "registration is no longer an upsert, so re-importing could now create a duplicate " +
                "entry -- which is what the removed condition was guarding against",
            coordinatorSource().contains("api.saveBook(accessToken, request)"),
        )
        val api = read("src/main/java/com/audiochoice/mobile/data/AudioChoiceApi.kt")
        assertTrue(
            "saveBook no longer takes an upsert request",
            api.contains("suspend fun saveBook(accessToken: String, request: LibraryBookUpsertRequest)"),
        )
    }

    /**
     * A genuine repeat still records where the file lives.
     *
     * Re-importing a book already in the library is how someone re-grants access to a file whose
     * location the app has lost, so that path has to keep working.
     */
    @Test
    fun `a book already in the library still has its location recorded`() {
        val importer = read("src/main/java/com/audiochoice/mobile/narration/NarrationImporter.kt")
        val block = importer.substring(importer.indexOf("if (isAlreadyInLibrary("))
        assertTrue(
            "a repeat import no longer records the file's location, so re-granting access to a " +
                "moved file stops working",
            block.take(220).contains("persistSourceLocation(identity.sha256)"),
        )
    }

    private fun coordinatorSource() =
        read("src/main/java/com/audiochoice/mobile/narration/NarrationImportCoordinator.kt")

    private fun read(relativePath: String): String {
        val file = listOf(
            File(relativePath),
            File("app/$relativePath"),
            File("../app/$relativePath"),
        ).firstOrNull(File::isFile)
        assertTrue(
            "could not locate $relativePath; this guard would otherwise pass without checking",
            file != null,
        )
        return file!!.readText()
    }
}
