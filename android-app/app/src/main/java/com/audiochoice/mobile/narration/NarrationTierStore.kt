package com.audiochoice.mobile.narration

import com.audiochoice.contracts.AccountAccessResponse
import com.audiochoice.mobile.data.SelectedVoice
import com.audiochoice.mobile.data.VoiceKind

/** Which narration voices an account may use. */
enum class NarrationTier {
    /** On-device voices only. Nothing a listener reads is sent anywhere to be spoken. */
    FREE,

    /** Adds the server-side voice. Requires an active entitlement, checked against the server. */
    PREMIUM,
}

/**
 * The tier in force, and how much confidence there is in it.
 *
 * [isConfirmed] is the part that matters to a listener. An unconfirmed Free tier is not the
 * same claim as a confirmed one: the first means "we could not check", which is worth saying
 * out loud to someone who is paying, and the second means "you are on the free tier".
 */
data class NarrationTierState(
    val tier: NarrationTier,
    val plan: String?,
    /** When the tier was last confirmed against the server, or null if it never was. */
    val confirmedAtEpochMillis: Long?,
    val isConfirmed: Boolean,
) {
    /** Whether the premium voice may be used right now. */
    val allowsPremiumVoice: Boolean get() = tier == NarrationTier.PREMIUM
}

/** What to do when a book was partly rendered with a voice the account may no longer use. */
sealed interface PremiumVoiceLapse {

    /** Nothing to do: either the tier still allows premium, or no premium audio exists. */
    data object None : PremiumVoiceLapse

    /**
     * Premium audio exists but no more may be made.
     *
     * [keptChapters] stay exactly as they are. Deleting audio a listener paid for because
     * their subscription later lapsed would be taking back something already delivered, and
     * it is also why provider and voice are recorded per chapter rather than per book: a book
     * legitimately holds two voices, and without the per-chapter record its audio would be
     * inexplicable.
     */
    data class Lapsed(
        val keptChapters: List<Int>,
        val chaptersNeedingAnotherVoice: List<Int>,
    ) : PremiumVoiceLapse {
        val message: String get() =
            "The premium voice is no longer available on your account. The " +
                "${keptChapters.size} chapter${if (keptChapters.size == 1) "" else "s"} " +
                "already made with it will keep playing. The remaining " +
                "${chaptersNeedingAnotherVoice.size} can be read by a voice on your device."
    }
}

/**
 * Tier rules, separated from storage and from the network so every transition is testable.
 */
object NarrationTiers {

    /**
     * How long a recorded tier survives without being reconfirmed.
     *
     * Long enough to cover a holiday with no signal, short enough that a lapsed
     * subscription stops paying for server synthesis within a week.
     */
    const val GRACE_MILLIS = 7L * 24 * 60 * 60 * 1_000

    /** How often the entitlement is reconfirmed while a narrated book is in the library. */
    const val REFRESH_INTERVAL_MILLIS = 24L * 60 * 60 * 1_000

    /**
     * The tier an entitlement response implies.
     *
     * Premium requires both an active flag and an expiry that is absent or in the future. An
     * unparseable expiry counts as expired: "absent" grants access with no end date, and a
     * value nobody could read is not evidence of that.
     */
    fun tierFor(access: AccountAccessResponse, nowEpochMillis: Long): NarrationTier {
        if (!access.isActive) return NarrationTier.FREE

        // Only an explicit null means "no end date". A present-but-blank value is not the
        // server saying that -- it distinguishes the two, so an empty string is a defect, and
        // reading a defect as unlimited access is the expensive direction to be wrong in.
        val expiresAt = access.expiresAt ?: return NarrationTier.PREMIUM

        val instant = parseInstant(expiresAt) ?: return NarrationTier.FREE
        return if (instant > nowEpochMillis) NarrationTier.PREMIUM else NarrationTier.FREE
    }

    /** The state to record after a successful read. */
    fun confirmed(
        access: AccountAccessResponse,
        nowEpochMillis: Long,
    ): NarrationTierState = NarrationTierState(
        tier = tierFor(access, nowEpochMillis),
        plan = access.plan.takeIf { it.isNotBlank() },
        confirmedAtEpochMillis = nowEpochMillis,
        isConfirmed = true,
    )

