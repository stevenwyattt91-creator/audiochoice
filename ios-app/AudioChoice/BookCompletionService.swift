import Foundation

/// Marks a book finished, or not, from anywhere in the app.
///
/// Separate from the playback manager because a listener can mark a book they are not
/// currently playing, and because the manager only knows about the one open book.
@MainActor
enum BookCompletionService {
    @discardableResult
    static func setFinished(
        _ value: Bool,
        for record: LibraryBookRecord
    ) async -> LibraryBookRecord? {
        let updated = AudiobookLibraryStore.setFinished(value, for: record.id)
        guard let accountID = updated?.accountLibraryID ?? record.accountLibraryID else {
            return updated
        }
        // The position is sent unchanged. UpdateProgress assigns both fields together, so
        // omitting it would move the listener's place as a side effect of marking a book.
        let position = AudioPlaybackManager.savedPosition(for: record.id)
        guard let client = try? CloudScanClient.configured() else { return updated }
        _ = try? await client.saveProgress(
            bookID: accountID, position: position, isFinished: value
        )
        return updated
    }
}
