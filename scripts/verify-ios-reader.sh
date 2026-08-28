#!/usr/bin/env bash
# Exercises the iOS reading-edition logic against a real EPUB.
#
# The iOS target has no test bundle, and the reader's riskiest code is exactly the kind
# that fails silently: a ZIP reader that returns nothing, or character offsets that drift
# because Swift indexes graphemes while the server and the Android client index UTF-16.
# These files depend only on Foundation and Compression, so they compile for the host and
# can be checked without a simulator.
#
# Usage: scripts/verify-ios-reader.sh
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sources="$repository_root/ios-app/AudioChoice"
checks="$repository_root/ios-app/ReaderChecks"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

# A minimal but genuine EPUB: a spine that is not ZIP order, front matter to trim, an
# entity to decode, and an emoji, which is two UTF-16 units but one Swift Character.
book="$workspace/book"
mkdir -p "$book/META-INF" "$book/OEBPS"
printf 'application/epub+zip' > "$book/mimetype"
cat > "$book/META-INF/container.xml" <<'XML'
<?xml version="1.0"?><container><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
XML
cat > "$book/OEBPS/content.opf" <<'XML'
<?xml version="1.0"?><package><manifest>
<item id="front" href="front.xhtml" media-type="application/xhtml+xml"/>
<item id="c1" href="chap1.xhtml" media-type="application/xhtml+xml"/>
<item id="c2" href="chap2.xhtml" media-type="application/xhtml+xml"/>
</manifest><spine><itemref idref="front"/><itemref idref="c1"/><itemref idref="c2"/></spine></package>
XML
printf '<html><body><p>Copyright notice page.</p><p>Table of Contents</p></body></html>' > "$book/OEBPS/front.xhtml"
printf '<html><body><h1>Chapter One</h1><p>The damned wind howled.</p><p>She said &amp;quot;stop&amp;quot; twice.</p></body></html>' > "$book/OEBPS/chap1.xhtml"
printf '<html><body><p>A second chapter with a caf\xc3\xa9 and an emoji \xf0\x9f\x98\x80 here.</p></body></html>' > "$book/OEBPS/chap2.xhtml"

# mimetype must be stored first and uncompressed, as a real EPUB does.
( cd "$book" && zip -X -q -0 ../test.epub mimetype && zip -X -q -r -9 ../test.epub META-INF OEBPS )

cp "$checks/main.swift" "$workspace/main.swift"
# The harness reads this path; keep it in the temporary workspace.
sed -i '' "s#/tmp/epubtest/test.epub#$workspace/test.epub#" "$workspace/main.swift"

swiftc -O -o "$workspace/readerchecks" \
  "$workspace/main.swift" \
  "$sources/ZipArchive.swift" \
  "$sources/EpubTextReader.swift" \
  "$sources/ReaderParagraphParser.swift" \
  "$sources/ReaderMasking.swift" \
  "$sources/ReaderSync.swift"

"$workspace/readerchecks"
