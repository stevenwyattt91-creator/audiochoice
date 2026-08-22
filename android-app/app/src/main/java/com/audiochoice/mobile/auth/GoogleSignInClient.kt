package com.audiochoice.mobile.auth

import android.content.Context
import androidx.credentials.CustomCredential
import androidx.credentials.GetCredentialRequest
import androidx.credentials.CredentialManager
import com.audiochoice.mobile.BuildConfig
import com.google.android.libraries.identity.googleid.GetSignInWithGoogleOption
import com.google.android.libraries.identity.googleid.GoogleIdTokenCredential

class GoogleSignInClient(private val context: Context) {
    suspend fun requestIdToken(): String {
        val clientId = BuildConfig.GOOGLE_SERVER_CLIENT_ID
        check(clientId.isNotBlank()) { "Google sign-in has not been connected to this build yet." }
        val option = GetSignInWithGoogleOption.Builder(clientId).build()
        val request = GetCredentialRequest.Builder().addCredentialOption(option).build()
        val credential = CredentialManager.create(context).getCredential(context, request).credential
        check(credential is CustomCredential && credential.type == GoogleIdTokenCredential.TYPE_GOOGLE_ID_TOKEN_CREDENTIAL) {
            "Google did not return a usable AudioChoice identity."
        }
        return GoogleIdTokenCredential.createFrom(credential.data).idToken
    }
}
