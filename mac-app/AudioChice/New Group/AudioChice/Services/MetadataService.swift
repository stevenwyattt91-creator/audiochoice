import AVFoundation
import Foundation

final class MetadataService {

    func extractMetadata(
        from url: URL
    ) async throws -> BookMetadata {

        let asset = AVURLAsset(url: url)

        let duration = try await asset.load(.duration)
        let metadata = try await asset.load(.commonMetadata)

        let chapterGroups =
            try await asset.loadChapterMetadataGroups(
                bestMatchingPreferredLanguages:
                    Locale.preferredLanguages
            )

        let title = await stringValue(
            for: .commonIdentifierTitle,
            in: metadata
        )

        let authorMetadata = await stringValue(
            for: .commonIdentifierAuthor,
            in: metadata
        )

        let artistMetadata = await stringValue(
            for: .commonIdentifierArtist,
            in: metadata
        )

        let author: String?

        if let authorMetadata {
            author = authorMetadata
        } else {
            author = artistMetadata
        }

        let artworkData = await dataValue(
            for: .commonIdentifierArtwork,
            in: metadata
        )

        let durationSeconds: TimeInterval?

        if duration.seconds.isFinite &&
            duration.seconds > 0 {

            durationSeconds = duration.seconds

        } else {
            durationSeconds = nil
        }

        let chapters = await extractChapters(
            from: chapterGroups
        )

        return BookMetadata(
            title: cleaned(title),
            author: cleaned(author),
            narrator: nil,
            duration: durationSeconds,
            coverArtData: artworkData,
            chapters: chapters
        )
    }

    private func extractChapters(
        from groups: [AVTimedMetadataGroup]
    ) async -> [Chapter] {

        var chapters: [Chapter] = []

        for (index, group) in groups.enumerated() {

            let startTime =
                group.timeRange.start.seconds

            guard startTime.isFinite,
                  startTime >= 0
            else {
                continue
            }

            let extractedTitle =
                await chapterTitle(
                    from: group.items
                )

            let title =
                cleaned(extractedTitle)
                ?? "Chapter \(index + 1)"

            chapters.append(
                Chapter(
                    title: title,
                    startTime: startTime
                )
            )
        }

        return chapters.sorted {
            $0.startTime < $1.startTime
        }
    }

    private func chapterTitle(
        from metadata: [AVMetadataItem]
    ) async -> String? {

        if let title = await stringValue(
            for: .commonIdentifierTitle,
            in: metadata
        ) {
            return title
        }

        for item in metadata {
            do {
                if let value =
                    try await item.load(.stringValue) {

                    let trimmedValue =
                        value.trimmingCharacters(
                            in: .whitespacesAndNewlines
                        )

                    if !trimmedValue.isEmpty {
                        return trimmedValue
                    }
                }
            } catch {
                continue
            }
        }

        return nil
    }

    private func stringValue(
        for identifier: AVMetadataIdentifier,
        in metadata: [AVMetadataItem]
    ) async -> String? {

        let matchingItems =
            AVMetadataItem.metadataItems(
                from: metadata,
                filteredByIdentifier: identifier
            )

        for item in matchingItems {
            do {
                if let value =
                    try await item.load(.stringValue) {

                    let trimmedValue =
                        value.trimmingCharacters(
                            in: .whitespacesAndNewlines
                        )

                    if !trimmedValue.isEmpty {
                        return trimmedValue
                    }
                }
            } catch {
                continue
            }
        }

        return nil
    }

    private func dataValue(
        for identifier: AVMetadataIdentifier,
        in metadata: [AVMetadataItem]
    ) async -> Data? {

        let matchingItems =
            AVMetadataItem.metadataItems(
                from: metadata,
                filteredByIdentifier: identifier
            )

        for item in matchingItems {
            do {
                if let value =
                    try await item.load(.dataValue) {

                    return value
                }
            } catch {
                continue
            }
        }

        return nil
    }

    private func cleaned(
        _ value: String?
    ) -> String? {

        guard let value else {
            return nil
        }

        let trimmedValue =
            value.trimmingCharacters(
                in: .whitespacesAndNewlines
            )

        return trimmedValue.isEmpty
            ? nil
            : trimmedValue
    }
}
