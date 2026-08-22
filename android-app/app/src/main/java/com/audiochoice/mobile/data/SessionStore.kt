package com.audiochoice.mobile.data

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

private val Context.sessionDataStore by preferencesDataStore("audiochoice_session")

class SessionStore(private val context: Context, private val json: Json) {
    private val sessionKey = stringPreferencesKey("authenticated_session")

    val session: Flow<AuthResponse?> = context.sessionDataStore.data.map { preferences ->
        preferences[sessionKey]?.let { runCatching { json.decodeFromString<AuthResponse>(it) }.getOrNull() }
    }

    suspend fun save(value: AuthResponse) {
        context.sessionDataStore.edit { it[sessionKey] = json.encodeToString(value) }
    }

    suspend fun clear() {
        context.sessionDataStore.edit { it.remove(sessionKey) }
    }
}
