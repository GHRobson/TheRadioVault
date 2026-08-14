#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RID="${RUNTIME_IDENTIFIER:-osx-arm64}"
CONFIGURATION="${CONFIGURATION:-Release}"
DOTNET_EXE="${RADIOVAULT_DOTNET:-dotnet}"
SKIP_PUBLISH=false
CREATE_DMG=true

usage() {
  cat <<'EOF'
Usage: ./package-macos-local.sh [--skip-publish] [--no-dmg]

Builds self-contained Radio Vault Client and Server app bundles, portable ZIPs,
and local unsigned DMG installers on Apple Silicon macOS.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-publish) SKIP_PUBLISH=true ;;
    --no-dmg) CREATE_DMG=false ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

if [[ "$RID" != "osx-arm64" && "$RID" != "osx-x64" ]]; then
  echo "RUNTIME_IDENTIFIER must be osx-arm64 or osx-x64." >&2
  exit 2
fi

VERSION="$(tr -d '\r\n' < "$ROOT/VERSION.txt")"
SHORT_VERSION="$(printf '%s' "$VERSION" | sed -E 's/^([0-9]+\.[0-9]+\.[0-9]+).*/\1/')"
BUNDLE_VERSION="${APPLICATION_BUILD:-48}"
BUILD_COMMIT="${RADIOVAULT_BUILD_IDENTITY:-${GITHUB_SHA:-$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || true)}}"
if [[ ! "$BUILD_COMMIT" =~ ^[0-9a-fA-F]{7,64}$ ]]; then BUILD_COMMIT="local"; fi
BUILD_COMMIT="$(printf '%s' "$BUILD_COMMIT" | tr '[:upper:]' '[:lower:]')"
BUILD_SHORT="${BUILD_COMMIT:0:12}"
SOURCE_DIRTY=false
if [[ "$BUILD_COMMIT" != "local" && -z "${GITHUB_SHA:-}" && -n "$(git -C "$ROOT" status --porcelain)" ]]; then
  SOURCE_DIRTY=true
  BUILD_SHORT="$BUILD_SHORT.dirty"
fi
BUILD_IDENTITY="$VERSION+$BUILD_SHORT"
EMBEDDED_BUILD_IDENTITY="$BUILD_COMMIT"
if [[ "$SOURCE_DIRTY" == true ]]; then EMBEDDED_BUILD_IDENTITY="$EMBEDDED_BUILD_IDENTITY.dirty"; fi
export RADIOVAULT_BUILD_IDENTITY="$EMBEDDED_BUILD_IDENTITY"
CLIENT_ARTIFACT="$ROOT/artifacts/macos/$RID"
SERVER_ARTIFACT="$ROOT/artifacts/macos-server/$RID"

