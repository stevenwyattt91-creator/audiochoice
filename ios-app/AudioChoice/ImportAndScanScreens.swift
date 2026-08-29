import AVFoundation
import SwiftUI
import UIKit
import UniformTypeIdentifiers

private extension UTType {
    static let audiobookAAX = UTType(filenameExtension: "aax") ?? .data
    static let audiobookM4B = UTType(filenameExtension: "m4b") ?? .audio
}

struct ImportScreen: View {
    @State private var showingImporter = false
    @State private var selectedFileName: String?
    @State private var importedRecords: [LibraryBookRecord] = []
    @State private var isImporting = false
    @State private var importError: String?
    @State private var showingTransferScanner = false
    /// The book whose scan should open without another tap, once a single import finishes.
    ///
    /// Held as an identifier rather than the record itself, because navigation needs a
    /// hashable value and the record is a stored model that has no reason to become one.
    @State private var scanTargetID: UUID?
    @State private var isReceivingTransfer = false
    @ObservedObject private var transferCoordinator = CompanionTransferCoordinator.shared

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

                    Button("Scan transfer QR", systemImage: "qrcode.viewfinder") {
                        showingTransferScanner = true
                    }
                    .buttonStyle(.bordered)
                    .tint(ACTheme.accent)
                    .disabled(isImporting || isReceivingTransfer)

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
                        format("AAX*")
                        format("M4A")
                    }
                }

                if isReceivingTransfer {
                    ProgressView("Receiving private transfer…")
                        .tint(ACTheme.accent)
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
            allowedContentTypes: [.audio, .audiobookM4B, .audiobookAAX],
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
                // Choosing a file is the listener saying what they want scanned, so the scan
                // starts on its own. Only for a single book: several at once would mean
                // picking one to open and leaving the rest looking ignored, so those keep
                // their buttons.
                if importedRecords.count == 1, importError == nil,
                   importedRecords[0].localFileName != nil {
                    scanTargetID = importedRecords[0].id
                }
            }
        }
        .navigationDestination(item: $scanTargetID) { id in
            if let record = importedRecords.first(where: { $0.id == id }),
               let localFileName = record.localFileName {
                ScanProgressScreen(
                    record: record,
                    fileURL: AudiobookImportService.audioURL(fileName: localFileName)
                )
            }
        }
        .sheet(isPresented: $showingTransferScanner) {
            QRScannerView { url in
                showingTransferScanner = false
                transferCoordinator.receive(url)
            }
            .ignoresSafeArea()
        }
        .onChange(of: transferCoordinator.pendingURL) { _, url in
            guard let url else { return }
            Task { await receiveTransfer(url) }
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

    private func receiveTransfer(_ url: URL) async {
        guard !isReceivingTransfer else { return }
        isReceivingTransfer = true
        defer {
            isReceivingTransfer = false
            transferCoordinator.clear()
        }
        do {
            guard let id = UUID(uuidString: url.pathComponents.dropFirst().first ?? ""),
                  let code = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "code" })?.value,
                  !code.isEmpty else { throw CloudClientError.invalidResponse }
            let client = try CloudScanClient.configured()
            let claim = try await client.claimCompanionTransfer(id: id, code: code)
            let temporary = FileManager.default.temporaryDirectory.appendingPathComponent(claim.fileName)
            defer { try? FileManager.default.removeItem(at: temporary) }
            try await client.downloadCompanionTransfer(claim, to: temporary)
            let record = try await AudiobookImportService().importBook(from: temporary)
            try await client.markCompanionTransferReceived(id: claim.transferID)
            importedRecords.append(record)
            selectedFileName = claim.fileName
            importError = nil
        } catch {
            importError = error.localizedDescription
        }
    }
}

private struct QRScannerView: UIViewControllerRepresentable {
    let onCode: (URL) -> Void
    func makeUIViewController(context: Context) -> QRScannerController {
        QRScannerController(onCode: onCode)
    }
    func updateUIViewController(_ controller: QRScannerController, context: Context) {}
}

