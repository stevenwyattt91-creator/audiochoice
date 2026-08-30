package com.audiochoice.mobile.narration.voice

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PremiumVoiceAgreementTest {

    // region the gate

    /**
     * Entitlement is checked before the agreement.
     *
     * Somebody without a subscription should be told that, rather than walked through an agreement
     * for something they cannot use — and certainly not shown a statement about sending their book
     * off the device when nothing is going to be sent.
     */
    @Test
    fun `no entitlement is reported before anything about agreements`() {
        assertEquals(
            PremiumVoiceGate.NotEntitled,
            PremiumVoiceAgreement.gate(
                isEntitled = false,
                serverVersion = "1",
                serverText = TEXT,
                recorded = null,
            ),
        )
        // Even with a current acceptance on file.
        assertEquals(
            PremiumVoiceGate.NotEntitled,
            PremiumVoiceAgreement.gate(false, "1", TEXT, accepted("1")),
        )
    }

    @Test
    fun `an entitled listener with no acceptance is asked first`() {
        val gate = PremiumVoiceAgreement.gate(true, "1", TEXT, recorded = null)
        val required = gate as PremiumVoiceGate.AgreementRequired
        assertEquals("1", required.version)
        assertEquals(TEXT, required.text)
        assertFalse(
            "nothing may be sent before the listener has agreed",
            PremiumVoiceAgreement.maySubmit(gate),
        )
    }

    @Test
    fun `a current acceptance allows submission`() {
        val gate = PremiumVoiceAgreement.gate(true, "1", TEXT, accepted("1"))
        assertEquals(PremiumVoiceGate.Allowed, gate)
        assertTrue(PremiumVoiceAgreement.maySubmit(gate))
    }

    /**
     * A superseded version is its own state, not "never accepted".
     *
     * The two want different words: somebody who agreed to a different arrangement is owed an
     * explanation of what changed, not a first-time introduction. And nothing may be submitted
     * under the old agreement in the meantime.
     */
    @Test
    fun `a superseded acceptance is distinguished from none`() {
        val gate = PremiumVoiceAgreement.gate(true, "2", TEXT, accepted("1"))
        val changed = gate as PremiumVoiceGate.AgreementChanged
        assertEquals("1", changed.acceptedVersion)
        assertEquals("2", changed.currentVersion)
        assertFalse(
            "text was submitted under a superseded agreement",
            PremiumVoiceAgreement.maySubmit(gate),
        )
    }

    /**
     * If the server has not said which version is in force, nothing new is sent.
     *
     * The wording lives on the server precisely so the server decides what is currently being
     * agreed to. Allowing submission on a stale local record would defeat that.
     */
    @Test
    fun `an unknown server version does not allow a first submission`() {
        val gate = PremiumVoiceAgreement.gate(true, serverVersion = null, serverText = null, recorded = null)
        assertFalse(PremiumVoiceAgreement.maySubmit(gate))
        assertTrue(gate is PremiumVoiceGate.AgreementRequired)

        // But a listener who has already accepted is not blocked by the server being unreachable:
        // that would make premium unusable offline for someone who had already agreed.
        assertTrue(
            PremiumVoiceAgreement.maySubmit(
                PremiumVoiceAgreement.gate(true, null, null, accepted("1")),
            ),
        )
    }

    @Test
    fun `a blank server version is treated as unknown rather than as a version`() {
        val gate = PremiumVoiceAgreement.gate(true, serverVersion = "  ", serverText = TEXT, recorded = null)
        assertTrue(gate is PremiumVoiceGate.AgreementRequired)
    }

    // endregion

    // region offline acceptance and delivery

    /**
     * An acceptance recorded with no signal is usable immediately and kept until confirmed.
     *
     * Dropping it after the first successful call would leave an acceptance that only ever existed
     * on one device — so a reinstall, or a second device, would have no record that the listener
     * ever agreed.
     */
    @Test
    fun `an acceptance is usable immediately and retained until delivered`() {
        val record = PremiumVoiceAgreement.accept("1", TEXT, nowMillis = 1_000)

        assertFalse("a fresh acceptance has not been delivered", record.deliveredToBackend)
        assertTrue(
            "an undelivered acceptance must still allow synthesis",
            PremiumVoiceAgreement.maySubmit(
                PremiumVoiceAgreement.gate(true, "1", TEXT, record),
            ),
        )
        assertTrue(PremiumVoiceAgreement.needsDelivery(record))
        assertFalse(
            PremiumVoiceAgreement.needsDelivery(record.copy(deliveredToBackend = true)),
        )
        assertFalse(PremiumVoiceAgreement.needsDelivery(null))
    }

    /** The wording is stored, not just its version, so it can be produced later. */
    @Test
    fun `the accepted wording is recorded alongside its version`() {
        val record = PremiumVoiceAgreement.accept("3", TEXT, nowMillis = 42)
        assertEquals("3", record.version)
        assertEquals(TEXT, record.text)
        assertEquals(42L, record.acceptedAtMillis)
    }

    // endregion

    // region what a lapsed or changed agreement does not do

    /**
     * A changed agreement stops new submissions and nothing else.
     *
     * Audio already made stays playable. Withdrawing a chapter somebody already has, because the
     * wording of a statement changed, would be taking back something already delivered.
     */
    @Test
    fun `a changed agreement blocks submission without implying anything about existing audio`() {
        val gate = PremiumVoiceAgreement.gate(true, "2", TEXT, accepted("1"))
        assertFalse(PremiumVoiceAgreement.maySubmit(gate))
        // The gate says nothing about deletion, and there is deliberately no state for it to say
        // it with: the only question it answers is whether more may be sent.
        assertTrue(gate is PremiumVoiceGate.AgreementChanged)
    }

    /** Every gate state answers the one question the render path asks. */
    @Test
    fun `only an allowed gate permits submission`() {
        val states = listOf(
            PremiumVoiceGate.Allowed,
            PremiumVoiceGate.NotEntitled,
            PremiumVoiceGate.AgreementRequired("1", TEXT),
            PremiumVoiceGate.AgreementChanged("1", "2", TEXT),
        )
        assertEquals(
            listOf(true, false, false, false),
            states.map(PremiumVoiceAgreement::maySubmit),
        )
    }

    // endregion

    /**
     * The render path must consult the gate, not the selected voice alone.
     *
     * Checked against the source because the bypass is a one-line simplification -- dropping the
     * `maySubmit` conjunction reads like removing a redundant check -- and it would send a
     * listener's book off the device with no agreement recorded. Nothing else in the suite catches
     * it, which was verified by making that exact edit.
     */
    @Test
    fun `the render path gates premium on the agreement`() {
        val source = listOf(
            java.io.File(VIEW_MODEL),
            java.io.File("app/$VIEW_MODEL"),
            java.io.File("../app/$VIEW_MODEL"),
        ).firstOrNull(java.io.File::isFile)
        assertTrue(
            "could not locate the view model; this guard would pass without checking anything",
            source != null,
        )
        val text = source!!.readText()
        assertTrue(
            "the render path selects the premium engine without checking the agreement gate, " +
                "so a book's text could be sent with no acceptance recorded",
            text.contains("PremiumVoiceAgreement.maySubmit("),
        )
        // And the selection itself must be conditional on it.
        val selection = text.substringAfter("val usePremium =").substringBefore("val engine =")
        assertTrue(
            "usePremium no longer depends on the gate: $selection",
            selection.contains("maySubmit"),
        )
    }

    private fun accepted(version: String) = PremiumAgreementRecord(
        version = version,
        text = TEXT,
        acceptedAtMillis = 1_000,
        deliveredToBackend = true,
    )

    private companion object {
        const val VIEW_MODEL =
            "src/main/java/com/audiochoice/mobile/narration/NarrationViewModel.kt"
        const val TEXT = "Each chapter's text is sent to AudioChoice to be turned into audio."
    }
}
