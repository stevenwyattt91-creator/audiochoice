package com.audiochoice.mobile.narration

import com.audiochoice.mobile.data.NarrationFlags
import com.audiochoice.mobile.data.RenderQueue
import com.audiochoice.mobile.data.RenderState
import com.audiochoice.mobile.data.VoiceKind
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class NarrationUiStateTest {

    // region rendering may not begin before filters are settled

    /**
     * The rule that protects the promise. Audio, once written, is what the listener hears until
     * it is re-rendered, so synthesising before filter results are known would speak passages
     * they asked to have removed -- and the removal would arrive too late to matter.
     */
    @Test
    fun `rendering waits for filter results`() {
        val awaiting = NarrationUiState(readiness = NarrationReadiness.AWAITING_FILTERS)
        assertFalse("a book with no filter results was allowed to render", awaiting.mayRender)

        assertTrue(NarrationUiState(readiness = NarrationReadiness.READY).mayRender)
    }

    /**
     * The listener can override it, and that override is recorded rather than transient: the
     * consequence -- audio that was never filtered -- outlives the session it was chosen in.
     */
    @Test
    fun `continuing without filter results unblocks rendering`() {
        val continued = NarrationUiState(
            readiness = NarrationReadiness.AWAITING_FILTERS,
            flags = NarrationFlags(continuedWithoutFilterResults = true),
        )
        assertTrue(continued.mayRender)
    }

    /** An unreadable book cannot render however the flags are set: there is no text to speak. */
    @Test
    fun `an unreadable book never renders`() {
        listOf(NarrationFlags(), NarrationFlags(continuedWithoutFilterResults = true)).forEach { flags ->
            assertFalse(
                NarrationUiState(readiness = NarrationReadiness.UNREADABLE, flags = flags).mayRender,
            )
        }
        assertFalse(
            NarrationUiState(readiness = NarrationReadiness.LOADING).mayRender,
        )
    }

    // endregion

    // region progress counts

    @Test
    fun `chapter counts come from the queue and the plan`() {
        val state = NarrationUiState(
            queue = RenderQueue(
                states = listOf(
                    RenderState.RENDERED, RenderState.RENDERED,
                    RenderState.RENDER_FAILED, RenderState.NOT_RENDERED,
                ),
            ),
        )
        assertEquals(2, state.renderedChapters)
        assertEquals(1, state.failedChapters)
    }

    /**
     * "Fully rendered" must be false for a book with no plan, not true by the vacuous reading
     * that zero of zero chapters are done. That value drives whether a duration is presented as
     * the book's real length.
     */
    @Test
    fun `a book with no plan is not fully rendered`() {
        assertFalse(NarrationUiState().isFullyRendered)
        assertEquals(0, NarrationUiState().totalChapters)
    }

    // endregion

    // region voice availability

    /**
     * The reader offers what the tier allows. An unread entitlement is treated as free rather
     * than as premium, so a device that has never reached the server cannot mint premium
     * synthesis for itself.
     */
    @Test
    fun `an unknown tier offers only the on-device voices`() {
        val kinds = NarrationUiState(tier = null).availableVoiceKinds(localNeuralSupported = true)
        assertEquals(listOf(VoiceKind.SYSTEM, VoiceKind.LOCAL_NEURAL), kinds)
        assertFalse(kinds.contains(VoiceKind.PREMIUM))
    }

    @Test
    fun `a premium tier adds the premium voice`() {
        val kinds = NarrationUiState(
            tier = NarrationTierState(NarrationTier.PREMIUM, "monthly", 1L, true),
        ).availableVoiceKinds(localNeuralSupported = true)
        assertTrue(kinds.contains(VoiceKind.PREMIUM))
    }

    /** A free tier is offered both on-device voices, neither of which sends text anywhere. */
    @Test
    fun `every voice a free tier is offered keeps the text on the device`() {
        NarrationUiState(tier = NarrationTierState(NarrationTier.FREE, null, 1L, true))
            .availableVoiceKinds(localNeuralSupported = true)
            .forEach { kind ->
                assertFalse(
                    "$kind is offered on the free tier but sends text off the device",
                    NarrationTiers.sendsTextOffDevice(kind),
                )
            }
    }

    // endregion

    // region the premium gate

    /**
     * The gate the render path consults before any text leaves the device.
     *
     * Derived from the tier and the agreement rather than stored, so it cannot go stale against
     * either. Asserted here because a stored copy is the obvious refactor and would reintroduce
     * exactly the drift this avoids.
     */
    @Test
    fun `premium is withheld without an entitlement whatever the agreement says`() {
        val state = NarrationUiState(
            tier = NarrationTierState(NarrationTier.FREE, null, 1L, true),
            agreementVersion = "1",
            agreementText = "statement",
            agreementRecord = com.audiochoice.mobile.narration.voice.PremiumAgreementRecord(
                version = "1", text = "statement", acceptedAtMillis = 1L, deliveredToBackend = true,
            ),
        )
        assertFalse(
            com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement.maySubmit(state.premiumGate),
        )
    }

    @Test
    fun `premium is withheld with an entitlement but no acceptance`() {
        val state = NarrationUiState(
            tier = NarrationTierState(NarrationTier.PREMIUM, "monthly", 1L, true),
            agreementVersion = "1",
            agreementText = "statement",
            agreementRecord = null,
        )
        assertFalse(
            "text was submittable before the listener agreed",
            com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement.maySubmit(state.premiumGate),
        )
    }

    @Test
    fun `premium is allowed with both an entitlement and a current acceptance`() {
        val state = NarrationUiState(
            tier = NarrationTierState(NarrationTier.PREMIUM, "monthly", 1L, true),
            agreementVersion = "1",
            agreementText = "statement",
            agreementRecord = com.audiochoice.mobile.narration.voice.PremiumAgreementRecord(
                version = "1", text = "statement", acceptedAtMillis = 1L, deliveredToBackend = true,
            ),
        )
        assertTrue(
            com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement.maySubmit(state.premiumGate),
        )
    }

    /** A default state must never permit submission: nothing has been checked yet. */
    @Test
    fun `a freshly opened book cannot submit anything`() {
        assertFalse(
            com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement.maySubmit(
                NarrationUiState().premiumGate,
            ),
        )
    }

    // endregion

    // region the state is separate from the player's

    /**
     * The two surfaces share the reader's rendering code and nothing else. Folding a narrated
     * book into `PlayerUiState` would have meant a nullable field for every audiobook concept and
     * a live one for every narration concept, on a class the shipping player reads in dozens of
     * places.
     *
     * Asserted structurally: this state must carry no audiobook-only concepts.
     */
    @Test
    fun `the narration state carries no audiobook concepts`() {
        val fields = NarrationUiState::class.java.declaredFields.map { it.name }
        listOf("chapters", "bookmarks", "durationMs", "speed", "sleepSecondsRemaining", "localUri")
            .forEach { audiobookOnly ->
                assertFalse(
                    "NarrationUiState gained '$audiobookOnly', which belongs to the player",
                    fields.contains(audiobookOnly),
                )
            }
        // And it must carry the narration ones.
        listOf("plan", "queue", "selectedVoice", "readiness").forEach { required ->
            assertTrue(
                "NarrationUiState is missing '$required'",
                fields.contains(required),
            )
        }
    }

    // endregion
}