case "$CLIENT_ARTIFACT:$SERVER_ARTIFACT" in
  "$ROOT"/artifacts/macos/*:"$ROOT"/artifacts/macos-server/*) ;;
  *) echo "Refusing to modify paths outside the Radio Vault artifact folders." >&2; exit 1 ;;
esac

create_icns() {
  local source_png="$1"
  local destination="$2"
  ruby - "$source_png" "$destination" <<'RUBY'
source, destination = ARGV
png = File.binread(source)
File.binwrite(
  destination,
  "icns" + [16 + png.bytesize].pack("N") + "ic09" + [8 + png.bytesize].pack("N") + png)
RUBY
}

write_plist() {
  local template="$1"
  local destination="$2"
  sed \
    -e "s/@SHORT_VERSION@/$SHORT_VERSION/g" \
    -e "s/@BUNDLE_VERSION@/$BUNDLE_VERSION/g" \
    "$template" > "$destination"
  plutil -lint "$destination" >/dev/null
}

create_manifest() {
  local artifact_root="$1"
  local product="$2"
  local bundle_id="$3"
  local entry_point="$4"
  local manifest="$artifact_root/manifest.txt"
  {
    echo "product=$product"
    echo "version=$VERSION"
    echo "buildIdentity=$BUILD_IDENTITY"
    echo "commit=$BUILD_COMMIT"
    echo "sourceDirty=$SOURCE_DIRTY"
    echo "runtimeIdentifier=$RID"
    echo "bundleIdentifier=$bundle_id"
    echo "entryPoint=$entry_point"
    echo "signed=false"
    echo "requiresMacFinalization=true"
    echo
    find "$artifact_root" -type f ! -name 'manifest.txt' ! -name '*.zip' -print0 \
      | sort -z \
      | while IFS= read -r -d '' file; do
          relative="${file#"$artifact_root/"}"
          printf '%s  %s\n' "$(shasum -a 256 "$file" | awk '{print $1}')" "$relative"
        done
  } > "$manifest"
}

package_product() {
  local product="$1"
  local project="$2"
  local executable="$3"
  local logo="$4"
  local plist_template="$5"
  local icon_name="$6"
  local bundle_id="$7"
  local artifact_root="$8"
  local bundle_name="$9"
  local entitlement_source="${10}"
  local entitlement_name="${11}"
  local finalizer_source="${12}"
  local finalizer_name="${13}"
  local publish_root="$artifact_root/publish"
  local bundle_root="$artifact_root/$bundle_name.app"
  local contents_root="$bundle_root/Contents"
  local archive="$artifact_root/RadioVault.${product// /}-$VERSION-$RID-unsigned.zip"

  if [[ "$SKIP_PUBLISH" == false ]]; then
    rm -rf "$artifact_root"
    mkdir -p "$publish_root"
    "$DOTNET_EXE" publish "$project" \
      -c "$CONFIGURATION" -r "$RID" --self-contained true \
      -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false \
      -p:ContinuousIntegrationBuild=true -o "$publish_root"
  elif [[ ! -x "$publish_root/$executable" ]]; then
    echo "--skip-publish requires $publish_root/$executable" >&2
    exit 1
  fi

  ruby -rjson -rtime - "$publish_root/BUILD_INFO.json" "$product" "$VERSION" "$BUILD_IDENTITY" "$BUILD_COMMIT" "$RID" "$SOURCE_DIRTY" <<'RUBY'
path, product, version, identity, commit, runtime, source_dirty = ARGV
document = {
  "product" => "Radio Vault #{product}",
  "version" => version,
  "buildIdentity" => identity,
  "commit" => commit == "local" ? nil : commit,
  "sourceDirty" => source_dirty == "true",
  "generatedAtUtc" => Time.now.utc.iso8601(7),
  "runtime" => runtime,
  "role" => product == "Server" ? "authoritative-server" : "desktop-client"
}
File.write(path, JSON.pretty_generate(document) + "\n")
RUBY

  rm -rf "$bundle_root" "$archive"
  mkdir -p "$contents_root/MacOS" "$contents_root/Resources"
  ditto "$publish_root" "$contents_root/MacOS"
  chmod +x "$contents_root/MacOS/$executable"
  write_plist "$plist_template" "$contents_root/Info.plist"
  cp "$logo" "$contents_root/Resources/${icon_name%.icns}.png"
  create_icns "$logo" "$contents_root/Resources/$icon_name"
  cp "$entitlement_source" "$artifact_root/$entitlement_name"
  cp "$finalizer_source" "$artifact_root/$finalizer_name"
  chmod +x "$artifact_root/$finalizer_name"
  create_manifest "$artifact_root" "Radio Vault $product" "$bundle_id" "$bundle_name.app/Contents/MacOS/$executable"

  local stage
  stage="$(mktemp -d "${TMPDIR:-/tmp}/radiovault-zip.XXXXXX")"
  ditto "$bundle_root" "$stage/$bundle_name.app"
  cp "$artifact_root/$entitlement_name" "$artifact_root/$finalizer_name" "$artifact_root/manifest.txt" "$stage/"
  ditto -c -k --sequesterRsrc "$stage/" "$archive"
  rm -rf "$stage"
  echo "$product bundle: $bundle_root"
  echo "$product portable archive: $archive"
}

package_product \
  "Client" \
  "$ROOT/TheRadioVault.Desktop.Avalonia/TheRadioVault.Desktop.Avalonia.csproj" \
  "TheRadioVault" \
  "$ROOT/TheRadioVault.Desktop.Avalonia/Assets/RadioVault-Logo.png" \
  "$ROOT/installer/macos/Info.plist" \
  "RadioVault.icns" \
  "com.theradiovault.client" \
  "$CLIENT_ARTIFACT" \
  "Radio Vault" \
  "$ROOT/installer/macos/RadioVault.entitlements" \
  "RadioVault.entitlements" \
  "$ROOT/installer/macos/finalize-macos-client.sh" \
  "finalize-macos-client.sh"

package_product \
  "Server" \
  "$ROOT/TheRadioVault.Server/TheRadioVault.Server.csproj" \
  "RadioVault.Server" \
  "$ROOT/TheRadioVault.Server/Assets/RadioVault.Server-Logo.png" \
  "$ROOT/installer/macos/ServerInfo.plist" \
  "RadioVaultServer.icns" \
  "com.theradiovault.server" \
  "$SERVER_ARTIFACT" \
  "Radio Vault Server" \
  "$ROOT/installer/macos/RadioVault.entitlements" \
  "RadioVaultServer.entitlements" \
  "$ROOT/installer/macos/finalize-macos-server.sh" \
  "finalize-macos-server.sh"

if [[ "$CREATE_DMG" == true ]]; then
  "$ROOT/package-macos-installers.sh"
fi
