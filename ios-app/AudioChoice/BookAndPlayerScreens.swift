import SwiftUI

struct BookDetailScreen: View {
    var book: MobileBook
    @State private var isFavorite = false
    @State private var bookmarkCount = 0
    @State private var showingBookmarks = false
    /// Held in state rather than re-read on every access, so renaming and marking a book
    /// complete show immediately instead of after leaving and returning.
    @State private var record: LibraryBookRecord?
    @State private var showingRename = false
    @State private var draftTitle = ""

    /// The record's title wins, because it is the one renaming updates.
    private var shownTitle: String { record?.book.title ?? book.title }
    private var isFinished: Bool { record?.isFinished ?? false }

    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                BookCover(title: shownTitle, artworkFileName: record?.artworkFileName, isFinished: isFinished)
                    .frame(width: 220, height: 290)
                    .shadow(color: .black.opacity(0.45), radius: 18, y: 10)

                VStack(spacing: 5) {
                    Text(shownTitle).font(.title2.bold())
                    Text(book.author).foregroundStyle(ACTheme.secondaryText)
                    Text(book.edition)
                        .font(.caption)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 5)
                        .background(ACTheme.panelRaised)
                        .clipShape(Capsule())
                }

                HStack {
                    metric("timer", book.runtime, "Runtime")
                    metric("list.bullet.rectangle", "\(book.chapters)", "Chapters")
                    metric("checkmark.shield", record?.scanResult == nil ? "Pending" : "Verified", "Scan")
                }

                if isFinished {
                    Label("Finished", systemImage: "checkmark.circle.fill")
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(ACTheme.accent)
                }

                HStack {
                    NavigationLink {
                        PlayerScreen(book: book)
                    } label: {
                        Label("Play", systemImage: "play.fill")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(ACTheme.accent)
                    .foregroundStyle(.black)

                    if let record {
                        Button {
                            showingBookmarks = true
                        } label: {
                            Label("Bookmarks", systemImage: "bookmark")
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.bordered)
                    }
                }

                ACCard {
                    VStack(spacing: 18) {
                        if let record {
                            NavigationLink { ChapterListScreen(record: record) } label: {
                                detailRow("Chapters", icon: "list.bullet", value: "\(book.chapters)")
                            }
                            Divider()
                            Button { showingBookmarks = true } label: {
                                detailRow("Bookmarks", icon: "bookmark", value: "\(bookmarkCount)")
                            }
                        }
                        Divider()
                        if let record {
                            NavigationLink { BookFiltersScreen(record: record) } label: {
                                detailRow(
                                    "Filters",
                                    icon: "ear.badge.checkmark",
                                    value: record.scanResult.map {
                                        "\(PlaybackFilterTaxonomy.controlCount($0.events)) controls"
                                    } ?? "Pending"
                                )
                            }
                        } else {
                            detailRow("Filters", icon: "ear.badge.checkmark", value: "Unavailable")
                        }
                    }
                }
            }
            .padding()
        }
        .background(ACTheme.background)
        .toolbar {
            Button("Favorite", systemImage: isFavorite ? "heart.fill" : "heart") {
                UserLibraryStore.toggleFavorite(book.id)
                isFavorite = UserLibraryStore.isFavorite(book.id)
            }
            if let record {
                Menu("More", systemImage: "ellipsis.circle") {
                    Button(
                        isFinished ? "Mark as Not Finished" : "Mark as Finished",
                        systemImage: isFinished ? "arrow.uturn.backward.circle" : "checkmark.circle"
                    ) {
                        let target = !isFinished
                        Task {
                            let updated = await BookCompletionService.setFinished(target, for: record)
                            if let updated { self.record = updated }
                        }
                    }
                    Button("Edit Title", systemImage: "pencil") {
                        draftTitle = shownTitle
                        showingRename = true
                    }
                }
            }
        }
        .onAppear {
            record = AudiobookLibraryStore.load().first { $0.id == book.id }
            isFavorite = UserLibraryStore.isFavorite(book.id)
            bookmarkCount = UserLibraryStore.bookmarks(for: book.id).count
        }
        .alert("Edit title", isPresented: $showingRename) {
            TextField("Title", text: $draftTitle)
            Button("Save") { rename() }
            Button("Cancel", role: .cancel) { draftTitle = "" }
        } message: {
            Text("Changes what this book is called on your devices. Its scan and filters are matched by the audio itself, so renaming it will not affect them.")
        }
        .sheet(isPresented: $showingBookmarks, onDismiss: {
            bookmarkCount = UserLibraryStore.bookmarks(for: book.id).count
        }) {
            if let record {
                BookmarkSheet(record: record, currentPosition: AudioPlaybackManager.savedPosition(for: record.id))
            }
        }
    }

    /// Saves the new title locally first, then tells the account.
    ///
    /// Local first because the rename should hold even with no network, and because the
    /// server treats these details as display only: identification keeps working from the
    /// file's own metadata whatever the book is called.
    private func rename() {
        let trimmed = draftTitle.trimmingCharacters(in: .whitespacesAndNewlines)
        draftTitle = ""
        guard !trimmed.isEmpty, trimmed != shownTitle,
              let updated = AudiobookLibraryStore.rename(trimmed, for: book.id) else { return }
        record = updated
        guard let accountID = updated.accountLibraryID else { return }
        Task {
            guard let client = try? CloudScanClient.configured() else { return }
            _ = try? await client.updateBookDetails(bookID: accountID, title: trimmed)
        }
    }

    private func metric(_ icon: String, _ value: String, _ label: String) -> some View {
        VStack(spacing: 5) {
            Image(systemName: icon).foregroundStyle(ACTheme.accent)
            Text(value).font(.caption.bold())
            Text(label).font(.caption2).foregroundStyle(ACTheme.secondaryText)
        }
        .frame(maxWidth: .infinity)
    }

    private func detailRow(_ title: String, icon: String, value: String) -> some View {
        HStack {
            Image(systemName: icon).frame(width: 24)
            Text(title)
            Spacer()
            Text(value).foregroundStyle(title == "Filters" ? ACTheme.accent : ACTheme.secondaryText)
            Image(systemName: "chevron.right").foregroundStyle(.secondary)
        }
    }
}

