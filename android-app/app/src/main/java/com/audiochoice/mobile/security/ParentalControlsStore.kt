package com.audiochoice.mobile.security

import android.content.Context
import android.util.Base64
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec

class ParentalControlsStore(context: Context, userID: String) {
    private val preferences = context.getSharedPreferences("audiochoice_parental_controls", Context.MODE_PRIVATE)
    private val prefix = "${userID}_"

    val enabled: Boolean get() = preferences.getBoolean(prefix + "enabled", false)
    val configured: Boolean get() = preferences.contains(prefix + "hash")

    fun configure(pin: String) {
        require(pin.matches(Regex("\\d{4,6}")))
        val salt = ByteArray(24).also(SecureRandom()::nextBytes)
        preferences.edit()
            .putString(prefix + "salt", Base64.encodeToString(salt, Base64.NO_WRAP))
            .putString(prefix + "hash", hash(pin, salt))
            .putBoolean(prefix + "enabled", true)
            .apply()
    }

    fun disable(pin: String): Boolean = if (verify(pin)) {
        preferences.edit().putBoolean(prefix + "enabled", false).apply()
        true
    } else false

    fun enable(pin: String): Boolean = if (verify(pin)) {
        preferences.edit().putBoolean(prefix + "enabled", true).apply()
        true
    } else false

    fun changePin(currentPin: String, newPin: String): Boolean {
        if (!verify(currentPin)) return false
        configure(newPin)
        return true
    }

    fun validate(pin: String): Boolean = verify(pin)

    private fun verify(pin: String): Boolean {
        val salt = preferences.getString(prefix + "salt", null)?.let {
            runCatching { Base64.decode(it, Base64.NO_WRAP) }.getOrNull()
        } ?: return false
        val expected = preferences.getString(prefix + "hash", null) ?: return false
        return MessageDigest.isEqual(expected.toByteArray(), hash(pin, salt).toByteArray())
    }

    private fun hash(pin: String, salt: ByteArray): String {
        val specification = PBEKeySpec(pin.toCharArray(), salt, 120_000, 256)
        return try {
            Base64.encodeToString(
                SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(specification).encoded,
                Base64.NO_WRAP,
            )
        } finally {
            specification.clearPassword()
        }
    }
}
