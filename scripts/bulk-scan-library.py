#!/usr/bin/env python3
"""Scans a folder of audiobooks into the shared catalogue, one job per file.

Why this exists
---------------
A listener who imports an edition nobody has scanned waits for transcription and analysis
before they can hear anything filtered. Every edition scanned ahead of time removes that wait
for everyone who owns the same file, because a scan is stored against the edition rather than
the account: the second person to import it gets the result immediately and costs nothing.

This is deliberately a script rather than a page in the admin portal. It uses only endpoints
that are already deployed, so it works against the API as it stands today, and a multi-gigabyte
upload is safer in a process that can be restarted than in a browser tab that can be closed.

What identifies an edition
--------------------------
The server keys a scan on `version:sha256:fileSize` and nothing else. So this has to produce
the same three values a phone would for the same bytes -- SHA-256 of the whole file, its exact
size, and fingerprint version 1 -- or a listener importing that file would not find this scan.
Title, author and duration are sent when ffprobe can read them, but they are description
rather than identity and cannot affect the match.

Safe to re-run
--------------
Every file is offered to /v1/scans/requests first. An edition already scanned is reported and
skipped without uploading or spending anything, so interrupting this and running it again
costs nothing for the work already done.

Usage:
  scripts/bulk-scan-library.py ~/Audiobooks --email you@example.com --password '...'
  scripts/bulk-scan-library.py ~/Audiobooks --token "$AUDIOCHOICE_TOKEN" --dry-run
"""
import argparse
import base64
import hashlib
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

DEFAULT_API = "https://audiochoice-stg-api.grayocean-b35d4bf9.eastus.azurecontainerapps.io"
AUDIO_SUFFIXES = {".m4b", ".m4a", ".mp3", ".aac", ".flac", ".ogg", ".wav"}
# Matches the mobile clients, which upload in 8 MiB blocks against the same SAS URL.
BLOCK_SIZE = 8 * 1024 * 1024
FINGERPRINT_VERSION = 1


def request(method, url, token=None, body=None, headers=None, raw=None, content_type=None):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(url, data=data, method=method)
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    if body is not None:
        req.add_header("Content-Type", "application/json")
    if content_type:
        req.add_header("Content-Type", content_type)
    for key, value in (headers or {}).items():
        req.add_header(key, value)
    with urllib.request.urlopen(req, timeout=900) as response:
        payload = response.read()
        if not payload:
            return None
        try:
            return json.loads(payload)
        except json.JSONDecodeError:
            return None


def sign_in(api, email, password):
    result = request("POST", f"{api}/v1/auth/login", body={"email": email, "password": password})
    return result["accessToken"]


def probe(path):
    """Duration and embedded title/author, when ffprobe is available.

    None of this affects which edition the scan belongs to. Duration is worth sending because
    the server scales progress by it and refuses a book past its processing limit; a missing
    one only makes progress coarser.
    """
    try:
        output = subprocess.run(
            ["ffprobe", "-v", "quiet", "-print_format", "json", "-show_format", path],
            capture_output=True, text=True, timeout=120, check=False).stdout
        fmt = (json.loads(output) or {}).get("format", {}) if output else {}
        tags = {k.lower(): v for k, v in (fmt.get("tags") or {}).items()}
        duration = float(fmt["duration"]) if fmt.get("duration") else None
        return duration, tags.get("title"), tags.get("artist") or tags.get("album_artist")
    except (FileNotFoundError, subprocess.SubprocessError, ValueError, KeyError):
        return None, None, None


