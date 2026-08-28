import Foundation

/// Identity-bearing metadata read out of an audiobook container.
///
/// Mirrors the Android client's AudioEditionTags field for field. A byte hash cannot
/// answer "which edition is this", because converting or re-tagging a file changes the
/// hash for identical audio. These are the cheap signals that survive that, and
/// `productIdentifier` is the only one that is definitive.
struct AudioEditionTags: Equatable {
    var title: String?
    var author: String?
    var albumTitle: String?
    var albumArtist: String?
    var narrator: String?
    var publisher: String?
    var copyright: String?
    var year: String?
    var seriesTitle: String?
    var seriesPart: Int?
    /// Audible's product identifier.
    var asin: String?
    var isbn: String?
    /// The publisher's synopsis, when the file carries one.
    ///
    /// Audiobooks routinely ship with the back-cover text in `ldes` or `©des`. It is the
    /// only description of the story available without asking an outside service, and it
    /// came with the file the listener already owns.
    var synopsis: String?

    /// A retail product identifier names one published edition, so it can be trusted
    /// outright. Everything else here needs corroboration from runtime or structure.
    var productIdentifier: String? { asin ?? isbn }

    var isEmpty: Bool { self == AudioEditionTags() }
}

/// Parses the iTunes-style `ilst` metadata list used by M4A/M4B/MP4/AAX audiobooks.
///
/// Deliberately not AVFoundation: it exposes a fixed set of common keys and does not
/// reliably surface the freeform `----` atoms where ASIN and ISBN are stored. This is
/// a direct port of the Android parser so both platforms derive the same signature
/// from the same file, which is what lets the server treat them as one edition.
enum Mp4TagParser {
    /// Well-known data types from the `data` atom's flag field.
    private static let typeUTF8: UInt32 = 1
    private static let typeUTF16: UInt32 = 2
    private static let typeSignedInt: UInt32 = 21
    private static let typeUnsignedInt: UInt32 = 22

    /// @param payload the raw contents of an `ilst` atom, header excluded.
    static func parseIlst(_ payload: [UInt8]) -> AudioEditionTags {
        var standard: [String: String] = [:]
        var freeform: [String: String] = [:]

        forEachAtom(payload, from: 0, to: payload.count) { type, bodyStart, bodyEnd in
            if type == "----" {
                if let entry = freeformEntry(payload, bodyStart, bodyEnd), freeform[entry.key] == nil {
                    freeform[entry.key] = entry.value
                }
            } else if let value = dataValue(payload, bodyStart, bodyEnd), standard[type] == nil {
                standard[type] = value
            }
        }

        func standardOf(_ keys: String...) -> String? { keys.lazy.compactMap { standard[$0] }.first }
        func freeformOf(_ keys: String...) -> String? { keys.lazy.compactMap { freeform[$0] }.first }

        var tags = AudioEditionTags()
        tags.title = standardOf("\u{00A9}nam")
        tags.author = standardOf("\u{00A9}ART") ?? freeformOf("author", "artist")
        tags.albumTitle = standardOf("\u{00A9}alb")
        tags.albumArtist = standardOf("aART")
        // Audible and most converters use a freeform tag; composer is the long-standing
        // fallback that tagging tools reach for.
        tags.narrator = freeformOf("narrator", "narrators")
            ?? standardOf("\u{00A9}nrt", "\u{00A9}wrt")
        tags.publisher = freeformOf("publisher", "label") ?? standardOf("\u{00A9}pub")
        tags.copyright = standardOf("cprt")
        tags.year = standardOf("\u{00A9}day").flatMap(year(in:))
        tags.seriesTitle = freeformOf("series", "series_name", "book_series", "show")
        tags.seriesPart = freeformOf(
            "series-part", "series_part", "seriespart", "book_series_index", "series_sequence"
        ).flatMap(seriesPart(in:))
        tags.asin = freeformOf("asin", "product_id").flatMap(identifier(in:))
        tags.isbn = freeformOf("isbn", "isbn13", "isbn_13").flatMap(identifier(in:))
        // Long description first: `ldes` holds the full synopsis where `©des` is often a
        // one-line summary, and a comment is the last resort because tagging tools put
        // encoder notes there.
        tags.synopsis = synopsis(
            in: standardOf("ldes", "\u{00A9}des", "desc", "\u{00A9}cmt")
                ?? freeformOf("description", "synopsis", "long_description")
        )
        return tags
    }

    /// The shortest text worth calling a synopsis.
    private static let minimumSynopsisLength = 40

