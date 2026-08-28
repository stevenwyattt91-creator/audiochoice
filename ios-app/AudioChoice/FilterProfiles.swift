import Foundation

/// The account's saved profiles, and which one applies to new books.
///
/// Cached locally so that opening a book offline still starts from the listener's own
/// defaults rather than from "filter everything", which would be a worse surprise than a
/// stale profile.
@MainActor
final class FilterProfileStore: ObservableObject {
    static let shared = FilterProfileStore()

    private static let storageKey = "filterProfiles.v1"

    @Published private(set) var profiles: [FilterProfile] = []
    @Published private(set) var isLoading = false
    @Published private(set) var errorMessage: String?

    var active: FilterProfile? { profiles.first { $0.isActive } }

    private init() {
        profiles = Self.cached()
    }

    func refresh() async {
        isLoading = true
        defer { isLoading = false }
        guard let client = try? CloudScanClient.configured() else { return }
        do {
            let loaded = try await client.filterProfiles()
            profiles = loaded
            Self.cache(loaded)
            errorMessage = nil
        } catch {
            // The cached copy stays in use. A profile that cannot be fetched is not a reason
            // to change what is being filtered.
            errorMessage = error.localizedDescription
        }
    }

    @discardableResult
    func save(name: String, settings: BookFilterSettings, makeActive: Bool) async -> Bool {
        guard let client = try? CloudScanClient.configured() else { return false }
        let request = FilterProfileUpsertRequest(
            name: name,
            isActive: makeActive,
            rules: FilterProfileMapping.rules(from: settings),
            customWords: []
        )
        do {
            _ = try await client.createFilterProfile(request)
            await refresh()
            return true
        } catch {
            errorMessage = error.localizedDescription
            return false
        }
    }

    @discardableResult
    func activate(_ profile: FilterProfile) async -> Bool {
        guard let client = try? CloudScanClient.configured() else { return false }
        do {
            _ = try await client.updateFilterProfile(
                id: profile.id,
                FilterProfileUpsertRequest(
                    name: profile.name,
                    isActive: true,
                    rules: profile.rules,
                    customWords: profile.customWords
                )
            )
            await refresh()
            return true
        } catch {
            errorMessage = error.localizedDescription
            return false
        }
    }

    @discardableResult
    func delete(_ profile: FilterProfile) async -> Bool {
        guard let client = try? CloudScanClient.configured() else { return false }
        do {
            try await client.deleteFilterProfile(id: profile.id)
            await refresh()
            return true
        } catch {
            errorMessage = error.localizedDescription
            return false
        }
    }

    /// The starting choices for a book with none of its own, or nil when no profile is set.
    static func defaultSettings() -> BookFilterSettings? {
        guard let profile = cached().first(where: { $0.isActive }) else { return nil }
        return FilterProfileMapping.settings(
            from: profile,
            taxonomy: FilterProfileMapping.knownTaxonomy()
        )
    }

    private static func cached() -> [FilterProfile] {
        guard let data = UserDefaults.standard.data(forKey: storageKey) else { return [] }
        return (try? JSONDecoder().decode([FilterProfile].self, from: data)) ?? []
    }

    private static func cache(_ values: [FilterProfile]) {
        guard let data = try? JSONEncoder().encode(values) else { return }
        UserDefaults.standard.set(data, forKey: storageKey)
    }
}
