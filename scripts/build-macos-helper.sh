#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
output="${1:-$root/artifacts/macos-tools/SnapTranslateVision}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$(dirname "$output")"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
build_universal() {
  local name="$1"
  local source_file="$2"
  local binary="$3"

  xcrun swiftc -O -sdk "$sdk" -target arm64-apple-macos12.0 "$source_file" -o "$work/$name-arm64"
  xcrun swiftc -O -sdk "$sdk" -target x86_64-apple-macos12.0 "$source_file" -o "$work/$name-x64"
  lipo -create "$work/$name-arm64" "$work/$name-x64" -output "$binary"
  chmod 755 "$binary"
  lipo -info "$binary"
}

build_universal SnapTranslateVision "$root/tools/macos/SnapTranslateVision.swift" "$output"

# 剪贴板辅助程序与 Vision 辅助程序输出到同一目录，应用包从该目录取用。
build_universal SnapTranslatePasteboard "$root/tools/macos/SnapTranslatePasteboard.swift" "$(dirname "$output")/SnapTranslatePasteboard"
