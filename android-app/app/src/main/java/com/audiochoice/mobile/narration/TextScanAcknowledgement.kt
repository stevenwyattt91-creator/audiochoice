package com.audiochoice.mobile.narration

import kotlinx.serialization.Serializable

/**
 * What a listener is told, and agrees to, before any of their book's text leaves the device.
 *
 * Naming each category of processor rather than saying "third parties" is the whole point.
 * Someone deciding whether to hand over a book they own is deciding about specific
 * recipients, and a description vague enough to cover anyone tells them nothing they can
 * weigh. The wording below is deliberately concrete about who receives the text, what each
 * recipient does with it, and what is not kept.
 *
 * Held in Kotlin rather than `strings.xml` to match the surrounding narration code, which
 * has its user-facing copy inline. This is a considered exception to the usual rule and the
 * reason is only consistency with the feature it belongs to: this project has two string
 * resources against a great many composable literals, so moving one statement into resources
 * would isolate it rather than follow a convention.
 */
object TextScanAcknowledgement {

    /**
     * Bumped whenever the set of recipients changes, or their role changes.
     *
     * A version bump means the listener is asked again, because they agreed to a different
     * arrangement from the one now in force. Rewording for clarity without changing who
     * receives what does not warrant one.
     */
    const val VERSION = "1"

    const val TITLE = "Reading this book aloud"

    /**
     * The statement itself.
     *
     * Note what it does not claim. It does not promise the text is never sent, because it is.
     * It promises what happens to it: held for one request, not written down, not used to
     * train anything. Those are commitments the implementation actually keeps and the tests
     * actually check, which is the only kind worth making.
     */
    val STATEMENT: String = listOf(
        "To read this book aloud and to offer the same content filters an audiobook gets, " +
            "AudioChoice needs to send this book's text off your device once.",
        "It goes to two places. The AudioChoice servers, which pass it straight through " +
            "without storing it. And the content-classification provider we use to find " +
            "passages you may want filtered, which processes the text and returns only " +
            "positions and neutral descriptions.",
        "If you choose the premium voice, the text of each chapter is also sent to " +
            "AudioChoice's own speech service on Amazon SageMaker, with Amazon Polly as a " +
            "fallback, to be turned into audio. The built-in voice on your phone needs none " +
            "of this and never sends anything.",
        "No copy of the book is kept on our servers, and its text is never used to train a " +
            "model. The audio and the filter results are stored on this device.",
        "This is asked once. You can read the book without any of it by using your phone's " +
            "built-in voice and skipping the filter scan.",
    ).joinToString("\n\n")

    /** What is recorded once the listener agrees. */
    fun record(acceptedAtMillis: Long): TextScanAcknowledgementRecord =
        TextScanAcknowledgementRecord(VERSION, STATEMENT, acceptedAtMillis)

    /**
     * Whether a stored record still covers what the app is about to do.
     *
     * Compares the version rather than the text, so fixing a typo does not invalidate
     * everyone's agreement, while adding a recipient does.
     */
    fun isCurrent(record: TextScanAcknowledgementRecord?): Boolean =
        record != null && record.version == VERSION
}

/**
 * The listener's agreement, kept with the text they agreed to.
 *
 * Storing the statement and not merely its version is what makes it possible to show
 * someone later exactly what they were told. A version on its own is only meaningful while
 * that wording is still in the build, which is precisely when nobody needs to look it up.
 */
@Serializable
data class TextScanAcknowledgementRecord(
    val version: String,
    val statement: String,
    val acceptedAtMillis: Long,
)
