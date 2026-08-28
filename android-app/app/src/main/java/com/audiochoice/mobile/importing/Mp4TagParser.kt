package com.audiochoice.mobile.importing

/**
 * Identity-bearing metadata read out of an audiobook container.
 *
 * This exists because a byte hash cannot answer "which edition is this". A
 * listener who converts an AAX to M4B, or re-tags a file, produces different
 * bytes for the same recording. The tags below are the cheap signals that
 * survive that, and [productIdentifier] is the only one that is definitive.
 */
data class AudioEditionTags(
    val title: String? = null,
    val author: String? = null,
    val albumTitle: String? = null,
    val albumArtist: String? = null,
    val narrator: String? = null,
    val publisher: String? = null,
    val copyright: String? = null,
    val year: String? = null,
    val seriesTitle: String? = null,
    val seriesPart: Int? = null,
    /** Audible's product identifier. */
    val asin: String? = null,
    val isbn: String? = null,
) {
    /**
     * A retail product identifier names one specific published edition, so it can
     * be trusted outright. Everything else here is a heuristic that needs
     * corroboration from duration or chapter structure.
     */
    val productIdentifier: String? get() = asin ?: isbn

    val isEmpty: Boolean get() = this == AudioEditionTags()
}

/**
 * Reads the iTunes-style `ilst` metadata list used by M4A/M4B/MP4/AAX audiobooks.
 *
 * Kept free of any file IO so the byte layout can be tested directly. [Mp4TagReader]
 * supplies the payload.
 */
object Mp4TagParser {

    /** Well-known data types from the `data` atom's flag field. */
    private const val TYPE_UTF8 = 1L
    private const val TYPE_UTF16 = 2L
    private const val TYPE_SIGNED_INT = 21L
    private const val TYPE_UNSIGNED_INT = 22L

    /** Smaller than this is not an image, whatever the atom claims. */
    private const val MINIMUM_COVER_BYTES = 64

    /** Audiobook covers are large but not unbounded. */
    private const val MAXIMUM_COVER_BYTES = 6 * 1024 * 1024

    /**
     * @param payload the raw contents of an `ilst` atom, header excluded.
     */
    fun parseIlst(payload: ByteArray): AudioEditionTags {
        val standard = mutableMapOf<String, String>()
        val freeform = mutableMapOf<String, String>()

        forEachAtom(payload, 0, payload.size) { type, bodyStart, bodyEnd ->
            if (type == "----") {
                freeformEntry(payload, bodyStart, bodyEnd)?.let { (key, value) ->
                    freeform.putIfAbsent(key, value)
                }
            } else {
                dataValue(payload, bodyStart, bodyEnd)?.let { value ->
                    standard.putIfAbsent(type, value)
                }
            }
        }

        fun standardOf(vararg keys: String) = keys.firstNotNullOfOrNull { standard[it] }
        fun freeformOf(vararg keys: String) = keys.firstNotNullOfOrNull { freeform[it] }

        return AudioEditionTags(
            title = standardOf("\u00A9nam"),
            author = standardOf("\u00A9ART") ?: freeformOf("author", "artist"),
            albumTitle = standardOf("\u00A9alb"),
            albumArtist = standardOf("aART"),
            // Audible and most converters put the narrator in a freeform tag;
            // composer is the long-standing fallback that tagging tools use.
            narrator = freeformOf("narrator", "narrators")
                ?: standardOf("\u00A9nrt", "\u00A9wrt"),
            publisher = freeformOf("publisher", "label") ?: standardOf("\u00A9pub"),
            copyright = standardOf("cprt"),
            year = standardOf("\u00A9day")?.let(::yearOf),
            seriesTitle = freeformOf("series", "series_name", "book_series", "show"),
            seriesPart = freeformOf(
                "series-part",
                "series_part",
                "seriespart",
                "book_series_index",
                "series_sequence",
            )?.let(::seriesPartOf),
            asin = freeformOf("asin", "product_id")?.let(::identifierOf),
            isbn = freeformOf("isbn", "isbn13", "isbn_13")?.let(::identifierOf),
        )
    }

