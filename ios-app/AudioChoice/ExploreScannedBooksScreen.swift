import SwiftUI
import UIKit

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
                HStack(alignment: .top, spacing: 14) {
                    catalogCover(book)
                        .frame(width: 78, height: 114)
                        .clipped()
                        .layoutPriority(1)
                    VStack(alignment: .leading, spacing: 7) {
                        Text(book.title)
                            .font(.headline)
                            .multilineTextAlignment(.leading)
                            .lineLimit(3)
                            .fixedSize(horizontal: false, vertical: true)
                        Text(book.author ?? "Audiobook")
                            .font(.subheadline)
                            .foregroundStyle(ACTheme.secondaryText)
                            .lineLimit(2)
                        Text("\(durationText(book.duration)) • \(book.eventCount) filter controls")
                            .font(.caption).foregroundStyle(ACTheme.accent)
                            .fixedSize(horizontal: false, vertical: true)
                        Text(inLibrary(book) ? "In your library" : "Ready to explore")
                            .font(.caption).foregroundStyle(ACTheme.secondaryText)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .layoutPriority(2)
                    Spacer(minLength: 0)
                }
                .frame(minHeight: 114, alignment: .top)
            }
        }.buttonStyle(.plain)
    }

    private func inLibrary(_ book: ExploreCatalogBook) -> Bool {
        localRecords.contains { $0.book.title.caseInsensitiveCompare(book.title) == .orderedSame }
    }

    @ViewBuilder
    private func catalogCover(_ book: ExploreCatalogBook) -> some View {
        if let artwork = localRecords.first(where: {
            $0.book.title.caseInsensitiveCompare(book.title) == .orderedSame
        })?.artworkFileName {
            BookCover(title: book.title, compact: true, artworkFileName: artwork)
        } else {
            RemoteBookCover(url: client?.coverURL(for: book), title: book.title)
        }
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
                detailCover(book).frame(width: 240, height: 350)
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

    @ViewBuilder
    private func detailCover(_ book: ExploreCatalogBook) -> some View {
        if let artwork = localRecord?.artworkFileName {
            BookCover(title: book.title, artworkFileName: artwork)
        } else {
            RemoteBookCover(url: client?.coverURL(for: book), title: book.title)
        }
    }
}

struct RemoteBookCover: View {
    let url: URL?
    let title: String
    @State private var image: UIImage?

    var body: some View {
        ZStack {
            ACTheme.panel
            if let image {
                Image(uiImage: image).resizable().scaledToFill().clipped()
            } else {
                Image(systemName: "headphones").font(.largeTitle).foregroundStyle(ACTheme.accent)
            }
        }
            .clipped()
            .clipShape(RoundedRectangle(cornerRadius: 18))
            .task(id: url) {
                image = nil
                guard let url, let client = try? CloudScanClient.configured(),
                      let data = try? await client.coverImageData(from: url) else { return }
                image = UIImage(data: data)
            }
    }
}

private func durationText(_ duration: Double?) -> String {
    guard let duration, duration > 0 else { return "Runtime unavailable" }
    let hours = Int(duration) / 3600; let minutes = (Int(duration) % 3600) / 60
    return hours > 0 ? "\(hours)h \(minutes)m" : "\(minutes)m"
}
