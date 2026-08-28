#!/usr/bin/env bash
# Archive, export and upload the iOS app to TestFlight.
#
# The App Store Connect API key does the signing, so Xcode creates and renews the
# distribution certificate and provisioning profile itself. Nothing needs to be
# clicked, and no certificate has to be exported between machines.
#
# Reads its configuration from local.properties style overrides or the environment:
#   AUDIOCHOICE_ASC_KEY_ID       App Store Connect API key id
#   AUDIOCHOICE_ASC_ISSUER_ID    App Store Connect issuer id
#   AUDIOCHOICE_ASC_KEY_PATH     .p8 path (default: ~/.appstoreconnect/private_keys)
#
# Usage:
#   scripts/ship-ios-testflight.sh            # archive, export, upload
#   scripts/ship-ios-testflight.sh --validate # stop after validation, upload nothing
set -euo pipefail

KEY_ID="${AUDIOCHOICE_ASC_KEY_ID:-5PKNS336V2}"
ISSUER_ID="${AUDIOCHOICE_ASC_ISSUER_ID:-6c586ea8-d53f-47ef-ba1f-84f15f80dccc}"
KEY_PATH="${AUDIOCHOICE_ASC_KEY_PATH:-$HOME/.appstoreconnect/private_keys/AuthKey_${KEY_ID}.p8}"
TEAM_ID="${AUDIOCHOICE_TEAM_ID:-8M67MANZ4S}"

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repository_root/ios-app/AudioChoice.xcodeproj"
workspace="$(mktemp -d)"
archive="$workspace/AudioChoice.xcarchive"
export_directory="$workspace/export"
trap 'rm -rf "$workspace"' EXIT

if [ ! -f "$KEY_PATH" ]; then
  echo "No App Store Connect key at $KEY_PATH." >&2
  echo "Download it from App Store Connect > Users and Access > Integrations." >&2
  exit 1
fi

authentication=(
  -allowProvisioningUpdates
  -authenticationKeyPath "$KEY_PATH"
  -authenticationKeyID "$KEY_ID"
  -authenticationKeyIssuerID "$ISSUER_ID"
)

echo "==> Archiving"
xcodebuild -project "$project" -scheme AudioChoice -configuration Release \
  -destination 'generic/platform=iOS' -archivePath "$archive" \
  "${authentication[@]}" archive

# Written here rather than committed so the team id stays in one place above.
cat > "$workspace/ExportOptions.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>method</key><string>app-store-connect</string>
  <key>teamID</key><string>$TEAM_ID</string>
  <key>uploadSymbols</key><true/>
  <key>signingStyle</key><string>automatic</string>
  <key>destination</key><string>export</string>
</dict>
</plist>
PLIST

echo "==> Exporting a distribution-signed build"
xcodebuild -exportArchive -archivePath "$archive" -exportPath "$export_directory" \
  -exportOptionsPlist "$workspace/ExportOptions.plist" "${authentication[@]}"

ipa="$export_directory/AudioChoice.ipa"

# codesign cannot inspect an .ipa, which is a zip; the signature lives on the .app inside
# it. Worth reporting, because a development-signed build validates far enough to look
# fine and then cannot be distributed.
#
# errexit is lifted deliberately: this is a diagnostic, and an empty grep under pipefail
# would otherwise abort a release that is actually fine.
set +e +o pipefail
unzip -o -q "$ipa" -d "$workspace/inspect" >/dev/null 2>&1
signing_identity="$(codesign -dvvv "$workspace/inspect/Payload/"*.app 2>&1 |
  sed -n 's/^Authority=\(Apple [^,]*\)/\1/p' | head -1)"
set -e -o pipefail

echo "==> Signed by: ${signing_identity:-could not be determined}"
case "$signing_identity" in
  "Apple Distribution"*) ;;
  *) echo "    Note: expected an Apple Distribution identity for TestFlight." >&2 ;;
esac

# Validation catches a missing app record, a bundle id mismatch or a rejected
# entitlement before anything is published.
echo "==> Validating"
xcrun altool --validate-app -f "$ipa" -t ios --apiKey "$KEY_ID" --apiIssuer "$ISSUER_ID"

if [ "${1:-}" = "--validate" ]; then
  echo "Validation only, nothing uploaded."
  exit 0
fi

echo "==> Uploading to TestFlight"
xcrun altool --upload-app -f "$ipa" -t ios --apiKey "$KEY_ID" --apiIssuer "$ISSUER_ID"

echo
echo "Uploaded. Processing in App Store Connect takes a few minutes before the build"
echo "appears for testers. Bump CURRENT_PROJECT_VERSION in the Xcode project before"
echo "the next run, since a build number cannot be reused."
