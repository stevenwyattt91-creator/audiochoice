package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.PronunciationRule
import java.security.MessageDigest

/** Where a rule applies. */
enum class RuleScope {
    /** This book only, matched on the source EPUB's SHA-256. */
    BOOK,

    /** Every narrated book on the account. */
    ACCOUNT,
}

/** A rule together with the scope and order that decide when it wins. */
data class ScopedRule(
    val rule: PronunciationRule,
    val scope: RuleScope,
    /** Position in the order the listener recorded them, ascending. */
    val recordedOrder: Int,
)

/** Why a rule was not accepted. */
sealed interface RuleRejection {

    /** Names which form is out of bounds, so the listener knows which field to fix. */
    data class OutOfBounds(val form: Form, val limit: Int) : RuleRejection {
        enum class Form { WRITTEN, SPOKEN }

        val message: String get() = when (form) {
            Form.WRITTEN -> "Enter the word as it appears in the book, up to $limit characters."
            Form.SPOKEN -> "Enter how it should be said, up to $limit characters."
        }
    }

    /** Already present in this scope. The listener is offered the existing rule to edit. */
    data class Duplicate(val existing: PronunciationRule) : RuleRejection {
        val message: String get() =
            "There is already a rule for \"${existing.writtenForm}\". Edit that one instead?"
    }

    data class ScopeFull(val limit: Int) : RuleRejection {
        val message: String get() =
            "You have reached the limit of $limit pronunciation rules. Delete one to add another."
    }
}

/** What recording, editing or deleting a rule means for audio already made. */
data class RerenderImpact(
    val affectedChapterIndices: List<Int>,
) {
    val chapterCount: Int get() = affectedChapterIndices.size
    val isEmpty: Boolean get() = affectedChapterIndices.isEmpty()
}

/**
 * Pronunciation rules, and the one-pass substitution that applies them.
 *
 * Rules never touch Book_Text. They apply to the characters handed to a voice, after filtered
 * ranges have been removed, which is what keeps every unit's source range pointing at the
 * words the reader displays. A rule that rewrote Book_Text would move every offset in the
 * book and invalidate the plan, the timeline and the scan in one go.
 */
object PronunciationRules {

    /**
     * Taken from the model rather than restated, so the storage limit and the validation
     * limit cannot drift into disagreeing about what fits.
     */
    const val MAXIMUM_WRITTEN_CHARACTERS = PronunciationRule.MAXIMUM_FORM_LENGTH
    const val MAXIMUM_SPOKEN_CHARACTERS = PronunciationRule.MAXIMUM_FORM_LENGTH

    /**
     * Rules per scope. High enough that no real reader hits it -- a fantasy novel might need
     * thirty names -- and low enough that the single pass below stays fast on a long chapter.
     */
    const val MAXIMUM_RULES_PER_SCOPE = PronunciationRule.MAXIMUM_RULES_PER_SCOPE

    /**
     * Applies every rule in one left-to-right pass.
     *
     * The single pass is the whole design, and a sequence of independent replacements is the
     * obvious implementation that is wrong. Take a reader with two rules: "Rhysand" said
     * "REE-sand", and "and" said "AND" for emphasis. Replace the first, and the output now
     * contains "REE-sand"; replace the second over that output, and the "and" inside the
     * replacement is itself rewritten, giving "REE-sAND". The reader hears a mangled name they
     * cannot connect to anything they typed.
     *
     * So: one cursor, moving forward only, and at each position the highest-precedence rule
     * that matches wins. Characters already emitted as a substitution are never revisited,
     * which makes it impossible for one rule to rewrite another's output.
     *
     * Precedence at a position: book scope ahead of account scope, then earlier-recorded ahead
     * of later. Longest match is deliberately *not* the rule -- a listener who records a
     * specific rule expects it to win over a general one they added later, and recording order
     * is something they can see and reorder, whereas match length is not.
     */
    fun apply(text: String, rules: List<ScopedRule>): String {
        if (text.isEmpty() || rules.isEmpty()) return text

        val ordered = rules
            .filter { it.rule.writtenForm.isNotEmpty() }
            .sortedWith(compareBy({ it.scope.ordinal }, { it.recordedOrder }))
        if (ordered.isEmpty()) return text

        val result = StringBuilder(text.length)
        var cursor = 0
        while (cursor < text.length) {
            val match = ordered.firstOrNull { scoped -> matchesAt(text, cursor, scoped.rule.writtenForm) }
            if (match == null) {
                result.append(text[cursor])
                cursor += 1
                continue
            }
            // Appended, then skipped past. The cursor never re-enters what was just written,
            // so no rule can see another rule's output.
            result.append(match.rule.replacementForm)
            cursor += match.rule.writtenForm.length
        }
        return result.toString()
    }

    /**
     * Applies rules to each unit's text independently.
     *
     * Per unit, never across units, because a unit is what gets submitted to a voice and a
     * match spanning two of them would be substituted into neither. This is also what stops a
     * rule matching across the boundary of a removed filtered range: by the time text reaches
     * here the filtered characters are already gone, and the two sides of the removal are in
     * separate units or separated within one, so no match can bridge the gap.
     */
    fun applyToUnits(unitTexts: List<String>, rules: List<ScopedRule>): List<String> =
        unitTexts.map { apply(it, rules) }

