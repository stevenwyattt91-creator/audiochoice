import SwiftUI

struct ExploreScannedBooksScreen: View {
    @State private var books: [ExploreCatalogBook] = []
    @State private var localRecords: [LibraryBookRecord] = []
    @State private var query = ""
    @State private var isLoading = true
    @State private var errorMessage: String?

    private var results: [ExploreCatalogBook] {
        guard !query.isEmpty else { return books }
        return books.filter { $0.title.localizedCaseInsensitiveContains(query) || ($0.author ?? "").localizedCaseInsensitiveContains(query) }
    }
    private var client: CloudScanClient? { try? CloudScanClient.configured() }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                Text("Explore Scanned Books").font(.largeTitle.bold())
                Text("Audiobooks with reusable AudioChoice filter scans.")
                    .foregroundStyle(ACTheme.secondaryText)
                if isLoading { ProgressView("Loading scanned books…").frame(maxWidth: .infinity).padding(.vertical, 60) }
                else if let errorMessage { ContentUnavailableView("Couldn’t load scanned books", systemImage: "wifi.exclamationmark", description: Text(errorMessage)) }
                else if results.isEmpty { ContentUnavailableView.search(text: query) }
                else { LazyVStack(spacing: 14) { ForEach(results) { book in catalogCard(book) } } }
            }.padding()
        }
        .background(ACTheme.background)
        .navigationTitle("Explore")
        .navigationBarTitleDisplayMode(.inline)
        .searchable(text: $query, prompt: "Search scanned books")
        .task { await load() }
    }

    private func catalogCard(_ book: ExploreCatalogBook) -> some View {
        NavigationLink { ScannedBookDetailScreen(book: book, localRecords: localRecords) } label: {
            ACCard {
                HStack(spacing: 16) {
                    RemoteBookCover(url: client?.coverURL(for: book), title: book.title).frame(width: 92, height: 134)
                    VStack(alignment: .leading, spacing: 7) {
                        Text(book.title).font(.headline).multilineTextAlignment(.leading)
                        Text(book.author ?? "Audiobook").foregroundStyle(ACTheme.secondaryText)
                        Text("\(durationText(book.duration)) • \(book.eventCount) filter controls")
                            .font(.subheadline).foregroundStyle(ACTheme.accent)
                        Text(inLibrary(book) ? "In your library" : "Ready to explore")
                            .font(.caption).foregroundStyle(ACTheme.secondaryText)
                    }
                    Spacer(minLength: 0)
                }
            }
        }.buttonStyle(.plain)
    }

    private func inLibrary(_ book: ExploreCatalogBook) -> Bool {
        localRecords.contains { $0.book.title.caseInsensitiveCompare(book.title) == .orderedSame }
    }

    private func load() async {
        localRecords = AudiobookLibraryStore.load()
        isLoading = true; defer { isLoading = false }
        do {
            guard let client else { throw CloudClientError.invalidConfiguration }
            books = try await client.exploreBooks()
        }
        catch { errorMessage = error.localizedDescription }
    }
}

struct ScannedBookDetailScreen: View {
    let book: ExploreCatalogBook
    let localRecords: [LibraryBookRecord]
    @Environment(\.openURL) private var openURL

    private var localRecord: LibraryBookRecord? { localRecords.first { $0.book.title.caseInsensitiveCompare(book.title) == .orderedSame } }
    private var client: CloudScanClient? { try? CloudScanClient.configured() }
    var body: some View {
        ScrollView {
            VStack(spacing: 24) {
                RemoteBookCover(url: client?.coverURL(for: book), title: book.title).frame(width: 240, height: 350)
                VStack(spacing: 7) {
                    Text(book.title).font(.title.bold()).multilineTextAlignment(.center)
                    Text(book.author ?? "Audiobook").foregroundStyle(ACTheme.secondaryText)
                    Text(book.editionType ?? "Scanned audiobook").foregroundStyle(ACTheme.accent)
                }
                HStack(spacing: 12) {
                    detailStat(durationText(book.duration), "Runtime")
                    detailStat(book.fileType, "Edition")
                    detailStat("\(book.eventCount)", "Controls")
                }
                ACCard {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("About this audiobook").font(.title3.bold())
                        Text(book.description ?? "A synopsis for this audiobook is being prepared.")
                            .foregroundStyle(ACTheme.secondaryText).lineSpacing(4)
                    }
                }
                if let localRecord {
                    NavigationLink(value: localRecord.book) {
                        Label("View in Library", systemImage: "books.vertical.fill").frame(maxWidth: .infinity)
                    }.buttonStyle(.borderedProminent).tint(ACTheme.accent).foregroundStyle(.black)
                } else {
                    Button { openURL(book.purchaseURL) } label: {
                        Label("Buy from \(book.purchaseProvider)", systemImage: "cart") .frame(maxWidth: .infinity)
                    }.buttonStyle(.borderedProminent).tint(ACTheme.accent).foregroundStyle(.black)
                }
            }.padding()
        }
        .background(ACTheme.background)
        .navigationTitle("Scanned Audiobook").navigationBarTitleDisplayMode(.inline)
    }

    private func detailStat(_ value: String, _ label: String) -> some View {
        VStack(spacing: 8) { Text(value).font(.headline); Text(label).font(.caption).foregroundStyle(ACTheme.secondaryText) }
            .frame(maxWidth: .infinity).padding(.vertical, 18).background(ACTheme.panel).clipShape(RoundedRectangle(cornerRadius: 18))
    }
}

private struct RemoteBookCover: View {
    let url: URL?
    let title: String
    var body: some View {
        AsyncImage(url: url) { image in image.resizable().scaledToFill() } placeholder: { ZStack { ACTheme.panel; Image(systemName: "headphones").font(.largeTitle).foregroundStyle(ACTheme.accent) } }
            .clipShape(RoundedRectangle(cornerRadius: 18))
    }
}

private func durationText(_ duration: Double?) -> String {
    guard let duration, duration > 0 else { return "Runtime unavailable" }
    let hours = Int(duration) / 3600; let minutes = (Int(duration) % 3600) / 60
    return hours > 0 ? "\(hours)h \(minutes)m" : "\(minutes)m"
}
