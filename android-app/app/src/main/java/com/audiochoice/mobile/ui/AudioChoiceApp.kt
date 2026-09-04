package com.audiochoice.mobile.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.net.Uri
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.BackHandler
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import com.audiochoice.mobile.auth.AuthViewModel
import com.audiochoice.mobile.R
import com.audiochoice.mobile.BuildConfig
import com.audiochoice.mobile.beta.BetaConfig
import com.audiochoice.mobile.beta.BetaDiagnostics
import com.audiochoice.contracts.FaqEntry
import com.audiochoice.contracts.FaqResponse
import com.audiochoice.contracts.FaqSection
import com.audiochoice.contracts.ReferralCodeCheck
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.AuthUser
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.ExploreCatalogBook
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.importing.ImportPhase
import com.audiochoice.mobile.importing.ImportViewModel
import com.audiochoice.mobile.library.LibraryShelf
import com.audiochoice.mobile.narration.NarrationConfig
import com.audiochoice.mobile.narration.voice.RateMeasurementOutcome
import com.audiochoice.mobile.narration.voice.OnDeviceRateMeasurement
import com.audiochoice.mobile.narration.voice.OnDeviceRate
import com.audiochoice.mobile.narration.availableVoiceKinds
import com.audiochoice.mobile.narration.NarrationViewModel
import com.audiochoice.mobile.narration.NarrationUiState
import com.audiochoice.mobile.narration.NarrationTiers
import com.audiochoice.mobile.narration.NarrationReaderState
import com.audiochoice.mobile.narration.NarrationReadiness
import com.audiochoice.mobile.narration.DiscardEstimate
import com.audiochoice.mobile.narration.FilterChangeImpact
import com.audiochoice.mobile.narration.FilteredRanges
import com.audiochoice.mobile.narration.NarrationStorage
import com.audiochoice.mobile.narration.RerenderImpact
import com.audiochoice.mobile.narration.RuleRejection
import com.audiochoice.mobile.narration.RuleScope
import com.audiochoice.mobile.data.VoiceKind
import com.audiochoice.mobile.data.SelectedVoice
import com.audiochoice.mobile.narration.NarrationImportCoordinator
import com.audiochoice.mobile.library.LibraryShelves
import com.audiochoice.mobile.library.LibraryViewModel
import com.audiochoice.mobile.player.PlayerViewModel
import com.audiochoice.mobile.player.FilterAvailability
import com.audiochoice.mobile.player.ListeningTime
import com.audiochoice.mobile.player.PlayerUiState
import com.audiochoice.mobile.player.enabledScanEvents
import com.audiochoice.mobile.reader.ReaderMask
import com.audiochoice.mobile.reader.ReaderFont
import com.audiochoice.mobile.reader.ReaderSettings
import com.audiochoice.mobile.reader.ReaderTheme
import com.audiochoice.mobile.reader.indexOfCharacter
import com.audiochoice.mobile.reader.merged
import com.audiochoice.mobile.reader.approximateReaderCharacter
import com.audiochoice.mobile.reader.readerCharacterForTime
import com.audiochoice.mobile.reader.readerDisplayParagraphs
import com.audiochoice.mobile.reader.readerTimeForCharacter
import com.audiochoice.mobile.player.PlaybackFilterTaxonomy
import com.audiochoice.mobile.security.ParentalControlsStore
import com.audiochoice.mobile.support.SupportViewModel
import com.audiochoice.mobile.ui.theme.readerFontFamily
import com.audiochoice.mobile.ui.theme.ChoiceGreen
import com.audiochoice.mobile.ui.theme.ChoiceMuted
import com.audiochoice.mobile.ui.theme.ChoiceOutline
import com.audiochoice.mobile.ui.theme.ChoiceSurface

@Composable
fun AudioChoiceApp(
    auth: AuthViewModel,
    /** Passed for the help content, which is fetched rather than compiled in. */
    api: AudioChoiceApi,
    importer: ImportViewModel,
    library: LibraryViewModel,
    player: PlayerViewModel,
    support: SupportViewModel,
    narration: NarrationViewModel,
    incomingAudioUri: StateFlow<Uri?>,
    incomingCompanionTransferUri: StateFlow<Uri?>,
    onExternalAudioHandled: () -> Unit,
    onCompanionTransferHandled: () -> Unit,
) {
    val state by auth.state.collectAsStateWithLifecycle()
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        when {
            state.loadingSession -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = ChoiceGreen)
            }
            state.session == null -> AuthScreen(
                busy = state.busy,
                error = state.error,
                onLogin = auth::login,
                onRegister = auth::register,
                onGoogle = auth::googleSignIn,
                onDismissError = auth::dismissError,
                onRequestReset = auth::requestPasswordReset,
                onConfirmReset = auth::confirmPasswordReset,
                onCheckReferralCode = api::checkReferralCode,
            )
            else -> LibraryShell(
                api = api,
                state.session!!.user,
                state.session!!.accessToken,
                importer,
                library,
                player,
                support,
                narration,
                auth::logout,
                incomingAudioUri,
                incomingCompanionTransferUri,
                onExternalAudioHandled,
                onCompanionTransferHandled,
            )
        }
    }
}

@Composable
private fun AuthScreen(
    busy: Boolean,
    error: String?,
    onLogin: (String, String) -> Unit,
    onRegister: (String, String, String) -> Unit,
    onGoogle: () -> Unit,
    onDismissError: () -> Unit,
    /** Asks for a reset code; reports a failure message, or null when accepted. */
    onRequestReset: (String, (String?) -> Unit) -> Unit,
    onConfirmReset: (String, String, (String?) -> Unit) -> Unit,
    onCheckReferralCode: suspend (String) -> ReferralCodeCheck,
) {
    var registering by rememberSaveable { mutableStateOf(false) }
    var email by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }
    /**
     * Typed a second time when creating an account, and compared before anything is sent.
     *
     * A mistyped password at sign-up is accepted, and then locks someone out of an account they only
     * just made, with the password they meant to use.
     */
    var confirmPassword by rememberSaveable { mutableStateOf("") }
    var resetting by rememberSaveable { mutableStateOf(false) }
    /** Optional. Never blocks account creation -- see the debounced check below. */
    var referralCode by rememberSaveable { mutableStateOf("") }
    var referralCodeValid by rememberSaveable { mutableStateOf<Boolean?>(null) }

    // Debounced rather than checked on every keystroke, and never blocks the Create account button
    // either way: this is purely informational, since the server itself never rejects a signup over
    // an unknown code.
    LaunchedEffect(referralCode) {
        if (referralCode.isBlank()) { referralCodeValid = null; return@LaunchedEffect }
        delay(500)
        referralCodeValid = runCatching { onCheckReferralCode(referralCode.trim()).valid }.getOrNull()
    }

    if (resetting) {
        PasswordResetDialog(
            busy = busy,
            initialEmail = email,
            onRequestReset = onRequestReset,
            onConfirmReset = onConfirmReset,
            onDone = { restoredEmail ->
                // Carried back so the address is not typed twice, and the password field is left
                // empty rather than holding the one that did not work.
                email = restoredEmail
                password = ""
                confirmPassword = ""
                resetting = false
                onDismissError()
            },
            onDismiss = { resetting = false; onDismissError() },
        )
    }

    Column(
        Modifier.fillMaxSize().navigationBarsPadding().imePadding().padding(horizontal = 28.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.weight(.7f))
        Image(
            painter = painterResource(R.drawable.audiochoice_logo),
            contentDescription = "AudioChoice logo",
            modifier = Modifier.size(112.dp),
            contentScale = ContentScale.Fit,
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Audio", fontSize = 32.sp, fontWeight = FontWeight.SemiBold)
            Text("Choice", color = ChoiceGreen, fontSize = 32.sp, fontWeight = FontWeight.SemiBold)
        }
        Text("Listen Your Way", color = ChoiceMuted, fontSize = 14.sp)
        Spacer(Modifier.height(40.dp))

        AudioField(email, { email = it }, "Email address")
        AudioField(password, { password = it }, "Password", password = true)
        if (registering) {
            AudioField(confirmPassword, { confirmPassword = it }, "Confirm password", password = true)
            // Said as soon as they diverge rather than on submit, so the fields are not retyped.
            if (confirmPassword.isNotEmpty() && confirmPassword != password) {
                Text(
                    "Those passwords do not match.",
                    color = MaterialTheme.colorScheme.error,
                    fontSize = 12.sp,
                    modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp),
                )
            }
            Text(
                "Use at least 12 characters.",
                color = ChoiceMuted,
                fontSize = 12.sp,
                modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp),
            )
            AudioField(referralCode, { referralCode = it }, "Referral code (optional)")
            if (referralCode.isNotBlank() && referralCodeValid != null) {
                Text(
                    if (referralCodeValid == true) "Referral code accepted." else "That code was not recognized, but you can still create your account.",
                    color = if (referralCodeValid == true) ChoiceGreen else ChoiceMuted,
                    fontSize = 12.sp,
                    modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp),
                )
            }
        }

        Button(
            onClick = {
                if (registering) onRegister(email, password, referralCode) else onLogin(email, password)
            },
            // The confirmation and the minimum length are checked here as well as by the server, so
            // the button refuses what the server would refuse rather than spending a round trip.
            enabled = !busy && email.isNotBlank() && password.isNotBlank() &&
                (!registering || (password == confirmPassword && password.length >= 12)),
            modifier = Modifier.fillMaxWidth().height(54.dp),
            shape = RoundedCornerShape(12.dp),
        ) {
            if (busy) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
            else Text(if (registering) "Create account" else "Sign in", fontWeight = FontWeight.Bold)
        }
        Spacer(Modifier.height(18.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            HorizontalDivider(Modifier.weight(1f), color = ChoiceOutline)
            Text("  or  ", color = ChoiceMuted)
            HorizontalDivider(Modifier.weight(1f), color = ChoiceOutline)
        }
        Spacer(Modifier.height(18.dp))
        OutlinedButton(
            onClick = onGoogle,
            enabled = !busy,
            modifier = Modifier.fillMaxWidth().height(54.dp),
            shape = RoundedCornerShape(12.dp),
        ) { Text("G", fontWeight = FontWeight.Black); Spacer(Modifier.width(12.dp)); Text("Continue with Google") }
        TextButton(onClick = {
            registering = !registering
            confirmPassword = ""
            onDismissError()
        }, enabled = !busy) {
            Text(if (registering) "Already have an account? Sign in" else "New to AudioChoice? Create an account")
        }
        // Offered on the sign-in path only: someone creating an account has no password to recover.
        if (!registering) {
            TextButton(onClick = { resetting = true; onDismissError() }, enabled = !busy) {
                Text("Forgot password?", color = ChoiceMuted, fontSize = 13.sp)
            }
        }
        error?.let {
            Text(it, color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center, fontSize = 13.sp)
        }
        Spacer(Modifier.weight(1f))
        Text("Private by design • Your audio remains yours", color = ChoiceMuted, fontSize = 12.sp)
        Spacer(Modifier.height(20.dp))
    }
}

/**
 * Recovers an account whose password no longer works.
 *
 * Without this a listener who could not sign in had no route back to their library: their only option
 * was a second account on another address, which abandons the books in the first.
 *
 * The code is typed rather than followed as a link. The account being recovered is only reachable in
 * the app, so sending someone to a browser adds a page to land on and a hand-off to come back from,
 * and both can fail.
 */
@Composable
private fun PasswordResetDialog(
    busy: Boolean,
    initialEmail: String,
    onRequestReset: (String, (String?) -> Unit) -> Unit,
    onConfirmReset: (String, String, (String?) -> Unit) -> Unit,
    onDone: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    var email by rememberSaveable { mutableStateOf(initialEmail) }
    var code by rememberSaveable { mutableStateOf("") }
    var newPassword by rememberSaveable { mutableStateOf("") }
    /**
     * A mistyped password here is worse than at sign-up: the reset succeeds, the old password stops
     * working, and the listener is locked out again by the very thing they used to get back in -- with
     * a code that has now been spent.
     */
    var confirmPassword by rememberSaveable { mutableStateOf("") }
    /** Advanced only once the request is accepted, so nobody is asked for a code before one is sent. */
    var codeSent by rememberSaveable { mutableStateOf(false) }
    var notice by rememberSaveable { mutableStateOf<String?>(null) }
    var failure by rememberSaveable { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Outlined.LockReset, null, tint = ChoiceGreen) },
        title = { Text(if (codeSent) "Enter your code" else "Reset password") },
        text = {
            Column {
                if (!codeSent) {
                    Text(
                        "Enter the email address on your account. We'll send you a 6-digit code.",
                        color = ChoiceMuted,
                        fontSize = 13.sp,
                    )
                    Spacer(Modifier.height(12.dp))
                    AudioField(email, { email = it; failure = null }, "Email address")
                } else {
                    Text(
                        "Enter the 6-digit code from the email, then choose a new password.",
                        color = ChoiceMuted,
                        fontSize = 13.sp,
                    )
                    Spacer(Modifier.height(12.dp))
                    OutlinedTextField(
                        value = code,
                        // Digits only, so a pasted code carrying stray characters cannot be
                        // submitted and spend an attempt for an invisible reason.
                        onValueChange = { entered ->
                            code = entered.filter(Char::isDigit).take(RESET_CODE_LENGTH)
                            failure = null
                        },
                        label = { Text("6-digit code") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                        modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),
                        shape = RoundedCornerShape(12.dp),
                    )
                    AudioField(newPassword, { newPassword = it; failure = null }, "New password", password = true)
                    AudioField(
                        confirmPassword,
                        { confirmPassword = it; failure = null },
                        "Confirm new password",
                        password = true,
                    )
                    if (confirmPassword.isNotEmpty() && confirmPassword != newPassword) {
                        Text(
                            "Those passwords do not match.",
                            color = MaterialTheme.colorScheme.error,
                            fontSize = 12.sp,
                        )
                    }
                    Text("Use at least 12 characters.", color = ChoiceMuted, fontSize = 12.sp)
                }
                notice?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = ChoiceMuted, fontSize = 12.sp)
                }
                failure?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, fontSize = 12.sp)
                }
            }
        },
        confirmButton = {
            if (!codeSent) {
                TextButton(
                    onClick = {
                        onRequestReset(email) { error ->
                            failure = error
                            if (error == null) {
                                codeSent = true
                                // Worded without confirming the address has an account, matching the
                                // server. Saying an address is unknown would let anyone discover who
                                // is registered here.
                                notice = "If that address has an account, a 6-digit code is on its " +
                                    "way. It can take a minute or two, and it expires in 15 minutes."
                            }
                        }
                    },
                    enabled = !busy && email.isNotBlank(),
                ) { Text("Send code") }
            } else {
                TextButton(
                    onClick = {
                        onConfirmReset(code, newPassword) { error ->
                            failure = error
                            if (error == null) onDone(email.trim())
                        }
                    },
                    enabled = !busy &&
                        code.length == RESET_CODE_LENGTH &&
                        newPassword.length >= 12 &&
                        newPassword == confirmPassword,
                ) { Text("Set new password") }
            }
        },
        dismissButton = {
            if (codeSent) {
                TextButton(
                    onClick = {
                        codeSent = false
                        code = ""
                        newPassword = ""
                        confirmPassword = ""
                        notice = null
                        failure = null
                    },
                    enabled = !busy,
                ) { Text("Send another") }
            } else {
                TextButton(onClick = onDismiss, enabled = !busy) { Text("Cancel") }
            }
        },
    )
}

/** Matches the server's code format. */
private const val RESET_CODE_LENGTH = 6

@Composable
private fun AudioField(value: String, onValueChange: (String) -> Unit, label: String, password: Boolean = false) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label) },
        singleLine = true,
        visualTransformation = if (password) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
        modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),
        shape = RoundedCornerShape(12.dp),
    )
}

@Composable
private fun LibraryShell(
    api: AudioChoiceApi,
    user: AuthUser,
    accessToken: String,
    importer: ImportViewModel,
    library: LibraryViewModel,
    player: PlayerViewModel,
    support: SupportViewModel,
    narration: NarrationViewModel,
    onLogout: () -> Unit,
    incomingAudioUri: StateFlow<Uri?>,
    incomingCompanionTransferUri: StateFlow<Uri?>,
    onExternalAudioHandled: () -> Unit,
    onCompanionTransferHandled: () -> Unit,
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    var selected by rememberSaveable { mutableIntStateOf(0) }
    var detailBook by remember { mutableStateOf<LibraryBook?>(null) }
    // A narrated book opens the reader, not the player. Held separately from detailBook so the
    // audiobook details path is untouched.
    var readerBook by remember { mutableStateOf<LibraryBook?>(null) }
    var showingBookFilters by remember { mutableStateOf(false) }
    var profilePage by rememberSaveable { mutableStateOf(ProfilePage.MAIN) }
    var librarySection by rememberSaveable { mutableStateOf(LibrarySection.MY_LIBRARY) }
    var searchExpanded by rememberSaveable { mutableStateOf(false) }
    var searchQuery by rememberSaveable { mutableStateOf("") }
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val tabs = listOf("Library", "Player", "Import", "Profile")
    val tabIcons = listOf(Icons.Outlined.LibraryBooks, Icons.Outlined.GraphicEq, Icons.Outlined.FileDownload, Icons.Outlined.PersonOutline)
    // Read once, not on every recomposition: it is a disk read, and the live player state is
    // authoritative the moment a book is open. This only has to cover the gap before that.
    val rememberedLastBookID = remember { player.lastOpenBookID() }
    val libraryState by library.state.collectAsStateWithLifecycle()
    val importState by importer.state.collectAsStateWithLifecycle()
    val playerState by player.state.collectAsStateWithLifecycle()
    val externalAudioUri by incomingAudioUri.collectAsStateWithLifecycle()
    val companionTransferUri by incomingCompanionTransferUri.collectAsStateWithLifecycle()
    val parentalControls = remember(user.id) { ParentalControlsStore(context, user.id) }
    var filtersLocked by remember(user.id) { mutableStateOf(parentalControls.enabled) }
    val onboardingPreferences = remember(user.id) {
        context.getSharedPreferences("audiochoice_onboarding", android.content.Context.MODE_PRIVATE)
    }
    val onboardingKey = "completed_${user.id}"
    var showOnboarding by rememberSaveable(user.id) {
        mutableStateOf(!onboardingPreferences.getBoolean(onboardingKey, false))
    }
    // The playback notification and lock-screen controls need POST_NOTIFICATIONS
    // on API 33+. Playback still works if the listener declines, so a denial is
    // deliberately not treated as an error.
    val notificationPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { }
    LaunchedEffect(Unit) {
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.TIRAMISU &&
            androidx.core.content.ContextCompat.checkSelfPermission(
                context,
                android.Manifest.permission.POST_NOTIFICATIONS,
            ) != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
    }
    LaunchedEffect(user.email) { importer.setOwnerTestingAccess(user.email) }
    LaunchedEffect(accessToken, user.id) { library.load(accessToken, user.id) }
    LaunchedEffect(accessToken, libraryState.loaded, libraryState.loading, libraryState.books) {
        // `loaded && loading` is the cached snapshot that LibraryViewModel
        // publishes before the network call finishes; those books carry stale
        // playback positions. Waiting for `loading` to clear means hydration
        // reconciles against real server values, not against a cached 0.
        if (!libraryState.loaded || libraryState.loading) return@LaunchedEffect
        if (!player.beginAccountProgressHydration(user.id)) return@LaunchedEffect
        player.hydrateAccountProgress(
            books = libraryState.books,
            accessToken = accessToken,
            onPositionAvailable = library::updatePlaybackPosition,
            onSynced = { library.load(accessToken, user.id, force = true) },
        )
    }
    // Reopens the book that was open when the app was last alive, so coming back to a
    // player Android killed in the background finds the book still there.
    //
    // Separate from the reconciliation above, and deliberately not waiting for `loading`
    // to clear. This needs only the book's identity, which the cached snapshot already
    // carries, and the position comes from the local checkpoint: resumePositionMs takes
    // maxOf(local, server), so a cached row's stale 0 cannot rewind anyone. Waiting for
    // the network would leave a restored Player tab reading "Nothing is playing" for as
    // long as the request took, which is the very thing being fixed.
    LaunchedEffect(accessToken, libraryState.books) {
        if (player.state.value.book != null) return@LaunchedEffect
        val lastBookID = player.lastOpenBookID() ?: return@LaunchedEffect
        val book = libraryState.books.firstOrNull { it.id == lastBookID } ?: return@LaunchedEffect
        // Claimed only once there is really a book to open, so an early pass over an empty
        // list does not spend the one attempt.
        if (!player.beginLastOpenBookRestore(user.id)) return@LaunchedEffect
        player.open(book, accessToken)
    }
    LaunchedEffect(accessToken) {
        importer.resumeActiveScan(context.contentResolver, accessToken)
    }
    LaunchedEffect(externalAudioUri, accessToken) {
        val uri = externalAudioUri ?: return@LaunchedEffect
        if (importer.importTransferred(uri, context.contentResolver, accessToken)) {
            selected = 2
            onExternalAudioHandled()
        }
    }
    LaunchedEffect(companionTransferUri, accessToken) {
        val uri = companionTransferUri ?: return@LaunchedEffect
        if (importer.claimCompanionTransfer(uri, context.contentResolver, accessToken)) {
            selected = 2
            onCompanionTransferHandled()
        }
    }
    LaunchedEffect(importState.savedBook?.id) {
        if (importState.savedBook != null) library.load(accessToken, user.id, force = true)
    }
    // Every ebook outcome, not only a fresh import. Re-importing a book after moving the file
    // reports AlreadyInLibrary and carries no saved row, but the list on screen may still predate
    // the book -- and a listener who just re-picked their file expects to see it either way.
    LaunchedEffect(importState.ebookOutcome) {
        if (importState.ebookOutcome != null) library.load(accessToken, user.id, force = true)
    }
    if (showOnboarding) {
        FirstRunGuide(
            onFinished = {
                onboardingPreferences.edit().putBoolean(onboardingKey, true).apply()
                showOnboarding = false
            },
        )
    }
    BackHandler(enabled = selected == 1 && detailBook == null) {
        player.state.value.book?.let {
            library.updatePlaybackPosition(it.id, player.state.value.positionMs / 1000.0)
        }
        player.saveProgress { library.load(accessToken, user.id, force = true) }
        detailBook = player.state.value.book
        selected = 0
    }
    detailBook?.let { book ->
        if (showingBookFilters) {
            BookFiltersScreen(player, filtersLocked, onBack = { showingBookFilters = false })
        } else {
            BookDetailsScreen(
                book = book,
                coverPath = libraryState.coverPaths[book.fingerprint.sha256.lowercase()],
                player = player,
                onBack = { player.saveProgress(); detailBook = null },
                onPlay = { fromBeginning -> player.openAndStart(book, accessToken, fromBeginning); selected = 1; detailBook = null },
                onFilters = { showingBookFilters = true },
                onEditDetails = { title, author, narrator ->
                    // The sheet holds its own copy of the book, so it needs the saved
                    // row back rather than waiting for a library reload.
                    library.updateDetails(accessToken, book, title, author, narrator) { saved ->
                        detailBook = saved
                    }
                },
                onDelete = {
                    library.delete(accessToken, book) {
                        player.close()
                        detailBook = null
                    }
                },
            )
        }
        return
    }
    readerBook?.let { book ->
        EbookReaderScreen(
            narration = narration,
            filtersLocked = filtersLocked,
            onBack = {
                narration.close()
                readerBook = null
            },
        )
        return
    }
    ModalNavigationDrawer(
        drawerState = drawerState,
        gesturesEnabled = selected == 0,
        drawerContent = {
            ModalDrawerSheet(drawerContainerColor = ChoiceSurface) {
                Spacer(Modifier.height(28.dp))
                Row(Modifier.padding(horizontal = 20.dp), verticalAlignment = Alignment.CenterVertically) {
                    Image(painterResource(R.drawable.audiochoice_logo), null, Modifier.size(48.dp))
                    Spacer(Modifier.width(10.dp))
                    Row { Text("Audio", fontSize = 20.sp, fontWeight = FontWeight.Bold); Text("Choice", color = ChoiceGreen, fontSize = 20.sp, fontWeight = FontWeight.Bold) }
                }
                Spacer(Modifier.height(28.dp))
                NavigationDrawerItem(
                    label = { Text("My Library") },
                    icon = { Icon(Icons.Outlined.LibraryBooks, null) },
                    selected = librarySection == LibrarySection.MY_LIBRARY,
                    onClick = { librarySection = LibrarySection.MY_LIBRARY; scope.launch { drawerState.close() } },
                    modifier = Modifier.padding(horizontal = 12.dp),
                )
                NavigationDrawerItem(
                    label = { Text("Explore Scanned Books") },
                    icon = { Icon(Icons.Outlined.TravelExplore, null) },
                    selected = librarySection == LibrarySection.EXPLORE,
                    onClick = { librarySection = LibrarySection.EXPLORE; scope.launch { drawerState.close() } },
                    modifier = Modifier.padding(horizontal = 12.dp),
                )
            }
        },
    ) {
    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        bottomBar = {
            NavigationBar(containerColor = ChoiceSurface) {
                tabs.forEachIndexed { index, label ->
                    NavigationBarItem(
                        selected = selected == index,
                        onClick = {
                            if (selected == 1 && index != 1) {
                                player.state.value.book?.let {
                                    library.updatePlaybackPosition(it.id, player.state.value.positionMs / 1000.0)
                                }
                                player.saveProgress { library.load(accessToken, user.id, force = true) }
                            }
                            if (index == 1 && player.state.value.book == null) {
                                // Skips narrated books: the player has no audio for one, and
                                // would report the listener's book as broken.
                                libraryState.books
                                    .firstOrNull { LibraryShelves.shelfFor(it) == LibraryShelf.AUDIOBOOKS }
                                    ?.let { player.open(it, accessToken) }
                            }
                            if (selected == 2 && index != 2) importer.onImportScreenLeft()
                            selected = index
                            if (index != 3) profilePage = ProfilePage.MAIN
                        },
                        icon = { Icon(tabIcons[index], contentDescription = label) },
                        label = { Text(label, fontSize = 11.sp) },
                        colors = NavigationBarItemDefaults.colors(
                            selectedIconColor = ChoiceGreen,
                            selectedTextColor = ChoiceGreen,
                            indicatorColor = Color(0xFF343B38),
                        ),
                    )
                }
            }
        },
    ) { padding ->
        Column(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(horizontal = if (selected == 1) 0.dp else 18.dp),
        ) {
            Spacer(Modifier.height(12.dp))
            if (selected == 0 || selected == 3) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                    if (selected == 0 && searchExpanded) {
                        OutlinedTextField(
                            value = searchQuery,
                            onValueChange = { searchQuery = it },
                            placeholder = { Text(if (librarySection == LibrarySection.MY_LIBRARY) "Search your library" else "Search scanned books") },
                            leadingIcon = { Icon(Icons.Outlined.Search, null) },
                            trailingIcon = {
                                IconButton(onClick = { searchQuery = ""; searchExpanded = false }) {
                                    Icon(Icons.Outlined.Close, "Close search")
                                }
                            },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                            shape = RoundedCornerShape(14.dp),
                        )
                    } else Row(verticalAlignment = Alignment.CenterVertically) {
                        if (selected == 0) IconButton(onClick = { scope.launch { drawerState.open() } }) { Icon(Icons.Outlined.Menu, "Open library menu") }
                        Row { Text("Audio", fontSize = 20.sp, fontWeight = FontWeight.Bold); Text("Choice", color = ChoiceGreen, fontSize = 20.sp, fontWeight = FontWeight.Bold) }
                    }
                    if (selected == 0 && !searchExpanded) {
                        Row {
                            IconButton(onClick = { searchExpanded = true }) { Icon(Icons.Outlined.Search, "Search") }
                            IconButton(onClick = { librarySection = LibrarySection.EXPLORE }) { Icon(Icons.Outlined.TravelExplore, "Explore scanned books") }
                        }
                    }
                }
                Spacer(Modifier.height(16.dp))
            }
            if (selected == 0) {
                if (librarySection == LibrarySection.MY_LIBRARY) {
                    LibraryHome(
                        lastPlayedBookID = playerState.book?.id ?: rememberedLastBookID,
                        books = libraryState.books,
                        coverPaths = libraryState.coverPaths,
                        ebooksWithoutFilterResults = libraryState.ebooksWithoutFilterResults,
                        loading = libraryState.loading,
                        query = searchQuery,
                        onImport = { selected = 2 },
                        onOpenBook = { book ->
                            if (NarrationConfig.enabled &&
                                LibraryShelves.shelfFor(book) == LibraryShelf.EBOOKS
                            ) {
                                // Deliberately not player.open: that looks for audio this book
                                // has none of and would report it as unplayable.
                                narration.open(book, accessToken)
                                narration.refreshTier()
                                readerBook = book
                            } else {
                                player.open(book, accessToken)
                                detailBook = book
                            }
                        },
                        // Only the green Continue button skips the details sheet
                        // and resumes in the player.
                        onPlayNow = { book ->
                            if (NarrationConfig.enabled &&
                                LibraryShelves.shelfFor(book) == LibraryShelf.EBOOKS
                            ) {
                                narration.open(book, accessToken)
                                narration.refreshTier()
                                readerBook = book
                            } else {
                                player.openAndStart(book, accessToken, fromBeginning = false)
                                selected = 1
                            }
                        },
                    )
                } else ExploreScannedBooks(
                    catalog = libraryState.exploreBooks,
                    libraryBooks = libraryState.books,
                    coverPaths = libraryState.coverPaths,
                    loading = libraryState.loading,
                    query = searchQuery,
                    onOpen = { book -> player.open(book, accessToken); detailBook = book },
                )
            } else if (selected == 1) {
                // Uses the collected state, not player.state.value: reading the
                // StateFlow directly in composition meant this branch did not
                // recompose when a book finished loading.
                if (playerState.book == null) EmptyPlayer { selected = 0 }
                else PlayerScreen(
                    player,
                    filtersLocked,
                    onBack = {
                        player.saveProgress()
                        detailBook = player.state.value.book
                        selected = 0
                    },
                    // Moves to the import tab, which is where scan progress is shown. Starting a scan
                    // the listener could not watch would look like the button had done nothing.
                    onRescan = playerState.localUri?.let { uri ->
                        {
                            importer.rescan(uri, context.contentResolver, accessToken)
                            selected = 2
                        }
                    },
                )
            } else if (selected == 2) {
                ImportScreen(importer, accessToken) {
                    importer.onImportScreenLeft()
                    selected = 0
                }
            } else {
                when (profilePage) {
                    ProfilePage.MAIN -> ProfileScreen(
                        user = user,
                        playerState = playerState,
                        onFaq = { profilePage = ProfilePage.FAQ },
                        onSupport = { profilePage = ProfilePage.SUPPORT },
                        onParentalControls = { profilePage = ProfilePage.PARENTAL_CONTROLS },
                        onPremium = { profilePage = ProfilePage.PREMIUM },
                        // Experimental only: a diagnostic for a feature the beta build has no
                        // access to, so it must not appear there.
                        onVoiceMeasurement = if (NarrationConfig.enabled) {
                            { profilePage = ProfilePage.VOICE_MEASUREMENT }
                        } else null,
                        onLogout = onLogout,
                        accountPlan = libraryState.accountPlan,
                    )
                    ProfilePage.FAQ -> FaqScreen(api) { profilePage = ProfilePage.MAIN }
                    ProfilePage.SUPPORT -> SupportScreen(
                        user = user,
                        accessToken = accessToken,
                        support = support,
                        onBack = {
                            support.reset()
                            profilePage = ProfilePage.MAIN
                        },
                    )
                    ProfilePage.PARENTAL_CONTROLS -> ParentalControlsScreen(
                        store = parentalControls,
                        enabled = filtersLocked,
                        onEnabledChanged = { filtersLocked = it },
                        onBack = { profilePage = ProfilePage.MAIN },
                    )
                    ProfilePage.VOICE_MEASUREMENT ->
                        VoiceMeasurementScreen(onBack = { profilePage = ProfilePage.MAIN })
                    ProfilePage.PREMIUM -> PremiumScreen(onBack = { profilePage = ProfilePage.MAIN })
                }
            }
        }
    }
    }
}

