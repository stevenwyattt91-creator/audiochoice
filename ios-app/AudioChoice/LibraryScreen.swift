import SwiftUI

private enum LibrarySort: String, CaseIterable, Identifiable {
    case recentlyAdded = "Recently Added"
    case titleAZ = "Title A–Z"
    case titleZA = "Title Z–A"
    var id: String { rawValue }
}

struct LibraryScreen: View {
    @State private var records: [LibraryBookRecord] = []
    @State private var accountBooks: [AccountLibraryBook] = []
    @State private var showingSearch = false
    @State private var showingMenu = false
    @State private var sort: LibrarySort = .recentlyAdded

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 26) {
                header
                Text("My Library").font(.largeTitle.bold())

                if let record = continueRecord {
                    continueListening(record)
                }

                HStack {
                    Text("Audiobooks").font(.title2.bold())
                    Spacer()
                    Menu {
                        Picker("Sort audiobooks", selection: $sort) {
                            ForEach(LibrarySort.allCases) { Text($0.rawValue).tag($0) }
                        }
                    } label: {
                        Label(sort.rawValue, systemImage: "line.3.horizontal.decrease.circle")
                            .font(.subheadline.weight(.semibold))
                            .padding(.horizontal, 14).padding(.vertical, 10)
                            .overlay(Capsule().stroke(ACTheme.secondaryText.opacity(0.55), lineWidth: 1.5))
                    }
                }

                if records.isEmpty && accountOnlyBooks.isEmpty {
                    ContentUnavailableView {
                        Label("Your Library Is Ready", systemImage: "books.vertical")
                    } description: {
                        Text("Import an audiobook to begin listening and filtering.")
                    } actions: {
                        NavigationLink("Import Audiobook") { ImportScreen() }
                            .buttonStyle(.borderedProminent).tint(ACTheme.accent)
                    }
                    .padding(.vertical, 60)
                } else {
                    LazyVStack(spacing: 14) {
                        ForEach(sortedRecords) { record in libraryCard(record) }
                        ForEach(accountOnlyBooks) { book in unavailableBookCard(book) }
                    }
                }
            }
            .padding()
            .padding(.bottom, 24)
        }
        .background(ACTheme.background)
        .navigationBarBackButtonHidden()
        .navigationDestination(for: MobileBook.self) { BookDetailScreen(book: $0) }
        .sheet(isPresented: $showingSearch) { LibrarySearchScreen(records: records, accountBooks: accountBooks) }
        .sheet(isPresented: $showingMenu) { LibraryMenuScreen() }
        .task { await refresh() }
        .onAppear { records = AudiobookLibraryStore.load() }
    }

    private var header: some View {
        HStack(spacing: 16) {
            Button { showingMenu = true } label: { Image(systemName: "line.3.horizontal").font(.title2) }
            Text("Audio") + Text("Choice").foregroundStyle(ACTheme.accent)
            Spacer()
            Button { showingSearch = true } label: { Image(systemName: "magnifyingglass").font(.title2) }
            NavigationLink { ExploreScannedBooksScreen() } label: {
                Image(systemName: "globe.americas.fill").font(.title2)
            }
        }
        .font(.title2.bold()).foregroundStyle(.white)
    }

    private var continueRecord: LibraryBookRecord? {
        guard !records.isEmpty else { return nil }
        if let lastID = UserDefaults.standard.string(forKey: "lastPlayedBookID"),
           let match = records.first(where: { $0.id.uuidString == lastID }) { return match }
        return records.max { AudioPlaybackManager.savedPosition(for: $0.id) < AudioPlaybackManager.savedPosition(for: $1.id) }
    }

    private var sortedRecords: [LibraryBookRecord] {
        switch sort {
        case .recentlyAdded: return records.sorted { $0.importedAt > $1.importedAt }
        case .titleAZ: return records.sorted { $0.book.title.localizedCaseInsensitiveCompare($1.book.title) == .orderedAscending }
        case .titleZA: return records.sorted { $0.book.title.localizedCaseInsensitiveCompare($1.book.title) == .orderedDescending }
        }
    }

    private var accountOnlyBooks: [AccountLibraryBook] {
        accountBooks.filter { cloud in !records.contains(where: { $0.accountLibraryID == cloud.id }) }
    }

    private func continueListening(_ record: LibraryBookRecord) -> some View {
        let position = AudioPlaybackManager.savedPosition(for: record.id)
        let duration = record.chapterMarkers?.map { $0.startTime + $0.duration }.max() ?? 0
        let fraction = duration > 0 ? min(position / duration, 1) : 0
        return VStack(alignment: .leading, spacing: 10) {
            Text("Continue Listening").font(.title2.bold())
            NavigationLink(value: record.book) {
                ACCard {
                    VStack(alignment: .leading, spacing: 13) {
                        libraryCover(record, compact: false).frame(height: 180)
                        HStack {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(record.book.title).font(.title3.bold()).lineLimit(1)
                                Text(record.book.author).foregroundStyle(ACTheme.secondaryText).lineLimit(1)
                                Text("Resume at \(timeText(position))").font(.caption).foregroundStyle(ACTheme.accent)
                            }
                            Spacer()
                            Image(systemName: "play.fill").font(.title3).frame(width: 60, height: 60)
                                .background(ACTheme.accent).foregroundStyle(.black).clipShape(Circle())
                        }
                        ProgressView(value: fraction).tint(ACTheme.accent)
                    }
                }
            }.buttonStyle(.plain)
        }
    }

    private func libraryCard(_ record: LibraryBookRecord) -> some View {
        NavigationLink(value: record.book) {
            ACCard {
                HStack(alignment: .top, spacing: 20) {
                    libraryCover(record, compact: true)
                        .frame(width: 82, height: 116)
                        .clipped()
                        .layoutPriority(1)
                    VStack(alignment: .leading, spacing: 7) {
                        Text(AudiobookTitleFormatter.format(record.book.title))
                            .font(.headline)
                            .multilineTextAlignment(.leading)
                            .lineLimit(3)
                            .fixedSize(horizontal: false, vertical: true)
                        Text(record.book.author)
                            .foregroundStyle(ACTheme.secondaryText)
                            .lineLimit(2)
                        if let duration = record.chapterMarkers?.map({ $0.startTime + $0.duration }).max(), duration > 0 {
                            Text(timeText(duration)).font(.subheadline).foregroundStyle(ACTheme.accent)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .layoutPriority(2)
                    Spacer(minLength: 0)
                    Image(systemName: "chevron.right").foregroundStyle(ACTheme.secondaryText)
                }
                .frame(minHeight: 116, alignment: .top)
            }
        }
        .buttonStyle(.plain)
        .contextMenu {
            Button(UserLibraryStore.isFavorite(record.id) ? "Remove Favorite" : "Favorite", systemImage: "heart") { UserLibraryStore.toggleFavorite(record.id) }
            Button("Remove Local Audio", systemImage: "trash", role: .destructive) {
                AudiobookLibraryStore.remove(record); records = AudiobookLibraryStore.load()
            }
        }
    }

    private func unavailableBookCard(_ book: AccountLibraryBook) -> some View {
        ACCard {
            HStack(spacing: 16) {
                RemoteBookCover(
                    url: (try? CloudScanClient.configured())?.coverURL(for: book.coverImageURL),
                    title: book.title
                )
                .frame(width: 90, height: 126)
                VStack(alignment: .leading, spacing: 7) {
                    Text(book.title).font(.headline)
                    Text(book.author ?? "Audiobook").foregroundStyle(ACTheme.secondaryText)
                    Text("Saved to your account • Resume at \(timeText(book.playbackPositionSeconds))")
                        .font(.caption).foregroundStyle(ACTheme.accent)
                    Text("Re-import audio to listen").font(.caption).foregroundStyle(ACTheme.secondaryText)
                }
                Spacer()
                NavigationLink { ImportScreen() } label: { Image(systemName: "square.and.arrow.down") }
            }
        }
    }

    private func refresh() async {
        var loaded = AudiobookLibraryStore.load()
        for index in loaded.indices {
            let normalized = AudiobookTitleFormatter.format(
                loaded[index].book.title,
                editionType: loaded[index].fingerprint?.editionType,
                partNumber: loaded[index].fingerprint?.partNumber,
                totalParts: loaded[index].fingerprint?.totalParts
            )
            if normalized != loaded[index].book.title {
                loaded[index].book.title = normalized
                AudiobookLibraryStore.update(loaded[index])
            }
        }
        for index in loaded.indices where loaded[index].artworkFileName == nil {
            if let recovered = try? await AudiobookImportService().recoverArtwork(for: loaded[index]) {
                loaded[index] = recovered
            }
        }
        records = loaded
        guard let client = try? CloudScanClient.configured() else { return }

        var remoteBooks = (try? await client.library()) ?? []
        for index in loaded.indices {
            guard let fingerprint = loaded[index].fingerprint,
                  let remote = remoteBooks.first(where: {
                      $0.fingerprint.sha256.caseInsensitiveCompare(fingerprint.sha256) == .orderedSame
                  }) else { continue }
            loaded[index].accountLibraryID = remote.id
            AudioPlaybackManager.applyRemotePosition(
                remote.playbackPositionSeconds,
                updatedAt: remote.updatedAt,
                for: loaded[index].id
            )
            if !remote.title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                loaded[index].book.title = AudiobookTitleFormatter.format(
                    remote.title,
                    editionType: remote.fingerprint.editionType,
                    partNumber: remote.fingerprint.partNumber,
                    totalParts: remote.fingerprint.totalParts
                )
            }
            if let author = remote.author?.trimmingCharacters(in: .whitespacesAndNewlines), !author.isEmpty {
                loaded[index].book.author = author
            }
            AudiobookLibraryStore.update(loaded[index])
        }

        for index in loaded.indices where loaded[index].accountLibraryID == nil {
            guard let fingerprint = loaded[index].fingerprint else { continue }
            // Narrator and signature come from the file's own tags. Sending them is
            // what lets the server recognise a converted or re-tagged copy as the same
            // edition and reuse its transcript and filter results.
            let request = LibraryBookUpsertRequest(
                fingerprint: fingerprint,
                title: loaded[index].book.title,
                author: loaded[index].book.author,
                narrator: loaded[index].narrator,
                coverImageURL: nil,
                signature: loaded[index].editionSignature
            )
            if let remote = try? await client.saveLibraryBook(request) {
                loaded[index].accountLibraryID = remote.id
                AudioPlaybackManager.applyRemotePosition(
                    remote.playbackPositionSeconds,
                    updatedAt: remote.updatedAt,
                    for: loaded[index].id
                )
                AudiobookLibraryStore.update(loaded[index])
            }
        }
        records = AudiobookLibraryStore.load()
        remoteBooks = (try? await client.library()) ?? remoteBooks
        accountBooks = remoteBooks
    }

    @ViewBuilder
    private func libraryCover(_ record: LibraryBookRecord, compact: Bool) -> some View {
        if record.artworkFileName != nil {
            BookCover(title: record.book.title, compact: compact, artworkFileName: record.artworkFileName)
        } else if let cloud = accountBook(for: record),
                  let client = try? CloudScanClient.configured(),
                  let url = client.coverURL(for: cloud.coverImageURL) {
            RemoteBookCover(url: url, title: record.book.title)
        } else {
            BookCover(title: record.book.title, compact: compact)
        }
    }

    private func accountBook(for record: LibraryBookRecord) -> AccountLibraryBook? {
        if let id = record.accountLibraryID,
           let match = accountBooks.first(where: { $0.id == id }) { return match }
        guard let sha = record.fingerprint?.sha256 else { return nil }
        return accountBooks.first { $0.fingerprint.sha256.caseInsensitiveCompare(sha) == .orderedSame }
    }

    private func timeText(_ seconds: Double) -> String {
        let total = max(0, Int(seconds)); return String(format: "%d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }
}

private struct LibraryMenuScreen: View {
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: 22) {
                HStack { Text("Audio") + Text("Choice").foregroundStyle(ACTheme.accent); Spacer() }
                    .font(.title.bold())
                Label("My Library", systemImage: "books.vertical.fill").font(.title3.weight(.semibold))
                    .padding().frame(maxWidth: .infinity, alignment: .leading).background(ACTheme.panel).clipShape(RoundedRectangle(cornerRadius: 22))
                NavigationLink { ExploreScannedBooksScreen() } label: {
                    Label("Explore Scanned Books", systemImage: "globe.americas.fill").font(.title3.weight(.semibold))
                }.buttonStyle(.plain).padding()
                Spacer()
            }.padding().background(ACTheme.background).toolbar { Button("Done") { dismiss() } }
        }.preferredColorScheme(.dark)
    }
}

private struct LibrarySearchScreen: View {
    let records: [LibraryBookRecord]
    let accountBooks: [AccountLibraryBook]
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    private var results: [LibraryBookRecord] { query.isEmpty ? records : records.filter { $0.book.title.localizedCaseInsensitiveContains(query) || $0.book.author.localizedCaseInsensitiveContains(query) } }
    var body: some View {
        NavigationStack {
            List(results) { record in NavigationLink { BookDetailScreen(book: record.book) } label: { VStack(alignment: .leading) { Text(record.book.title); Text(record.book.author).font(.caption).foregroundStyle(.secondary) } } }
                .searchable(text: $query, prompt: "Search your library")
                .navigationTitle("Search Library").toolbar { Button("Done") { dismiss() } }
        }.preferredColorScheme(.dark)
    }
}
