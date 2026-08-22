import AppKit
import SwiftUI
import UniformTypeIdentifiers

extension UTType {
    static var aax: UTType {
        UTType(filenameExtension: "aax") ?? .data
    }
}

enum SidebarDestination: String, CaseIterable, Identifiable {
    case library
    case recentlyAdded
    case importAudiobook
    case player
    case settings

    var id: String {
        rawValue
    }

    var title: String {
        switch self {
        case .library:
            return "Library"

        case .recentlyAdded:
            return "Recently Added"

        case .importAudiobook:
            return "Import Audiobook"

        case .player:
            return "Now Playing"

        case .settings:
            return "Settings"
        }
    }

    var icon: String {
        switch self {
        case .library:
            return "books.vertical.fill"

        case .recentlyAdded:
            return "clock.arrow.circlepath"

        case .importAudiobook:
            return "square.and.arrow.down"

        case .player:
            return "play.circle.fill"

        case .settings:
            return "slider.horizontal.3"
        }
    }
}

private enum PlayerSheet: String, Identifiable {
    case chapters
    case bookmarks
    case sleep
    case filters

    var id: String {
        rawValue
    }
}

struct ContentView: View {

    @StateObject private var library = LibraryManager()
    @StateObject private var playback = PlaybackService()
    @StateObject private var filterManager = FilterManager()

    @State private var filterBookID: UUID?
    
    @State private var showingFilePicker = false
    @State private var selectedBookID: UUID?
    @State private var selectedDestination: SidebarDestination? = .library

    @State private var playerBookID: UUID?
    @State private var sliderPosition: Double = 0
    @State private var isDraggingSlider = false
    @State private var activePlayerSheet: PlayerSheet?

    @State private var showingDuplicateAlert = false
    @State private var duplicateBookTitle = ""
    @State private var importErrorMessage: String?

    @State private var bookPendingDeletion: Book?
    @State private var showingDeleteConfirmation = false
    @State private var detailBookForFilters: Book?

    var body: some View {
        NavigationSplitView {
            sidebar
        } detail: {
            detailView
        }
        .fileImporter(
            isPresented: $showingFilePicker,
            allowedContentTypes: [
                .mp3,
                .mpeg4Audio,
                .audio,
                .aax
            ],
            allowsMultipleSelection: false
        ) { result in
            handleImport(result)
        }
        .alert(
            "Already in Your Library",
            isPresented: $showingDuplicateAlert
        ) {
            Button("Open Book") {
                selectedDestination = .library
            }
            
            Button("OK", role: .cancel) {
            }
        } message: {
            Text(
                "\(duplicateBookTitle) is already in your AudioChoice library."
            )
        }
        .alert(
            "Import Failed",
            isPresented: Binding(
                get: {
                    importErrorMessage != nil
                },
                set: { newValue in
                    if !newValue {
                        importErrorMessage = nil
                    }
                }
            )
        ) {
            Button("OK", role: .cancel) {
                importErrorMessage = nil
            }
        } message: {
            Text(
                importErrorMessage
                ?? "The audiobook could not be imported."
            )
        }
        .confirmationDialog(
            "Remove Audiobook?",
            isPresented: $showingDeleteConfirmation,
            titleVisibility: .visible
        ) {
            Button(
                "Remove from Library",
                role: .destructive
            ) {
                deletePendingBook()
            }
            
            Button(
                "Cancel",
                role: .cancel
            ) {
                bookPendingDeletion = nil
            }
        } message: {
            Text(deleteConfirmationMessage)
        }
        .onChange(of: selectedDestination) {
            selectedBookID = nil
        }
        .onAppear {
            playback.onProgressUpdate = {
                bookID,
                currentPosition,
                playbackSpeed in
                
                library.updatePlayback(
                    bookID: bookID,
                    currentPosition: currentPosition,
                    playbackSpeed: playbackSpeed
                )
            }
        }
    }

    // MARK: - Sidebar

    private var sidebar: some View {
        List(selection: $selectedDestination) {
            Section("AudioChoice") {
                ForEach(SidebarDestination.allCases) { destination in
                    Label(
                        destination.title,
                        systemImage: destination.icon
                    )
                    .tag(destination)
                }
            }
        }
        .navigationTitle("AudioChoice")
        .frame(minWidth: 220)
    }

