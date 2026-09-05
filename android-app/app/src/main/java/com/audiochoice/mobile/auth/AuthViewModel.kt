package com.audiochoice.mobile.auth

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.audiochoice.mobile.BuildConfig
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.AuthResponse
import com.audiochoice.mobile.data.LoginRequest
import com.audiochoice.mobile.data.RegisterRequest
import com.audiochoice.mobile.data.SessionStore
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

data class AuthUiState(
    val loadingSession: Boolean = true,
    val busy: Boolean = false,
    val session: AuthResponse? = null,
    val error: String? = null,
)

class AuthViewModel(
    private val api: AudioChoiceApi,
    private val sessions: SessionStore,
    private val google: GoogleSignInClient,
) : ViewModel() {
    private val mutableState = MutableStateFlow(AuthUiState())
    val state: StateFlow<AuthUiState> = mutableState.asStateFlow()

    init {
        viewModelScope.launch {
            mutableState.value = mutableState.value.copy(
                loadingSession = false,
                session = sessions.session.first(),
            )
        }
        // The server is the authority on session validity. When it rejects the
        // stored token, drop it and return to sign-in rather than leaving the
        // app signed in with a dead session where every request fails.
        viewModelScope.launch {
            api.sessionExpired.collect {
                if (mutableState.value.session == null) return@collect
                sessions.clear()
                mutableState.value = AuthUiState(
                    loadingSession = false,
                    error = "Your AudioChoice session has expired. Please sign in again.",
                )
            }
        }
    }

    fun login(email: String, password: String) = authenticate { api.login(LoginRequest(email.trim(), password)) }

    fun register(email: String, password: String, referralCode: String = "") = authenticate {
        require(password.length >= 12) { "Use at least 12 characters for your password." }
        // No display name is sent. The server treats it as optional and derives one from the
        // address, so sending a blank would store a blank where a derived name is better.
        //
        // An unknown or mistyped referral code is never rejected here -- the server attributes what
        // it can and otherwise silently ignores it, so a typo in an optional field is never the
        // reason an account fails to create.
        api.register(RegisterRequest(email.trim(), password, referralCode = referralCode.trim().ifBlank { null }))
    }

    fun googleSignIn() = authenticate { api.googleSignIn(google.requestIdToken()) }

    /**
     * Asks for a reset code, reporting only whether the request was accepted.
     *
     * Deliberately says nothing about whether the address is registered, matching the server. Telling
     * someone their email is unknown would let anyone discover who has an account here.
     */
    fun requestPasswordReset(email: String, onResult: (String?) -> Unit) {
        viewModelScope.launch {
            mutableState.value = mutableState.value.copy(busy = true, error = null)
            val failure = runCatching { api.requestPasswordReset(email.trim()) }
                .exceptionOrNull()
                ?.let { it.message ?: "That request could not be sent." }
            mutableState.value = mutableState.value.copy(busy = false, error = failure)
            onResult(failure)
        }
    }

    /** Sets a new password from the emailed code. */
    fun confirmPasswordReset(code: String, newPassword: String, onResult: (String?) -> Unit) {
        viewModelScope.launch {
            mutableState.value = mutableState.value.copy(busy = true, error = null)
            val failure = runCatching {
                // Trimmed, because copying a code out of an email very often takes whitespace with
                // it and a code that fails for an invisible reason is the worst kind.
                api.confirmPasswordReset(code.trim(), newPassword)
            }.exceptionOrNull()?.let { it.message ?: "That code was not accepted." }
            mutableState.value = mutableState.value.copy(busy = false, error = failure)
            onResult(failure)
        }
    }

    fun dismissError() {
        mutableState.value = mutableState.value.copy(error = null)
    }

    fun logout() {
        val current = mutableState.value.session ?: return
        viewModelScope.launch {
            mutableState.value = mutableState.value.copy(busy = true, error = null)
            runCatching { api.logout(current.accessToken) }
            sessions.clear()
            mutableState.value = AuthUiState(loadingSession = false)
        }
    }

    private fun authenticate(block: suspend () -> AuthResponse) {
        if (mutableState.value.busy) return
        viewModelScope.launch {
            mutableState.value = mutableState.value.copy(busy = true, error = null)
            runCatching { block() }
                .onSuccess {
                    sessions.save(it)
                    mutableState.value = AuthUiState(loadingSession = false, session = it)
                }
                .onFailure { failure ->
                    // Beta builds append the underlying Credential Manager type so
                    // a sign-in problem can be diagnosed from a tester's report
                    // rather than only from an opaque provider string.
                    val diagnostic = (failure as? GoogleSignInFailure)?.diagnostic
                    mutableState.value = mutableState.value.copy(
                        busy = false,
                        error = buildString {
                            append(failure.message ?: "AudioChoice could not sign you in.")
                            if (BuildConfig.BETA_BUILD && diagnostic != null) {
                                append("\n\n(")
                                append(diagnostic)
                                append(")")
                            }
                        },
                    )
                }
        }
    }

    class Factory(
        private val api: AudioChoiceApi,
        private val sessions: SessionStore,
        private val google: GoogleSignInClient,
    ) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T =
            AuthViewModel(api, sessions, google) as T
    }
}
