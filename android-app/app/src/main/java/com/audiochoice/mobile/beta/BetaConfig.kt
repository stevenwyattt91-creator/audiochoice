package com.audiochoice.mobile.beta

import com.audiochoice.contracts.BookFingerprint
import com.audiochoice.mobile.BuildConfig
import com.audiochoice.mobile.data.ExploreCatalogBook

data class BetaApprovedEdition(
    val part: Int?,
    val displayName: String,
    val catalogBook: ExploreCatalogBook,
)

/** Single source of truth for every closed-beta-only behavior. */
object BetaConfig {
    /**
     * Private owner test access, deliberately not surfaced in the UI. Closed-beta
     * testers still follow the normal pre-scanned-edition flow.
     *
     * Stored as a SHA-256 digest rather than the address itself: the plaintext
     * value was compiled into every shipping APK, so anyone who unzipped the app
     * could read a personal email address. The digest is not personal data and
     * the comparison is unchanged.
     *
     * The proper fix is a server-side entitlement on the account, which is
     * tracked on the backend checklist; this removes the exposure without adding
     * a backend dependency to the beta build.
     */
    private const val OWNER_TEST_EMAIL_SHA256 =
        "645252a133fb36dd27445f7343e30c0f35ba5d2d6c13d8c83c54259ce784abab"
    /**
     * Approved local converter output for Fourth Wing Part 1. Conversion can
     * rewrite the M4B container and strip its tags, so this full SHA-256 is
     * retained as an edition-level identity alongside the source fingerprint.
     */
    private const val FOURTH_WING_PART_1_CONVERTED_SHA256 =
        "4dc8a860a136f4880b41a011cce13393d3917725030665b72aa79032bee9c1c7"
    // Internal test-only allowance. Keep this out of supportedAudiobooks so it
    // never appears in the beta import screen or other public-facing copy.
    private const val INTERNAL_IRON_FLAME_PART_2 = "Iron Flame GraphicAudio Part 2"

    val enabled: Boolean get() = BuildConfig.BETA_BUILD
    val version: String get() = BuildConfig.BETA_VERSION
    val discordUrl: String get() = BuildConfig.BETA_DISCORD_URL
    val feedbackFormUrl: String get() = BuildConfig.BETA_FEEDBACK_FORM_URL

    fun hasOwnerTestingAccess(email: String): Boolean {
        if (!enabled) return false
        val normalized = email.trim().lowercase()
        if (normalized.isEmpty()) return false
        return java.security.MessageDigest.isEqual(
            sha256Hex(normalized).toByteArray(),
            OWNER_TEST_EMAIL_SHA256.toByteArray(),
        )
    }

    /** Internal for test coverage of the hex encoding, which must be unsigned. */
    internal fun sha256Hex(value: String): String =
        java.security.MessageDigest.getInstance("SHA-256")
            .digest(value.toByteArray())
            .joinToString("") { "%02x".format(it) }

    val supportedAudiobooks = listOf(
        "ACOTAR GraphicAudio Part 1",
        "ACOTAR GraphicAudio Part 2",
        "Fourth Wing GraphicAudio Part 1",
        "Dungeon Crawler Carl",
    )

    /**
     * Recognition begins with the existing catalog's SHA-256-derived ID (the first 96 bits),
     * then verifies the registered edition metadata. File names and user-entered text are never
     * used. A 96-bit cryptographic prefix is the strongest identity exposed by Explore today.
     */
    fun approvedEdition(
        fingerprint: BookFingerprint,
        catalog: List<ExploreCatalogBook>,
    ): BetaApprovedEdition? {
        if (!enabled) return null
        val exactCatalogID = fingerprint.sha256.lowercase().take(24)
        val exactBook = catalog.singleOrNull { it.catalogID.lowercase() == exactCatalogID }
        val convertedFourthWingBook = if (
            fingerprint.sha256.equals(FOURTH_WING_PART_1_CONVERTED_SHA256, ignoreCase = true)
        ) {
            catalog.singleOrNull { candidate ->
                normalize(candidate.title).contains("fourth wing") &&
                    Regex("(?:part )?1 of 2").containsMatchIn(normalize(candidate.title)) &&
                    normalize(candidate.editionType.orEmpty()).contains("dramatized adaptation")
            }
        } else {
            null
        }
        // M4B conversion changes a file's byte hash. For the approved split-part
        // editions only, accept the immutable embedded edition metadata and a
        // near-identical runtime, then bind it to the published catalog entry.
        val book = exactBook ?: convertedFourthWingBook ?: catalog.singleOrNull { candidate ->
            sameApprovedEdition(fingerprint, candidate)
        }
        if (book == null) return privateIronFlamePart2(fingerprint, catalog)
        val title = normalize(book.title)
        val author = normalize(book.author.orEmpty())
        val isDungeonCrawlerCarl = title.contains("dungeon crawler carl") &&
            author.contains("matt dinniman")
        if (isDungeonCrawlerCarl) {
            // The exact catalog ID above binds the beta import to the edition
            // already scanned and published by the main app. Unlike the
            // GraphicAudio beta titles, this is a single-volume audiobook.
            return BetaApprovedEdition(null, supportedAudiobooks[3], book)
        }
        val edition = normalize(book.editionType.orEmpty())
        val correctEdition = edition.contains("dramatized adaptation") ||
            title.contains("dramatized adaptation") || title.contains("graphicaudio")
        val part = when {
            Regex("(?:part )?1 of 2").containsMatchIn(title) || title.endsWith("part 1") -> 1
            Regex("(?:part )?2 of 2").containsMatchIn(title) || title.endsWith("part 2") -> 2
            else -> null
        } ?: return null
        if (!correctEdition) return null

        val displayName = when {
            title.contains("a court of thorns and roses") && part == 1 -> supportedAudiobooks[0]
            title.contains("a court of thorns and roses") && part == 2 -> supportedAudiobooks[1]
            title.contains("fourth wing") && part == 1 -> supportedAudiobooks[2]
            title.contains("iron flame") && part == 2 -> INTERNAL_IRON_FLAME_PART_2
            else -> return null
        }
        return BetaApprovedEdition(part, displayName, book)
    }

