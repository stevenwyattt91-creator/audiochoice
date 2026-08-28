import Compression
import Foundation

/// A minimal read-only ZIP reader, enough for EPUB containers.
///
/// Swift has no ZIP support in the standard library and the Compression framework only
/// handles raw streams, not the container. Rather than take on a package dependency for
/// one file format, this parses the central directory directly. EPUB only ever uses
/// stored or deflated entries, so those are the only two methods supported.
///
/// Sizes come from the central directory rather than local headers, because an entry
/// written with a streaming data descriptor leaves zeroes in its local header.
struct ZipArchive {
    struct Entry {
        let name: String
        let compressionMethod: UInt16
        let compressedSize: Int
        let uncompressedSize: Int
        let localHeaderOffset: Int
    }

    private let bytes: [UInt8]
    let entries: [Entry]

    private static let endOfCentralDirectorySignature: UInt32 = 0x0605_4B50
    private static let centralDirectorySignature: UInt32 = 0x0201_4B50
    private static let localHeaderSignature: UInt32 = 0x0403_4B50
    private static let methodStored: UInt16 = 0
    private static let methodDeflated: UInt16 = 8
    /// A field of all ones means the real value lives in a Zip64 extra record.
    private static let zip64Marker: UInt32 = 0xFFFF_FFFF

    init?(fileURL: URL) {
        guard let data = try? Data(contentsOf: fileURL, options: .mappedIfSafe) else { return nil }
        self.init(data: data)
    }

    init?(data: Data) {
        let bytes = [UInt8](data)
        guard let directoryStart = Self.centralDirectoryOffset(in: bytes) else { return nil }
        self.bytes = bytes
        self.entries = Self.readCentralDirectory(bytes, from: directoryStart)
        if entries.isEmpty { return nil }
    }

    func entry(named name: String) -> Entry? {
        entries.first { $0.name == name }
    }

    /// Decompressed contents, or nil when the entry is damaged or uses a method or
    /// Zip64 layout this reader does not handle.
    func contents(of entry: Entry) -> Data? {
        // The local header repeats the name and extra fields, whose lengths vary, so the
        // payload offset can only be computed by reading it.
        let header = entry.localHeaderOffset
        guard header >= 0, header + 30 <= bytes.count,
              uint32(header) == Self.localHeaderSignature else { return nil }
        let nameLength = Int(uint16(header + 26))
        let extraLength = Int(uint16(header + 28))
        let start = header + 30 + nameLength + extraLength
        guard start >= 0, start + entry.compressedSize <= bytes.count else { return nil }

        let payload = Data(bytes[start..<(start + entry.compressedSize)])
        switch entry.compressionMethod {
        case Self.methodStored:
            return payload
        case Self.methodDeflated:
            return Self.inflate(payload, expectedSize: entry.uncompressedSize)
        default:
            return nil
        }
    }

    /// Raw DEFLATE, which is what ZIP stores. Apple's COMPRESSION_ZLIB is the raw
    /// format rather than a zlib-wrapped stream, so no header needs stripping.
    private static func inflate(_ payload: Data, expectedSize: Int) -> Data? {
        guard expectedSize > 0, !payload.isEmpty else { return nil }
        var output = Data(count: expectedSize)
        let written: Int = output.withUnsafeMutableBytes { destination in
            payload.withUnsafeBytes { source in
                guard let destinationBase = destination.bindMemory(to: UInt8.self).baseAddress,
                      let sourceBase = source.bindMemory(to: UInt8.self).baseAddress else { return 0 }
                return compression_decode_buffer(
                    destinationBase, expectedSize,
                    sourceBase, payload.count,
                    nil, COMPRESSION_ZLIB
                )
            }
        }
        guard written > 0 else { return nil }
        return written == expectedSize ? output : output.prefix(written)
    }

    /// Locates the end-of-central-directory record, which sits at the very end unless a
    /// trailing archive comment pushes it back by up to 64 KB.
    private static func centralDirectoryOffset(in bytes: [UInt8]) -> Int? {
        guard bytes.count >= 22 else { return nil }
        let earliest = max(0, bytes.count - 22 - 65_535)
        var offset = bytes.count - 22
        while offset >= earliest {
            if read32(bytes, offset) == endOfCentralDirectorySignature {
                let directoryOffset = Int(read32(bytes, offset + 16))
                guard directoryOffset >= 0, directoryOffset < bytes.count else { return nil }
                return directoryOffset
            }
            offset -= 1
        }
        return nil
    }

    private static func readCentralDirectory(_ bytes: [UInt8], from start: Int) -> [Entry] {
        var entries: [Entry] = []
        var offset = start
        while offset + 46 <= bytes.count, read32(bytes, offset) == centralDirectorySignature {
            let method = read16(bytes, offset + 10)
            let compressedSize = read32(bytes, offset + 20)
            let uncompressedSize = read32(bytes, offset + 24)
            let nameLength = Int(read16(bytes, offset + 28))
            let extraLength = Int(read16(bytes, offset + 30))
            let commentLength = Int(read16(bytes, offset + 32))
            let localHeaderOffset = read32(bytes, offset + 42)
            let nameStart = offset + 46
            guard nameStart + nameLength <= bytes.count else { break }

            // Zip64 entries would need the extra field parsed; a book that large is not
            // a real EPUB, so skip rather than mis-read a truncated size.
            let isZip64 = compressedSize == zip64Marker
                || uncompressedSize == zip64Marker
                || localHeaderOffset == zip64Marker
            if !isZip64,
               let name = String(bytes: bytes[nameStart..<(nameStart + nameLength)], encoding: .utf8) {
                entries.append(Entry(
                    name: name,
                    compressionMethod: method,
                    compressedSize: Int(compressedSize),
                    uncompressedSize: Int(uncompressedSize),
                    localHeaderOffset: Int(localHeaderOffset)
                ))
            }
            offset = nameStart + nameLength + extraLength + commentLength
        }
        return entries
    }

    private func uint16(_ offset: Int) -> UInt16 { Self.read16(bytes, offset) }
    private func uint32(_ offset: Int) -> UInt32 { Self.read32(bytes, offset) }

    private static func read16(_ bytes: [UInt8], _ offset: Int) -> UInt16 {
        guard offset >= 0, offset + 2 <= bytes.count else { return 0 }
        return UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
    }

    private static func read32(_ bytes: [UInt8], _ offset: Int) -> UInt32 {
        guard offset >= 0, offset + 4 <= bytes.count else { return 0 }
        return UInt32(bytes[offset])
            | (UInt32(bytes[offset + 1]) << 8)
            | (UInt32(bytes[offset + 2]) << 16)
            | (UInt32(bytes[offset + 3]) << 24)
    }
}
