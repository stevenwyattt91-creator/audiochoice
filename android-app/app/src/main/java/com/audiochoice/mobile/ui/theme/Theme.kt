package com.audiochoice.mobile.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable

private val DarkColorScheme = darkColorScheme(
    primary = ChoiceGreen,
    onPrimary = ChoiceBlack,
    primaryContainer = ChoiceGreenDark,
    background = ChoiceBlack,
    onBackground = ChoiceText,
    surface = ChoiceSurface,
    onSurface = ChoiceText,
    surfaceVariant = ChoiceSurfaceRaised,
    onSurfaceVariant = ChoiceMuted,
    outline = ChoiceOutline,
    error = ChoiceError,
)

@Composable
fun AudioChoiceTheme(
    content: @Composable () -> Unit
) {
    MaterialTheme(
        colorScheme = DarkColorScheme,
        typography = Typography,
        content = content
    )
}
