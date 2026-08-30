package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationPlan
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.RenderQueue
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.security.MessageDigest

/**
 * On-disk narration state for one device, keyed by the Source_EPUB SHA-256 so a
 * narrated book uses the same per-book key space as an imported audiobook.
 *
 * Everything here is a file rather than a DataStore preference, and that is a
 * deliberate repeat of the reasoning `LocalAudioStore.saveEpubText` already
 * records: Preferences DataStore holds its whole document in memory and rewrites
 * it on every edit. A twenty-thousand-unit plan is a few megabytes of JSON, and
 * the render loop rewrites chapter state after every chapter, so keeping the plan
 * in preferences would make every unrelated preference write copy megabytes.
 *
 * The directory sits under the caller's app-private `filesDir` and deliberately
 * outside `LocalAudioStore.PURGEABLE_AUDIO_DIRECTORIES`. `purgeOrphanedAudioFiles`
 * walks only those three directories and only recognises files referenced by an
 * `audio_` preference key, so narration audio is out of its reach entirely
 * rather than protected by reference-counting bolted onto a path that has already
 * had subtle bugs.
 */
class NarrationStore(private val filesDir: File) {

    private val json = Json { ignoreUnknownKeys = true }

    // region layout

    fun bookDirectory(sha256: String): File = NarrationConfig.bookDirectory(filesDir, sha256)

    private fun planFile(sha256: String) = File(bookDirectory(sha256), PLAN_FILE)
    private fun queueFile(sha256: String) = File(bookDirectory(sha256), QUEUE_FILE)
    private fun bookTextFile(sha256: String) = File(bookDirectory(sha256), BOOK_TEXT_FILE)
    private fun textScanFile(sha256: String) = File(bookDirectory(sha256), TEXT_SCAN_FILE)
    private fun libraryBookFile(sha256: String) = File(bookDirectory(sha256), LIBRARY_BOOK_FILE)
    private fun timelineDirectory(sha256: String) = File(bookDirectory(sha256), TIMELINE_DIRECTORY)
    private fun audioDirectory(sha256: String) = File(bookDirectory(sha256), AUDIO_DIRECTORY)

    fun timelineFile(sha256: String, chapterIndex: Int) =
        File(timelineDirectory(sha256), "$chapterIndex.json")

    /** Final resting place of one chapter's audio. */
    fun chapterAudioFile(sha256: String, chapterIndex: Int) =
        File(audioDirectory(sha256), "chapter_$chapterIndex.$AUDIO_EXTENSION")

    /**
     * Where a render writes before it is complete. Renamed on success, so a
     * reader never observes a truncated file. Any `.partial` left behind is
     * evidence of a killed render and is swept on the next worker start.
     */
    fun partialChapterAudioFile(sha256: String, chapterIndex: Int) =
        File(audioDirectory(sha256), "chapter_$chapterIndex.$AUDIO_EXTENSION$PARTIAL_SUFFIX")

    // endregion

    // region Book_Text

    suspend fun saveBookText(sha256: String, bookText: String): Unit = withContext(Dispatchers.IO) {
        writeAtomically(bookTextFile(sha256), bookText)
    }

    suspend fun bookText(sha256: String): String? = withContext(Dispatchers.IO) {
        bookTextFile(sha256).takeIf(File::isFile)
            ?.let { runCatching { it.readText() }.getOrNull() }
            ?.takeIf(String::isNotEmpty)
    }

    // endregion

    // region text scan

    suspend fun saveTextScan(sha256: String, scan: StoredTextScan): Unit =
        withContext(Dispatchers.IO) {
            writeAtomically(textScanFile(sha256), json.encodeToString(scan))
        }

    suspend fun textScan(sha256: String): StoredTextScan? = withContext(Dispatchers.IO) {
        textScanFile(sha256).takeIf(File::isFile)?.let { file ->
            runCatching { json.decodeFromString<StoredTextScan>(file.readText()) }.getOrNull()
        }
    }