private enum class ProfilePage { MAIN, FAQ, SUPPORT, PARENTAL_CONTROLS, VOICE_MEASUREMENT, PREMIUM }

/**
 * The subscription paywall.
 *
 * Reachable anytime from Profile, not gated behind hitting a limit -- the premium voice itself
 * is what [NarrationTierStore] gates, and a listener deciding whether to subscribe should be
 * able to find this screen without first bumping into a wall.
 *
 * A fresh [PurchaseViewModel] per visit rather than one held for the app's lifetime: it owns a
 * live [com.android.billingclient.api.BillingClient] connection, and ending that connection when
 * this screen closes (`onCleared`) is the whole reason `viewModel()` + `remember` are used here
 * instead of a singleton, unlike iOS where StoreKit2 has no comparable per-screen connection to
 * manage.
 */
@Composable
private fun PremiumScreen(onBack: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val activity = context as? android.app.Activity ?: return
    val api = remember { com.audiochoice.mobile.data.AudioChoiceApi(kotlinx.serialization.json.Json { ignoreUnknownKeys = true; encodeDefaults = true }) }
    val sessions = remember { com.audiochoice.mobile.data.SessionStore(context, kotlinx.serialization.json.Json { ignoreUnknownKeys = true; encodeDefaults = true }) }
    val viewModel: com.audiochoice.mobile.purchase.PurchaseViewModel = androidx.lifecycle.viewmodel.compose.viewModel(
        factory = com.audiochoice.mobile.purchase.PurchaseViewModel.Factory(api, sessions, activity),
    )
    val state by viewModel.state.collectAsStateWithLifecycle()

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.Outlined.ArrowBack, "Back") }
            Text("Premium", fontSize = 22.sp, fontWeight = FontWeight.Bold)
        }
        Spacer(Modifier.height(8.dp))
        Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(14.dp)) {
            Column(Modifier.padding(20.dp)) {
                Row {
                    Text("Audio", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                    Text("Choice", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = ChoiceGreen)
                    Text(" Premium", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                }
                Spacer(Modifier.height(6.dp))
                Text(
                    "The most natural narration voice, closest to a human narrator.",
                    color = ChoiceMuted,
                )
            }
        }
        Spacer(Modifier.height(18.dp))

        when {
            state.access.plan == "premium" && state.access.isActive -> Card(
                Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
                shape = RoundedCornerShape(14.dp),
            ) {
                Row(Modifier.padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Outlined.CheckCircle, null, tint = ChoiceGreen)
                    Spacer(Modifier.width(10.dp))
                    Text("You're subscribed to AudioChoice Premium.", fontWeight = FontWeight.SemiBold)
                }
            }
            state.access.plan == "founder" -> Card(
                Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
                shape = RoundedCornerShape(14.dp),
            ) {
                Row(Modifier.padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Outlined.Star, null, tint = ChoiceGreen)
                    Spacer(Modifier.width(10.dp))
                    Text("You have free lifetime Founder access.", fontWeight = FontWeight.SemiBold)
                }
            }
            state.product != null -> {
                val price = state.product?.subscriptionOfferDetails?.firstOrNull()
                    ?.pricingPhases?.pricingPhaseList?.firstOrNull()?.formattedPrice ?: ""
                Button(
                    onClick = { viewModel.purchase(activity) },
                    enabled = !state.isPurchasing,
                    modifier = Modifier.fillMaxWidth().height(54.dp),
                    shape = RoundedCornerShape(12.dp),
                ) {
                    if (state.isPurchasing) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                    else Text("Subscribe — $price/month", fontWeight = FontWeight.Bold)
                }
                Spacer(Modifier.height(12.dp))
                TextButton(onClick = { viewModel.restorePurchases() }, enabled = !state.isPurchasing) {
                    Text("Restore Purchases")
                }
            }
            state.isLoadingProducts -> Box(Modifier.fillMaxWidth().padding(20.dp), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = ChoiceGreen)
            }
            else -> {
                Text(
                    "AudioChoice Premium is not available for purchase yet. Please check back soon.",
                    color = ChoiceMuted,
                )
                Spacer(Modifier.height(12.dp))
                TextButton(onClick = { viewModel.restorePurchases() }) { Text("Restore Purchases") }
            }
        }

        state.error?.let {
            Spacer(Modifier.height(12.dp))
            Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
        }
    }
}
private enum class LibrarySection { MY_LIBRARY, EXPLORE }
private enum class LibrarySort(val label: String) { RECENT("Recently Added"), A_TO_Z("A–Z"), Z_TO_A("Z–A") }

@Composable
private fun FirstRunGuide(onFinished: () -> Unit) {
    var page by rememberSaveable { mutableIntStateOf(0) }
    // Four steps, in the order someone actually meets them: get a book in, get a book in from a
    // computer, decide what it plays, then the reading edition. Each names the control it is talking
    // about, because a tour that describes a feature without saying where it lives is a tour someone
    // has to take twice.
    val slides = listOf(
        Triple(
            Icons.Outlined.LibraryAdd,
            "Bring in a book you own",
            "Tap Import and choose an audiobook file. It is copied into AudioChoice's private " +
                "storage on this device — nothing is uploaded unless a scan is needed for that " +
                "exact recording. MP3 and M4B work directly, and Audible AAX files are converted " +
                "here using your own account.",
        ),
        Triple(
            Icons.Outlined.Devices,
            "Downloaded it on a computer?",
            "Some audiobooks are easiest to get on a computer. Open the AudioChoice transfer tool " +
                "there and send the file straight to your phone — no cable, no cloud drive. It " +
                "arrives in Import like any other file.",
        ),
        Triple(
            Icons.Outlined.Shield,
            "Choose what you hear",
            "Each audiobook is scanned once, and you pick which kinds of content to remove. " +
                "Playback skips or mutes those moments. Open the shield in the player to change " +
                "your choices, and protect them with a PIN under Parental Controls if you like.",
        ),
        Triple(
            Icons.Outlined.MenuBook,
            "Read along, or be read to",
            "Import an EPUB and it lands on the Ebooks shelf, opening in the reader instead of the " +
                "player. Adjust the text, follow along while it is read aloud, or attach it to an " +
                "audiobook you already own to read and listen together.",
        ),
    )
    val slide = slides[page]
    AlertDialog(
        onDismissRequest = {},
        icon = {
            Box(
                Modifier.size(70.dp).background(ChoiceSurface, RoundedCornerShape(22.dp)),
                contentAlignment = Alignment.Center,
            ) { Icon(slide.first, null, tint = ChoiceGreen, modifier = Modifier.size(38.dp)) }
        },
        title = { Text(slide.second, textAlign = TextAlign.Center, fontWeight = FontWeight.Bold) },
        text = {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Text(slide.third, color = ChoiceMuted, textAlign = TextAlign.Center, lineHeight = 21.sp)
                Spacer(Modifier.height(20.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                    slides.indices.forEach { index ->
                        Box(
                            Modifier
                                .size(if (index == page) 22.dp else 7.dp, 7.dp)
                                .background(if (index == page) ChoiceGreen else ChoiceOutline, RoundedCornerShape(50)),
                        )
                    }
                }
            }
        },
        dismissButton = {
            if (page > 0) TextButton(onClick = { page-- }) { Text("Back") }
            else TextButton(onClick = onFinished) { Text("Skip") }
        },
        confirmButton = {
            Button(onClick = { if (page < slides.lastIndex) page++ else onFinished() }) {
                Text(if (page < slides.lastIndex) "Next" else "Start listening")
            }
        },
    )
}

@Composable
private fun LibraryHome(
    books: List<LibraryBook>,
    coverPaths: Map<String, String>,
    /** Ebooks with no filter results, by lowercase sha256. */
    ebooksWithoutFilterResults: Set<String>,
    loading: Boolean,
    query: String,
    onImport: () -> Unit,
    /** Opens the book details sheet. Every artwork and row tap lands here. */
    onOpenBook: (LibraryBook) -> Unit,
    /** Resumes straight into the player. Only the green Continue button does this. */
    onPlayNow: (LibraryBook) -> Unit,
    /**
     * The book most recently opened in the player, or null if none has been.
     *
     * Continue Listening needs "most recent", and the library rows carry no listened-at time --
     * only a position and an added-at. So this comes from the player, which records the book it
     * opened, and is the same value iOS has always used for its own Continue card.
     */
    lastPlayedBookID: String?,
) {
    var sort by rememberSaveable { mutableStateOf(LibrarySort.RECENT) }
    var sortMenu by remember { mutableStateOf(false) }
    // The ebook shelf is only offered where narration exists, and only once the listener has an
    // ebook on it. A beta build has no way to create one, so the tab row never appears there and
    // the screen below is byte-for-byte what it renders today.
    val ebooksAvailable = NarrationConfig.enabled && LibraryShelves.hasEbooks(books)
    var shelf by rememberSaveable { mutableStateOf(LibraryShelf.AUDIOBOOKS) }
    // A listener who deletes their last ebook must not be left on an empty tab with no way back.
    if (!ebooksAvailable && shelf != LibraryShelf.AUDIOBOOKS) shelf = LibraryShelf.AUDIOBOOKS
    if (loading) { LinearProgressIndicator(Modifier.fillMaxWidth()); return }
    if (books.isEmpty()) {
        Box(
            Modifier.fillMaxWidth().border(1.dp, ChoiceOutline, RoundedCornerShape(16.dp)).background(ChoiceSurface, RoundedCornerShape(16.dp)).padding(32.dp),
            contentAlignment = Alignment.Center,
        ) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Icon(Icons.Outlined.LibraryBooks, null, tint = ChoiceGreen, modifier = Modifier.size(46.dp))
                Spacer(Modifier.height(12.dp)); Text("Your library is ready", fontWeight = FontWeight.SemiBold)
                Text("Import an audiobook to begin.", color = ChoiceMuted)
                Spacer(Modifier.height(16.dp)); Button(onClick = onImport) { Text("Import Audiobook") }
            }
        }
        return
    }
    // Scoped to the shelf on view. A shelf offering to resume a book that is not on it -- and
    // that opens a different surface when tapped -- would be actively misleading.
    val shelfBooks = if (ebooksAvailable) LibraryShelves.booksOn(books, shelf) else books
    // The book last listened to, then any book with progress, then anything at all.
    //
    // This used to take the first book on the shelf with a position, which is list order and has
    // nothing to do with recency: whichever book sorted first kept the card forever, so starting
    // a second book changed nothing and Continue Listening pointed at the wrong one permanently.
    val featured = shelfBooks.firstOrNull {
        it.id == lastPlayedBookID && !it.isFinished
    } ?: shelfBooks.firstOrNull { it.playbackPositionSeconds > 0 && !it.isFinished }
        ?: shelfBooks.firstOrNull()
    val visibleBooks = shelfBooks.filter { book ->
        query.isBlank() || book.title.contains(query, ignoreCase = true) ||
            book.author?.contains(query, ignoreCase = true) == true
    }
    val sortedBooks = when (sort) {
        LibrarySort.RECENT -> visibleBooks.sortedByDescending { it.addedAt }
        LibrarySort.A_TO_Z -> visibleBooks.sortedBy { it.title.lowercase() }
        LibrarySort.Z_TO_A -> visibleBooks.sortedByDescending { it.title.lowercase() }
    }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        Text("My Library", fontSize = 26.sp, fontWeight = FontWeight.Bold)
        if (ebooksAvailable) {
            Spacer(Modifier.height(12.dp))
            TabRow(
                selectedTabIndex = shelf.ordinal,
                containerColor = MaterialTheme.colorScheme.background,
                contentColor = ChoiceGreen,
            ) {
                LibraryShelf.entries.forEach { option ->
                    Tab(
                        selected = shelf == option,
                        onClick = { shelf = option },
                        text = {
                            Text(
                                "${option.label} (${LibraryShelves.booksOn(books, option).size})",
                                fontSize = 13.sp,
                            )
                        },
                        selectedContentColor = ChoiceGreen,
                        unselectedContentColor = ChoiceMuted,
                    )
                }
            }
        }
        if (featured != null) {
            Spacer(Modifier.height(16.dp))
            Text("Continue Listening", fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(10.dp))
            Card(
                Modifier.fillMaxWidth().height(205.dp).clickable { onOpenBook(featured) },
                shape = RoundedCornerShape(15.dp),
                colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
            ) {
                Box(Modifier.fillMaxSize()) {
                    BookArtwork(
                        coverPaths[featured.fingerprint.sha256.lowercase()],
                        Modifier.fillMaxWidth().height(120.dp),
                        isFinished = featured.isFinished,
                    )
                    Column(Modifier.align(Alignment.BottomStart).fillMaxWidth().background(Color(0xE6101514)).padding(14.dp)) {
                        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(featured.title, fontSize = 18.sp, fontWeight = FontWeight.SemiBold, maxLines = 1)
                                Text(
                                    featured.author
                                        ?: if (LibraryShelves.shelfFor(featured) == LibraryShelf.EBOOKS) {
                                            "Imported ebook"
                                        } else "Imported audiobook",
                                    color = ChoiceMuted,
                                    fontSize = 12.sp,
                                )
                            }
                            FilledIconButton(onClick = { onPlayNow(featured) }, colors = IconButtonDefaults.filledIconButtonColors(containerColor = ChoiceGreen)) {
                                Icon(Icons.Outlined.PlayArrow, "Resume listening", tint = Color.Black)
                            }
                        }
                        Spacer(Modifier.height(8.dp))
                        // Absent while a narrated book is still rendering: a bar drawn against
                        // an unknown total reads as "barely started" however far in they are.
                        LibraryShelves.progressOf(featured)?.let { fraction ->
                            LinearProgressIndicator(
                                progress = { fraction },
                                modifier = Modifier.fillMaxWidth(),
                            )
                        }
                    }
                }
            }
        }
        Spacer(Modifier.height(22.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                if (ebooksAvailable) shelf.label else "Audiobooks",
                Modifier.weight(1f),
                fontSize = 18.sp,
                fontWeight = FontWeight.SemiBold,
            )
            Box {
                OutlinedButton(onClick = { sortMenu = true }, contentPadding = PaddingValues(horizontal = 12.dp, vertical = 7.dp)) {
                    Icon(Icons.Outlined.Sort, null, Modifier.size(18.dp)); Spacer(Modifier.width(6.dp)); Text(sort.label, fontSize = 12.sp)
                }
                DropdownMenu(expanded = sortMenu, onDismissRequest = { sortMenu = false }) {
                    LibrarySort.entries.forEach { option ->
                        DropdownMenuItem(
                            text = { Text(option.label) },
                            onClick = { sort = option; sortMenu = false },
                            trailingIcon = { if (sort == option) Icon(Icons.Outlined.Check, null, tint = ChoiceGreen) },
                        )
                    }
                }
            }
        }
        Spacer(Modifier.height(24.dp))
        if (sortedBooks.isEmpty()) {
            Text(
                if (ebooksAvailable && shelf == LibraryShelf.EBOOKS) {
                    "No ebooks match “$query”."
                } else "No audiobooks match “$query”.",
                color = ChoiceMuted,
                modifier = Modifier.padding(vertical = 28.dp),
            )
        } else sortedBooks.forEach { book ->
            LibraryBookRow(
                book,
                coverPaths[book.fingerprint.sha256.lowercase()],
                onOpenBook,
                filtersUnavailable =
                    book.fingerprint.sha256.lowercase() in ebooksWithoutFilterResults,
            )
        }
        Spacer(Modifier.height(24.dp))
    }
}

