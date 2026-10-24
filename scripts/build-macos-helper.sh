#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
output="${1:-$root/artifacts/macos-tools/SnapTranslateVision}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$(dirname "$output")"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
source_file="$root/tools/macos/SnapTranslateVision.swift"

xcrun swiftc -O -sdk "$sdk" -target arm64-apple-macos12.0 "$source_file" -o "$work/helper-arm64"
xcrun swiftc -O -sdk "$sdk" -target x86_64-apple-macos12.0 "$source_file" -o "$work/helper-x64"
lipo -create "$work/helper-arm64" "$work/helper-x64" -output "$output"
chmod 755 "$output"
lipo -info "$output"