    /**
     * The library row for a narrated book, kept on the device.
     *
     * A narrated book is entirely local: the file, its extracted text, its rendered audio. Its place
     * in the library was the one part that lived only on the server, so a lost or unreachable server
     * record made a book that is sitting on the device unreachable through it -- and because the
     * ebook shelf only appears when an ebook is on it, the shelf vanished too.
     *
     * Written here rather than in the shared preferences store so it stays inside the narration root,
     * which the experimental application id already isolates, and so deleting a narrated book removes
     * its library row with the rest of its directory.
     */
    suspend fun saveLibraryBook(sha256: String, book: LibraryBook): Unit = withContext(Dispatchers.IO) {
        writeAtomically(libraryBookFile(sha256), json.encodeToString(book))
    }

    suspend fun libraryBook(sha256: String): LibraryBook? = withContext(Dispatchers.IO) {
        libraryBookFile(sha256).takeIf(File::isFile)?.let { file ->
            runCatching { json.decodeFromString<LibraryBook>(file.readText()) }.getOrNull()
        }
    }

    /**
     * Every narrated book this device holds a library row for.
     *
     * Read by walking the narration root, so a book is listed because its data is present rather than
     * because an index says so. An index would be a second thing to keep in step, and the failure it
     * would introduce is the one this whole record exists to remove.
     */
    suspend fun narratedBooks(): List<LibraryBook> = withContext(Dispatchers.IO) {
        NarrationConfig.narrationRoot(filesDir).listFiles().orEmpty()
            .filter(File::isDirectory)
            .mapNotNull { directory ->
                File(directory, LIBRARY_BOOK_FILE).takeIf(File::isFile)?.let { file ->
                    runCatching { json.decodeFromString<LibraryBook>(file.readText()) }.getOrNull()
                }
            }
    }

    suspend fun deleteTextScan(sha256: String): Boolean = withContext(Dispatchers.IO) {
        textScanFile(sha256).delete()
    }

    // endregion

    // region plan

    suspend fun savePlan(sha256: String, plan: NarrationPlan): Unit = withContext(Dispatchers.IO) {
        writeAtomically(planFile(sha256), json.encodeToString(plan))
    }

    /**
     * Load the plan, deciding what is still trustworthy.
     *
     * The three stale outcomes are genuinely different and the caller must treat
     * them differently, which is why this returns an outcome rather than a
     * nullable plan:
     *
     * - A plan-version change means the segmentation rules moved but every offset
     *   still refers to the same Book_Text, so the scan results survive.
     * - A Book_Text-hash change means every recorded offset belongs to a
     *   coordinate space that no longer exists, so the scan results must go too.
     * - An unreadable file says nothing about either, so only the plan is lost.
     */
    suspend fun loadPlan(sha256: String, currentBookTextHash: String?): PlanLoad =
        withContext(Dispatchers.IO) {
            val file = planFile(sha256)
            if (!file.isFile) return@withContext PlanLoad.Absent

            val plan = runCatching { json.decodeFromString<NarrationPlan>(file.readText()) }
                .getOrNull()
                ?: return@withContext PlanLoad.Stale(StaleReason.UNREADABLE)

            if (plan.planVersion != NarrationPlan.PLAN_VERSION) {
                return@withContext PlanLoad.Stale(StaleReason.PLAN_VERSION)
            }
            if (currentBookTextHash != null && plan.inputs.bookTextHash != currentBookTextHash) {
                return@withContext PlanLoad.Stale(StaleReason.BOOK_TEXT_HASH)
            }
            PlanLoad.Loaded(plan)
        }

