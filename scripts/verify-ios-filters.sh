#!/usr/bin/env bash
# Exercises the iOS per-book filter logic.
#
# The iOS target has no test bundle, and this is the code that decides whether content a
# listener asked never to hear is actually removed. Its failure modes are all quiet: a
# control that disappears when switched off, a key that does not match the one the Android
# client saves, or a default that filters nothing. These files depend only on Foundation,
# so they compile for the host and can be checked without a simulator.
#
# Usage: scripts/verify-ios-filters.sh
set -euo pipefail
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sources="$repository_root/ios-app/AudioChoice"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

swiftc -O -o "$workspace/filterchecks" \
  "$repository_root/ios-app/FilterChecks/main.swift" \
  "$sources/CloudScanModels.swift" \
  "$sources/ReaderSync.swift" \
  "$sources/BookFilterSettings.swift" \
  "$sources/PlaybackFilterTaxonomy.swift" \

# The taxonomy table in the app is one of four copies of the same thing. Pointing the checks
# at the declared contract is what stops them drifting apart unnoticed.
export AUDIOCHOICE_TAXONOMY_CONTRACT="$repository_root/contracts/content-taxonomy.v2.json"

# UserDefaults needs somewhere to write for the storage round trip.
cd "$workspace" && "$workspace/filterchecks"