private struct LegacyPlayerScreen: View {
    var book: MobileBook
    var initialPosition: Double? = nil
    @ObservedObject private var playback = AudioPlaybackManager.shared
    @State private var sleepTask: Task<Void, Never>?
    @State private var bookmarkSaved = false

    private var record: LibraryBookRecord? {
        AudiobookLibraryStore.load().first { $0.id == book.id }
    }

    var body: some View {
        VStack(spacing: 26) {
            Spacer()
            BookCover(title: book.title, artworkFileName: record?.artworkFileName)
                .frame(maxWidth: 310)
                .aspectRatio(0.78, contentMode: .fit)
                .shadow(color: .black.opacity(0.5), radius: 22, y: 12)

            VStack(spacing: 5) {
                Text(book.title).font(.headline)
                Text(playback.currentChapterTitle)
                    .font(.subheadline)
                    .foregroundStyle(ACTheme.accent)
            }

            if let error = playback.playbackError {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.footnote)
                    .foregroundStyle(.orange)
                    .multilineTextAlignment(.center)
            }

            FilterAvailabilityNotice(availability: playback.filterAvailability)

            if let event = playback.activeFilterEvent,
               let category = IOSContentTaxonomy.category(for: event) {
                Label(
                    "Skipping \(category.title)",
                    systemImage: "checkmark.shield.fill"
                )
                .font(.caption.bold())
                .foregroundStyle(ACTheme.accent)
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .background(ACTheme.panel)
                .clipShape(Capsule())
            }

            VStack {
                Slider(
                    value: Binding(
                        get: { playback.position },
                        set: { playback.updateScrub(to: $0) }
                    ),
                    in: 0...max(playback.duration, 1),
                    onEditingChanged: { isEditing in
                        if isEditing { playback.beginScrubbing() } else { playback.endScrubbing() }
                    }
                )
                HStack {
                    Text(time(playback.position))
                    Spacer()
                    Text("-\(time(max(playback.duration - playback.position, 0)))")
                }
                .font(.caption)
                .foregroundStyle(ACTheme.secondaryText)
            }

            HStack(spacing: 34) {
                Button("Back 15 Seconds", systemImage: "gobackward.15") { playback.skip(by: -15) }
                Button("Previous Chapter", systemImage: "backward.end.fill") { playback.previousChapter() }
                Button {
                    playback.togglePlayback()
                } label: {
                    Image(systemName: playback.isPlaying ? "pause.fill" : "play.fill")
                        .font(.title)
                        .frame(width: 68, height: 68)
                        .background(ACTheme.accent)
                        .foregroundStyle(.black)
                        .clipShape(Circle())
                }
                Button("Next Chapter", systemImage: "forward.end.fill") { playback.nextChapter() }
                Button("Forward 30 Seconds", systemImage: "goforward.30") { playback.skip(by: 30) }
            }
            .labelStyle(.iconOnly)

            HStack {
                Menu {
                    ForEach([0.75, 1, 1.25, 1.5, 1.75, 2], id: \.self) { rate in
                        Button("\(String(format: "%g", rate))×") { playback.setRate(Float(rate)) }
                    }
                } label: {
                    playerTool("\(String(format: "%g", playback.playbackRate))×", "Speed")
                }
                if let record {
                    NavigationLink { ChapterListScreen(record: record) } label: {
                        playerTool("list.bullet", "Chapters")
                    }
                }
                Menu {
                    Button("15 minutes") { setSleepTimer(minutes: 15) }
                    Button("30 minutes") { setSleepTimer(minutes: 30) }
                    Button("60 minutes") { setSleepTimer(minutes: 60) }
                    Button("Cancel Timer", role: .destructive) { sleepTask?.cancel() }
                } label: {
                    playerTool("moon", "Sleep Timer")
                }
                Button {
                    UserLibraryStore.addBookmark(bookID: book.id, position: playback.position)
                    bookmarkSaved = true
                    Task {
                        try? await Task.sleep(for: .seconds(1.5))
                        bookmarkSaved = false
                    }
                } label: {
                    playerTool(bookmarkSaved ? "bookmark.fill" : "bookmark", bookmarkSaved ? "Saved" : "Bookmark")
                }
            }
            Spacer()
        }
        .padding()
        .background(ACTheme.background.ignoresSafeArea())
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if let record {
                playback.load(record)
                if let initialPosition { playback.seek(to: initialPosition) }
            }
        }
    }

    private func playerTool(_ icon: String, _ title: String) -> some View {
        VStack(spacing: 6) {
            Image(systemName: icon)
            Text(title).font(.caption2)
        }
        .frame(maxWidth: .infinity)
    }

    private func time(_ seconds: Double) -> String {
        guard seconds.isFinite else { return "0:00" }
        let total = max(Int(seconds), 0)
        let hours = total / 3600
        let minutes = (total % 3600) / 60
        let remaining = total % 60
        return hours > 0
            ? String(format: "%d:%02d:%02d", hours, minutes, remaining)
            : String(format: "%d:%02d", minutes, remaining)
    }

    private func setSleepTimer(minutes: Int) {
        sleepTask?.cancel()
        sleepTask = Task {
            try? await Task.sleep(for: .seconds(minutes * 60))
            guard !Task.isCancelled, playback.isPlaying else { return }
            playback.togglePlayback()
        }
    }
}

