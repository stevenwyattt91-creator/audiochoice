package com.audiochoice.mobile.narration.voice

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.MediaMuxer
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Concatenates the engine's per-utterance WAVs into one AAC chapter file.
 *
 * Two decisions shape this.
 *
 * Concatenation happens at the PCM level, before encoding, rather than by stitching
 * encoded containers together. `synthesizeToFile` hands back WAV, and joining WAVs
 * then encoding once is straightforward, whereas splicing AAC streams means dealing
 * with frame alignment and priming samples for no benefit.
 *
 * Per-unit boundaries come from the running sample count as audio is appended, not
 * from measuring the finished file. Sample counts are exact; measurement accumulates
 * error, and a few milliseconds per unit compounds into a highlight that sits a
 * sentence behind the voice by the end of a chapter.
 *
 * Speech at 24 kbps mono is the target. The content is one voice reading prose, so
 * spending more produces a bigger file a listener cannot hear the difference in -- and
 * a twelve-hour book is already a couple of hundred megabytes.
 */
class AacChapterAudioWriter(
    destination: File,
    private val sampleRate: Int = DEFAULT_SAMPLE_RATE,
    private val bitRate: Int = DEFAULT_BIT_RATE,
) : ChapterAudioWriter {

    private val muxer = MediaMuxer(destination.absolutePath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
    private val codec: MediaCodec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_AAC)
    private val bufferInfo = MediaCodec.BufferInfo()

    private var trackIndex = -1
    private var muxerStarted = false
    private var totalSamples = 0L
    private var closed = false

    init {
        val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, sampleRate, 1).apply {
            setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
            setInteger(MediaFormat.KEY_BIT_RATE, bitRate)
            setInteger(MediaFormat.KEY_CHANNEL_COUNT, 1)
            setInteger(MediaFormat.KEY_SAMPLE_RATE, sampleRate)
        }
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        codec.start()
    }

    /**
     * Append one WAV's samples and return the running duration.
     *
     * A WAV whose sample rate differs from the encoder's is resampled by nearest
     * neighbour. Engines do vary, and a mismatch left alone plays back at the wrong
     * pitch, which is far more noticeable than the artefacts of a crude resample on
     * speech.
     */
    override fun append(source: File): Long {
        check(!closed) { "Writer already closed" }
        val wav = WavReader.read(source) ?: return durationMs()

        val samples = when {
            wav.sampleRate == sampleRate -> wav.samples
            else -> resample(wav.samples, wav.sampleRate, sampleRate)
        }

        var offset = 0
        while (offset < samples.size) {
            val chunk = minOf(SAMPLES_PER_CHUNK, samples.size - offset)
            enqueue(samples, offset, chunk)
            offset += chunk
        }
        return durationMs()
    }

    override fun finish(): Long {
        if (closed) return durationMs()
        try {
            signalEndOfStream()
            drain(endOfStream = true)
        } finally {
            // Closed whatever happened. The codec and muxer hold native resources, and leaking
            // them takes the next chapter's render down with them.
            closeInternal()
        }
        return durationMs()
    }

    /**
     * Queues the end-of-stream marker, retrying until the codec takes it.
     *
     * The marker used to be queued once, on a single [MediaCodec.dequeueInputBuffer] that was
     * allowed to fail. When it did -- which happens whenever every input buffer is in flight at
     * the moment the chapter ends -- the marker was silently dropped, and [drain] then waited for
     * an end-of-stream flag that could never arrive. It span for ever. A render that hangs is
     * worse than one that fails, because there is nothing to report and waiting looks reasonable.
     */
    private fun signalEndOfStream() {
        val deadline = System.nanoTime() + STAGE_DEADLINE_NANOS
        while (true) {
            val inputIndex = codec.dequeueInputBuffer(TIMEOUT_US)
            if (inputIndex >= 0) {
                codec.queueInputBuffer(
                    inputIndex,
                    0,
                    0,
                    totalSamples * 1_000_000L / sampleRate,
                    MediaCodec.BUFFER_FLAG_END_OF_STREAM,
                )
                return
            }
            // Output has to be consumed for input buffers to come back, so the two are drained
            // together rather than waited on in turn.
            drain(endOfStream = false)
            check(System.nanoTime() < deadline) {
                "The audio encoder would not accept the end-of-stream marker"
            }
        }
    }

    override fun close() {
        if (!closed) closeInternal()
    }

    private fun durationMs(): Long = totalSamples * 1_000L / sampleRate

    private fun enqueue(samples: ShortArray, offset: Int, count: Int) {
        var written = 0
        val deadline = System.nanoTime() + STAGE_DEADLINE_NANOS
        while (written < count) {
            // A wedged codec must fail rather than spin. Without this the render never returns,
            // which surfaces as a book that says it is being prepared for ever -- the worst
            // possible failure, because it looks like patience is the answer.
            check(System.nanoTime() < deadline) {
                "The audio encoder stopped accepting input after " +
                    "${STAGE_DEADLINE_NANOS / 1_000_000_000} seconds"
            }
            val inputIndex = codec.dequeueInputBuffer(TIMEOUT_US)
            if (inputIndex >= 0) {
                val buffer = codec.getInputBuffer(inputIndex) ?: continue
                buffer.clear()
                val capacitySamples = buffer.capacity() / 2
                val take = minOf(capacitySamples, count - written)
                val shorts = buffer.order(ByteOrder.nativeOrder()).asShortBuffer()
                shorts.put(samples, offset + written, take)
                // Presentation time from the sample count, which is what makes the
                // timings exact rather than approximate.
                val presentationUs = totalSamples * 1_000_000L / sampleRate
                codec.queueInputBuffer(inputIndex, 0, take * 2, presentationUs, 0)
                totalSamples += take
                written += take
            }
            drain(endOfStream = false)
        }
    }

    /**
     * Moves encoded output into the muxer.
     *
     * With [endOfStream] set this waits for the terminating flag, so it must only be called after
     * [signalEndOfStream] has actually queued the marker. It carries its own deadline regardless:
     * an encoder that stops producing output should end the render with a message, not with a
     * thread that never comes back.
     */
    private fun drain(endOfStream: Boolean) {
        val deadline = System.nanoTime() + STAGE_DEADLINE_NANOS
        while (true) {
            check(System.nanoTime() < deadline) {
                "The audio encoder produced no output for " +
                    "${STAGE_DEADLINE_NANOS / 1_000_000_000} seconds"
            }
            val outputIndex = codec.dequeueOutputBuffer(bufferInfo, TIMEOUT_US)
            when {
                outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> if (!endOfStream) return
                outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    if (!muxerStarted) {
                        trackIndex = muxer.addTrack(codec.outputFormat)
                        muxer.start()
                        muxerStarted = true
                    }
                }

                outputIndex >= 0 -> {
                    val encoded = codec.getOutputBuffer(outputIndex)
                    if (encoded != null && bufferInfo.size > 0 && muxerStarted &&
                        bufferInfo.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0
                    ) {
                        encoded.position(bufferInfo.offset)
                        encoded.limit(bufferInfo.offset + bufferInfo.size)
                        muxer.writeSampleData(trackIndex, encoded, bufferInfo)
                    }
                    codec.releaseOutputBuffer(outputIndex, false)
                    if (bufferInfo.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) return
                }
            }
        }
    }

    private fun closeInternal() {
        closed = true
        runCatching { codec.stop() }
        runCatching { codec.release() }
        if (muxerStarted) runCatching { muxer.stop() }
        runCatching { muxer.release() }
    }

    private companion object {
        /**
         * 22.05 kHz is plenty for one speaking voice and halves the encoder's work
         * against 44.1 kHz.
         */
        const val DEFAULT_SAMPLE_RATE = 22_050
        const val DEFAULT_BIT_RATE = 24_000
        const val TIMEOUT_US = 10_000L
        const val SAMPLES_PER_CHUNK = 8_192

        /**
         * How long any one encoder stage may make no progress before the render fails.
         *
         * Generous, because a slow device encoding a long chapter is legitimate. The point is not
         * to catch slowness but to guarantee that a stuck codec ends as a reportable failure
         * rather than as a render that never returns.
         */
        const val STAGE_DEADLINE_NANOS = 30L * 1_000_000_000

        /**
         * Nearest-neighbour resampling.
         *
         * Crude by design. This only runs when an engine writes at a rate the encoder
         * is not configured for, and on speech the artefacts are inaudible next to the
         * alternative, which is playback at the wrong pitch.
         */
        fun resample(samples: ShortArray, from: Int, to: Int): ShortArray {
            if (from <= 0 || to <= 0 || from == to) return samples
            val outputLength = (samples.size.toLong() * to / from).toInt().coerceAtLeast(1)
            val output = ShortArray(outputLength)
            for (index in output.indices) {
                val source = (index.toLong() * from / to).toInt().coerceIn(0, samples.lastIndex)
                output[index] = samples[source]
            }
            return output
        }
    }
}

