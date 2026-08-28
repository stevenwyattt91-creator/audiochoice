#!/usr/bin/env bash
# Exercises the iOS completion rule and the rename-versus-identity split.
#
# The iOS target has no test bundle. Renaming is the part worth covering: the displayed
# title is editable, but the title sent as identity evidence has to keep being the file's
# own, or correcting a label could quietly change which recording a book is matched to and
# therefore which filters and transcript apply to it.
#
# Usage: scripts/verify-ios-library.sh
set -euo pipefail
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sources="$repository_root/ios-app/AudioChoice"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

swiftc -O -o "$workspace/librarychecks" \
  "$repository_root/ios-app/LibraryChecks/main.swift" \
  "$sources/CloudScanModels.swift" \
  "$sources/ReaderSync.swift" \
  "$sources/BookCompletion.swift" \
  "$sources/MobileModels.swift"

# UserDefaults needs somewhere to write, and each run should start clean so a stored
# library from a previous run cannot make a check pass.
cd "$workspace" && "$workspace/librarychecks"
