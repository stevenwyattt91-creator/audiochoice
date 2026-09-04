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
    /// Renders as one of the player's tool buttons rather than a standalone control, so it
    /// sits in the row beside Bookmarks as it does on Android.
    var asPlayerTool = false
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
                    if asPlayerTool {
                        VStack(spacing: 6) {
                            // The icon fills once a report is saved, which is the whole
                            // acknowledgement in this form. A caption appearing under one
                            // item would resize its column and shift the whole row.
                            Image(systemName: queue.lastConfirmation == nil ? "flag" : "flag.fill")
                                .font(.title3)
                            Text("Report").font(.caption2).foregroundStyle(ACTheme.secondaryText)
                        }
                        .foregroundStyle(ACTheme.accent)
                        .frame(maxWidth: .infinity)
                    } else {
                        Label("Report missed content", systemImage: "flag")
                            .font(.caption.bold())
                            .foregroundStyle(ACTheme.secondaryText)
                    }
                }
                .accessibilityLabel("Report missed content")
                .accessibilityHint("Reports that something played which should have been filtered")

                if !asPlayerTool, let confirmation = queue.lastConfirmation {
                    Text(confirmation)
                        .font(.caption2)
                        .foregroundStyle(ACTheme.accent)
                        .transition(.opacity)
                }
            }
            // Asked after the report is already safe, so dismissing it loses nothing.
            .sheet(isPresented: $showingRefinement) {
                FilterReportRefinementSheet(
                    reportedPosition: reportedPosition,
                    onSubmit: { category, timeframe in
                        queue.submit(
                            FilterReportComposer.missedContent(
                                fingerprint: fingerprint,
                                position: reportedPosition,
                                scannerVersion: record.scanResult?.scannerVersion,
                                categoryID: category.map(IOSContentTaxonomy.categoryID(for:)),
                                windowSeconds: timeframe.seconds
                            ),
                            confirmation: "Thank you, noted."
                        )
                    }
                )
                .presentationDetents([.medium])
            }
        }
    }

    private func timestamp(_ seconds: Double) -> String {
        let total = max(Int(seconds), 0)
        return String(format: "%d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }
}

/// The optional "what did you hear, and how far back" refinement offered after a report
/// is already saved. A sheet rather than a confirmationDialog because that control only
/// supports a flat list of buttons, and this needs two independent choices -- category and
/// time frame -- submitted together.
private struct FilterReportRefinementSheet: View {
    let reportedPosition: Double
    let onSubmit: (FilterCategory?, FilterReportTimeframe) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var selectedCategory: FilterCategory?
    @State private var selectedTimeframe: FilterReportTimeframe = .justThisMoment

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Text("Optional. Your report at \(timestamp(reportedPosition)) is already saved.")
                        .foregroundStyle(ACTheme.secondaryText)
                }
                Section("Category") {
                    ForEach(FilterCategory.allCases) { category in
                        Button {
                            selectedCategory = selectedCategory == category ? nil : category
                        } label: {
                            HStack {
                                Text(category.title).foregroundStyle(.primary)
                                Spacer()
                                if selectedCategory == category {
                                    Image(systemName: "checkmark").foregroundStyle(ACTheme.accent)
                                }
                            }
                        }
                    }
                }
                Section("How far back") {
                    ForEach(FilterReportTimeframe.allCases) { timeframe in
                        Button {
                            selectedTimeframe = timeframe
                        } label: {
                            HStack {
                                Text(timeframe.title).foregroundStyle(.primary)
                                Spacer()
                                if selectedTimeframe == timeframe {
                                    Image(systemName: "checkmark").foregroundStyle(ACTheme.accent)
                                }
                            }
                        }
                    }
                }
            }
            .navigationTitle("What did you hear?")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Skip") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Submit") {
                        onSubmit(selectedCategory, selectedTimeframe)
                        dismiss()
                    }
                }
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