    /**
     * The state to use when the server could not be reached.
     *
     * Within the grace window the last recorded tier stands, but is reported as unconfirmed
     * so a paying listener is told their entitlement could not be checked rather than being
     * silently downgraded. Past the window the tier is Free, because an entitlement that
     * cannot be confirmed for a week is not one to keep honouring.
     *
     * A recorded time in the future means the device clock moved backwards, or the record was
     * tampered with. Either way the elapsed time is not a measurement of anything, so the
     * grace period is treated as used up rather than as freshly begun -- otherwise setting a
     * phone's clock back would extend premium access indefinitely.
     */
    fun withoutConfirmation(
        recorded: NarrationTierState?,
        nowEpochMillis: Long,
    ): NarrationTierState {
        val confirmedAt = recorded?.confirmedAtEpochMillis
            ?: return NarrationTierState(NarrationTier.FREE, recorded?.plan, null, false)

        val elapsed = nowEpochMillis - confirmedAt
        val withinGrace = elapsed in 0 until GRACE_MILLIS
        return NarrationTierState(
            tier = if (withinGrace) recorded.tier else NarrationTier.FREE,
            plan = recorded.plan,
            confirmedAtEpochMillis = confirmedAt,
            isConfirmed = false,
        )
    }

    /** Whether the entitlement is due to be reconfirmed. */
    fun shouldRefresh(lastConfirmedAtEpochMillis: Long?, nowEpochMillis: Long): Boolean {
        val confirmedAt = lastConfirmedAtEpochMillis ?: return true
        val elapsed = nowEpochMillis - confirmedAt
        // A record from the future is not usable as a measurement, so refresh rather than
        // wait out an interval that may never elapse.
        if (elapsed < 0) return true
        return elapsed >= REFRESH_INTERVAL_MILLIS
    }

    /**
     * Which voices to offer.
     *
     * Free is not a degraded experience: both on-device voices are offered, and neither sends
     * a word of the book anywhere. Premium adds the server voice and takes nothing away.
     *
     * No purchase control and no price appear anywhere in this list, because billing is not
     * built. During the experimental cycle a premium entitlement is granted through the
     * existing administrative endpoint, so offering a listener a way to buy something that
     * cannot be bought would be worse than offering nothing.
     */
    fun availableVoiceKinds(
        tier: NarrationTier,
        localNeuralSupported: Boolean,
    ): List<VoiceKind> = buildList {
        // Always present, always works, needs no network and no entitlement.
        add(VoiceKind.SYSTEM)
        if (localNeuralSupported) add(VoiceKind.LOCAL_NEURAL)
        if (tier == NarrationTier.PREMIUM) add(VoiceKind.PREMIUM)
    }

    /**
     * Whether choosing this voice sends the book's text off the device.
     *
     * A single place to ask, so the acknowledgement gate and the render path cannot disagree
     * about which voices need one.
     */
    fun sendsTextOffDevice(kind: VoiceKind): Boolean = when (kind) {
        VoiceKind.SYSTEM, VoiceKind.LOCAL_NEURAL -> false
        VoiceKind.PREMIUM -> true
    }

    /** Whether a voice may be used under [tier]. */
    fun isVoiceAllowed(kind: VoiceKind, tier: NarrationTier, localNeuralSupported: Boolean): Boolean =
        kind in availableVoiceKinds(tier, localNeuralSupported)

    /**
     * What a lapse means for a book that already has premium audio.
     *
     * [premiumRenderedChapters] are kept and [remainingChapters] need another voice. The
     * split is by chapter because that is the unit audio is written in, and because a book
     * that spans a lapse genuinely holds two voices.
     */
    fun lapseFor(
        tier: NarrationTier,
        premiumRenderedChapters: List<Int>,
        remainingChapters: List<Int>,
    ): PremiumVoiceLapse {
        if (tier == NarrationTier.PREMIUM) return PremiumVoiceLapse.None
        if (premiumRenderedChapters.isEmpty()) return PremiumVoiceLapse.None
        return PremiumVoiceLapse.Lapsed(
            keptChapters = premiumRenderedChapters.sorted(),
            chaptersNeedingAnotherVoice = remainingChapters.sorted(),
        )
    }