/**
 * Just enough WAV parsing to read what a speech engine writes.
 *
 * Deliberately narrow: it finds the format and data chunks and reads 16-bit PCM. A
 * general WAV decoder would handle compressed variants and multi-channel layouts that
 * no text-to-speech engine produces.
 */
internal object WavReader {

    data class Pcm(val samples: ShortArray, val sampleRate: Int, val channels: Int)

    fun read(file: File): Pcm? = runCatching {
        RandomAccessFile(file, "r").use { input ->
            val header = ByteArray(12)
            if (input.read(header) < 12) return null
            val riff = String(header, 0, 4, Charsets.US_ASCII)
            val wave = String(header, 8, 4, Charsets.US_ASCII)
            if (riff != "RIFF" || wave != "WAVE") return null

            var sampleRate = 0
            var channels = 1
            var bitsPerSample = 16

            while (input.filePointer < input.length() - 8) {
                val chunkHeader = ByteArray(8)
                if (input.read(chunkHeader) < 8) break
                val id = String(chunkHeader, 0, 4, Charsets.US_ASCII)
                val size = ByteBuffer.wrap(chunkHeader, 4, 4)
                    .order(ByteOrder.LITTLE_ENDIAN).int

                when (id) {
                    "fmt " -> {
                        val format = ByteArray(size)
                        input.readFully(format, 0, minOf(size, format.size))
                        val buffer = ByteBuffer.wrap(format).order(ByteOrder.LITTLE_ENDIAN)
                        buffer.short // audio format
                        channels = buffer.short.toInt().coerceAtLeast(1)
                        sampleRate = buffer.int
                        buffer.int // byte rate
                        buffer.short // block align
                        bitsPerSample = buffer.short.toInt()
                    }

                    "data" -> {
                        if (sampleRate <= 0 || bitsPerSample != 16) return null
                        val bytes = ByteArray(size.coerceAtMost((input.length() - input.filePointer).toInt()))
                        input.readFully(bytes)
                        val shorts = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer()
                        val interleaved = ShortArray(shorts.remaining())
                        shorts.get(interleaved)
                        val mono = if (channels <= 1) interleaved else downmix(interleaved, channels)
                        return Pcm(mono, sampleRate, 1)
                    }

                    else -> input.seek(input.filePointer + size + (size % 2))
                }
            }
            null
        }
    }.getOrNull()

    /** Averaging rather than dropping channels, so nothing pans to silence. */
    private fun downmix(interleaved: ShortArray, channels: Int): ShortArray {
        val frames = interleaved.size / channels
        val mono = ShortArray(frames)
        for (frame in 0 until frames) {
            var sum = 0
            for (channel in 0 until channels) sum += interleaved[frame * channels + channel]
            mono[frame] = (sum / channels).toShort()
        }
        return mono
    }
}
