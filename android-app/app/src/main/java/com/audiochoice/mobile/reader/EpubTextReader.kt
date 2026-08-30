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

    /**
     * Narration extraction. Same archive, richer result than [read].
     *
     * A second entry point rather than a change to [read] because the two want
     * different text. [read] trims front matter with a keyword heuristic so a
     * read-along starts where the narrator starts. Narration keeps every spine
     * document and classifies front matter from the EPUB's own declared
     * semantics, which is declaration-driven instead of a guess and works on a
     * book whose first division is not named "prologue" or "chapter one".
     *
     * Keeping [read] untouched matters for a second reason: reader alignments for
     * imported audiobooks are cached against `READER_ALIGNMENT_VERSION`, and
     * changing the text they were computed from would invalidate every one of
     * them on devices that already hold them.
     *
     * The two profiles are distinguished by [NARRATION_EXTRACTION_VERSION], which
     * is folded into the Book_Text hash, so a plan built by one profile is never
     * reinterpreted against text produced by the other.
     */
    suspend fun readNarrationDocument(resolver: ContentResolver, uri: Uri): EpubDocument =
        kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            runCatching {
                resolver.openInputStream(uri)?.use { readNarrationDocument(it) }
                    ?: EpubDocument.failed(ExtractionFailure.UNREADABLE_ARCHIVE)
            }.getOrDefault(EpubDocument.failed(ExtractionFailure.UNREADABLE_ARCHIVE))
        }

    /**
     * Stream-level entry point, separated from the `ContentResolver` so extraction
     * can be exercised against real EPUB archives in a plain unit test.
     *
     * Extraction is where the correctness properties live -- offsets must index
     * Book_Text exactly, and Book_Text must be stable for a given file -- and those
     * are worth testing against fixtures rather than only on a device.
     */
    internal fun readNarrationDocument(input: java.io.InputStream): EpubDocument {
        val entryNames = linkedSetOf<String>()
        val textEntries = linkedMapOf<String, String>()

        ZipInputStream(input).use { zip ->
            while (true) {
                val entry = zip.nextEntry ?: break
                if (entry.isDirectory) continue
                val name = normalize(entry.name)
                entryNames += name
                // Only decode entries that can carry text. The existing reader
                // decodes every entry as UTF-8, including images and fonts, which
                // on a large illustrated book is a substantial and pointless heap
                // spike.
                if (isTextualEntry(name)) {
                    textEntries[name] = zip.readBytes().toString(Charsets.UTF_8)
                }
            }
        }

        if (entryNames.isEmpty()) return EpubDocument.failed(ExtractionFailure.UNREADABLE_ARCHIVE)

        val container = textEntries["meta-inf/container.xml"]
        val opfPath = container
            ?.let { RootfilePattern.find(it)?.groupValues?.getOrNull(1) }
            ?.let(::normalize)
        val opf = opfPath?.let(textEntries::get)
        if (opfPath == null || opf == null) {
            // No container, or it names a package document that is not present.
            // Not an EPUB this app can read, which the validator reports as its own
            // reason rather than as missing text.
            return EpubDocument.failed(ExtractionFailure.MISSING_PACKAGE_DOCUMENT)
        }

        val basePath = opfPath.substringBeforeLast('/', "")
        fun resolveHref(href: String) = resolve(basePath, href)

        val manifest = ManifestItemPattern.findAll(opf).associate { match ->
            match.groupValues[1] to resolveHref(match.groupValues[2])
        }
        val manifestProperties = ManifestPropertiesPattern.findAll(opf).associate { match ->
            match.groupValues[1] to match.groupValues[2]
        }
        val manifestMediaTypes = ManifestMediaTypePattern.findAll(opf).associate { match ->
            match.groupValues[1] to match.groupValues[2].lowercase(Locale.US)
        }

        val declaredSpine = SpineItemPattern.findAll(opf)
            .mapNotNull { manifest[it.groupValues[1]] }
            .toList()

        val navigationEntry = manifestProperties.entries
            .firstOrNull { it.value.split(' ').any { token -> token.trim() == "nav" } }
            ?.let { manifest[it.key] }
        val ncxEntry = manifestMediaTypes.entries
            .firstOrNull { it.value == "application/x-dtbncx+xml" }
            ?.let { manifest[it.key] }
            ?: SpineTocPattern.find(opf)?.groupValues?.getOrNull(1)?.let { manifest[it] }

        val encryptedEntries = encryptedEntryNames(textEntries["meta-inf/encryption.xml"], basePath)

        // Store DRM is encryption over anything that carries the text. An EPUB that
        // encrypts only fonts, images or media is perfectly narratable: those
        // entries contribute nothing to Book_Text, so they are simply excluded.
        val storeDrm = buildList {
            if (opfPath in encryptedEntries) {
                add(EncryptedTextResource(opfPath, TextResourceRole.PACKAGE_DOCUMENT))
            }
            navigationEntry?.takeIf { it in encryptedEntries }?.let {
                add(EncryptedTextResource(it, TextResourceRole.NAVIGATION_DOCUMENT))
            }
            ncxEntry?.takeIf { it in encryptedEntries }?.let {
                add(EncryptedTextResource(it, TextResourceRole.NCX_DOCUMENT))
            }
            declaredSpine.filter { it in encryptedEntries }.forEach {
                add(EncryptedTextResource(it, TextResourceRole.SPINE_DOCUMENT))
            }
        }

        val metadata = readMetadata(opf, manifest, manifestProperties, ::resolveHref)

        if (storeDrm.isNotEmpty()) {
            // Stop before converting a single spine document. The validator has to
            // be able to decline without having extracted any of the book's text,
            // so there is nothing to purge on that path.
            return EpubDocument(
                text = "",
                extractionVersion = NARRATION_EXTRACTION_VERSION,
                language = metadata.language,
                title = metadata.title,
                author = metadata.author,
                coverImageEntry = metadata.coverEntry,
                resources = emptyList(),
                nonProseRanges = emptyList(),
                anchorOffsets = emptyMap(),
                navigation = null,
                declaresNavigation = navigationEntry != null || ncxEntry != null,
                encryptedEntries = encryptedEntries,
                storeDrmResources = storeDrm,
                unreadableSpineEntries = emptyList(),
                declaredSpineEntries = declaredSpine,
            )
        }

        val extractor = EpubHtmlOffsetExtractor()
        val resources = mutableListOf<ResourceSpan>()
        val unreadable = mutableListOf<String>()

        // EPUB 2 books declare front matter in the package document's guide rather
        // than with element semantics on the page. Without this, the cover, title
        // and copyright pages of an older book would be narrated as prose, which is
        // exactly the outcome R3.13 exists to prevent.
        val guideFrontMatter = GuideReferencePattern.findAll(opf)
            .filter { it.groupValues[1].lowercase(Locale.US).trim() in GUIDE_FRONT_MATTER_TYPES }
            .map { resolveHref(it.groupValues[2].substringBefore('#')) }
            .toSet()

        declaredSpine.forEach { entry ->
            val html = textEntries[entry]
            if (entry in encryptedEntries || html.isNullOrBlank()) {
                unreadable += entry
                return@forEach
            }
            // The navigation document is a table of contents. It is text, and some
            // books put it in the spine, but nobody wants it read aloud.
            val range = extractor.appendDocument(
                entryName = entry,
                html = html,
                forceNonProse = entry == navigationEntry || entry in guideFrontMatter,
            )
            if (range.isEmpty) unreadable += entry else resources += ResourceSpan(entry, range)
        }

        val extraction = extractor.result()

        val navigation = navigationEntry
            ?.let(textEntries::get)
            ?.let { nav ->
                EpubNavigationParser.parseEpub3Nav(nav) { href ->
                    resolve(navigationEntry.substringBeforeLast('/', ""), href)
                }
            }
            ?.let { NavigationOutline(NavigationSource.EPUB3_NAV, it) }
            ?: ncxEntry
                ?.let(textEntries::get)
                ?.let { ncx ->
                    EpubNavigationParser.parseNcx(ncx) { href ->
                        resolve(ncxEntry.substringBeforeLast('/', ""), href)
                    }
                }
                ?.let { NavigationOutline(NavigationSource.NCX, it) }

        return EpubDocument(
            text = extraction.text,
            extractionVersion = NARRATION_EXTRACTION_VERSION,
            language = metadata.language,
            title = metadata.title,
            author = metadata.author,
            coverImageEntry = metadata.coverEntry,
            resources = resources,
            nonProseRanges = extraction.nonProseRanges,
            anchorOffsets = extraction.anchorOffsets,
            navigation = navigation,
            declaresNavigation = navigationEntry != null || ncxEntry != null,
            encryptedEntries = encryptedEntries,
            storeDrmResources = emptyList(),
            unreadableSpineEntries = unreadable,
            declaredSpineEntries = declaredSpine,
        )
    }

    private data class PackageMetadata(
        val title: String?,
        val author: String?,
        val language: String?,
        val coverEntry: String?,
    )

    private fun readMetadata(
        opf: String,
        manifest: Map<String, String>,
        manifestProperties: Map<String, String>,
        resolveHref: (String) -> String,
    ): PackageMetadata {
        // First in document order, per the requirement: a package document may
        // declare several titles or creators and the first is the work's own.
        val title = DcTitlePattern.find(opf)?.groupValues?.getOrNull(1)?.let(::collapseWhitespace)
        val author = DcCreatorPattern.find(opf)?.groupValues?.getOrNull(1)?.let(::collapseWhitespace)
        val language = DcLanguagePattern.find(opf)?.groupValues?.getOrNull(1)?.trim()
            ?.takeIf { it.isNotBlank() }
        val coverByProperty = manifestProperties.entries
            .firstOrNull { it.value.split(' ').any { token -> token.trim() == "cover-image" } }
            ?.let { manifest[it.key] }
        val coverByMeta = CoverMetaPattern.find(opf)?.groupValues?.getOrNull(1)?.let { manifest[it] }
        val coverByHref = CoverHrefPattern.find(opf)?.groupValues?.getOrNull(1)?.let(resolveHref)
        return PackageMetadata(
            title = title?.takeIf { it.isNotBlank() },
            author = author?.takeIf { it.isNotBlank() },
            language = language,
            coverEntry = coverByProperty ?: coverByMeta ?: coverByHref,
        )
    }

    /**
     * Entries named by a `CipherReference`.
     *
     * No `CipherData` payload is read and nothing is decrypted. All this does is
     * learn which entries are unreadable so the book can be declined or those
     * entries skipped.
     *
     * A `CipherReference` URI is relative to the archive root rather than to the
     * package document, but books get this wrong often enough that both
     * interpretations are recorded.
     */
    private fun encryptedEntryNames(encryptionXml: String?, basePath: String): Set<String> {
        if (encryptionXml.isNullOrBlank()) return emptySet()
        return CipherReferencePattern.findAll(encryptionXml)
            .map { it.groupValues[1] }
            .flatMap { uri ->
                val decoded = uri.replace("&amp;", "&").let(::decodePercentEncoding)
                sequenceOf(normalize(decoded), resolve(basePath, decoded))
            }
            .toSet()
    }

    private fun decodePercentEncoding(value: String): String = runCatching {
        java.net.URLDecoder.decode(value, "UTF-8")
    }.getOrDefault(value)

    private fun collapseWhitespace(value: String): String =
        value.replace(Regex("<[^>]*>"), " ")
            .replace("&amp;", "&")
            .replace("&apos;", "'")
            .replace("&quot;", "\"")
            .replace(Regex("\\s+"), " ")
            .trim()

    private fun isTextualEntry(name: String): Boolean =
        name.startsWith("meta-inf/") ||
            TEXTUAL_EXTENSIONS.any { name.endsWith(it) }

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

    // Narration extraction only. The patterns above are shared with read().
    private val ManifestPropertiesPattern =
        Regex("(?i)<item\\b(?=[^>]*\\bid=[\"']([^\"']+))(?=[^>]*\\bproperties=[\"']([^\"']*))[^>]*>")
    private val ManifestMediaTypePattern =
        Regex("(?i)<item\\b(?=[^>]*\\bid=[\"']([^\"']+))(?=[^>]*\\bmedia-type=[\"']([^\"']+))[^>]*>")
    private val SpineTocPattern = Regex("(?i)<spine[^>]*\\btoc=[\"']([^\"']+)")
    private val CipherReferencePattern = Regex("(?i)<cipherreference[^>]*\\buri=[\"']([^\"']+)")
    private val DcTitlePattern = Regex("(?is)<dc:title[^>]*>(.*?)</dc:title>")
    private val DcCreatorPattern = Regex("(?is)<dc:creator[^>]*>(.*?)</dc:creator>")
    private val DcLanguagePattern = Regex("(?is)<dc:language[^>]*>(.*?)</dc:language>")
    private val CoverMetaPattern =
        Regex("(?i)<meta\\b(?=[^>]*\\bname=[\"']cover[\"'])(?=[^>]*\\bcontent=[\"']([^\"']+))[^>]*>")
    private val CoverHrefPattern =
        Regex("(?i)<reference[^>]*\\btype=[\"']cover[\"'][^>]*\\bhref=[\"']([^\"']+)")
    private val GuideReferencePattern =
        Regex("(?i)<reference\\b(?=[^>]*\\btype=[\"']([^\"']+))(?=[^>]*\\bhref=[\"']([^\"']+))[^>]*>")

    /**
     * EPUB 2 guide types that are not narration. Deliberately excludes dedication,
     * epigraph, foreword and preface: a narrator does read those, and dropping them
     * would remove text the author wrote for the reader.
     */
    private val GUIDE_FRONT_MATTER_TYPES = setOf(
        "cover", "title-page", "titlepage", "toc", "copyright-page", "colophon",
        "loi", "lot", "index", "landmarks",
    )

    private val TEXTUAL_EXTENSIONS = listOf(
        ".xhtml", ".html", ".htm", ".xml", ".opf", ".ncx", ".txt", ".svg",
    )

    /**
     * Increment whenever narration extraction changes what Book_Text contains.
     *
     * Folded into the Book_Text hash, so a change here is detected as a hash
     * change and the affected plans are rebuilt rather than reinterpreted against
     * text that moved underneath their offsets.
     */
    const val NARRATION_EXTRACTION_VERSION = 1
}