    /**
     * The embedded cover image, read from the `covr` atom in the same metadata list.
     *
     * MediaMetadataRetriever exposes artwork through `embeddedPicture`, but it needs a
     * seekable data source and gives nothing at all when it cannot open one — which is how a
     * book imports with its title, author, narrator and chapters intact and no cover, since
     * those come from this parser instead. Reading the atom directly means artwork survives
     * wherever the rest of the tags do.
     *
     * @param payload the raw contents of an `ilst` atom, header excluded.
     */
    fun coverArt(payload: ByteArray): ByteArray? {
        var result: ByteArray? = null
        forEachAtom(payload, 0, payload.size) { type, bodyStart, bodyEnd ->
            if (type != "covr" || result != null) return@forEachAtom
            forEachAtom(payload, bodyStart, bodyEnd) { childType, dataStart, dataEnd ->
                // A `data` atom is four bytes of version and type flags, then four reserved
                // locale bytes, then the image itself.
                if (childType != "data" || result != null || dataStart + 8 > dataEnd) {
                    return@forEachAtom
                }
                val imageStart = dataStart + 8
                val length = dataEnd - imageStart
                // Keeps a corrupt size field from becoming a large allocation, and refuses
                // anything too small to be an image.
                if (length in MINIMUM_COVER_BYTES..MAXIMUM_COVER_BYTES) {
                    result = payload.copyOfRange(imageStart, dataEnd)
                }
            }
        }
        return result
    }

    /**
     * The runtime stated by a movie header (`mvhd`) atom, in seconds.
     *
     * The header carries a timescale and a duration counted in those units, so this is what
     * the container says about itself rather than an estimate from the file size or bitrate.
     *
     * @param payload the raw contents of an `mvhd` atom, header excluded.
     */
    fun durationSeconds(payload: ByteArray): Double? {
        if (payload.size < 4) return null
        // A full box: one version byte, then three flag bytes. Version 1 widened the creation
        // and modification times to 64 bits, which moves everything after them.
        val version = payload[0].toInt() and 0xFF
        val timescaleOffset = if (version == 1) 20 else 12
        val durationOffset = timescaleOffset + 4
        val durationBytes = if (version == 1) 8 else 4
        if (durationOffset + durationBytes > payload.size) return null

        val timescale = uint32(payload, timescaleOffset)
        if (timescale <= 0L) return null
        val duration = if (version == 1) {
            var value = 0L
            for (index in 0 until 8) {
                value = (value shl 8) or (payload[durationOffset + index].toLong() and 0xFF)
            }
            value
        } else {
            uint32(payload, durationOffset)
        }
        // A duration of zero means the header was written without one, which some converters
        // do; treating that as a real runtime would show a book as zero seconds long.
        if (duration <= 0L) return null
        val seconds = duration.toDouble() / timescale
        return seconds.takeIf { it.isFinite() && it > 0 }
    }

    /** Dates arrive as bare years or as full ISO timestamps. */
    private fun yearOf(value: String): String? =
        Regex("(1[89]\\d{2}|20\\d{2}|21\\d{2})").find(value)?.value

    /**
     * Series indices are written as plain numbers but also as "Book 3" or "3 of 7",
     * so take the first standalone number rather than requiring a bare integer.
     */
    private fun seriesPartOf(value: String): Int? =
        Regex("\\d{1,3}").find(value)?.value?.toIntOrNull()?.takeIf { it > 0 }

    /** Identifiers are compared for equality, so surrounding punctuation matters. */
    private fun identifierOf(value: String): String? = value
        .filter { it.isLetterOrDigit() }
        .uppercase()
        .takeIf { it.isNotBlank() }

    /**
     * Walks the direct children of an atom payload.
     *
     * Every bound is checked against [end] because this parses untrusted files:
     * a truncated or hostile container must yield fewer tags, never an exception
     * or an out-of-range read.
     */
    private fun forEachAtom(
        payload: ByteArray,
        start: Int,
        end: Int,
        action: (type: String, bodyStart: Int, bodyEnd: Int) -> Unit,
    ) {
        var offset = start
        while (offset + 8 <= end) {
            val declared = uint32(payload, offset)
            // Size 0 means "runs to the end of the parent"; size 1 means a 64-bit
            // size follows, which does not occur inside a metadata list.
            val atomEnd: Long = when {
                declared == 0L -> end.toLong()
                declared == 1L -> return
                declared < 8L -> return
                else -> offset.toLong() + declared
            }
            if (atomEnd > end || atomEnd <= offset) return
            action(atomType(payload, offset + 4), offset + 8, atomEnd.toInt())
            offset = atomEnd.toInt()
        }
    }

