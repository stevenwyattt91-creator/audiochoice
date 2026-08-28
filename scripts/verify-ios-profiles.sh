#!/usr/bin/env bash
# Exercises how a saved filter profile becomes a new book's starting choices.
#
# The iOS target has no test bundle. A profile decides what is removed from books the listener
# has not looked at yet, so the failure modes are a key meaning something different in another
# book, and a profile switching off protection nobody asked it to. These files depend only on
# Foundation, so they compile for the host.
#
# Usage: scripts/verify-ios-profiles.sh
set -euo pipefail
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sources="$repository_root/ios-app/AudioChoice"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

swiftc -O -o "$workspace/profilechecks" \
  "$repository_root/ios-app/ProfileChecks/main.swift" \
  "$sources/CloudScanModels.swift" \
  "$sources/ReaderSync.swift" \
  "$sources/BookFilterSettings.swift" \
  "$sources/PlaybackFilterTaxonomy.swift" \
  "$sources/FilterProfileMapping.swift"

"$workspace/profilechecks"
