import Foundation

/// One rendered block of reading text, carrying its position in the flat EPUB string
/// produced by `EpubTextReader.read`.
///
/// `startCharacter` and `endCharacter` are **UTF-16 code unit** indices into that exact
/// string, which is also the coordinate space the server's reader alignment returns.
/// This is not a detail that can be glossed over: the backend indexes .NET strings and
/// the Android client indexes Kotlin strings, both of which are UTF-16, whereas Swift's
/// `String` is a collection of grapheme clusters. Using Swift's native indices here would
/// silently misalign the reader on any book containing a character outside the basic
/// multilingual plane.
struct ReaderParagraph: Equatable {
    let text: String
    /// Inclusive UTF-16 index into the flat EPUB text.
    let startCharacter: Int
    /// Exclusive UTF-16 index into the flat EPUB text.
    let endCharacter: Int
}

/// Splits the flat EPUB text into paragraphs for display without altering it.
///
/// `EpubTextReader` output must stay stable because every cached alignment indexes into
/// it, so this parser only ever *indexes* the string. It never rewrites, re-joins or
/// normalises it.
enum ReaderParagraphParser {
    /// `EpubTextReader` emits one newline per block element and collapses runs of three
    /// or more, so a newline run is the paragraph separator.
    static func parse(_ epubText: String) -> [ReaderParagraph] {
        let units = Array(epubText.utf16)
        var paragraphs: [ReaderParagraph] = []
        let length = units.count
        var index = 0
        let newline = UInt16(UnicodeScalar("\n").value)

        while index < length {
            if units[index] == newline {
                index += 1
                continue
            }
            var start = index
            while index < length, units[index] != newline { index += 1 }
            var end = index
            // Tighten past surrounding spaces so the recorded offsets bound the returned
            // text exactly rather than approximately.
            while start < end, isWhitespace(units[start]) { start += 1 }
            while end > start, isWhitespace(units[end - 1]) { end -= 1 }
            if end > start {
                paragraphs.append(ReaderParagraph(
                    text: String(decoding: units[start..<end], as: UTF16.self),
                    startCharacter: start,
                    endCharacter: end
                ))
            }
        }
        return paragraphs
    }

    /// Matches the whitespace set the Kotlin parser trims, without building a Character
    /// for every code unit.
    static func isWhitespace(_ unit: UInt16) -> Bool {
        switch unit {
        case 0x20, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x85, 0xA0, 0x2028, 0x2029, 0x3000:
            return true
        case 0x2000...0x200A:
            return true
        default:
            return false
        }
    }
}

extension Array where Element == ReaderParagraph {
    /// Index of the paragraph containing `character`, or the nearest preceding one, or nil
    /// when empty.
    ///
    /// Paragraphs are ordered and non-overlapping, so this binary searches rather than
    /// scanning: audio-follow calls it on every position tick.
    func indexOfCharacter(_ character: Int) -> Int? {
        if isEmpty { return nil }
        var low = 0
        var high = count - 1
        var best = 0
        while low <= high {
            let middle = (low + high) / 2
            let paragraph = self[middle]
            if character < paragraph.startCharacter {
                high = middle - 1
            } else if character >= paragraph.endCharacter {
                best = middle
                low = middle + 1
            } else {
                return middle
            }
        }
        return best
    }
}
