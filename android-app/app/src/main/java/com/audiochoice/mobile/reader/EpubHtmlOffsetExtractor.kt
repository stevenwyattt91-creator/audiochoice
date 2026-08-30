package com.audiochoice.mobile.reader

import com.audiochoice.mobile.data.SourceRange
import com.audiochoice.mobile.data.mergedRanges

/**
 * Converts spine documents to Book_Text while recording where things landed.
 *
 * The offsets and the text come from the same pass, deliberately. A second pass
 * over the markup would have to reproduce this one's whitespace handling
 * character for character to agree with it, and any disagreement would put a
 * highlight or a filter on the wrong words. Recording the builder length before
 * and after each element instead makes the spans correct by construction.
 *
 * Whitespace is normalised as text is emitted rather than cleaned up afterwards,
 * because a cleanup pass would move every offset recorded before it.
 *
 * This is a tag scanner, not an HTML parser. EPUB content is XHTML and the
 * existing reader already relies on that, but the scanner tolerates unbalanced
 * and unknown tags because real books contain them and losing a chapter to a
 * stray `<i>` would be worse than an approximate span.
 */
internal class EpubHtmlOffsetExtractor {

    private val builder = StringBuilder()
    private val nonProseRanges = mutableListOf<SourceRange>()
    private val anchorOffsets = mutableMapOf<String, Int>()

    /** Queued whitespace, applied only when real text follows it. */
    private var pendingSpace = false
    private var pendingNewlines = 0

    /**
     * Anchors waiting for their first character.
     *
     * Resolved lazily rather than at the opening tag, because at that moment a
     * paragraph break from the previous element is still queued and would land
     * inside the anchor. A chapter whose offset points at the blank line before
     * its title reads as starting with an empty line.
     *
     * Lazy resolution also handles the common EPUB 2 pattern of an empty marker
     * immediately before a heading -- `<a id="ch3"/><h1>Three</h1>` -- which has no
     * content of its own and should point at the heading.
     */
    private val pendingAnchors = mutableListOf<String>()

    /**
     * Append one spine document and report the range it occupies.
     *
     * [forceNonProse] marks the whole document as unsuited to narration, used for
     * the navigation document and for a document whose root declares itself front
     * matter.
     */
    fun appendDocument(entryName: String, html: String, forceNonProse: Boolean): SourceRange {
        if (builder.isNotEmpty()) {
            // Documents are separated by a paragraph break, matching how the
            // existing reader joins pages.
            pendingNewlines = 2
        }
        val start = builder.length
        scan(entryName, html)
        val range = SourceRange(start, builder.length)
        if (forceNonProse && !range.isEmpty) nonProseRanges += range
        return range
    }

    fun result(): ExtractionResult = ExtractionResult(
        text = builder.toString(),
        nonProseRanges = nonProseRanges.mergedRanges(),
        anchorOffsets = anchorOffsets.toMap(),
    )

    data class ExtractionResult(
        val text: String,
        val nonProseRanges: List<SourceRange>,
        val anchorOffsets: Map<String, Int>,
    )

    // region scanning

    private class OpenElement(
        val name: String,
        val nonProseStart: Int?,
        val suppressesText: Boolean,
    )

    private fun scan(entryName: String, html: String) {
        val stack = ArrayDeque<OpenElement>()
        var suppressDepth = 0
        var index = 0
        val length = html.length

        while (index < length) {
            val char = html[index]
            if (char != '<') {
                val next = html.indexOf('<', index)
                val end = if (next < 0) length else next
                if (suppressDepth == 0) emitText(html, index, end)
                index = end
                continue
            }

            // Comments, doctypes and processing instructions carry no text.
            if (html.startsWith("<!--", index)) {
                val close = html.indexOf("-->", index + 4)
                index = if (close < 0) length else close + 3
                continue
            }
            if (html.startsWith("<!", index) || html.startsWith("<?", index)) {
                val close = html.indexOf('>', index)
                index = if (close < 0) length else close + 1
                continue
            }

            val close = html.indexOf('>', index)
            if (close < 0) break
            val raw = html.substring(index + 1, close)
            index = close + 1

            if (raw.startsWith("/")) {
                val name = elementName(raw.substring(1))
                closeElement(name, stack).let { closed ->
                    if (closed?.suppressesText == true) {
                        suppressDepth = (suppressDepth - 1).coerceAtLeast(0)
                    }
                }
                if (name in BLOCK_ELEMENTS) requestNewline()
                continue
            }

            val selfClosing = raw.endsWith("/")
            val name = elementName(raw)
            if (name.isEmpty()) continue
            val attributes = parseAttributes(raw)

            attributes["id"]?.takeIf { it.isNotBlank() }?.let { id ->
                pendingAnchors += "$entryName#$id"
            }

            val suppresses = name in TEXT_SUPPRESSING_ELEMENTS
            val nonProse = isNonProse(name, attributes)

            if (name in VOID_ELEMENTS || selfClosing) {
                if (name == "br" || name == "hr") requestNewline()
                continue
            }

            if (suppresses) suppressDepth++
            stack.addLast(
                OpenElement(
                    name = name,
                    nonProseStart = if (nonProse) builder.length else null,
                    suppressesText = suppresses,
                ),
            )
        }

        // Unbalanced markup: close what is still open so a marked element that
        // never closed still contributes a span rather than being dropped.
        while (stack.isNotEmpty()) {
            val open = stack.removeLast()
            open.nonProseStart?.let { recordNonProse(it) }
        }

        // An anchor at the very end of a document has no character to attach to.
        // Pointing it at the end of the text is still a usable chapter boundary,
        // and dropping it would lose a chapter.
        if (pendingAnchors.isNotEmpty()) {
            val offset = builder.length
            pendingAnchors.forEach { anchorOffsets.putIfAbsent(it, offset) }
            pendingAnchors.clear()
        }
    }