private final class QRScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    private let onCode: (URL) -> Void
    private let session = AVCaptureSession()
    private var preview: AVCaptureVideoPreviewLayer?
    private var delivered = false

    init(onCode: @escaping (URL) -> Void) {
        self.onCode = onCode
        super.init(nibName: nil, bundle: nil)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        guard let device = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device),
              session.canAddInput(input) else { return }
        session.addInput(input)
        let output = AVCaptureMetadataOutput()
        guard session.canAddOutput(output) else { return }
        session.addOutput(output)
        output.setMetadataObjectsDelegate(self, queue: .main)
        output.metadataObjectTypes = [.qr]
        let layer = AVCaptureVideoPreviewLayer(session: session)
        layer.videoGravity = .resizeAspectFill
        view.layer.addSublayer(layer)
        preview = layer
        session.startRunning()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        preview?.frame = view.bounds
    }

    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
        guard !delivered,
              let value = (metadataObjects.first as? AVMetadataMachineReadableCodeObject)?.stringValue,
              let url = URL(string: value) else { return }
        delivered = true
        session.stopRunning()
        onCode(url)
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
            step(3, icon: "arrow.up.circle", title: "Private upload", activeTitle: "Uploading — \(model.uploadProgress)%"),
            step(4, icon: "waveform.badge.magnifyingglass", title: "Analyzing content", activeTitle: analysisStatus),
            step(5, icon: "checkmark.shield", title: "Filter scan ready", activeTitle: "Finishing…")
        ]
    }

    private var analysisStatus: String {
        let chunks = model.totalChunks > 0 ? " — \(model.completedChunks)/\(model.totalChunks) chunks" : ""
        return "\(model.analysisStage) — \(model.analysisProgress)%\(chunks)"
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
                            if step.isActive && model.phase == .uploading {
                                ProgressView(value: Double(model.uploadProgress), total: 100)
                                    .tint(ACTheme.accent)
                                    .padding(.top, 4)
                            } else if step.isActive && (model.phase == .queued || model.phase == .processing) {
                                ProgressView(value: Double(model.analysisProgress), total: 100)
                                    .tint(ACTheme.accent)
                                    .padding(.top, 4)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        Spacer()
                        if step.isComplete {
                            Image(systemName: "checkmark.circle.fill")
                                .foregroundStyle(ACTheme.accent)
                        } else if step.isActive && model.phase != .uploading && model.phase != .processing {
                            ProgressView().tint(ACTheme.accent)
                        }
                    }
                    .padding(.vertical, 8)
                }

                ACCard {
                    HStack(spacing: 14) {
                        BookCover(title: book.title, compact: true, artworkFileName: record.artworkFileName)
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

                if let status = model.connectionStatus {
                    ACCard {
                        HStack(spacing: 12) {
                            if model.isReconnecting { ProgressView().tint(ACTheme.accent) }
                            else { Image(systemName: "checkmark.circle.fill").foregroundStyle(ACTheme.accent) }
                            Text(status).font(.subheadline.weight(.semibold))
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }

                if let result = model.result {
                    ACCard {
                        Label(
                            "Ready with \(result.events.count) filter events",
                            systemImage: "checkmark.shield.fill"
                        )
                        .foregroundStyle(ACTheme.accent)
                    }

                    Button {
                        NotificationCenter.default.post(name: .showAudioChoiceLibrary, object: nil)
                    } label: {
                        Label("Go to Library", systemImage: "books.vertical.fill")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(ACTheme.accent)
                    .foregroundStyle(.black)
                }

                if let error = model.errorMessage {
                    ACCard {
                        VStack(alignment: .leading, spacing: 12) {
                            Label(model.isReconnecting ? "Scan paused — reconnecting" : "Import failed", systemImage: "exclamationmark.triangle")
                                .font(.headline)
                            Text(error)
                                .font(.subheadline)
                                .foregroundStyle(ACTheme.secondaryText)
                            Text(model.isReconnecting
                                 ? "AudioChoice will retry the backend automatically. Keep this screen open; progress will resume when the connection returns."
                                 : "The incomplete audiobook was not added to your library. Select the file again to retry.")
                                .font(.caption)
                                .foregroundStyle(ACTheme.secondaryText)
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
