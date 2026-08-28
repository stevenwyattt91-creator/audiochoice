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
import com.audiochoice.mobile.data.AuthUser
import com.audiochoice.mobile.data.LibraryBook
import com.audiochoice.mobile.data.ExploreCatalogBook
import com.audiochoice.mobile.data.ReaderTimingRange
import com.audiochoice.mobile.importing.ImportPhase
import com.audiochoice.mobile.importing.ImportViewModel
import com.audiochoice.mobile.library.LibraryViewModel
import com.audiochoice.mobile.player.PlayerViewModel
import com.audiochoice.mobile.player.FilterAvailability
import com.audiochoice.mobile.player.PlayerUiState
import com.audiochoice.mobile.player.enabledScanEvents
import com.audiochoice.mobile.reader.ReaderMask
import com.audiochoice.mobile.reader.ReaderSettings
import com.audiochoice.mobile.reader.ReaderTheme
import com.audiochoice.mobile.reader.indexOfCharacter
import com.audiochoice.mobile.reader.merged
import com.audiochoice.mobile.reader.readerCharacterForTime
import com.audiochoice.mobile.reader.readerDisplayParagraphs
import com.audiochoice.mobile.reader.readerTimeForCharacter
import com.audiochoice.mobile.player.PlaybackFilterTaxonomy
import com.audiochoice.mobile.security.ParentalControlsStore
import com.audiochoice.mobile.support.SupportViewModel
import com.audiochoice.mobile.ui.theme.ChoiceGreen
import com.audiochoice.mobile.ui.theme.ChoiceMuted
import com.audiochoice.mobile.ui.theme.ChoiceOutline
import com.audiochoice.mobile.ui.theme.ChoiceSurface

@Composable
fun AudioChoiceApp(
    auth: AuthViewModel,
    importer: ImportViewModel,
    library: LibraryViewModel,
    player: PlayerViewModel,
    support: SupportViewModel,
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
            )
            else -> LibraryShell(
                state.session!!.user,
                state.session!!.accessToken,
                importer,
                library,
                player,
                support,
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
) {
    var registering by rememberSaveable { mutableStateOf(false) }
    var name by rememberSaveable { mutableStateOf("") }
    var email by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }

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

        if (registering) AudioField(name, { name = it }, "Name")
        AudioField(email, { email = it }, "Email address")
        AudioField(password, { password = it }, "Password", password = true)

        Button(
            onClick = {
                if (registering) onRegister(name, email, password) else onLogin(email, password)
            },
            enabled = !busy && email.isNotBlank() && password.isNotBlank(),
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
        TextButton(onClick = { registering = !registering; onDismissError() }, enabled = !busy) {
            Text(if (registering) "Already have an account? Sign in" else "New to AudioChoice? Create an account")
        }
        error?.let {
            Text(it, color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center, fontSize = 13.sp)
        }
        Spacer(Modifier.weight(1f))
        Text("Private by design • Your audio remains yours", color = ChoiceMuted, fontSize = 12.sp)
        Spacer(Modifier.height(20.dp))
    }
}

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
    user: AuthUser,
    accessToken: String,
    importer: ImportViewModel,
    library: LibraryViewModel,
    player: PlayerViewModel,
    support: SupportViewModel,
    onLogout: () -> Unit,
    incomingAudioUri: StateFlow<Uri?>,
    incomingCompanionTransferUri: StateFlow<Uri?>,
    onExternalAudioHandled: () -> Unit,
    onCompanionTransferHandled: () -> Unit,
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    var selected by rememberSaveable { mutableIntStateOf(0) }
    var detailBook by remember { mutableStateOf<LibraryBook?>(null) }
    var showingBookFilters by remember { mutableStateOf(false) }
    var profilePage by rememberSaveable { mutableStateOf(ProfilePage.MAIN) }
    var librarySection by rememberSaveable { mutableStateOf(LibrarySection.MY_LIBRARY) }
    var searchExpanded by rememberSaveable { mutableStateOf(false) }
    var searchQuery by rememberSaveable { mutableStateOf("") }
    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val tabs = listOf("Library", "Player", "Import", "Profile")
    val tabIcons = listOf(Icons.Outlined.LibraryBooks, Icons.Outlined.GraphicEq, Icons.Outlined.FileDownload, Icons.Outlined.PersonOutline)
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
    var progressHydrated by rememberSaveable(user.id) { mutableStateOf(false) }
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
        if (!libraryState.loaded || libraryState.loading || progressHydrated) return@LaunchedEffect
        progressHydrated = true
        player.hydrateAccountProgress(
            books = libraryState.books,
            accessToken = accessToken,
            onPositionAvailable = library::updatePlaybackPosition,
            onSynced = { library.load(accessToken, user.id, force = true) },
        )
        // Restore the last-open book into the player so the Player tab is not
        // empty after a process restart. The position is recovered by open()
        // through resumePositionMs, which reads the local SharedPreferences
        // checkpoint written with commit() in saveProgressSync().
        if (player.state.value.book == null) {
            val lastBookID = player.lastOpenBookID()
            if (lastBookID != null) {
                libraryState.books.firstOrNull { it.id == lastBookID }?.let { book ->
                    player.open(book, accessToken)
                }
            }
        }
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
                                libraryState.books.firstOrNull()?.let { player.open(it, accessToken) }
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
                        books = libraryState.books,
                        coverPaths = libraryState.coverPaths,
                        loading = libraryState.loading,
                        query = searchQuery,
                        onImport = { selected = 2 },
                        onOpenBook = { book -> player.open(book, accessToken); detailBook = book },
                        // Only the green Continue button skips the details sheet
                        // and resumes in the player.
                        onPlayNow = { book ->
                            player.openAndStart(book, accessToken, fromBeginning = false)
                            selected = 1
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
                else PlayerScreen(player, filtersLocked, onBack = {
                    player.saveProgress()
                    detailBook = player.state.value.book
                    selected = 0
                })
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
                        onLogout = onLogout,
                    )
                    ProfilePage.FAQ -> FaqScreen { profilePage = ProfilePage.MAIN }
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
                }
            }
        }
    }
    }
}

