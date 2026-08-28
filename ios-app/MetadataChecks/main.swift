import Foundation

// Exercises the container reader against real MP4 atom layouts. Lives outside
// ios-app/AudioChoice so it does not join the app target.
//
// This code earns a test because it is the reason a book imports with a runtime and a
// cover instead of neither: AVFoundation returns nothing for files whose metadata it
// cannot map onto its own common keys, and then these atoms are the only source. It is
// also byte-offset parsing of untrusted input, where an off-by-one is a crash rather
// than a wrong answer. The Android parser has the same tests over the same layouts,
// which is what keeps the two clients agreeing about one file.

var failures = 0
func check(_ description: String, _ condition: @autoclosure () -> Bool) {
    if condition() {
        print("  ok   \(description)")
    } else {
        print("  FAIL \(description)")
        failures += 1
    }
}

// MARK: - Atom builders

func be32(_ value: UInt32) -> [UInt8] {
    [UInt8(truncatingIfNeeded: value >> 24), UInt8(truncatingIfNeeded: value >> 16),
     UInt8(truncatingIfNeeded: value >> 8), UInt8(truncatingIfNeeded: value)]
}

func be64(_ value: UInt64) -> [UInt8] {
    (0..<8).map { UInt8(truncatingIfNeeded: value >> (56 - 8 * $0)) }
}

/// An atom type is four single bytes. The `©` that prefixes the iTunes tag names is one
/// byte in a container and two in UTF-8, so encoding it the obvious way would build
/// five-byte types and quietly desync the whole walk.
func fourCC(_ type: String) -> [UInt8] {
    guard let bytes = type.data(using: .isoLatin1), bytes.count == 4 else {
        fatalError("an atom type is four Latin-1 bytes, got \(type)")
    }
    return [UInt8](bytes)
}

/// Size, type, payload: the shape every MP4 atom takes.
func atom(_ type: String, _ payload: [UInt8]) -> [UInt8] {
    be32(UInt32(payload.count + 8)) + fourCC(type) + payload
}

/// A `data` atom: a well-known type, four reserved locale bytes, then the value.
func dataAtom(_ wellKnownType: UInt32, _ value: [UInt8]) -> [UInt8] {
    atom("data", be32(wellKnownType) + be32(0) + value)
}

func textTag(_ type: String, _ text: String) -> [UInt8] {
    atom(type, dataAtom(1, Array(text.utf8)))
}

func freeformTag(_ name: String, _ value: String) -> [UInt8] {
    atom(
        "----",
        atom("mean", be32(0) + Array("com.apple.iTunes".utf8))
            + atom("name", be32(0) + Array(name.utf8))
            + dataAtom(1, Array(value.utf8))
    )
}

let synopsis = "Enter the brutal and elite world of a war college for dragon riders. "
    + "Twenty-year-old Violet Sorrengail was supposed to enter the Scribe Quadrant, "
    + "living a quiet life among books and history."

// MARK: - Synopsis

print("Reading the publisher synopsis")
check(
    "the long description is the synopsis",
    Mp4TagParser.parseIlst(textTag("ldes", synopsis)).synopsis == synopsis
)
check(
    "the long description wins over a one-line summary",
    Mp4TagParser.parseIlst(
        textTag("\u{00A9}des", "A dragon rider goes to war. She may not survive it.")
            + textTag("ldes", synopsis)
    ).synopsis == synopsis
)
check(
    "a freeform description is read too",
    Mp4TagParser.parseIlst(freeformTag("description", synopsis)).synopsis == synopsis
)
// The Explore screen labels this "About this audiobook". Showing a converter's name
// there is worse than showing nothing, because a listener reads it as the story.
check(
    "an encoder credit is not a synopsis",
    Mp4TagParser.parseIlst(
        textTag("\u{00A9}cmt", "Created by Libation, the Audible library exporter tool")
    ).synopsis == nil
)
check(
    "a one-word genre is not a synopsis",
    Mp4TagParser.parseIlst(textTag("ldes", "Fantasy")).synopsis == nil
)
check(
    "a file with no description reports none",
    Mp4TagParser.parseIlst(textTag("\u{00A9}nam", "King Sorrow")).synopsis == nil
)

// MARK: - Cover art

print("Reading the embedded cover")
// A real JPEG signature and terminator, padded past the minimum size the parser accepts.
let jpeg: [UInt8] = [0xFF, 0xD8, 0xFF, 0xE0] + [UInt8](repeating: 0x42, count: 200) + [0xFF, 0xD9]
let coverIlst = textTag("\u{00A9}nam", "King Sorrow") + atom("covr", dataAtom(13, jpeg))
check(
    "the cover comes back byte for byte",
    Mp4TagParser.coverArt(coverIlst) == Data(jpeg)
)
check(
    "the surrounding tags still parse",
    Mp4TagParser.parseIlst(coverIlst).title == "King Sorrow"
)
check(
    "a file with no artwork reports none",
    Mp4TagParser.coverArt(textTag("\u{00A9}nam", "King Sorrow")) == nil
)
// Some tools write an empty artwork atom. Treating that as an image gives every book a
// blank cover, which looks like a broken import rather than a missing file.
check(
    "an empty artwork atom is not a cover",
    Mp4TagParser.coverArt(atom("covr", dataAtom(13, []))) == nil
)

