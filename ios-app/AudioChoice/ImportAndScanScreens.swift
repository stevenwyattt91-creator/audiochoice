import SwiftUI
import UniformTypeIdentifiers

struct ImportScreen: View {
    @State private var showingImporter = false
    @State private var selectedFileName: String?
    @State private var importedRecords: [LibraryBookRecord] = []
    @State private var isImporting = false
    @State private var importError: String?

    var body: some View {
        ScrollView {
            VStack(spacing: 28) {
                VStack(spacing: 18) {
                    Image(systemName: "icloud.and.arrow.up")
                        .font(.system(size: 60, weight: .light))
                        .foregroundStyle(ACTheme.accent)

                    Text(selectedFileName ?? "Choose your audiobook")
                        .font(.title3.weight(.semibold))

                    Text("AudioChoice checks the fingerprint first and uploads audio only when a scan is unavailable.")
                        .font(.subheadline)
                        .foregroundStyle(ACTheme.secondaryText)
                        .multilineTextAlignment(.center)

                    Button("Browse Files") {
                        showingImporter = true
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .tint(ACTheme.accent)
                    .foregroundStyle(.black)
                    .disabled(isImporting)

                    if isImporting {
                        ProgressView("Saving to your library…")
                            .tint(ACTheme.accent)
                    }
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 42)
                .padding(.horizontal, 22)
                .background(ACTheme.panel)
                .clipShape(RoundedRectangle(cornerRadius: 22))
                .overlay {
                    RoundedRectangle(cornerRadius: 22)
                        .stroke(style: StrokeStyle(lineWidth: 1, dash: [6]))
                        .foregroundStyle(ACTheme.border)
                }

                VStack(spacing: 16) {
                    Text("Supported Formats")
                        .font(.subheadline)
                        .foregroundStyle(ACTheme.secondaryText)
                    HStack {
                        format("MP3")
                        format("M4B")
                        format("AAX")
                        format("M4A")
                    }
                }

                ACCard {
                    Label {
                        Text("Transcripts stay private on AudioChoice servers and are never returned to mobile devices.")
                    } icon: {
                        Image(systemName: "lock.shield")
                            .foregroundStyle(ACTheme.accent)
                    }
                    .font(.footnote)
                    .foregroundStyle(ACTheme.secondaryText)
                }

                if let importError {
                    ACCard {
                        Label(importError, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.orange)
                    }
                }

                ForEach(importedRecords) { record in
                    if let localFileName = record.localFileName {
                        NavigationLink {
                            ScanProgressScreen(
                                record: record,
                                fileURL: AudiobookImportService.audioURL(fileName: localFileName)
                            )
                        } label: {
                            Label("Scan \(record.book.title)", systemImage: "waveform.badge.magnifyingglass")
                                .lineLimit(1)
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                        .tint(ACTheme.accent)
                        .foregroundStyle(.black)
                    }
                }
            }
            .padding()
        }
        .background(ACTheme.background)
        .navigationTitle("Import Audiobook")
        .fileImporter(
            isPresented: $showingImporter,
            allowedContentTypes: [.audio],
            allowsMultipleSelection: true
        ) { result in
            guard let sourceURLs = try? result.get(), !sourceURLs.isEmpty else { return }
            selectedFileName = sourceURLs.count == 1 ? sourceURLs[0].lastPathComponent : "\(sourceURLs.count) audiobooks selected"
            importedRecords = []
            importError = nil
            isImporting = true
            Task {
                do {
                    for sourceURL in sourceURLs {
                        let record = try await AudiobookImportService().importBook(from: sourceURL)
                        importedRecords.append(record)
                    }
                } catch {
                    importError = error.localizedDescription
                }
                isImporting = false
            }
        }
    }

    private func format(_ name: String) -> some View {
        VStack(spacing: 7) {
            Image(systemName: "doc.badge.plus")
                .font(.title2)
                .foregroundStyle(ACTheme.accent)
            Text(name).font(.caption)
        }
        .frame(maxWidth: .infinity)
    }
}

struct ScanProgressScreen: View {
    var record: LibraryBookRecord
    var fileURL: URL
    @StateObject private var model = CloudScanViewModel()

    private var book: MobileBook { record.book }

    private var steps: [ScanStep] {
        let order: [CloudScanViewModel.Phase] = [.reading, .fingerprinting, .searching, .uploading, .processing, .complete]
        let current = order.firstIndex(of: model.phase) ?? (model.phase == .queued ? 4 : 0)
        func step(_ index: Int, icon: String, title: String, activeTitle: String) -> ScanStep {
            ScanStep(
                icon: icon,
                title: title,
                status: index < current || model.phase == .complete ? "Completed" : (index == current ? activeTitle : "Pending"),
                isComplete: index < current || model.phase == .complete,
                isActive: index == current && model.phase != .complete && model.phase != .failed
            )
        }
        return [
            step(0, icon: "doc.text.magnifyingglass", title: "Reading audiobook", activeTitle: "Reading…"),
            step(1, icon: "lock.doc", title: "Fingerprinting file", activeTitle: "Fingerprinting…"),
            step(2, icon: "books.vertical", title: "Searching scan library", activeTitle: "Searching…"),
            step(3, icon: "arrow.up.circle", title: "Private upload", activeTitle: "Uploading…"),
            step(4, icon: "waveform.badge.magnifyingglass", title: "Analyzing content", activeTitle: model.phase == .queued ? "Waiting securely…" : "Analyzing…"),
            step(5, icon: "checkmark.shield", title: "Filter scan ready", activeTitle: "Finishing…")
        ]
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 10) {
                ForEach(steps) { step in
                    HStack(spacing: 16) {
                        Image(systemName: step.icon)
                            .frame(width: 44, height: 44)
                            .background(ACTheme.panel)
                            .clipShape(Circle())
                            .overlay { Circle().stroke(ACTheme.border) }

                        VStack(alignment: .leading) {
                            Text(step.title)
                            Text(step.status)
                                .font(.caption)
                                .foregroundStyle(step.isActive ? ACTheme.accent : ACTheme.secondaryText)
                        }
                        Spacer()
                        if step.isComplete {
                            Image(systemName: "checkmark.circle.fill")
                                .foregroundStyle(ACTheme.accent)
                        } else if step.isActive {
                            ProgressView().tint(ACTheme.accent)
                        }
                    }
                    .padding(.vertical, 8)
                }

                ACCard {
                    HStack(spacing: 14) {
                        BookCover(title: book.title, compact: true)
                            .frame(width: 62, height: 86)
                        VStack(alignment: .leading) {
                            Text(book.title).font(.headline)
                            Text(book.edition)
                                .font(.caption)
                                .foregroundStyle(ACTheme.secondaryText)
                        }
                        Spacer()
                    }
                }
                .padding(.top, 24)

                if let result = model.result {
                    ACCard {
                        Label(
                            "Ready with \(result.events.count) filter events",
                            systemImage: "checkmark.shield.fill"
                        )
                        .foregroundStyle(ACTheme.accent)
                    }
                }

                if let error = model.errorMessage {
                    ACCard {
                        VStack(alignment: .leading, spacing: 12) {
                            Label("Scan paused", systemImage: "exclamationmark.triangle")
                                .font(.headline)
                            Text(error)
                                .font(.subheadline)
                                .foregroundStyle(ACTheme.secondaryText)
                            Button("Try Again") {
                                Task { await model.retry(fileURL: fileURL, record: record) }
                            }
                            .buttonStyle(.borderedProminent)
                            .tint(ACTheme.accent)
                            .foregroundStyle(.black)
                        }
                    }
                }
            }
            .padding()
        }
        .background(ACTheme.background)
        .navigationTitle("Analyzing Audiobook")
        .navigationBarTitleDisplayMode(.inline)
        .task { await model.start(fileURL: fileURL, record: record) }
    }
}
