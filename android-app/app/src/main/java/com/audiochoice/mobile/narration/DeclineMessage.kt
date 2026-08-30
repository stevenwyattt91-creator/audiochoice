package com.audiochoice.mobile.narration

import com.audiochoice.mobile.reader.TextResourceRole

/**
 * What the listener is told when a file cannot be narrated.
 *
 * Modelled rather than assembled inside a composable for two reasons. The
 * requirements are about what is said -- name which documents are encrypted, name
 * three DRM-free sources, distinguish a Kindle purchase from a Kindle Unlimited
 * borrow -- and content assembled in a composable can only be checked by a UI
 * test. And the same content is wanted in more than one place: the import sheet
 * and, later, the library row for a book whose file stopped being readable.
 *
 * Copy lives in Kotlin rather than `strings.xml` because that is what the rest of
 * this app does; the codebase has two string resources and a hundred and forty
 * literals in its composables. Keeping it in one object at least makes it
 * reviewable and testable in a way scattered literals are not.
 */
data class DeclineMessage(
    val headline: String,
    val explanation: String,
    /** Specifics about this file, such as which documents are encrypted. */
    val details: List<String> = emptyList(),
    val drmFreeSources: List<DrmFreeSource> = emptyList(),
    val kindleGuidance: List<String> = emptyList(),
    val actions: List<DeclineAction> = emptyList(),
)

/** A place a listener can get an EPUB that will work. */
data class DrmFreeSource(val name: String, val url: String, val note: String)

/** A control offered alongside a decline. A null [url] means an in-app action. */
data class DeclineAction(val label: String, val url: String? = null)

object DeclineMessages {

    /**
     * Amazon's own page for a reader's purchased content. Named explicitly because
     * the January 2026 change made DRM-free Kindle purchases downloadable as EPUB
     * from here, which turns "your Kindle books will not work" into "some of them
     * will, and here is where".
     */
    const val AMAZON_CONTENT_AND_DEVICES_URL = "https://www.amazon.com/hz/mycd/myx"

    /**
     * Three or more, per the requirement. The first two are free and permanent,
     * the third is a commercial store, so the list is useful whether someone wants
     * a classic or a new release.
     */
    val DRM_FREE_SOURCES = listOf(
        DrmFreeSource(
            name = "Standard Ebooks",
            url = "https://standardebooks.org",
            note = "Free, carefully typeset public-domain books.",
        ),
        DrmFreeSource(
            name = "Project Gutenberg",
            url = "https://www.gutenberg.org",
            note = "Free public-domain library, EPUB downloads.",
        ),
        DrmFreeSource(
            name = "Smashwords",
            url = "https://www.smashwords.com",
            note = "Independent titles, sold without DRM.",
        ),
    )

    fun forReason(reason: DeclineReason): DeclineMessage = when (reason) {
        DeclineReason.CouldNotOpen -> DeclineMessage(
            headline = "This file could not be opened",
            explanation = "AudioChoice could not read the file you picked. It may be an " +
                "incomplete download, or the app may no longer have permission to read it. " +
                "Narration needs an EPUB whose text is unencrypted.",
            actions = listOf(DeclineAction("Pick a different file")),
        )

        DeclineReason.NotAnEpub -> DeclineMessage(
            headline = "This is not an EPUB AudioChoice can read",
            explanation = "The file opened, but it does not contain the package document every " +
                "EPUB needs. It may be a different format that has been renamed, or an EPUB " +
                "that did not finish downloading.",
            drmFreeSources = DRM_FREE_SOURCES,
            actions = listOf(DeclineAction("Pick a different file")),
        )

        DeclineReason.TextUnreadable -> DeclineMessage(
            headline = "This book's text could not be read",
            explanation = "The file is not protected, but none of its chapters could be opened. " +
                "The pages it lists are either missing from the file or unreadable.",
            drmFreeSources = DRM_FREE_SOURCES,
            actions = listOf(DeclineAction("Pick a different file")),
        )

        is DeclineReason.TooLittleText -> DeclineMessage(
            headline = "There is not enough text to narrate",
            explanation = "This file contains ${reason.letterOrDigitCount} letters and digits. " +
                "Narration needs at least ${reason.minimum}. It may be a cover-only file, a " +
                "sample, or a book whose text is stored as images rather than as text.",
            actions = listOf(DeclineAction("Pick a different file")),
        )

        is DeclineReason.StoreDrm -> DeclineMessage(
            headline = "The store that sold this file protected its text",
            explanation = "AudioChoice cannot read this book's text, so it cannot narrate it. " +
                "Whether a book is sold without protection is the publisher's or the author's " +
                "choice, not an AudioChoice restriction.",
            details = listOf(encryptedDocumentsSentence(reason.encryptedRoles)),
            drmFreeSources = DRM_FREE_SOURCES,
            kindleGuidance = listOf(
                "If you bought this book from Amazon and the author published it without " +
                    "protection, you can download it as an EPUB from Manage Your Content and " +
                    "Devices, then add that file here.",
                "A Kindle Unlimited book is borrowed rather than bought, so Amazon offers no " +
                    "EPUB download for it.",
            ),
            actions = listOf(
                DeclineAction(
                    label = "Open Manage Your Content and Devices",
                    url = AMAZON_CONTENT_AND_DEVICES_URL,
                ),
                DeclineAction("Pick a different file"),
            ),
        )
    }

    /**
     * Names which text-bearing documents are encrypted.
     *
     * Saying "the file is protected" without saying what is protected reads as the
     * app guessing, and it gives someone troubleshooting nothing to go on.
     */
    private fun encryptedDocumentsSentence(roles: List<TextResourceRole>): String {
        val names = roles.map { role ->
            when (role) {
                TextResourceRole.PACKAGE_DOCUMENT -> "the package document"
                TextResourceRole.NAVIGATION_DOCUMENT -> "the contents document"
                TextResourceRole.NCX_DOCUMENT -> "the contents index"
                TextResourceRole.SPINE_DOCUMENT -> "the chapter pages"
            }
        }
        val joined = when (names.size) {
            0 -> "part of this file"
            1 -> names.single()
            2 -> "${names[0]} and ${names[1]}"
            else -> names.dropLast(1).joinToString(", ") + " and " + names.last()
        }
        return "Encrypted in this file: $joined."
    }
}
