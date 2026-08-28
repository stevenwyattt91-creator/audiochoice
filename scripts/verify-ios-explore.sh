#!/usr/bin/env bash
# Exercises the explore catalogue cleanup.
#
# The iOS target has no test bundle, and this logic can fail in both directions. Merging too
# little leaves the duplicate rows it exists to remove; merging too much hides a genuinely
# different recording behind another one's entry, whose filter scan does not describe the
# audio someone then plays. These files depend only on Foundation, so they compile for the
# host and can be checked without a simulator.
#
# Usage: scripts/verify-ios-explore.sh
set -euo pipefail
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sources="$repository_root/ios-app/AudioChoice"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

swiftc -O -o "$workspace/explorechecks" \
  "$repository_root/ios-app/ExploreChecks/main.swift" \
  "$sources/CloudScanModels.swift" \
  "$sources/ReaderSync.swift" \
  "$sources/ExploreCatalogCleanup.swift"

"$workspace/explorechecks"