    /**
     * Parses an ISO-8601 instant, or null when it cannot be read.
     *
     * Hand-rolled rather than `java.time`, which is what the rest of this module does for the
     * same reason: `Instant.parse` is available from API 26 and this app supports older
     * devices, and desugaring is not something to rely on for an entitlement decision.
     */
    internal fun parseInstant(value: String): Long? = runCatching {
        // Normalises the common shapes the server emits: a trailing Z, or a numeric offset.
        val trimmed = value.trim()
        val match = IsoPattern.matchEntire(trimmed) ?: return null
        val (year, month, day, hour, minute, second, fraction, zone) = match.destructured

        // The pattern checks shape, not range, and a shape-only check is not enough: a
        // nonsense date like 2026-13-45T99:99:99Z has the right shape and arithmetic turns it
        // into a perfectly ordinary instant some months in the future. Left unvalidated, a
        // malformed expiry would therefore *grant* premium rather than deny it -- the failure
        // this whole path is built to avoid.
        val monthValue = month.toInt()
        val dayValue = day.toInt()
        if (monthValue !in 1..12) return null
        if (dayValue !in 1..daysInMonth(year.toInt(), monthValue)) return null
        if (hour.toInt() !in 0..23) return null
        if (minute.toInt() !in 0..59) return null
        // 60 admitted for a leap second, which some servers still emit.
        if (second.toInt() !in 0..60) return null

        val days = daysFromCivil(year.toInt(), monthValue, dayValue)
        var millis = days * 86_400_000L +
            hour.toLong() * 3_600_000L +
            minute.toLong() * 60_000L +
            second.toLong() * 1_000L
        if (fraction.isNotEmpty()) {
            millis += fraction.padEnd(3, '0').take(3).toLong()
        }
        if (zone.isNotEmpty() && !zone.equals("Z", ignoreCase = true)) {
            val sign = if (zone[0] == '-') 1 else -1
            val offsetHours = zone.substring(1, 3).toLong()
            val offsetMinutes = zone.substring(zone.length - 2).toLong()
            millis += sign * (offsetHours * 3_600_000L + offsetMinutes * 60_000L)
        }
        millis
    }.getOrNull()

    private fun daysInMonth(year: Int, month: Int): Int = when (month) {
        1, 3, 5, 7, 8, 10, 12 -> 31
        4, 6, 9, 11 -> 30
        2 -> if (isLeapYear(year)) 29 else 28
        else -> 0
    }

    private fun isLeapYear(year: Int): Boolean =
        year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)

    /** Days since 1970-01-01, by Howard Hinnant's civil-date algorithm. */
    private fun daysFromCivil(year: Int, month: Int, day: Int): Long {
        val y = if (month <= 2) year - 1 else year
        val era = (if (y >= 0) y else y - 399) / 400
        val yearOfEra = y - era * 400
        val monthTerm = if (month > 2) month - 3 else month + 9
        val dayOfYear = (153 * monthTerm + 2) / 5 + day - 1
        val dayOfEra = yearOfEra * 365L + yearOfEra / 4 - yearOfEra / 100 + dayOfYear
        return era * 146_097L + dayOfEra - 719_468L
    }

    private val IsoPattern = Regex(
        """(\d{4})-(\d{2})-(\d{2})[Tt ](\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?([Zz]|[+-]\d{2}:?\d{2})?""",
    )
}

/**
 * Reads and records the narration tier.
 *
 * Collaborators are function types so the whole state machine can be driven without a
 * network or a device clock. The rules themselves live in [NarrationTiers]; this class only
 * sequences them and decides when to ask again.
 */
class NarrationTierStore(
    private val readAccess: suspend () -> AccountAccessResponse,
    private val loadRecorded: suspend () -> NarrationTierState?,
    private val saveRecorded: suspend (NarrationTierState) -> Unit,
    private val now: () -> Long = System::currentTimeMillis,
) {

    /**
     * The tier to act on, refreshing first if it is due.
     *
     * [force] is set when the listener opens a voice-selection surface: choosing a voice is
     * exactly the moment a stale entitlement would be noticed, so it is checked then
     * regardless of the interval.
     */
    suspend fun currentTier(force: Boolean = false): NarrationTierState {
        val recorded = loadRecorded()
        val nowMillis = now()

        if (!force && !NarrationTiers.shouldRefresh(recorded?.confirmedAtEpochMillis, nowMillis)) {
            // Recent enough to stand on its own. Reported as confirmed because it was, at a
            // time within the refresh interval.
            return recorded ?: NarrationTiers.withoutConfirmation(null, nowMillis)
        }

        return try {
            val confirmed = NarrationTiers.confirmed(readAccess(), nowMillis)
            saveRecorded(confirmed)
            confirmed
        } catch (cancellation: kotlinx.coroutines.CancellationException) {
            throw cancellation
        } catch (_: Throwable) {
            // Nothing is recorded on failure. The stored confirmation time must keep meaning
            // "when the server last agreed", or an offline device would renew its own grace
            // period every time it failed to reach the server.
            NarrationTiers.withoutConfirmation(recorded, nowMillis)
        }
    }

    /**
     * The voice to use for the chapters still to be rendered.
     *
     * Returns null when the recorded choice is still allowed. Otherwise the caller has to ask
     * the listener, which is deliberate: silently swapping the voice halfway through a book
     * would be a surprising thing to discover mid-chapter.
     */
    fun fallbackNeeded(
        selected: SelectedVoice?,
        tier: NarrationTierState,
        localNeuralSupported: Boolean,
    ): Boolean {
        val kind = selected?.kind ?: return false
        return !NarrationTiers.isVoiceAllowed(kind, tier.tier, localNeuralSupported)
    }
}
