#!/usr/bin/env python3
"""Rebuild an audiobook's private transcript on the Lambda host's GPU.

Transcription needs the audio, and the server deletes it once a scan completes, so
a transcript lost to a storage fault cannot be regenerated from the backend alone.
This takes a local copy of the audio, transcribes it with the host-local Whisper
service, and stores the result against an existing edition through the scanner's
admin API -- which derives the storage key and writes the payload exactly as the
scan pipeline would.

Run it on the Lambda host, from the repository root:

    python3 scripts/rebuild-lambda-transcript.py "King Sorrow" /path/to/audiobook.m4b

Only the audio is read locally; the transcript never touches this machine's disk.
"""
from __future__ import annotations

import argparse
import json
import mimetypes
import os
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path

API_BASE = os.environ.get("AUDIOCHOICE_LAMBDA_API", "http://127.0.0.1:8080").rstrip("/")
WHISPER_BASE = os.environ.get("AUDIOCHOICE_WHISPER_API", "http://127.0.0.1:8001").rstrip("/")
ENV_PATH = Path("deploy/lambda/lambda-worker.env")

# Matches the pipeline's own chunking so segment timings line up with what a normal
# scan would have produced.
CHUNK_SECONDS = int(os.environ.get("AUDIOCHOICE_CHUNK_SECONDS", "600"))
SAMPLE_RATE = 16000


def api_token() -> str:
    token = os.environ.get("AUDIOCHOICE_API_TOKEN", "").strip()
    if token:
        return token
    if ENV_PATH.exists():
        for raw_line in ENV_PATH.read_text(encoding="utf-8").splitlines():
            key, separator, value = raw_line.partition("=")
            if separator and key.strip() == "AudioChoice__ApiToken":
                return value.strip().strip('"').strip("'")
    raise SystemExit(
        "No admin token found. Set AudioChoice__ApiToken in "
        "deploy/lambda/lambda-worker.env and recreate the scanner container."
    )