    /**
     * A local AAX-to-M4B remux changes the file hash and can discard some
     * edition tags. Resolve the privately approved Iron Flame Part 2 edition
     * from the original AAX metadata before conversion, then keep that binding
     * for the resulting local M4B. This remains intentionally absent from all
     * public beta messaging.
     */
    fun approvedAaxSourceEdition(
        fingerprint: BookFingerprint,
        catalog: List<ExploreCatalogBook>,
    ): BetaApprovedEdition? {
        approvedEdition(fingerprint, catalog)?.let { return it }
        return privateIronFlamePart2(fingerprint, catalog)
    }

    private fun privateIronFlamePart2(
        fingerprint: BookFingerprint,
        catalog: List<ExploreCatalogBook>,
    ): BetaApprovedEdition? {
        // This hidden test exception intentionally uses the imported file's
        // own title only. Some legitimate M4B/AAX editions do not preserve
        // author, series, or part tags after transfer/conversion.
        if (!normalize(fingerprint.workTitle.orEmpty()).contains("iron flame")) return null
        val book = catalog.singleOrNull { candidate ->
            val title = normalize(candidate.title)
            title.contains("iron flame") &&
                (Regex("(?:part )?2 of 2").containsMatchIn(title) || title.endsWith("part 2"))
        } ?: return null
        return BetaApprovedEdition(2, INTERNAL_IRON_FLAME_PART_2, book)
    }

    private fun normalize(value: String): String = value.lowercase()
        .replace(Regex("[^a-z0-9]+"), " ")
        .trim()

    private fun sameApprovedEdition(
        fingerprint: BookFingerprint,
        candidate: ExploreCatalogBook,
    ): Boolean {
        val candidateTitle = normalize(candidate.title)
        val localTitle = normalize(fingerprint.workTitle.orEmpty())
        val titleMatches = localTitle.contains(candidateTitle.substringBefore(" dramatized adaptation")) ||
            candidateTitle.contains(localTitle.substringBefore(" dramatized adaptation"))
        val localAuthor = normalize(fingerprint.author.orEmpty())
        val candidateAuthor = normalize(candidate.author.orEmpty())
        val isAcotar = candidateTitle.contains("a court of thorns and roses") ||
            localTitle.contains("a court of thorns and roses")
        val expectedAuthor = if (isAcotar) "maas" else "yarros"
        val authorMatches = (localAuthor.isBlank() || localAuthor.contains(expectedAuthor)) &&
            (candidateAuthor.isBlank() || candidateAuthor.contains(expectedAuthor))
        val candidateIsApproved = candidateTitle.contains("a court of thorns and roses") ||
            candidateTitle.contains("fourth wing") ||
            candidateTitle.contains("iron flame")
        val candidateEdition = normalize(candidate.editionType.orEmpty())
        val editionMatches = candidateEdition.contains("dramatized") ||
            candidateEdition.contains("graphic audio") ||
            candidateTitle.contains("dramatized adaptation") ||
            candidateTitle.contains("graphicaudio")
        val localPart = partNumber(localTitle)
        val candidatePart = partNumber(candidateTitle)
        val partMatches = localPart == null || candidatePart == null || localPart == candidatePart
        val runtimeMatches = fingerprint.duration != null && candidate.duration != null &&
            kotlin.math.abs(fingerprint.duration - candidate.duration) <= 900.0
        return candidateIsApproved && titleMatches && authorMatches && editionMatches && partMatches && runtimeMatches
    }

    private fun partNumber(value: String): Int? = when {
        Regex("(?:part )?1 of 2").containsMatchIn(value) || value.endsWith("part 1") -> 1
        Regex("(?:part )?2 of 2").containsMatchIn(value) || value.endsWith("part 2") -> 2
        else -> null
    }
}
