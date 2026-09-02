#!/usr/bin/env python3
"""Compares what two scanner configurations find in the same audiobooks.

Why this exists
---------------
Moving classification to a different model is the one change that can go wrong invisibly. A
book still scans, still reports success, and still plays -- but a scene the old models caught
is now missed, and the listener hears the thing they asked never to hear. No error appears
anywhere. The only way to know is to compare, on books whose correct answer is already known.

This reanalyses saved transcripts, so it costs nothing in transcription and never touches
audio. Reanalysis is also invisible to listeners until a result is published, and every
edition compared here is one somebody already scanned.

How to use it
-------------
1. Snapshot what the current models produce, before changing anything:

     scripts/compare-scanner-models.py snapshot --out before.json --admin-token "$ADMIN"

2. Switch the scanner to the new models and restart it.

3. Reanalyse the same editions and compare:

     scripts/compare-scanner-models.py compare --baseline before.json --admin-token "$ADMIN"

Read the result as: "missed" is content the previous models flagged and the new ones did not,
which is the column that matters. "added" may be a genuine improvement or a false positive,
and only listening tells you which.
"""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

DEFAULT_API = "https://audiochoice-stg-api.grayocean-b35d4bf9.eastus.azurecontainerapps.io"
# Two events are treated as the same finding when they overlap at all and share an event type.
# Exact stable keys are far too strict: a model that finds the same scene two seconds later has
# agreed, not disagreed, and comparing keys would report every scene as both missed and added.
OVERLAP_TOLERANCE_SECONDS = 15.0


def call(api, path, token=None, body=None, method=None, lane_header=False):
    url = f"{api}{path}"
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method or ("POST" if data else "GET"))
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    if data:
        request.add_header("Content-Type", "application/json")
    if lane_header:
        # Routes the work to the GPU worker rather than the Azure one, matching where beta
        # scans run. The Azure worker is configured for a five-minute ceiling and cannot
        # finish a real audiobook.
        request.add_header("X-AudioChoice-Scan-Channel", "ios-beta")
    with urllib.request.urlopen(request, timeout=300) as response:
        payload = response.read()
        return json.loads(payload) if payload else None


def stored_result(api, admin_token, fingerprint):
    """The stored result for this edition, read without causing any work.

    Deliberately not /v1/scans/requests. That route returns a result when one exists, but for
    an edition holding a transcript and no result it queues a reanalysis and charges for it --
    and it does not check that the caller owns the edition. Surveying nineteen editions
    through it would have started paid jobs on every one that had not been analysed yet.
    """
    from urllib.parse import urlencode
    query = urlencode({
        "sha256": fingerprint["sha256"],
        "fingerprintVersion": fingerprint["version"],
        "fileSize": fingerprint["fileSize"],
    })
    try:
        return call(api, f"/v1/admin/editions/result?{query}", token=admin_token)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise


def editions_with_transcripts(api, admin_token, limit):
    everything = call(api, "/v1/admin/editions", token=admin_token) or []
    usable = [e for e in everything if e.get("hasTranscript")]
    usable.sort(key=lambda e: e.get("segmentCount", 0), reverse=True)
    return usable[:limit] if limit else usable


def describe(fingerprint):
    title = fingerprint.get("workTitle") or "(untitled)"
    return f"{title} [{fingerprint['sha256'][:10]}]"


def summarise(result):
    """Counts and filtered seconds per event type, which is what a listener experiences."""
    events = (result or {}).get("events") or []
    by_type = {}
    for event in events:
        key = event.get("eventID")
        entry = by_type.setdefault(key, {"count": 0, "seconds": 0.0})
        entry["count"] += 1
        entry["seconds"] += max(0.0, event.get("endTime", 0) - event.get("startTime", 0))
    return {
        "scannerVersion": (result or {}).get("scannerVersion"),
        "eventCount": len(events),
        "filteredSeconds": round(sum(v["seconds"] for v in by_type.values()), 1),
        "byType": {k: {"count": v["count"], "seconds": round(v["seconds"], 1)}
                   for k, v in by_type.items()},
        "events": [
            {"eventID": e.get("eventID"), "start": e.get("startTime"),
             "end": e.get("endTime"), "confidence": e.get("confidence"),
             "description": e.get("safeDescription")}
            for e in sorted(events, key=lambda e: e.get("startTime", 0))
        ],
    }


def diff(before, after):
    """Which findings agree, which the new models lost, and which they added."""
    old_events = list(before.get("events", []))
    new_events = list(after.get("events", []))
    unmatched_new = list(new_events)
    agreed, missed = [], []

    for old in old_events:
        match = None
        for candidate in unmatched_new:
            if candidate["eventID"] != old["eventID"]:
                continue
            starts_within = abs((candidate["start"] or 0) - (old["start"] or 0)) <= OVERLAP_TOLERANCE_SECONDS
            overlaps = ((old["start"] or 0) < (candidate["end"] or 0) and
                        (candidate["start"] or 0) < (old["end"] or 0))
            if overlaps or starts_within:
                match = candidate
                break
        if match:
            unmatched_new.remove(match)
            agreed.append((old, match))
        else:
            missed.append(old)
    return agreed, missed, unmatched_new


