import AVFoundation
import Foundation
import UIKit

enum AudiobookImportError: LocalizedError {
    case inaccessibleFile
    case copyFailed
    case insufficientStorage
    case aaxRequiresAuthorizedConversion
    case unsupportedAudio

    var errorDescription: String? {
        switch self {
        case .inaccessibleFile: "AudioChoice could not access that file. Try selecting it again."
        case .copyFailed: "AudioChoice could not save the audiobook on this iPhone. Check available storage."
        case .insufficientStorage: "This iPhone does not have enough free space to save that audiobook safely."
        case .aaxRequiresAuthorizedConversion: "AAX files must be converted with an authorized converter before importing on iPhone. Select the resulting M4B file."
        case .unsupportedAudio: "This audiobook file cannot be played by iPhone. Try an MP3, M4A, or non-protected M4B file."
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

        // iOS accepts every normal audiobook file; it does not silently whitelist
        // titles. AAX is a proprietary Audible container, so only an authorized
        // conversion to M4B is supported here.
        if sourceURL.pathExtension.caseInsensitiveCompare("aax") == .orderedSame {
            throw AudiobookImportError.aaxRequiresAuthorizedConversion
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
            let validationAsset = AVURLAsset(url: destination)
            let audioTracks = (try? await validationAsset.loadTracks(withMediaType: .audio)) ?? []
            let validatedDuration = (try? await validationAsset.load(.duration).seconds) ?? 0
            // AVAsset.isPlayable can report false for otherwise valid long-form MP3s
            // while their audio track is still readable by AVPlayer. Validate the
            // media itself so converted audiobooks are not rejected prematurely.
            guard !audioTracks.isEmpty, validatedDuration.isFinite, validatedDuration > 0 else {
                throw AudiobookImportError.unsupportedAudio
            }
            let metadata = await metadata(for: destination, fallback: sourceURL.deletingPathExtension().lastPathComponent)
            let artworkName = try saveArtwork(metadata.artwork, id: id)
            let record = LibraryBookRecord(
                id: id,
                book: MobileBook(
                    id: id,
                    title: AudiobookTitleFormatter.format(
                        metadata.title,
                        editionType: fingerprint.editionType,
                        partNumber: fingerprint.partNumber,
                        totalParts: fingerprint.totalParts
                    ),
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

    func recoverArtwork(for record: LibraryBookRecord) async throws -> LibraryBookRecord {
        guard record.artworkFileName == nil, let localFileName = record.localFileName else { return record }
        let audioURL = Self.audioURL(fileName: localFileName)
        guard FileManager.default.fileExists(atPath: audioURL.path) else { return record }
        let extracted = await metadata(for: audioURL, fallback: record.book.title)
        guard let artworkName = try saveArtwork(extracted.artwork, id: record.id) else { return record }
        var updated = record
        updated.artworkFileName = artworkName
        AudiobookLibraryStore.update(updated)
        return updated
    }

    private func metadata(for url: URL, fallback: String) async -> (title: String, author: String, duration: Double, artwork: Data?, chapters: [AudiobookChapter]) {
        let asset = AVURLAsset(url: url)
        let duration = (try? await asset.load(.duration).seconds) ?? 0
        let commonItems = (try? await asset.load(.commonMetadata)) ?? []
        let formatItems = (try? await asset.load(.metadata)) ?? []
        let items = commonItems + formatItems
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
        // Many M4A/M4B audiobook tools store the cover as a one-frame MJPEG
        // attached-picture track instead of AVMetadataCommonKeyArtwork.
        if artwork == nil { artwork = await attachedArtwork(from: asset) }
        if artwork == nil { artwork = embeddedImageBytes(from: url) }
        let chapters = await chapters(for: asset, totalDuration: duration)
        return (title?.trimmingCharacters(in: .whitespacesAndNewlines).nilIfEmpty ?? fallback,
                author?.trimmingCharacters(in: .whitespacesAndNewlines).nilIfEmpty ?? "Unknown Author",
                duration.isFinite ? duration : 0,
                artwork,
                chapters)
    }

    private func attachedArtwork(from asset: AVAsset) async -> Data? {
        guard let tracks = try? await asset.loadTracks(withMediaType: .video) else { return nil }
        for track in tracks {
            guard let reader = try? AVAssetReader(asset: asset) else { continue }
            let output = AVAssetReaderTrackOutput(
                track: track,
                outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
            )
            guard reader.canAdd(output) else { continue }
            reader.add(output)
            guard reader.startReading(),
                  let sample = output.copyNextSampleBuffer(),
                  let buffer = CMSampleBufferGetImageBuffer(sample) else { continue }
            let image = CIImage(cvPixelBuffer: buffer)
            guard let cgImage = CIContext().createCGImage(image, from: image.extent) else { continue }
            return UIImage(cgImage: cgImage).jpegData(compressionQuality: 0.92)
        }
        return nil
    }

    private func embeddedImageBytes(from url: URL) -> Data? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        let fileSize = (try? handle.seekToEnd()) ?? 0
        let regions: [(UInt64, Int)] = [
            (0, Int(min(fileSize, 8 * 1024 * 1024))),
            (fileSize > 32 * 1024 * 1024 ? fileSize - 32 * 1024 * 1024 : 0,
             Int(min(fileSize, 32 * 1024 * 1024)))
        ]
        for (offset, count) in regions {
            try? handle.seek(toOffset: offset)
            guard let data = try? handle.read(upToCount: count) else { continue }
            if let jpeg = firstValidImage(
                in: data,
                start: Data([0xFF, 0xD8, 0xFF]),
                end: Data([0xFF, 0xD9])
            ) { return jpeg }
            if let png = firstValidImage(
                in: data,
                start: Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
                end: Data([0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82])
            ) { return png }
        }
        return nil
    }

    private func firstValidImage(in data: Data, start: Data, end: Data) -> Data? {
        var lowerBound = data.startIndex
        while lowerBound < data.endIndex,
              let startRange = data.range(of: start, in: lowerBound..<data.endIndex) {
            if let endRange = data.range(of: end, in: startRange.lowerBound..<data.endIndex) {
                let candidate = data.subdata(in: startRange.lowerBound..<endRange.upperBound)
                if UIImage(data: candidate)?.size.width ?? 0 > 0 { return candidate }
            }
            lowerBound = data.index(after: startRange.lowerBound)
        }
        return nil
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
