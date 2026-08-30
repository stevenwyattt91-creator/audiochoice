package com.audiochoice.mobile.narration.voice

import android.content.Context
import android.os.Bundle
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import android.speech.tts.Voice
import com.audiochoice.mobile.data.VoiceKind
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.File
import java.util.Locale
import java.util.concurrent.atomic.AtomicLong
import kotlin.coroutines.resume

/**
 * The free, offline voice: Android's own text-to-speech engine.
 *
 * Always available, always the fallback, and never a network request. That last part
 * is not incidental -- the free tiers are offered on the promise that a listener's
 * book does not leave their device, so synthesis explicitly asks for embedded
 * synthesis rather than merely hoping the engine stays local.
 *
 * Rate and pitch are pinned at 1.0. The player already has a speed control, and a
 * second speed applied at synthesis time would bake a rate into the audio that the
 * listener could then never undo without re-rendering the whole book.
 */
class SystemVoiceEngine(
    private val speech: TextToSpeechHandle,
    override val voiceID: String,
    private val writerFactory: (File) -> ChapterAudioWriter,
    private val scratchDirectory: File,
) : VoiceEngine {

    override val kind: VoiceKind = VoiceKind.SYSTEM

    override val maximumInputCharacters: Int = speech.maximumInputCharacters

    private val renderer = SynthesisChapterRenderer(
        synthesizer = speech,
        writerFactory = writerFactory,
        scratchFile = { index -> File(scratchDirectory, "utterance_$index.wav") },
    )

    override suspend fun renderChapter(request: ChapterRenderRequest): ChapterRenderOutcome {
        scratchDirectory.mkdirs()
        return try {
            renderer.render(request)
        } finally {
            // Utterance WAVs are large and short-lived. Leaving them behind would let a
            // long book quietly occupy several times its own final size.
            scratchDirectory.listFiles()?.forEach { it.delete() }
        }
    }
}

/**
 * A connected `TextToSpeech` instance, wrapped so the rest of narration never touches
 * the framework's callback shape.
 *
 * The framework reports completion through a listener keyed by utterance id, which is
 * awkward to use and easy to leak. Bridging it into a suspending call once, here, is
 * what lets the render policy above be ordinary sequential code.
 */