@Composable
private fun LibraryBookRow(
    book: LibraryBook,
    coverPath: String?,
    onOpen: (LibraryBook) -> Unit,
    filtersUnavailable: Boolean = false,
) {
    // Both shelves share this row, so the labels have to suit whichever book is in it. An ebook has
    // no running time and was not imported as an audiobook, and saying otherwise about someone's own
    // book is the kind of small wrongness that makes the whole screen feel unreliable.
    val isEbook = LibraryShelves.shelfFor(book) == LibraryShelf.EBOOKS
    Card(
        Modifier.fillMaxWidth().padding(bottom = 10.dp).clickable { onOpen(book) },
        colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
        shape = RoundedCornerShape(13.dp),
    ) {
        Row(Modifier.fillMaxWidth().padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            BookArtwork(
                coverPath,
                Modifier.size(width = 62.dp, height = 80.dp),
                isFinished = book.isFinished,
            )
            Spacer(Modifier.width(13.dp))
            Column(Modifier.weight(1f)) {
                Text(book.title, fontWeight = FontWeight.SemiBold, maxLines = 2)
                Text(
                    book.author ?: if (isEbook) "Imported ebook" else "Imported audiobook",
                    color = ChoiceMuted,
                    fontSize = 12.sp,
                )
                Spacer(Modifier.height(6.dp))
                if (isEbook) {
                    // An ebook has a length in words, not in hours, and its running time does not
                    // exist until a voice has read it. "Read aloud" says what the row offers.
                    Text("Read aloud", color = ChoiceGreen, fontSize = 11.sp)
                } else {
                    Text(
                        formatDuration(book.fingerprint.duration),
                        color = ChoiceGreen,
                        fontSize = 11.sp,
                    )
                }
                // Said here rather than only inside the reader, so the listener learns it before
                // they open the book and press a button that cannot do what they expect.
                if (filtersUnavailable) {
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "Filters unavailable",
                        color = ChoiceMuted,
                        fontSize = 10.sp,
                    )
                }
            }
            Icon(Icons.Outlined.ChevronRight, null, tint = ChoiceMuted)
        }
    }
}

@Composable
private fun ExploreScannedBooks(
    catalog: List<ExploreCatalogBook>,
    libraryBooks: List<LibraryBook>,
    coverPaths: Map<String, String>,
    loading: Boolean,
    query: String,
    onOpen: (LibraryBook) -> Unit,
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    var selectedBook by remember { mutableStateOf<ExploreCatalogBook?>(null) }
    val visibleCatalog = catalog.filter { item ->
        query.isBlank() || item.title.contains(query, ignoreCase = true) ||
            item.author?.contains(query, ignoreCase = true) == true ||
            item.seriesTitle?.contains(query, ignoreCase = true) == true
    }
    Column(Modifier.fillMaxSize()) {
        Text("Explore Scanned Books", Modifier.fillMaxWidth(), fontSize = 26.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(6.dp))
        Text("Audiobooks with reusable AudioChoice filter scans.", color = ChoiceMuted, fontSize = 13.sp)
        Spacer(Modifier.height(18.dp))
        if (loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        else if (visibleCatalog.isEmpty()) {
            Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
                Icon(Icons.Outlined.TravelExplore, null, tint = ChoiceGreen, modifier = Modifier.size(60.dp))
                Spacer(Modifier.height(14.dp))
                Text(if (query.isBlank()) "No published scans yet" else "No matching scanned books", fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
                Text(if (query.isBlank()) "The first verified audiobook scans will appear here." else "Try a different title, author, or series.", color = ChoiceMuted, textAlign = TextAlign.Center)
            }
        } else Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
            visibleCatalog.forEach { item ->
                val owned = libraryBooks.firstOrNull { book ->
                    book.title.equals(item.title, ignoreCase = true) &&
                        (item.author == null || book.author.equals(item.author, ignoreCase = true))
                }
                Card(
                    Modifier.fillMaxWidth().padding(bottom = 11.dp).clickable { selectedBook = item },
                    colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
                    shape = RoundedCornerShape(14.dp),
                ) {
                    Row(Modifier.fillMaxWidth().padding(14.dp)) {
                        BookArtwork(
                            owned?.let { coverPaths[it.fingerprint.sha256.lowercase()] }
                                ?: coverPaths[item.catalogID.lowercase()],
                            Modifier.size(width = 70.dp, height = 92.dp).clip(RoundedCornerShape(8.dp)),
                            isFinished = owned?.isFinished == true,
                        )
                        Spacer(Modifier.width(13.dp))
                        Column(Modifier.weight(1f)) {
                            Text(item.title, fontWeight = FontWeight.SemiBold, maxLines = 2)
                            Text(item.author ?: "Unknown author", color = ChoiceMuted, fontSize = 12.sp)
                            Text("${formatDuration(item.duration)} • ${item.eventCount} filter controls", color = ChoiceGreen, fontSize = 11.sp)
                            Spacer(Modifier.height(9.dp))
                            Text(if (owned != null) "In your library" else "View details", color = ChoiceMuted, fontSize = 12.sp)
                        }
                    }
                }
            }
            Spacer(Modifier.height(20.dp))
        }
    }
    selectedBook?.let { item ->
        val owned = libraryBooks.firstOrNull { book ->
            book.title.equals(item.title, ignoreCase = true) &&
                (item.author == null || book.author.equals(item.author, ignoreCase = true))
        }
        ExploreBookDetails(
            item = item,
            coverPath = owned?.let { coverPaths[it.fingerprint.sha256.lowercase()] }
                ?: coverPaths[item.catalogID.lowercase()],
            owned = owned != null,
            onDismiss = { selectedBook = null },
            onAction = {
                selectedBook = null
                if (owned != null) onOpen(owned)
                else runCatching {
                    val audibleUrl = android.net.Uri.Builder()
                        .scheme("https").authority("www.audible.com").path("/search")
                        .appendQueryParameter("keywords", listOfNotNull(item.title, item.author).joinToString(" "))
                        .build()
                    context.startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, audibleUrl))
                }
            },
        )
    }
}

@Composable
private fun ExploreBookDetails(
    item: ExploreCatalogBook,
    coverPath: String?,
    owned: Boolean,
    onDismiss: () -> Unit,
    onAction: () -> Unit,
) {
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss) {
        Surface(
            Modifier.fillMaxWidth().fillMaxHeight(.92f),
            color = MaterialTheme.colorScheme.background,
            shape = RoundedCornerShape(24.dp),
        ) {
            Column(Modifier.fillMaxSize()) {
                Row(Modifier.fillMaxWidth().padding(10.dp), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = onDismiss) { Icon(Icons.Outlined.ArrowBack, "Close") }
                    Text("Scanned Audiobook", fontWeight = FontWeight.SemiBold)
                    Icon(Icons.Outlined.VerifiedUser, "Verified scan", tint = ChoiceGreen, modifier = Modifier.padding(end = 14.dp))
                }
                Column(
                    Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 22.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    BookArtwork(coverPath, Modifier.width(190.dp).aspectRatio(.72f).clip(RoundedCornerShape(16.dp)))
                    Spacer(Modifier.height(18.dp))
                    Text(item.title, fontSize = 23.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
                    Spacer(Modifier.height(5.dp))
                    Text(item.author ?: "Unknown author", color = ChoiceMuted, fontSize = 15.sp)
                    item.seriesTitle?.let {
                        Text(listOfNotNull(it, item.seriesNumber?.let { number -> "Book $number" }).joinToString(" • "), color = ChoiceGreen, fontSize = 13.sp)
                    }
                    Spacer(Modifier.height(18.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(9.dp)) {
                        ExploreStat("Runtime", formatDuration(item.duration), Modifier.weight(1f))
                        ExploreStat("Edition", item.editionType ?: item.fileType.uppercase(), Modifier.weight(1f))
                        ExploreStat("Controls", "${item.eventCount}", Modifier.weight(1f))
                    }
                    // Only shown when there is a real synopsis. The server now sends the
                    // publisher's own text, read from the file's description tags, and sends
                    // nothing at all rather than the generated line about AudioChoice's
                    // features that every uncurated book used to get under this heading.
                    item.description?.trim()?.takeIf { it.isNotEmpty() }?.let { synopsis ->
                        Spacer(Modifier.height(20.dp))
                        Card(colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(15.dp)) {
                            Column(Modifier.fillMaxWidth().padding(16.dp)) {
                                Text("About this audiobook", fontWeight = FontWeight.SemiBold)
                                Spacer(Modifier.height(8.dp))
                                Text(
                                    synopsis,
                                    color = ChoiceMuted,
                                    fontSize = 13.sp,
                                    lineHeight = 19.sp,
                                )
                            }
                        }
                    }
                    Spacer(Modifier.height(14.dp))
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Outlined.Shield, null, tint = ChoiceGreen)
                        Spacer(Modifier.width(10.dp))
                        Column {
                            Text("AudioChoice scan ready", fontWeight = FontWeight.SemiBold)
                            Text("${item.eventCount} filter controls • Scan ${item.scannerVersion}", color = ChoiceMuted, fontSize = 12.sp)
                        }
                    }
                    Spacer(Modifier.height(24.dp))
                    if (!owned && item.purchaseVerified) {
                        Text("Verified ${item.purchaseProvider} listing", color = ChoiceGreen, fontSize = 12.sp)
                        Spacer(Modifier.height(8.dp))
                    }
                }
                Button(
                    onClick = onAction,
                    modifier = Modifier.fillMaxWidth().padding(18.dp).height(54.dp),
                    shape = RoundedCornerShape(14.dp),
                ) {
                    Icon(if (owned) Icons.Outlined.LibraryBooks else Icons.Outlined.ShoppingBag, null)
                    Spacer(Modifier.width(9.dp))
                    Text(if (owned) "View in Library" else "Buy on Audible", fontWeight = FontWeight.Bold)
                }
            }
        }
    }
}

@Composable
private fun ExploreStat(label: String, value: String, modifier: Modifier = Modifier) {
    Card(modifier, colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(12.dp)) {
        Column(Modifier.fillMaxWidth().padding(vertical = 11.dp, horizontal = 6.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            Text(value, fontWeight = FontWeight.SemiBold, fontSize = 12.sp, maxLines = 2, textAlign = TextAlign.Center)
            Text(label, color = ChoiceMuted, fontSize = 10.sp)
        }
    }
}

@Composable
private fun EmptyPlayer(openLibrary: () -> Unit) {
    Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
        Icon(Icons.Outlined.GraphicEq, null, tint = ChoiceGreen, modifier = Modifier.size(58.dp))
        Text("Nothing is playing", fontSize = 22.sp, fontWeight = FontWeight.SemiBold)
        Text("Choose an audiobook from your library.", color = ChoiceMuted)
        Spacer(Modifier.height(16.dp)); Button(onClick = openLibrary) { Text("Open Library") }
    }
}

@Composable
private fun ProfileScreen(
    user: AuthUser,
    playerState: PlayerUiState,
    /** The account's plan, or null before it is known. Display only. */
    accountPlan: String? = null,
    onFaq: () -> Unit,
    onSupport: () -> Unit,
    onParentalControls: () -> Unit,
    onPremium: () -> Unit,
    /** Null outside the experimental build, where the row must not appear at all. */
    onVoiceMeasurement: (() -> Unit)? = null,
    onLogout: () -> Unit,
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    fun openUrl(url: String, preferDiscord: Boolean = false) {
        if (url.isBlank() || url.startsWith("REPLACE_")) {
            Toast.makeText(context, "The feedback form will be available soon.", Toast.LENGTH_SHORT).show()
            return
        }
        val generic = Intent(Intent.ACTION_VIEW, Uri.parse(url))
        val preferred = if (preferDiscord) Intent(generic).setPackage("com.discord") else generic
        runCatching {
            if (preferDiscord && preferred.resolveActivity(context.packageManager) != null) context.startActivity(preferred)
            else context.startActivity(generic)
        }.onFailure { Toast.makeText(context, "Unable to open that link.", Toast.LENGTH_SHORT).show() }
    }
    fun copyDiagnostics() {
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText("AudioChoice Beta Diagnostics", BetaDiagnostics.text(playerState)))
        Toast.makeText(context, "Diagnostics copied to clipboard.", Toast.LENGTH_SHORT).show()
    }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        Text("Profile", fontSize = 26.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(20.dp))
        Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = ChoiceSurface)) {
            Row(Modifier.padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.AccountCircle, null, tint = ChoiceGreen, modifier = Modifier.size(52.dp))
                Spacer(Modifier.width(14.dp)); Column { Text(user.displayName.ifBlank { "AudioChoice listener" }, fontSize = 19.sp); Text(user.email, color = ChoiceMuted); Text("Signed in with ${user.provider}", color = ChoiceGreen, fontSize = 12.sp)
                    // Shown only for a plan that is never charged. A "Free" label on an ordinary
                    // account would read as a limitation rather than a fact.
                    if (com.audiochoice.contracts.AccountPlans.isComplimentary(accountPlan)) {
                        Spacer(Modifier.height(4.dp))
                        Text(
                            "Founder · free for life",
                            color = ChoiceGreen,
                            fontSize = 12.sp,
                            fontWeight = FontWeight.SemiBold,
                        )
                    }
                }
            }
        }
        Spacer(Modifier.height(18.dp))
        Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(14.dp)) {
            ProfileRow(Icons.Outlined.Star, "Premium", "The most natural narration voice", onPremium)
            HorizontalDivider(color = ChoiceOutline)
            ProfileRow(Icons.Outlined.Lock, "Parental Controls", "Protect audiobook filters with a PIN", onParentalControls)
            HorizontalDivider(color = ChoiceOutline)
            ProfileRow(Icons.Outlined.HelpOutline, "FAQs", "Answers about importing, privacy, and filters", onFaq)
            HorizontalDivider(color = ChoiceOutline)
            ProfileRow(Icons.Outlined.SupportAgent, "Support", "Send a message to the AudioChoice team", onSupport)
            onVoiceMeasurement?.let { openMeasurement ->
                HorizontalDivider(color = ChoiceOutline)
                ProfileRow(
                    Icons.Outlined.RecordVoiceOver,
                    "Voice speed test",
                    "Check how fast this phone can read a book aloud",
                    openMeasurement,
                )
            }
        }
        if (BetaConfig.enabled) {
            Spacer(Modifier.height(18.dp))
            Text("Feedback", color = ChoiceMuted, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(7.dp))
            Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(14.dp)) {
                ProfileRow(Icons.Outlined.Forum, "Open Discord Community", "Join the AudioChoice beta community") {
                    openUrl(BetaConfig.discordUrl, preferDiscord = true)
                }
                HorizontalDivider(color = ChoiceOutline)
                ProfileRow(Icons.Outlined.RateReview, "Submit Feedback", "Report filter timing, playback, or app issues") {
                    if (BetaConfig.feedbackFormUrl.isBlank() || BetaConfig.feedbackFormUrl.startsWith("REPLACE_")) {
                        onSupport()
                    } else {
                        openUrl(BetaDiagnostics.feedbackUrl(BetaConfig.feedbackFormUrl, playerState))
                    }
                }
                HorizontalDivider(color = ChoiceOutline)
                ProfileRow(Icons.Outlined.ContentCopy, "Copy Diagnostics", "Copy safe device, book, playback, and filter details", ::copyDiagnostics)
            }
            Spacer(Modifier.height(18.dp))
            Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(14.dp)) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text("Beta Information", fontWeight = FontWeight.SemiBold)
                    Text("Beta Version: ${BetaConfig.version}", color = ChoiceMuted, fontSize = 12.sp)
                    Text("Supported Audiobooks:", color = ChoiceMuted, fontSize = 12.sp)
                    BetaConfig.supportedAudiobooks.forEach { Text("• $it", color = ChoiceMuted, fontSize = 12.sp) }
                    Text("Filter Profile Version: ${playerState.scannerVersion ?: "available after opening a book"}", color = ChoiceMuted, fontSize = 12.sp)
                    Text("This beta is focused on testing app functionality and filter accuracy.", color = ChoiceMuted, fontSize = 12.sp)
                }
            }
        }
        Spacer(Modifier.height(18.dp))
        OutlinedButton(onClick = onLogout, modifier = Modifier.fillMaxWidth().height(50.dp)) { Text("Sign out") }
        Spacer(Modifier.height(20.dp))
    }
}

@Composable
private fun ParentalControlsScreen(
    store: ParentalControlsStore,
    enabled: Boolean,
    onEnabledChanged: (Boolean) -> Unit,
    onBack: () -> Unit,
) {
    var pin by rememberSaveable { mutableStateOf("") }
    var confirmation by rememberSaveable { mutableStateOf("") }
    var currentPin by rememberSaveable { mutableStateOf("") }
    var error by rememberSaveable { mutableStateOf<String?>(null) }
    var changePinStep by rememberSaveable { mutableIntStateOf(0) }
    var verifiedCurrentPin by rememberSaveable { mutableStateOf("") }
    val validNewPin = pin.matches(Regex("\\d{4,6}")) && pin == confirmation
    // PIN checks derive a 120,000-iteration PBKDF2 key, so they run in a
    // coroutine rather than blocking the click handler.
    val pinScope = rememberCoroutineScope()

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Parental Controls", onBack)
        Column(Modifier.verticalScroll(rememberScrollState())) {
            Card(colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(16.dp)) {
                Column(Modifier.padding(18.dp)) {
                    Icon(if (enabled) Icons.Outlined.Lock else Icons.Outlined.LockOpen, null, tint = ChoiceGreen, modifier = Modifier.size(34.dp))
                    Spacer(Modifier.height(12.dp))
                    Text(if (enabled) "Filters are locked" else "Filters are unlocked", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                    Text(
                        if (enabled) "Filter choices can be viewed, but they cannot be changed until parental controls are turned off with the PIN."
                        else "Set the filters for each audiobook, then turn this lock on to protect those choices.",
                        color = ChoiceMuted,
                        fontSize = 13.sp,
                    )
                }
            }
            Spacer(Modifier.height(18.dp))
            if (!store.configured) {
                PinField(pin, { pin = it.take(6).filter(Char::isDigit); error = null }, "Create a 4–6 digit PIN")
                PinField(confirmation, { confirmation = it.take(6).filter(Char::isDigit); error = null }, "Confirm PIN")
                Button(
                    onClick = {
                        pinScope.launch {
                            store.configure(pin)
                            onEnabledChanged(true)
                            pin = ""
                            confirmation = ""
                        }
                    },
                    enabled = validNewPin,
                    modifier = Modifier.fillMaxWidth().height(50.dp),
                ) { Text("Set PIN and lock filters") }
            } else {
                PinField(currentPin, { currentPin = it.take(6).filter(Char::isDigit); error = null }, "Enter parental PIN")
                Button(
                    onClick = {
                        pinScope.launch {
                            val changed = if (enabled) store.disable(currentPin) else store.enable(currentPin)
                            if (changed) {
                                onEnabledChanged(!enabled)
                                currentPin = ""
                            } else error = "That PIN is incorrect."
                        }
                    },
                    enabled = currentPin.length in 4..6,
                    modifier = Modifier.fillMaxWidth().height(50.dp),
                ) { Text(if (enabled) "Unlock filter changes" else "Lock filter changes") }
                Spacer(Modifier.height(24.dp))
                OutlinedButton(
                    onClick = {
                        currentPin = ""
                        pin = ""
                        confirmation = ""
                        error = null
                        changePinStep = 1
                    },
                    modifier = Modifier.fillMaxWidth().height(50.dp),
                ) { Text("Change PIN") }
            }
            error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 12.dp)) }
            Spacer(Modifier.height(18.dp))
            Text("The PIN is stored securely for this AudioChoice account on this device. Clearing the app's data or reinstalling resets the local parental lock.", color = ChoiceMuted, fontSize = 12.sp)
        }
    }


    if (changePinStep == 1) {
        AlertDialog(
            onDismissRequest = { changePinStep = 0; currentPin = ""; error = null },
            title = { Text("Confirm current PIN") },
            text = {
                Column {
                    Text("Enter the current parental-controls PIN before choosing a new one.", color = ChoiceMuted)
                    Spacer(Modifier.height(12.dp))
                    PinField(currentPin, { currentPin = it.take(6).filter(Char::isDigit); error = null }, "Current PIN")
                    error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                }
            },
            confirmButton = {
                Button(
                    enabled = currentPin.length in 4..6,
                    onClick = {
                        pinScope.launch {
                        if (store.validate(currentPin)) {
                            verifiedCurrentPin = currentPin
                            currentPin = ""
                            error = null
                            changePinStep = 2
                        } else error = "That PIN is incorrect."
                        }
                    },
                ) { Text("Continue") }
            },
            dismissButton = { TextButton(onClick = { changePinStep = 0; currentPin = ""; error = null }) { Text("Cancel") } },
        )
    }

    if (changePinStep == 2) {
        AlertDialog(
            onDismissRequest = {
                changePinStep = 0; verifiedCurrentPin = ""; pin = ""; confirmation = ""; error = null
            },
            title = { Text("Create new PIN") },
            text = {
                Column {
                    PinField(pin, { pin = it.take(6).filter(Char::isDigit); error = null }, "New 4–6 digit PIN")
                    PinField(confirmation, { confirmation = it.take(6).filter(Char::isDigit); error = null }, "Confirm new PIN")
                    error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                }
            },
            confirmButton = {
                Button(
                    enabled = validNewPin,
                    onClick = {
                        pinScope.launch {
                            if (store.changePin(verifiedCurrentPin, pin)) {
                                onEnabledChanged(true)
                                changePinStep = 0
                                verifiedCurrentPin = ""
                                pin = ""
                                confirmation = ""
                                error = null
                            } else {
                                changePinStep = 1
                                verifiedCurrentPin = ""
                                error = "Please confirm the current PIN again."
                            }
                        }
                    },
                ) { Text("Submit") }
            },
            dismissButton = {
                TextButton(onClick = {
                    changePinStep = 0; verifiedCurrentPin = ""; pin = ""; confirmation = ""; error = null
                }) { Text("Cancel") }
            },
        )
    }
}

@Composable
private fun PinField(value: String, onValueChange: (String) -> Unit, label: String) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label) },
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
        visualTransformation = PasswordVisualTransformation(),
        singleLine = true,
        modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp),
        shape = RoundedCornerShape(12.dp),
    )
}

@Composable
private fun ProfileRow(icon: ImageVector, title: String, subtitle: String, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, null, tint = ChoiceGreen, modifier = Modifier.size(24.dp))
        Spacer(Modifier.width(13.dp))
        Column(Modifier.weight(1f)) { Text(title, fontWeight = FontWeight.SemiBold); Text(subtitle, color = ChoiceMuted, fontSize = 12.sp) }
        Icon(Icons.Outlined.ChevronRight, null, tint = ChoiceMuted)
    }
}

private data class FaqItem(val question: String, val answer: String)

/**
 * The copy that ships with the app.
 *
 * Used only when the served content cannot be fetched. Deliberately kept short: it exists so the help
 * screen is never empty, not to be a second source of truth that drifts from the server the way the
 * two apps' hardcoded copies drifted from each other.
 */
private val bundledFaq = FaqResponse(
    version = 1,
    sections = listOf(
        FaqSection(
            "Getting your audiobooks in",
            listOf(
                FaqEntry(
                    "Where can I get audiobooks I can import?",
                    "Any audiobook you own as a file will work. Stores selling DRM-free downloads, " +
                        "such as Libro.fm, are simplest: download the file and import it.",
                ),
                FaqEntry(
                    "Which file types work?",
                    "MP3 and M4B are the usual ones. Audible AAX files can be converted on the " +
                        "device using your own account's activation. EPUB files are imported as " +
                        "reading editions rather than audiobooks.",
                ),
            ),
        ),
        FaqSection(
            "Filters",
            listOf(
                FaqEntry(
                    "How do filters work?",
                    "An audiobook is scanned once, and you choose which categories to remove. " +
                        "Playback skips or mutes those moments.",
                ),
                FaqEntry(
                    "Why does one audiobook say filters are unavailable?",
                    "Filter results belong to one exact recording, so a different edition needs its " +
                        "own scan. Open the player and tap \"Scan this audiobook\".",
                ),
            ),
        ),
        FaqSection(
            "Your account",
            listOf(
                FaqEntry(
                    "I cannot sign in on a new device.",
                    "Your account works on every device. If the password is not accepted, choose " +
                        "\"Forgot password\" and we will email a six-digit code.",
                ),
            ),
        ),
    ),
)

