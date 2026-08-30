package com.audiochoice.mobile.narration

import com.audiochoice.contracts.AccountAccessResponse
import com.audiochoice.mobile.data.SelectedVoice
import com.audiochoice.mobile.data.VoiceKind
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class NarrationTierStoreTest {

    // region entitlement to tier

    /**
     * Premium requires an active flag *and* an expiry that has not passed. Both halves matter:
     * a lapsed subscription often keeps `isActive` until a billing job catches up.
     */
    @Test
    fun `premium requires an active entitlement that has not expired`() {
        assertEquals(
            NarrationTier.PREMIUM,
            NarrationTiers.tierFor(access(active = true, expiresAt = null), NOW),
        )
        assertEquals(
            NarrationTier.PREMIUM,
            NarrationTiers.tierFor(access(active = true, expiresAt = "2026-12-31T00:00:00Z"), NOW),
        )
        assertEquals(
            NarrationTier.FREE,
            NarrationTiers.tierFor(access(active = true, expiresAt = "2020-01-01T00:00:00Z"), NOW),
        )
        assertEquals(
            NarrationTier.FREE,
            NarrationTiers.tierFor(access(active = false, expiresAt = null), NOW),
        )
    }

    /**
     * An expiry nobody can read is treated as expired, not as absent. Absent means access with
     * no end date, and a parse failure is not evidence of that -- reading it the other way
     * would turn a malformed date into unlimited premium synthesis.
     */
    @Test
    fun `an unreadable expiry is treated as expired rather than absent`() {
        listOf("not a date", "", "   ", "2026-13-45T99:99:99Z", "1788006896").forEach { value ->
            assertEquals(
                "'$value' should not grant premium",
                NarrationTier.FREE,
                NarrationTiers.tierFor(access(active = true, expiresAt = value), NOW),
            )
        }
    }

    /**
     * Only an explicit null means "no end date". A present-but-blank value looks the same to
     * a reader and behaves oppositely, which is exactly why both directions are pinned here.
     */
    @Test
    fun `only a null expiry grants premium, never a blank or malformed one`() {
        assertEquals(
            NarrationTier.PREMIUM,
            NarrationTiers.tierFor(access(active = true, expiresAt = null), NOW),
        )
        assertEquals(
            NarrationTier.FREE,
            NarrationTiers.tierFor(access(active = true, expiresAt = "yesterday"), NOW),
        )
        assertEquals(
            "a blank expiry is a defect, not permission",
            NarrationTier.FREE,
            NarrationTiers.tierFor(access(active = true, expiresAt = ""), NOW),
        )
    }

    // endregion

    // region the instant parser

    /**
     * Hand-rolled because `Instant.parse` needs API 26 and an entitlement decision is not
     * something to leave to desugaring. Checked against known epoch values, including a leap
     * day, a century boundary and both offset directions -- every place a hand-rolled civil
     * date calculation goes wrong.
     */
    @Test
    fun `the instant parser agrees with known epoch values`() {
        val cases = mapOf(
            "1970-01-01T00:00:00Z" to 0L,
            "2024-01-01T00:00:00Z" to 1_704_067_200_000L,
            "2026-08-29T12:34:56Z" to 1_788_006_896_000L,
            "2026-08-29T12:34:56.789Z" to 1_788_006_896_789L,
            // 2000 is a leap year; 1900 and 2100 are not.
            "2000-02-29T00:00:00Z" to 951_782_400_000L,
            "1999-12-31T23:59:59Z" to 946_684_799_000L,
            "2100-03-01T00:00:00Z" to 4_107_542_400_000L,
            // Offsets move the instant in the opposite direction to the sign.
            "2026-08-29T12:34:56+02:00" to 1_787_999_696_000L,
            "2026-08-29T12:34:56-05:00" to 1_788_024_896_000L,
        )
        cases.forEach { (text, expected) ->
            assertEquals(text, expected, NarrationTiers.parseInstant(text))
        }
    }

    @Test
    fun `the instant parser rejects what it cannot read`() {
        listOf("", "2026-08-29", "29-08-2026T00:00:00Z", "2026-08-29T12:34Z", "nonsense")
            .forEach { assertNull("'$it' should not parse", NarrationTiers.parseInstant(it)) }
    }

    /**
     * Out-of-range components with the right shape are the dangerous case, and the reason is
     * counter-intuitive: unvalidated arithmetic turns nonsense like month 13 into an ordinary
     * instant in the *future*, so a malformed expiry would grant premium rather than deny it.
     */
    @Test
    fun `the instant parser rejects out-of-range components`() {
        listOf(
            "2026-13-01T00:00:00Z",   // month 13
            "2026-00-01T00:00:00Z",   // month 0
            "2026-08-32T00:00:00Z",   // day 32
            "2026-08-00T00:00:00Z",   // day 0
            "2026-02-30T00:00:00Z",   // February never has 30
            "2025-02-29T00:00:00Z",   // 2025 is not a leap year
            "2100-02-29T00:00:00Z",   // nor is 2100, despite dividing by four
            "2026-08-29T24:00:00Z",   // hour 24
            "2026-08-29T12:60:00Z",   // minute 60
            "2026-08-29T12:34:61Z",   // second 61
            "2026-13-45T99:99:99Z",   // all at once
        ).forEach { assertNull("'$it' should not parse", NarrationTiers.parseInstant(it)) }

        // The genuine edge cases next to them must still parse.
        listOf(
            "2024-02-29T00:00:00Z",   // a real leap day
            "2000-02-29T00:00:00Z",   // divisible by 400
            "2026-08-29T23:59:59Z",
            "2026-08-29T12:34:60Z",   // a leap second, which some servers emit
        ).forEach { assertNotNull("'$it' should parse", NarrationTiers.parseInstant(it)) }
    }

    /**
     * Checked against the platform's own date library across a wide span of instants, because
     * a hand-rolled civil-date calculation is exactly the kind of code that is right for the
     * dates someone thought to try and wrong three years either side.
     */
    @Test
    fun `the instant parser agrees with the platform across many dates`() {
        var checked = 0
        // Every 37 days for roughly forty years, which crosses every leap year and century
        // rule in range without being a slow test.
        var epochDay = -3_650L
        while (epochDay < 11_000L) {
            val millis = epochDay * 86_400_000L + 45_296_000L // 12:34:56
            val text = java.time.Instant.ofEpochMilli(millis).toString()
            assertEquals(text, millis, NarrationTiers.parseInstant(text))
            checked += 1
            epochDay += 37
        }
        assertTrue("the sweep checked almost nothing", checked > 350)
    }

    // endregion

    // region grace period

    /**
     * Inside the window the tier stands, but is reported unconfirmed. A paying listener is
     * told their entitlement could not be checked rather than being silently downgraded.
     */
    @Test
    fun `a premium tier survives the grace window but is reported unconfirmed`() {
        val recorded = recordedPremium(confirmedAt = NOW)
        val state = NarrationTiers.withoutConfirmation(
            recorded,
            NOW + NarrationTiers.GRACE_MILLIS - 1,
        )
        assertEquals(NarrationTier.PREMIUM, state.tier)
        assertFalse("a tier held on trust must not claim to be confirmed", state.isConfirmed)
        assertEquals(NOW, state.confirmedAtEpochMillis)
    }

    /**
     * The boundary is exclusive: seven days of grace means that at exactly seven days it is
     * used up. Pinned because "keep for 7 days" is ambiguous by one interval, and an
     * entitlement claim should resolve the ambiguity against itself.
     */
    @Test
    fun `at exactly seven days the grace period is over`() {
        val recorded = recordedPremium(confirmedAt = NOW)

        val justInside = NarrationTiers.withoutConfirmation(
            recorded, NOW + NarrationTiers.GRACE_MILLIS - 1,
        )
        val exactly = NarrationTiers.withoutConfirmation(
            recorded, NOW + NarrationTiers.GRACE_MILLIS,
        )

        assertEquals(NarrationTier.PREMIUM, justInside.tier)
        assertEquals("premium survived a full seven days without confirmation",
            NarrationTier.FREE, exactly.tier)
        assertFalse(exactly.isConfirmed)
    }

    /**
     * A recorded time in the future means the device clock moved back, or the record was
     * edited. Either way the elapsed time measures nothing, so the grace period counts as
     * spent -- otherwise setting a phone's clock back would extend premium indefinitely.
     */
    @Test
    fun `a confirmation timestamp in the future does not renew the grace period`() {
        val recorded = recordedPremium(confirmedAt = NOW + 60_000)
        val state = NarrationTiers.withoutConfirmation(recorded, NOW)
        assertEquals(NarrationTier.FREE, state.tier)
        assertFalse(state.isConfirmed)
    }

    @Test
    fun `nothing recorded means an unconfirmed free tier`() {
        val state = NarrationTiers.withoutConfirmation(null, NOW)
        assertEquals(NarrationTier.FREE, state.tier)
        assertFalse(state.isConfirmed)
        assertNull(state.confirmedAtEpochMillis)
    }

    /** A free tier inside the grace window is still free. The window preserves, not promotes. */
    @Test
    fun `the grace window does not promote a free tier`() {
        val recorded = NarrationTierState(NarrationTier.FREE, null, NOW, true)
        val state = NarrationTiers.withoutConfirmation(recorded, NOW + 1_000)
        assertEquals(NarrationTier.FREE, state.tier)
    }

    // endregion

    // region refresh interval

    @Test
    fun `the entitlement is reconfirmed once a day`() {
        assertTrue("never read should refresh", NarrationTiers.shouldRefresh(null, NOW))
        assertFalse(
            NarrationTiers.shouldRefresh(NOW, NOW + NarrationTiers.REFRESH_INTERVAL_MILLIS - 1),
        )
        assertTrue(
            NarrationTiers.shouldRefresh(NOW, NOW + NarrationTiers.REFRESH_INTERVAL_MILLIS),
        )
        // A record from the future would otherwise wait out an interval that never elapses.
        assertTrue(NarrationTiers.shouldRefresh(NOW + 60_000, NOW))
    }

    // endregion

    // region voice availability

    /**
     * Free is not a degraded tier: both on-device voices are offered and neither sends a word
     * anywhere. Premium adds one voice and removes nothing.
     */
    @Test
    fun `free offers the on-device voices and premium adds the server voice`() {
        assertEquals(
            listOf(VoiceKind.SYSTEM, VoiceKind.LOCAL_NEURAL),
            NarrationTiers.availableVoiceKinds(NarrationTier.FREE, localNeuralSupported = true),
        )
        assertEquals(
            listOf(VoiceKind.SYSTEM, VoiceKind.LOCAL_NEURAL, VoiceKind.PREMIUM),
            NarrationTiers.availableVoiceKinds(NarrationTier.PREMIUM, localNeuralSupported = true),
        )
        // The system voice is always there, so a device with no neural model still reads.
        assertEquals(
            listOf(VoiceKind.SYSTEM),
            NarrationTiers.availableVoiceKinds(NarrationTier.FREE, localNeuralSupported = false),
        )
    }

    @Test
    fun `the premium voice is never offered on the free tier`() {
        listOf(true, false).forEach { supported ->
            assertFalse(
                NarrationTiers.availableVoiceKinds(NarrationTier.FREE, supported)
                    .contains(VoiceKind.PREMIUM),
            )
            assertFalse(
                NarrationTiers.isVoiceAllowed(VoiceKind.PREMIUM, NarrationTier.FREE, supported),
            )
        }
    }

    /**
     * Only the premium voice sends text off the device. Asked in one place so the
     * acknowledgement gate and the render path cannot disagree about which voices need one.
     */
    @Test
    fun `only the premium voice sends text off the device`() {
        assertFalse(NarrationTiers.sendsTextOffDevice(VoiceKind.SYSTEM))
        assertFalse(NarrationTiers.sendsTextOffDevice(VoiceKind.LOCAL_NEURAL))
        assertTrue(NarrationTiers.sendsTextOffDevice(VoiceKind.PREMIUM))
        // Every free-tier voice must be a voice that keeps the text on the device.
        NarrationTiers.availableVoiceKinds(NarrationTier.FREE, localNeuralSupported = true)
            .forEach { kind ->
                assertFalse(
                    "$kind is offered on the free tier but sends text off the device",
                    NarrationTiers.sendsTextOffDevice(kind),
                )
            }
    }

    // endregion

    // region premium lapse

    /**
     * Audio already made with the premium voice keeps playing. Deleting it because a
     * subscription later lapsed would be taking back something already delivered, and it is
     * why provider and voice are recorded per chapter rather than per book.
     */
    @Test
    fun `a lapse keeps premium audio and asks for a voice for the rest`() {
        val lapse = NarrationTiers.lapseFor(
            NarrationTier.FREE,
            premiumRenderedChapters = listOf(2, 0, 1),
            remainingChapters = listOf(5, 3, 4),
        )
        val lapsed = lapse as PremiumVoiceLapse.Lapsed
        assertEquals(listOf(0, 1, 2), lapsed.keptChapters)
        assertEquals(listOf(3, 4, 5), lapsed.chaptersNeedingAnotherVoice)
        assertTrue(lapsed.message.contains("keep playing"))
        assertTrue(lapsed.message.contains("3 chapters"))
    }

    @Test
    fun `no lapse while the tier still allows premium`() {
        assertEquals(
            PremiumVoiceLapse.None,
            NarrationTiers.lapseFor(NarrationTier.PREMIUM, listOf(0, 1), listOf(2)),
        )
    }

    @Test
    fun `no lapse for a book with no premium audio`() {
        assertEquals(
            PremiumVoiceLapse.None,
            NarrationTiers.lapseFor(NarrationTier.FREE, emptyList(), listOf(0, 1, 2)),
        )
    }

    /** Singular wording, because "1 chapters will keep playing" reads like a bug. */
    @Test
    fun `the lapse message reads correctly for a single chapter`() {
        val lapsed = NarrationTiers.lapseFor(NarrationTier.FREE, listOf(0), listOf(1))
            as PremiumVoiceLapse.Lapsed
        assertTrue(lapsed.message, lapsed.message.contains("1 chapter already"))
        assertFalse(lapsed.message, lapsed.message.contains("1 chapters"))
    }

    // endregion

    // region the store, driven through every transition

    @Test
    fun `a successful read records the tier, the plan and the time`(): Unit = runBlocking {
        val saved = mutableListOf<NarrationTierState>()
        val store = NarrationTierStore(
            readAccess = { access(active = true, expiresAt = null, plan = "premium-monthly") },
            loadRecorded = { null },
            saveRecorded = { saved += it },
            now = { NOW },
        )

        val state = store.currentTier()

        assertEquals(NarrationTier.PREMIUM, state.tier)
        assertEquals("premium-monthly", state.plan)
        assertEquals(NOW, state.confirmedAtEpochMillis)
        assertTrue(state.isConfirmed)
        assertEquals(listOf(state), saved)
    }

    /**
     * A failed read must not record anything. The stored time has to keep meaning "when the
     * server last agreed", or an offline device would renew its own grace period each time it
     * failed to reach the server -- premium forever, one failure at a time.
     */
    @Test
    fun `a failed read records nothing and holds the tier on trust`(): Unit = runBlocking {
        val saved = mutableListOf<NarrationTierState>()
        val store = NarrationTierStore(
            readAccess = { throw java.io.IOException("offline") },
            loadRecorded = { recordedPremium(confirmedAt = NOW) },
            saveRecorded = { saved += it },
            now = { NOW + NarrationTiers.REFRESH_INTERVAL_MILLIS },
        )

        val state = store.currentTier()

        assertEquals(NarrationTier.PREMIUM, state.tier)
        assertFalse(state.isConfirmed)
        assertEquals("the confirmation time was moved by a failure", NOW,
            state.confirmedAtEpochMillis)
        assertTrue("a failed read recorded a new confirmation", saved.isEmpty())
    }

    @Test
    fun `a failed read past the grace window falls back to free`(): Unit = runBlocking {
        val store = NarrationTierStore(
            readAccess = { throw java.io.IOException("offline") },
            loadRecorded = { recordedPremium(confirmedAt = NOW) },
            saveRecorded = {},
            now = { NOW + NarrationTiers.GRACE_MILLIS },
        )

        val state = store.currentTier()

        assertEquals(NarrationTier.FREE, state.tier)
        assertFalse(state.isConfirmed)
    }

    @Test
    fun `a recent confirmation is reused without asking again`(): Unit = runBlocking {
        var reads = 0
        val store = NarrationTierStore(
            readAccess = { reads += 1; access(active = true, expiresAt = null) },
            loadRecorded = { recordedPremium(confirmedAt = NOW) },
            saveRecorded = {},
            now = { NOW + 1_000 },
        )

        val state = store.currentTier()

        assertEquals(0, reads)
        assertEquals(NarrationTier.PREMIUM, state.tier)
    }

    /**
     * Opening a voice-selection surface forces a read. Choosing a voice is exactly the moment
     * a stale entitlement would be noticed, so it is checked then whatever the interval says.
     */
    @Test
    fun `opening voice selection forces a read even when recent`(): Unit = runBlocking {
        var reads = 0
        val store = NarrationTierStore(
            readAccess = { reads += 1; access(active = false, expiresAt = null) },
            loadRecorded = { recordedPremium(confirmedAt = NOW) },
            saveRecorded = {},
            now = { NOW + 1_000 },
        )

        val state = store.currentTier(force = true)

        assertEquals(1, reads)
        assertEquals("a forced read did not apply the server's answer",
            NarrationTier.FREE, state.tier)
    }

    /** Premium to free, confirmed by the server rather than guessed at. */
    @Test
    fun `a server-confirmed downgrade takes effect immediately`(): Unit = runBlocking {
        val saved = mutableListOf<NarrationTierState>()
        val store = NarrationTierStore(
            readAccess = { access(active = true, expiresAt = "2020-01-01T00:00:00Z") },
            loadRecorded = { recordedPremium(confirmedAt = NOW - NarrationTiers.REFRESH_INTERVAL_MILLIS) },
            saveRecorded = { saved += it },
            now = { NOW },
        )

        val state = store.currentTier()

        assertEquals(NarrationTier.FREE, state.tier)
        assertTrue(state.isConfirmed)
        assertEquals(NarrationTier.FREE, saved.single().tier)
    }

    /** Free to premium, the other direction, so the machine is not one-way. */
    @Test
    fun `an upgrade takes effect on the next read`(): Unit = runBlocking {
        val store = NarrationTierStore(
            readAccess = { access(active = true, expiresAt = null, plan = "premium-annual") },
            loadRecorded = { NarrationTierState(NarrationTier.FREE, null, NOW - NarrationTiers.REFRESH_INTERVAL_MILLIS, true) },
            saveRecorded = {},
            now = { NOW },
        )

        val state = store.currentTier()

        assertEquals(NarrationTier.PREMIUM, state.tier)
        assertEquals("premium-annual", state.plan)
    }

    @Test
    fun `cancellation propagates rather than being read as a failed entitlement`(): Unit =
        runBlocking {
            val store = NarrationTierStore(
                readAccess = { throw kotlinx.coroutines.CancellationException("gone") },
                loadRecorded = { recordedPremium(confirmedAt = NOW) },
                saveRecorded = {},
                now = { NOW + NarrationTiers.REFRESH_INTERVAL_MILLIS },
            )

            var propagated = false
            try {
                store.currentTier()
            } catch (expected: kotlinx.coroutines.CancellationException) {
                propagated = true
            }
            assertTrue(propagated)
        }

    // endregion

    // region the mixed-provider book

    /**
     * The case the per-chapter voice record exists for: a book half-rendered with the premium
     * voice when the entitlement lapses. The premium half plays, the rest needs a decision,
     * and the selected voice is no longer usable.
     */
    @Test
    fun `a half-premium book after a downgrade keeps its audio and needs a new voice`(): Unit =
        runBlocking {
            val store = NarrationTierStore(
                readAccess = { access(active = false, expiresAt = null) },
                loadRecorded = { recordedPremium(confirmedAt = NOW - NarrationTiers.REFRESH_INTERVAL_MILLIS) },
                saveRecorded = {},
                now = { NOW },
            )

            val tier = store.currentTier()
            val selected = SelectedVoice(VoiceKind.PREMIUM, "premium-voice-1")

            assertEquals(NarrationTier.FREE, tier.tier)
            assertTrue(
                "the recorded premium voice should no longer be usable",
                store.fallbackNeeded(selected, tier, localNeuralSupported = true),
            )

            val lapse = NarrationTiers.lapseFor(
                tier.tier,
                premiumRenderedChapters = listOf(0, 1, 2),
                remainingChapters = listOf(3, 4),
            ) as PremiumVoiceLapse.Lapsed
            assertEquals(listOf(0, 1, 2), lapse.keptChapters)
            assertEquals(listOf(3, 4), lapse.chaptersNeedingAnotherVoice)

            // Accepting an on-device voice is allowed and needs no entitlement.
            val fallback = SelectedVoice(VoiceKind.SYSTEM, "en-us-system")
            assertFalse(store.fallbackNeeded(fallback, tier, localNeuralSupported = true))
            assertFalse(NarrationTiers.sendsTextOffDevice(fallback.kind))
        }

    @Test
    fun `an on-device voice needs no fallback when the tier is premium`(): Unit = runBlocking {
        val store = NarrationTierStore(
            readAccess = { access(active = true, expiresAt = null) },
            loadRecorded = { null },
            saveRecorded = {},
            now = { NOW },
        )
        val tier = store.currentTier()
        assertFalse(
            store.fallbackNeeded(
                SelectedVoice(VoiceKind.SYSTEM, "en-us"), tier, localNeuralSupported = false,
            ),
        )
    }

    /** A device that cannot run the neural model must fall back even on the free tier. */
    @Test
    fun `a local neural voice needs a fallback where the model is unsupported`() {
        val tier = NarrationTierState(NarrationTier.FREE, null, NOW, true)
        val store = NarrationTierStore({ access() }, { null }, {}, { NOW })
        assertTrue(
            store.fallbackNeeded(
                SelectedVoice(VoiceKind.LOCAL_NEURAL, "neural-1"), tier,
                localNeuralSupported = false,
            ),
        )
        assertFalse(
            store.fallbackNeeded(
                SelectedVoice(VoiceKind.LOCAL_NEURAL, "neural-1"), tier,
                localNeuralSupported = true,
            ),
        )
    }

    @Test
    fun `no selected voice needs no fallback`() {
        val store = NarrationTierStore({ access() }, { null }, {}, { NOW })
        assertFalse(
            store.fallbackNeeded(
                null, NarrationTierState(NarrationTier.FREE, null, NOW, true), true,
            ),
        )
    }

    // endregion

    // region generators and fixtures

    private fun access(
        active: Boolean = false,
        expiresAt: String? = null,
        plan: String = "free",
    ) = AccountAccessResponse(
        isActive = active,
        plan = plan,
        source = "test",
        expiresAt = expiresAt,
        canUseFilters = true,
        canUseCompanion = false,
    )

    private fun recordedPremium(confirmedAt: Long) =
        NarrationTierState(NarrationTier.PREMIUM, "premium-monthly", confirmedAt, true)

    private companion object {
        /** 2026-08-29T12:34:56Z, so expiry comparisons read against a real date. */
        const val NOW = 1_788_006_896_000L
    }
}