class TextToSpeechHandle private constructor(
    private val engine: TextToSpeech,
    private val language: Locale,
) : SpeechSynthesizer, AutoCloseable {

    private val utteranceCounter = AtomicLong(0)

    override val maximumInputCharacters: Int =
        runCatching { TextToSpeech.getMaxSpeechInputLength() }
            .getOrDefault(DEFAULT_MAXIMUM_INPUT)
            .coerceAtLeast(MINIMUM_MAXIMUM_INPUT)

    /** Voices the engine offers for this book's language. */
    fun voicesForLanguage(): List<Voice> = runCatching {
        engine.voices.orEmpty()
            .filter { it.locale.language.equals(language.language, ignoreCase = true) }
            .sortedBy { it.name }
    }.getOrDefault(emptyList())

    fun defaultVoiceName(): String? = runCatching {
        engine.voice?.takeIf { it.locale.language.equals(language.language, ignoreCase = true) }?.name
            ?: voicesForLanguage().firstOrNull()?.name
    }.getOrNull()

    fun selectVoice(name: String): Boolean = runCatching {
        val voice = engine.voices.orEmpty().firstOrNull { it.name == name } ?: return false
        engine.setVoice(voice) == TextToSpeech.SUCCESS
    }.getOrDefault(false)

    override suspend fun synthesize(text: String, destination: File): Boolean =
        withContext(Dispatchers.IO) {
            destination.parentFile?.mkdirs()
            val utteranceID = "audiochoice-${utteranceCounter.incrementAndGet()}"

            suspendCancellableCoroutine { continuation ->
                val listener = object : UtteranceProgressListener() {
                    override fun onStart(id: String?) = Unit

                    override fun onDone(id: String?) {
                        if (id == utteranceID && continuation.isActive) continuation.resume(true)
                    }

                    @Deprecated("Required by the framework's abstract class.")
                    override fun onError(id: String?) {
                        if (id == utteranceID && continuation.isActive) continuation.resume(false)
                    }

                    override fun onError(id: String?, errorCode: Int) {
                        if (id == utteranceID && continuation.isActive) continuation.resume(false)
                    }
                }
                engine.setOnUtteranceProgressListener(listener)
                continuation.invokeOnCancellation { runCatching { engine.stop() } }

                val parameters = Bundle().apply {
                    // Ask for offline synthesis explicitly. Some engines will reach the
                    // network for a better voice given the chance, and the free tier
                    // promises the book stays on the device.
                    putString(TextToSpeech.Engine.KEY_FEATURE_EMBEDDED_SYNTHESIS, "true")
                }

                val queued = runCatching {
                    engine.synthesizeToFile(text, parameters, destination, utteranceID)
                }.getOrDefault(TextToSpeech.ERROR)

                if (queued != TextToSpeech.SUCCESS && continuation.isActive) {
                    continuation.resume(false)
                }
            }
        }

    override fun close() {
        runCatching { engine.stop() }
        runCatching { engine.shutdown() }
    }

    companion object {
        /** The framework's own ceiling, used when it cannot be read. */
        private const val DEFAULT_MAXIMUM_INPUT = 4_000
        private const val MINIMUM_MAXIMUM_INPUT = 40

        const val INITIALISATION_TIMEOUT_MS = 5_000L

        /**
         * Connect to the device's engine, or report why not.
         *
         * The timeout exists because engine initialisation is asynchronous and, on
         * some devices with a broken or mid-update engine, simply never calls back.
         * Waiting forever would leave the import spinner turning with nothing to say.
         */
        suspend fun connect(context: Context, language: Locale): TextToSpeechConnection {
            val ready = CompletableDeferred<Int>()
            var engine: TextToSpeech? = null
            engine = runCatching {
                TextToSpeech(context.applicationContext) { status -> ready.complete(status) }
            }.getOrNull() ?: return TextToSpeechConnection.NoEngineInstalled

            val status = withTimeoutOrNull(INITIALISATION_TIMEOUT_MS) { ready.await() }
            if (status == null || status != TextToSpeech.SUCCESS) {
                runCatching { engine.shutdown() }
                return TextToSpeechConnection.NoEngineInstalled
            }

            // Pinned so the player's speed control stays the only place speed is set.
            runCatching { engine.setSpeechRate(1.0f) }
            runCatching { engine.setPitch(1.0f) }

            val languageStatus = runCatching { engine.setLanguage(language) }
                .getOrDefault(TextToSpeech.LANG_NOT_SUPPORTED)
            if (languageStatus == TextToSpeech.LANG_MISSING_DATA ||
                languageStatus == TextToSpeech.LANG_NOT_SUPPORTED
            ) {
                runCatching { engine.shutdown() }
                return TextToSpeechConnection.LanguageUnavailable(language.toLanguageTag())
            }

            val handle = TextToSpeechHandle(engine, language)
            if (handle.voicesForLanguage().isEmpty() && handle.defaultVoiceName() == null) {
                handle.close()
                return TextToSpeechConnection.LanguageUnavailable(language.toLanguageTag())
            }
            return TextToSpeechConnection.Connected(handle)
        }
    }
}

/**
 * The outcome of trying to reach the device's speech engine.
 *
 * Three cases rather than a nullable handle, because the listener needs different
 * words for each: no engine at all, an engine that has nothing for this book's
 * language, and success.
 */
sealed interface TextToSpeechConnection {
    data class Connected(val handle: TextToSpeechHandle) : TextToSpeechConnection

    /** No engine, or one that never finished initialising. */
    data object NoEngineInstalled : TextToSpeechConnection

    /** An engine is present but has no voice for the book's declared language. */
    data class LanguageUnavailable(val languageTag: String) : TextToSpeechConnection
}
