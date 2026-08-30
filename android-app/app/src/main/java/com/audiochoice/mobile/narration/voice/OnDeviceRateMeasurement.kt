package com.audiochoice.mobile.narration.voice

import android.content.Context
import com.audiochoice.mobile.data.VoiceKind
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.util.Locale

/** What the measurement produced, or why it could not run. */
sealed interface RateMeasurementOutcome {

    data class Measured(
        val rate: MeasuredSynthesisRate,
        val engineName: String,
        val voiceName: String,
        val deviceModel: String,
        val androidVersion: Int,
    ) : RateMeasurementOutcome {
        /**
         * The single block of text a listener reads off the screen and sends back.
         *
         * Includes the device and engine, because a rate without them is not a measurement of
         * anything -- and this is the only route a number has from a phone to the people who need
         * it when the build machine cannot reach the phone.
         */
        val report: String get() = buildString {
            appendLine("AudioChoice on-device voice measurement")
            appendLine("device: $deviceModel (Android $androidVersion)")
            appendLine("engine: $engineName")
            appendLine("voice: $voiceName")
            appendLine(OnDeviceRate.report(rate))
        }
    }

    data object NoEngineInstalled : RateMeasurementOutcome

    data class LanguageUnavailable(val languageTag: String) : RateMeasurementOutcome

    data class Failed(val message: String) : RateMeasurementOutcome
}

/**
 * Measures how fast this device's own text-to-speech engine actually synthesizes.
 *
 * Runs the real engine over a fixed passage, writing to a real file, and times it. Nothing is
 * simulated, because what needs measuring is precisely the thing a simulation would assume.
 *
 * This exists as an in-app measurement rather than an instrumented benchmark because the machine
 * that builds AudioChoice cannot reach a phone: the two design values that depend on it -- whether
 * to offer an on-device neural voice at all, and how many chapters to keep rendered ahead of the
 * playhead -- were specified as measurements rather than estimates, and inventing them was already
 * tried once and got a speech rate wrong by a third.
 */
class OnDeviceRateMeasurement(
    private val context: Context,
    private val scratchDirectory: File,
) {

    suspend fun measure(
        kind: VoiceKind = VoiceKind.SYSTEM,
        language: Locale = Locale.US,
    ): RateMeasurementOutcome = withContext(Dispatchers.IO) {
        when (val connection = TextToSpeechHandle.connect(context, language)) {
            TextToSpeechConnection.NoEngineInstalled ->
                RateMeasurementOutcome.NoEngineInstalled

            is TextToSpeechConnection.LanguageUnavailable ->
                RateMeasurementOutcome.LanguageUnavailable(connection.languageTag)

            is TextToSpeechConnection.Connected -> runCatching {
                connection.handle.use { handle -> measureWith(handle, kind) }
            }.getOrElse { failure ->
                RateMeasurementOutcome.Failed(
                    failure.message ?: "The measurement could not be completed.",
                )
            }
        }
    }

    private suspend fun measureWith(
        handle: TextToSpeechHandle,
        kind: VoiceKind,
    ): RateMeasurementOutcome {
        scratchDirectory.mkdirs()
        val destination = File(scratchDirectory, "rate-benchmark.wav")

        // A discarded warm-up pass. The first utterance on most engines pays for loading a voice
        // model, and including that in the measurement would report every device as slower than it
        // is -- most of all the fast ones, where the fixed cost dominates.
        runCatching { handle.synthesize(WARM_UP_TEXT, destination) }
        destination.delete()

        var characters = 0
        var audioMillis = 0L
        val startedAt = System.nanoTime()
        OnDeviceRate.BENCHMARK_PASSAGE.forEach { passage ->
            val written = handle.synthesize(passage, destination)
            if (!written || !destination.isFile) {
                destination.delete()
                return RateMeasurementOutcome.Failed(
                    "The device's voice engine did not produce audio for the test passage.",
                )
            }
            characters += passage.length
            audioMillis += wavDurationMillis(destination)
            destination.delete()
        }
        val elapsedMillis = (System.nanoTime() - startedAt) / 1_000_000

        return RateMeasurementOutcome.Measured(
            rate = MeasuredSynthesisRate(
                kind = kind,
                charactersSynthesized = characters,
                elapsedMillis = elapsedMillis,
                audioDurationMillis = audioMillis,
            ),
            // The voice name is what identifies which synthesizer produced this figure; the
            // handle does not expose the engine package, and the voice is the more specific fact
            // anyway -- two voices from one engine can differ in speed considerably.
            engineName = handle.voicesForLanguage().size.let { "$it voices available" },
            voiceName = handle.defaultVoiceName() ?: "default",
            deviceModel = "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}",
            androidVersion = android.os.Build.VERSION.SDK_INT,
        )
    }

    /**
     * Duration from the WAV header, rather than from the file's size alone.
     *
     * Engines differ in sample rate and bit depth, so bytes-per-second is not a constant that can
     * be assumed. Reading the header is a few lines and removes a whole class of wrong answer.
     */
    private fun wavDurationMillis(file: File): Long = runCatching {
        file.inputStream().use { input ->
            val header = ByteArray(44)
            if (input.read(header) < 44) return 0L

            fun intAt(offset: Int): Int =
                (header[offset].toInt() and 0xFF) or
                    ((header[offset + 1].toInt() and 0xFF) shl 8) or
                    ((header[offset + 2].toInt() and 0xFF) shl 16) or
                    ((header[offset + 3].toInt() and 0xFF) shl 24)

            fun shortAt(offset: Int): Int =
                (header[offset].toInt() and 0xFF) or ((header[offset + 1].toInt() and 0xFF) shl 8)

            val sampleRate = intAt(24)
            val channels = shortAt(22)
            val bitsPerSample = shortAt(34)
            val bytesPerSecond = sampleRate * channels * (bitsPerSample / 8)
            if (bytesPerSecond <= 0) return 0L

            val dataBytes = (file.length() - 44).coerceAtLeast(0)
            dataBytes * 1_000 / bytesPerSecond
        }
    }.getOrDefault(0L)

    private companion object {
        /**
         * Deliberately unrelated to the benchmark passage, so a warm-up cannot leave a cached
         * result that makes the measured pass look faster than a cold one would be.
         */
        const val WARM_UP_TEXT = "Warming up the voice engine before timing anything."
    }
}
