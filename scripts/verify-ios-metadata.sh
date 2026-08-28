#!/usr/bin/env bash
# Exercises the iOS container metadata reader against real MP4 atom layouts.
#
# The iOS target has no test bundle, and this parser is both the riskiest and the most
# load-bearing metadata code in the app: it reads byte offsets out of untrusted files, and
# it is the only source of a cover and a runtime for books AVFoundation cannot describe.
# Mp4TagReader.swift depends only on Foundation, so it compiles for the host and can be
# checked without a simulator. The Android suite runs the same cases over the same
# layouts, which is what keeps both clients agreeing about one file.
#
# Usage: scripts/verify-ios-metadata.sh
set -euo pipefail
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sources="$repository_root/ios-app/AudioChoice"
checks="$repository_root/ios-app/MetadataChecks"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

swiftc -O -o "$workspace/metadatachecks" \
  "$checks/main.swift" \
  "$sources/Mp4TagReader.swift"
"$workspace/metadatachecks"
