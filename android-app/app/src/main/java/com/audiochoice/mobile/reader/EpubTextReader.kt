package com.audiochoice.mobile.reader

import android.content.ContentResolver
import android.net.Uri
import java.util.Locale
import java.util.zip.ZipInputStream

/** Reads the EPUB package spine, not ZIP-entry order, which is often not book order. */
object EpubTextReader {
    /**
     * Unzips and decodes the whole book, so this must never run on the main
     * thread: it was previously called directly from a Dispatchers.Main coroutine
     * and froze the UI on every book open that had an EPUB attached.
     */
    suspend fun read(resolver: ContentResolver, uri: Uri): String =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { readBlocking(resolver, uri) }

    private fun readBlocking(resolver: ContentResolver, uri: Uri): String = runCatching {
        resolver.openInputStream(uri)?.use { input ->
            val entries = linkedMapOf<String, String>()
            ZipInputStream(input).use { zip ->
                while (true) {
                    val entry = zip.nextEntry ?: break
                    if (!entry.isDirectory) entries[normalize(entry.name)] = zip.readBytes().toString(Charsets.UTF_8)
                }
            }
            val container = entries["meta-inf/container.xml"].orEmpty()
            val opfPath = RootfilePattern.find(container)?.groupValues?.getOrNull(1)?.let(::normalize)
            val opf = opfPath?.let(entries::get).orEmpty()
            val basePath = opfPath?.substringBeforeLast('/', "").orEmpty()
            val manifest = ManifestItemPattern.findAll(opf).associate { match ->
                match.groupValues[1] to resolve(basePath, match.groupValues[2])
            }
            val spine = SpineItemPattern.findAll(opf).mapNotNull { manifest[it.groupValues[1]] }.toList()
            val pages = if (spine.isNotEmpty()) spine.mapNotNull(entries::get) else entries
                .filterKeys { it.endsWith(".xhtml") || it.endsWith(".html") || it.endsWith(".htm") }
                .toSortedMap().values.toList()
            trimFrontMatter(pages.map(::htmlToText)).joinToString("\n\n").trim()
        }.orEmpty()
    }.getOrDefault("")

    private fun htmlToText(html: String): String = html
        .replace(Regex("(?is)<script.*?</script>|<style.*?</style>"), "")
        .replace(Regex("(?i)<br\\s*/?>|</p>|</div>|</h[1-6]>|</li>"), "\n")
        .replace(Regex("<[^>]+>"), "")
        .replace("&nbsp;", " ").replace("&amp;", "&")
        .replace("&quot;", "\"").replace("&apos;", "'")
        .replace(Regex("[ \\t]+"), " ").replace(Regex("\\n{3,}"), "\n\n").trim()

    private fun resolve(base: String, href: String): String = normalize(
        (if (base.isBlank()) href else "$base/$href").replace("../", "")
    )

    private fun normalize(path: String): String = path.replace('\\', '/').lowercase(Locale.US)

    /** Audiobooks normally begin with the story, not ebook copyright and contents pages. */
    private fun trimFrontMatter(pages: List<String>): List<String> {
        val firstStoryPage = pages.indexOfFirst { page ->
            StoryStartPattern.containsMatchIn(page.take(600))
        }
        return if (firstStoryPage > 0) pages.drop(firstStoryPage) else pages
    }

    private val RootfilePattern = Regex("(?i)<rootfile[^>]*full-path=[\"']([^\"']+)")
    private val ManifestItemPattern = Regex("(?i)<item\\b(?=[^>]*\\bid=[\"']([^\"']+))(?=[^>]*\\bhref=[\"']([^\"']+))[^>]*>")
    private val SpineItemPattern = Regex("(?i)<itemref[^>]*\\bidref=[\"']([^\"']+)")
    private val StoryStartPattern = Regex("(?im)^\\s*(prologue|chapter\\s+(one|1)\\b|part\\s+(one|1)\\b)")
}
