#!/usr/bin/env python3
"""Queue an admin-only Lambda reanalysis from a saved transcript."""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path


API_BASE = os.environ.get("AUDIOCHOICE_LAMBDA_API", "http://127.0.0.1:8080").rstrip("/")
ENV_PATH = Path("deploy/lambda/lambda-worker.env")


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


def request_json(path: str, token: str, *, payload: dict | None = None) -> dict | list:
    headers = {"Authorization": f"Bearer {token}"}
    method = "GET"
    data = None
    if payload is not None:
        method = "POST"
        headers["Content-Type"] = "application/json"
        headers["X-AudioChoice-Scan-Channel"] = "ios-beta"
        data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        f"{API_BASE}{path}", data=data, headers=headers, method=method
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", errors="replace")
        raise SystemExit(f"Request failed with HTTP {error.code}: {body or error.reason}")
    except urllib.error.URLError as error:
        raise SystemExit(f"Cannot reach the Lambda scanner at {API_BASE}: {error.reason}")


def label(record: dict) -> str:
    fingerprint = record.get("fingerprint") or {}
    title = fingerprint.get("workTitle") or "Untitled audiobook"
    author = fingerprint.get("author")
    return f"{title} — {author}" if author else title


def main() -> None:
    query = " ".join(sys.argv[1:]).strip().casefold()
    token = api_token()
    records = request_json("/v1/admin/transcripts", token)
    if not isinstance(records, list):
        raise SystemExit("The transcript endpoint returned an unexpected response.")

    complete = [record for record in records if record.get("isComplete")]
    if query:
        complete = [
            record
            for record in complete
            if query in label(record).casefold()
            or query in str((record.get("fingerprint") or {}).get("seriesTitle") or "").casefold()
        ]
    if not complete:
        suffix = f' matching "{query}"' if query else ""
        raise SystemExit(f"No complete saved transcript was found{suffix}.")

    selected = max(complete, key=lambda record: record.get("createdAt") or "")
    fingerprint = selected["fingerprint"]
    print(
        f"Selected {label(selected)} ({selected.get('segmentCount', 0)} segments; "
        f"saved {selected.get('createdAt', 'unknown')})."
    )
    response = request_json(
        "/v1/admin/scans/reanalysis",
        token,
        payload={
            "ownerUserID": "00000000-0000-0000-0000-000000000000",
            "fingerprint": fingerprint,
        },
    )
    scan_id = response.get("scanID") if isinstance(response, dict) else None
    if not scan_id:
        raise SystemExit(f"Reanalysis did not return a scan ID: {response}")
    print(f"Queued scan {scan_id}.")

    previous = None
    while True:
        status = request_json(f"/v1/admin/scans/jobs/{scan_id}", token)
        snapshot = (
            status.get("status"),
            status.get("progressPercent", status.get("percentComplete", 0)),
            status.get("progressStage") or "waiting",
        )
        if snapshot != previous:
            print(f"{snapshot[0]} — {snapshot[1]}% ({snapshot[2]})", flush=True)
            previous = snapshot
        if snapshot[0] == "completed":
            events = ((status.get("result") or {}).get("events") or [])
            print(f"Reanalysis completed with {len(events)} filter events.")
            return
        if snapshot[0] == "failed":
            raise SystemExit("Reanalysis failed. Check the scanner logs for the error.")
        time.sleep(3)


if __name__ == "__main__":
    main()