    /**
     * Pop to the named element, recording spans for anything closed on the way.
     *
     * Popping through unclosed children rather than ignoring a mismatch is what
     * keeps a `<table>` span from swallowing the rest of the chapter when a `<td>`
     * inside it was never closed.
     */
    private fun closeElement(name: String, stack: ArrayDeque<OpenElement>): OpenElement? {
        val depth = stack.indexOfLast { it.name == name }
        if (depth < 0) return null
        var closed: OpenElement? = null
        while (stack.size > depth) {
            val open = stack.removeLast()
            open.nonProseStart?.let { recordNonProse(it) }
            closed = open
        }
        return closed
    }

    private fun recordNonProse(start: Int) {
        if (builder.length > start) nonProseRanges += SourceRange(start, builder.length)
    }

    // endregion

    // region text emission

    private fun emitText(html: String, from: Int, to: Int) {
        var index = from
        while (index < to) {
            val char = html[index]
            when {
                char.isWhitespace() -> {
                    pendingSpace = true
                    index++
                }

                char == '&' -> {
                    val semicolon = html.indexOf(';', index + 1)
                    val decoded = if (semicolon in (index + 1) until minOf(to, index + 12)) {
                        decodeEntity(html.substring(index + 1, semicolon))
                    } else {
                        null
                    }
                    if (decoded == null) {
                        appendCharacter('&')
                        index++
                    } else {
                        decoded.forEach { appendCharacter(it) }
                        index = semicolon + 1
                    }
                }

                else -> {
                    appendCharacter(char)
                    index++
                }
            }
        }
    }

    private fun appendCharacter(char: Char) {
        if (char.isWhitespace()) {
            pendingSpace = true
            return
        }
        flushPending()
        if (pendingAnchors.isNotEmpty()) {
            val offset = builder.length
            pendingAnchors.forEach { anchorOffsets.putIfAbsent(it, offset) }
            pendingAnchors.clear()
        }
        builder.append(char)
    }

    private fun requestNewline() {
        // Capped at two on flush, which is what turns a run of closing block tags
        // into one paragraph break instead of a column of blank lines.
        pendingNewlines = (pendingNewlines + 1).coerceAtMost(2)
        pendingSpace = false
    }

    private fun flushPending() {
        if (builder.isEmpty()) {
            // Never open Book_Text with whitespace: a leading space would shift
            // every offset in the book by one for no visible reason.
            pendingSpace = false
            pendingNewlines = 0
            return
        }
        if (pendingNewlines > 0) {
            var existing = 0
            var cursor = builder.length - 1
            while (cursor >= 0 && builder[cursor] == '\n') {
                existing++
                cursor--
            }
            repeat((pendingNewlines + existing).coerceAtMost(2) - existing) { builder.append('\n') }
            pendingNewlines = 0
            pendingSpace = false
            return
        }
        if (pendingSpace) {
            val last = builder.last()
            if (last != ' ' && last != '\n') builder.append(' ')
            pendingSpace = false
        }
    }

    // endregion

    private fun isNonProse(name: String, attributes: Map<String, String>): Boolean {
        if (name in NON_PROSE_ELEMENTS) return true
        val semantics = attributes["epub:type"] ?: attributes["type"]
        if (semantics != null && semantics.split(' ').any { it.trim() in NON_PROSE_SEMANTICS }) {
            return true
        }
        val role = attributes["role"]
        return role != null && role.split(' ').any { it.trim() in NON_PROSE_ROLES }
    }