    /**
     * Apply the consequence of a stale load.
     *
     * Kept beside [loadPlan] so the discard rules live next to the detection
     * rules rather than being restated at each call site. The library entry and
     * the persisted content URI are never touched here: a stale plan is
     * rebuildable from the file the listener still owns.
     */
    suspend fun discardStalePlan(sha256: String, reason: StaleReason): Unit =
        withContext(Dispatchers.IO) {
            planFile(sha256).delete()
            queueFile(sha256).delete()
            timelineDirectory(sha256).deleteRecursively()
            audioDirectory(sha256).deleteRecursively()
            if (reason == StaleReason.BOOK_TEXT_HASH) {
                // Offsets are expressed against the old Book_Text, so the events
                // point at the wrong words. Keeping them would filter the wrong
                // passages, which is worse than having no filter results.
                textScanFile(sha256).delete()
                bookTextFile(sha256).delete()
            }
        }

    // endregion

    // region render queue

    suspend fun saveQueue(sha256: String, queue: RenderQueue): Unit = withContext(Dispatchers.IO) {
        writeAtomically(queueFile(sha256), json.encodeToString(queue))
    }

    suspend fun loadQueue(sha256: String): RenderQueue? = withContext(Dispatchers.IO) {
        queueFile(sha256).takeIf(File::isFile)
            ?.let { file -> runCatching { json.decodeFromString<RenderQueue>(file.readText()) }.getOrNull() }
    }

    // endregion

    // region chapter timelines

    /**
     * Timings are stored **chapter-relative**, measured from the first sample of
     * that chapter's own audio.
     *
     * This is the most useful property in the whole layout. When one chapter is
     * re-rendered at a different length -- a voice change, a filter change, a
     * retry -- no other chapter's timeline file needs rewriting. Book_Time is
     * applied at load by adding the chapter's cumulative start, in one place.
     */
    suspend fun saveChapterTimeline(
        sha256: String,
        chapterIndex: Int,
        timings: List<ReaderTimingRange>,
    ): Unit = withContext(Dispatchers.IO) {
        writeAtomically(timelineFile(sha256, chapterIndex), json.encodeToString(timings))
    }

    suspend fun loadChapterTimeline(sha256: String, chapterIndex: Int): List<ReaderTimingRange>? =
        withContext(Dispatchers.IO) {
            timelineFile(sha256, chapterIndex).takeIf(File::isFile)?.let { file ->
                runCatching { json.decodeFromString<List<ReaderTimingRange>>(file.readText()) }
                    .getOrNull()
            }
        }

    // endregion

    // region maintenance

    /**
     * Delete any `.partial` audio and report which chapters owned it.
     *
     * A partial file means a render was interrupted by process death, worker
     * cancellation or a pause. Resuming mid-chapter is not supported -- the
     * encoder state is gone -- so the chapter returns to not-rendered and starts
     * from its first unit. Called on worker start, which is what makes this hold
     * across a killed process rather than only across a clean cancellation.
     */
    suspend fun sweepPartialAudio(sha256: String): List<Int> = withContext(Dispatchers.IO) {
        val directory = audioDirectory(sha256)
        if (!directory.isDirectory) return@withContext emptyList()
        directory.listFiles().orEmpty()
            .filter { it.isFile && it.name.endsWith(PARTIAL_SUFFIX) }
            .mapNotNull { file ->
                val index = file.name
                    .removeSuffix(PARTIAL_SUFFIX)
                    .removePrefix("chapter_")
                    .removeSuffix(".$AUDIO_EXTENSION")
                    .toIntOrNull()
                file.delete()
                index
            }
            .sorted()
    }

    /** Total bytes of rendered chapter audio for one book. */
    suspend fun audioBytes(sha256: String): Long = withContext(Dispatchers.IO) {
        audioDirectory(sha256).listFiles().orEmpty()
            .filter { it.isFile && !it.name.endsWith(PARTIAL_SUFFIX) }
            .sumOf { it.length() }
    }

