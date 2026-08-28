import Foundation

// Where a book's filter choices meet the network. Kept apart from the model, the
// predicate and the editor so that the logic deciding what gets filtered stays free
// of the API client and can be exercised without one.

extension BookFilterSettings {
    /// Converts to the server's shape.
    ///
    /// Identifiers that will not parse as GUIDs are dropped rather than sent, because
    /// the endpoint would reject the whole request and lose the rest of the choices with
    /// it. Locally they stay put, so nothing is silently forgotten on this device.
    var upsertRequest: BookFilterSettingsUpsertRequest {
        BookFilterSettingsUpsertRequest(
            disabledCategoryIDs: disabledCategoryIDs.compactMap(UUID.init(uuidString:)),
            disabledGroupIDs: disabledGroupIDs.compactMap(UUID.init(uuidString:)),
            disabledEventKeys: Array(disabledEventKeys),
            disabledAggregateKeys: Array(disabledAggregateKeys)
        )
    }

    init(_ remote: RemoteBookFilterSettings) {
        self.init(
            disabledCategoryIDs: Set(remote.disabledCategoryIDs.map { $0.uuidString.lowercased() }),
            disabledGroupIDs: Set(remote.disabledGroupIDs.map { $0.uuidString.lowercased() }),
            disabledEventKeys: Set(remote.disabledEventKeys),
            disabledAggregateKeys: Set(remote.disabledAggregateKeys)
        )
    }
}

extension BookFilterSettingsStore {
    /// Adopts the account's stored choices for this book, if there are any.
    ///
    /// Returns what playback should now use. A book nobody has adjusted has no server
    /// record, and an unreachable server is not a reason to change anything, so both
    /// cases keep the local copy. Filtering therefore keeps working offline, and a
    /// network failure can never quietly switch a filter off.
    @discardableResult
    static func refresh(bookID: UUID, accountLibraryID: UUID?) async -> BookFilterSettings {
        let hasOwnChoices = hasStoredSettings(bookID)
        let local = load(bookID)

        if let accountLibraryID,
           let client = try? CloudScanClient.configured(),
           let remote = try? await client.bookFilterSettings(bookID: accountLibraryID) {
            let settings = BookFilterSettings(remote)
            save(settings, bookID: bookID)
            return settings
        }

        // Nothing stored anywhere for this book, so the listener's saved profile decides
        // where it starts. Checked against having *stored* choices rather than against
        // having any disabled: someone who deliberately left everything filtered must not
        // have a profile applied over that.
        if !hasOwnChoices, let seeded = await MainActor.run(body: {
            FilterProfileStore.defaultSettings()
        }) {
            save(seeded, bookID: bookID)
            return seeded
        }

        return local
    }

    /// Saves locally, then mirrors to the account.
    ///
    /// The local write is not conditional on the upload: the listener's choice has to
    /// take effect on this device even with no network, and playback reads local state.
    static func update(
        _ settings: BookFilterSettings,
        bookID: UUID,
        accountLibraryID: UUID?
    ) {
        save(settings, bookID: bookID)
        guard let accountLibraryID else { return }
        Task {
            guard let client = try? CloudScanClient.configured() else { return }
            _ = try? await client.saveBookFilterSettings(
                bookID: accountLibraryID,
                settings.upsertRequest
            )
        }
    }
}