@Composable
private fun FaqScreen(api: AudioChoiceApi, onBack: () -> Unit) {
    var expandedKey by rememberSaveable { mutableStateOf<String?>(null) }
    // Starts from the bundled copy so the screen has content on the first frame, then prefers the
    // served one when it arrives. Compared by version rather than assumed newer: an app that has not
    // been updated in a while should still show the better answers, and a server that has somehow
    // fallen behind should not replace them with worse ones.
    var faq by remember { mutableStateOf(bundledFaq) }
    LaunchedEffect(Unit) {
        runCatching { api.faq() }.getOrNull()?.let { served ->
            if (served.sections.isNotEmpty() && served.version >= faq.version) faq = served
        }
    }
    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Frequently Asked Questions", onBack)
        Column(Modifier.verticalScroll(rememberScrollState())) {
            faq.sections.forEach { section ->
                Text(
                    section.title,
                    color = ChoiceGreen,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.padding(top = 6.dp, bottom = 8.dp),
                )
                section.items.forEach { item ->
                    val key = "${section.title}/${item.question}"
                    Card(
                        Modifier.fillMaxWidth().padding(bottom = 9.dp).clickable {
                            expandedKey = if (expandedKey == key) null else key
                        },
                        colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
                        shape = RoundedCornerShape(12.dp),
                    ) {
                        Column(Modifier.padding(16.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(item.question, Modifier.weight(1f), fontWeight = FontWeight.SemiBold)
                                Icon(
                                    if (expandedKey == key) Icons.Outlined.ExpandLess
                                    else Icons.Outlined.ExpandMore,
                                    null,
                                    tint = ChoiceGreen,
                                )
                            }
                            if (expandedKey == key) {
                                Spacer(Modifier.height(10.dp))
                                Text(item.answer, color = ChoiceMuted, lineHeight = 20.sp, fontSize = 13.sp)
                            }
                        }
                    }
                }
                Spacer(Modifier.height(8.dp))
            }
            Spacer(Modifier.height(20.dp))
        }
    }
}

@Composable
private fun SupportScreen(
    user: AuthUser,
    accessToken: String,
    support: SupportViewModel,
    onBack: () -> Unit,
) {
    val state by support.state.collectAsStateWithLifecycle()
    var subject by rememberSaveable { mutableStateOf("") }
    var message by rememberSaveable { mutableStateOf("") }
    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Support", onBack)
        if (state.sent) {
            Card(
                Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
                shape = RoundedCornerShape(16.dp),
            ) {
                Column(Modifier.padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Icon(Icons.Outlined.MarkEmailRead, null, tint = ChoiceGreen, modifier = Modifier.size(48.dp))
                    Spacer(Modifier.height(14.dp))
                    Text("Message received", fontSize = 21.sp, fontWeight = FontWeight.Bold)
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "We sent a confirmation to ${user.email}. The AudioChoice support team will reply as soon as possible.",
                        color = ChoiceMuted,
                        textAlign = TextAlign.Center,
                    )
                    Spacer(Modifier.height(20.dp))
                    Button(onClick = onBack, modifier = Modifier.fillMaxWidth()) { Text("Done") }
                }
            }
        } else {
            Text("Send a message to the AudioChoice team. We’ll reply to ${user.email}.", color = ChoiceMuted, fontSize = 13.sp)
            Spacer(Modifier.height(18.dp))
            OutlinedTextField(
                subject,
                { if (it.length <= 120) subject = it },
                label = { Text("Subject") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                shape = RoundedCornerShape(12.dp),
                enabled = !state.submitting,
            )
            Spacer(Modifier.height(12.dp))
            OutlinedTextField(
                message,
                { if (it.length <= 5_000) message = it },
                label = { Text("How can we help?") },
                supportingText = { Text("${message.length} / 5,000") },
                modifier = Modifier.fillMaxWidth().height(230.dp),
                shape = RoundedCornerShape(12.dp),
                enabled = !state.submitting,
            )
            state.error?.let {
                Spacer(Modifier.height(10.dp))
                Text(it, color = MaterialTheme.colorScheme.error, fontSize = 13.sp)
            }
            Spacer(Modifier.height(16.dp))
            Button(
                onClick = { support.submit(accessToken, subject, message) },
                enabled = !state.submitting && subject.trim().length >= 3 && message.trim().length >= 10,
                modifier = Modifier.fillMaxWidth().height(52.dp),
            ) {
                if (state.submitting) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                else {
                    Icon(Icons.Outlined.Send, null)
                    Spacer(Modifier.width(8.dp))
                    Text("Send Message")
                }
            }
        }
    }
}

@Composable
private fun ScreenHeader(title: String, onBack: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(bottom = 16.dp), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = onBack) { Icon(Icons.Outlined.ArrowBack, "Back") }
        Text(title, Modifier.weight(1f), fontSize = 22.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
        Spacer(Modifier.size(48.dp))
    }
}

@Composable
private fun BookDetailsScreen(
    book: LibraryBook,
    coverPath: String?,
    player: PlayerViewModel,
    onBack: () -> Unit,
    onPlay: (Boolean) -> Unit,
    onFilters: () -> Unit,
    onDelete: () -> Unit,
    onEditDetails: (title: String, author: String?, narrator: String?) -> Unit,
) {
    val state by player.state.collectAsStateWithLifecycle()
    var chapterDialog by remember { mutableStateOf(false) }
    var bookmarkDialog by remember { mutableStateOf(false) }
    var moreMenu by remember { mutableStateOf(false) }
    var editDetails by remember { mutableStateOf(false) }
    var showDiagnostics by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    var readingEditionSheet by remember { mutableStateOf(false) }
    val epubLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> uri?.let(player::attachEpub) }
    if (showDiagnostics) {
        // Reads the persisted trace rather than live state: the failure only occurs
        // across a process restart, so the useful values are the ones recorded before
        // the app was killed.
        val trace = remember(book.id, showDiagnostics) { player.progressTrace(book.id) }
        AlertDialog(
            onDismissRequest = { showDiagnostics = false },
            icon = { Icon(Icons.Outlined.BugReport, null, tint = ChoiceGreen) },
            title = { Text("Playback diagnostics") },
            text = {
                Column(
                    Modifier.verticalScroll(rememberScrollState()),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    Text(
                        "The most recent saves and resume decisions for this book, " +
                            "oldest first. Send this to support.",
                        color = ChoiceMuted,
                        fontSize = 12.sp,
                    )
                    Text(trace, fontSize = 11.sp, fontFamily = FontFamily.Monospace)
                    Text(
                        "live position ${state.positionMs}ms of ${state.durationMs}ms",
                        fontSize = 11.sp,
                        fontFamily = FontFamily.Monospace,
                        color = ChoiceMuted,
                    )
                }
            },
            confirmButton = { TextButton(onClick = { showDiagnostics = false }) { Text("Close") } },
            dismissButton = {
                TextButton(onClick = { player.clearProgressTrace(book.id); showDiagnostics = false }) {
                    Text("Clear")
                }
            },
        )
    }
    if (editDetails) {
        BookDetailsEditDialog(
            book = book,
            onDismiss = { editDetails = false },
            onSave = { title, author, narrator ->
                editDetails = false
                onEditDetails(title, author, narrator)
            },
        )
    }
    Column(
        Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().verticalScroll(rememberScrollState()).padding(18.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            IconButton(onClick = onBack) { Icon(Icons.Outlined.ArrowBack, "Back") }
            Box {
                IconButton(onClick = { moreMenu = true }) { Icon(Icons.Outlined.MoreVert, "More") }
                DropdownMenu(expanded = moreMenu, onDismissRequest = { moreMenu = false }) {
                    DropdownMenuItem(
                        text = { Text("Start from Beginning") },
                        leadingIcon = { Icon(Icons.Outlined.Replay, null) },
                        enabled = state.localUri != null,
                        onClick = { moreMenu = false; onPlay(true) },
                    )
                    DropdownMenuItem(
                        text = { Text("Edit Details") },
                        leadingIcon = { Icon(Icons.Outlined.Edit, null) },
                        onClick = { moreMenu = false; editDetails = true },
                    )
                    if (BuildConfig.BETA_BUILD) {
                        DropdownMenuItem(
                            text = { Text("Playback Diagnostics") },
                            leadingIcon = { Icon(Icons.Outlined.BugReport, null) },
                            onClick = { moreMenu = false; showDiagnostics = true },
                        )
                    }
                    DropdownMenuItem(
                        text = { Text("Delete from Library") },
                        leadingIcon = { Icon(Icons.Outlined.DeleteOutline, null) },
                        onClick = { moreMenu = false; confirmDelete = true },
                    )
                }
            }
        }
        BookArtwork(
            state.coverPath ?: coverPath,
            Modifier.size(220.dp),
            isFinished = state.book?.takeIf { it.id == book.id }?.isFinished ?: book.isFinished,
        )
        Spacer(Modifier.height(14.dp))
        Text(book.title, fontSize = 24.sp, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center)
        Text(book.author ?: "Imported audiobook", color = ChoiceMuted)
        book.narrator?.trim()?.takeIf { it.isNotBlank() }?.let { narrator ->
            Text("Narrated by $narrator", color = ChoiceMuted, fontSize = 13.sp)
        }
        Spacer(Modifier.height(8.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            SuggestionChip(onClick = {}, label = { Text(book.fingerprint.fileType.uppercase()) })
            // Only state a production style the file itself claims. This chip read
            // "Full Cast" for every audiobook, which was untrue of most of them.
            book.fingerprint.editionType?.trim()?.takeIf { it.isNotBlank() }?.let { edition ->
                SuggestionChip(onClick = {}, label = { Text(edition) })
            }
        }
        Spacer(Modifier.height(18.dp))
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly) {
            DetailMetric(Icons.Outlined.Timer, formatDuration(book.fingerprint.duration), "Runtime")
            DetailMetric(Icons.Outlined.TableRows, "${state.chapters.size}", "Chapters")
            DetailMetric(Icons.Outlined.Security, state.resultVersion(), "Filters", onFilters)
            DetailMetric(Icons.Outlined.VerifiedUser, "Verified", "Private")
        }
        Spacer(Modifier.height(18.dp))
        if (state.localUri == null) {
            Text("This audiobook file is no longer on this device. Re-import it to play.", color = ChoiceMuted, textAlign = TextAlign.Center)
            Spacer(Modifier.height(10.dp))
        }
        // One button, and it always picks up where the listener stopped. Having Play
        // mean "start over" beside Continue meaning "resume" invited exactly the
        // mistake of losing your place. Starting over is the rarer intent, so it
        // moved to the overflow menu.
        val hasProgress = state.positionMs > RESUMABLE_POSITION_MS ||
            book.playbackPositionSeconds > 1.0
        Button(
            enabled = state.localUri != null,
            onClick = { onPlay(false) },
            modifier = Modifier.fillMaxWidth().height(50.dp),
        ) {
            Icon(Icons.Outlined.PlayArrow, null)
            Spacer(Modifier.width(6.dp))
            Text(if (hasProgress) "Resume" else "Play")
        }
        Spacer(Modifier.height(16.dp))
        Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
            shape = RoundedCornerShape(14.dp),
        ) {
            DetailRow(Icons.Outlined.List, "Chapters", "${state.chapters.size}") { chapterDialog = true }
            HorizontalDivider(color = ChoiceOutline)
            // Was a hardcoded "0" that did nothing when tapped. Notes are kept on
            // bookmarks, so that is both the real count and the right destination.
            val noteCount = state.bookmarks.count { !it.note.isNullOrBlank() }
            DetailRow(Icons.Outlined.Notes, "Notes", "$noteCount") { bookmarkDialog = true }
            HorizontalDivider(color = ChoiceOutline)
            DetailRow(Icons.Outlined.BookmarkBorder, "Bookmarks", "${state.bookmarks.size}") { bookmarkDialog = true }
            HorizontalDivider(color = ChoiceOutline)
            val detectedCategoryCount = PlaybackFilterTaxonomy.available(state.scanEvents).size
            DetailRow(Icons.Outlined.Tune, "Filters", "$detectedCategoryCount detected", onFilters)
            HorizontalDivider(color = ChoiceOutline)
            // Books finish on reaching the end, but someone who stopped before the credits
            // has no other way to say they are done with it.
            val finished = state.book?.takeIf { it.id == book.id }?.isFinished ?: book.isFinished
            DetailRow(
                if (finished) Icons.Outlined.CheckCircle else Icons.Outlined.RadioButtonUnchecked,
                "Finished",
                if (finished) "Yes" else "No",
            ) { player.setFinished(!finished) }
            if (BuildConfig.BETA_BUILD) {
                HorizontalDivider(color = ChoiceOutline)
                DetailRow(
                    Icons.Outlined.MenuBook,
                    "Reading edition",
                    if (state.epubText == null) "Attach EPUB" else "EPUB attached",
                ) {
                    // Some providers report an EPUB as octet-stream, which made the
                    // file unselectable with a strict epub+zip filter.
                    if (state.epubText == null) {
                        epubLauncher.launch(
                            arrayOf("application/epub+zip", "application/octet-stream", "*/*"),
                        )
                    } else {
                        readingEditionSheet = true
                    }
                }
                // readerSyncMessage was computed on every sync and never shown, so
                // a reading edition that failed to match the audiobook looked
                // identical to one that worked.
                state.readerSyncMessage?.let { message ->
                    Text(
                        message,
                        color = ChoiceMuted,
                        fontSize = 11.sp,
                        modifier = Modifier.padding(start = 48.dp, end = 15.dp, bottom = 12.dp),
                    )
                }
            }
        }
        Spacer(Modifier.height(16.dp))
        Text("Audiobook information", Modifier.fillMaxWidth(), fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(8.dp))
        Card(colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(14.dp)) {
            MetadataRow("Title", book.title)
            MetadataRow("Author", book.author ?: book.fingerprint.author ?: "Not provided")
            MetadataRow("Series", book.fingerprint.seriesTitle ?: "Not provided")
            MetadataRow("Series number", book.fingerprint.seriesNumber?.toString() ?: "Not provided")
            MetadataRow("Edition", book.fingerprint.editionType ?: "Not provided")
            MetadataRow("Format", book.fingerprint.fileType.uppercase())
            MetadataRow("Runtime", formatDuration(book.fingerprint.duration))
        }
    }
    if (chapterDialog) {
        AlertDialog(
            onDismissRequest = { chapterDialog = false }, title = { Text("Chapters") },
            text = { Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) { if (state.chapters.isEmpty()) Text("No chapters were found in this audiobook file.", color = ChoiceMuted); state.chapters.forEach { chapter -> TextButton(onClick = { player.seekToChapter(chapter); chapterDialog = false }) { Text(chapter.title) } } } },
            confirmButton = { TextButton(onClick = { chapterDialog = false }) { Text("Done") } },
        )
    }
    if (bookmarkDialog) {
        var bookmarkToDelete by remember { mutableStateOf<com.audiochoice.mobile.data.LibraryBookmark?>(null) }
        AlertDialog(
            onDismissRequest = { bookmarkDialog = false }, title = { Text("Bookmarks") },
            text = { Column { if (state.bookmarks.isEmpty()) Text("No bookmarks yet.", color = ChoiceMuted); state.bookmarks.forEach { bookmark -> TextButton(onClick = { player.seekToBookmark(bookmark); bookmarkDialog = false }) { Text(bookmark.title ?: "Bookmark") } } } },
            confirmButton = { TextButton(onClick = { bookmarkDialog = false }) { Text("Done") } },
        )
    }
    if (readingEditionSheet) {
        AlertDialog(
            onDismissRequest = { readingEditionSheet = false },
            title = { Text("Reading edition") },
            text = {
                Column {
                    Text(
                        "This audiobook has an EPUB attached. Open the player and tap the book " +
                            "icon to read along.",
                        color = ChoiceMuted,
                    )
                    Spacer(Modifier.height(14.dp))
                    TextButton(onClick = {
                        readingEditionSheet = false
                        player.syncReaderEditionNow()
                    }) { Text("Re-sync with the audiobook") }
                    TextButton(onClick = {
                        readingEditionSheet = false
                        epubLauncher.launch(
                            arrayOf("application/epub+zip", "application/octet-stream", "*/*"),
                        )
                    }) { Text("Replace EPUB") }
                    TextButton(onClick = {
                        readingEditionSheet = false
                        player.detachEpub()
                    }) { Text("Remove EPUB", color = MaterialTheme.colorScheme.error) }
                }
            },
            confirmButton = {
                TextButton(onClick = { readingEditionSheet = false }) { Text("Done") }
            },
        )
    }
    if (confirmDelete) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Delete audiobook?") },
            text = { Text("This removes the audiobook, progress, bookmarks, and filter choices from your account and this device. The shared scan remains available if you import this edition again.") },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
            confirmButton = { TextButton(onClick = { confirmDelete = false; onDelete() }) { Text("Delete", color = MaterialTheme.colorScheme.error) } },
        )
    }
}

@Composable
private fun DetailMetric(icon: ImageVector, value: String, label: String, onClick: (() -> Unit)? = null) {
    val modifier = Modifier
        .width(72.dp)
        // Was roughly 45dp tall, under the 48dp minimum touch target.
        .defaultMinSize(minHeight = 48.dp)
        .then(if (onClick == null) Modifier else Modifier.clickable(onClick = onClick))
        // Announce once as "Filters: 12 filter controls" rather than as an
        // unlabelled icon plus two disconnected text fragments.
        .semantics(mergeDescendants = true) { contentDescription = "$label: $value" }
    Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = modifier) {
        Icon(icon, null, tint = ChoiceGreen, modifier = Modifier.size(22.dp))
        Text(value, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, maxLines = 1)
        // 9sp is below a readable floor; 11sp still fits the 72dp column.
        Text(label, color = ChoiceMuted, fontSize = 11.sp, maxLines = 1)
    }
}

@Composable
private fun MetadataRow(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 15.dp, vertical = 11.dp)) {
        Text(label, Modifier.weight(1f), color = ChoiceMuted, fontSize = 12.sp)
        Text(value, Modifier.weight(1.6f), textAlign = TextAlign.End, fontSize = 12.sp)
    }
}

/**
 * Decodes a cover at roughly display resolution instead of full size.
 *
 * A 3000x3000 embedded cover decodes to about 36 MB as ARGB_8888, and this was
 * previously decoded on the main thread during composition with nothing ever
 * recycled -- the most likely source of an out-of-memory crash in the app.
 */
private fun decodeDownsampledCover(path: String, targetPixels: Int = 512): ImageBitmap? = runCatching {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeFile(path, bounds)
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return@runCatching null
    var sampleSize = 1
    while (bounds.outWidth / (sampleSize * 2) >= targetPixels &&
        bounds.outHeight / (sampleSize * 2) >= targetPixels
    ) {
        sampleSize *= 2
    }
    BitmapFactory.decodeFile(path, BitmapFactory.Options().apply { inSampleSize = sampleSize })
        ?.asImageBitmap()
}.getOrNull()

@Composable
private fun BookArtwork(
    coverPath: String?,
    modifier: Modifier = Modifier,
    isFinished: Boolean = false,
) {
    // produceState keeps the disk read and decode off the composition thread.
    val cover by produceState<ImageBitmap?>(initialValue = null, coverPath) {
        value = coverPath?.let { path ->
            withContext(Dispatchers.IO) { decodeDownsampledCover(path) }
        }
    }
    val artwork = cover
    Box(modifier.background(ChoiceSurface, RoundedCornerShape(16.dp)).clip(RoundedCornerShape(16.dp)), contentAlignment = Alignment.Center) {
        if (artwork != null) Image(artwork, "Book artwork", Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
        else Image(painterResource(R.drawable.audiochoice_logo), "AudioChoice artwork", Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
        if (isFinished) {
            // On a dark disc rather than relying on contrast with whatever artwork is
            // underneath, and labelled because a colour alone says nothing to TalkBack.
            Icon(
                Icons.Outlined.CheckCircle,
                "Finished",
                tint = ChoiceGreen,
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .padding(4.dp)
                    .background(Color.Black.copy(alpha = 0.75f), CircleShape)
                    .padding(1.dp)
                    .size(18.dp),
            )
        }
    }
}

@Composable
private fun DetailRow(icon: ImageVector, label: String, value: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .defaultMinSize(minHeight = 48.dp)
            .padding(15.dp)
            .semantics(mergeDescendants = true) { contentDescription = "$label: $value" },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(icon, null, tint = ChoiceGreen, modifier = Modifier.size(21.dp)); Spacer(Modifier.width(12.dp))
        Text(label, Modifier.weight(1f)); Text(value, color = if (label == "Filters") ChoiceGreen else ChoiceMuted, fontSize = 12.sp)
        Spacer(Modifier.width(6.dp)); Icon(Icons.Outlined.ChevronRight, null, tint = ChoiceMuted, modifier = Modifier.size(18.dp))
    }
}

@Composable
private fun BookFiltersScreen(player: PlayerViewModel, filtersLocked: Boolean, onBack: () -> Unit) {
    val state by player.state.collectAsStateWithLifecycle()
    val available = PlaybackFilterTaxonomy.available(state.scanEvents)
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().padding(18.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.Outlined.ArrowBack, "Back") }
            Text("Playback Filters", Modifier.weight(1f), textAlign = TextAlign.Center, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.size(48.dp))
        }
        Spacer(Modifier.height(12.dp))
        Card(colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(14.dp)) {
            Text(
                when {
                    state.filterAvailability == FilterAvailability.UNAVAILABLE ->
                        "This audiobook's filter data could not be loaded, so nothing is being filtered right now."
                    state.filterAvailability == FilterAvailability.CACHED ->
                        "Using the filter data saved on this device. Reconnect to check for an updated scan."
                    filtersLocked -> "Parental Controls are on. Filter choices are visible but locked."
                    else -> "Only filters detected in this audiobook are shown. Everything starts on; changes apply only to this book."
                },
                color = if (state.filterAvailability == FilterAvailability.UNAVAILABLE) {
                    MaterialTheme.colorScheme.error
                } else ChoiceMuted,
                fontSize = 12.sp,
                modifier = Modifier.padding(16.dp),
            )
            if (available.isEmpty()) {
                HorizontalDivider(color = ChoiceOutline)
                Text(
                    when (state.filterAvailability) {
                        FilterAvailability.LOADING -> "Loading this audiobook's filters…"
                        FilterAvailability.UNAVAILABLE ->
                            "No filter data is available for this audiobook yet. It may still be scanning, " +
                                "or AudioChoice could not reach the scan service. Playback is not filtered until it loads."
                        else -> "No filterable content was detected in this audiobook."
                    },
                    color = ChoiceMuted,
                    modifier = Modifier.padding(18.dp),
                )
            }
            FilterHierarchyContent(player, available, Modifier.weight(1f), filtersLocked)
        }
    }
}

