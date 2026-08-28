import Foundation

/// A half-open UTF-16 character range in the flat EPUB text that a filter covers.
struct ReaderMask: Equatable {
    let start: Int
    let end: Int
}

/// One paragraph prepared for display, with filtered text physically removed.
struct ReaderDisplayParagraph: Equatable {
    let paragraph: ReaderParagraph
    /// What is actually rendered. Filtered characters are absent, not styled over.
    let displayText: String
    /// How many separate filtered passages were removed from this paragraph.
    let removedPassages: Int

    var hasRemovedText: Bool { removedPassages > 0 }
}

/// Marker left where text was removed, so a sentence that suddenly changes direction
/// reads as a deliberate edit rather than a rendering bug.
let readerRemovalMarker = "…"

extension Array where Element == ReaderMask {
    /// Merges overlapping and touching ranges so removal never double-counts.
    func merged() -> [ReaderMask] {
        sorted { $0.start < $1.start }.reduce(into: [ReaderMask]()) { result, next in
            if let previous = result.last, next.start <= previous.end {
                result[result.count - 1] = ReaderMask(
                    start: previous.start,
                    end: Swift.max(previous.end, next.end)
                )
            } else {
                result.append(next)
            }
        }
    }
}

/// Builds display text for every paragraph with filtered passages **removed**.
///
/// Removal rather than styling is deliberate, and it is a privacy property rather than a
/// cosmetic one. Painting over filtered ranges leaves the characters present in the view
/// hierarchy, so VoiceOver, the accessibility tree, or any text-extraction path would
/// surface exactly the content the audio path skips.
///
/// `masks` must already be merged. Each paragraph keeps its original `ReaderParagraph` so
/// audio-follow can still map display back to source offsets.
func readerDisplayParagraphs(
    _ paragraphs: [ReaderParagraph],
    masks: [ReaderMask]
) -> [ReaderDisplayParagraph] {
    if masks.isEmpty {
        return paragraphs.map {
            ReaderDisplayParagraph(paragraph: $0, displayText: $0.text, removedPassages: 0)
        }
    }
    return paragraphs.map { maskParagraph($0, masks: masks) }
}

private func maskParagraph(_ paragraph: ReaderParagraph, masks: [ReaderMask]) -> ReaderDisplayParagraph {
    var maskIndex = firstMaskEndingAfter(masks, paragraph.startCharacter)
    if maskIndex >= masks.count || masks[maskIndex].start >= paragraph.endCharacter {
        return ReaderDisplayParagraph(
            paragraph: paragraph, displayText: paragraph.text, removedPassages: 0
        )
    }

    // Work in UTF-16 units so mask offsets line up with the server's coordinate space.
    let units = Array(paragraph.text.utf16)
    var output: [UInt16] = []
    output.reserveCapacity(units.count)
    let marker = Array(readerRemovalMarker.utf16)
    var cursor = paragraph.startCharacter
    var removed = 0

    while maskIndex < masks.count, masks[maskIndex].start < paragraph.endCharacter {
        let mask = masks[maskIndex]
        let overlapStart = max(mask.start, paragraph.startCharacter)
        let overlapEnd = min(mask.end, paragraph.endCharacter)
        if overlapEnd > overlapStart {
            if overlapStart > cursor {
                let from = cursor - paragraph.startCharacter
                let to = overlapStart - paragraph.startCharacter
                if from >= 0, to <= units.count, from < to { output.append(contentsOf: units[from..<to]) }
            }
            output.append(contentsOf: marker)
            removed += 1
            cursor = max(cursor, overlapEnd)
        }
        maskIndex += 1
    }
    if cursor < paragraph.endCharacter {
        let from = cursor - paragraph.startCharacter
        let to = paragraph.endCharacter - paragraph.startCharacter
        if from >= 0, to <= units.count, from < to { output.append(contentsOf: units[from..<to]) }
    }

    // Removing mid-sentence text can strand doubled spaces around the marker.
    let display = String(decoding: output, as: UTF16.self)
        .replacingOccurrences(of: #"\s{2,}"#, with: " ", options: .regularExpression)
        .trimmingCharacters(in: .whitespacesAndNewlines)
    return ReaderDisplayParagraph(
        paragraph: paragraph, displayText: display, removedPassages: removed
    )
}

/// Binary search for the first mask that could overlap `character`.
private func firstMaskEndingAfter(_ masks: [ReaderMask], _ character: Int) -> Int {
    var low = 0
    var high = masks.count
    while low < high {
        let middle = (low + high) / 2
        if masks[middle].end <= character { low = middle + 1 } else { high = middle }
    }
    return low
}
