package com.audiochoice.mobile.data

import kotlinx.serialization.Serializable

@Serializable
data class RegisterRequest(val email: String, val password: String, val displayName: String? = null)

@Serializable
data class LoginRequest(val email: String, val password: String)

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