    companion object {
        private fun elementName(raw: String): String {
            var end = 0
            while (end < raw.length && !raw[end].isWhitespace() && raw[end] != '/') end++
            return raw.substring(0, end).lowercase().removePrefix("xhtml:")
        }

        internal fun parseAttributes(raw: String): Map<String, String> {
            val attributes = mutableMapOf<String, String>()
            var index = 0
            // Skip the element name.
            while (index < raw.length && !raw[index].isWhitespace()) index++
            while (index < raw.length) {
                while (index < raw.length && (raw[index].isWhitespace() || raw[index] == '/')) index++
                if (index >= raw.length) break
                val nameStart = index
                while (index < raw.length && raw[index] != '=' && !raw[index].isWhitespace() &&
                    raw[index] != '/'
                ) {
                    index++
                }
                val name = raw.substring(nameStart, index).lowercase()
                if (name.isEmpty()) {
                    index++
                    continue
                }
                while (index < raw.length && raw[index].isWhitespace()) index++
                if (index >= raw.length || raw[index] != '=') {
                    attributes.putIfAbsent(name, "")
                    continue
                }
                index++
                while (index < raw.length && raw[index].isWhitespace()) index++
                if (index >= raw.length) break
                val quote = raw[index]
                val value: String
                if (quote == '"' || quote == '\'') {
                    index++
                    val valueStart = index
                    while (index < raw.length && raw[index] != quote) index++
                    value = raw.substring(valueStart, index)
                    if (index < raw.length) index++
                } else {
                    val valueStart = index
                    while (index < raw.length && !raw[index].isWhitespace()) index++
                    value = raw.substring(valueStart, index)
                }
                attributes.putIfAbsent(name, value)
            }
            return attributes
        }

        internal fun decodeEntity(body: String): String? {
            if (body.isEmpty() || body.length > 10) return null
            if (body[0] == '#') {
                val code = if (body.length > 1 && (body[1] == 'x' || body[1] == 'X')) {
                    body.substring(2).toIntOrNull(16)
                } else {
                    body.substring(1).toIntOrNull()
                } ?: return null
                if (code <= 0 || code > 0x10FFFF) return null
                return String(Character.toChars(code))
            }
            return NAMED_ENTITIES[body]
        }

        /** Elements whose text is markup or metadata rather than prose. */
        private val TEXT_SUPPRESSING_ELEMENTS = setOf("script", "style", "head")

        /**
         * Elements that end a line. A run of these collapses to one paragraph
         * break rather than several blank lines.
         */
        private val BLOCK_ELEMENTS = setOf(
            "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "tr", "blockquote",
            "section", "article", "aside", "header", "footer", "figure", "figcaption",
            "table", "pre", "ul", "ol", "dd", "dt", "dl", "body", "nav", "main", "td", "th",
        )

        private val VOID_ELEMENTS = setOf(
            "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
            "param", "source", "track", "wbr",
        )

        /** Structural content a narrator would not read aloud. */
        private val NON_PROSE_ELEMENTS = setOf("table", "pre", "code", "figcaption", "img")

        /**
         * EPUB structural semantics. The first group is content inside a chapter
         * that is not narration; the second is whole-document front and back
         * matter, which is what makes narration begin at the story.
         */
        private val NON_PROSE_SEMANTICS = setOf(
            "footnote", "endnote", "pagebreak", "noteref", "toc",
            "cover", "titlepage", "copyright-page", "colophon", "landmarks", "loi", "lot",
        )

        private val NON_PROSE_ROLES = setOf("doc-footnote", "doc-endnote", "doc-pagebreak")

        private val NAMED_ENTITIES = mapOf(
            "amp" to "&", "lt" to "<", "gt" to ">", "quot" to "\"", "apos" to "'",
            "nbsp" to " ", "ensp" to " ", "emsp" to " ", "thinsp" to " ",
            "mdash" to "\u2014", "ndash" to "\u2013", "hellip" to "\u2026",
            "lsquo" to "\u2018", "rsquo" to "\u2019", "ldquo" to "\u201C", "rdquo" to "\u201D",
            "sbquo" to "\u201A", "bdquo" to "\u201E", "dagger" to "\u2020", "Dagger" to "\u2021",
            "bull" to "\u2022", "prime" to "\u2032", "Prime" to "\u2033",
            "copy" to "\u00A9", "reg" to "\u00AE", "trade" to "\u2122", "deg" to "\u00B0",
            "shy" to "", "zwnj" to "", "zwj" to "",
            "eacute" to "\u00E9", "egrave" to "\u00E8", "agrave" to "\u00E0",
            "ccedil" to "\u00E7", "uuml" to "\u00FC", "ouml" to "\u00F6", "auml" to "\u00E4",
            "szlig" to "\u00DF", "ntilde" to "\u00F1", "iexcl" to "\u00A1", "iquest" to "\u00BF",
            "laquo" to "\u00AB", "raquo" to "\u00BB", "middot" to "\u00B7", "pound" to "\u00A3",
            "euro" to "\u20AC", "sect" to "\u00A7", "para" to "\u00B6",
        )
    }
}