    /// Rejects the encoder noise and single words that end up in description atoms.
    private static func synopsis(in value: String?) -> String? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              trimmed.count >= minimumSynopsisLength else { return nil }
        // Converters write their own name into comment and description fields. Showing
        // "Created by ..." where a listener expects the story is worse than showing nothing.
        let lowered = trimmed.lowercased()
        let toolNames = [
            "lame", "ffmpeg", "itunes", "audible", "chapter and verse", "mp3tag",
            "created by", "encoded by", "converted", "audiobookshelf", "libation"
        ]
        if toolNames.contains(where: { lowered.hasPrefix($0) }) { return nil }
        return trimmed
    }

    /// Smaller than this is not an image, whatever the atom claims.
    private static let minimumCoverBytes = 64
    /// Audiobook covers are large but not unbounded.
    private static let maximumCoverBytes = 6 * 1024 * 1024

    /// The embedded cover image, from the `covr` atom in the same metadata list.
    ///
    /// The import already falls back to scanning raw bytes for a JPEG or PNG signature,
    /// which works but can pick up any image the file happens to contain. Reading the atom
    /// names the cover exactly, so it is tried first and the scan stays as a last resort.
    ///
    /// - Parameter payload: the raw contents of an `ilst` atom, header excluded.
    static func coverArt(_ payload: [UInt8]) -> Data? {
        var result: Data?
        forEachAtom(payload, from: 0, to: payload.count) { type, bodyStart, bodyEnd in
            guard type == "covr", result == nil else { return }
            forEachAtom(payload, from: bodyStart, to: bodyEnd) { childType, dataStart, dataEnd in
                // A `data` atom is four bytes of version and type flags, four reserved
                // locale bytes, then the image itself.
                guard childType == "data", result == nil, dataStart + 8 <= dataEnd else { return }
                let imageStart = dataStart + 8
                let length = dataEnd - imageStart
                guard length >= minimumCoverBytes, length <= maximumCoverBytes else { return }
                result = Data(payload[imageStart..<dataEnd])
            }
        }
        return result
    }

    /// The runtime stated by a movie header (`mvhd`) atom, in seconds.
    ///
    /// The header carries a timescale and a duration counted in those units, so this is the
    /// container's own answer rather than an estimate.
    ///
    /// - Parameter payload: the raw contents of an `mvhd` atom, header excluded.
    static func durationSeconds(_ payload: [UInt8]) -> Double? {
        guard payload.count >= 4 else { return nil }
        // A full box: one version byte then three flag bytes. Version 1 widened the creation
        // and modification times to 64 bits, moving everything after them.
        let version = payload[0]
        let timescaleOffset = version == 1 ? 20 : 12
        let durationOffset = timescaleOffset + 4
        let durationBytes = version == 1 ? 8 : 4
        guard durationOffset + durationBytes <= payload.count else { return nil }

        let timescale = uint32(payload, timescaleOffset)
        guard timescale > 0 else { return nil }
        let duration: UInt64
        if version == 1 {
            var value: UInt64 = 0
            for index in 0..<8 { value = (value << 8) | UInt64(payload[durationOffset + index]) }
            duration = value
        } else {
            duration = UInt64(uint32(payload, durationOffset))
        }
        // Some converters leave this at zero. Reporting it would show the book as
        // instantaneous, which reads as a corrupt import rather than a missing field.
        guard duration > 0 else { return nil }
        let seconds = Double(duration) / Double(timescale)
        return seconds.isFinite && seconds > 0 ? seconds : nil
    }

    /// Dates arrive as bare years and as full ISO timestamps.
    private static func year(in value: String) -> String? {
        let digits = Array(value)
        for start in 0..<max(digits.count - 3, 0) {
            let candidate = String(digits[start..<(start + 4)])
            guard candidate.allSatisfy(\.isNumber), let number = Int(candidate) else { continue }
            if (1800...2199).contains(number) { return candidate }
        }
        return nil
    }

    /// Series indices are written as bare numbers but also as "Book 3" or "3 of 7".
    private static func seriesPart(in value: String) -> Int? {
        var digits = ""
        for character in value {
            if character.isNumber {
                digits.append(character)
                if digits.count == 3 { break }
            } else if !digits.isEmpty {
                break
            }
        }
        return Int(digits).flatMap { $0 > 0 ? $0 : nil }
    }

    /// Identifiers are compared for equality, so surrounding punctuation matters.
    private static func identifier(in value: String) -> String? {
        let cleaned = value.filter { $0.isLetter || $0.isNumber }.uppercased()
        return cleaned.isEmpty ? nil : cleaned
    }

    /// Walks the direct children of an atom payload.
    ///
    /// Every bound is checked because this parses untrusted files: a truncated or
    /// hostile container must yield fewer tags, never a crash or an out-of-range read.
    private static func forEachAtom(
        _ payload: [UInt8],
        from start: Int,
        to end: Int,
        _ action: (_ type: String, _ bodyStart: Int, _ bodyEnd: Int) -> Void
    ) {
        var offset = start
        while offset + 8 <= end {
            let declared = uint32(payload, offset)
            // Size 0 runs to the end of the parent; size 1 means a 64-bit size follows,
            // which does not occur inside a metadata list.
            let atomEnd: Int
            if declared == 0 {
                atomEnd = end
            } else if declared == 1 || declared < 8 {
                return
            } else {
                atomEnd = offset + Int(declared)
            }
            guard atomEnd <= end, atomEnd > offset else { return }
            action(atomType(payload, offset + 4), offset + 8, atomEnd)
            offset = atomEnd
        }
    }

    /// Reads the value out of a tag atom's child `data` atom.
    private static func dataValue(_ payload: [UInt8], _ start: Int, _ end: Int) -> String? {
        var result: String?
        forEachAtom(payload, from: start, to: end) { type, bodyStart, bodyEnd in
            // A `data` atom is four bytes of version and type flags, four reserved
            // locale bytes, then the value.
            guard type == "data", result == nil, bodyStart + 8 <= bodyEnd else { return }
            let wellKnownType = uint32(payload, bodyStart) & 0x00FF_FFFF
            result = decode(wellKnownType, payload, bodyStart + 8, bodyEnd)
        }
        return result
    }

    /// Reads a freeform (`----`) tag, which carries its own namespace and name rather
    /// than a four-character code. ASIN and ISBN live here.
    private static func freeformEntry(
        _ payload: [UInt8], _ start: Int, _ end: Int
    ) -> (key: String, value: String)? {
        var name: String?
        var value: String?
        forEachAtom(payload, from: start, to: end) { type, bodyStart, bodyEnd in
            switch type {
            // `mean` holds the namespace, e.g. com.apple.iTunes. Writers are
            // inconsistent about it, so the name alone is the key.
            case "name":
                if name == nil { name = fullBoxText(payload, bodyStart, bodyEnd) }
            case "data":
                guard value == nil, bodyStart + 8 <= bodyEnd else { return }
                let wellKnownType = uint32(payload, bodyStart) & 0x00FF_FFFF
                value = decode(wellKnownType, payload, bodyStart + 8, bodyEnd)
            default:
                break
            }
        }
        guard let key = name?.trimmed()?.lowercased(), let text = value?.trimmed() else { return nil }
        return (key, text)
    }

    /// `mean` and `name` are full boxes: four bytes of version and flags, then text.
    private static func fullBoxText(_ payload: [UInt8], _ start: Int, _ end: Int) -> String? {
        guard start + 4 <= end else { return nil }
        return String(bytes: payload[(start + 4)..<end], encoding: .utf8)?.trimmed()
    }

    private static func decode(
        _ wellKnownType: UInt32, _ payload: [UInt8], _ start: Int, _ end: Int
    ) -> String? {
        guard end > start else { return nil }
        let bytes = payload[start..<end]
        switch wellKnownType {
        case typeUTF8:
            return String(bytes: bytes, encoding: .utf8)?.trimmed()
        case typeUTF16:
            return String(bytes: bytes, encoding: .utf16BigEndian)?.trimmed()
        case typeSignedInt, typeUnsignedInt:
            guard bytes.count <= 8 else { return nil }
            return String(bytes.reduce(UInt64(0)) { ($0 << 8) | UInt64($1) })
        default:
            // Artwork and other binary payloads carry no identity information.
            return nil
        }
    }

    private static func uint32(_ payload: [UInt8], _ offset: Int) -> UInt32 {
        (UInt32(payload[offset]) << 24)
            | (UInt32(payload[offset + 1]) << 16)
            | (UInt32(payload[offset + 2]) << 8)
            | UInt32(payload[offset + 3])
    }

    /// Atom codes are four bytes, and the common ones begin with the 0xA9 copyright
    /// byte. ISO-8859-1 maps that byte to "©" so the codes read as they do in the spec.
    private static func atomType(_ payload: [UInt8], _ offset: Int) -> String {
        String(bytes: payload[offset..<(offset + 4)], encoding: .isoLatin1) ?? ""
    }
}