@Composable
private fun FilterHierarchyContent(
    player: PlayerViewModel,
    available: List<com.audiochoice.mobile.player.PlaybackFilterParent>,
    modifier: Modifier = Modifier,
    filtersLocked: Boolean = false,
) {
    val state by player.state.collectAsStateWithLifecycle()
    var expandedParent by remember { mutableStateOf<String?>(null) }
    var expandedChild by remember { mutableStateOf<String?>(null) }
    Column(modifier.fillMaxWidth().verticalScroll(rememberScrollState())) {
        available.forEach { parent ->
            HorizontalDivider(color = ChoiceOutline)
            Row(
                Modifier.fillMaxWidth().clickable {
                    expandedParent = if (expandedParent == parent.id) null else parent.id
                }.padding(14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(Icons.Outlined.Shield, null, tint = ChoiceGreen)
                Spacer(Modifier.width(10.dp))
                Text(if (expandedParent == parent.id) "⌄" else "›", color = ChoiceMuted)
                Spacer(Modifier.width(8.dp))
                Column(Modifier.weight(1f)) {
                    Text(parent.label)
                    Text("${parent.children.sumOf { it.events.size }} filter controls", color = ChoiceMuted, fontSize = 11.sp)
                }
                Switch(
                    checked = player.isCategoryEnabled(parent.id),
                    onCheckedChange = { player.setFilterCategory(parent.id, it) },
                    enabled = !filtersLocked,
                    // A bare Switch announces only "on"/"off" with no indication
                    // of which filter it belongs to.
                    modifier = Modifier.semantics {
                        contentDescription = "Filter ${parent.label}"
                        stateDescription =
                            if (player.isCategoryEnabled(parent.id)) "Filtering" else "Not filtering"
                    },
                )
            }
            if (expandedParent == parent.id) parent.children.forEach { child ->
                Row(
                    Modifier.fillMaxWidth().clickable {
                        expandedChild = if (expandedChild == child.id) null else child.id
                    }.padding(start = 48.dp, end = 14.dp, top = 8.dp, bottom = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(if (expandedChild == child.id) "⌄" else "›", color = ChoiceMuted)
                    Spacer(Modifier.width(8.dp))
                    Column(Modifier.weight(1f)) {
                        Text(child.label, fontSize = 13.sp)
                        Text("${child.events.size} controls", color = ChoiceMuted, fontSize = 10.sp)
                    }
                    Switch(
                        checked = player.isGroupEnabled(child.id),
                        onCheckedChange = { player.setFilterGroup(child.id, it) },
                        enabled = !filtersLocked,
                        modifier = Modifier.semantics {
                            contentDescription = "Filter ${child.label}"
                            stateDescription =
                                if (player.isGroupEnabled(child.id)) "Filtering" else "Not filtering"
                        },
                    )
                }
                if (expandedChild == child.id) child.events.forEach { event ->
                    Row(
                        Modifier.fillMaxWidth().padding(start = 76.dp, end = 14.dp, top = 5.dp, bottom = 5.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(
                                event.label,
                                fontSize = 12.sp,
                            )
                            event.startTime?.let {
                                Text(formatTime((it * 1000).toLong()), color = ChoiceMuted, fontSize = 10.sp)
                            }
                        }
                        // Reporting a control is what makes over-filtering fixable: it names
                        // the thing that fired, rather than leaving a timestamp to be matched
                        // back to one. Switching it off only helps this listener; reporting it
                        // helps everyone with the same recording.
                        IconButton(
                            onClick = { player.reportWronglyFiltered(event.key, event.aggregate) },
                            modifier = Modifier.size(34.dp),
                        ) {
                            Icon(
                                Icons.Outlined.OutlinedFlag,
                                "Report ${event.label} as wrongly filtered",
                                tint = ChoiceMuted,
                                modifier = Modifier.size(17.dp),
                            )
                        }
                        Switch(
                            checked = player.isFilterEventEnabled(event),
                            onCheckedChange = { player.setFilterEvent(event, it) },
                            enabled = !filtersLocked,
                            modifier = Modifier.semantics {
                                contentDescription = "Filter ${event.label}"
                                stateDescription = if (player.isFilterEventEnabled(event)) {
                                    "Filtering"
                                } else "Not filtering"
                            },
                        )
                    }
                }
            }
        }
    }
}

private fun formatDuration(seconds: Double?): String {
    if (seconds == null || seconds <= 0) return "—"
    val value = seconds.toLong(); return "%dh %02dm".format(value / 3600, (value % 3600) / 60)
}


private fun com.audiochoice.mobile.player.PlayerUiState.resultVersion(): String = when {
    // Never report "Clean" when the scan simply could not be loaded -- that
    // reads as "nothing to filter" while nothing is actually being filtered.
    filterAvailability == FilterAvailability.LOADING -> "Checking…"
    filterAvailability == FilterAvailability.UNAVAILABLE -> "Unavailable"
    scanEvents.isEmpty() -> "Clean"
    else -> "${PlaybackFilterTaxonomy.controlCount(scanEvents)} filter controls"
}

@Composable
private fun PlayerScreen(
    player: PlayerViewModel,
    filtersLocked: Boolean,
    onBack: (() -> Unit)? = null,
    /**
     * Scans this book again, where its file is still on the device to scan.
     *
     * Null when there is no local file, since offering to scan one that is not there would fail after
     * the listener had already committed to waiting for it.
     */
    onRescan: (() -> Unit)? = null,
) {
    val state by player.state.collectAsStateWithLifecycle()
    val book = state.book ?: return
    var speedMenu by remember { mutableStateOf(false) }
    var sleepMenu by remember { mutableStateOf(false) }
    var bookmarkDialog by remember { mutableStateOf(false) }
    var filterDialog by remember { mutableStateOf(false) }
    var chapterDialog by remember { mutableStateOf(false) }
    var readerMode by remember(book.id) { mutableStateOf(false) }
    var isChapterScrubbing by remember(book.id) { mutableStateOf(false) }
    var chapterScrubPositionMs by remember(book.id) { mutableFloatStateOf(state.positionMs.toFloat()) }
    val currentChapter = state.chapters.firstOrNull {
        state.positionMs / 1000.0 >= it.startSeconds && state.positionMs / 1000.0 < it.endSeconds
    }
    LaunchedEffect(state.positionMs, currentChapter, isChapterScrubbing) {
        if (!isChapterScrubbing) chapterScrubPositionMs = state.positionMs.toFloat()
    }
    // Reading mode is a full-screen surface rather than a panel inside the player,
    // so it delegates entirely instead of threading conditionals through the
    // player layout below.
    if (BuildConfig.BETA_BUILD && readerMode && state.epubText != null) {
        ReaderScreen(
            player = player,
            state = state,
            onCloseReader = { readerMode = false },
        )
        return
    }
    Column(
        Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().padding(horizontal = 8.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Row(
            Modifier.fillMaxWidth().offset(y = (-38).dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = { onBack?.invoke() }) {
                Icon(Icons.Outlined.KeyboardArrowDown, "Book details")
            }
            if (BuildConfig.BETA_BUILD) {
                val readerEpubLauncher = rememberLauncherForActivityResult(
                    ActivityResultContracts.OpenDocument(),
                ) { uri -> uri?.let(player::attachEpub) }
                IconButton(
                    onClick = {
                        when {
                            state.epubText != null -> readerMode = !readerMode
                            // No EPUB yet: the icon's other job is attaching one, so a tap
                            // here opens the file picker instead of doing nothing. Mirrors
                            // the dual behaviour iOS's reader icon already has.
                            else -> readerEpubLauncher.launch(
                                arrayOf("application/epub+zip", "application/octet-stream", "*/*"),
                            )
                        }
                    },
                ) {
                    // Open book invites opening the reader; closed book invites returning
                    // to the player; no book yet invites attaching one.
                    Icon(
                        when {
                            readerMode -> Icons.Outlined.Book
                            state.epubText != null -> Icons.Outlined.MenuBook
                            else -> Icons.Outlined.LibraryAdd
                        },
                        when {
                            readerMode -> "Close reading edition"
                            state.epubText != null -> "Open reading edition"
                            else -> "Attach a reading edition"
                        },
                        tint = ChoiceGreen,
                    )
                }
            } else {
                Icon(Icons.Outlined.GraphicEq, "AudioChoice", tint = ChoiceGreen)
            }
            Box {
                IconButton(onClick = { sleepMenu = true }) { Icon(Icons.Outlined.Timer, "Sleep timer") }
                DropdownMenu(expanded = sleepMenu, onDismissRequest = { sleepMenu = false }) {
                    if (currentChapter != null) {
                        DropdownMenuItem(
                            text = { Text("End of chapter") },
                            onClick = { player.sleepAtEndOfChapter(); sleepMenu = false },
                        )
                        HorizontalDivider()
                    }
                    listOf(15, 30, 45, 60).forEach { minutes ->
                        DropdownMenuItem(
                            text = { Text("$minutes minutes") },
                            onClick = { player.setSleepTimer(minutes); sleepMenu = false },
                        )
                    }
                    DropdownMenuItem(
                        text = { Text("Turn off") },
                        onClick = { player.setSleepTimer(null); sleepMenu = false },
                    )
                }
            }
        }
        Spacer(Modifier.height(0.dp))
        Text(
            book.title,
            color = ChoiceGreen,
            fontSize = 20.sp,
            maxLines = 2,
            textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth(.86f).offset(y = (-14).dp),
        )
        Spacer(Modifier.height(10.dp))
        BookArtwork(state.coverPath, Modifier.fillMaxWidth(.86f).aspectRatio(1f))
        Spacer(Modifier.height(12.dp))
        // Matches iOS, which puts the chapter between the cover and the scrubber and falls back
        // to "Audiobook" when a file carries no chapter marks. Held to one line because this
        // column does not scroll: a wrapped chapter title would push the transport controls off
        // the bottom edge on a small screen, which is the failure a listener just reported on
        // the other platform.
        Text(
            currentChapter?.title?.takeIf { it.isNotBlank() } ?: "Audiobook",
            color = ChoiceMuted,
            fontSize = 15.sp,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center,
            modifier = Modifier.fillMaxWidth(.86f),
        )
        Spacer(Modifier.height(8.dp))
        // Filtering silently doing nothing is worse than an explicit warning:
        // the listener would otherwise assume their filters were active.
        if (state.filterAvailability == FilterAvailability.UNAVAILABLE) {
            Card(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 4.dp),
                colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
            ) {
                Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(
                        Icons.Outlined.Warning,
                        null,
                        tint = MaterialTheme.colorScheme.error,
                        modifier = Modifier.size(20.dp),
                    )
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Text(
                            "Filters are not active. This audiobook's scan could not be loaded.",
                            color = MaterialTheme.colorScheme.error,
                            fontSize = 12.sp,
                        )
                        // Named as an action rather than a retry: for an edition nobody has scanned
                        // before, this is the first attempt rather than a second one.
                        if (onRescan != null) {
                            TextButton(
                                onClick = onRescan,
                                contentPadding = PaddingValues(horizontal = 0.dp, vertical = 2.dp),
                            ) {
                                Text("Scan this audiobook", fontSize = 12.sp)
                            }
                        }
                    }
                }
            }
            Spacer(Modifier.height(10.dp))
        }
        val chapterIndex = state.chapters.indexOf(currentChapter).takeIf { it >= 0 }
        if (state.localUri == null) {
            Card(colors = CardDefaults.cardColors(containerColor = ChoiceSurface)) {
                Text(
                    "Reimport the audio file on this device to enable playback. Your progress and scan remain saved.",
                    modifier = Modifier.padding(18.dp),
                    color = ChoiceMuted,
                    textAlign = TextAlign.Center,
                )
            }
        } else {
            val durationMs = state.durationMs.coerceAtLeast(1L)
            val displayedPositionMs = if (isChapterScrubbing) chapterScrubPositionMs.toLong() else state.positionMs
            CompositionLocalProvider(LocalMinimumInteractiveComponentSize provides 0.dp) {
                Slider(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp)
                    .height(18.dp)
                    // Without this a screen reader announces a bare percentage,
                    // which is meaningless for a multi-hour audiobook.
                    .semantics {
                        contentDescription = "Playback position"
                        stateDescription =
                            "${formatTime(displayedPositionMs.coerceIn(0L, durationMs))} " +
                                "of ${formatTime(durationMs)}"
                    },
                value = (if (isChapterScrubbing) chapterScrubPositionMs else state.positionMs.toFloat())
                    .coerceIn(0f, durationMs.toFloat()),
                onValueChange = {
                    isChapterScrubbing = true
                    chapterScrubPositionMs = it
                },
                onValueChangeFinished = {
                    val target = chapterScrubPositionMs.toLong()
                    isChapterScrubbing = false
                    player.seekTo(target)
                },
                valueRange = 0f..durationMs.toFloat(),
                colors = SliderDefaults.colors(
                    thumbColor = ChoiceGreen,
                    activeTrackColor = ChoiceGreen,
                    inactiveTrackColor = ChoiceOutline,
                ),
                )
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text(formatTime(displayedPositionMs.coerceIn(0L, durationMs)), color = ChoiceMuted, fontSize = 12.sp)
                // Real time left, not the book's remaining length: at 1.5x this answers when the
                // listener will finish rather than counting the same figure down faster.
                Text(
                    "-${formatTime(
                        ListeningTime.remainingRealMs(
                            remainingBookMs = (durationMs - displayedPositionMs).coerceAtLeast(0),
                            speed = state.speed,
                        ),
                    )}",
                    color = ChoiceMuted,
                    fontSize = 12.sp,
                )
            }
            Spacer(Modifier.height(24.dp))
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 40.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                IconButton(onClick = { player.skip(-30) }) { Icon(Icons.Outlined.Replay30, "Back 30 seconds", modifier = Modifier.size(38.dp)) }
                Button(onClick = player::toggle, modifier = Modifier.size(68.dp), shape = RoundedCornerShape(50), contentPadding = PaddingValues(0.dp)) {
                    Icon(if (state.isPlaying) Icons.Outlined.Pause else Icons.Outlined.PlayArrow, if (state.isPlaying) "Pause" else "Play", modifier = Modifier.size(34.dp))
                }
                IconButton(onClick = { player.skip(30) }) { Icon(Icons.Outlined.Forward30, "Forward 30 seconds", modifier = Modifier.size(38.dp)) }
            }
            Spacer(Modifier.height(14.dp))
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 10.dp).offset(y = 18.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Box {
                    PlayerToolButton(formatSpeed(state.speed), "Speed") { speedMenu = true }
                    DropdownMenu(expanded = speedMenu, onDismissRequest = { speedMenu = false }) {
                        listOf(.25f, .5f, .75f, 1f, 1.25f, 1.5f, 1.75f, 2f).forEach { speed ->
                            DropdownMenuItem(
                                text = { Text(formatSpeed(speed)) },
                                onClick = { player.setSpeed(speed); speedMenu = false },
                                trailingIcon = { if (state.speed == speed) Text("✓", color = ChoiceGreen) },
                            )
                        }
                    }
                }
                IconPlayerToolButton(Icons.Outlined.QueueMusic, "Chapters") { chapterDialog = true }
                IconPlayerToolButton(Icons.Outlined.Security, "Filters") { filterDialog = true }
                IconPlayerToolButton(if (state.bookmarkSaved) Icons.Outlined.BookmarkAdded else Icons.Outlined.BookmarkBorder, "Bookmarks") {
                    bookmarkDialog = true
                }
                // One tap, no dialog. Someone hearing something they asked never to hear is
                // usually driving or walking, and anything that needs reading first means the
                // report never happens.
                IconPlayerToolButton(
                    if (state.filterReportSent) Icons.Outlined.Flag else Icons.Outlined.OutlinedFlag,
                    "Report missed content",
                ) {
                    player.reportMissedContent()
                }
            }
            if (state.filterReportSent) {
                Text(
                    "Reported at ${formatTime(state.positionMs)}. Thank you.",
                    color = ChoiceGreen,
                    fontSize = 11.sp,
                    modifier = Modifier.padding(top = 22.dp),
                )
                LaunchedEffect(state.filterReportSent) {
                    delay(2_500)
                    player.acknowledgeFilterReport()
                }
            }
        }
    }
    if (state.filterReportRefinementPending) {
        var selectedCategory by remember { mutableStateOf<com.audiochoice.mobile.data.FilterReportCategory?>(null) }
        var selectedTimeframe by remember {
            mutableStateOf(com.audiochoice.mobile.data.FilterReportTimeframe.JUST_THIS_MOMENT)
        }
        AlertDialog(
            // Already saved by the time this shows, so dismissing it loses nothing.
            onDismissRequest = { player.dismissMissedContentRefinement() },
            title = { Text("What did you hear?") },
            text = {
                Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) {
                    Text(
                        "Optional. Your report at ${formatTime(state.positionMs)} is already saved.",
                        color = ChoiceMuted,
                        fontSize = 12.sp,
                    )
                    Spacer(Modifier.height(12.dp))
                    Text("Category", fontSize = 12.sp, color = ChoiceMuted)
                    com.audiochoice.mobile.data.FilterReportCategory.entries.forEach { category ->
                        Row(
                            Modifier.fillMaxWidth()
                                .clickable { selectedCategory = category }
                                .padding(vertical = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(selected = selectedCategory == category, onClick = { selectedCategory = category })
                            Text(category.label)
                        }
                    }
                    Spacer(Modifier.height(8.dp))
                    Text("How far back", fontSize = 12.sp, color = ChoiceMuted)
                    com.audiochoice.mobile.data.FilterReportTimeframe.entries.forEach { timeframe ->
                        Row(
                            Modifier.fillMaxWidth()
                                .clickable { selectedTimeframe = timeframe }
                                .padding(vertical = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(selected = selectedTimeframe == timeframe, onClick = { selectedTimeframe = timeframe })
                            Text(timeframe.label)
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    player.refineMissedContentReport(selectedCategory?.categoryID, selectedTimeframe.seconds)
                }) { Text("Submit") }
            },
            dismissButton = {
                TextButton(onClick = { player.dismissMissedContentRefinement() }) { Text("Skip") }
            },
        )
    }
    if (chapterDialog) {
        AlertDialog(
            onDismissRequest = { chapterDialog = false },
            title = { Text("Chapters") },
            text = {
                Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) {
                    if (state.chapters.isEmpty()) Text("No chapters were found in this audiobook file.", color = ChoiceMuted)
                    state.chapters.forEachIndexed { index, chapter ->
                        TextButton(
                            onClick = { player.seekToChapter(chapter); chapterDialog = false },
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text("${index + 1}", color = ChoiceMuted, modifier = Modifier.width(34.dp))
                            Text(chapter.title, Modifier.weight(1f), textAlign = TextAlign.Start)
                            Text(formatTime((chapter.startSeconds * 1000).toLong()), color = ChoiceMuted, fontSize = 12.sp)
                        }
                    }
                }
            },
            confirmButton = { TextButton(onClick = { chapterDialog = false }) { Text("Done") } },
        )
    }
    if (bookmarkDialog) {
        var bookmarkToDelete by remember { mutableStateOf<com.audiochoice.mobile.data.LibraryBookmark?>(null) }
        AlertDialog(
            onDismissRequest = { bookmarkDialog = false },
            title = { Text("Bookmarks") },
            text = {
                Column {
                    if (state.bookmarks.isEmpty()) Text("No bookmarks yet.", color = ChoiceMuted)
                    state.bookmarks.forEach { bookmark ->
                        Row(
                            Modifier.fillMaxWidth().combinedClickable(
                                onClick = { player.seekToBookmark(bookmark); bookmarkDialog = false },
                                onLongClick = { bookmarkToDelete = bookmark },
                            ).padding(vertical = 4.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(
                                bookmark.title ?: formatTime((bookmark.positionSeconds * 1000).toLong()),
                                modifier = Modifier.weight(1f),
                            )
                        }
                    }
                    Spacer(Modifier.height(8.dp))
                    OutlinedButton(onClick = { player.addBookmark() }, modifier = Modifier.fillMaxWidth()) {
                        Icon(Icons.Outlined.Add, null)
                        Spacer(Modifier.width(6.dp))
                        Text("Add bookmark")
                    }
                }
            },
            confirmButton = { TextButton(onClick = { bookmarkDialog = false }) { Text("Done") } },
        )
        bookmarkToDelete?.let { bookmark ->
            AlertDialog(
                onDismissRequest = { bookmarkToDelete = null },
                title = { Text("Delete bookmark?") },
                text = { Text("Remove this bookmark?") },
                dismissButton = { TextButton(onClick = { bookmarkToDelete = null }) { Text("Cancel") } },
                confirmButton = {
                    TextButton(onClick = { player.deleteBookmark(bookmark); bookmarkToDelete = null }) { Text("Delete") }
                },
            )
        }
    }
    if (filterDialog) {
        val availableFilters = PlaybackFilterTaxonomy.available(state.scanEvents)
        AlertDialog(
            onDismissRequest = { filterDialog = false },
            title = { Text("Playback Filters") },
            text = {
                Column {
                    if (filtersLocked) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Outlined.Lock, null, tint = ChoiceGreen, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(8.dp))
                            Text("Locked by Parental Controls", color = ChoiceMuted, fontSize = 13.sp)
                        }
                        Spacer(Modifier.height(10.dp))
                    }
                    if (availableFilters.isEmpty()) {
                        Text("No filterable content was detected in this audiobook.", color = ChoiceMuted)
                    } else {
                        Text("Only categories detected in this audiobook are shown.", color = ChoiceMuted, fontSize = 13.sp)
                        FilterHierarchyContent(player, availableFilters, Modifier.heightIn(max = 480.dp), filtersLocked)
                    }
                }
            },
            confirmButton = { TextButton(onClick = { filterDialog = false }) { Text("Done") } },
        )
    }
}

/**
 * The timing API returns character ranges rather than transcript content.  Within
 * a matched spoken segment we brighten the current word; playback speed needs no
 * separate calculation because ExoPlayer's position remains audio-time based.
 */
/** Resolved reader colours. Kept in the UI layer so ReaderSettings stays Compose-free. */
private data class ReaderPalette(val paper: Color, val ink: Color, val mutedInk: Color)

private fun readerPalette(theme: ReaderTheme): ReaderPalette = when (theme) {
    ReaderTheme.LIGHT -> ReaderPalette(Color(0xFFFFFFFF), Color(0xFF1A1A1A), Color(0xFF5F5F5F))
    // The paper tone the reader originally shipped with.
    ReaderTheme.SEPIA -> ReaderPalette(Color(0xFFF8F4E8), Color(0xFF201C16), Color(0xFF5E574A))
    ReaderTheme.DARK -> ReaderPalette(Color(0xFF14171A), Color(0xFFE3E3E3), Color(0xFF9AA0A6))
}

private const val READER_BASE_FONT_SP = 19f
private const val READER_BASE_LINE_SP = 30f
private const val READER_BASE_MARGIN_DP = 22f

/**
 * Full-screen reading edition: continuous scroll, filtered passages removed, and
 * the transport controls moved to the bottom of the screen so the text gets the
 * whole body.
 */
