#!/bin/bash
set -e
cd "$(dirname "$0")"

echo "==> Building release binary..."
env -u TOOLCHAINS xcrun swift build -c release

APP="MonitorSwitcher.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

cp Info.plist "$APP/Contents/Info.plist"
cp .build/release/MonitorSwitcher "$APP/Contents/MacOS/MonitorSwitcher"

echo "==> Signing (ad-hoc, local use only)..."
codesign --force --deep --sign - "$APP"

echo "==> Done: $APP"
echo "    Move it to Applications with:  mv \"$APP\" /Applications/"
