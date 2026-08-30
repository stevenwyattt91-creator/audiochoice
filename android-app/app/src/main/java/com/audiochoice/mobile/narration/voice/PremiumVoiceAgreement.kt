package com.audiochoice.mobile.narration.voice

import kotlinx.serialization.Serializable

/**
 * The listener's acceptance of premium synthesis, recorded on the device.
 *
 * Stores the statement text, not only its version, so what somebody agreed to can be produced
 * later. A version alone is only meaningful while that wording is still in the build, which is
 * exactly when nobody needs to look it up.
 *
 * [deliveredToBackend] is what makes the offline path honest rather than a shortcut: the record is
 * sufficient to start synthesising immediately, and it is retained until the server confirms it has
 * been stored. Dropping it on the first successful call would leave an acceptance that only ever
 * existed on one device.
 */
@Serializable
data class PremiumAgreementRecord(
    val version: String,
    val text: String,
    val acceptedAtMillis: Long,
    val deliveredToBackend: Boolean = false,
)

/** Whether the premium voice may be used, and what to do if not. */
sealed interface PremiumVoiceGate {

    /** Accepted, current, and usable. */
    data object Allowed : PremiumVoiceGate

    /**
     * The statement has to be shown and accepted first.
     *
     * Carries the text to show, which comes from the server so a wording change reaches every
     * client at once rather than waiting for an app update.
     */
    data class AgreementRequired(val version: String, val text: String) : PremiumVoiceGate

    /**
     * Accepted, but an older version than the one now in force.
     *
     * Distinct from never having accepted, because the two want different words: somebody who
     * agreed to a different arrangement is owed an explanation of what changed, not a first-time
     * introduction. Audio already made stays playable either way.
     */
    data class AgreementChanged(
        val acceptedVersion: String,
        val currentVersion: String,
        val text: String,
    ) : PremiumVoiceGate

    /** The account has no active premium entitlement. */
    data object NotEntitled : PremiumVoiceGate
}

/**
 * Decides whether premium synthesis may proceed.
 *
 * Pure, because this is the gate that decides whether a listener's book leaves their device, and a
 * decision of that weight should be inspectable without a device or a network.
 */
object PremiumVoiceAgreement {

    /**
     * The gate, given what the server says and what the device has recorded.
     *
     * Entitlement is checked first and separately: somebody without a subscription should be told
     * that, rather than being walked through an agreement for something they cannot use.
     *
     * A null [serverVersion] means the server has not said what it requires. The premium voice is
     * withheld in that case rather than allowed on a stale local record, because the whole point of
     * taking the wording from the server is that the server decides what is currently being agreed
     * to.
     */
    fun gate(
        isEntitled: Boolean,
        serverVersion: String?,
        serverText: String?,
        recorded: PremiumAgreementRecord?,
    ): PremiumVoiceGate {
        if (!isEntitled) return PremiumVoiceGate.NotEntitled

        if (serverVersion.isNullOrBlank()) {
            // Nothing to agree to yet, so nothing may be sent. An offline listener who has already
            // accepted keeps working through the recorded path below only once the server has said
            // which version is in force; until then the on-device voices are unaffected.
            return recorded?.let { PremiumVoiceGate.Allowed } ?: PremiumVoiceGate.AgreementRequired(
                version = "",
                text = "",
            )
        }

        val text = serverText.orEmpty()
        if (recorded == null) {
            return PremiumVoiceGate.AgreementRequired(serverVersion, text)
        }
        if (recorded.version != serverVersion) {
            return PremiumVoiceGate.AgreementChanged(recorded.version, serverVersion, text)
        }
        return PremiumVoiceGate.Allowed
    }

    /**
     * Whether an acceptance still needs delivering to the server.
     *
     * Retained until confirmed, so an acceptance recorded with no signal is not lost. Delivery is
     * idempotent on the version at the far end, so re-sending costs nothing and cannot create a
     * second record or move the first one's timestamp.
     */
    fun needsDelivery(recorded: PremiumAgreementRecord?): Boolean =
        recorded != null && !recorded.deliveredToBackend

    /** Records an acceptance, undelivered until the server says otherwise. */
    fun accept(version: String, text: String, nowMillis: Long) = PremiumAgreementRecord(
        version = version,
        text = text,
        acceptedAtMillis = nowMillis,
        deliveredToBackend = false,
    )

    /**
     * Whether any text may be submitted right now.
     *
     * The single question the render path asks. Written as its own function rather than left to
     * each call site comparing gate types, because "may I send this book's text" is exactly the
     * check that must not be reimplemented in two places and end up disagreeing.
     */
    fun maySubmit(gate: PremiumVoiceGate): Boolean = gate is PremiumVoiceGate.Allowed
}
