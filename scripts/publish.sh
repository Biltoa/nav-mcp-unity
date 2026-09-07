#!/usr/bin/env bash
#
# Build a macOS release drop of NAV MCP.
#
#   dist-mac/
#     NAV MCP.app/                  the thing a person drags to /Applications
#       Contents/MacOS/             the GUI binary and its runtime
#       Contents/Resources/         umcpd, umcp-stdio, com.umcp.agent, the icon
#     NAV-MCP-<arch>.zip            the same app, zipped
#     NAV-MCP-<arch>.dmg            drag-to-Applications disk image, which is what Mac users expect
#
# Self-contained by default: the audience has never installed a .NET runtime and should not have
# to. Pass --framework-dependent for the small build that needs one.
#
# Usage:
#   scripts/publish.sh                       # this Mac's architecture
#   scripts/publish.sh --arch osx-x64        # Intel
#   scripts/publish.sh --arch both
#   scripts/publish.sh --skip-tests
#
# Signing: the app is ad-hoc signed (codesign -s -), which is what Apple Silicon requires in
# order to run at all. It is NOT notarised, so the first launch needs right-click -> Open. Set
# UMCP_SIGN_IDENTITY to a Developer ID Application identity to sign properly instead.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo"

configuration=Release
output=dist-mac
self_contained=true
skip_tests=false
arch=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) arch="$2"; shift 2 ;;
    --configuration) configuration="$2"; shift 2 ;;
    --output) output="$2"; shift 2 ;;
    --framework-dependent) self_contained=false; shift ;;
    --skip-tests) skip_tests=true; shift ;;
    -h|--help) sed -n '2,25p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$arch" ]]; then
  [[ "$(uname -m)" == "arm64" ]] && arch=osx-arm64 || arch=osx-x64
fi
[[ "$arch" == "both" ]] && arches=(osx-arm64 osx-x64) || arches=("$arch")

say() { printf '\033[36m== %s\033[0m\n' "$1"; }

say "generating tools from the [UnityTool] sources"
dotnet run --project src/Umcp.ToolGen -c "$configuration"

# Generated output is committed; a release built from a tree where it differs is a release whose
# catalog does not match its sources.
if ! git diff --quiet -- unity/com.umcp.agent/Editor/Generated src/Umcp.Daemon/Generated docs/TOOLS.md; then
  echo "generated files are out of date; run toolgen and commit before publishing" >&2
  git status --porcelain -- unity/com.umcp.agent/Editor/Generated src/Umcp.Daemon/Generated docs/TOOLS.md >&2
  exit 1
fi

say "the rule the compiler cannot enforce"
dotnet run --project src/Umcp.MainThreadCheck -c "$configuration"

if [[ "$skip_tests" == false ]]; then
  say "tests"
  dotnet test UnityMcpTool.sln -c "$configuration" --nologo
fi

rm -rf "$output"
mkdir -p "$output"

