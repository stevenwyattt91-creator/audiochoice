package com.audiochoice.mobile.ui.theme

import androidx.compose.runtime.Composable
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import com.audiochoice.mobile.R
import com.audiochoice.mobile.reader.ReaderFont

/**
 * OpenDyslexic, bundled from the SIL Open Font License release.
 *
 * The licence text ships in assets/licenses/OpenDyslexic-OFL.txt, which the OFL
 * requires to travel with the font. Bundled rather than fetched so the reader keeps
 * working offline and the typeface cannot vanish from under a listener who depends
 * on it.
 */
private val OpenDyslexic = FontFamily(
    Font(R.font.opendyslexic_regular, FontWeight.Normal),
    Font(R.font.opendyslexic_bold, FontWeight.Bold),
)

/**
 * @return null for the system face, which lets Text keep honouring the reader's own
 *   font settings rather than pinning a family.
 */
@Composable
fun readerFontFamily(font: ReaderFont): FontFamily? = when (font) {
    ReaderFont.SYSTEM -> null
    ReaderFont.OPEN_DYSLEXIC -> OpenDyslexic
}