// MARK: - Duration

print("Reading the runtime from the movie header")
/// A version 0 movie header: version and flags, created, modified, timescale, duration.
func mvhd0(timescale: UInt32, duration: UInt32) -> [UInt8] {
    [0, 0, 0, 0] + be32(0) + be32(0) + be32(timescale) + be32(duration)
}
/// A version 1 header widens the timestamps to 64 bits, moving everything after them.
func mvhd1(timescale: UInt32, duration: UInt64) -> [UInt8] {
    [1, 0, 0, 0] + be64(0) + be64(0) + be32(timescale) + be64(duration)
}

let elevenHours = 11.0 * 3600
check(
    "a version 0 header gives the runtime in seconds",
    Mp4TagParser.durationSeconds(mvhd0(timescale: 1000, duration: 39_600_000)) == elevenHours
)
// Long audiobooks at a high timescale overflow 32 bits, which is why the wide form exists.
check(
    "a version 1 header reads the 64-bit duration",
    Mp4TagParser.durationSeconds(mvhd1(timescale: 44_100, duration: 1_746_360_000))
        == 1_746_360_000.0 / 44_100.0
)
// Reporting zero would show the book as instantaneous, which reads as a corrupt import.
check(
    "a zero duration is reported as unknown",
    Mp4TagParser.durationSeconds(mvhd0(timescale: 1000, duration: 0)) == nil
)
check(
    "a zero timescale cannot divide by zero",
    Mp4TagParser.durationSeconds(mvhd0(timescale: 0, duration: 39_600_000)) == nil
)
check(
    "a truncated header is reported as unknown",
    Mp4TagParser.durationSeconds([0, 0, 0, 0, 0, 0]) == nil
)

// MARK: - Whole file

print("Reading a whole container")
let workspace = URL(fileURLWithPath: NSTemporaryDirectory())
    .appendingPathComponent("metadatachecks-\(UUID().uuidString)", isDirectory: true)
try? FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: workspace) }

/// The real nesting: `moov` holds `mvhd`, and the tags sit under `udta/meta/ilst`.
/// `meta` is a full box, so four version-and-flag bytes precede its children.
func container(ilst: [UInt8], mvhd: [UInt8]) -> [UInt8] {
    atom("ftyp", Array("M4A M4A mp42isom".utf8))
        + atom(
            "moov",
            atom("mvhd", mvhd)
                + atom("trak", atom("tkhd", [UInt8](repeating: 0, count: 84)))
                + atom("udta", atom("meta", be32(0) + atom("hdlr", [UInt8](repeating: 0, count: 24))
                    + atom("ilst", ilst)))
        )
}

let fileURL = workspace.appendingPathComponent("book.m4b")
let bytes = container(
    ilst: textTag("\u{00A9}nam", "Fourth Wing")
        + textTag("\u{00A9}ART", "Rebecca Yarros")
        + freeformTag("NARRATOR", "Rebecca Soler")
        + freeformTag("ASIN", "B0BW2CCVQ2")
        + textTag("ldes", synopsis)
        + atom("covr", dataAtom(13, jpeg)),
    mvhd: mvhd0(timescale: 1000, duration: 76_020_000)
)
try? Data(bytes).write(to: fileURL)

let read = Mp4TagReader().readContainer(fileURL: fileURL)
check("the title is read from the file", read.tags.title == "Fourth Wing")
check("the author is read from the file", read.tags.author == "Rebecca Yarros")
check("the narrator is read from a freeform atom", read.tags.narrator == "Rebecca Soler")
check("the ASIN is read from a freeform atom", read.tags.asin == "B0BW2CCVQ2")
check("the synopsis is read from the file", read.tags.synopsis == synopsis)
check("the cover is read from the file", read.coverBytes == Data(jpeg))
check("the runtime is read from the file", read.durationSeconds == 76_020.0)
check("read(fileURL:) agrees with readContainer", Mp4TagReader().read(fileURL: fileURL) == read.tags)

// A file with no metadata at all must come back empty rather than throwing.
let bare = workspace.appendingPathComponent("bare.m4b")
try? Data(atom("ftyp", Array("M4A M4A mp42isom".utf8))).write(to: bare)
let bareRead = Mp4TagReader().readContainer(fileURL: bare)
check("a file with no metadata yields empty tags", bareRead.tags.isEmpty)
check("a file with no metadata yields no cover", bareRead.coverBytes == nil)
check("a file with no metadata yields no runtime", bareRead.durationSeconds == nil)

// Truncation is the common real-world corruption: an interrupted download or a partial
// copy. It must lose metadata, never crash.
let truncated = workspace.appendingPathComponent("truncated.m4b")
try? Data(bytes.prefix(bytes.count / 3)).write(to: truncated)
let truncatedRead = Mp4TagReader().readContainer(fileURL: truncated)
check("a truncated file is read without crashing", truncatedRead.coverBytes == nil)

let missing = workspace.appendingPathComponent("absent.m4b")
check(
    "a missing file yields empty metadata",
    Mp4TagReader().readContainer(fileURL: missing) == ContainerMetadata()
)

if failures == 0 {
    print("\nAll container metadata checks passed.")
} else {
    print("\n\(failures) container metadata check(s) failed.")
    exit(1)
}
