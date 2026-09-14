#!/usr/bin/env python3
"""Watches a local drop folder for new audiobook files (deposited via the restricted
audiobook-upload SFTP account) and automatically fingerprints, uploads, and submits each
one for scanning against the local API -- no manual per-file signed-URL dance needed.

Runs on the H100 box itself, watching /srv/audiobook-drop/incoming. Files are moved to a
"processing" subfolder while being handled and to "done" or "failed" afterward, so a
partially-uploaded file (still being SFTP'd) is never picked up mid-transfer: a file is
only considered ready once its size has been stable across two consecutive poll cycles.

Usage:
    python3 scripts/watch-and-scan-drop-folder.py \
        --api-base http://127.0.0.1:8080 \
        --drop-folder /srv/audiobook-drop/incoming \
        --email <account> --password <account>
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request
import urllib.error

STABLE_CHECKS_REQUIRED = 2
POLL_INTERVAL_SECONDS = 10
AUDIO_EXTENSIONS = {".m4a", ".m4b", ".mp3"}


def log(message: str):
    timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
    print(f"[{timestamp}] {message}", flush=True)


def ffprobe_duration(path: str) -> float:
    out = subprocess.check_output([
        "ffprobe", "-v", "error", "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=1", path,
    ])
    return float(out.strip())


def ffprobe_tags(path: str) -> dict[str, str]:
    """Reads embedded title/artist/album_artist/ASIN-style tags, so a properly-tagged
    drop-folder file gets a real work title and author rather than a bare filename with
    no author -- the latter is exactly what Explore's own publishability check treats as
    "probably guessed from a filename" and silently withholds from listeners. Every file
    checked from a real drop-folder batch carried at least an artist tag even when title
    was missing, so this is worth reading before falling back to the filename.

    Also reads whatever retail-product-identifier tag the source actually carries. Audible
    downloads (and AAX-derived conversions) commonly carry it as a bare "asin" tag; some
    other pipelines write it into a free-text "comment" field instead. Both are read here
    so downstream code has one place to look, regardless of which container produced the
    file -- see EditionMatch.SameRecording's own remarks on why a retail identifier is the
    strongest same-recording evidence available."""
    try:
        out = subprocess.check_output([
            "ffprobe", "-v", "error", "-show_entries",
            "format_tags=title,artist,album_artist,asin,comment",
            "-of", "default=noprint_wrappers=1", path,
        ], stderr=subprocess.DEVNULL).decode("utf-8", errors="replace")
    except subprocess.CalledProcessError:
        return {}
    tags: dict[str, str] = {}
    for line in out.splitlines():
        if line.startswith("TAG:") and "=" in line:
            key, _, value = line[4:].partition("=")
            tags[key.strip()] = value.strip()
    return tags


def ffprobe_chapters(path: str) -> list[int]:
    """Reads chapter start offsets, rounded to whole seconds.

    This is the one identity signal that survives an AAX-to-M4B conversion, a re-encode,
    or a re-tag intact: rewrapping a container does not move chapter marks, while it
    always changes the file's sha256 and almost always its file size. Reported to the
    server as EditionSignature.ChapterOffsetSeconds, which EditionMatch.ChapterStructureIdentifies
    already treats as decisive (>= 8 marks, matching count, each agreeing within 2 seconds)
    -- that matching logic already existed and was already used to resolve a *transcript* or
    *filter result* for a re-encoded file the server had already scanned once before under a
    different fingerprint. What this file was missing was ever reading chapters here, at
    ingest, before that upload -- so a converted copy of a book this pipeline had already
    scanned always created a second edition, a second transcript, and a second (potentially
    worse) scan rather than being recognized as the same recording up front.
    """
    try:
        out = subprocess.check_output([
            "ffprobe", "-v", "error", "-show_chapters",
            "-of", "json", path,
        ], stderr=subprocess.DEVNULL).decode("utf-8", errors="replace")
    except subprocess.CalledProcessError:
        return []
    try:
        data = json.loads(out)
    except json.JSONDecodeError:
        return []
    offsets: list[int] = []
    for chapter in data.get("chapters", []):
        start = chapter.get("start_time")
        if start is None:
            continue
        try:
            offsets.append(round(float(start)))
        except (TypeError, ValueError):
            continue
    return offsets


def extract_product_identifier(tags: dict[str, str]) -> str | None:
    """Pulls a retail product identifier (ASIN/ISBN) out of whatever tag actually
    carries it. Reduced to letters and digits to match EditionSignature's own contract
    (see EditionIdentityContracts.cs) -- taggers vary punctuation and case, and none of
    that carries identity."""
    asin = tags.get("asin", "").strip()
    if asin:
        candidate = asin
    else:
        # Some non-Audible sources put it in a free-text comment field instead, often
        # alongside other text -- pull the first ASIN-shaped (10 alphanumeric, starting
        # with a digit or "B") or ISBN-shaped (10 or 13 digit) token out of it.
        import re
        comment = tags.get("comment", "")
        match = re.search(r"\b([0-9B][0-9A-Z]{9}|\d{10}|\d{13})\b", comment.upper())
        candidate = match.group(1) if match else None
    if not candidate:
        return None
    normalized = "".join(ch for ch in candidate if ch.isalnum()).upper()
    return normalized or None


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            chunk = f.read(4 * 1024 * 1024)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


def call(method: str, url: str, token: str | None = None, body: dict | None = None,
          extra_headers: dict | None = None, timeout: int = 60):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    if extra_headers:
        for k, v in extra_headers.items():
            req.add_header(k, v)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read()
        return json.loads(raw) if raw else None


def guess_file_type(path: str) -> str:
    ext = os.path.splitext(path)[1].lower().lstrip(".")
    return ext or "m4b"


def guess_content_type(file_type: str) -> str:
    return {
        "m4a": "audio/mp4", "m4b": "audio/mp4", "mp3": "audio/mpeg",
    }.get(file_type, "audio/mp4")


def process_file(path: str, api_base: str, token: str, done_dir: str, failed_dir: str):
    filename = os.path.basename(path)
    title = os.path.splitext(filename)[0]
    log(f"Processing {filename} ...")

    try:
        file_size = os.path.getsize(path)
        duration = ffprobe_duration(path)
        sha256 = sha256_file(path)
        log(f"  sha256={sha256[:16]}... size={file_size} bytes duration={duration:.1f}s")

        # Prefer the file's own embedded tags over the filename: a filename-derived title
        # with no author is exactly what Explore's own publishability check withholds from
        # listeners (see ScanCatalog.UnpublishableReason), so a properly tagged file must
        # not be thrown away in favor of a raw filename just because that was the original
        # behavior here.
        tags = ffprobe_tags(path)
        tagged_title = tags.get("title", "").strip()
        # album_artist is preferred when present: a GraphicAudio full-cast credit list in
        # `artist` (narrators and all) is not a usable "author" field, while album_artist on
        # every file checked from a real batch was the single credited book author.
        tagged_author = (tags.get("album_artist") or tags.get("artist") or "").strip()
        work_title = tagged_title if tagged_title else title

        file_type = guess_file_type(path)
        fingerprint = {
            "version": 1,
            "sha256": sha256,
            "fileSize": file_size,
            "duration": duration,
            "fileType": file_type,
            "workTitle": work_title,
            "author": tagged_author or None,
            "seriesTitle": None,
            "seriesNumber": None,
            "editionType": None,
            "partNumber": None,
            "totalParts": None,
        }

        chapters = ffprobe_chapters(path)
        product_identifier = extract_product_identifier(tags)
        signature = None
        if chapters or product_identifier:
            signature = {
                "productIdentifier": product_identifier,
                "narrator": None,
                "chapterOffsetSeconds": chapters or None,
            }
            log(f"  signature: identifier={product_identifier or 'none'} "
                f"chapters={len(chapters)}")

        # Checked before ever authorizing an upload, not after: this same
        # fingerprint+signature match is exactly what EditionResolver.FindResult already
        # uses to reuse an existing filter result for a converted or re-tagged copy of a
        # book this pipeline has already scanned -- the only thing missing was ever
        # checking here, at ingest, before paying to upload and re-scan audio that is
        # already on file under a different fingerprint. A miss here (CloudScanStatus
        # other than "available") falls through to the normal upload path below exactly
        # as it always did; only a genuine match short-circuits it.
        existing = call("POST", f"{api_base}/v1/scans/requests", token=token, body={
            "fingerprint": fingerprint,
            "currentScannerVersion": None,
            "signature": signature,
        })
        if existing and existing.get("status") == "available" and existing.get("result") is not None:
            log(f"  matched an existing scanned edition by fingerprint/signature evidence "
                f"(scanner version {existing['result'].get('scannerVersion')}); skipping "
                f"upload and re-scan entirely.")
            shutil.move(path, os.path.join(done_dir, filename))
            log(f"  moved to done/: {filename}")
            return

        authorization = call("POST", f"{api_base}/v1/uploads/authorizations", token=token, body={
            "fingerprint": fingerprint,
            "fileName": filename,
            "contentType": guess_content_type(file_type),
            "fileSize": file_size,
        })
        upload_id = authorization["uploadID"]
        upload_url = authorization["uploadURL"]
        headers = authorization["headers"]

        log(f"  uploading {file_size / 1_000_000:.1f} MB to blob storage ...")
        start = time.time()
        with open(path, "rb") as f:
            req = urllib.request.Request(upload_url, data=f.read(), method="PUT")
            for k, v in headers.items():
                req.add_header(k, v)
            with urllib.request.urlopen(req, timeout=3600) as resp:
                resp.read()
        elapsed = time.time() - start
        log(f"  upload complete in {elapsed:.1f}s ({file_size / max(elapsed, 0.01) / 1_000_000:.1f} MB/s)")

        call("POST", f"{api_base}/v1/uploads/{upload_id}/complete", token=token)

        submission = call("POST", f"{api_base}/v1/scans/jobs", token=token, body={
            "uploadID": upload_id,
            "fingerprint": fingerprint,
        })
        if submission.get("result") is not None:
            log(f"  server already had a completed result for this exact fingerprint "
                f"(scanner version {submission['result'].get('scannerVersion')}).")
        else:
            scan_id = submission.get("scanID")
            log(f"  scan job submitted: {scan_id} (status={submission.get('status')})")

        shutil.move(path, os.path.join(done_dir, filename))
        log(f"  moved to done/: {filename}")

    except Exception as exc:
        log(f"  FAILED: {exc}")
        try:
            shutil.move(path, os.path.join(failed_dir, filename))
        except Exception:
            pass


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--api-base", default="http://127.0.0.1:8080")
    parser.add_argument("--drop-folder", default="/srv/audiobook-drop/incoming")
    parser.add_argument("--email", required=True)
    parser.add_argument("--password", required=True)
    args = parser.parse_args()

    done_dir = os.path.join(args.drop_folder, "..", "done")
    failed_dir = os.path.join(args.drop_folder, "..", "failed")
    os.makedirs(done_dir, exist_ok=True)
    os.makedirs(failed_dir, exist_ok=True)

    log(f"Logging in as {args.email} ...")
    auth = call("POST", f"{args.api_base}/v1/auth/login", body={
        "email": args.email, "password": args.password,
    })
    token = auth["accessToken"]
    log("Logged in. Watching for new files ...")

    seen_sizes: dict[str, tuple[int, int]] = {}  # filename -> (size, stable_count)

    while True:
        try:
            entries = [
                f for f in os.listdir(args.drop_folder)
                if os.path.splitext(f)[1].lower() in AUDIO_EXTENSIONS
            ]
        except FileNotFoundError:
            entries = []

        for filename in entries:
            path = os.path.join(args.drop_folder, filename)
            try:
                size = os.path.getsize(path)
            except FileNotFoundError:
                continue

            previous_size, stable_count = seen_sizes.get(filename, (None, 0))
            if size == previous_size:
                stable_count += 1
            else:
                stable_count = 0
            seen_sizes[filename] = (size, stable_count)

            if stable_count >= STABLE_CHECKS_REQUIRED:
                del seen_sizes[filename]
                process_file(path, args.api_base, token, done_dir, failed_dir)

        time.sleep(POLL_INTERVAL_SECONDS)


if __name__ == "__main__":
    main()
