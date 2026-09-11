#!/usr/bin/env python3
"""Transcribes a local audio file into the same shape the production pipeline uses --
600-second chunks with 2-second overlap (matching FfmpegAudioChunker's defaults), word-level
timestamps via faster-whisper -- for building new ground-truth eval cases from a book that has
never been scanned before.

Not part of the production pipeline. This exists only to produce a real transcript.json (same
shape as /v1/admin/transcripts/content returns) for a brand-new book, entirely offline, so
scripts/eval/ scripts can mine it for real candidate windows the same way they already do for
books that went through the real scanner. Run this on the Lambda host (or any machine with
ffmpeg/ffprobe and the faster-whisper server reachable) -- it calls the SAME whisper-server.py
/transcribe endpoint the production scanner calls, so results match production transcription
exactly, not a different local model.

Usage (on the Lambda host, where the whisper service is already running on :8001):
    python3 scripts/eval/transcribe_for_eval.py /tmp/some_book.m4a /tmp/some_book_transcript.json

This can take a while for a long book -- each 600s chunk is a real transcription call.
"""
import argparse
import json
import subprocess
import sys
import tempfile
import os
from pathlib import Path

import urllib.request
import urllib.error

CHUNK_DURATION_SECONDS = 600.0
OVERLAP_SECONDS = 2.0
SAMPLE_RATE = 16_000


def probe_duration(ffprobe_path: str, audio_path: str) -> float:
    result = subprocess.run(
        [ffprobe_path, "-v", "error", "-select_streams", "a:0",
         "-show_entries", "stream=duration", "-of", "default=noprint_wrappers=1:nokey=1",
         audio_path],
        capture_output=True, text=True, check=True,
    )
    return float(result.stdout.strip())


def create_chunk(ffmpeg_path: str, audio_path: str, chunk_path: str,
                  start: float, duration: float) -> None:
    subprocess.run(
        [ffmpeg_path, "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
         "-ss", str(start), "-t", str(duration), "-i", audio_path,
         "-vn", "-ac", "1", "-ar", str(SAMPLE_RATE), "-c:a", "pcm_s16le", chunk_path],
        check=True,
    )


def transcribe_chunk(whisper_url: str, chunk_path: str, timeout: int) -> dict:
    boundary = "----AudioChoiceEvalBoundary"
    with open(chunk_path, "rb") as handle:
        audio_bytes = handle.read()

    def field(name: str, value: str) -> bytes:
        return (
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n"
            f"{value}\r\n"
        ).encode("utf-8")

    body = bytearray()
    body += field("language", "en")
    body += field("word_timestamps", "true")
    body += field("use_fallback", "false")
    body += f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; " \
            f"filename=\"chunk.wav\"\r\nContent-Type: audio/wav\r\n\r\n".encode("utf-8")
    body += audio_bytes
    body += f"\r\n--{boundary}--\r\n".encode("utf-8")

    request = urllib.request.Request(
        f"{whisper_url.rstrip('/')}/transcribe",
        data=bytes(body),
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                      formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("audio_path")
    parser.add_argument("output_path")
    parser.add_argument("--whisper-url", default="http://127.0.0.1:8001")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    parser.add_argument("--chunk-timeout", type=int, default=900,
                         help="Per-chunk transcription timeout in seconds, matching the "
                              "production AudioChoice__OpenAI__FasterWhisperTimeoutSeconds.")
    args = parser.parse_args()

    if not Path(args.audio_path).exists():
        parser.error(f"Audio file not found: {args.audio_path}")

    duration = probe_duration(args.ffprobe, args.audio_path)
    print(f"Duration: {duration:.1f}s ({duration / 3600:.2f}h)")

    step = CHUNK_DURATION_SECONDS - OVERLAP_SECONDS
    all_segments = []
    index = 0
    start = 0.0
    with tempfile.TemporaryDirectory() as tmp_dir:
        while start < duration:
            end = min(start + CHUNK_DURATION_SECONDS, duration)
            chunk_path = os.path.join(tmp_dir, f"chunk-{index:06d}.wav")
            print(f"Chunk {index}: {start:.1f}s - {end:.1f}s ... ", end="", flush=True)
            create_chunk(args.ffmpeg, args.audio_path, chunk_path, start, end - start)
            try:
                result = transcribe_chunk(args.whisper_url, chunk_path, args.chunk_timeout)
            except (urllib.error.URLError, urllib.error.HTTPError) as error:
                print(f"FAILED: {error}")
                os.remove(chunk_path)
                return 1
            os.remove(chunk_path)

            chunk_segment_count = 0
            for segment in result["segments"]:
                segment_start = segment["start"] + start
                segment_end = segment["end"] + start
                words = [
                    {
                        "text": word["word"].strip(),
                        "startTime": word["start"] + start,
                        "endTime": word["end"] + start,
                    }
                    for word in segment.get("words", [])
                ]
                all_segments.append({
                    "startTime": segment_start,
                    "endTime": segment_end,
                    "text": segment["text"].strip(),
                    "words": words,
                })
                chunk_segment_count += 1
            print(f"{chunk_segment_count} segments (model={result.get('model')}, "
                  f"{result.get('elapsedSeconds', 0):.1f}s)")

            index += 1
            if end >= duration:
                break
            start += step

    # Overlapping chunks produce duplicate segments in the overlap window; the production
    # pipeline's own transcript-merging logic dedupes these downstream. For this eval-only
    # transcript, a simple time-sorted list with obvious near-duplicate segments left in is
    # fine -- the eval scripts pick explicit windows by timestamp and read segment text
    # directly, so a handful of overlap-window duplicates do not affect correctness, only
    # add a little noise a human skimming the raw transcript would notice.
    all_segments.sort(key=lambda s: s["startTime"])

    output = {
        "version": 1,
        "language": "en",
        "transcriptionModel": "large-v3-turbo",
        "segments": all_segments,
        "isComplete": True,
        "checkpoints": [],
    }

    with open(args.output_path, "w") as handle:
        json.dump(output, handle)

    print(f"\nWrote {len(all_segments)} segments to {args.output_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