def fingerprint_of(path):
    digest = hashlib.sha256()
    size = 0
    with open(path, "rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
            size += len(chunk)
    duration, title, author = probe(path)
    return {
        "version": FINGERPRINT_VERSION,
        # Upper case to match the mobile clients. The server lower-cases it for the edition
        # key, so either would match, but staying identical keeps stored rows comparable.
        "sha256": digest.hexdigest().upper(),
        "fileSize": size,
        "duration": duration,
        "fileType": os.path.splitext(path)[1].lstrip(".").lower(),
        "workTitle": title or os.path.splitext(os.path.basename(path))[0],
        "author": author,
        "seriesTitle": None, "seriesNumber": None,
        "editionType": None, "partNumber": None, "totalParts": None,
    }


def append_query(url, extra):
    return f"{url}{'&' if '?' in url else '?'}{extra}"


def upload_blob(authorization, path, size, on_progress):
    """Uploads in blocks, the way the apps do, then commits the block list.

    A single PUT would be simpler and is what the fallback API upload path takes, but this
    SAS is the same one the mobile clients use and the block protocol is what has been proven
    against it for large files. Blocks also mean a failure retries eight megabytes rather
    than an entire audiobook.
    """
    url = authorization["uploadURL"]
    headers = authorization["headers"]
    direct = any(key.lower() == "x-ms-blob-type" for key in headers)
    if not direct:
        with open(path, "rb") as handle:
            request("PUT", url, raw=handle.read(), headers=headers,
                    content_type=headers.get("Content-Type", "application/octet-stream"))
        on_progress(1.0)
        return

    block_ids = []
    uploaded = 0
    with open(path, "rb") as handle:
        index = 0
        while True:
            chunk = handle.read(BLOCK_SIZE)
            if not chunk:
                break
            block_id = base64.b64encode(f"{index:08d}".encode()).decode()
            block_url = append_query(
                url, "comp=block&blockid=" + urllib.parse.quote(block_id, safe=""))
            for attempt in range(4):
                try:
                    request("PUT", block_url, raw=chunk, headers=headers,
                            content_type="application/octet-stream")
                    break
                except (urllib.error.URLError, TimeoutError):
                    if attempt == 3:
                        raise
                    time.sleep(2 ** attempt)
            block_ids.append(block_id)
            uploaded += len(chunk)
            index += 1
            on_progress(min(uploaded / max(size, 1), 1.0))

    xml = ('<?xml version="1.0" encoding="utf-8"?><BlockList>'
           + "".join(f"<Latest>{b}</Latest>" for b in block_ids)
           + "</BlockList>").encode()
    commit_headers = {k: v for k, v in headers.items() if k.lower() != "x-ms-blob-type"}
    request("PUT", append_query(url, "comp=blocklist"), raw=xml,
            headers=commit_headers, content_type="application/xml")


def submit(api, token, path, dry_run):
    """Returns (state, scan_id). State is what happened, for the closing summary."""
    name = os.path.basename(path)
    fingerprint = fingerprint_of(path)
    size = fingerprint["fileSize"]
    hours = (fingerprint["duration"] or 0) / 3600
    print(f"  {name}  {size / 1e9:.2f} GB  {hours:.1f}h  {fingerprint['sha256'][:12]}")

    existing = request("POST", f"{api}/v1/scans/requests", token=token,
                       body={"fingerprint": fingerprint, "currentScannerVersion": None})
    status = (existing or {}).get("status")
    # Anything other than an upload request means the server already has this edition, or is
    # already working on it. Either way there is nothing to pay for and nothing to send.
    if status and str(status).lower() != "uploadrequired":
        events = len(((existing.get("result") or {}).get("events") or []))
        print(f"    already known ({status}), {events} filter events. Skipped.")
        return "skipped", existing.get("scanID")

    if dry_run:
        print("    would upload and queue a scan")
        return "would-scan", None

    authorization = request("POST", f"{api}/v1/uploads/authorizations", token=token, body={
        "fingerprint": fingerprint, "fileName": name,
        "contentType": "audio/mp4" if fingerprint["fileType"] in ("m4b", "m4a") else "audio/mpeg",
        "fileSize": size})

    def show(fraction):
        sys.stdout.write(f"\r    uploading {fraction * 100:5.1f}%")
        sys.stdout.flush()

    upload_blob(authorization, path, size, show)
    sys.stdout.write("\r    uploaded            \n")

    upload_id = authorization["uploadID"]
    if any(key.lower() == "x-ms-blob-type" for key in authorization["headers"]):
        request("POST", f"{api}/v1/uploads/{upload_id}/complete", token=token,
                body={"fingerprint": fingerprint, "fileName": name,
                      "contentType": "audio/mp4", "fileSize": size})

    job = request("POST", f"{api}/v1/scans/jobs", token=token,
                  body={"uploadID": upload_id, "fingerprint": fingerprint})
    scan_id = (job or {}).get("scanID")
    print(f"    queued scan {scan_id}")
    return "queued", scan_id


def watch(api, token, jobs, poll_seconds):
    """Follows queued jobs to completion.

    Uploads finish long before scanning does, so every job is submitted first and watched
    afterwards. The queue is durable and leased, so stopping this loop abandons the watching,
    not the work.
    """
    outstanding = dict(jobs)
    while outstanding:
        time.sleep(poll_seconds)
        for scan_id, name in list(outstanding.items()):
            try:
                job = request("GET", f"{api}/v1/scans/jobs/{scan_id}", token=token)
            except urllib.error.HTTPError as error:
                print(f"  {name}: cannot read job ({error.code})")
                del outstanding[scan_id]
                continue
            status = str(job.get("status", "")).lower()
            if status == "completed":
                events = len(((job.get("result") or {}).get("events") or []))
                print(f"  {name}: done, {events} filter events")
                del outstanding[scan_id]
            elif status == "failed":
                print(f"  {name}: FAILED")
                del outstanding[scan_id]
            else:
                stage = job.get("progressStage") or status
                chunks = ""
                if job.get("totalChunks"):
                    chunks = f" chunk {job.get('completedChunks', 0)}/{job['totalChunks']}"
                print(f"  {name}: {job.get('progressPercent', 0)}% {stage}{chunks}")


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("folder")
    parser.add_argument("--api", default=os.environ.get("AUDIOCHOICE_API", DEFAULT_API))
    parser.add_argument("--token", default=os.environ.get("AUDIOCHOICE_TOKEN"))
    parser.add_argument("--email")
    parser.add_argument("--password")
    parser.add_argument("--limit", type=int, help="Stop after this many new scans.")
    parser.add_argument("--poll-seconds", type=int, default=30)
    parser.add_argument("--no-watch", action="store_true",
                        help="Queue the scans and exit rather than following them.")
    parser.add_argument("--dry-run", action="store_true",
                        help="Report what would be scanned. Uploads nothing, spends nothing.")
    args = parser.parse_args()

    token = args.token
    if not token:
        if not (args.email and args.password):
            parser.error("Supply --token, or --email and --password.")
        token = sign_in(args.api, args.email, args.password)

    files = sorted(
        os.path.join(root, name)
        for root, _, names in os.walk(os.path.expanduser(args.folder))
        for name in names
        if os.path.splitext(name)[1].lower() in AUDIO_SUFFIXES)
    if not files:
        print(f"No audiobooks found under {args.folder}")
        return 1

    print(f"{len(files)} audiobook file(s) under {args.folder}\n")
    jobs, counts = {}, {"queued": 0, "skipped": 0, "would-scan": 0, "error": 0}
    for path in files:
        if args.limit and counts["queued"] >= args.limit:
            print(f"  reached the limit of {args.limit} new scans; stopping")
            break
        try:
            state, scan_id = submit(args.api, token, path, args.dry_run)
            counts[state] = counts.get(state, 0) + 1
            if state == "queued" and scan_id:
                jobs[scan_id] = os.path.basename(path)
        except (urllib.error.HTTPError, urllib.error.URLError, OSError, KeyError) as error:
            # One unreadable or rejected file must not abandon the rest of the folder.
            detail = getattr(error, "read", lambda: b"")()[:200] if hasattr(error, "read") else b""
            print(f"    ERROR {error} {detail.decode(errors='replace')}")
            counts["error"] += 1

    print(f"\nQueued {counts['queued']}, already known {counts['skipped']}, "
          f"errors {counts['error']}"
          + (f", would scan {counts['would-scan']}" if args.dry_run else ""))

    if jobs and not args.no_watch:
        print("\nFollowing scans. Stopping this does not stop the work.")
        watch(args.api, token, jobs, args.poll_seconds)
    return 0


if __name__ == "__main__":
    sys.exit(main())