    /**
     * Drop every rendered chapter's audio, keeping the plan, the timelines, the
     * scan results and the position. Timelines survive because they are stored
     * apart from the audio, which is what makes reclaiming space cheap to undo.
     */
    suspend fun deleteAllChapterAudio(sha256: String): Unit = withContext(Dispatchers.IO) {
        audioDirectory(sha256).deleteRecursively()
    }

    /**
     * Drops a chapter's word timings.
     *
     * Deleted alongside its audio, never on its own: timings describe where words fall in a specific
     * recording, so keeping them after the audio has gone would let the reader highlight against
     * timings that belong to sound nobody can play.
     */
    suspend fun deleteChapterTimeline(sha256: String, chapterIndex: Int): Boolean =
        withContext(Dispatchers.IO) {
            timelineFile(sha256, chapterIndex).let { it.isFile && it.delete() }
        }

    suspend fun deleteChapterAudio(sha256: String, chapterIndex: Int): Boolean =
        withContext(Dispatchers.IO) {
            chapterAudioFile(sha256, chapterIndex).delete()
        }

    /** Remove everything for one book. Used when the listener deletes it. */
    suspend fun deleteBook(sha256: String): Boolean = withContext(Dispatchers.IO) {
        val directory = bookDirectory(sha256)
        if (!directory.exists()) return@withContext true
        directory.deleteRecursively()
    }

    // endregion

    /**
     * Write to a sibling and rename.
     *
     * A plan is megabytes and the render loop rewrites queue state after every
     * chapter, so a process death mid-write is not hypothetical. A truncated
     * `plan.json` would fail to parse and discard the listener's rendered audio
     * on the next open, so the write is never allowed to be observed
     * half-finished.
     */
    private fun writeAtomically(target: File, contents: String) {
        target.parentFile?.mkdirs()
        val temporary = File(target.parentFile, target.name + TEMPORARY_SUFFIX)
        runCatching {
            temporary.writeText(contents)
            if (!temporary.renameTo(target)) {
                // Rename can fail if the target exists on some filesystems.
                target.delete()
                if (!temporary.renameTo(target)) {
                    temporary.copyTo(target, overwrite = true)
                    temporary.delete()
                }
            }
        }.onFailure { temporary.delete() }
    }

    companion object {
        const val PLAN_FILE = "plan.json"
        const val QUEUE_FILE = "render-queue.json"
        const val BOOK_TEXT_FILE = "book-text.txt"
        const val TEXT_SCAN_FILE = "text-scan.json"
        const val LIBRARY_BOOK_FILE = "library-book.json"
        const val TIMELINE_DIRECTORY = "timeline"
        const val AUDIO_DIRECTORY = "audio"
        const val AUDIO_EXTENSION = "m4a"
        const val PARTIAL_SUFFIX = ".partial"
        private const val TEMPORARY_SUFFIX = ".tmp"

        /**
         * Identifies the Book_Text a plan was built against. Any change to
         * extraction changes this, so stale offsets are detected rather than
         * reinterpreted against text that has moved underneath them.
         */
        fun bookTextHash(bookText: String, extractionVersion: Int): String {
            val digest = MessageDigest.getInstance("SHA-256")
            digest.update(extractionVersion.toString().toByteArray())
            digest.update(0)
            digest.update(bookText.toByteArray())
            return digest.digest().joinToString("") { "%02x".format(it) }
        }
    }
}

/** Why a persisted plan cannot be used. */
enum class StaleReason {
    /** Segmentation rules changed. Offsets remain valid, so scan results survive. */
    PLAN_VERSION,

    /** Book_Text changed. Every offset is in a coordinate space that is gone. */
    BOOK_TEXT_HASH,

    /** The file could not be parsed. Says nothing about the scan results. */
    UNREADABLE,
}

/** Outcome of loading a persisted plan. */
sealed interface PlanLoad {
    data class Loaded(val plan: NarrationPlan) : PlanLoad

    /** No plan has been built for this book yet. */
    data object Absent : PlanLoad

    data class Stale(val reason: StaleReason) : PlanLoad
}