for rid in "${arches[@]}"; do
  say "publishing for $rid"

  stage="$output/.stage-$rid"
  rm -rf "$stage"
  publish_args=(-c "$configuration" -r "$rid" --self-contained "$self_contained" --nologo)

  dotnet publish src/Umcp.Gui    "${publish_args[@]}" -o "$stage/gui"
  dotnet publish src/Umcp.Daemon "${publish_args[@]}" -o "$stage/daemon"
  dotnet publish src/Umcp.Stdio  "${publish_args[@]}" -o "$stage/stdio"

  suffix=""
  [[ ${#arches[@]} -gt 1 ]] && suffix=" (${rid#osx-})"
  app="$output/NAV MCP$suffix.app"
  rm -rf "$app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

  # The GUI and its runtime go in MacOS/; the tools it launches go in Resources/, which is where
  # DaemonProcess and ClientRegistrations look (BaseDirectory, then ../Resources).
  cp -R "$stage/gui/." "$app/Contents/MacOS/"

  # The *whole* publish output for each tool, not a selection of it. A self-contained build needs
  # its native runtime beside it — libhostpolicy.dylib, libcoreclr.dylib and the rest — and copying
  # only the executable and its managed DLLs produced a umcpd that exits with "the library
  # libhostpolicy.dylib required to execute the application was not found". The app looked complete
  # and its server could never start.
  #
  # Flat, because DaemonProcess and ClientRegistrations look for the binaries directly in
  # Contents/Resources. The two tools share runtime files; they are identical copies, so the
  # overwrite is harmless.
  cp -R "$stage/daemon/." "$app/Contents/Resources/"
  cp -R "$stage/stdio/."  "$app/Contents/Resources/"

  cp -R unity/com.umcp.agent "$app/Contents/Resources/com.umcp.agent"
  find "$app/Contents/Resources/com.umcp.agent" \
       \( -name Library -o -name Temp -o -name obj -o -name bin \) -type d -prune -exec rm -rf {} + 2>/dev/null || true

  cp docs/INSTALL.md "$output/INSTALL.md"

  # The brand kit ships a real .icns; only build one from the PNG if it is ever missing.
  if [[ -f src/Umcp.Gui/Assets/icon.icns ]]; then
    cp src/Umcp.Gui/Assets/icon.icns "$app/Contents/Resources/icon.icns"
  else
    iconset="$stage/icon.iconset"
    mkdir -p "$iconset"
    for size in 16 32 128 256 512; do
      sips -z $size $size src/Umcp.Gui/Assets/icon.png --out "$iconset/icon_${size}x${size}.png" >/dev/null 2>&1 || true
      sips -z $((size * 2)) $((size * 2)) src/Umcp.Gui/Assets/icon.png --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null 2>&1 || true
    done
    iconutil -c icns "$iconset" -o "$app/Contents/Resources/icon.icns"
  fi

  version=$(grep -o '<Version>[^<]*' Directory.Build.props | head -1 | cut -d'>' -f2)

  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>NAV MCP</string>
  <key>CFBundleDisplayName</key><string>NAV MCP</string>
  <key>CFBundleIdentifier</key><string>com.navmcp.app</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>NAV MCP</string>
  <key>CFBundleIconFile</key><string>icon</string>
  <key>NSHighResolutionCapable</key><true/>
  <!-- The window can be closed to the menu bar while the server keeps running; it is still a
       normal app with a Dock icon, because hiding a server people need to find is worse. -->
  <key>LSMinimumSystemVersion</key><string>11.0</string>
</dict>
</plist>
PLIST

  chmod +x "$app/Contents/MacOS/NAV MCP" "$app/Contents/Resources/umcpd" "$app/Contents/Resources/umcp-stdio"

  say "signing $app"
  identity="${UMCP_SIGN_IDENTITY:--}"
  # Deep signing, inner binaries first. An unsigned helper inside a signed bundle fails
  # validation on Apple Silicon, and the failure reads as "the app is damaged".
  codesign --force --deep --sign "$identity" --timestamp=none "$app" 2>/dev/null || \
    codesign --force --deep --sign - "$app"

  if [[ "$identity" == "-" ]]; then
    echo "   ad-hoc signed: the first launch needs right-click -> Open (not notarised)."
  fi

  ( cd "$output" && zip -qry "NAV-MCP-${rid#osx-}.zip" "$(basename "$app")" )

  # A .dmg as well as the zip, because dragging an app to Applications is what a Mac user expects
  # and a zip that unpacks to the Downloads folder is how apps end up running from Downloads
  # forever. hdiutil ships with macOS; create-dmg would be prettier and is one more dependency.
  say "disk image for $rid"
  dmg_stage="$output/.dmg-$rid"
  rm -rf "$dmg_stage"
  mkdir -p "$dmg_stage"
  cp -R "$app" "$dmg_stage/"
  ln -s /Applications "$dmg_stage/Applications"
  cp docs/INSTALL.md "$dmg_stage/Read me first.md"

  dmg="$output/NAV-MCP-${rid#osx-}.dmg"
  rm -f "$dmg"
  hdiutil create -volname "NAV MCP" -srcfolder "$dmg_stage" -ov -quiet -format UDZO "$dmg"
  rm -rf "$dmg_stage"

  rm -rf "$stage"
done

say "done"
ls -la "$output"
echo
echo "Open the .dmg, drag 'NAV MCP.app' to Applications, then right-click it and choose Open the first time."
