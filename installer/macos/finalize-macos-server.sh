#!/bin/zsh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
if [[ -d "$SCRIPT_DIR/Radio Vault Server.app" ]]; then
  DEFAULT_APP="$SCRIPT_DIR/Radio Vault Server.app"
  ENTITLEMENTS="$SCRIPT_DIR/RadioVaultServer.entitlements"
else
  ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
  DEFAULT_APP="$ROOT/artifacts/macos-server/osx-arm64/Radio Vault Server.app"
  ENTITLEMENTS="$SCRIPT_DIR/RadioVault.entitlements"
fi
APP="${1:-$DEFAULT_APP}"
IDENTITY="${DEVELOPER_ID_APPLICATION:-}"
NOTARY_PROFILE="${APPLE_NOTARY_PROFILE:-}"

if [[ ! -d "$APP" ]]; then
  echo "Radio Vault Server.app was not found: $APP" >&2
  exit 1
fi

EXECUTABLE="$APP/Contents/MacOS/RadioVault.Server"
chmod +x "$EXECUTABLE"

if [[ -z "$IDENTITY" ]]; then
  echo "Prepared an unsigned development server bundle. Set DEVELOPER_ID_APPLICATION to sign it."
  exit 0
fi

while IFS= read -r -d '' item; do
  codesign --force --timestamp --options runtime --sign "$IDENTITY" "$item"
done < <(find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -name '*.so' \) -print0)

codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$IDENTITY" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

FINAL_ZIP="${APP%.app}-signed.zip"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$FINAL_ZIP"

if [[ -n "$NOTARY_PROFILE" ]]; then
  xcrun notarytool submit "$FINAL_ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  rm -f "$FINAL_ZIP"
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$FINAL_ZIP"
  spctl --assess --type execute --verbose=2 "$APP"
fi

echo "Final Mac Server archive: $FINAL_ZIP"
