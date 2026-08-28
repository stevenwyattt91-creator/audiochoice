import Foundation

let epub = URL(fileURLWithPath: "/tmp/epubtest/test.epub")

// 1. ZIP reader
guard let archive = ZipArchive(fileURL: epub) else { print("FAIL: archive did not open"); exit(1) }
print("PASS zip: \(archive.entries.count) entries")
guard let container = archive.entry(named: "META-INF/container.xml"),
      let data = archive.contents(of: container),
      String(data: data, encoding: .utf8)?.contains("content.opf") == true else {
    print("FAIL: container.xml not inflated"); exit(1)
}
print("PASS inflate: container.xml readable")

// 2. Spine-ordered extraction with front matter trimmed
let text = EpubTextReader.readBlocking(fileURL: epub)
guard !text.isEmpty else { print("FAIL: empty text"); exit(1) }
print("PASS extract: \(text.utf16.count) utf16 units")
guard !text.contains("Copyright notice") else { print("FAIL: front matter not trimmed"); exit(1) }
print("PASS trim: front matter dropped")
guard text.contains("Chapter One"), text.contains("damned wind"), text.contains("second chapter") else {
    print("FAIL: missing chapter text -> \(text.debugDescription)"); exit(1)
}
guard text.range(of: "Chapter One")!.lowerBound < text.range(of: "second chapter")!.lowerBound else {
    print("FAIL: spine order wrong"); exit(1)
}
print("PASS order: chapter one precedes chapter two")

// 3. Paragraph offsets must bound their own text exactly, in UTF-16 space
let paragraphs = ReaderParagraphParser.parse(text)
guard !paragraphs.isEmpty else { print("FAIL: no paragraphs"); exit(1) }
let units = Array(text.utf16)
for p in paragraphs {
    let slice = String(decoding: units[p.startCharacter..<p.endCharacter], as: UTF16.self)
    guard slice == p.text else {
        print("FAIL: offset/text mismatch. offsets gave \(slice.debugDescription) but text is \(p.text.debugDescription)")
        exit(1)
    }
}
print("PASS offsets: \(paragraphs.count) paragraphs, every offset pair reproduces its text")

// The emoji is why this matters: it is two UTF-16 units but one Character.
guard text.contains("😀") else { print("FAIL: emoji lost"); exit(1) }
let emojiParagraph = paragraphs.first { $0.text.contains("😀") }!
let reproduced = String(decoding: units[emojiParagraph.startCharacter..<emojiParagraph.endCharacter], as: UTF16.self)
guard reproduced == emojiParagraph.text else { print("FAIL: non-BMP offset drift"); exit(1) }
print("PASS utf16: non-BMP character offsets are stable")

// 4. Masking removes text rather than covering it
let target = paragraphs.first { $0.text.contains("damned") }!
let localRange = target.text.range(of: "damned")!
let start = target.startCharacter + target.text.utf16.distance(
    from: target.text.utf16.startIndex,
    to: localRange.lowerBound.samePosition(in: target.text.utf16)!)
let mask = ReaderMask(start: start, end: start + 6)
let displayed = readerDisplayParagraphs(paragraphs, masks: [mask].merged())
let maskedParagraph = displayed.first { $0.paragraph.startCharacter == target.startCharacter }!
guard !maskedParagraph.displayText.contains("damned") else {
    print("FAIL: filtered word still present -> \(maskedParagraph.displayText)"); exit(1)
}
guard maskedParagraph.displayText.contains("…"), maskedParagraph.removedPassages == 1 else {
    print("FAIL: no removal marker -> \(maskedParagraph.displayText)"); exit(1)
}
print("PASS mask: '\(maskedParagraph.displayText)'")
guard displayed.filter({ $0.hasRemovedText }).count == 1 else { print("FAIL: bled into other paragraphs"); exit(1) }
print("PASS mask scope: only the masked paragraph changed")

// 5. Time <-> character mapping
let timings = [ReaderTimingRange(startTime: 0, endTime: 10, startCharacter: 0, endCharacter: 100)]
guard ReaderSync.character(at: 5, in: timings) == 50 else { print("FAIL: midpoint mapping"); exit(1) }
guard ReaderSync.character(at: 99, in: timings) == nil else { print("FAIL: should not guess past coverage"); exit(1) }
guard ReaderSync.time(forCharacter: 50, in: timings) == 5 else { print("FAIL: reverse mapping"); exit(1) }
guard ReaderSync.time(forCharacter: 500, in: timings) == nil else { print("FAIL: beyond coverage"); exit(1) }
print("PASS sync: mapping both directions, and nil across gaps")

print("\nALL READER CHECKS PASSED")
