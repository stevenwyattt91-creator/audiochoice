#!/usr/bin/env bash
# Exercises what a filter report says before it leaves the device.
#
# The iOS target has no test bundle. Two properties matter here: a report has to describe the
# passage the listener actually heard, which is already behind them by the time they tap; and
# it must never carry the content itself, because a listener's audio staying private is the
# promise the whole app rests on. The second is checked against the encoded JSON, since that
# is what is actually sent.
#
# Usage: scripts/verify-ios-reports.sh
set -euo pipefail
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sources="$repository_root/ios-app/AudioChoice"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

swiftc -O -o "$workspace/reportchecks" \
  "$repository_root/ios-app/ReportChecks/main.swift" \
  "$sources/CloudScanModels.swift" \
  "$sources/ReaderSync.swift" \
  "$sources/FilterReportComposer.swift"

"$workspace/reportchecks"
