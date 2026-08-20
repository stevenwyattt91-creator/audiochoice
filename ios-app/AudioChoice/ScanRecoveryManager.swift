import Foundation

@MainActor
final class ScanRecoveryManager: ObservableObject {
    static let shared = ScanRecoveryManager()
    @Published private(set) var activeRecoveryCount = 0
    private var recovering = Set<UUID>()

    private init() {}

    func recoverPendingScans() async {
        guard let client = try? CloudScanClient.configured() else { return }
        let pending = AudiobookLibraryStore.load().filter { $0.pendingScanID != nil }
        for record in pending where !recovering.contains(record.id) {
            recovering.insert(record.id)
            activeRecoveryCount = recovering.count
            Task { await monitor(record: record, client: client) }
        }
    }

    private func monitor(record: LibraryBookRecord, client: CloudScanClient) async {
        defer {
            recovering.remove(record.id)
            activeRecoveryCount = recovering.count
        }
        guard let scanID = record.pendingScanID else { return }
        for _ in 0..<900 {
            do {
                let response = try await client.job(scanID: scanID)
                switch response.status {
                case .available, .completed:
                    guard let result = response.result else { return }
                    AudiobookLibraryStore.attach(result: result, to: record.id)
                    return
                case .queued, .processing:
                    AudiobookLibraryStore.setPendingScan(id: scanID, state: response.status, for: record.id)
                case .failed, .uploadRequired:
                    AudiobookLibraryStore.setScanState(.failed, for: record.id)
                    return
                }
                try await Task.sleep(for: .seconds(3))
            } catch is CancellationError {
                return
            } catch {
                try? await Task.sleep(for: .seconds(10))
            }
        }
    }
}