    // MARK: - Main Navigation

    @ViewBuilder
    private var detailView: some View {
        if let selectedBook {
            bookDetailView(for: selectedBook)
        } else {
            switch selectedDestination {
            case .library:
                libraryView

            case .recentlyAdded:
                recentlyAddedView

            case .importAudiobook:
                importView

            case .player:
                playerView

            case .settings:
                settingsView

            case .none:
                libraryView
            }
        }
    }

    private var selectedBook: Book? {
        guard let selectedBookID else {
            return nil
        }

        return library.books.first { book in
            book.id == selectedBookID
        }
    }

    // MARK: - Library Screen

    private var libraryView: some View {
        ZStack {
            Color.black.opacity(0.96)
                .ignoresSafeArea()

            if library.books.isEmpty {
                emptyLibraryView
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 24) {
                        libraryHeader

                        LazyVGrid(
                            columns: [
                                GridItem(
                                    .adaptive(
                                        minimum: 190,
                                        maximum: 240
                                    ),
                                    spacing: 24
                                )
                            ],
                            alignment: .leading,
                            spacing: 30
                        ) {
                            ForEach(library.books) { book in
                                libraryBookCard(for: book)
                            }
                        }
                    }
                    .padding(32)
                }
            }
        }
    }

    private var libraryHeader: some View {
        HStack {
            VStack(alignment: .leading, spacing: 5) {
                Text("Your Library")
                    .font(.system(size: 32, weight: .bold))

                Text(
                    library.books.count == 1
                        ? "1 audiobook"
                        : "\(library.books.count) audiobooks"
                )
                .foregroundStyle(.secondary)
            }

            Spacer()

            Button {
                showingFilePicker = true
            } label: {
                Label(
                    "Import Audiobook",
                    systemImage: "plus"
                )
            }
            .buttonStyle(.borderedProminent)
            .tint(.green)
            .controlSize(.large)
        }
    }

    private func libraryBookCard(
        for book: Book
    ) -> some View {
        Button {
            selectedBookID = book.id
        } label: {
            VStack(alignment: .leading, spacing: 10) {
                libraryCover(for: book)

                Text(book.identity?.workTitle ?? book.title)
                    .font(.headline)
                    .foregroundStyle(.primary)
                    .lineLimit(2)

                if let author = book.author,
                   !author.isEmpty {
                    Text(author)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }

                HStack(spacing: 6) {
                    Text(book.fileType.uppercased())
                        .foregroundStyle(.green)

                    if let duration = book.duration {
                        Text("•")
                        Text(formattedDuration(duration))
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)

                if let identity = book.identity {
                    Text(identity.editionType.displayName)
                        .font(.caption)
                        .foregroundStyle(.green)
                        .lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .contextMenu {
            Button {
                selectedBookID = book.id
                selectedDestination = .library
            } label: {
                Label(
                    "Open Book",
                    systemImage: "book.open"
                )
            }

            Button {
                openPlayer(for: book)
            } label: {
                Label(
                    "Play Audiobook",
                    systemImage: "play.fill"
                )
            }

            Divider()

            Button(role: .destructive) {
                bookPendingDeletion = book
                showingDeleteConfirmation = true
            } label: {
                Label(
                    "Remove from Library",
                    systemImage: "trash"
                )
            }
        }
    }

    @ViewBuilder
    private func libraryCover(
        for book: Book
    ) -> some View {
        if let coverArtData = book.coverArtData,
           let image = NSImage(data: coverArtData) {

            Image(nsImage: image)
                .resizable()
                .scaledToFill()
                .frame(height: 290)
                .frame(maxWidth: .infinity)
                .clipShape(
                    RoundedRectangle(cornerRadius: 16)
                )
                .shadow(
                    color: .black.opacity(0.45),
                    radius: 12,
                    y: 6
                )

        } else {
            RoundedRectangle(cornerRadius: 16)
                .fill(
                    LinearGradient(
                        colors: [
                            Color.green.opacity(0.7),
                            Color.black
                        ],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing
                    )
                )
                .frame(height: 290)
                .overlay {
                    Image(systemName: "waveform")
                        .font(.system(size: 56))
                        .foregroundStyle(.white.opacity(0.9))
                }
                .shadow(
                    color: .black.opacity(0.45),
                    radius: 12,
                    y: 6
                )
        }
    }

    private var emptyLibraryView: some View {
        VStack(spacing: 22) {
            Image(systemName: "books.vertical.fill")
                .font(.system(size: 76))
                .foregroundStyle(.green)

            Text("Your Library")
                .font(.system(size: 34, weight: .bold))

            Text("Your imported audiobooks will appear here.")
                .font(.title3)
                .foregroundStyle(.secondary)

            Button {
                showingFilePicker = true
            } label: {
                Label(
                    "Import Audiobook",
                    systemImage: "square.and.arrow.down"
                )
                .padding(.horizontal, 8)
            }
            .buttonStyle(.borderedProminent)
            .tint(.green)
            .controlSize(.large)

            Text("Import an MP3, M4B, M4A or AAX audiobook.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(40)
    }

    // MARK: - Recently Added Screen

    private var recentlyAddedView: some View {
        ZStack {
            Color.black.opacity(0.96)
                .ignoresSafeArea()

            if library.books.isEmpty {
                placeholderView(
                    title: "Recently Added",
                    message: "Newly imported audiobooks will appear here.",
                    icon: "clock.arrow.circlepath"
                )
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 24) {
                        Text("Recently Added")
                            .font(.system(size: 32, weight: .bold))

                        LazyVGrid(
                            columns: [
                                GridItem(
                                    .adaptive(
                                        minimum: 190,
                                        maximum: 240
                                    ),
                                    spacing: 24
                                )
                            ],
                            alignment: .leading,
                            spacing: 30
                        ) {
                            ForEach(library.books.reversed()) { book in
                                libraryBookCard(for: book)
                            }
                        }
                    }
                    .padding(32)
                }
            }
        }
    }

    // MARK: - Import Screen

    private var importView: some View {
        ZStack {
            Color.black.opacity(0.96)
                .ignoresSafeArea()

            VStack(spacing: 26) {
                Image(systemName: "arrow.up.doc.fill")
                    .font(.system(size: 74))
                    .foregroundStyle(.green)

                VStack(spacing: 8) {
                    Text("Import Audiobook")
                        .font(.system(size: 34, weight: .bold))

                    Text(
                        "Choose an audiobook file you own or have permission to use."
                    )
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                }

                VStack(spacing: 18) {
                    Button {
                        showingFilePicker = true
                    } label: {
                        Label(
                            "Browse Files",
                            systemImage: "folder"
                        )
                        .frame(minWidth: 180)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.green)
                    .controlSize(.large)

                    Text("Supported formats")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    HStack(spacing: 22) {
                        formatBadge("MP3")
                        formatBadge("M4B")
                        formatBadge("M4A")
                        formatBadge("AAX")
                    }
                }

                Text(
                    "Audiobook files remain on your device. AudioChoice only creates metadata, fingerprints and scan information."
                )
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 500)
                .padding(.top, 10)
            }
            .padding(40)
        }
    }

    private func formatBadge(
        _ title: String
    ) -> some View {
        Text(title)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.green)
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(
                Color.green.opacity(0.12)
            )
            .clipShape(Capsule())
    }

    // MARK: - Player Screen

    private var playerBook: Book? {
        guard let playerBookID else {
            return nil
        }

        return library.books.first { book in
            book.id == playerBookID
        }
    }

    @ViewBuilder
    private var playerView: some View {
        if let book = playerBook {
            ZStack {
                Color.black.opacity(0.97)
                    .ignoresSafeArea()

                ScrollView {
                    VStack(spacing: 24) {
                        playerHeader(for: book)

                        bookCover(for: book)
                            .frame(maxHeight: 390)

                        VStack(spacing: 7) {
                            Text(book.identity?.workTitle ?? book.title)
                                .font(.system(size: 30, weight: .bold))
                                .multilineTextAlignment(.center)
                                .lineLimit(3)
                                .minimumScaleFactor(0.65)
                                .frame(maxWidth: 700)

                            if let author = book.author,
                               !author.isEmpty {
                                Text(author)
                                    .font(.title3)
                                    .foregroundStyle(.secondary)
                            }

                            Text(currentChapterTitle(for: book))
                                .font(.subheadline)
                                .foregroundStyle(.green)
                        }

                        playerProgressSection

                        primaryPlaybackControls

                        secondaryPlaybackControls

                        playbackSpeedControl

                        if let error = playback.playbackError {
                            Label(
                                error,
                                systemImage:
                                    "exclamationmark.triangle.fill"
                            )
                            .foregroundStyle(.orange)
                            .multilineTextAlignment(.center)
                            .frame(maxWidth: 560)
                        }
                    }
                    .frame(maxWidth: .infinity)
                    .padding(40)
                }
            }
            .onChange(of: playback.currentTime) {
                guard !isDraggingSlider else {
                    return
                }

                sliderPosition = playback.currentTime
            }
            .sheet(item: $activePlayerSheet) { sheet in
                switch sheet {

                case .sleep:
                    SleepTimerView(
                        playback: playback,
                        book: book
                    )

                case .chapters:
                    ChapterListView(
                        book: book,
                        currentTime: playback.currentTime
                    ) { selectedTime in
                        playback.seek(to: selectedTime)
                        sliderPosition = selectedTime
                    }

                case .bookmarks:
                    BookmarkListView(
                        book: book,
                        currentTime: playback.currentTime,
                        onAdd: { position, title, note in
                            library.addBookmark(
                                bookID: book.id,
                                position: position,
                                title: title,
                                note: note
                            )
                        },
                        onSelect: { selectedTime in
                            playback.seek(to: selectedTime)
                            sliderPosition = selectedTime
                        },
                        onDelete: { bookmarkID in
                            library.deleteBookmark(
                                bookID: book.id,
                                bookmarkID: bookmarkID
                            )
                        }
                    )

                case .filters:
                    ContentControlsView(
                        filterManager: filterManager
                    )
                    .onAppear {
                        filterBookID = book.id
                        filterManager.profile = book.filterProfile

                        filterManager.onProfileChanged = { profile in
                            library.updateFilterProfile(
                                bookID: book.id,
                                profile: profile
                            )
                        }
                    }
                }
            }
        } else {
            ZStack {
                Color.black.opacity(0.97)
                    .ignoresSafeArea()

                VStack(spacing: 20) {
                    Image(systemName: "play.circle.fill")
                        .font(.system(size: 72))
                        .foregroundStyle(.green)

                    Text("Nothing Playing")
                        .font(.largeTitle.bold())

                    Text("Choose an audiobook from your library.")
                        .foregroundStyle(.secondary)

                    Button("Open Library") {
                        selectedDestination = .library
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.green)
                }
            }
        }
    }

    private func playerHeader(
        for book: Book
    ) -> some View {
        HStack {
            Button {
                selectedDestination = .library
            } label: {
                Label(
                    "Library",
                    systemImage: "chevron.left"
                )
            }
            .buttonStyle(.plain)
            .foregroundStyle(.green)

            Spacer()

            VStack(spacing: 2) {
                Text("Now Playing")
                    .font(.headline)

                Text(book.fileType.uppercased())
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Spacer()

            Button {
                activePlayerSheet = .bookmarks
            } label: {
                Image(systemName: "bookmark")
                    .font(.title3)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.green)
            .accessibilityLabel("Add Bookmark")
        }
    }

    private var playerProgressSection: some View {
        VStack(spacing: 8) {
            Slider(
                value: $sliderPosition,
                in: 0...max(playback.duration, 1),
                onEditingChanged: { editing in
                    isDraggingSlider = editing

                    if !editing {
                        playback.seek(to: sliderPosition)
                    }
                }
            )
            .tint(.green)
            .frame(maxWidth: 720)

            HStack {
                Text(
                    formattedPlaybackTime(
                        playback.currentTime
                    )
                )

                Spacer()

                Text(
                    "-\(formattedPlaybackTime(playback.remainingTime))"
                )
            }
            .font(.caption.monospacedDigit())
            .foregroundStyle(.secondary)
            .frame(maxWidth: 720)
        }
    }

    private var primaryPlaybackControls: some View {
        HStack(spacing: 32) {
            playerControlButton(
                icon: "gobackward.60",
                accessibilityLabel: "Back 1 minute"
            ) {
                playback.skipBackward(seconds: 60)
            }

            playerControlButton(
                icon: "gobackward.10",
                accessibilityLabel: "Back 10 seconds"
            ) {
                playback.skipBackward(seconds: 10)
            }

            Button {
                playback.togglePlayPause()
            } label: {
                ZStack {
                    Circle()
                        .fill(Color.green)
                        .frame(width: 78, height: 78)

                    Image(
                        systemName: playback.isPlaying
                            ? "pause.fill"
                            : "play.fill"
                    )
                    .font(
                        .system(
                            size: 30,
                            weight: .bold
                        )
                    )
                    .foregroundStyle(.black)
                    .offset(
                        x: playback.isPlaying ? 0 : 2
                    )
                }
            }
            .buttonStyle(.plain)
            .accessibilityLabel(
                playback.isPlaying ? "Pause" : "Play"
            )

            playerControlButton(
                icon: "goforward.10",
                accessibilityLabel: "Forward 10 seconds"
            ) {
                playback.skipForward(seconds: 10)
            }

            playerControlButton(
                icon: "goforward.60",
                accessibilityLabel: "Forward 1 minute"
            ) {
                playback.skipForward(seconds: 60)
            }
        }
    }

    private func playerControlButton(
        icon: String,
        accessibilityLabel: String,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .font(
                    .system(
                        size: 25,
                        weight: .medium
                    )
                )
                .frame(width: 44, height: 44)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .foregroundStyle(.primary)
        .accessibilityLabel(accessibilityLabel)
    }

    private var secondaryPlaybackControls: some View {
        HStack(spacing: 48) {
            secondaryControl(
                title: "Sleep",
                icon: "moon.zzz"
            ) {
                activePlayerSheet = .sleep
            }

            secondaryControl(
                title: "Chapters",
                icon: "list.bullet.rectangle"
            ) {
                activePlayerSheet = .chapters
            }

            secondaryControl(
                title: "Bookmarks",
                icon: "bookmark"
            ) {
                activePlayerSheet = .bookmarks
            }

            secondaryControl(
                title: "Filters",
                icon: "line.3.horizontal.decrease.circle"
            ) {
                activePlayerSheet = .filters
            }
        }
    }

    private func secondaryControl(
        title: String,
        icon: String,
        action: @escaping () -> Void
    ) -> some View {
        VStack(spacing: 6) {
            Button(action: action) {
                Image(systemName: icon)
                    .font(.title2)
            }
            .buttonStyle(.plain)
            .foregroundStyle(.green)

            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var playbackSpeedControl: some View {
        VStack(spacing: 10) {
            Text("Playback Speed")
                .font(.headline)

            HStack(spacing: 10) {
                ForEach(
                    [0.75, 1.0, 1.25, 1.5, 1.75, 2.0],
                    id: \.self
                ) { speed in
                    playbackSpeedButton(speed)
                }
            }
        }
        .padding(.top, 4)
    }

    private func playbackSpeedButton(
        _ speed: Double
    ) -> some View {
        let isSelected =
            abs(Double(playback.playbackSpeed) - speed) < 0.01

        return Button {
            playback.setPlaybackSpeed(
                Float(speed)
            )
        } label: {
            Text(speedLabel(speed))
                .font(.caption.weight(.semibold))
                .frame(minWidth: 46)
                .padding(.horizontal, 7)
                .padding(.vertical, 7)
                .background(
                    isSelected
                        ? Color.green
                        : Color.clear
                )
                .foregroundStyle(
                    isSelected
                        ? Color.black
                        : Color.primary
                )
                .clipShape(Capsule())
                .overlay {
                    Capsule()
                        .stroke(
                            Color.green.opacity(0.55),
                            lineWidth: 1
                        )
                }
        }
        .buttonStyle(.plain)
    }

    private func speedLabel(
        _ speed: Double
    ) -> String {
        if speed == 1.0 {
            return "1x"
        }

        if speed == 2.0 {
            return "2x"
        }

        return "\(speed)x"
    }

    private func openPlayer(
        for book: Book
    ) {
        playerBookID = book.id
        selectedBookID = nil
        selectedDestination = .player

        if playback.currentBookID != book.id {
            playback.load(book: book)
            sliderPosition = playback.currentTime
        }
    }

    private func currentChapterTitle(
        for book: Book
    ) -> String {
        guard !book.chapters.isEmpty else {
            return "Audiobook"
        }

        let matchingChapter = book.chapters.last { chapter in
            chapter.startTime <= playback.currentTime
        }

        return matchingChapter?.title
            ?? book.chapters.first?.title
            ?? "Audiobook"
    }

    private func formattedPlaybackTime(
        _ time: TimeInterval
    ) -> String {
        let seconds = max(0, Int(time))
        let hours = seconds / 3600
        let minutes = (seconds % 3600) / 60
        let remainingSeconds = seconds % 60

        if hours > 0 {
            return String(
                format: "%d:%02d:%02d",
                hours,
                minutes,
                remainingSeconds
            )
        }

        return String(
            format: "%d:%02d",
            minutes,
            remainingSeconds
        )
    }

    // MARK: - Settings Screen

    private var settingsView: some View {
        ZStack {
            Color.black.opacity(0.96)
                .ignoresSafeArea()

            placeholderView(
                title: "Settings",
                message: "Playback, scanning and account settings will appear here.",
                icon: "slider.horizontal.3"
            )
        }
    }

    private func placeholderView(
        title: String,
        message: String,
        icon: String
    ) -> some View {
        VStack(spacing: 20) {
            Image(systemName: icon)
                .font(.system(size: 68))
                .foregroundStyle(.green)

            Text(title)
                .font(.system(size: 32, weight: .bold))

            Text(message)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(40)
    }

    // MARK: - Book Details Screen

    private func bookDetailView(
        for book: Book
    ) -> some View {

        BookDetailView(

            book: book,

            onBack: {
                selectedBookID = nil
                selectedDestination = .library
            },

            onPlay: {
                openPlayer(for: book)
            },

            onFilters: {
                filterManager.profile = book.filterProfile

                filterManager.onProfileChanged = { profile in
                    library.updateFilterProfile(
                        bookID: book.id,
                        profile: profile
                    )
                }

                activePlayerSheet = .filters
            }
        )
        .sheet(item: $activePlayerSheet) { sheet in

            switch sheet {

            case .filters:
                ContentControlsView(
                    filterManager: filterManager
                )

            default:
                EmptyView()
            }
        }
    }
    private func audiobookInformation(
        for book: Book
    ) -> some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 12) {
                detailRow(
                    title: "Original format",
                    value: book.fileType.uppercased()
                )

                detailRow(
                    title: "Conversion",
                    value: readableStatus(
                        book.conversionStatus.rawValue
                    )
                )

                detailRow(
                    title: "Scan",
                    value: readableStatus(
                        book.scanStatus.rawValue
                    )
                )

                if let duration = book.duration {
                    detailRow(
                        title: "Duration",
                        value: formattedDuration(duration)
                    )
                }

                if let identity = book.identity {
                    identityInformation(identity)
                }

                if let fingerprint = book.fingerprint {
                    Divider()

                    detailRow(
                        title: "Fingerprint",
                        value: shortenedFingerprint(
                            fingerprint.sha256
                        )
                    )

                    detailRow(
                        title: "File size",
                        value: formattedFileSize(
                            fingerprint.fileSize
                        )
                    )

                    detailRow(
                        title: "Fingerprint version",
                        value: "Version \(fingerprint.version)"
                    )
                }
            }
            .padding(4)
        } label: {
            Label(
                "Audiobook Information",
                systemImage: "info.circle"
            )
        }
        .frame(maxWidth: 560)
    }

    @ViewBuilder
    private func identityInformation(
        _ identity: BookIdentity
    ) -> some View {
        detailRow(
            title: "Edition",
            value: identity.editionType.displayName
        )

        if let seriesTitle = identity.seriesTitle {
            if let seriesNumber = identity.seriesNumber {
                detailRow(
                    title: "Series",
                    value: "\(seriesTitle), Book \(seriesNumber)"
                )
            } else {
                detailRow(
                    title: "Series",
                    value: seriesTitle
                )
            }
        }

        if let partNumber = identity.partNumber,
           let totalParts = identity.totalParts {
            detailRow(
                title: "Part",
                value: "\(partNumber) of \(totalParts)"
            )
        }

        detailRow(
            title: "Identity confidence",
            value: formattedConfidence(
                identity.confidence
            )
        )
    }

    @ViewBuilder
    private func bookCover(
        for book: Book
    ) -> some View {
        if let coverArtData = book.coverArtData,
           let image = NSImage(data: coverArtData) {

            Image(nsImage: image)
                .resizable()
                .scaledToFit()
                .frame(
                    maxWidth: 320,
                    maxHeight: 420
                )
                .clipShape(
                    RoundedRectangle(cornerRadius: 18)
                )
                .shadow(radius: 18)

        } else {
            RoundedRectangle(cornerRadius: 18)
                .fill(
                    LinearGradient(
                        colors: [
                            Color.green.opacity(0.65),
                            Color.black
                        ],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing
                    )
                )
                .frame(
                    width: 280,
                    height: 380
                )
                .overlay {
                    Image(systemName: "waveform")
                        .font(.system(size: 76))
                        .foregroundStyle(.white.opacity(0.9))
                }
                .shadow(radius: 18)
        }
    }

    // MARK: - Delete Helpers

    private var deleteConfirmationMessage: String {
        guard let book = bookPendingDeletion else {
            return "This audiobook will be removed from AudioChoice."
        }

        let title =
            book.identity?.workTitle
            ?? book.title

        return """
        \(title) and its stored audiobook file will be removed from AudioChoice. \
        Your original source file will not be affected.
        """
    }

    private func deletePendingBook() {
        guard let book = bookPendingDeletion else {
            return
        }

        if playerBookID == book.id {
            playback.stop()
            playerBookID = nil
        }

        let deletionSucceeded = library.removeBook(
            id: book.id
        )

        if deletionSucceeded,
           selectedBookID == book.id {
            selectedBookID = nil
            selectedDestination = .library
        }

        bookPendingDeletion = nil
    }

    // MARK: - Formatting Helpers

    private func detailRow(
        title: String,
        value: String
    ) -> some View {
        HStack {
            Text(title)
                .foregroundStyle(.secondary)

            Spacer()

            Text(value)
                .fontWeight(.medium)
                .multilineTextAlignment(.trailing)
                .textSelection(.enabled)
        }
    }

    private func formattedDuration(
        _ duration: TimeInterval
    ) -> String {
        let totalSeconds = max(0, Int(duration))
        let hours = totalSeconds / 3600
        let minutes = (totalSeconds % 3600) / 60

        if hours > 0 {
            return "\(hours) hr \(minutes) min"
        }

        return "\(minutes) min"
    }

    private func formattedConfidence(
        _ confidence: Double
    ) -> String {
        let percentage = Int(
            (confidence * 100).rounded()
        )

        return "\(percentage)%"
    }

    private func shortenedFingerprint(
        _ fingerprint: String
    ) -> String {
        guard fingerprint.count > 16 else {
            return fingerprint
        }

        let beginning = fingerprint.prefix(8)
        let ending = fingerprint.suffix(8)

        return "\(beginning)…\(ending)"
    }

    private func formattedFileSize(
        _ bytes: Int64
    ) -> String {
        let formatter = ByteCountFormatter()

        formatter.allowedUnits = [
            .useMB,
            .useGB
        ]

        formatter.countStyle = .file

        return formatter.string(
            fromByteCount: bytes
        )
    }

    private func readableStatus(
        _ value: String
    ) -> String {
        switch value {
        case "notNeeded":
            return "Not needed"

        case "notScanned":
            return "Not scanned"

        case "waiting":
            return "Waiting"

        case "converting":
            return "Converting"

        case "completed":
            return "Completed"

        case "failed":
            return "Failed"

        case "scanning":
            return "Scanning"

        default:
            return value
        }
    }

    // MARK: - Import

    private func handleImport(
        _ result: Result<[URL], Error>
    ) {
        switch result {
        case .success(let urls):
            guard let url = urls.first else {
                return
            }

            Task {
                let addResult = await library.addBook(url: url)

                switch addResult {
                case .added(let bookID):
                    selectedBookID = bookID
                    selectedDestination = .library

                case .duplicate(let bookID):
                    selectedBookID = bookID
                    selectedDestination = .library

                    if let existingBook = library.books.first(
                        where: { $0.id == bookID }
                    ) {
                        duplicateBookTitle =
                            existingBook.identity?.workTitle
                            ?? existingBook.title
                    } else {
                        duplicateBookTitle = "This audiobook"
                    }

                    showingDuplicateAlert = true

                case .failed(let message):
                    importErrorMessage = message
                }
            }

        case .failure(let error):
            importErrorMessage = error.localizedDescription
        }
    }
}

#Preview {
    ContentView()
}
