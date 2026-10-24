#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: package-macos.sh <osx-arm64|osx-x64> <publish-directory> [vision-helper]" >&2
  exit 64
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
rid="$1"
publish_dir="$(cd "$2" && pwd)"
helper="${3:-$root/artifacts/macos-tools/SnapTranslateVision}"
artifact_dir="$root/artifacts/macos/$rid"
app="$artifact_dir/SnapTranslate.app"

if [[ ! -x "$publish_dir/SnapTranslate" ]]; then
  echo "Published executable not found: $publish_dir/SnapTranslate" >&2
  exit 1
fi
if [[ ! -x "$helper" ]]; then
  echo "Vision helper not found: $helper" >&2
  exit 1
fi

rm -rf "$artifact_dir"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish_dir/." "$app/Contents/MacOS/"
cp "$helper" "$app/Contents/Resources/SnapTranslateVision"
cp "$root/packaging/macos/Info.plist" "$app/Contents/Info.plist"
chmod 755 "$app/Contents/MacOS/SnapTranslate" "$app/Contents/Resources/SnapTranslateVision"

if [[ -n "${MACOS_SIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime --timestamp --sign "$MACOS_SIGN_IDENTITY" "$app"
else
  codesign --force --deep --sign - "$app"
fi

ditto -c -k --sequesterRsrc --keepParent "$app" "$artifact_dir/SnapTranslate-$rid.zip"
echo "$app"