/// What the container itself can tell us, independent of AVFoundation.
struct ContainerMetadata: Equatable {
    var tags = AudioEditionTags()
    var coverBytes: Data?
    var durationSeconds: Double?
}

/// Locates `moov/udta/meta/ilst` and `moov/mvhd` in an MP4-family container.
struct Mp4TagReader {
    /// The metadata list also holds embedded artwork, so it is not small. This bound
    /// keeps a corrupt size field from becoming a huge allocation while still admitting
    /// real audiobook covers.
    ///
    /// Generous on purpose: overshooting it drops every tag in the file, not just the
    /// artwork, so a book with a large cover would lose its title and author too.
    private static let maximumIlstBytes: UInt64 = 24 * 1024 * 1024
    /// A movie header is a fixed ~100 bytes; anything larger is a corrupt size field.
    private static let maximumMvhdBytes: UInt64 = 4096
    private static let containerAtoms: Set<String> = ["moov", "udta", "meta"]

    func read(fileURL: URL) -> AudioEditionTags { readContainer(fileURL: fileURL).tags }

    /// Everything this reader can get from the container in one open.
    ///
    /// Artwork and runtime used to come only from AVFoundation, which returns nothing for
    /// files whose metadata it cannot map onto its own common keys. This reader seeks the
    /// atoms directly, so it keeps working on those files and AVFoundation becomes one
    /// source among several rather than the only one.
    func readContainer(fileURL: URL) -> ContainerMetadata {
        guard let handle = try? FileHandle(forReadingFrom: fileURL) else { return ContainerMetadata() }
        defer { try? handle.close() }
        let size = (try? handle.seekToEnd()) ?? 0
        let ilst = findIlst(handle, from: 0, to: size)
        return ContainerMetadata(
            tags: ilst.map(Mp4TagParser.parseIlst) ?? AudioEditionTags(),
            coverBytes: ilst.flatMap(Mp4TagParser.coverArt),
            durationSeconds: findDurationSeconds(handle, from: 0, to: size)
        )
    }