/// The Beta-equivalent player layout: title, large cover, chapter progress,
/// thirty-second controls, then the listener tools in the same order as Android.
struct PlayerScreen: View {
    var book: MobileBook
    var initialPosition: Double? = nil
    @ObservedObject private var playback = AudioPlaybackManager.shared
    @State private var sleepTask: Task<Void, Never>?
    @State private var bookmarkSaved = false
    @State private var showingBookmarks = false
    @State private var showingReader = false
    @State private var importingEpub = false

    private var record: LibraryBookRecord? {
        AudiobookLibraryStore.load().first { $0.id == book.id }
    }

    private var hasReadingEdition: Bool {
        record.map { ReaderStore.hasEpub(bookID: $0.id) } ?? false
    }

    var body: some View {
        GeometryReader { geometry in
            VStack(spacing: 0) {
                HStack {
                    Image(systemName: "chevron.down").font(.title3.bold())
                    Spacer()
                    // An open book invites opening the reader; adding one invites attaching
                    // an EPUB. The reader's own close button sits in this same position so
                    // the icon does not jump when toggling between the two.
                    if record != nil {
                        Button {
                            if hasReadingEdition { showingReader = true } else { importingEpub = true }
                        } label: {
                            Image(systemName: hasReadingEdition ? "book" : "book.badge.plus")
                                .font(.title2)
                                .foregroundStyle(ACTheme.accent)
                        }
                        .accessibilityLabel(hasReadingEdition ? "Open reading edition" : "Attach a reading edition")
                    } else {
                        Image(systemName: "waveform").font(.title2).foregroundStyle(ACTheme.accent)
                    }
                    Spacer()
                    Menu {
                        Button("15 minutes") { setSleepTimer(minutes: 15) }
                        Button("30 minutes") { setSleepTimer(minutes: 30) }
                        Button("60 minutes") { setSleepTimer(minutes: 60) }
                        Button("Turn off", role: .destructive) { sleepTask?.cancel() }
                    } label: { Image(systemName: "timer").font(.title2) }
                }
                .padding(.top, 8)

                Text(book.title)
                    .font(.system(size: 20, weight: .medium))
                    .foregroundStyle(ACTheme.accent)
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .frame(maxWidth: geometry.size.width * 0.86)
                    .padding(.top, 38)

                BookCover(title: book.title, artworkFileName: record?.artworkFileName)
                    .frame(width: min(geometry.size.width * 0.86, 340), height: min(geometry.size.width * 0.86, 340))
                    .padding(.top, 28)
                    .shadow(color: .black.opacity(0.45), radius: 16, y: 8)

                Text(playback.currentChapterTitle.isEmpty ? "Audiobook" : playback.currentChapterTitle)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(ACTheme.secondaryText)
                    .padding(.top, 16)

                if let error = playback.playbackError {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.footnote)
                        .foregroundStyle(.orange)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal)
                }

                FilterAvailabilityNotice(availability: playback.filterAvailability)
                    .padding(.horizontal)

                VStack(spacing: 4) {
                    Slider(
                        value: Binding(
                            get: { playback.position },
                            set: { playback.updateScrub(to: $0) }
                        ),
                        in: 0...max(playback.duration, 1),
                        onEditingChanged: { isEditing in
                            if isEditing { playback.beginScrubbing() } else { playback.endScrubbing() }
                        }
                    )
                        .tint(ACTheme.accent)
                    HStack {
                        Text(time(playback.position))
                        Spacer()
                        Text("-\(time(max(playback.duration - playback.position, 0)))")
                    }
                    .font(.caption)
                    .foregroundStyle(ACTheme.secondaryText)
                }
                .padding(.top, 16)

                HStack {
                    Button("Back 30 seconds", systemImage: "gobackward.30") { playback.skip(by: -30) }
                    Spacer()
                    Button { playback.togglePlayback() } label: {
                        Image(systemName: playback.isPlaying ? "pause.fill" : "play.fill")
                            .font(.system(size: 32, weight: .bold))
                            .frame(width: 82, height: 82)
                            .background(ACTheme.accent)
                            .foregroundStyle(.black)
                            .clipShape(Circle())
                    }
                    Spacer()
                    Button("Forward 30 seconds", systemImage: "goforward.30") { playback.skip(by: 30) }
                }
                .labelStyle(.iconOnly)
                .font(.system(size: 34))
                .padding(.horizontal, 54)
                .padding(.top, 20)

                HStack {
                    Menu {
                        ForEach([0.75, 1, 1.25, 1.5, 1.75, 2], id: \.self) { rate in
                            Button("\(String(format: "%g", rate))×") { playback.setRate(Float(rate)) }
                        }
                    } label: { tool("\(String(format: "%g", playback.playbackRate))×", "Speed", isSymbol: false) }

                    if let record {
                        NavigationLink { ChapterListScreen(record: record) } label: { tool("list.bullet", "Chapters") }
                    } else { tool("list.bullet", "Chapters") }

                    if let record {
                        NavigationLink { BookFiltersScreen(record: record) } label: { tool("shield", "Filters") }
                    } else { tool("shield", "Filters") }

                    Button { showingBookmarks = true } label: { tool("bookmark", "Bookmarks") }
                }
                .padding(.top, 18)
                Spacer(minLength: 8)
            }
            .padding(.horizontal, 20)
        }
        .background(ACTheme.background.ignoresSafeArea())
        .navigationBarTitleDisplayMode(.inline)
        .fullScreenCover(isPresented: $showingReader) {
            if let record {
                ReadingEditionScreen(record: record) { showingReader = false }
            }
        }
        .fileImporter(
            isPresented: $importingEpub,
            allowedContentTypes: [.epub],
            allowsMultipleSelection: false
        ) { result in
            guard let url = try? result.get().first, let record else { return }
            Task {
                await ReadingEditionManager.shared.attach(fileURL: url, record: record)
                // Straight into the reader on success, since attaching one has no other purpose.
                if ReadingEditionManager.shared.hasReadingEdition { showingReader = true }
            }
        }
        .task {
            if let record {
                playback.load(record)
                if let initialPosition { playback.seek(to: initialPosition) }
            }
        }
        .sheet(isPresented: $showingBookmarks) {
            if let record {
                BookmarkSheet(record: record, currentPosition: playback.position)
            }
        }
    }

    private func tool(_ value: String, _ label: String, isSymbol: Bool = true) -> some View {
        VStack(spacing: 6) {
            if isSymbol { Image(systemName: value).font(.title3) }
            else { Text(value).font(.title3.bold()) }
            Text(label).font(.caption2).foregroundStyle(ACTheme.secondaryText)
        }
        .foregroundStyle(ACTheme.accent)
        .frame(maxWidth: .infinity)
    }

    private func time(_ seconds: Double) -> String {
        let total = max(Int(seconds.isFinite ? seconds : 0), 0)
        return total >= 3600 ? String(format: "%d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60) : String(format: "%d:%02d", total / 60, total % 60)
    }

    private func setSleepTimer(minutes: Int) {
        sleepTask?.cancel()
        sleepTask = Task {
            try? await Task.sleep(for: .seconds(minutes * 60))
            guard !Task.isCancelled, playback.isPlaying else { return }
            playback.togglePlayback()
        }
    }
}