@Composable
private fun ReaderScreen(
    player: PlayerViewModel,
    state: PlayerUiState,
    onCloseReader: () -> Unit,
) {
    val epubText = state.epubText ?: return
    val settings = state.readerSettings
    val palette = readerPalette(settings.theme)
    var settingsSheet by remember { mutableStateOf(false) }
    // Restore where the listener stopped. Keyed on the book so switching books
    // does not carry one book's anchor into another.
    val listState = rememberLazyListState(
        initialFirstVisibleItemIndex = state.readerPosition.paragraphIndex,
        initialFirstVisibleItemScrollOffset = state.readerPosition.scrollOffset,
    )

    // Recomputed only when the text or the enabled filter set actually changes,
    // never per scroll frame.
    val masks = remember(
        epubText,
        state.scanEvents,
        state.readerTimingRanges,
        state.disabledCategoryIDs,
        state.disabledGroupIDs,
        state.disabledEventKeys,
        state.disabledAggregateKeys,
    ) { readerMaskRanges(state, epubText) }
    val displayParagraphs = remember(state.readerParagraphs, masks) {
        readerDisplayParagraphs(state.readerParagraphs, masks)
    }
    val removedCount = remember(displayParagraphs) {
        displayParagraphs.sumOf { it.removedPassages }
    }

    // Closing the reader with the system back gesture should feel like the icon.
    BackHandler(onBack = onCloseReader)

    // Persist the anchor as it changes rather than on every scroll frame.
    LaunchedEffect(listState, state.book?.id) {
        snapshotFlow { listState.firstVisibleItemIndex to listState.firstVisibleItemScrollOffset }
            .collect { (index, offset) -> player.saveReaderPosition(index, offset) }
    }

    // Which paragraph the narration is currently in. Null across an alignment
    // gap, and `narratedIndex` deliberately keeps its previous value in that case
    // rather than snapping the highlight back to the start of the book.
    var narratedIndex by remember(state.book?.id) { mutableIntStateOf(-1) }
    if (settings.followAudio) {
        // Alignment first. Without it, a proportion of the way through the text, which is wrong by
        // pages and still far better than the title page ten hours into a book. Alignment is a
        // separate request from the filter scan and can fail on its own, so a book with working
        // filters and no read-along timings is an ordinary state rather than a broken one.
        val narratedCharacter = remember(
            state.positionMs,
            state.readerTimingRanges,
            state.durationMs,
            displayParagraphs,
        ) {
            val seconds = state.positionMs / 1000.0
            readerCharacterForTime(state.readerTimingRanges, seconds)
                ?: approximateReaderCharacter(
                    seconds = seconds,
                    durationSeconds = state.durationMs / 1000.0,
                    characterCount = displayParagraphs.lastOrNull()?.paragraph?.endCharacter ?: 0,
                )
        }
        // Hoisted so the lookup list is not rebuilt on every position tick.
        val sourceParagraphs = remember(displayParagraphs) {
            displayParagraphs.map { it.paragraph }
        }
        LaunchedEffect(narratedCharacter, sourceParagraphs) {
            val character = narratedCharacter ?: return@LaunchedEffect
            val index = sourceParagraphs.indexOfCharacter(character)
            if (index >= 0) narratedIndex = index
        }
        // Only scroll when the narrated paragraph is off screen, and never while
        // the listener is scrolling themselves.
        LaunchedEffect(narratedIndex, listState.isScrollInProgress) {
            if (narratedIndex < 0 || listState.isScrollInProgress) return@LaunchedEffect
            val visible = listState.layoutInfo.visibleItemsInfo
            val onScreen = visible.any { it.index == narratedIndex }
            if (!onScreen) {
                runCatching { listState.animateScrollToItem(narratedIndex) }
            }
        }
    }

    Column(
        Modifier
            .fillMaxSize()
            .background(palette.paper)
            .statusBarsPadding()
            .navigationBarsPadding(),
    ) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            // The title sits on the left, but the close-book button is centred so
            // it lands on the exact spot the player's open-book button occupies.
            // Toggling between player and reader then leaves the icon still
            // instead of making it jump across the bar. Equal weights on the two
            // side slots are what put the middle button at the true centre, the
            // same way the player's SpaceBetween row does with its 48dp buttons.
            Column(Modifier.weight(1f)) {
                Text(
                    state.book?.title.orEmpty(),
                    color = palette.ink,
                    fontSize = 13.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    if (removedCount > 0) "Reading edition · $removedCount filtered passages removed"
                    else "Reading edition",
                    color = palette.mutedInk,
                    fontSize = 11.sp,
                    // The left slot is now half the bar, so cap the wrap instead
                    // of letting a long count grow the header.
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            IconButton(onClick = onCloseReader) {
                Icon(Icons.Outlined.Book, "Close reading edition", tint = ChoiceGreen)
            }
            Row(Modifier.weight(1f), horizontalArrangement = Arrangement.End) {
                IconButton(onClick = { settingsSheet = true }) {
                    Icon(Icons.Outlined.TextFields, "Reading settings", tint = ChoiceGreen)
                }
            }
        }
        HorizontalDivider(color = palette.mutedInk.copy(alpha = .2f))
        if (displayParagraphs.isEmpty()) {
            Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
                Text(
                    "This reading edition has no readable text.",
                    color = palette.mutedInk,
                    fontSize = 14.sp,
                )
            }
        } else {
            LazyColumn(
                state = listState,
                modifier = Modifier.weight(1f).fillMaxWidth(),
                contentPadding = PaddingValues(
                    horizontal = (READER_BASE_MARGIN_DP * settings.marginScale).dp,
                    vertical = 16.dp,
                ),
            ) {
                items(
                    count = displayParagraphs.size,
                    key = { index -> displayParagraphs[index].paragraph.startCharacter },
                ) { index ->
                    val display = displayParagraphs[index]
                    val isNarrated = settings.followAudio && index == narratedIndex
                    Text(
                        display.displayText,
                        color = palette.ink,
                        fontFamily = readerFontFamily(settings.font),
                        fontSize = (READER_BASE_FONT_SP * settings.fontScale).sp,
                        lineHeight = (
                            READER_BASE_LINE_SP * settings.fontScale *
                                ReaderSettings.lineHeightFactor(settings.font)
                            ).sp,
                        modifier = Modifier
                            .fillMaxWidth()
                            .then(
                                if (isNarrated) {
                                    Modifier.background(
                                        ChoiceGreen.copy(alpha = .16f),
                                        RoundedCornerShape(6.dp),
                                    )
                                } else Modifier,
                            )
                            .then(
                                if (settings.followAudio) {
                                    Modifier
                                        .clickable(
                                            // Names the gesture, which is otherwise
                                            // undiscoverable, and marks the passage
                                            // currently being narrated.
                                            onClickLabel = "Play the audiobook from here",
                                        ) {
                                            // Seek to wherever this paragraph begins in
                                            // the audio, falling forward across gaps.
                                            readerTimeForCharacter(
                                                state.readerTimingRanges,
                                                display.paragraph.startCharacter,
                                            )?.let { seconds ->
                                                player.seekTo((seconds * 1000).toLong())
                                            }
                                        }
                                        .then(
                                            if (isNarrated) {
                                                Modifier.semantics {
                                                    stateDescription = "Now being narrated"
                                                }
                                            } else Modifier,
                                        )
                                } else Modifier,
                            )
                            .padding(horizontal = 4.dp, vertical = 2.dp)
                            .padding(bottom = 12.dp),
                    )
                }
            }
        }
        HorizontalDivider(color = palette.mutedInk.copy(alpha = .2f))
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 40.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            IconButton(onClick = { player.skip(-30) }) {
                Icon(Icons.Outlined.Replay30, "Back 30 seconds", tint = palette.ink, modifier = Modifier.size(34.dp))
            }
            Button(
                onClick = player::toggle,
                modifier = Modifier.size(60.dp),
                shape = RoundedCornerShape(50),
                contentPadding = PaddingValues(0.dp),
            ) {
                Icon(
                    if (state.isPlaying) Icons.Outlined.Pause else Icons.Outlined.PlayArrow,
                    if (state.isPlaying) "Pause" else "Play",
                    modifier = Modifier.size(30.dp),
                )
            }
            IconButton(onClick = { player.skip(30) }) {
                Icon(Icons.Outlined.Forward30, "Forward 30 seconds", tint = palette.ink, modifier = Modifier.size(34.dp))
            }
        }
    }

    if (settingsSheet) {
        ReaderSettingsDialog(
            settings = settings,
            onSettingsChanged = player::updateReaderSettings,
            onDismiss = { settingsSheet = false },
        )
    }
}

/**
 * Lets a listener correct details AudioChoice had to guess.
 *
 * Worth having because a file with no tags leaves the title derived from its
 * filename, and the import screen says so without offering any way to put it right.
 */
@Composable
private fun BookDetailsEditDialog(
    book: LibraryBook,
    onDismiss: () -> Unit,
    onSave: (title: String, author: String?, narrator: String?) -> Unit,
) {
    var title by rememberSaveable(book.id) { mutableStateOf(book.title) }
    var author by rememberSaveable(book.id) { mutableStateOf(book.author.orEmpty()) }
    var narrator by rememberSaveable(book.id) { mutableStateOf(book.narrator.orEmpty()) }
    val titleIsValid = title.isNotBlank() && title.length <= MAXIMUM_BOOK_DETAIL_LENGTH
    val everythingFits = author.length <= MAXIMUM_BOOK_DETAIL_LENGTH &&
        narrator.length <= MAXIMUM_BOOK_DETAIL_LENGTH

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Outlined.Edit, null, tint = ChoiceGreen) },
        title = { Text("Edit details") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(
                    "Changes what you see in your library. It does not change how this " +
                        "audiobook is matched to its filters or reading edition.",
                    color = ChoiceMuted,
                    fontSize = 13.sp,
                )
                OutlinedTextField(
                    value = title,
                    onValueChange = { title = it },
                    label = { Text("Title") },
                    singleLine = true,
                    isError = !titleIsValid,
                    supportingText = if (title.isBlank()) {
                        { Text("A title is required.") }
                    } else if (title.length > MAXIMUM_BOOK_DETAIL_LENGTH) {
                        { Text("Titles are limited to $MAXIMUM_BOOK_DETAIL_LENGTH characters.") }
                    } else {
                        null
                    },
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = author,
                    onValueChange = { author = it },
                    label = { Text("Author") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = narrator,
                    onValueChange = { narrator = it },
                    label = { Text("Narrator") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        },
        confirmButton = {
            TextButton(
                enabled = titleIsValid && everythingFits,
                onClick = { onSave(title, author, narrator) },
            ) { Text("Save") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

/** Matches the varchar(300) columns these values are stored in. */
private const val MAXIMUM_BOOK_DETAIL_LENGTH = 300

/** Far enough in that calling it a resume is honest rather than pedantic. */
private const val RESUMABLE_POSITION_MS = 5_000L

@Composable
private fun ReaderSettingsDialog(
    settings: ReaderSettings,
    onSettingsChanged: (ReaderSettings) -> Unit,
    onDismiss: () -> Unit,
    // Defaulted to the audiobook wording, so the existing call site is unchanged. A narrated book
    // has no audiobook to follow; what the switch tracks there is a synthetic voice.
    followLabel: String = "Follow the audiobook",
    followDescription: String =
        "Highlights and scrolls to the passage being narrated, and lets you " +
            "tap a paragraph to jump the audio there.",
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Reading settings") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text("Text size", fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                Spacer(Modifier.height(6.dp))
                ReaderOptionRow(
                    options = ReaderSettings.FONT_SCALES,
                    selected = settings.fontScale,
                    label = ReaderSettings::fontScaleLabel,
                    onSelect = { onSettingsChanged(settings.copy(fontScale = it)) },
                )
                Spacer(Modifier.height(16.dp))
                Text("Typeface", fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                Spacer(Modifier.height(6.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    ReaderFont.entries.forEach { font ->
                        FilterChip(
                            selected = settings.font == font,
                            onClick = { onSettingsChanged(settings.copy(font = font)) },
                            // Each chip renders in the face it selects, so the choice
                            // can be judged by eye rather than by name.
                            label = {
                                Text(
                                    ReaderSettings.fontLabel(font),
                                    fontFamily = readerFontFamily(font),
                                )
                            },
                        )
                    }
                }
                Spacer(Modifier.height(4.dp))
                Text(
                    "OpenDyslexic weights the bottom of each letter and varies similar " +
                        "shapes, which can make characters harder to transpose or flip.",
                    color = ChoiceMuted,
                    fontSize = 11.sp,
                )
                Spacer(Modifier.height(16.dp))
                Text("Margins", fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                Spacer(Modifier.height(6.dp))
                ReaderOptionRow(
                    options = ReaderSettings.MARGIN_SCALES,
                    selected = settings.marginScale,
                    label = ReaderSettings::marginScaleLabel,
                    onSelect = { onSettingsChanged(settings.copy(marginScale = it)) },
                )
                Spacer(Modifier.height(16.dp))
                Text("Theme", fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                Spacer(Modifier.height(6.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    ReaderTheme.entries.forEach { theme ->
                        FilterChip(
                            selected = settings.theme == theme,
                            onClick = { onSettingsChanged(settings.copy(theme = theme)) },
                            label = { Text(theme.name.lowercase().replaceFirstChar(Char::uppercase)) },
                        )
                    }
                }
                Spacer(Modifier.height(20.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(followLabel, fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                        Text(followDescription, color = ChoiceMuted, fontSize = 11.sp)
                    }
                    Spacer(Modifier.width(10.dp))
                    Switch(
                        checked = settings.followAudio,
                        onCheckedChange = { onSettingsChanged(settings.copy(followAudio = it)) },
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Done") } },
    )
}

@Composable
private fun ReaderOptionRow(
    options: List<Float>,
    selected: Float,
    label: (Float) -> String,
    onSelect: (Float) -> Unit,
) {
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        options.forEach { option ->
            FilterChip(
                selected = selected == option,
                onClick = { onSelect(option) },
                label = { Text(label(option), fontSize = 12.sp) },
            )
        }
    }
}

/** Mirrors the same "enabled" filter condition used by PlayerViewModel's skip planner. */
private fun readerMaskRanges(state: PlayerUiState, text: String): List<ReaderMask> = buildList {
    // Shares PlaybackFilterPredicate with the audio skip planner so text and
    // audio can never disagree about what is filtered.
    val enabledEvents = state.enabledScanEvents()
    enabledEvents.asSequence()
        .forEach { event ->
            val overlaps = state.readerTimingRanges.filter { timing ->
                val overlapStart = maxOf(event.startTime, timing.startTime)
                val overlapEnd = minOf(event.endTime, timing.endTime)
                overlapEnd > overlapStart
            }
            // Only use an actual audio/text overlap. Falling back to a nearby
            // section could hide unrelated text, which is worse than leaving a
            // tiny transcript gap visible.
            overlaps.forEach { timing ->
                val overlapStart = maxOf(event.startTime, timing.startTime).coerceIn(timing.startTime, timing.endTime)
                val overlapEnd = minOf(event.endTime, timing.endTime).coerceIn(timing.startTime, timing.endTime)
                val duration = (timing.endTime - timing.startTime).coerceAtLeast(.001)
                val length = timing.endCharacter - timing.startCharacter
                val rawStart = timing.startCharacter + (length * ((overlapStart - timing.startTime) / duration)).toInt()
                val rawEnd = timing.startCharacter + (length * ((overlapEnd - timing.startTime) / duration)).toInt()
                val start = minOf(rawStart, rawEnd)
                val end = maxOf(rawStart, rawEnd).coerceAtLeast(start + 1)
                if (end > start) add(ReaderMask(start, end))
            }
        }
    // Word-based filters, especially profanity, can be hidden directly from the
    // EPUB. This remains dependable even if audiobook/ebook scene timing has a gap.
    enabledEvents.mapNotNull { it.aggregateDisplay?.trim() }
        .filter { it.length in 2..64 && !it.equals("Censored word", ignoreCase = true) }
        .distinct()
        .forEach { censoredWord ->
            // The server intentionally sends displays such as "f**k", never the
            // uncensored word. Treat each asterisk as one letter in the EPUB rather
            // than searching for literal asterisks, which made word filters a no-op.
            val wordPattern = censoredWord.asSequence().joinToString(separator = "") { character ->
                if (character == '*') "[\\p{L}]" else Regex.escape(character.toString())
            }
            Regex("(?i)\\b$wordPattern\\b").findAll(text).forEach { match ->
                add(ReaderMask(match.range.first, match.range.last + 1))
            }
        }
}.merged()

@Composable
private fun PlayerToolButton(value: String, label: String, onClick: () -> Unit) {
    TextButton(onClick = onClick, contentPadding = PaddingValues(horizontal = 7.dp, vertical = 5.dp)) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(value, color = ChoiceGreen, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
            Text(label, color = ChoiceMuted, fontSize = 10.sp)
        }
    }
}

@Composable
private fun IconPlayerToolButton(icon: ImageVector, label: String, onClick: () -> Unit) {
    TextButton(onClick = onClick, contentPadding = PaddingValues(horizontal = 7.dp, vertical = 5.dp)) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Icon(icon, label, tint = ChoiceGreen, modifier = Modifier.size(21.dp))
            Text(label, color = ChoiceMuted, fontSize = 10.sp)
        }
    }
}

private fun formatSpeed(speed: Float): String = when (speed) {
    .25f -> "0.25x"
    .5f -> "0.50x"
    .75f -> "0.75x"
    1f -> "1.0x"
    1.25f -> "1.25x"
    1.5f -> "1.5x"
    1.75f -> "1.75x"
    2f -> "2.0x"
    else -> "${speed}x"
}

private fun formatTime(milliseconds: Long): String {
    val totalSeconds = milliseconds / 1000
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60
    return if (hours > 0) "%d:%02d:%02d".format(hours, minutes, seconds)
    else "%d:%02d".format(minutes, seconds)
}

@Composable
private fun ImportScreen(importer: ImportViewModel, accessToken: String, showLibrary: () -> Unit) {
    val state by importer.state.collectAsStateWithLifecycle()
    val context = androidx.compose.ui.platform.LocalContext.current
    DisposableEffect(importer) {
        onDispose(importer::onImportScreenLeft)
    }
    var displayedScanProgress by remember { mutableFloatStateOf(0f) }
    val notificationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    var organizeWithAudioChoice by remember { mutableStateOf(true) }
    val organizationFolderPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) importer.organizeInSelectedFolder(context, uri, organizeWithAudioChoice)
    }
    if (state.showOrganizationPrompt) {
        AlertDialog(
            onDismissRequest = { },
            icon = { Icon(Icons.Outlined.Folder, null, tint = ChoiceGreen) },
            title = { Text("Where should this audiobook be stored?") },
            text = {
                Text(
                    "AudioChoice can move the verified playable copy into its own organized audiobook " +
                        "folder automatically, or you can choose the exact folder yourself.",
                )
            },
            confirmButton = {
                Button(onClick = {
                    importer.organizeInAudioChoiceStorage(context.contentResolver)
                }) {
                    Text("Let AudioChoice organize it")
                }
            },
            dismissButton = {
                TextButton(onClick = {
                    organizeWithAudioChoice = false
                    organizationFolderPicker.launch(null)
                }) { Text("I'll choose the folder") }
            },
        )
    }
    if (state.showBetaRestriction) {
        AlertDialog(
            onDismissRequest = importer::dismissBetaRestriction,
            title = { Text("AudioChoice Beta") },
            text = {
                Text(
                    "Importing is currently limited to the A Court of Thorns and Roses " +
                        "Dramatized Adaptation (GraphicAudio) Part 1 and Part 2, and Fourth Wing " +
                        "Dramatized Adaptation (GraphicAudio) Part 1, and Dungeon Crawler Carl " +
                        "during this beta.\n\n" +
                        "Support for additional audiobooks will be added in future beta releases.",
                )
            },
            confirmButton = {
                TextButton(onClick = importer::dismissBetaRestriction) { Text("OK") }
            },
        )
    }
    LaunchedEffect(state.phase) {
        if (state.phase == ImportPhase.ANALYZING && android.os.Build.VERSION.SDK_INT >= 33 &&
            androidx.core.content.ContextCompat.checkSelfPermission(
                context,
                android.Manifest.permission.POST_NOTIFICATIONS,
            ) != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
    }
    LaunchedEffect(state.phase, state.scanProgress) {
        val serverProgress = state.scanProgress.coerceIn(0, 100) / 100f
        if (state.phase != ImportPhase.ANALYZING) {
            displayedScanProgress = 0f
            return@LaunchedEffect
        }
        displayedScanProgress = maxOf(displayedScanProgress, serverProgress)
        val projectionLimit = (serverProgress + 0.025f).coerceAtMost(0.99f)
        while (displayedScanProgress < projectionLimit) {
            delay(750)
            displayedScanProgress = (displayedScanProgress + 0.001f).coerceAtMost(projectionLimit)
        }
    }
    LaunchedEffect(state.organizationComplete) {
        if (state.organizationComplete) showLibrary()
    }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            runCatching { context.contentResolver.takePersistableUriPermission(uri, android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION) }
            importer.select(uri, context.contentResolver, accessToken)
        }
    }
    Text(
        when (state.phase) {
            ImportPhase.IDLE -> if (NarrationConfig.enabled) "Import a Book" else "Import Audiobook"
            ImportPhase.AGREEMENT -> "AAX Import"
            ImportPhase.CONVERTING -> "Converting Audiobook"
            ImportPhase.CONVERSION_COMPLETE -> "Conversion Complete"
            else -> "Analyzing Audiobook"
        },
        fontSize = 26.sp,
        fontWeight = FontWeight.Bold,
    )
    Spacer(Modifier.height(18.dp))
    if (state.phase == ImportPhase.AGREEMENT) {
        AaxOwnershipAgreement(
            fileName = state.fileName.orEmpty(),
            onAgree = { importer.acceptAaxAgreement(context.contentResolver, accessToken) },
            onCancel = importer::declineAaxAgreement,
        )
    } else if (state.phase == ImportPhase.IDLE) {
        Box(
            Modifier.fillMaxWidth().height(250.dp)
                .border(1.dp, ChoiceOutline, RoundedCornerShape(18.dp))
                .background(ChoiceSurface, RoundedCornerShape(18.dp)),
            contentAlignment = Alignment.Center,
        ) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Icon(Icons.Outlined.CloudUpload, null, tint = ChoiceGreen, modifier = Modifier.size(62.dp))
                Text(
                    if (NarrationConfig.enabled) {
                        "Choose an audiobook or ebook from this device"
                    } else "Choose an audiobook from this device",
                    color = ChoiceMuted,
                )
                Spacer(Modifier.height(22.dp))
                Button(onClick = {
                    picker.launch(
                        if (NarrationConfig.enabled) {
                            NarrationImportCoordinator.PICKER_MIME_TYPES
                        } else {
                            NarrationImportCoordinator.AUDIO_ONLY_PICKER_MIME_TYPES
                        },
                    )
                }) {
                    Text("Browse Files")
                }
            }
        }
        Spacer(Modifier.height(24.dp))
        Text("Supported Formats", color = ChoiceMuted)
        Text(
            if (NarrationConfig.enabled) "MP3   M4B   M4A   AAX   EPUB" else "MP3   M4B   M4A   AAX",
            color = ChoiceGreen,
            fontWeight = FontWeight.SemiBold,
        )
        if (NarrationConfig.enabled) {
            Spacer(Modifier.height(6.dp))
            Text(
                "An EPUB with no audiobook is read aloud by a synthetic voice and lands in your " +
                    "Ebooks library. To read along with an audiobook you already have, open that " +
                    "audiobook and attach the EPUB there instead.",
                color = ChoiceMuted,
                fontSize = 12.sp,
                lineHeight = 17.sp,
            )
        }
        Spacer(Modifier.height(20.dp))
        Text(
            "Your audiobook is uploaded privately only when a new scan is required. Temporary audio is deleted after processing.",
            color = ChoiceMuted,
            fontSize = 13.sp,
        )
    } else if (state.phase == ImportPhase.CONVERTING) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.fillMaxWidth()) {
            CircularProgressIndicator(progress = { state.conversionProgress }, color = ChoiceGreen)
            Spacer(Modifier.height(18.dp))
            Text("Converting on this device", fontSize = 19.sp, fontWeight = FontWeight.SemiBold)
            Text("${(state.conversionProgress * 100).toInt().coerceIn(0, 100)}% complete", color = ChoiceGreen)
            Spacer(Modifier.height(10.dp))
            Text(
                "Keep AudioChoice open while conversion is running. The converted audiobook stays local.",
                color = ChoiceMuted,
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(8.dp))
            Text(
                "If conversion is interrupted, reopen AudioChoice and select this same AAX file. " +
                    "After confirming ownership again, conversion resumes from saved progress.",
                color = ChoiceMuted,
                fontSize = 13.sp,
                lineHeight = 18.sp,
                textAlign = TextAlign.Center,
            )
        }
    } else if (state.phase == ImportPhase.CONVERSION_COMPLETE) {
        Card(
            colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
            shape = RoundedCornerShape(18.dp),
            modifier = Modifier.fillMaxWidth(),
        ) {
            Column(
                Modifier.fillMaxWidth().padding(22.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Icon(Icons.Outlined.CheckCircle, null, tint = ChoiceGreen, modifier = Modifier.size(52.dp))
                Spacer(Modifier.height(14.dp))
                Text("M4B ready on this device", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                Text(state.fileName.orEmpty(), color = ChoiceMuted, textAlign = TextAlign.Center)
                Spacer(Modifier.height(12.dp))
                Text(
                    "The protected AAX was converted locally. No audiobook audio has been uploaded yet.",
                    color = ChoiceMuted,
                    textAlign = TextAlign.Center,
                )
                Spacer(Modifier.height(20.dp))
                Button(
                    onClick = { importer.scanConverted(context.contentResolver, accessToken) },
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Start Private Scan") }
            }
        }
    } else {
        val steps = listOf(
            ImportPhase.READING to "Reading audiobook",
            ImportPhase.FINGERPRINTING to "Fingerprinting file",
            ImportPhase.SEARCHING to "Searching scan library",
            ImportPhase.UPLOADING to "Private upload",
            ImportPhase.ANALYZING to "Analyzing content",
            ImportPhase.COMPLETE to "Filter scan ready",
        )
        steps.forEachIndexed { index, (_, label) ->
            val done = index < state.completedSteps || state.phase == ImportPhase.COMPLETE
            val active = steps[index].first == state.phase
            Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier.size(42.dp).border(1.dp, if (active) ChoiceGreen else ChoiceOutline, RoundedCornerShape(50)),
                    contentAlignment = Alignment.Center,
                ) {
                    if (active && steps[index].first == ImportPhase.ANALYZING) {
                        CircularProgressIndicator(
                            progress = { displayedScanProgress },
                            color = ChoiceGreen,
                            trackColor = ChoiceOutline,
                            strokeWidth = 3.dp,
                            modifier = Modifier.fillMaxSize().padding(2.dp),
                        )
                        Text("%.1f%%".format(displayedScanProgress * 100), color = ChoiceGreen, fontSize = 8.sp)
                    } else {
                        Text(if (done) "✓" else if (active) "•" else "", color = ChoiceGreen, fontSize = 20.sp)
                    }
                }
                Spacer(Modifier.width(14.dp))
                Column(Modifier.weight(1f)) {
                    Text(label, fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal)
                    if (active && steps[index].first == ImportPhase.ANALYZING) {
                        Text(
                            if (state.totalChunks > 0) {
                                "${state.completedChunks}/${state.totalChunks} chunks"
                            } else {
                                "Preparing chunks…"
                            },
                            color = ChoiceGreen,
                            fontSize = 12.sp,
                        )
                    }
                    Text(if (done) "Completed" else if (active) "In progress…" else "Pending", color = if (active) ChoiceGreen else ChoiceMuted, fontSize = 12.sp)
                }
            }
        }
        state.fileName?.let { Text(it, color = ChoiceMuted, modifier = Modifier.padding(top = 12.dp)) }
        state.statusMessage?.let {
            Card(
                colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
                shape = RoundedCornerShape(12.dp),
                modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
            ) {
                Text(it, color = ChoiceMuted, fontSize = 13.sp, modifier = Modifier.padding(14.dp))
            }
        }
        if (state.phase == ImportPhase.ANALYZING) {
            Text(
                "Full-length audiobook scans can take a while. You may leave this screen or close " +
                    "AudioChoice—the private scan continues in the cloud and reconnects automatically when you return.",
                color = ChoiceMuted,
                fontSize = 13.sp,
                lineHeight = 18.sp,
                modifier = Modifier.padding(top = 12.dp),
            )
        }
        state.error?.let {
            Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 12.dp))
            Row(Modifier.padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Button(onClick = { importer.retry(context.contentResolver, accessToken) }) { Text("Retry") }
                TextButton(onClick = importer::reset) { Text("Choose another file") }
            }
        }
        if (state.phase == ImportPhase.COMPLETE) {
            val count = state.result?.result?.events?.let(PlaybackFilterTaxonomy::controlCount) ?: 0
            Text("Ready with $count filter controls", color = ChoiceGreen, fontSize = 17.sp, modifier = Modifier.padding(top = 18.dp))
            if (state.titleFromFilename) {
                // Saying so is the honest option. The file carried no edition tags,
                // so this title came from the filename and may well be wrong.
                Text(
                    "This file carried no edition details, so its title was taken from the " +
                        "filename and may not be exact.",
                    color = ChoiceMuted,
                    fontSize = 13.sp,
                    modifier = Modifier.padding(top = 10.dp),
                )
            }
            state.organizationMessage?.let {
                Text(it, color = ChoiceMuted, fontSize = 13.sp, modifier = Modifier.padding(top = 10.dp))
            }
            if (state.organizingFile) {
                CircularProgressIndicator(color = ChoiceGreen, modifier = Modifier.padding(top = 12.dp))
            } else if (!state.showOrganizationPrompt) {
                Button(onClick = showLibrary, modifier = Modifier.padding(top = 12.dp)) { Text("View in Library") }
            }
        }
    }
}

