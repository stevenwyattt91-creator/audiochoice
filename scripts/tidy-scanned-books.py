#!/usr/bin/env python3
"""Finds Scanned Books entries that are the same recording, and links them.

Why this exists
---------------
Identity used to be byte-exact, so importing a converted or re-tagged copy of a book the server
already held created a second entry: the same audio scanned twice, listed twice, with different
filter counts. Chapter structure can now identify a recording, which makes those pairs findable.

What it does
------------
Reports first, and changes nothing unless asked. A merge hands one entry's filter timings to
another, so it is worth reading the groups before applying them: a wrong link would play content a
listener asked never to hear, which is the one failure this whole product exists to prevent.

Nothing is deleted. Linking is the whole operation, because a link is reversible by ignoring it and
a deletion is not, and because both file identities are real files on real devices.

Usage:
  scripts/tidy-scanned-books.py --admin-token "$ADMIN"            # report only
  scripts/tidy-scanned-books.py --admin-token "$ADMIN" --apply    # link each group
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.request

DEFAULT_API = "https://audiochoice-stg-api.grayocean-b35d4bf9.eastus.azurecontainerapps.io"


def call(api, path, token, body=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(
        f"{api}{path}", data=data, method=method or ("POST" if data else "GET"))
    request.add_header("Authorization", f"Bearer {token}")
    if data:
        request.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(request, timeout=180) as response:
        payload = response.read()
        return json.loads(payload) if payload else None


def hours(seconds):
    return f"{(seconds or 0) / 3600:.1f}h"


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--api", default=os.environ.get("AUDIOCHOICE_API", DEFAULT_API))
    parser.add_argument("--admin-token", default=os.environ.get("AUDIOCHOICE_ADMIN_TOKEN"))
    parser.add_argument("--apply", action="store_true",
                        help="Link each group. Without this, nothing is changed.")
    args = parser.parse_args()
    if not args.admin_token:
        parser.error("Supply --admin-token, or set AUDIOCHOICE_ADMIN_TOKEN.")

    groups = call(args.api, "/v1/admin/editions/duplicates", args.admin_token) or []
    if not groups:
        print("No duplicate recordings found.")
        return 0

    print(f"{len(groups)} group(s) of entries that appear to be the same recording.\n")
    linked = skipped = failed = 0

    for number, group in enumerate(groups, start=1):
        members = group["members"]
        print(f"Group {number}:")
        for member in members:
            keep = "  <- has a scan" if member["hasResult"] else ""
            print(f"  {(member['workTitle'] or '(untitled)')[:44]:44} {hours(member['duration']):>7} "
                  f"{member['fileType'] or '?':>5}  {member['chapterMarks']:>3} marks  "
                  f"{member['sha256'][:10]}{keep}")

        # The entry carrying a scan is the one worth keeping, because linking to it is what lets the
        # others resolve to real filters. With none, there is nothing to inherit.
        anchor = next((m for m in members if m["hasResult"]), None)
        if anchor is None:
            print("  none of these has a scan, so there is nothing to inherit. Skipped.\n")
            skipped += 1
            continue
        if not args.apply:
            print(f"  would link the others to {anchor['sha256'][:10]}\n")
            continue

        def fingerprint(member):
            return {
                "version": member["version"], "sha256": member["sha256"],
                "fileSize": member["fileSize"], "duration": member["duration"],
                "fileType": member["fileType"] or "m4b",
                "workTitle": member["workTitle"], "author": member["author"],
                "seriesTitle": None, "seriesNumber": None,
                "editionType": None, "partNumber": None, "totalParts": None,
            }

        for member in members:
            if member["sha256"] == anchor["sha256"]:
                continue
            try:
                call(args.api, "/v1/admin/editions/alias", args.admin_token,
                     body={"first": fingerprint(member), "second": fingerprint(anchor)})
                print(f"  linked {member['sha256'][:10]} -> {anchor['sha256'][:10]}")
                linked += 1
            except urllib.error.HTTPError as error:
                detail = error.read()[:150].decode(errors="replace")
                print(f"  FAILED {member['sha256'][:10]}: {error.code} {detail}")
                failed += 1
        print()

    if args.apply:
        print(f"Linked {linked}, skipped {skipped} group(s) with no scan, {failed} failure(s).")
    else:
        print("Nothing was changed. Re-run with --apply once the groups above look right.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