def command_snapshot(args, api):
    editions = editions_with_transcripts(api, args.admin_token, args.limit)
    print(f"{len(editions)} edition(s) with a saved transcript\n")
    snapshot = {}
    for edition in editions:
        fingerprint = edition["fingerprint"]
        result = stored_result(api, args.admin_token, fingerprint)
        if not result:
            print(f"  {describe(fingerprint)}: no stored result, skipped")
            continue
        entry = summarise(result)
        snapshot[fingerprint["sha256"]] = {"fingerprint": fingerprint, **entry}
        print(f"  {describe(fingerprint)}: scanner {entry['scannerVersion']}, "
              f"{entry['eventCount']} events, {entry['filteredSeconds']}s filtered")
    with open(args.out, "w") as handle:
        json.dump(snapshot, handle, indent=2)
    print(f"\nWrote {len(snapshot)} edition(s) to {args.out}")
    print("Keep this file. It is the only record of what the current models produced.")
    return 0


def command_compare(args, api):
    with open(args.baseline) as handle:
        baseline = json.load(handle)
    print(f"{len(baseline)} edition(s) in the baseline\n")

    queued = {}
    for sha, entry in baseline.items():
        fingerprint = entry["fingerprint"]
        try:
            response = call(api, "/v1/admin/scans/reanalysis", token=args.admin_token,
                            body={"ownerUserID": args.owner_user_id or
                                  "00000000-0000-0000-0000-000000000000",
                                  "fingerprint": fingerprint},
                            lane_header=True)
            scan_id = (response or {}).get("scanID")
            if scan_id:
                queued[scan_id] = sha
                print(f"  {describe(fingerprint)}: reanalysis {scan_id}")
            else:
                print(f"  {describe(fingerprint)}: not queued ({response})")
        except urllib.error.HTTPError as error:
            print(f"  {describe(fingerprint)}: {error.code} {error.read()[:160].decode(errors='replace')}")

    if not queued:
        print("\nNothing was queued. Nothing to compare.")
        return 1

    print(f"\nWaiting for {len(queued)} reanalysis job(s).")
    outstanding = dict(queued)
    while outstanding:
        time.sleep(args.poll_seconds)
        for scan_id in list(outstanding):
            try:
                job = call(api, f"/v1/admin/scans/jobs/{scan_id}", token=args.admin_token)
            except urllib.error.HTTPError:
                continue
            status = str((job or {}).get("status", "")).lower()
            name = describe(baseline[outstanding[scan_id]]["fingerprint"])
            if status == "completed":
                print(f"  {name}: done")
                del outstanding[scan_id]
            elif status == "failed":
                print(f"  {name}: FAILED")
                del outstanding[scan_id]
            else:
                print(f"  {name}: {(job or {}).get('progressPercent', 0)}% {status}")

    print("\n" + "=" * 72)
    totals = {"agreed": 0, "missed": 0, "added": 0}
    for sha, before in baseline.items():
        after = summarise(stored_result(api, args.admin_token, before["fingerprint"]))
        agreed, missed, added = diff(before, after)
        totals["agreed"] += len(agreed)
        totals["missed"] += len(missed)
        totals["added"] += len(added)
        print(f"\n{describe(before['fingerprint'])}")
        print(f"  before: scanner {before['scannerVersion']}, {before['eventCount']} events, "
              f"{before['filteredSeconds']}s filtered")
        print(f"  after:  scanner {after['scannerVersion']}, {after['eventCount']} events, "
              f"{after['filteredSeconds']}s filtered")
        print(f"  agreed {len(agreed)}, missed {len(missed)}, added {len(added)}")
        # Missed findings are printed in full. Every one is a passage the previous models
        # removed and these do not, which is a listener hearing something they switched off.
        for event in missed[:12]:
            print(f"    MISSED  {event['start']:>9.1f}s  {event.get('description')}")
        if len(missed) > 12:
            print(f"    ... and {len(missed) - 12} more")
        for event in added[:6]:
            print(f"    added   {event['start']:>9.1f}s  {event.get('description')}")
        if len(added) > 6:
            print(f"    ... and {len(added) - 6} more")

    print("\n" + "=" * 72)
    print(f"TOTAL agreed {totals['agreed']}, missed {totals['missed']}, added {totals['added']}")
    if totals["missed"]:
        print("\nEvery missed finding is content the previous models removed and these do not.")
        print("Listen to a sample of them before switching anything a listener depends on.")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["snapshot", "compare"])
    parser.add_argument("--api", default=os.environ.get("AUDIOCHOICE_API", DEFAULT_API))
    parser.add_argument("--admin-token", default=os.environ.get("AUDIOCHOICE_ADMIN_TOKEN"),
                        required=False, help="The configured AudioChoice:ApiToken.")
    parser.add_argument("--owner-user-id", help="Account the reanalysis is attributed to.")
    parser.add_argument("--out", default="scanner-baseline.json")
    parser.add_argument("--baseline", default="scanner-baseline.json")
    parser.add_argument("--limit", type=int, default=10,
                        help="Editions to include, largest transcript first.")
    parser.add_argument("--poll-seconds", type=int, default=30)
    args = parser.parse_args()

    if not args.admin_token:
        parser.error("Supply --admin-token, or set AUDIOCHOICE_ADMIN_TOKEN.")
    # Only the admin token is needed. Everything here reads results through the admin
    # endpoint and queues reanalysis through the admin endpoint, so there is no reason to ask
    # for an account password as well.
    if args.command == "snapshot":
        return command_snapshot(args, args.api)
    return command_compare(args, args.api)


if __name__ == "__main__":
    sys.exit(main())