@Composable
private fun AaxOwnershipAgreement(fileName: String, onAgree: () -> Unit, onCancel: () -> Unit) {
    var showConversionNotice by rememberSaveable { mutableStateOf(false) }
    if (showConversionNotice) {
        AlertDialog(
            onDismissRequest = { showConversionNotice = false },
            icon = { Icon(Icons.Outlined.Schedule, null, tint = ChoiceGreen) },
            title = { Text("Before conversion begins") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text(
                        "Local AAX preparation can take several minutes. Longer audiobooks do not " +
                            "necessarily take longer—the time depends on the file authorization search.",
                    )
                    Text(
                        "Keep AudioChoice open and visible until conversion finishes. Avoid force-closing " +
                            "the app or restarting the device. Connecting your phone to power is recommended.",
                    )
                    Text(
                        "If it is interrupted, reopen AudioChoice, choose the same AAX file, and confirm " +
                            "ownership again. AudioChoice will resume from the most recently saved checkpoint.",
                        color = ChoiceMuted,
                    )
                }
            },
            confirmButton = {
                Button(onClick = {
                    showConversionNotice = false
                    onAgree()
                }) { Text("Begin conversion") }
            },
            dismissButton = {
                TextButton(onClick = { showConversionNotice = false }) { Text("Not now") }
            },
        )
    }
    Card(colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(18.dp)) {
        Column(Modifier.padding(20.dp)) {
            Icon(Icons.Outlined.Security, null, tint = ChoiceGreen, modifier = Modifier.size(38.dp))
            Spacer(Modifier.height(12.dp))
            Text("Confirm audiobook ownership", fontSize = 21.sp, fontWeight = FontWeight.Bold)
            Text(fileName, color = ChoiceMuted, fontSize = 13.sp)
            Spacer(Modifier.height(16.dp))
            Text(
                com.audiochoice.mobile.importing.LocalAaxConverter.AGREEMENT_TEXT,
                color = MaterialTheme.colorScheme.onSurface,
                lineHeight = 21.sp,
            )
            Spacer(Modifier.height(12.dp))
            Text(
                "Conversion happens on this device. AudioChoice does not request Audible login details " +
                    "and does not retain the converted audiobook on its servers.",
                color = ChoiceMuted,
                fontSize = 13.sp,
                lineHeight = 18.sp,
            )
            Spacer(Modifier.height(22.dp))
            Button(onClick = { showConversionNotice = true }, modifier = Modifier.fillMaxWidth()) {
                Text("I agree and confirm ownership")
            }
            TextButton(onClick = onCancel, modifier = Modifier.fillMaxWidth()) { Text("Cancel") }
        }
    }
}

// region the ebook reader
//
// A narrated book opens here rather than in the player. Everything below lives in this file
// rather than its own so it can reuse `readerPalette`, `readerFontFamily` and
// `ReaderSettingsDialog`, all of which are private and all of which the beta build's read-along
// reader depends on. Widening their visibility to move this out would have put the shipping
// reader's helpers on the public surface for the benefit of an experimental screen.

/**
 * Full-screen reader for a book with no audiobook.
 *
 * Differs from `ReaderScreen` in one structural way that shapes the whole surface: there is no
 * audio until a voice makes some, so the bottom bar offers to start reading aloud rather than
 * offering transport over something that already exists. The text, the filtering and the
 * typography are the same code, because a filtered passage must look identical whichever kind of
 * book it came from.
 */
@Composable
private fun EbookReaderScreen(
    narration: NarrationViewModel,
    filtersLocked: Boolean,
    onBack: () -> Unit,
) {
    val state by narration.state.collectAsStateWithLifecycle()
    val book = state.book ?: return
    val readerContext = androidx.compose.ui.platform.LocalContext.current
    val settings = state.readerSettings
    val palette = readerPalette(settings.theme)
    var settingsSheet by remember { mutableStateOf(false) }
    var filterSheet by remember { mutableStateOf(false) }
    var voiceSheet by remember { mutableStateOf(false) }
    var pronunciationSheet by remember { mutableStateOf(false) }
    var agreementSheet by remember { mutableStateOf(false) }

    val listState = rememberLazyListState(
        initialFirstVisibleItemIndex = state.readerPosition.paragraphIndex,
        initialFirstVisibleItemScrollOffset = state.readerPosition.scrollOffset,
    )

    // Recomputed only when the text or the enabled filter set changes, never per scroll frame.
    // The masks come from the same FilteredRanges the renderer uses, so what is hidden here and
    // what is never spoken cannot disagree.
    val filtered = remember(
        state.scanEvents,
        state.disabledCategoryIDs,
        state.disabledGroupIDs,
        state.disabledEventKeys,
        state.disabledAggregateKeys,
    ) {
        FilteredRanges.forEnabledEvents(
            events = state.scanEvents,
            disabledCategoryIDs = state.disabledCategoryIDs,
            disabledGroupIDs = state.disabledGroupIDs,
            disabledEventKeys = state.disabledEventKeys,
            disabledAggregateKeys = state.disabledAggregateKeys,
        )
    }

    var previousHighlight by remember(book.id) { mutableStateOf<Int?>(null) }
    val view = remember(state.bookText, filtered, state.positionSeconds, state.plan) {
        val bookText = state.bookText
        if (bookText == null) {
            null
        } else {
            NarrationReaderState.derive(
                bookText = bookText,
                filteredRanges = filtered,
                narrationTimingRanges = emptyList(),
                bookTimeSeconds = state.positionSeconds.takeIf { state.isSpeaking },
                previousHighlightIndex = previousHighlight,
            )
        }
    }
    LaunchedEffect(view?.highlightedParagraphIndex) {
        view?.highlightedParagraphIndex?.let { previousHighlight = it }
    }

    BackHandler(onBack = onBack)

    LaunchedEffect(listState, book.id) {
        snapshotFlow { listState.firstVisibleItemIndex to listState.firstVisibleItemScrollOffset }
            .collect { (index, offset) -> narration.saveReaderPosition(index, offset) }
    }

    val removedCount = view?.displayParagraphs?.sumOf { it.removedPassages } ?: 0

    Column(
        Modifier.fillMaxSize().background(palette.paper).statusBarsPadding().navigationBarsPadding(),
    ) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = onBack) {
                Icon(Icons.Outlined.KeyboardArrowDown, "Back to library", tint = ChoiceGreen)
            }
            Column(Modifier.weight(1f)) {
                Text(
                    book.title,
                    color = palette.ink,
                    fontSize = 13.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    buildString {
                        append("Read aloud")
                        if (state.totalChapters > 0) {
                            append(" · ${state.renderedChapters}/${state.totalChapters} chapters")
                        }
                        if (removedCount > 0) append(" · $removedCount filtered passages removed")
                    },
                    color = palette.mutedInk,
                    fontSize = 11.sp,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            IconButton(onClick = { filterSheet = true }) {
                Icon(Icons.Outlined.Shield, "Content filters", tint = ChoiceGreen)
            }
            IconButton(onClick = { pronunciationSheet = true }) {
                Icon(Icons.Outlined.RecordVoiceOver, "How words are said", tint = ChoiceGreen)
            }
            IconButton(onClick = { settingsSheet = true }) {
                Icon(Icons.Outlined.TextFields, "Reading settings", tint = ChoiceGreen)
            }
        }
        HorizontalDivider(color = palette.mutedInk.copy(alpha = .2f))

        // Filtering silently doing nothing is worse than saying so: without this the listener
        // would assume their filters were applied to what is read aloud.
        if (state.readiness == NarrationReadiness.AWAITING_FILTERS &&
            !state.flags.continuedWithoutFilterResults
        ) {
            Card(
                Modifier.fillMaxWidth().padding(12.dp),
                colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
            ) {
                Column(Modifier.padding(14.dp)) {
                    Text(
                        NarrationViewModel.FILTERS_UNAVAILABLE_MESSAGE,
                        color = ChoiceMuted,
                        fontSize = 12.sp,
                        lineHeight = 17.sp,
                    )
                    Spacer(Modifier.height(8.dp))
                    TextButton(onClick = narration::continueWithoutFilterResults) {
                        Text("Read aloud without filters")
                    }
                }
            }
        }

        if (view == null || view.visibleParagraphs.isEmpty()) {
            Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
                Text(
                    when (state.readiness) {
                        NarrationReadiness.LOADING -> "Opening this book…"
                        NarrationReadiness.UNREADABLE ->
                            state.error ?: "This book's text is no longer on this device."
                        else -> "This book has no readable text."
                    },
                    color = palette.mutedInk,
                    fontSize = 14.sp,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(28.dp),
                )
            }
        } else {
            LazyColumn(
                state = listState,
                modifier = Modifier.weight(1f).fillMaxWidth(),
                contentPadding = PaddingValues(
                    horizontal = (READER_BASE_MARGIN_DP * settings.marginScale).dp,
                    vertical = 16.dp,
                ),
            ) {
                items(
                    count = view.visibleParagraphs.size,
                    key = { index -> view.visibleParagraphs[index].paragraph.startCharacter },
                ) { index ->
                    val display = view.visibleParagraphs[index]
                    val isSpoken = settings.followAudio &&
                        view.highlightedParagraphIndex ==
                        view.paragraphs.indexOf(display.paragraph)
                    Text(
                        display.displayText,
                        color = palette.ink,
                        fontFamily = readerFontFamily(settings.font),
                        fontSize = (READER_BASE_FONT_SP * settings.fontScale).sp,
                        lineHeight = (
                            READER_BASE_LINE_SP * settings.fontScale *
                                ReaderSettings.lineHeightFactor(settings.font)
                            ).sp,
                        modifier = Modifier
                            .fillMaxWidth()
                            .then(
                                if (isSpoken) {
                                    Modifier.background(
                                        ChoiceGreen.copy(alpha = .16f),
                                        RoundedCornerShape(6.dp),
                                    ).semantics { stateDescription = "Now being read aloud" }
                                } else Modifier,
                            )
                            .padding(horizontal = 4.dp, vertical = 2.dp)
                            .padding(bottom = 12.dp),
                    )
                }
            }
        }

        HorizontalDivider(color = palette.mutedInk.copy(alpha = .2f))
        // Making a chapter takes a moment even on a fast phone, and silence with no explanation
        // reads as a button that did nothing.
        state.message?.let { message ->
            Row(
                Modifier.fillMaxWidth().padding(start = 20.dp, end = 20.dp, top = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                CircularProgressIndicator(Modifier.size(14.dp), strokeWidth = 2.dp, color = ChoiceGreen)
                Spacer(Modifier.width(10.dp))
                Text(message, color = palette.mutedInk, fontSize = 12.sp)
            }
        }
        state.error?.let { error ->
            Row(
                Modifier.fillMaxWidth().padding(start = 20.dp, end = 20.dp, top = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    error,
                    color = MaterialTheme.colorScheme.error,
                    fontSize = 12.sp,
                    lineHeight = 17.sp,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = narration::dismissMessage) { Text("Dismiss", fontSize = 12.sp) }
            }
        }
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            TextButton(onClick = {
                voiceSheet = true
                // Choosing a voice is exactly the moment a lapsed subscription or a changed
                // agreement should be noticed, so both are re-read here rather than trusted.
                narration.refreshTier(force = true)
                narration.refreshVoices()
            }) {
                Icon(Icons.Outlined.RecordVoiceOver, null, tint = ChoiceGreen, modifier = Modifier.size(19.dp))
                Spacer(Modifier.width(7.dp))
                Text(
                    state.selectedVoice?.let { voiceKindLabel(it.kind) } ?: "Your phone's voice",
                    color = palette.ink,
                    fontSize = 12.sp,
                )
            }
            // Only once there is audio to reclaim. Before then this is an action with nothing to
            // act on, and "0 MB" invites a tap that cannot do anything.
            if (state.audioBytes > 0) {
                TextButton(onClick = narration::offerDiscardAllAudio) {
                    Icon(
                        Icons.Outlined.Storage,
                        null,
                        tint = ChoiceGreen,
                        modifier = Modifier.size(17.dp),
                    )
                    Spacer(Modifier.width(6.dp))
                    Text(
                        // Rounded down, which is how a size reads everywhere else on the device.
                        "${NarrationStorage.displayMegabytes(state.audioBytes)} MB",
                        color = palette.mutedInk,
                        fontSize = 12.sp,
                    )
                }
            }
            // Deliberately disabled rather than hidden while filters are unsettled: a missing
            // control reads as a broken screen, whereas a disabled one with the explanation
            // above it reads as a step still to do.
            Button(
                onClick = { narration.toggleReadAloud(readerContext) },
                // A voice is not required to start: with none chosen the phone's own voice is
                // used, which is the free default and works offline. Requiring a choice first
                // would put a dialogue between the listener and the button they pressed.
                enabled = state.mayRender,
                shape = RoundedCornerShape(50),
            ) {
                Icon(
                    if (state.isSpeaking) Icons.Outlined.Pause else Icons.Outlined.PlayArrow,
                    if (state.isSpeaking) "Pause" else "Read aloud",
                    modifier = Modifier.size(22.dp),
                )
                Spacer(Modifier.width(7.dp))
                Text(if (state.isSpeaking) "Pause" else "Read aloud")
            }
        }
    }

    if (settingsSheet) {
        ReaderSettingsDialog(
            settings = settings,
            onSettingsChanged = narration::updateReaderSettings,
            onDismiss = { settingsSheet = false },
            // "Follow the audiobook" is the wrong words here: there is no audiobook, and what
            // the switch controls is whether the reader tracks the synthetic voice.
            followLabel = "Follow the voice",
            followDescription = "Highlights and scrolls to the passage being read aloud.",
        )
    }
    if (filterSheet) {
        NarrationFilterDialog(
            state = state,
            filtersLocked = filtersLocked,
            onChoices = narration::setFilterChoices,
            onDismiss = { filterSheet = false },
        )
    }
    // Shown over the filter sheet, because it is a consequence of the switch they just moved and
    // reads as a continuation of it rather than as a new task.
    if (pronunciationSheet) {
        NarrationPronunciationDialog(
            state = state,
            onRecord = { written, spoken, scope, editing ->
                narration.recordPronunciationRule(written, spoken, scope, editing)
            },
            onDelete = narration::deletePronunciationRule,
            onPreview = { spoken -> narration.previewPronunciation(readerContext, spoken) },
            onFormChanged = narration::clearPronunciationRejection,
            onDismiss = { pronunciationSheet = false },
        )
    }
    state.pendingPronunciationRerender?.let { impact ->
        NarrationPronunciationRerenderDialog(
            impact = impact,
            onConfirm = { narration.confirmPronunciationRerender(readerContext) },
            onCancel = narration::cancelPronunciationRerender,
        )
    }
    state.pendingDiscardAll?.let { estimate ->
        NarrationDiscardAudioDialog(
            estimate = estimate,
            onConfirm = narration::discardAllAudio,
            onCancel = narration::cancelDiscardAllAudio,
        )
    }
    state.pendingFilterChange?.let { pending ->
        NarrationFilterChangeDialog(
            impact = pending.impact,
            onConfirm = { narration.confirmFilterChange(readerContext) },
            onDecline = narration::declineFilterChange,
        )
    }
    if (voiceSheet) {
        NarrationVoiceDialog(
            state = state,
            onSelect = { voice ->
                if (voice.kind == VoiceKind.PREMIUM &&
                    !com.audiochoice.mobile.narration.voice.PremiumVoiceAgreement.maySubmit(
                        state.premiumGate,
                    )
                ) {
                    // Selecting premium never records the choice before the statement has been
                    // accepted. On decline or dismissal nothing at all changes, which is why the
                    // selection happens in the dialog's accept handler rather than here.
                    voiceSheet = false
                    agreementSheet = true
                } else {
                    narration.selectVoice(voice)
                    voiceSheet = false
                }
            },
            onDismiss = { voiceSheet = false },
        )
    }
    if (agreementSheet) {
        PremiumAgreementDialog(
            gate = state.premiumGate,
            onAccept = {
                narration.acceptPremiumAgreement()
                narration.selectVoice(SelectedVoice(VoiceKind.PREMIUM, defaultVoiceID(VoiceKind.PREMIUM)))
                agreementSheet = false
            },
            onDecline = {
                // Changes nothing: no record, no selected voice, no submission. A listener who
                // declines is left exactly where they were, still able to read and still able to
                // use their phone's own voice.
                agreementSheet = false
            },
        )
    }
}

/**
 * Filter controls for a narrated book.
 *
 * Builds its tree with the same `PlaybackFilterTaxonomy` an audiobook uses, so the two present
 * the identical control hierarchy. Written against plain sets and a callback rather than against
 * `PlayerViewModel`, which is what lets a narrated book reuse the taxonomy without the player
 * having to know narration exists.
 */
