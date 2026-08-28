import Foundation

/// Holds filter reports until they reach the server.
///
/// Queuing is not optional here. The moment someone most wants to report a missed passage
/// is in a car, on a run, or anywhere else with no signal, and a report that failed to
/// upload is a mistake nobody ever hears about. Reports are written to disk first and sent
/// afterwards, so the tap always succeeds from the listener's point of view.
///
/// Ordering does not matter and neither does immediacy: each report is an independent
/// observation stamped with the moment it describes, not the moment it was sent.
@MainActor
final class FilterReportQueue: ObservableObject {
    static let shared = FilterReportQueue()

    private static let storageKey = "filterReports.pending.v1"

    /// Stops a stuck queue from growing without bound. Old reports are dropped first: a
    /// recent one is more likely to still be worth acting on, and by this point the same
    /// problem has almost certainly been reported already.
    private static let maximumPending = 200

    /// Shown briefly after a tap, so a silent queue write still feels like it did something.
    @Published private(set) var lastConfirmation: String?

    private var isFlushing = false

    private init() {}

    /// Records a report and tries to send it. Never throws: the listener has already moved
    /// on, and a failure here is the queue's problem rather than theirs.
    func submit(_ report: FilterReportRequest, confirmation: String) {
        var pending = load()
        pending.append(report)
        if pending.count > Self.maximumPending {
            pending.removeFirst(pending.count - Self.maximumPending)
        }
        save(pending)

        lastConfirmation = confirmation
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(2))
            guard let self, self.lastConfirmation == confirmation else { return }
            self.lastConfirmation = nil
        }
        Task { await flush() }
    }

    /// Whether a status means this report can never succeed.
    ///
    /// Only a malformed report qualifies. Everything else that looks like a client error is
    /// worth another attempt later, and one case matters in particular: an app released ahead
    /// of the server gets 404 from an endpoint that does not exist yet. Treating that as a
    /// refusal would quietly discard every report made before the server caught up -- exactly
    /// the reports from the listeners who tried first.
    private static func isPermanentRefusal(_ status: Int) -> Bool {
        // 400 Bad Request and 422 Unprocessable Content are the server saying the report
        // itself is wrong. 401 and 403 can pass once the session is renewed, 404 and 501 once
        // the endpoint exists, 429 once the limit resets.
        status == 400 || status == 422
    }

    /// Sends everything queued, keeping whatever fails.
    func flush() async {
        guard !isFlushing else { return }
        isFlushing = true
        defer { isFlushing = false }

        var pending = load()
        guard !pending.isEmpty, let client = try? CloudScanClient.configured() else { return }

        var remaining: [FilterReportRequest] = []
        for report in pending {
            do {
                try await client.reportFilter(report)
            } catch CloudClientError.server(let status, _) where Self.isPermanentRefusal(status) {
                // The server will never accept this one, so keeping it would retry forever.
                continue
            } catch {
                remaining.append(report)
            }
        }
        pending = remaining
        save(pending)
    }

    var pendingCount: Int { load().count }

    private func load() -> [FilterReportRequest] {
        guard let data = UserDefaults.standard.data(forKey: Self.storageKey) else { return [] }
        return (try? JSONDecoder().decode([FilterReportRequest].self, from: data)) ?? []
    }

    private func save(_ values: [FilterReportRequest]) {
        guard let data = try? JSONEncoder().encode(values) else { return }
        UserDefaults.standard.set(data, forKey: Self.storageKey)
    }
}