    /**
     * Whether [written] occurs at [index] with non-alphanumeric boundaries.
     *
     * Case-insensitive, because a listener typing "rhysand" means the name wherever it appears.
     * Boundaries matter for the opposite reason: a rule for "and" must not fire inside
     * "Rhysand" or "sandwich", which is precisely the mistake that makes a naive
     * find-and-replace unusable on prose.
     */
    private fun matchesAt(text: String, index: Int, written: String): Boolean {
        if (index + written.length > text.length) return false
        if (!text.regionMatches(index, written, 0, written.length, ignoreCase = true)) return false

        val before = index - 1
        if (before >= 0 && text[before].isLetterOrDigit()) return false
        val after = index + written.length
        if (after < text.length && text[after].isLetterOrDigit()) return false
        return true
    }

    /**
     * Validates a rule against the rules already in its scope.
     *
     * Returns null when it is acceptable. The entered values are never modified here beyond
     * trimming, so a rejected form can be handed straight back to the listener with what they
     * typed still in it.
     */
    fun validate(
        written: String,
        spoken: String,
        existingInScope: List<PronunciationRule>,
        editingWritten: String? = null,
    ): RuleRejection? {
        val trimmedWritten = written.trim()
        val trimmedSpoken = spoken.trim()

        if (trimmedWritten.isEmpty() || trimmedWritten.length > MAXIMUM_WRITTEN_CHARACTERS) {
            return RuleRejection.OutOfBounds(
                RuleRejection.OutOfBounds.Form.WRITTEN, MAXIMUM_WRITTEN_CHARACTERS,
            )
        }
        if (trimmedSpoken.isEmpty() || trimmedSpoken.length > MAXIMUM_SPOKEN_CHARACTERS) {
            return RuleRejection.OutOfBounds(
                RuleRejection.OutOfBounds.Form.SPOKEN, MAXIMUM_SPOKEN_CHARACTERS,
            )
        }

        // Case-insensitive, matching how the rule itself matches: "Rhysand" and "rhysand"
        // would fire on the same words, so holding both would make the outcome depend on
        // recording order for no reason a listener could see.
        val duplicate = existingInScope.firstOrNull { candidate ->
            candidate.writtenForm.equals(trimmedWritten, ignoreCase = true) &&
                !candidate.writtenForm.equals(editingWritten, ignoreCase = true)
        }
        if (duplicate != null) return RuleRejection.Duplicate(duplicate)

        // An edit replaces rather than adds, so it cannot push a full scope over the limit.
        val isEdit = editingWritten != null &&
            existingInScope.any { it.writtenForm.equals(editingWritten, ignoreCase = true) }
        if (!isEdit && existingInScope.size >= MAXIMUM_RULES_PER_SCOPE) {
            return RuleRejection.ScopeFull(MAXIMUM_RULES_PER_SCOPE)
        }
        return null
    }

    /**
     * Which rendered chapters hold at least one match, so the listener can be told what
     * changing a rule costs before any audio is discarded.
     *
     * Uses the same matching as [apply], because a count derived from a looser rule would
     * offer to re-render chapters that would come out identical.
     */
    fun rerenderImpact(
        chapterTexts: Map<Int, List<String>>,
        rule: PronunciationRule,
    ): RerenderImpact = RerenderImpact(
        chapterTexts
            .filter { (_, unitTexts) ->
                unitTexts.any { text -> containsMatch(text, rule.writtenForm) }
            }
            .keys
            .sorted(),
    )

    fun containsMatch(text: String, written: String): Boolean {
        if (written.isEmpty()) return false
        var index = 0
        while (index + written.length <= text.length) {
            if (matchesAt(text, index, written)) return true
            index += 1
        }
        return false
    }

    /**
     * Identifies the rule set a plan was built with.
     *
     * Recorded on the plan so a chapter rendered under an older set is detectable. Order is
     * part of the fingerprint because order decides precedence, so two identical sets in a
     * different order genuinely produce different audio.
     */
    fun fingerprint(rules: List<ScopedRule>): String {
        if (rules.isEmpty()) return ""
        val digest = MessageDigest.getInstance("SHA-256")
        rules
            .sortedWith(compareBy({ it.scope.ordinal }, { it.recordedOrder }))
            .forEach { scoped ->
                digest.update(scoped.scope.name.toByteArray())
                digest.update(0)
                digest.update(scoped.rule.writtenForm.lowercase().toByteArray())
                digest.update(0)
                digest.update(scoped.rule.replacementForm.toByteArray())
                digest.update(0)
            }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    /** Combines the two scopes into the precedence order [apply] expects. */
    fun scoped(
        bookRules: List<PronunciationRule>,
        accountRules: List<PronunciationRule>,
    ): List<ScopedRule> =
        bookRules.mapIndexed { index, rule -> ScopedRule(rule, RuleScope.BOOK, index) } +
            accountRules.mapIndexed { index, rule -> ScopedRule(rule, RuleScope.ACCOUNT, index) }

    /** How long a preview may take to start, and how long it may run. */
    const val PREVIEW_START_DEADLINE_MS = 3_000L
    const val PREVIEW_MAXIMUM_MS = 10_000L
}