struct NowPlayingScreen: View {
    @ObservedObject private var playback = AudioPlaybackManager.shared

    var body: some View {
        Group {
            if let record = playback.currentRecord {
                PlayerScreen(book: record.book)
            } else {
                ContentUnavailableView("Nothing Playing", systemImage: "waveform", description: Text("Choose an audiobook from your Library to open the Beta-style player."))
                    .background(ACTheme.background.ignoresSafeArea())
            }
        }
        .navigationBarHidden(true)
    }
}

/// Tells the listener when their filters are not actually being applied.
///
/// A book with no scan data plays exactly like a book with nothing to filter, so
/// without this the two are indistinguishable and someone relying on filtering has no
/// way to know it is inactive. Shown only when there is something to say.
struct FilterAvailabilityNotice: View {
    let availability: FilterAvailability

    var body: some View {
        switch availability {
        case .available:
            EmptyView()
        case .loading:
            Label(
                "Still checking this audiobook's filters. Nothing is filtered until they load.",
                systemImage: "clock.fill"
            )
            .font(.footnote)
            .foregroundStyle(ACTheme.secondaryText)
            .multilineTextAlignment(.center)
        case .unavailable:
            Label(
                "No filter data for this audiobook, so nothing is being filtered.",
                systemImage: "exclamationmark.shield.fill"
            )
            .font(.footnote.weight(.semibold))
            .foregroundStyle(.red)
            .multilineTextAlignment(.center)
        }
    }
}
