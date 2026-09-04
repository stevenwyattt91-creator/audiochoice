package com.audiochoice.mobile.data

import kotlinx.serialization.Serializable

@Serializable
data class RegisterRequest(
    val email: String,
    val password: String,
    val displayName: String? = null,
    val referralCode: String? = null,
)

@Serializable
data class LoginRequest(val email: String, val password: String)

@Serializable
data class PasswordResetRequest(val email: String)

/**
 * The emailed six-digit code and the password to set.
 *
 * The field is named `token` because that is what the server calls it, and renaming it here would
 * silently stop matching.
 */
@Serializable
data class PasswordResetConfirmRequest(val token: String, val newPassword: String)

/** What the reset endpoints answer with: an acknowledgement, not a session. */
@Serializable
data class AuthActionResponse(val status: String? = null)

@Serializable
data class ExternalLoginRequest(
    val provider: String,
    val authorizationCode: String = "",
    val identityToken: String? = null,
    val displayName: String? = null,
)

@Serializable
data class AuthUser(val id: String, val email: String, val displayName: String, val provider: String)

@Serializable
data class AuthResponse(val accessToken: String, val expiresAt: String, val user: AuthUser)

@Serializable
data class ApiError(val error: String? = null)

@Serializable
data class SupportMessageRequest(val subject: String, val message: String)

@Serializable
data class SupportMessageResponse(val status: String)
