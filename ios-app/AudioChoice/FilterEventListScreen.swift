import SwiftUI

struct FilterEventListScreen: View {
    let record: LibraryBookRecord
    @State private var reportedIDs: Set<UUID> = []

    private var events: [ScanEvent] {
        record.scanResult?.events.sorted { $0.startTime < $1.startTime } ?? []
    }

    var body: some View {
        Group {
            if events.isEmpty {
                ContentUnavailableView(
                    "No Filter Events",
                    systemImage: "checkmark.shield",
                    description: Text("This scan did not find supported content events.")
                )
            } else {
                List(events) { event in
                    VStack(alignment: .leading, spacing: 8) {
                        HStack {
                            if let category = IOSContentTaxonomy.category(for: event) {
                                Label(category.title, systemImage: category.icon)
                                    .foregroundStyle(ACTheme.accent)
                            } else {
                                Label("Unknown", systemImage: "questionmark.circle")
                            }
                            Spacer()
                            Text("\(Int(event.confidence * 100))%")
                                .font(.caption)
                                .foregroundStyle(ACTheme.secondaryText)
                        }
                        Text(IOSContentTaxonomy.detail(for: event)).font(.subheadline)
                        Text("\(time(event.startTime)) – \(time(event.endTime))")
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(ACTheme.secondaryText)
                        HStack {
                            NavigationLink {
                                PlayerScreen(book: record.book, initialPosition: event.startTime)
                            } label: {
                                Label("Preview", systemImage: "play.circle")
                            }
                            Spacer()
                            Button(reportedIDs.contains(event.id) ? "Reported" : "Report Incorrect") {
                                FilterCorrectionStore.report(bookID: record.id, eventID: event.id)
                                reportedIDs.insert(event.id)
                            }
                            .disabled(reportedIDs.contains(event.id))
                        }
                        .font(.caption)
                    }
                    .padding(.vertical, 5)
                }
                .scrollContentBackground(.hidden)
            }
        }
        .background(ACTheme.background)
        .navigationTitle("Filter Events")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear {
            reportedIDs = Set(events.filter {
                FilterCorrectionStore.contains(bookID: record.id, eventID: $0.id)
            }.map(\.id))
        }
    }

    private func time(_ seconds: Double) -> String {
        let total = max(Int(seconds), 0)
        return String(format: "%d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }
}