    /** Reads the value out of a tag atom's child `data` atom. */
    private fun dataValue(payload: ByteArray, start: Int, end: Int): String? {
        var result: String? = null
        forEachAtom(payload, start, end) { type, bodyStart, bodyEnd ->
            // A `data` atom is 4 bytes of version and type flags, then 4 reserved
            // locale bytes, then the value.
            if (type == "data" && result == null && bodyStart + 8 <= bodyEnd) {
                val wellKnownType = uint32(payload, bodyStart) and 0xFFFFFFL
                result = decode(wellKnownType, payload, bodyStart + 8, bodyEnd)
            }
        }
        return result
    }

    /**
     * Reads a freeform (`----`) tag, which carries its own namespace and name
     * rather than using a four-character code. ASIN and ISBN live here.
     */
    private fun freeformEntry(payload: ByteArray, start: Int, end: Int): Pair<String, String>? {
        var name: String? = null
        var value: String? = null
        forEachAtom(payload, start, end) { type, bodyStart, bodyEnd ->
            when (type) {
                // `mean` holds the namespace, e.g. com.apple.iTunes. Writers are
                // inconsistent about it, so the name alone is the key.
                "name" -> if (name == null) name = fullBoxText(payload, bodyStart, bodyEnd)
                "data" -> if (value == null && bodyStart + 8 <= bodyEnd) {
                    val wellKnownType = uint32(payload, bodyStart) and 0xFFFFFFL
                    value = decode(wellKnownType, payload, bodyStart + 8, bodyEnd)
                }
            }
        }
        val key = name?.trim()?.lowercase()?.takeIf { it.isNotEmpty() } ?: return null
        val text = value?.trim()?.takeIf { it.isNotEmpty() } ?: return null
        return key to text
    }

    /** `mean` and `name` are full boxes: four bytes of version and flags, then text. */
    private fun fullBoxText(payload: ByteArray, start: Int, end: Int): String? {
        if (start + 4 > end) return null
        return String(payload, start + 4, end - start - 4, Charsets.UTF_8)
            .trim()
            .takeIf { it.isNotEmpty() }
    }

    private fun decode(wellKnownType: Long, payload: ByteArray, start: Int, end: Int): String? {
        val length = end - start
        if (length <= 0) return null
        return when (wellKnownType) {
            TYPE_UTF8 -> String(payload, start, length, Charsets.UTF_8)
            TYPE_UTF16 -> String(payload, start, length, Charsets.UTF_16BE)
            TYPE_SIGNED_INT, TYPE_UNSIGNED_INT -> integer(payload, start, length)
            // Artwork and other binary payloads carry no identity information.
            else -> null
        }?.trim()?.takeIf { it.isNotEmpty() }
    }

    private fun integer(payload: ByteArray, start: Int, length: Int): String? {
        if (length !in 1..8) return null
        var value = 0L
        for (index in 0 until length) {
            value = (value shl 8) or (payload[start + index].toLong() and 0xFF)
        }
        return value.toString()
    }

    private fun uint32(payload: ByteArray, offset: Int): Long =
        ((payload[offset].toLong() and 0xFF) shl 24) or
            ((payload[offset + 1].toLong() and 0xFF) shl 16) or
            ((payload[offset + 2].toLong() and 0xFF) shl 8) or
            (payload[offset + 3].toLong() and 0xFF)

    /**
     * Atom codes are four bytes, and the common ones begin with the 0xA9
     * copyright byte. ISO-8859-1 maps that byte to "\u00A9" so the codes read
     * the way they do in the specification.
     */
    private fun atomType(payload: ByteArray, offset: Int): String =
        String(payload, offset, 4, Charsets.ISO_8859_1)
}
