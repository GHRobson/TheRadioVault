#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RID="${RUNTIME_IDENTIFIER:-osx-arm64}"
VERSION="$(tr -d '\r\n' < "$ROOT/VERSION.txt")"
CLIENT_APP="$ROOT/artifacts/macos/$RID/Radio Vault.app"
SERVER_APP="$ROOT/artifacts/macos-server/$RID/Radio Vault Server.app"
OUTPUT_ROOT="$ROOT/artifacts/installers/macos/$RID"
CLIENT_DMG="$OUTPUT_ROOT/RadioVault.Client-$VERSION-$RID-unsigned.dmg"
SERVER_DMG="$OUTPUT_ROOT/RadioVault.Server-$VERSION-$RID-unsigned.dmg"

case "$OUTPUT_ROOT" in
  "$ROOT"/artifacts/installers/macos/*) ;;
  *) echo "Refusing to modify a path outside the Radio Vault macOS installer directory." >&2; exit 1 ;;
esac

if [[ ! -d "$CLIENT_APP" || ! -d "$SERVER_APP" ]]; then
  echo "Create the macOS Client and Server app bundles before creating their disk images." >&2
  exit 1
fi

STAGING_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/radiovault-dmg.XXXXXX")"
trap 'rm -rf "$STAGING_ROOT"' EXIT
rm -rf "$OUTPUT_ROOT"
mkdir -p "$OUTPUT_ROOT"

create_dmg() {
  local source_app="$1"
  local executable="$2"
  local volume_name="$3"
  local output_path="$4"
  local stage="$STAGING_ROOT/$volume_name"

  mkdir -p "$stage"
  ditto "$source_app" "$stage/$(basename "$source_app")"
  chmod +x "$stage/$(basename "$source_app")/Contents/MacOS/$executable"
  xattr -cr "$stage/$(basename "$source_app")"
  codesign --force --deep --sign - "$stage/$(basename "$source_app")"
  ln -s /Applications "$stage/Applications"
  printf '%s\n' \
    "Install Radio Vault" \
    "" \
    "Drag the Radio Vault application into the Applications folder." \
    "This alpha build is ad-hoc signed and is not notarized by Apple." \
    > "$stage/Install Radio Vault.txt"

  hdiutil create \
    -volname "$volume_name" \
    -srcfolder "$stage" \
    -format UDZO \
    -ov \
    "$output_path"
  hdiutil verify "$output_path"
}

create_dmg "$CLIENT_APP" "TheRadioVault" "Radio Vault Client" "$CLIENT_DMG"
create_dmg "$SERVER_APP" "RadioVault.Server" "Radio Vault Server" "$SERVER_DMG"

echo "macOS Client disk image: $CLIENT_DMG"
echo "macOS Server disk image: $SERVER_DMG"