private enum class ProfilePage { MAIN, FAQ, SUPPORT, PARENTAL_CONTROLS }
private enum class LibrarySection { MY_LIBRARY, EXPLORE }
private enum class LibrarySort(val label: String) { RECENT("Recently Added"), A_TO_Z("A–Z"), Z_TO_A("Z–A") }

@Composable
private fun FirstRunGuide(onFinished: () -> Unit) {
    var page by rememberSaveable { mutableIntStateOf(0) }
    val slides = listOf(
        Triple(
            Icons.Outlined.Headphones,
            "Listen Your Way",
            "Import audiobook files you already own. AudioChoice finds potentially sensitive moments, then lets you choose what plays or skips for each book.",
        ),
        Triple(
            Icons.Outlined.CloudDone,
            "Scan once, reuse securely",
            "If AudioChoice already recognizes your exact audiobook edition, its saved filter scan is ready without uploading or transcribing it again. New scans continue privately in the cloud even if you close the app.",
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
    loading: Boolean,
    query: String,
    onImport: () -> Unit,
    /** Opens the book details sheet. Every artwork and row tap lands here. */
    onOpenBook: (LibraryBook) -> Unit,
    /** Resumes straight into the player. Only the green Continue button does this. */
    onPlayNow: (LibraryBook) -> Unit,
) {
    var sort by rememberSaveable { mutableStateOf(LibrarySort.RECENT) }
    var sortMenu by remember { mutableStateOf(false) }
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
    val featured = books.firstOrNull { it.playbackPositionSeconds > 0 && !it.isFinished } ?: books.first()
    val visibleBooks = books.filter { book ->
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
                    )
                    Column(Modifier.align(Alignment.BottomStart).fillMaxWidth().background(Color(0xE6101514)).padding(14.dp)) {
                        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(featured.title, fontSize = 18.sp, fontWeight = FontWeight.SemiBold, maxLines = 1)
                                Text(featured.author ?: "Imported audiobook", color = ChoiceMuted, fontSize = 12.sp)
                            }
                            FilledIconButton(onClick = { onPlayNow(featured) }, colors = IconButtonDefaults.filledIconButtonColors(containerColor = ChoiceGreen)) {
                                Icon(Icons.Outlined.PlayArrow, "Resume listening", tint = Color.Black)
                            }
                        }
                        Spacer(Modifier.height(8.dp))
                        LinearProgressIndicator(
                            progress = { ((featured.playbackPositionSeconds / (featured.fingerprint.duration ?: 1.0)).coerceIn(0.0, 1.0)).toFloat() },
                            modifier = Modifier.fillMaxWidth(),
                        )
                    }
                }
            }
        }
        Spacer(Modifier.height(22.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("Audiobooks", Modifier.weight(1f), fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
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
            Text("No audiobooks match “$query”.", color = ChoiceMuted, modifier = Modifier.padding(vertical = 28.dp))
        } else sortedBooks.forEach { book ->
            LibraryBookRow(
                book,
                coverPaths[book.fingerprint.sha256.lowercase()],
                onOpenBook,
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
) {
    Card(
        Modifier.fillMaxWidth().padding(bottom = 10.dp).clickable { onOpen(book) },
        colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
        shape = RoundedCornerShape(13.dp),
    ) {
        Row(Modifier.fillMaxWidth().padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            BookArtwork(coverPath, Modifier.size(width = 62.dp, height = 80.dp))
            Spacer(Modifier.width(13.dp))
            Column(Modifier.weight(1f)) {
                Text(book.title, fontWeight = FontWeight.SemiBold, maxLines = 2)
                Text(book.author ?: "Imported audiobook", color = ChoiceMuted, fontSize = 12.sp)
                Spacer(Modifier.height(6.dp))
                Text(formatDuration(book.fingerprint.duration), color = ChoiceGreen, fontSize = 11.sp)
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
    var showGraphicAudioInstructions by rememberSaveable { mutableStateOf(false) }
    // Explore purchases currently route through Audible while the catalog
    // provider migration is in progress.
    val requiresGraphicAudioInstructions = false
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
                    Spacer(Modifier.height(20.dp))
                    Card(colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(15.dp)) {
                        Column(Modifier.fillMaxWidth().padding(16.dp)) {
                            Text("About this audiobook", fontWeight = FontWeight.SemiBold)
                            Spacer(Modifier.height(8.dp))
                            Text(
                                item.description ?: localExploreDescription(item.title, item.author)
                                    ?: "A synopsis is not available yet for this audiobook edition.",
                                color = ChoiceMuted,
                                fontSize = 13.sp,
                                lineHeight = 19.sp,
                            )
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
                    if (!owned && item.purchaseProvider.equals("GraphicAudio", ignoreCase = true)) {
                        Card(
                            colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
                            shape = RoundedCornerShape(15.dp),
                        ) {
                            Row(
                                Modifier.fillMaxWidth().padding(16.dp),
                                verticalAlignment = Alignment.Top,
                            ) {
                                Icon(Icons.Outlined.Info, null, tint = ChoiceGreen)
                                Spacer(Modifier.width(11.dp))
                                Column {
                                    Text("Buying for AudioChoice", fontWeight = FontWeight.SemiBold)
                                    Spacer(Modifier.height(5.dp))
                                    Text(
                                        "Choose M4B Zip Download on GraphicAudio. After purchasing, download and unzip it, then import the .m4b file into AudioChoice. The Access App and Browser Player option alone does not include an importable file.",
                                        color = ChoiceMuted,
                                        fontSize = 12.sp,
                                        lineHeight = 18.sp,
                                    )
                                }
                            }
                        }
                        Spacer(Modifier.height(12.dp))
                    }
                    if (!owned && item.purchaseVerified) {
                        Text("Verified ${item.purchaseProvider} listing", color = ChoiceGreen, fontSize = 12.sp)
                        Spacer(Modifier.height(8.dp))
                    }
                }
                Button(
                    onClick = {
                        if (requiresGraphicAudioInstructions) showGraphicAudioInstructions = true
                        else onAction()
                    },
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
    if (showGraphicAudioInstructions) {
        AlertDialog(
            onDismissRequest = { showGraphicAudioInstructions = false },
            icon = { Icon(Icons.Outlined.Download, null, tint = ChoiceGreen) },
            title = { Text("Choose M4B Zip Download") },
            text = {
                Text(
                    "On GraphicAudio, select M4B Zip Download—not Access App and Browser Player alone. After purchasing, download and unzip the file, then import the .m4b file into AudioChoice.",
                    lineHeight = 20.sp,
                )
            },
            confirmButton = {
                Button(onClick = {
                    showGraphicAudioInstructions = false
                    onAction()
                }) { Text("Continue to GraphicAudio") }
            },
            dismissButton = {
                TextButton(onClick = { showGraphicAudioInstructions = false }) { Text("Cancel") }
            },
        )
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
    onFaq: () -> Unit,
    onSupport: () -> Unit,
    onParentalControls: () -> Unit,
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
                Spacer(Modifier.width(14.dp)); Column { Text(user.displayName.ifBlank { "AudioChoice listener" }, fontSize = 19.sp); Text(user.email, color = ChoiceMuted); Text("Signed in with ${user.provider}", color = ChoiceGreen, fontSize = 12.sp) }
            }
        }
        Spacer(Modifier.height(18.dp))
        Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = ChoiceSurface), shape = RoundedCornerShape(14.dp)) {
            ProfileRow(Icons.Outlined.Lock, "Parental Controls", "Protect audiobook filters with a PIN", onParentalControls)
            HorizontalDivider(color = ChoiceOutline)
            ProfileRow(Icons.Outlined.HelpOutline, "FAQs", "Answers about importing, privacy, and filters", onFaq)
            HorizontalDivider(color = ChoiceOutline)
            ProfileRow(Icons.Outlined.SupportAgent, "Support", "Send a message to the AudioChoice team", onSupport)
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

private val audioChoiceFaqs = listOf(
    FaqItem("Where can I obtain audiobooks?", "You can buy DRM-free audiobook downloads from stores such as Libro.fm and import the downloaded files. You can also import supported audiobooks you lawfully obtained elsewhere."),
    FaqItem("How do I import a Libro.fm audiobook?", "Download the audiobook file from your Libro.fm library, open AudioChoice, choose Import, and select the downloaded MP3 or M4B file."),
    FaqItem(
        "How do I download an Audible AAX file on Android or iPhone?",
        "Open Audible's website in Chrome or Safari and sign in. Open the browser menu and turn on Desktop site (Chrome) or Request Desktop Website (Safari). Go to Library, find the audiobook, and choose Download. Save the AAX file in Downloads on Android or in the Files app on iPhone/iPad. Then open AudioChoice, choose Import, and select that AAX file. A download made inside the Audible app is stored privately and normally cannot be selected by AudioChoice.",
    ),
    FaqItem(
        "How do I transfer an Audible audiobook from a computer?",
        "On a laptop or desktop, sign in at Audible's website, open Library, and choose Download beside the audiobook to save its AAX file. For Android, transfer it with a USB cable, Quick Share, or a cloud drive and save it in Downloads. For iPhone or iPad, use AirDrop, iCloud Drive, Finder, or another cloud drive and save it in the Files app. In AudioChoice, choose Import and select the transferred AAX file.",
    ),
    FaqItem("Which file types are supported?", "AudioChoice supports MP3, M4A, M4B, and AAX imports. AAX files are converted locally to M4B before scanning."),
    FaqItem("Does AudioChoice keep my audiobook?", "No. Your playable audiobook stays on your device. Temporary private processing data is removed after the scan; reusable transcript analysis and filter timing data are retained so the same edition does not need to be scanned again."),
    FaqItem("Can I close the app during AAX conversion?", "Keep AudioChoice open until the local conversion finishes. If it is interrupted, reopen the app and select the same AAX file to resume or restart safely."),
    FaqItem("Why must I reimport on another device?", "AudioChoice does not store your audiobook files. Your account data can follow you, but each device needs its own local copy of the audio."),
    FaqItem("How do playback filters work?", "Every category detected for an audiobook starts on. You can turn off a whole category or an individual subfilter, and AudioChoice remembers those choices for that audiobook only."),
    FaqItem("Can AudioChoice skip an entire scene?", "Yes. When a scan identifies a complete scene as a filter event, playback can jump from the start of that event to its end."),
    FaqItem("What if a filter is incorrect?", "Use Support to report the audiobook and category. Never attach the audiobook file; include the title and approximate playback time."),
)

@Composable
private fun FaqScreen(onBack: () -> Unit) {
    var expanded by rememberSaveable { mutableIntStateOf(-1) }
    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Frequently Asked Questions", onBack)
        Column(Modifier.verticalScroll(rememberScrollState())) {
            audioChoiceFaqs.forEachIndexed { index, item ->
                Card(
                    Modifier.fillMaxWidth().padding(bottom = 9.dp).clickable { expanded = if (expanded == index) -1 else index },
                    colors = CardDefaults.cardColors(containerColor = ChoiceSurface),
                    shape = RoundedCornerShape(12.dp),
                ) {
                    Column(Modifier.padding(16.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(item.question, Modifier.weight(1f), fontWeight = FontWeight.SemiBold)
                            Icon(if (expanded == index) Icons.Outlined.ExpandLess else Icons.Outlined.ExpandMore, null, tint = ChoiceGreen)
                        }
                        if (expanded == index) {
                            Spacer(Modifier.height(10.dp))
                            Text(item.answer, color = ChoiceMuted, lineHeight = 20.sp, fontSize = 13.sp)
                        }
                    }
                }
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
        BookArtwork(state.coverPath ?: coverPath, Modifier.size(220.dp))
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
private fun BookArtwork(coverPath: String?, modifier: Modifier = Modifier) {
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

private fun localExploreDescription(title: String, author: String?): String? =
    if (title.contains("Dungeon Crawler Carl", ignoreCase = true) &&
        author?.contains("Matt Dinniman", ignoreCase = true) == true) {
        "Carl and Princess Donut are forced into a planet-spanning dungeon crawl after Earth becomes a deadly televised game. Staying alive means surviving bizarre levels, building unlikely alliances, and keeping an audience entertained."
    } else null

private fun com.audiochoice.mobile.player.PlayerUiState.resultVersion(): String = when {
    // Never report "Clean" when the scan simply could not be loaded -- that
    // reads as "nothing to filter" while nothing is actually being filtered.
    filterAvailability == FilterAvailability.LOADING -> "Checking…"
    filterAvailability == FilterAvailability.UNAVAILABLE -> "Unavailable"
    scanEvents.isEmpty() -> "Clean"
    else -> "${PlaybackFilterTaxonomy.controlCount(scanEvents)} filter controls"
}

@Composable
private fun PlayerScreen(player: PlayerViewModel, filtersLocked: Boolean, onBack: (() -> Unit)? = null) {
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
                IconButton(
                    onClick = {
                        if (state.epubText != null) {
                            readerMode = !readerMode
                        }
                    },
                    enabled = state.epubText != null,
                ) {
                    // Open book invites opening the reader; closed book invites
                    // returning to the player.
                    Icon(
                        if (readerMode) Icons.Outlined.Book else Icons.Outlined.MenuBook,
                        if (readerMode) "Close reading edition" else "Open reading edition",
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
        Spacer(Modifier.height(20.dp))
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
                    Text(
                        "Filters are not active. This audiobook's scan could not be loaded.",
                        color = MaterialTheme.colorScheme.error,
                        fontSize = 12.sp,
                    )
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
                Text("-${formatTime((durationMs - displayedPositionMs).coerceAtLeast(0))}", color = ChoiceMuted, fontSize = 12.sp)
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
            }
        }
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
        val narratedCharacter = remember(state.positionMs, state.readerTimingRanges) {
            readerCharacterForTime(state.readerTimingRanges, state.positionMs / 1000.0)
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
                        fontSize = (READER_BASE_FONT_SP * settings.fontScale).sp,
                        lineHeight = (READER_BASE_LINE_SP * settings.fontScale).sp,
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
                        Text("Follow the audiobook", fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                        Text(
                            "Highlights and scrolls to the passage being narrated, and lets you " +
                                "tap a paragraph to jump the audio there.",
                            color = ChoiceMuted,
                            fontSize = 11.sp,
                        )
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
            ImportPhase.IDLE -> "Import Audiobook"
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
                Text("Choose an audiobook from this device", color = ChoiceMuted)
                Spacer(Modifier.height(22.dp))
                Button(onClick = { picker.launch(arrayOf("audio/*", "application/octet-stream")) }) {
                    Text("Browse Files")
                }
            }
        }
        Spacer(Modifier.height(24.dp))
        Text("Supported Formats", color = ChoiceMuted)
        Text("MP3   M4B   M4A   AAX", color = ChoiceGreen, fontWeight = FontWeight.SemiBold)
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