def api_json(path: str, token: str, payload: dict | None = None):
    headers = {"Authorization": f"Bearer {token}"}
    data = None
    method = "GET"
    if payload is not None:
        method = "POST"
        headers["Content-Type"] = "application/json"
        data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(f"{API_BASE}{path}", data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", errors="replace")
        raise SystemExit(f"{method} {path} failed with HTTP {error.code}: {body or error.reason}")
    except urllib.error.URLError as error:
        raise SystemExit(f"Cannot reach the scanner at {API_BASE}: {error.reason}")


def pick_edition(editions: list[dict], title_query: str) -> dict:
    query = "".join(character for character in title_query.lower() if character.isalnum())
    matches = []
    for edition in editions:
        fingerprint = edition.get("fingerprint") or {}
        work_title = (fingerprint.get("workTitle") or "").lower()
        compact = "".join(character for character in work_title if character.isalnum())
        if query and query in compact:
            matches.append(edition)

    if not matches:
        print("No edition matched. Known editions:", file=sys.stderr)
        for edition in editions:
            fingerprint = edition.get("fingerprint") or {}
            print(
                f"  {fingerprint.get('workTitle')!r} "
                f"duration={fingerprint.get('duration')} "
                f"hasTranscript={edition.get('hasTranscript')}",
                file=sys.stderr,
            )
        raise SystemExit("Refine the title and try again.")

    if len(matches) > 1:
        print("Several editions matched. Be more specific:", file=sys.stderr)
        for edition in matches:
            fingerprint = edition.get("fingerprint") or {}
            print(
                f"  {fingerprint.get('workTitle')!r} "
                f"duration={fingerprint.get('duration')} "
                f"sha256={(fingerprint.get('sha256') or '')[:12]}...",
                file=sys.stderr,
            )
        raise SystemExit("Ambiguous title.")

    return matches[0]


def audio_duration_seconds(path: Path) -> float:
    output = subprocess.run(
        [
            "ffprobe", "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", str(path),
        ],
        capture_output=True, text=True, check=True,
    )
    return float(output.stdout.strip())


def transcribe_chunk(path: Path) -> list[dict]:
    """POSTs one chunk to the host-local Whisper service as multipart/form-data."""
    boundary = uuid.uuid4().hex
    content_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
    body = bytearray()
    body += f"--{boundary}\r\n".encode()
    body += f'Content-Disposition: form-data; name="file"; filename="{path.name}"\r\n'.encode()
    body += f"Content-Type: {content_type}\r\n\r\n".encode()
    body += path.read_bytes()
    body += f"\r\n--{boundary}\r\n".encode()
    body += b'Content-Disposition: form-data; name="language"\r\n\r\nen\r\n'
    body += f"--{boundary}--\r\n".encode()

    request = urllib.request.Request(
        f"{WHISPER_BASE}/transcribe",
        data=bytes(body),
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )
    try:
        # A single GPU chunk can take minutes under load; the scanner allows 900s.
        with urllib.request.urlopen(request, timeout=1800) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        body_text = error.read().decode("utf-8", errors="replace")
        raise SystemExit(f"Whisper failed with HTTP {error.code}: {body_text or error.reason}")
    except urllib.error.URLError as error:
        raise SystemExit(f"Cannot reach Whisper at {WHISPER_BASE}: {error.reason}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("title", help="Enough of the audiobook title to identify it")
    parser.add_argument("audio", type=Path, help="Local path to the audiobook file")
    parser.add_argument(
        "--force", action="store_true",
        help="Rebuild even when timing data already exists for this edition",
    )
    arguments = parser.parse_args()

    if not arguments.audio.is_file():
        raise SystemExit(f"No such file: {arguments.audio}")

    token = api_token()
    editions = api_json("/v1/admin/editions", token)
    edition = pick_edition(editions, arguments.title)
    fingerprint = edition["fingerprint"]

    print(f"Edition: {fingerprint.get('workTitle')} ({fingerprint.get('duration')}s)")
    if edition.get("hasTranscript") and not arguments.force:
        raise SystemExit(
            f"This edition already has {edition.get('segmentCount')} segments of timing "
            "data. Pass --force to replace it."
        )

    total_seconds = audio_duration_seconds(arguments.audio)
    print(f"Audio duration {total_seconds:.0f}s, {CHUNK_SECONDS}s chunks on {WHISPER_BASE}")

    segments: list[dict] = []
    model_name = "faster-whisper"
    chunk_count = int(total_seconds // CHUNK_SECONDS) + 1

    with tempfile.TemporaryDirectory() as workspace:
        for index in range(chunk_count):
            start = index * CHUNK_SECONDS
            if start >= total_seconds:
                break
            chunk_path = Path(workspace) / f"chunk-{index:04d}.wav"
            # Mono 16 kHz WAV is what the pipeline feeds Whisper.
            subprocess.run(
                [
                    "ffmpeg", "-nostdin", "-loglevel", "error", "-y",
                    "-ss", str(start), "-t", str(CHUNK_SECONDS),
                    "-i", str(arguments.audio),
                    "-ac", "1", "-ar", str(SAMPLE_RATE), "-vn",
                    str(chunk_path),
                ],
                check=True,
            )
            result = transcribe_chunk(chunk_path)
            model_name = result.get("model", model_name)
            for segment in result.get("segments", []):
                text = (segment.get("text") or "").strip()
                if not text:
                    continue
                segments.append({
                    "startTime": float(segment["start"]) + start,
                    "endTime": float(segment["end"]) + start,
                    "text": text,
                })
            chunk_path.unlink(missing_ok=True)
            print(
                f"  chunk {index + 1}/{chunk_count} at {start:.0f}s "
                f"-> {len(segments)} segments so far",
                flush=True,
            )

    if not segments:
        raise SystemExit("Whisper returned no speech. Check the audio file.")

    segments.sort(key=lambda segment: segment["startTime"])
    transcript = {
        "version": "1.0",
        "language": "en",
        "transcriptionModel": model_name,
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "segments": segments,
        "isComplete": True,
    }

    response = api_json(
        "/v1/admin/transcripts", token,
        payload={"fingerprint": fingerprint, "transcript": transcript},
    )
    print(
        f"Stored {response.get('segmentCount')} segments covering "
        f"{float(response.get('coverageSeconds', 0)):.0f}s. "
        "Re-sync the reading edition in the app."
    )


if __name__ == "__main__":
    main()
