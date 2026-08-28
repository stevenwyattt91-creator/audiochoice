#!/usr/bin/env bash
# Runs everything that can be checked without a device or a deployment.
#
# There are three codebases and eight separate verification commands between them, which in
# practice meant remembering which ones mattered for a given change. This runs the lot and
# reports once at the end, so "did I break anything" has a single answer.
#
# Usage:
#   scripts/verify.sh            # everything
#   scripts/verify.sh ios        # one area: ios, android, backend
set -uo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"
area="${1:-all}"

# Not set -e: a failure in one area should not hide the state of the others, which is the
# whole point of running them together.
declare -a names=()
declare -a outcomes=()

record() {
  names+=("$1")
  outcomes+=("$2")
}

run() {
  local label="$1"
  shift
  printf '\n=== %s ===\n' "$label"
  if "$@"; then
    record "$label" "pass"
  else
    record "$label" "FAIL"
  fi
}

backend() {
  export PATH="$HOME/.dotnet:$PATH"
  # The API has to build before its contract tests mean anything.
  ( cd backend && dotnet build AudioChoice.Api/AudioChoice.Api.csproj -v q --nologo ) || return 1
  # And it has to build the way the deploy builds it. EnablePostgres switches on the
  # POSTGRES and GOOGLEAUTH constants, so every Postgres store and the Google auth path are
  # excluded from a plain build -- which meant a compile error in any of them stayed hidden
  # locally and surfaced only in the deployment pipeline. Both Dockerfiles pass this flag.
  ( cd backend && dotnet build AudioChoice.Api/AudioChoice.Api.csproj \
      -v q --nologo -p:EnablePostgres=true ) || return 1
  ( cd backend && dotnet run \
      --project AudioChoice.Api.ContractTests/AudioChoice.Api.ContractTests.csproj \
      -v q --nologo )
}

ios_build() {
  ( cd ios-app && xcodebuild -project AudioChoice.xcodeproj -scheme AudioChoice \
      -configuration Debug -sdk iphonesimulator \
      -destination 'generic/platform=iOS Simulator' \
      CODE_SIGNING_ALLOWED=NO build ) >/dev/null 2>&1
}

android() {
  # Never `clean`: it discards the NDK build and turns a two-minute check into twenty.
  ( cd android-app && ./gradlew :app:assembleBeta :app:testDebugUnitTest :app:lintVitalBeta ) \
    >/dev/null 2>&1
}

if [ "$area" = "all" ] || [ "$area" = "backend" ]; then
  run "backend build and contract tests" backend
fi

if [ "$area" = "all" ] || [ "$area" = "ios" ]; then
  run "iOS build" ios_build
  # The iOS target has no test bundle, so the logic that decides what gets filtered, what a
  # report says and how the reader aligns is checked by compiling those files for the host.
  for harness in filters library reader explore reports profiles metadata; do
    run "iOS $harness checks" "$repository_root/scripts/verify-ios-$harness.sh"
  done
fi

if [ "$area" = "all" ] || [ "$area" = "android" ]; then
  run "Android build, unit tests and lint" android
fi

printf '\n===================== summary =====================\n'
failures=0
for index in "${!names[@]}"; do
  printf '%-40s %s\n' "${names[$index]}" "${outcomes[$index]}"
  [ "${outcomes[$index]}" = "pass" ] || failures=$((failures + 1))
done

if [ "$failures" -eq 0 ]; then
  printf '\nAll checks passed.\n'
  exit 0
fi
printf '\n%d check(s) failed.\n' "$failures"
exit 1
