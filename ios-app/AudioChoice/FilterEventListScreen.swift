import SwiftUI

struct FilterEventListScreen: View {
    let record: LibraryBookRecord
    @State private var reportedIDs: Set<UUID> = []

    private var events: [ScanEvent] { IOSContentTaxonomy.userFacingEvents(record.scanResult?.events ?? []) }
    private var hierarchy: [FilterEventCategoryGroup] { IOSContentTaxonomy.hierarchy(for: record.scanResult?.events ?? []) }

    var body: some View {
        Group {
            if events.isEmpty {
                ContentUnavailableView(
                    "No Filter Events",
                    systemImage: "checkmark.shield",
                    description: Text("This scan did not find supported content events.")
                )
            } else {
                List {
                    ForEach(hierarchy) { category in
                        DisclosureGroup {
                            ForEach(category.groups) { group in
                                DisclosureGroup {
                                    ForEach(group.events) { event in
                                        eventRow(event)
                                    }
                                } label: {
                                    HStack {
                                        Text(group.title).font(.headline)
                                        Spacer()
                                        Text("\(group.events.count)").foregroundStyle(ACTheme.secondaryText)
                                    }
                                }
                            }
                        } label: {
                            Label(category.title, systemImage: category.icon)
                                .font(.title3.bold())
                                .foregroundStyle(ACTheme.accent)
                        }
                    }
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

    private func eventRow(_ event: ScanEvent) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(IOSContentTaxonomy.detail(for: event))
                .font(.subheadline)
                .fixedSize(horizontal: false, vertical: true)
            if IOSContentTaxonomy.category(for: event) != .profanity {
                Text("\(time(event.startTime)) – \(time(event.endTime))")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(ACTheme.secondaryText)
            }
            HStack {
                NavigationLink { PlayerScreen(book: record.book, initialPosition: event.startTime) } label: {
                    Label("Go to event", systemImage: "play.circle")
                }
                Spacer()
                Button(reportedIDs.contains(event.id) ? "Reported" : "Report") {
                    FilterCorrectionStore.report(bookID: record.id, eventID: event.id)
                    reportedIDs.insert(event.id)
                }
                .disabled(reportedIDs.contains(event.id))
            }
            .font(.caption)
        }
        .padding(.vertical, 6)
    }

    private func time(_ seconds: Double) -> String {
        let total = max(Int(seconds), 0)
        return String(format: "%d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }
}
