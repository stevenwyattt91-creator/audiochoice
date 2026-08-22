package com.audiochoice.mobile.importing

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class AaxRecoveryStore(context: Context) {
    private val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)

    fun checkpoint(checksumKey: String): UInt =
        preferences.getString("checkpoint.$checksumKey", null)?.toUIntOrNull() ?: UInt.MIN_VALUE

    fun saveCheckpoint(checksumKey: String, nextCandidate: UInt) {
        preferences.edit().putString("checkpoint.$checksumKey", nextCandidate.toString()).apply()
    }

    fun clearCheckpoint(checksumKey: String) {
        preferences.edit().remove("checkpoint.$checksumKey").apply()
    }

    fun activation(checksumKey: String): UInt? {
        val encoded = preferences.getString("activation.$checksumKey", null) ?: return null
        return runCatching {
            val packed = Base64.decode(encoded, Base64.NO_WRAP)
            require(packed.size > IV_SIZE)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(
                Cipher.DECRYPT_MODE,
                secretKey(),
                GCMParameterSpec(TAG_BITS, packed.copyOfRange(0, IV_SIZE)),
            )
            val plain = cipher.doFinal(packed.copyOfRange(IV_SIZE, packed.size))
            require(plain.size == UInt.SIZE_BYTES)
            ((plain[0].toUInt() and 0xffu) shl 24) or
                ((plain[1].toUInt() and 0xffu) shl 16) or
                ((plain[2].toUInt() and 0xffu) shl 8) or
                (plain[3].toUInt() and 0xffu)
        }.getOrNull()
    }

    fun saveActivation(checksumKey: String, activation: UInt) {
        val plain = byteArrayOf(
            (activation shr 24).toByte(),
            (activation shr 16).toByte(),
            (activation shr 8).toByte(),
            activation.toByte(),
        )
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, secretKey())
        val packed = cipher.iv + cipher.doFinal(plain)
        preferences.edit()
            .putString("activation.$checksumKey", Base64.encodeToString(packed, Base64.NO_WRAP))
            .remove("checkpoint.$checksumKey")
            .apply()
    }

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .build(),
            )
            generateKey()
        }
    }

    private companion object {
        const val PREFERENCES_NAME = "audiochoice_aax_recovery"
        const val KEY_ALIAS = "audiochoice.aax.activation.v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
        const val TAG_BITS = 128
    }
}
