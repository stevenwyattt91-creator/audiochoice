package com.audiochoice.mobile

import android.os.Bundle
import android.content.Intent
import android.net.Uri
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import com.audiochoice.mobile.auth.AuthViewModel
import com.audiochoice.mobile.auth.GoogleSignInClient
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.SessionStore
import com.audiochoice.mobile.data.LocalAudioStore
import com.audiochoice.mobile.importing.AudioFileInspector
import com.audiochoice.mobile.importing.ActiveScanStore
import com.audiochoice.mobile.importing.ImportViewModel
import com.audiochoice.mobile.importing.LocalAaxConverter
import com.audiochoice.mobile.library.LibraryViewModel
import com.audiochoice.mobile.player.PlayerViewModel
import com.audiochoice.mobile.support.SupportViewModel
import com.audiochoice.mobile.ui.AudioChoiceApp
import com.audiochoice.mobile.ui.theme.AudioChoiceTheme
import kotlinx.serialization.json.Json
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

class MainActivity : ComponentActivity() {
    private val pendingExternalAudioUri = MutableStateFlow<Uri?>(null)
    private val pendingCompanionTransferUri = MutableStateFlow<Uri?>(null)
    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }
    private val api = AudioChoiceApi(json)
    private val localAudio by lazy { LocalAudioStore(applicationContext) }
    private val authViewModel by viewModels<AuthViewModel> {
        AuthViewModel.Factory(
            api,
            SessionStore(applicationContext, json),
            GoogleSignInClient(this),
        )
    }
    private val importViewModel by viewModels<ImportViewModel> {
        ImportViewModel.Factory(
            api,
            AudioFileInspector(applicationContext),
            localAudio,
            LocalAaxConverter(applicationContext),
            ActiveScanStore(applicationContext),
            applicationContext.filesDir,
            applicationContext,
        )
    }
    private val libraryViewModel by viewModels<LibraryViewModel> { LibraryViewModel.Factory(api, localAudio) }
    private val playerViewModel by viewModels<PlayerViewModel> { PlayerViewModel.Factory(this, api, localAudio) }
    private val supportViewModel by viewModels<SupportViewModel> { SupportViewModel.Factory(api) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        acceptExternalAudio(intent)
        enableEdgeToEdge()
        setContent {
            AudioChoiceTheme {
                AudioChoiceApp(
                    authViewModel,
                    importViewModel,
                    libraryViewModel,
                    playerViewModel,
                    supportViewModel,
                    pendingExternalAudioUri.asStateFlow(),
                    pendingCompanionTransferUri.asStateFlow(),
                    onExternalAudioHandled = { pendingExternalAudioUri.value = null },
                    onCompanionTransferHandled = { pendingCompanionTransferUri.value = null },
                )
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        acceptExternalAudio(intent)
    }

    /**
     * Receives a standard Android audio share/open request. This is deliberately
     * independent from the companion relay: once an audiobook file is present
     * on Android, it enters the same verified import pipeline as a file picked
     * by the user. This includes AAX, which is handed to the existing AAX
     * agreement and local-conversion flow.
     */
    private fun acceptExternalAudio(intent: Intent?) {
        if (intent?.action == Intent.ACTION_VIEW && intent.data?.scheme in setOf("audiochoice", "audiochoice-beta") && intent.data?.host == "transfer") {
            pendingCompanionTransferUri.value = intent.data
            return
        }
        val uri = when (intent?.action) {
            Intent.ACTION_VIEW -> intent.data
            @Suppress("DEPRECATION")
            Intent.ACTION_SEND -> intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)
            else -> null
        } ?: return
        if (uri.scheme != "content" && uri.scheme != "file") return
        runCatching {
            contentResolver.takePersistableUriPermission(
                uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION,
            )
        }
        pendingExternalAudioUri.value = uri
    }

    override fun onStop() {
        playerViewModel.saveProgress()
        super.onStop()
    }
}
