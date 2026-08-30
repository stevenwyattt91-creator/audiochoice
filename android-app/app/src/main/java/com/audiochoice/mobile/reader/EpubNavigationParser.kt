package com.audiochoice.mobile.reader

/**
 * Reads a book's top-level chapter list from its navigation document or NCX.
 *
 * Only top-level entries become chapters. A book that lists every scene break in
 * its table of contents would otherwise produce hundreds of chapters, each a
 * separate render job and a separate row in the chapter control, which is worse
 * for the listener than the chapters the author actually named.
 *
 * Depth is tracked explicitly rather than matched with a regex, because "the
 * direct children of this element" is not something a regular expression can
 * express and getting it wrong flattens a nested contents list into noise.
 */
internal object EpubNavigationParser {

    /**
     * Parse an EPUB 3 navigation document's `toc` nav element.
     *
     * Returns null when the document has no toc nav or the nav has no list, so the
     * caller can fall through to the NCX and then to the spine rather than
     * treating an empty contents list as a book with no chapters.
     */
    fun parseEpub3Nav(html: String, resolve: (String) -> String): List<NavigationEntry>? {
        val tocNav = tocNavContent(html) ?: return null
        val list = firstInnerContent(tocNav, "ol") ?: return null
        val entries = directChildren(list, "li").mapNotNull { item ->
            // The first anchor in an <li> is its own target; later anchors belong
            // to a nested list, which is not a top-level entry.
            val anchor = firstElement(item, "a") ?: return@mapNotNull null
            val href = EpubHtmlOffsetExtractor.parseAttributes(anchor.openTag)["href"]
                ?.takeIf { it.isNotBlank() }
                ?: return@mapNotNull null
            navigationEntry(href, plainText(anchor.inner), resolve)
        }
        return entries.takeIf { it.isNotEmpty() }
    }

    /** Parse an NCX `navMap`'s top-level `navPoint` elements. */
    fun parseNcx(xml: String, resolve: (String) -> String): List<NavigationEntry>? {
        val navMap = firstInnerContent(xml, "navmap") ?: return null
        val entries = directChildren(navMap, "navpoint").mapNotNull { point ->
            val contentTag = firstElement(point, "content")?.openTag ?: return@mapNotNull null
            val source = EpubHtmlOffsetExtractor.parseAttributes(contentTag)["src"]
                ?.takeIf { it.isNotBlank() }
                ?: return@mapNotNull null
            val label = firstInnerContent(point, "navlabel")
                ?.let { firstInnerContent(it, "text") }
                ?.let(::plainText)
            navigationEntry(source, label, resolve)
        }
        return entries.takeIf { it.isNotEmpty() }
    }

    private fun navigationEntry(
        href: String,
        title: String?,
        resolve: (String) -> String,
    ): NavigationEntry {
        val decoded = href.trim().replace("&amp;", "&")
        val fragment = decoded.substringAfter('#', "").takeIf { it.isNotBlank() }
        val path = decoded.substringBefore('#')
        return NavigationEntry(
            title = title?.takeIf { it.isNotBlank() },
            targetEntry = resolve(path),
            targetFragment = fragment,
        )
    }

    /**
     * The content of the `nav` element declaring itself the table of contents.
     *
     * A navigation document also carries landmarks and a page list, and those are
     * not chapters, so the toc has to be picked out rather than the first nav
     * taken.
     */
    private fun tocNavContent(html: String): String? {
        var index = 0
        while (true) {
            val open = indexOfTag(html, "nav", index) ?: return null
            val close = html.indexOf('>', open)
            if (close < 0) return null
            val attributes = EpubHtmlOffsetExtractor.parseAttributes(html.substring(open + 1, close))
            val semantics = attributes["epub:type"] ?: attributes["type"]
            val inner = innerContentFrom(html, "nav", open) ?: return null
            if (semantics != null && semantics.split(' ').any { it.trim() == "toc" }) return inner
            index = close + 1
        }
    }

    private class Element(val openTag: String, val inner: String)

    /** The first element with [name] anywhere inside [html]. */
    private fun firstElement(html: String, name: String): Element? {
        val open = indexOfTag(html, name, 0) ?: return null
        val close = html.indexOf('>', open)
        if (close < 0) return null
        val openTag = html.substring(open + 1, close)
        val inner = innerContentFrom(html, name, open).orEmpty()
        return Element(openTag, inner)
    }

    private fun firstInnerContent(html: String, name: String): String? {
        val open = indexOfTag(html, name, 0) ?: return null
        return innerContentFrom(html, name, open)
    }

    /**
     * Content between the element opening at [openIndex] and its matching close,
     * counting nested occurrences of the same name.
     */
    private fun innerContentFrom(html: String, name: String, openIndex: Int): String? {
        val openTagEnd = html.indexOf('>', openIndex)
        if (openTagEnd < 0) return null
        if (html[openTagEnd - 1] == '/') return ""

        var depth = 1
        var index = openTagEnd + 1
        while (index < html.length) {
            val next = html.indexOf('<', index)
            if (next < 0) break
            val tagEnd = html.indexOf('>', next)
            if (tagEnd < 0) break
            val raw = html.substring(next + 1, tagEnd)
            val tagName = raw.removePrefix("/").takeWhile { !it.isWhitespace() && it != '/' }
                .lowercase()
            if (tagName == name) {
                if (raw.startsWith("/")) {
                    depth--
                    if (depth == 0) return html.substring(openTagEnd + 1, next)
                } else if (!raw.endsWith("/")) {
                    depth++
                }
            }
            index = tagEnd + 1
        }
        // Unclosed element: take the rest, which is better than losing the list.
        return html.substring(openTagEnd + 1)
    }

    /** Inner content of each direct child named [name], ignoring deeper ones. */
    private fun directChildren(html: String, name: String): List<String> {
        val children = mutableListOf<String>()
        var index = 0
        while (index < html.length) {
            val open = indexOfTag(html, name, index) ?: break
            val openTagEnd = html.indexOf('>', open)
            if (openTagEnd < 0) break
            val inner = innerContentFrom(html, name, open)
            if (inner == null) {
                index = openTagEnd + 1
                continue
            }
            children += inner
            // Skip past this child entirely so its nested namesakes are not
            // mistaken for siblings.
            index = (openTagEnd + 1 + inner.length).coerceAtLeast(openTagEnd + 1)
            val closeTag = html.indexOf('>', index)
            index = if (closeTag < 0) html.length else closeTag + 1
        }
        return children
    }

    /** Index of the next opening tag named [name] at or after [from]. */
    private fun indexOfTag(html: String, name: String, from: Int): Int? {
        var index = from
        while (index < html.length) {
            val next = html.indexOf('<', index)
            if (next < 0) return null
            val tagEnd = html.indexOf('>', next)
            if (tagEnd < 0) return null
            val raw = html.substring(next + 1, tagEnd)
            if (!raw.startsWith("/") && !raw.startsWith("!") && !raw.startsWith("?")) {
                val tagName = raw.takeWhile { !it.isWhitespace() && it != '/' }.lowercase()
                if (tagName == name) return next
            }
            index = tagEnd + 1
        }
        return null
    }

    /** Tags stripped and whitespace collapsed. Offsets do not matter here. */
    private fun plainText(html: String): String = html
        .replace(Regex("<[^>]*>"), " ")
        .replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&apos;", "'")
        .replace("&nbsp;", " ")
        .replace(Regex("\\s+"), " ")
        .trim()
}