    /// Reads the runtime from `moov/mvhd`, which every MP4-family file has.
    private func findDurationSeconds(
        _ handle: FileHandle, from start: UInt64, to end: UInt64
    ) -> Double? {
        var position = start
        while position + 8 <= end {
            guard let header = read(handle, at: position, count: 16), header.count >= 8 else { return nil }
            var size = UInt64(be32(header, 0))
            var headerSize: UInt64 = 8
            guard let type = String(bytes: header[4..<8], encoding: .isoLatin1) else { return nil }
            if size == 1 {
                guard header.count >= 16 else { return nil }
                size = be64(header, 8)
                headerSize = 16
            }
            if size == 0 { size = end - position }
            guard size >= headerSize, position + size <= end else { return nil }
            let payloadStart = position + headerSize
            let payloadSize = size - headerSize

            if type == "mvhd", payloadSize > 0, payloadSize <= Self.maximumMvhdBytes {
                // Locating the atom is this type's job; interpreting it belongs with the
                // rest of the parsing, where it can be tested without a file.
                return read(handle, at: payloadStart, count: Int(payloadSize))
                    .flatMap(Mp4TagParser.durationSeconds)
            }
            // `mvhd` sits directly inside `moov`, so only that needs descending into.
            if type == "moov",
               let found = findDurationSeconds(handle, from: payloadStart, to: position + size) {
                return found
            }
            position += size
        }
        return nil
    }

    private func findIlst(_ handle: FileHandle, from start: UInt64, to end: UInt64) -> [UInt8]? {
        var position = start
        while position + 8 <= end {
            guard let header = read(handle, at: position, count: 16), header.count >= 8 else { return nil }
            var size = UInt64(be32(header, 0))
            var headerSize: UInt64 = 8
            guard let type = String(bytes: header[4..<8], encoding: .isoLatin1) else { return nil }
            if size == 1 {
                guard header.count >= 16 else { return nil }
                size = be64(header, 8)
                headerSize = 16
            }
            if size == 0 { size = end - position }
            guard size >= headerSize, position + size <= end else { return nil }

            let payloadStart = position + headerSize
            let payloadSize = size - headerSize
            if type == "ilst", payloadSize > 0, payloadSize <= Self.maximumIlstBytes {
                return read(handle, at: payloadStart, count: Int(payloadSize))
            }
            if Self.containerAtoms.contains(type) {
                // `meta` is a FullBox: its first four payload bytes are version and
                // flags rather than a nested atom header.
                let childStart = type == "meta" ? payloadStart + 4 : payloadStart
                if let found = findIlst(handle, from: childStart, to: position + size) { return found }
            }
            position += size
        }
        return nil
    }

    private func read(_ handle: FileHandle, at offset: UInt64, count: Int) -> [UInt8]? {
        guard count > 0 else { return nil }
        try? handle.seek(toOffset: offset)
        guard let data = try? handle.read(upToCount: count), !data.isEmpty else { return nil }
        return [UInt8](data)
    }

    private func be32(_ bytes: [UInt8], _ offset: Int) -> UInt32 {
        (UInt32(bytes[offset]) << 24) | (UInt32(bytes[offset + 1]) << 16)
            | (UInt32(bytes[offset + 2]) << 8) | UInt32(bytes[offset + 3])
    }

    private func be64(_ bytes: [UInt8], _ offset: Int) -> UInt64 {
        (0..<8).reduce(UInt64(0)) { ($0 << 8) | UInt64(bytes[offset + $1]) }
    }
}

private extension String {
    /// Trimmed, or nil when nothing is left.
    func trimmed() -> String? {
        let value = trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }
}
