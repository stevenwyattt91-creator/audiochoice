import Foundation
import SwiftUI

struct AudioBookmark: Identifiable, Codable, Hashable {
    let id: UUID
    let bookID: UUID
    let position: Double
    let createdAt: Date
}

struct BookCollection: Identifiable, Codable, Hashable {
    let id: UUID
    var name: String
    var bookIDs: [UUID]
}

enum UserLibraryStore {
    private static let favoriteKey = "favoriteBooks.v1"
    private static let bookmarkKey = "audioBookmarks.v1"
    private static let collectionKey = "bookCollections.v1"

    static func isFavorite(_ id: UUID) -> Bool { favorites.contains(id) }
    static func toggleFavorite(_ id: UUID) {
        var values = favorites
        if values.contains(id) { values.remove(id) } else { values.insert(id) }
        save(values, key: favoriteKey)
    }

    static func bookmarks(for bookID: UUID) -> [AudioBookmark] {
        bookmarks.filter { $0.bookID == bookID }.sorted { $0.position < $1.position }
    }
    static func addBookmark(bookID: UUID, position: Double) {
        var values = bookmarks
        guard !values.contains(where: { $0.bookID == bookID && abs($0.position - position) < 2 }) else { return }
        values.append(AudioBookmark(id: UUID(), bookID: bookID, position: position, createdAt: Date()))
        save(values, key: bookmarkKey)
    }
    static func removeBookmark(_ value: AudioBookmark) {
        save(bookmarks.filter { $0.id != value.id }, key: bookmarkKey)
    }

    static var collections: [BookCollection] { load(collectionKey, default: []) }
    static func createCollection(name: String) {
        let clean = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else { return }
        var values = collections
        values.append(BookCollection(id: UUID(), name: clean, bookIDs: []))
        save(values, key: collectionKey)
    }
    static func removeCollection(_ id: UUID) { save(collections.filter { $0.id != id }, key: collectionKey) }
    static func setBook(_ bookID: UUID, included: Bool, collectionID: UUID) {
        var values = collections
        guard let index = values.firstIndex(where: { $0.id == collectionID }) else { return }
        values[index].bookIDs.removeAll { $0 == bookID }
        if included { values[index].bookIDs.append(bookID) }
        save(values, key: collectionKey)
    }

    private static var favorites: Set<UUID> { load(favoriteKey, default: []) }
    private static var bookmarks: [AudioBookmark] { load(bookmarkKey, default: []) }
    private static func load<T: Decodable>(_ key: String, default defaultValue: T) -> T {
        guard let data = UserDefaults.standard.data(forKey: key),
              let value = try? JSONDecoder().decode(T.self, from: data) else {
            return defaultValue
        }
        return value
    }
    private static func save<T: Encodable>(_ value: T, key: String) {
        if let data = try? JSONEncoder().encode(value) { UserDefaults.standard.set(data, forKey: key) }
    }
}

struct CollectionsScreen: View {
    @State private var collections: [BookCollection] = []
    @State private var newName = ""
    @State private var showingNewCollection = false

    var body: some View {
        Group {
            if collections.isEmpty {
                ContentUnavailableView("No Collections", systemImage: "rectangle.stack", description: Text("Group audiobooks into series or personal lists."))
            } else {
                List {
                    ForEach(collections) { collection in
                        NavigationLink { CollectionDetailScreen(collectionID: collection.id) } label: {
                            Label {
                                VStack(alignment: .leading) {
                                    Text(collection.name)
                                    Text("\(collection.bookIDs.count) books").font(.caption).foregroundStyle(.secondary)
                                }
                            } icon: { Image(systemName: "books.vertical.fill").foregroundStyle(ACTheme.accent) }
                        }
                    }
                    .onDelete { offsets in
                        offsets.map { collections[$0].id }.forEach(UserLibraryStore.removeCollection)
                        collections = UserLibraryStore.collections
                    }
                }
                .scrollContentBackground(.hidden)
            }
        }
        .background(ACTheme.background)
        .navigationTitle("Collections")
        .toolbar { Button("New Collection", systemImage: "plus") { showingNewCollection = true } }
        .alert("New Collection", isPresented: $showingNewCollection) {
            TextField("Name", text: $newName)
            Button("Create") {
                UserLibraryStore.createCollection(name: newName)
                collections = UserLibraryStore.collections
                newName = ""
            }
            Button("Cancel", role: .cancel) {}
        }
        .onAppear { collections = UserLibraryStore.collections }
    }
}

private struct CollectionDetailScreen: View {
    let collectionID: UUID
    @State private var collection: BookCollection?
    @State private var records: [LibraryBookRecord] = []
    var body: some View {
        List(records) { record in
            Toggle(isOn: Binding(
                get: { collection?.bookIDs.contains(record.id) == true },
                set: { included in
                    UserLibraryStore.setBook(record.id, included: included, collectionID: collectionID)
                    collection = UserLibraryStore.collections.first { $0.id == collectionID }
                }
            )) {
                VStack(alignment: .leading) {
                    Text(record.book.title)
                    Text(record.book.author).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .navigationTitle(collection?.name ?? "Collection")
        .acScreen()
        .onAppear {
            collection = UserLibraryStore.collections.first { $0.id == collectionID }
            records = AudiobookLibraryStore.load()
        }
    }
}

struct ChapterListScreen: View {
    let record: LibraryBookRecord
    var body: some View {
        List(record.chapterMarkers ?? []) { chapter in
            NavigationLink {
                PlayerScreen(book: record.book, initialPosition: chapter.startTime)
            } label: {
                VStack(alignment: .leading) {
                    Text(chapter.title)
                    Text(time(chapter.startTime)).font(.caption).foregroundStyle(ACTheme.secondaryText)
                }
            }
        }
        .navigationTitle("Chapters")
        .acScreen()
    }
    private func time(_ value: Double) -> String {
        let seconds = max(Int(value), 0)
        return String(format: "%d:%02d:%02d", seconds / 3600, (seconds % 3600) / 60, seconds % 60)
    }
}

struct BookmarkListScreen: View {
    let record: LibraryBookRecord
    @State private var bookmarks: [AudioBookmark] = []
    var body: some View {
        Group {
            if bookmarks.isEmpty {
                ContentUnavailableView("No Bookmarks", systemImage: "bookmark", description: Text("Add one from the player."))
            } else {
                List {
                    ForEach(bookmarks) { bookmark in
                        NavigationLink {
                            PlayerScreen(book: record.book, initialPosition: bookmark.position)
                        } label: { Text(time(bookmark.position)).monospacedDigit() }
                    }
                    .onDelete { offsets in
                        offsets.map { bookmarks[$0] }.forEach(UserLibraryStore.removeBookmark)
                        bookmarks = UserLibraryStore.bookmarks(for: record.id)
                    }
                }
                .scrollContentBackground(.hidden)
            }
        }
        .background(ACTheme.background)
        .navigationTitle("Bookmarks")
        .onAppear { bookmarks = UserLibraryStore.bookmarks(for: record.id) }
    }
    private func time(_ value: Double) -> String {
        let seconds = max(Int(value), 0)
        return String(format: "%d:%02d:%02d", seconds / 3600, (seconds % 3600) / 60, seconds % 60)
    }
}
