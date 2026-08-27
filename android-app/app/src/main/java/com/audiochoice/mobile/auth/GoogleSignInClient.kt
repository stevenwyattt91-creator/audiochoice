package com.audiochoice.mobile.auth

import android.content.Context
import androidx.credentials.CustomCredential
import androidx.credentials.GetCredentialRequest
import androidx.credentials.CredentialManager
import androidx.credentials.exceptions.GetCredentialCancellationException
import androidx.credentials.exceptions.GetCredentialException
import androidx.credentials.exceptions.GetCredentialProviderConfigurationException
import androidx.credentials.exceptions.GetCredentialUnsupportedException
import androidx.credentials.exceptions.NoCredentialException
import com.audiochoice.mobile.BuildConfig
import com.google.android.libraries.identity.googleid.GetSignInWithGoogleOption
import com.google.android.libraries.identity.googleid.GoogleIdTokenCredential

/** Raised when Google sign-in cannot complete, with a cause the listener can act on. */
class GoogleSignInFailure(
    message: String,
    /** Retained for diagnostics; Credential Manager messages are often opaque. */
    val diagnostic: String,
    cause: Throwable? = null,
) : Exception(message, cause)

class GoogleSignInClient(private val context: Context) {
    suspend fun requestIdToken(): String {
        val clientId = BuildConfig.GOOGLE_SERVER_CLIENT_ID
        check(clientId.isNotBlank()) { "Google sign-in has not been connected to this build yet." }
        val option = GetSignInWithGoogleOption.Builder(clientId).build()
        val request = GetCredentialRequest.Builder().addCredentialOption(option).build()

        val credential = try {
            CredentialManager.create(context).getCredential(context, request).credential
        } catch (failure: GetCredentialException) {
            throw translate(failure)
        }

        check(credential is CustomCredential && credential.type == GoogleIdTokenCredential.TYPE_GOOGLE_ID_TOKEN_CREDENTIAL) {
            "Google did not return a usable AudioChoice identity."
        }
        return GoogleIdTokenCredential.createFrom(credential.data).idToken
    }

    /**
     * Credential Manager reports several distinct problems through the same
     * opaque strings -- notably "[16] Account reauth failed", which surfaces as a
     * cancellation and can mean either that the device's Google account needs
     * re-authentication or that this build is not authorised for the configured
     * OAuth client. Mapping the exception type separates those cases so a failure
     * is actionable instead of a dead end.
     */
    private fun translate(failure: GetCredentialException): GoogleSignInFailure {
        val diagnostic = "${failure::class.java.simpleName}: ${failure.message ?: "no message"}"
        val message = when (failure) {
            is NoCredentialException ->
                "No Google account is available on this device. Add one in Settings, and make " +
                    "sure Sign in with Google is enabled for it, then try again."

            is GetCredentialProviderConfigurationException ->
                "Google sign-in is not available on this device. Update Google Play services and try again."

            is GetCredentialUnsupportedException ->
                "This device does not support Google sign-in. Use an email address and password instead."

            is GetCredentialCancellationException ->
                // Genuine user cancellation and an unauthorised app both land here.
                if (failure.message?.contains("reauth", ignoreCase = true) == true) {
                    "Google could not verify this account. Re-add your Google account in the " +
                        "device settings, then try again. If it keeps failing, sign in with an " +
                        "email address and password instead."
                } else {
                    "Google sign-in was cancelled."
                }

            else ->
                "Google sign-in could not be completed. You can sign in with an email address " +
                    "and password instead."
        }
        return GoogleSignInFailure(message, diagnostic, failure)
    }
}
