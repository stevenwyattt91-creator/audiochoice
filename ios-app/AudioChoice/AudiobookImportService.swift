import AVFoundation
import Foundation

enum AudiobookImportError: LocalizedError {
    case inaccessibleFile
    case copyFailed
    case insufficientStorage

    var errorDescription: String? {
        switch self {
        case .inaccessibleFile: "AudioChoice could not access that file. Try selecting it again."
        case .copyFailed: "AudioChoice could not save the audiobook on this iPhone. Check available storage."
        case .insufficientStorage: "This iPhone does not have enough free space to save that audiobook safely."
        }
    }
}

struct AudiobookImportService {
    static let libraryFolder = "Audiobooks"
    static let artworkFolder = "Artwork"

    func importBook(from sourceURL: URL) async throws -> LibraryBookRecord {
        let hasAccess = sourceURL.startAccessingSecurityScopedResource()
        defer { if hasAccess { sourceURL.stopAccessingSecurityScopedResource() } }

        guard FileManager.default.isReadableFile(atPath: sourceURL.path) else {
            throw AudiobookImportError.inaccessibleFile
        }

        let fingerprint = try await AudiobookFingerprintService().fingerprint(fileURL: sourceURL)
        if let existing = AudiobookLibraryStore.record(matching: fingerprint),
           existing.localFileName != nil {
            return existing
        }

        let id = UUID()
        let fileName = "\(id.uuidString).\(sourceURL.pathExtension.lowercased())"
        let destination = Self.audioURL(fileName: fileName)
        try Self.ensureDirectories()
        let available = try? Self.applicationSupportDirectory.resourceValues(
            forKeys: [.volumeAvailableCapacityForImportantUsageKey]
        ).volumeAvailableCapacityForImportantUsage
        if let available, available < fingerprint.fileSize + 100_000_000 {
            throw AudiobookImportError.insufficientStorage
        }
        do {
            try FileManager.default.copyItem(at: sourceURL, to: destination)
            var values = URLResourceValues()
            values.isExcludedFromBackup = true
            var mutableDestination = destination
            try mutableDestination.setResourceValues(values)
        } catch {
            throw AudiobookImportError.copyFailed
        }

        do {
            let metadata = await metadata(for: destination, fallback: sourceURL.deletingPathExtension().lastPathComponent)
            let artworkName = try saveArtwork(metadata.artwork, id: id)
            let record = LibraryBookRecord(
                id: id,
                book: MobileBook(
                    id: id,
                    title: metadata.title,
                    author: metadata.author,
                    progress: 0,
                    timeRemaining: metadata.duration > 0 ? Self.durationText(metadata.duration) : "",
                    runtime: Self.durationText(metadata.duration),
                    chapters: metadata.chapters.count,
                    edition: sourceURL.pathExtension.uppercased()
                ),
                localFileName: fileName,
                artworkFileName: artworkName,
                fileSize: fingerprint.fileSize,
                importedAt: Date(),
                fingerprint: fingerprint,
                scanResult: nil,
                chapterMarkers: metadata.chapters,
                pendingScanID: nil,
                scanState: nil
            )
            AudiobookLibraryStore.upsert(record)
            return record
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
    }

    static func audioURL(fileName: String) -> URL {
        applicationSupportDirectory.appendingPathComponent(libraryFolder, isDirectory: true)
            .appendingPathComponent(fileName)
    }

    static func artworkURL(fileName: String) -> URL {
        applicationSupportDirectory.appendingPathComponent(artworkFolder, isDirectory: true)
            .appendingPathComponent(fileName)
    }

    private func metadata(for url: URL, fallback: String) async -> (title: String, author: String, duration: Double, artwork: Data?, chapters: [AudiobookChapter]) {
        let asset = AVURLAsset(url: url)
        let duration = (try? await asset.load(.duration).seconds) ?? 0
        let items = (try? await asset.load(.commonMetadata)) ?? []
        var title: String?
        var author: String?
        var artwork: Data?
        for item in items {
            switch item.commonKey {
            case .commonKeyTitle:
                title = try? await item.load(.stringValue)
            case .commonKeyArtist, .commonKeyAuthor:
                author = try? await item.load(.stringValue)
            case .commonKeyArtwork:
                artwork = try? await item.load(.dataValue)
            default:
                break
            }
        }
        let chapters = await chapters(for: asset, totalDuration: duration)
        return (title?.trimmingCharacters(in: .whitespacesAndNewlines).nilIfEmpty ?? fallback,
                author?.trimmingCharacters(in: .whitespacesAndNewlines).nilIfEmpty ?? "Unknown Author",
                duration.isFinite ? duration : 0,
                artwork,
                chapters)
    }

    private func chapters(for asset: AVAsset, totalDuration: Double) async -> [AudiobookChapter] {
        guard let locales = try? await asset.load(.availableChapterLocales),
              let locale = locales.first,
              let groups = try? await asset.loadChapterMetadataGroups(
                withTitleLocale: locale,
                containingItemsWithCommonKeys: [.commonKeyTitle]
              ) else {
            return totalDuration > 0 ? [AudiobookChapter(id: UUID(), title: "Full Audiobook", startTime: 0, duration: totalDuration)] : []
        }
        var result: [AudiobookChapter] = []
        for (index, group) in groups.enumerated() {
            let titleItem = group.items.first { $0.commonKey == .commonKeyTitle }
            let title = (try? await titleItem?.load(.stringValue)) ?? "Chapter \(index + 1)"
            result.append(AudiobookChapter(
                id: UUID(),
                title: title,
                startTime: group.timeRange.start.seconds,
                duration: group.timeRange.duration.seconds
            ))
        }
        return result
    }

    private func saveArtwork(_ data: Data?, id: UUID) throws -> String? {
        guard let data, !data.isEmpty else { return nil }
        let name = "\(id.uuidString).artwork"
        try data.write(to: Self.artworkURL(fileName: name), options: .atomic)
        return name
    }

    private static func ensureDirectories() throws {
        let audioDirectory = applicationSupportDirectory.appendingPathComponent(libraryFolder)
        try FileManager.default.createDirectory(at: audioDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: applicationSupportDirectory.appendingPathComponent(artworkFolder), withIntermediateDirectories: true)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        var mutableDirectory = audioDirectory
        try mutableDirectory.setResourceValues(values)
    }

    private static var applicationSupportDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
    }

    private static func durationText(_ seconds: Double) -> String {
        guard seconds > 0 else { return "" }
        let totalMinutes = Int(seconds) / 60
        return "\(totalMinutes / 60)h \(totalMinutes % 60)m"
    }
}

private extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