@Composable
private fun NarrationFilterDialog(
    state: NarrationUiState,
    filtersLocked: Boolean,
    onChoices: (Set<String>, Set<String>, Set<String>, Set<String>) -> Unit,
    onDismiss: () -> Unit,
) {
    val available = remember(state.scanEvents) {
        PlaybackFilterTaxonomy.available(state.scanEvents)
    }
    var expandedParent by remember { mutableStateOf<String?>(null) }
    var expandedChild by remember { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Outlined.Shield, null, tint = ChoiceGreen) },
        title = { Text("Content filters") },
        text = {
            Column(Modifier.heightIn(max = 460.dp).verticalScroll(rememberScrollState())) {
                Text(
                    when {
                        state.readiness == NarrationReadiness.AWAITING_FILTERS ->
                            "Filter results for this book aren't ready, so nothing is being " +
                                "filtered yet."
                        filtersLocked ->
                            "Parental Controls are on. Filter choices are visible but locked."
                        available.isEmpty() ->
                            "No filterable content was found in this book."
                        else ->
                            "Filtered passages are never read aloud and never appear in the " +
                                "text. Changing a filter after a chapter is read aloud offers " +
                                "to make that chapter again."
                    },
                    color = ChoiceMuted,
                    fontSize = 12.sp,
                    lineHeight = 17.sp,
                )
                Spacer(Modifier.height(10.dp))
                available.forEach { parent ->
                    HorizontalDivider(color = ChoiceOutline)
                    val categoryOff = parent.id.lowercase() in state.disabledCategoryIDs
                    Row(
                        Modifier.fillMaxWidth().clickable {
                            expandedParent = if (expandedParent == parent.id) null else parent.id
                        }.padding(vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(if (expandedParent == parent.id) "⌄" else "›", color = ChoiceMuted)
                        Spacer(Modifier.width(8.dp))
                        Column(Modifier.weight(1f)) {
                            Text(parent.label, fontSize = 14.sp)
                            Text(
                                "${parent.children.sumOf { it.events.size }} filter controls",
                                color = ChoiceMuted,
                                fontSize = 11.sp,
                            )
                        }
                        Switch(
                            checked = !categoryOff,
                            enabled = !filtersLocked,
                            onCheckedChange = { enabled ->
                                val categories = state.disabledCategoryIDs.toMutableSet()
                                if (enabled) {
                                    categories.remove(parent.id.lowercase())
                                } else {
                                    categories.add(parent.id.lowercase())
                                }
                                onChoices(
                                    categories,
                                    state.disabledGroupIDs,
                                    state.disabledEventKeys,
                                    state.disabledAggregateKeys,
                                )
                            },
                            modifier = Modifier.semantics {
                                contentDescription = "Filter ${parent.label}"
                                stateDescription = if (categoryOff) "Not filtering" else "Filtering"
                            },
                        )
                    }
                    if (expandedParent == parent.id) parent.children.forEach { child ->
                        val groupOff = child.id.lowercase() in state.disabledGroupIDs
                        Row(
                            Modifier.fillMaxWidth().clickable {
                                expandedChild = if (expandedChild == child.id) null else child.id
                            }.padding(start = 26.dp, top = 6.dp, bottom = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(if (expandedChild == child.id) "⌄" else "›", color = ChoiceMuted)
                            Spacer(Modifier.width(8.dp))
                            Column(Modifier.weight(1f)) {
                                Text(child.label, fontSize = 13.sp)
                                Text(
                                    "${child.events.size} controls",
                                    color = ChoiceMuted,
                                    fontSize = 10.sp,
                                )
                            }
                            Switch(
                                checked = !groupOff && !categoryOff,
                                enabled = !filtersLocked && !categoryOff,
                                onCheckedChange = { enabled ->
                                    val groups = state.disabledGroupIDs.toMutableSet()
                                    if (enabled) {
                                        groups.remove(child.id.lowercase())
                                    } else {
                                        groups.add(child.id.lowercase())
                                    }
                                    onChoices(
                                        state.disabledCategoryIDs,
                                        groups,
                                        state.disabledEventKeys,
                                        state.disabledAggregateKeys,
                                    )
                                },
                                modifier = Modifier.semantics {
                                    contentDescription = "Filter ${child.label}"
                                    stateDescription =
                                        if (groupOff || categoryOff) "Not filtering" else "Filtering"
                                },
                            )
                        }
                        // Individual controls, so a listener can switch off one word rather than a
                        // whole category. Repeated profanity arrives as a single aggregate control
                        // holding every occurrence, exactly as it does for an audiobook.
                        if (expandedChild == child.id) child.events.forEach { event ->
                            val eventOff = event.key in state.disabledEventKeys ||
                                event.key in state.disabledAggregateKeys
                            Row(
                                Modifier.fillMaxWidth()
                                    .padding(start = 52.dp, top = 4.dp, bottom = 4.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Column(Modifier.weight(1f)) {
                                    Text(event.label, fontSize = 12.sp)
                                    // Presented as a position through the book, never as a
                                    // timestamp. For a narrated book this value is a character
                                    // offset carried in a time-named field, so formatting it as
                                    // mm:ss would render offset 84,000 as "23:20:00" -- a number
                                    // that looks authoritative and means nothing.
                                    narratedPositionLabel(event.startTime, state.bookText?.length)
                                        ?.let { label ->
                                            Text(label, color = ChoiceMuted, fontSize = 10.sp)
                                        }
                                }
                                Switch(
                                    checked = !eventOff && !groupOff && !categoryOff,
                                    enabled = !filtersLocked && !groupOff && !categoryOff,
                                    onCheckedChange = { enabled ->
                                        val events = state.disabledEventKeys.toMutableSet()
                                        val aggregates = state.disabledAggregateKeys.toMutableSet()
                                        val target = if (event.aggregate) aggregates else events
                                        if (enabled) target.remove(event.key) else target.add(event.key)
                                        onChoices(
                                            state.disabledCategoryIDs,
                                            state.disabledGroupIDs,
                                            events,
                                            aggregates,
                                        )
                                    },
                                    modifier = Modifier.semantics {
                                        contentDescription = "Filter ${event.label}"
                                        stateDescription =
                                            if (eventOff) "Not filtering" else "Filtering"
                                    },
                                )
                            }
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Done") } },
    )
}

/**
 * Records how a word in this book should be said.
 *
 * Validation runs before anything is written and a refusal keeps what was typed: making someone
 * retype a long name because the other field was wrong is its own small insult.
 */
@Composable
private fun NarrationPronunciationDialog(
    state: NarrationUiState,
    onRecord: (String, String, RuleScope, String?) -> Unit,
    onDelete: (String, RuleScope) -> Unit,
    onPreview: (String) -> Unit,
    onFormChanged: () -> Unit,
    onDismiss: () -> Unit,
) {
    var written by remember { mutableStateOf("") }
    var spoken by remember { mutableStateOf("") }
    var scope by remember { mutableStateOf(RuleScope.BOOK) }
    // Set when an existing rule is being replaced, so validation knows not to treat the rule as a
    // duplicate of itself.
    var editing by remember { mutableStateOf<String?>(null) }
    // Cleared when the view model reports a rule accepted, never on the button press. Recording is
    // asynchronous, so clearing on press would discard what someone typed precisely when the rule
    // was refused and they need it back to correct it.
    var seenAccepted by remember { mutableStateOf(state.pronunciationAccepted) }
    LaunchedEffect(state.pronunciationAccepted) {
        if (state.pronunciationAccepted != seenAccepted) {
            seenAccepted = state.pronunciationAccepted
            written = ""
            spoken = ""
            editing = null
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Outlined.RecordVoiceOver, null, tint = ChoiceGreen) },
        title = { Text("How words are said") },
        text = {
            Column {
                Text(
                    "Names the voice gets wrong. This changes only what is read aloud, never the " +
                        "words on the page.",
                    color = ChoiceMuted,
                    fontSize = 12.sp,
                )
                Spacer(Modifier.height(12.dp))
                OutlinedTextField(
                    value = written,
                    onValueChange = { written = it; onFormChanged() },
                    label = { Text("Written like") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(
                    value = spoken,
                    onValueChange = { spoken = it; onFormChanged() },
                    label = { Text("Said like") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                // Said plainly and next to the field it concerns. The entered values stay put.
                state.pronunciationRejection?.let { rejection ->
                    Spacer(Modifier.height(8.dp))
                    Text(
                        pronunciationRejectionMessage(rejection),
                        color = ChoiceGreen,
                        fontSize = 12.sp,
                    )
                    // A duplicate is not a dead end: the rule they are trying to add already
                    // exists, so the useful next step is editing it.
                    if (rejection is RuleRejection.Duplicate) {
                        TextButton(onClick = {
                            written = rejection.existing.writtenForm
                            spoken = rejection.existing.replacementForm
                            editing = rejection.existing.writtenForm
                            onFormChanged()
                        }) { Text("Edit the existing one") }
                    }
                }
                Spacer(Modifier.height(10.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    // Hearing it is the only way to tell whether a spelling works, so the preview
                    // sits next to the field rather than behind the save.
                    TextButton(
                        onClick = { onPreview(spoken) },
                        enabled = spoken.isNotBlank(),
                    ) {
                        Icon(
                            Icons.Outlined.PlayArrow,
                            null,
                            tint = ChoiceGreen,
                            modifier = Modifier.size(17.dp),
                        )
                        Spacer(Modifier.width(5.dp))
                        Text("Hear it", fontSize = 12.sp)
                    }
                    Spacer(Modifier.weight(1f))
                    TextButton(onClick = {
                        scope = if (scope == RuleScope.BOOK) RuleScope.ACCOUNT else RuleScope.BOOK
                    }) {
                        Text(
                            if (scope == RuleScope.BOOK) "This book" else "All my books",
                            fontSize = 12.sp,
                        )
                    }
                }
                if (state.pronunciationRules.isNotEmpty()) {
                    Spacer(Modifier.height(10.dp))
                    HorizontalDivider(color = ChoiceMuted.copy(alpha = .2f))
                    Spacer(Modifier.height(8.dp))
                    state.pronunciationRules.forEach { scoped ->
                        Row(
                            Modifier.fillMaxWidth().padding(vertical = 3.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column(Modifier.weight(1f)) {
                                Text(
                                    "${scoped.rule.writtenForm} → ${scoped.rule.replacementForm}",
                                    fontSize = 12.sp,
                                )
                                Text(
                                    if (scoped.scope == RuleScope.BOOK) {
                                        "This book"
                                    } else {
                                        "All my books"
                                    },
                                    color = ChoiceMuted,
                                    fontSize = 10.sp,
                                )
                            }
                            TextButton(onClick = {
                                onDelete(scoped.rule.writtenForm, scoped.scope)
                            }) { Text("Remove", fontSize = 11.sp) }
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(
                onClick = { onRecord(written, spoken, scope, editing) },
                enabled = written.isNotBlank() && spoken.isNotBlank(),
            ) { Text(if (editing == null) "Add" else "Save") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Done") } },
    )
}

/** Says why a rule was refused, in the terms the listener typed it in. */
private fun pronunciationRejectionMessage(rejection: RuleRejection): String = when (rejection) {
    is RuleRejection.OutOfBounds -> when (rejection.form) {
        RuleRejection.OutOfBounds.Form.WRITTEN ->
            "Fill in how the word is written, up to ${rejection.limit} characters."
        RuleRejection.OutOfBounds.Form.SPOKEN ->
            "Fill in how it should be said, up to ${rejection.limit} characters."
    }
    is RuleRejection.Duplicate ->
        "There is already a rule for “${rejection.existing.writtenForm}”."
    is RuleRejection.ScopeFull ->
        "That is the limit of ${rejection.limit} rules. Remove one to add another."
}

/**
 * Offers to redo audio a pronunciation change affects.
 *
 * The rule is already saved and governs everything made from here on. The only question is whether to
 * redo what already exists, which costs the wait again.
 */
@Composable
private fun NarrationPronunciationRerenderDialog(
    impact: RerenderImpact,
    onConfirm: () -> Unit,
    onCancel: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onCancel,
        icon = { Icon(Icons.Outlined.Refresh, null, tint = ChoiceGreen) },
        title = { Text("Update the audio already made?") },
        text = {
            val chapters = if (impact.chapterCount == 1) "chapter" else "chapters"
            Column {
                Text(
                    "${impact.chapterCount} $chapters already have audio using the old " +
                        "pronunciation.",
                    fontSize = 13.sp,
                )
                Spacer(Modifier.height(10.dp))
                Text(
                    "Everything read from now on uses your new pronunciation either way.",
                    color = ChoiceMuted,
                    fontSize = 12.sp,
                )
            }
        },
        confirmButton = { TextButton(onClick = onConfirm) { Text("Update them") } },
        dismissButton = { TextButton(onClick = onCancel) { Text("Leave them") } },
    )
}

/**
 * Confirms reclaiming the space a book's audio occupies.
 *
 * Asked because reclaiming is instant and undoing it is not: the audio returns only by waiting for it
 * to be made again. Everything cheap to keep is kept -- the reading plan, the word timings, the scan
 * results, the pronunciation rules and their place in the book -- so this trades space for waiting and
 * nothing else.
 */
@Composable
private fun NarrationDiscardAudioDialog(
    estimate: DiscardEstimate,
    onConfirm: () -> Unit,
    onCancel: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onCancel,
        icon = { Icon(Icons.Outlined.Storage, null, tint = ChoiceGreen) },
        title = { Text("Free up this space?") },
        text = {
            Column {
                Text(
                    "This frees " +
                        "${NarrationStorage.displayMegabytes(estimate.reclaimableBytes)} MB.",
                    fontSize = 13.sp,
                )
                Spacer(Modifier.height(10.dp))
                val chapters =
                    if (estimate.chaptersNeedingRerender == 1) "chapter" else "chapters"
                Text(
                    "${estimate.chaptersNeedingRerender} $chapters will need to be made again " +
                        "before you can hear them.",
                    color = ChoiceMuted,
                    fontSize = 12.sp,
                )
                Spacer(Modifier.height(10.dp))
                Text(
                    "Your place in the book, your filters and your pronunciations are kept.",
                    color = ChoiceMuted,
                    fontSize = 12.sp,
                )
            }
        },
        confirmButton = { TextButton(onClick = onConfirm) { Text("Free up space") } },
        dismissButton = { TextButton(onClick = onCancel) { Text("Keep the audio") } },
    )
}

/**
 * Confirms a filter change that would invalidate audio already made for this book.
 *
 * Asked only when rendered audio is actually affected. A narrated book's audio carries the filters
 * that were in force when it was made, so changing one afterwards leaves the book saying one thing
 * and sounding like another until it is re-rendered -- and re-rendering means discarding audio the
 * listener has already waited for. That is their decision, not one to make quietly on their behalf.
 */
@Composable
private fun NarrationFilterChangeDialog(
    impact: FilterChangeImpact.Rerender,
    onConfirm: () -> Unit,
    onDecline: () -> Unit,
) {
    AlertDialog(
        // Dismissing is declining. The safe reading of a tap outside is "I did not mean to do
        // that", and nothing has been written or discarded yet.
        onDismissRequest = onDecline,
        icon = { Icon(Icons.Outlined.Refresh, null, tint = ChoiceGreen) },
        title = { Text("Make this audio again?") },
        text = {
            Column {
                val chapters = if (impact.chapterCount == 1) "chapter" else "chapters"
                Text(
                    "This filter change affects ${impact.chapterCount} $chapters that already " +
                        "have audio. That audio was made with your previous filters, so it has " +
                        "to be made again to match.",
                    fontSize = 13.sp,
                )
                Spacer(Modifier.height(10.dp))
                val minutes = if (impact.estimatedMinutes == 1) "minute" else "minutes"
                Text(
                    "About ${impact.estimatedMinutes} $minutes.",
                    color = ChoiceMuted,
                    fontSize = 12.sp,
                )
                // Named separately because it is the one part of a re-render that costs more than
                // waiting: the text of those chapters leaves the device again.
                if (impact.chaptersResynthesizedByPremiumVoice > 0) {
                    Spacer(Modifier.height(6.dp))
                    val sent = impact.chaptersResynthesizedByPremiumVoice
                    Text(
                        "$sent will be sent to the premium voice again.",
                        color = ChoiceMuted,
                        fontSize = 12.sp,
                    )
                }
                Spacer(Modifier.height(10.dp))
                Text(
                    "Your place in the book is kept.",
                    color = ChoiceMuted,
                    fontSize = 12.sp,
                )
            }
        },
        confirmButton = { TextButton(onClick = onConfirm) { Text("Make it again") } },
        dismissButton = { TextButton(onClick = onDecline) { Text("Keep it as it is") } },
    )
}

/**
 * Where a narrated book's flagged passage sits, expressed as a share of the book.
 *
 * A narrated book's scan events carry **character offsets** in the same `startTime` field an
 * audiobook uses for seconds. That reuse is what lets the whole existing filter stack work
 * unchanged, and it is also the single most dangerous thing about the contract: formatting one as a
 * timestamp turns offset 84,000 into "23:20:00", which looks authoritative and means nothing.
 *
 * A percentage is the honest reading. It is what a character offset actually tells a listener, it
 * needs no timings to compute, and it stays correct while a book is only part-rendered — unlike a
 * time, which does not exist until the audio does.
 */
private fun narratedPositionLabel(offset: Double?, bookTextLength: Int?): String? {
    if (offset == null || bookTextLength == null || bookTextLength <= 0) return null
    val share = (offset / bookTextLength).coerceIn(0.0, 1.0)
    // Rounded to whole percent: a flagged passage is a place in a book, not a coordinate, and
    // decimals would imply a precision the number does not carry.
    return "${(share * 100).toInt()}% through the book"
}

/**
 * Voice selection for a narrated book.
 *
 * Presents only what the tier allows, and says which voices keep the text on the device. No
 * purchase control and no price appear: billing is not built, and offering a listener a way to
 * buy something that cannot be bought would be worse than offering nothing.
 */
@Composable
private fun NarrationVoiceDialog(
    state: NarrationUiState,
    onSelect: (SelectedVoice) -> Unit,
    onDismiss: () -> Unit,
) {
    // Not yet measured on a device, so the local neural voice is withheld rather than offered
    // and then found to be too slow. NarrationTierStore already models the distinction.
    val localNeuralSupported = false
    val kinds = state.availableVoiceKinds(localNeuralSupported)

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Outlined.RecordVoiceOver, null, tint = ChoiceGreen) },
        title = { Text("Choose a voice") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                if (state.tier?.isConfirmed == false) {
                    Text(
                        "Your subscription could not be checked just now, so premium voices " +
                            "may not appear. Everything already read aloud keeps playing.",
                        color = ChoiceMuted,
                        fontSize = 12.sp,
                        lineHeight = 17.sp,
                    )
                    Spacer(Modifier.height(12.dp))
                }
                kinds.forEach { kind ->
                    val selected = state.selectedVoice?.kind == kind
                    Card(
                        Modifier.fillMaxWidth().padding(bottom = 8.dp).clickable {
                            onSelect(SelectedVoice(kind, defaultVoiceID(kind)))
                        },
                        colors = CardDefaults.cardColors(
                            containerColor = if (selected) Color(0xFF343B38) else ChoiceSurface,
                        ),
                        shape = RoundedCornerShape(12.dp),
                    ) {
                        Row(
                            Modifier.fillMaxWidth().padding(14.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column(Modifier.weight(1f)) {
                                Text(voiceKindLabel(kind), fontWeight = FontWeight.SemiBold)
                                Text(
                                    voiceKindDescription(kind),
                                    color = ChoiceMuted,
                                    fontSize = 11.sp,
                                    lineHeight = 16.sp,
                                )
                            }
                            if (selected) {
                                Icon(Icons.Outlined.Check, "Selected", tint = ChoiceGreen)
                            }
                        }
                    }
                }
                if (!kinds.contains(VoiceKind.PREMIUM)) {
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "The premium voice is part of a paid plan that is not on sale yet.",
                        color = ChoiceMuted,
                        fontSize = 11.sp,
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Done") } },
    )
}

private fun voiceKindLabel(kind: VoiceKind): String = when (kind) {
    VoiceKind.SYSTEM -> "Your phone's voice"
    VoiceKind.LOCAL_NEURAL -> "Enhanced on-device voice"
    VoiceKind.PREMIUM -> "Premium voice"
}

/**
 * Says plainly which voices send the book's text off the device.
 *
 * The distinction is the one thing a listener choosing a voice most needs to know, and it is
 * read from [NarrationTiers.sendsTextOffDevice] rather than restated, so the copy cannot end up
 * claiming something different from what the render path does.
 */
private fun voiceKindDescription(kind: VoiceKind): String {
    val privacy = if (NarrationTiers.sendsTextOffDevice(kind)) {
        "Sends each chapter's text to AudioChoice to be turned into audio."
    } else {
        "Works offline. Nothing from this book leaves your phone."
    }
    val quality = when (kind) {
        VoiceKind.SYSTEM -> "Always available, free."
        VoiceKind.LOCAL_NEURAL -> "Better than the built-in voice, free."
        VoiceKind.PREMIUM -> "The most natural voice, closest to a human narrator."
    }
    return "$quality $privacy"
}

private fun defaultVoiceID(kind: VoiceKind): String = when (kind) {
    VoiceKind.SYSTEM -> "system-default"
    VoiceKind.LOCAL_NEURAL -> "local-neural-default"
    // Measured as the clearest of the generative voices during verification.
    VoiceKind.PREMIUM -> "Ruth"
}

// endregion

// region the on-device voice measurement
//
// A screen rather than an instrumented benchmark because the machine that builds AudioChoice
// cannot reach a phone: it is an EC2 Mac on a private subnet. Two design values depend on this
// measurement -- whether to offer an on-device neural voice at all, and how many chapters to keep
// rendered ahead of the playhead -- and both were specified as measurements rather than estimates.
// Inventing one was already tried and got a speech rate wrong by a third.

/**
 * Runs the device's own voice engine over a fixed passage and shows how fast it was.
 *
 * The result is presented as a block of text to copy out, because a number that stays on the phone
 * is not a measurement anybody can act on.
 */
@Composable
private fun VoiceMeasurementScreen(onBack: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    var running by remember { mutableStateOf(false) }
    var outcome by remember { mutableStateOf<RateMeasurementOutcome?>(null) }
    val clipboard = androidx.compose.ui.platform.LocalClipboardManager.current

    Column(
        Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()
            .verticalScroll(rememberScrollState()).padding(18.dp),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.Outlined.ArrowBack, "Back") }
            Text(
                "Voice speed test",
                Modifier.weight(1f),
                textAlign = TextAlign.Center,
                fontWeight = FontWeight.SemiBold,
            )
            Spacer(Modifier.size(48.dp))
        }
        Spacer(Modifier.height(14.dp))
        Text(
            "Reads a short fixed passage with your phone's own voice and times it. Nothing is " +
                "uploaded and no audio is kept. Takes about ten seconds.",
            color = ChoiceMuted,
            fontSize = 13.sp,
            lineHeight = 19.sp,
        )
        Spacer(Modifier.height(18.dp))
        Button(
            onClick = {
                running = true
                outcome = null
            },
            enabled = !running,
            modifier = Modifier.fillMaxWidth().height(50.dp),
            shape = RoundedCornerShape(12.dp),
        ) {
            if (running) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(10.dp))
                Text("Measuring…")
            } else {
                Text("Run the speed test")
            }
        }

        if (running) {
            LaunchedEffect(Unit) {
                val measurement = OnDeviceRateMeasurement(
                    context = context,
                    scratchDirectory = java.io.File(context.cacheDir, "voice-benchmark"),
                )
                outcome = measurement.measure()
                running = false
            }
        }

        outcome?.let { result ->
            Spacer(Modifier.height(20.dp))
            Card(
                colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
                shape = RoundedCornerShape(14.dp),
                modifier = Modifier.fillMaxWidth(),
            ) {
                Column(Modifier.padding(16.dp)) {
                    when (result) {
                        is RateMeasurementOutcome.Measured -> {
                            Text("Result", fontWeight = FontWeight.SemiBold)
                            Spacer(Modifier.height(10.dp))
                            // Monospaced and selectable, because this text exists to be copied
                            // out accurately rather than skimmed.
                            androidx.compose.foundation.text.selection.SelectionContainer {
                                Text(
                                    result.report,
                                    fontFamily = FontFamily.Monospace,
                                    fontSize = 12.sp,
                                    lineHeight = 18.sp,
                                )
                            }
                            Spacer(Modifier.height(14.dp))
                            Button(
                                onClick = {
                                    clipboard.setText(
                                        androidx.compose.ui.text.AnnotatedString(result.report),
                                    )
                                },
                                modifier = Modifier.fillMaxWidth(),
                            ) { Text("Copy result") }
                            Spacer(Modifier.height(10.dp))
                            Text(
                                if (OnDeviceRate.isFastEnough(result.rate.realTimeFactor)) {
                                    "This phone is fast enough to read books aloud with its own " +
                                        "voice, keeping " +
                                        "${OnDeviceRate.renderAheadChapters(result.rate.realTimeFactor)} " +
                                        "chapter(s) ready ahead of you."
                                } else {
                                    "This phone's own voice is too slow to stay ahead of " +
                                        "listening. It would keep pausing to catch up."
                                },
                                color = ChoiceMuted,
                                fontSize = 12.sp,
                                lineHeight = 18.sp,
                            )
                        }

                        RateMeasurementOutcome.NoEngineInstalled -> Text(
                            "This phone has no text-to-speech engine installed, or it did not " +
                                "start. Install Google Text-to-Speech from the Play Store and " +
                                "try again.",
                            color = ChoiceMuted,
                            fontSize = 13.sp,
                            lineHeight = 19.sp,
                        )

                        is RateMeasurementOutcome.LanguageUnavailable -> Text(
                            "The voice engine has no English voice installed " +
                                "(${result.languageTag}). Add one in Settings, then try again.",
                            color = ChoiceMuted,
                            fontSize = 13.sp,
                            lineHeight = 19.sp,
                        )

                        is RateMeasurementOutcome.Failed -> Text(
                            result.message,
                            color = MaterialTheme.colorScheme.error,
                            fontSize = 13.sp,
                            lineHeight = 19.sp,
                        )
                    }
                }
            }
        }
    }
}

// endregion


/**
 * The statement a listener accepts before any chapter's text leaves the device.
 *
 * The wording comes from the server, so a change to who receives the text reaches every client at
 * once rather than waiting for an app update. Presented before anything is recorded, and on decline
 * nothing at all changes -- no record, no selected voice, no submission.
 */
@Composable
private fun PremiumAgreementDialog(
    gate: com.audiochoice.mobile.narration.voice.PremiumVoiceGate,
    onAccept: () -> Unit,
    onDecline: () -> Unit,
) {
    val changed = gate as? com.audiochoice.mobile.narration.voice.PremiumVoiceGate.AgreementChanged
    val required = gate as? com.audiochoice.mobile.narration.voice.PremiumVoiceGate.AgreementRequired
    val statement = changed?.text ?: required?.text.orEmpty()

    if (gate is com.audiochoice.mobile.narration.voice.PremiumVoiceGate.NotEntitled) {
        AlertDialog(
            onDismissRequest = onDecline,
            icon = { Icon(Icons.Outlined.RecordVoiceOver, null, tint = ChoiceGreen) },
            title = { Text("Premium voice") },
            text = {
                Text(
                    "The premium voice is part of a paid plan that is not on sale yet. Your " +
                        "phone's own voice reads books aloud for free and sends nothing anywhere.",
                    color = ChoiceMuted,
                    fontSize = 13.sp,
                    lineHeight = 19.sp,
                )
            },
            confirmButton = { TextButton(onClick = onDecline) { Text("Close") } },
        )
        return
    }

    AlertDialog(
        onDismissRequest = onDecline,
        icon = { Icon(Icons.Outlined.RecordVoiceOver, null, tint = ChoiceGreen) },
        title = {
            Text(
                if (changed != null) "The premium voice terms have changed" else "Using the premium voice",
            )
        },
        text = {
            Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) {
                if (changed != null) {
                    Text(
                        "You agreed to version ${changed.acceptedVersion}. " +
                            "Version ${changed.currentVersion} is now in force. Chapters already " +
                            "made stay on your phone and keep playing.",
                        color = ChoiceMuted,
                        fontSize = 12.sp,
                        lineHeight = 18.sp,
                    )
                    Spacer(Modifier.height(12.dp))
                }
                Text(
                    statement.ifBlank {
                        "AudioChoice could not load the premium voice terms just now. Try again " +
                            "when you have a connection."
                    },
                    fontSize = 13.sp,
                    lineHeight = 20.sp,
                )
            }
        },
        confirmButton = {
            Button(onClick = onAccept, enabled = statement.isNotBlank()) {
                Text(if (changed != null) "Accept and continue" else "Accept")
            }
        },
        dismissButton = {
            TextButton(onClick = onDecline) { Text("Use my phone's voice") }
        },
    )
}
