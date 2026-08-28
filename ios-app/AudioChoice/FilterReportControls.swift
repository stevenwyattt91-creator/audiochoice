import SwiftUI

/// One tap to say "that should not have played".
///
/// No dialog, no form, no category picker in the way. Someone hearing something they asked
/// never to hear is usually driving, walking or washing up, and anything that needs reading
/// means the report does not happen. The tap records the moment and confirms; refining it is
/// offered afterwards and can be ignored.
struct FilterReportControl: View {
    let record: LibraryBookRecord?
    @ObservedObject var playback: AudioPlaybackManager
    @StateObject private var queue = FilterReportQueue.shared
    @State private var showingRefinement = false
    @State private var reportedPosition: Double = 0

    var body: some View {
        if let record, let fingerprint = record.fingerprint {
            VStack(spacing: 6) {
                Button {
                    reportedPosition = playback.position
                    queue.submit(
                        FilterReportComposer.missedContent(
                            fingerprint: fingerprint,
                            position: reportedPosition,
                            scannerVersion: record.scanResult?.scannerVersion
                        ),
                        confirmation: "Reported at \(timestamp(reportedPosition)). Thank you."
                    )
                    showingRefinement = true
                } label: {
                    Label("Report missed content", systemImage: "flag")
                        .font(.caption.bold())
                        .foregroundStyle(ACTheme.secondaryText)
                }
                .accessibilityHint("Reports that something played which should have been filtered")

                if let confirmation = queue.lastConfirmation {
                    Text(confirmation)
                        .font(.caption2)
                        .foregroundStyle(ACTheme.accent)
                        .transition(.opacity)
                }
            }
            // Asked after the report is already safe, so dismissing it loses nothing.
            .confirmationDialog(
                "What did you hear?",
                isPresented: $showingRefinement,
                titleVisibility: .visible
            ) {
                ForEach(FilterCategory.allCases) { category in
                    Button(category.title) {
                        queue.submit(
                            FilterReportComposer.missedContent(
                                fingerprint: fingerprint,
                                position: reportedPosition,
                                scannerVersion: record.scanResult?.scannerVersion,
                                categoryID: IOSContentTaxonomy.categoryID(for: category)
                            ),
                            confirmation: "Thank you. Noted as \(category.title.lowercased())."
                        )
                    }
                }
                Button("Skip", role: .cancel) {}
            } message: {
                Text("Optional. Your report at \(timestamp(reportedPosition)) is already saved.")
            }
        }
    }

    private func timestamp(_ seconds: Double) -> String {
        let total = max(Int(seconds), 0)
        return String(format: "%d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }
}

/// "That should not have been cut", offered on the skip notice itself.
///
/// Placed here rather than in a menu because this is the only moment the app knows which
/// control fired, and naming the event is what turns a vague complaint about over-filtering
/// into something that can actually be corrected.
struct WronglySkippedButton: View {
    let record: LibraryBookRecord?
    let event: ScanEvent
    @StateObject private var queue = FilterReportQueue.shared

    var body: some View {
        if let record, let fingerprint = record.fingerprint {
            Button("Wrong?") {
                queue.submit(
                    FilterReportComposer.wronglyFiltered(
                        fingerprint: fingerprint,
                        event: event,
                        scannerVersion: record.scanResult?.scannerVersion
                    ),
                    confirmation: "Reported as wrongly skipped. Thank you."
                )
            }
            .font(.caption.bold())
            .foregroundStyle(ACTheme.secondaryText)
            .accessibilityLabel("Report this passage as wrongly skipped")
        }
    }
}
