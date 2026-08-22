package com.audiochoice.mobile.support

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.SupportMessageRequest
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class SupportUiState(
    val submitting: Boolean = false,
    val sent: Boolean = false,
    val error: String? = null,
)

class SupportViewModel(private val api: AudioChoiceApi) : ViewModel() {
    private val mutableState = MutableStateFlow(SupportUiState())
    val state: StateFlow<SupportUiState> = mutableState.asStateFlow()

    fun submit(accessToken: String, subject: String, message: String) {
        if (mutableState.value.submitting) return
        viewModelScope.launch {
            mutableState.value = SupportUiState(submitting = true)
            runCatching {
                api.sendSupportMessage(
                    accessToken,
                    SupportMessageRequest(subject.trim(), message.trim()),
                )
            }.onSuccess {
                mutableState.value = SupportUiState(sent = true)
            }.onFailure { error ->
                mutableState.value = SupportUiState(
                    error = error.message ?: "AudioChoice could not send your message. Please try again.",
                )
            }
        }
    }

    fun reset() {
        mutableState.value = SupportUiState()
    }

    class Factory(private val api: AudioChoiceApi) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T =
            SupportViewModel(api) as T
    }
}
