import SwiftUI

struct BookDetailScreen: View {
    var book: MobileBook
    @State private var isFavorite = false
    @State private var bookmarkCount = 0

    private var record: LibraryBookRecord? {
        AudiobookLibraryStore.load().first { $0.id == book.id }
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                BookCover(title: book.title, artworkFileName: record?.artworkFileName)
                    .frame(width: 220, height: 290)
                    .shadow(color: .black.opacity(0.45), radius: 18, y: 10)

                VStack(spacing: 5) {
                    Text(book.title).font(.title2.bold())
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

                if let record,
                   let fileName = record.localFileName {
                    NavigationLink {
                        ScanProgressScreen(
                            record: record,
                            fileURL: AudiobookImportService.audioURL(fileName: fileName)
                        )
                    } label: {
                        Label(
                            record.scanResult == nil ? "Scan Audiobook" : "Check Scan Updates",
                            systemImage: "arrow.triangle.2.circlepath"
                        )
                    }
                    .buttonStyle(.bordered)
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
                        NavigationLink {
                            BookmarkListScreen(record: record)
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
                            NavigationLink { BookmarkListScreen(record: record) } label: {
                                detailRow("Bookmarks", icon: "bookmark", value: "\(bookmarkCount)")
                            }
                        }
                        Divider()
                        NavigationLink {
                            if let record {
                                FilterEventListScreen(record: record)
                            } else {
                                FilterProfileScreen()
                            }
                        } label: {
                            detailRow(
                                "Filters",
                                icon: "ear.badge.checkmark",
                                value: record?.scanResult.map {
                                    "\(IOSContentTaxonomy.controlCount($0.events)) Events"
                                } ?? "Not Scanned"
                            )
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
        }
        .onAppear {
            isFavorite = UserLibraryStore.isFavorite(book.id)
            bookmarkCount = UserLibraryStore.bookmarks(for: book.id).count
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

            if let event = playback.activeFilterEvent,
               let category = IOSContentTaxonomy.category(for: event) {
                Label(
                    FilterPreferences.behavior == .skip
                        ? "Skipping \(category.title)"
                        : "Muting \(category.title)",
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
                        set: { playback.seek(to: $0) }
                    ),
                    in: 0...max(playback.duration, 1)
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

    private var record: LibraryBookRecord? {
        AudiobookLibraryStore.load().first { $0.id == book.id }
    }

    var body: some View {
        GeometryReader { geometry in
            VStack(spacing: 0) {
                HStack {
                    Image(systemName: "chevron.down").font(.title3.bold())
                    Spacer()
                    Image(systemName: "waveform").font(.title2).foregroundStyle(ACTheme.accent)
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

                VStack(spacing: 4) {
                    Slider(value: Binding(get: { playback.position }, set: playback.seek(to:)), in: 0...max(playback.duration, 1))
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

                    NavigationLink { FilterProfileScreen() } label: { tool("shield", "Filters") }

                    Button {
                        UserLibraryStore.addBookmark(bookID: book.id, position: playback.position)
                        bookmarkSaved = true
                        Task { try? await Task.sleep(for: .seconds(1.5)); bookmarkSaved = false }
                    } label: { tool(bookmarkSaved ? "bookmark.fill" : "bookmark", bookmarkSaved ? "Saved" : "Bookmarks") }
                }
                .padding(.top, 18)
                Spacer(minLength: 8)
            }
            .padding(.horizontal, 20)
        }
        .background(ACTheme.background.ignoresSafeArea())
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if let record {
                playback.load(record)
                if let initialPosition { playback.seek(to: initialPosition) }
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
