#!/bin/bash
set -euo pipefail

# Run this on a Mac after publishing the Apple-silicon build into releases/macos-arm64.
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APP_NAME="AudioChoice Companion"
APP_BUNDLE="$ROOT/distribution/$APP_NAME.app"
SOURCE_DIR="$ROOT/releases/macos-arm64"
DMG_PATH="$ROOT/distribution/AudioChoiceCompanion-macOS-AppleSilicon.dmg"

rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"
cp "$ROOT/packaging/macos/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
cp "$ROOT/packaging/macos/AppIcon.icns" "$APP_BUNDLE/Contents/Resources/AppIcon.icns"
cp -R "$SOURCE_DIR/." "$APP_BUNDLE/Contents/MacOS/"
cp "$ROOT/packaging/macos/launcher.sh" "$APP_BUNDLE/Contents/MacOS/AudioChoiceCompanion"
chmod +x "$APP_BUNDLE/Contents/MacOS/AudioChoiceCompanion"

rm -f "$DMG_PATH"
hdiutil create -volname "$APP_NAME" -srcfolder "$APP_BUNDLE" -ov -format UDZO "$DMG_PATH"
echo "Created: $DMG_PATH"
