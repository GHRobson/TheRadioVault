#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
RID="${RUNTIME_IDENTIFIER:-linux-x64}"
DOTNET_EXE="${DOTNET_EXE:-dotnet}"
VERSION="$(tr -d '\r\n' < "$ROOT/VERSION.txt")"
ARTIFACT_ROOT="$ROOT/artifacts/linux/$RID"
CLIENT_PUBLISH="$ARTIFACT_ROOT/publish-client"
SERVER_PUBLISH="$ARTIFACT_ROOT/publish-server"
CLIENT_BUNDLE="$ARTIFACT_ROOT/RadioVault.Client-$VERSION-$RID"
SERVER_BUNDLE="$ARTIFACT_ROOT/RadioVault.Server-$VERSION-$RID"
DEB_ARCH="amd64"
CLIENT_DEB_ROOT="$ARTIFACT_ROOT/deb-client"
SERVER_DEB_ROOT="$ARTIFACT_ROOT/deb-server"

case "$ARTIFACT_ROOT" in
  "$ROOT"/artifacts/linux/*) ;;
  *) echo "Refusing to modify a path outside the Radio Vault Linux artifact directory." >&2; exit 1 ;;
esac

rm -rf "$ARTIFACT_ROOT"
mkdir -p "$CLIENT_PUBLISH" "$SERVER_PUBLISH" "$CLIENT_BUNDLE" "$SERVER_BUNDLE"

"$DOTNET_EXE" publish "$ROOT/TheRadioVault.Desktop.Avalonia/TheRadioVault.Desktop.Avalonia.csproj" \
  -c "$CONFIGURATION" -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false \
  -p:ContinuousIntegrationBuild=true -o "$CLIENT_PUBLISH"
"$DOTNET_EXE" publish "$ROOT/TheRadioVault.Server/TheRadioVault.Server.csproj" \
  -c "$CONFIGURATION" -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false \
  -p:ContinuousIntegrationBuild=true -o "$SERVER_PUBLISH"

cp -R "$CLIENT_PUBLISH"/. "$CLIENT_BUNDLE/"
cp -R "$SERVER_PUBLISH"/. "$SERVER_BUNDLE/"
cp "$ROOT/TheRadioVault.Desktop.Avalonia/Assets/RadioVault-Logo.png" "$CLIENT_BUNDLE/RadioVault.png"
cp "$ROOT/TheRadioVault.Server/Assets/RadioVault.Server-Logo.png" "$SERVER_BUNDLE/RadioVaultServer.png"

cat > "$CLIENT_BUNDLE/run-radiovault.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/TheRadioVault" "$@"
EOF
cat > "$SERVER_BUNDLE/run-radiovault-server.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/RadioVault.Server" "$@"
EOF
cat > "$CLIENT_BUNDLE/README.txt" <<EOF
Radio Vault Linux Client $VERSION

Run ./run-radiovault.sh from a graphical Linux desktop.
Audio playback uses mpv. Install mpv with your distribution's software manager.
Pair the client with any Radio Vault Server from Settings.
EOF
cat > "$SERVER_BUNDLE/README.txt" <<EOF
Radio Vault Linux Server $VERSION

Run ./run-radiovault-server.sh from a graphical Linux desktop.
The server stores its database under your normal per-user application-data folder.
Use the server settings window to add archive folders, enable network access and create pairing codes.
EOF
chmod +x "$CLIENT_BUNDLE/TheRadioVault" "$CLIENT_BUNDLE/run-radiovault.sh"
chmod +x "$SERVER_BUNDLE/RadioVault.Server" "$SERVER_BUNDLE/run-radiovault-server.sh"

tar -C "$ARTIFACT_ROOT" -czf "$ARTIFACT_ROOT/RadioVault.Client-$VERSION-$RID.tar.gz" "$(basename "$CLIENT_BUNDLE")"
tar -C "$ARTIFACT_ROOT" -czf "$ARTIFACT_ROOT/RadioVault.Server-$VERSION-$RID.tar.gz" "$(basename "$SERVER_BUNDLE")"

if ! command -v dpkg-deb >/dev/null 2>&1; then
  echo "dpkg-deb is required to create the Linux installers." >&2
  exit 1
fi

mkdir -p \
  "$CLIENT_DEB_ROOT/DEBIAN" \
  "$CLIENT_DEB_ROOT/usr/bin" \
  "$CLIENT_DEB_ROOT/usr/lib/radiovault-client" \
  "$CLIENT_DEB_ROOT/usr/share/applications" \
  "$CLIENT_DEB_ROOT/usr/share/icons/hicolor/512x512/apps" \
  "$SERVER_DEB_ROOT/DEBIAN" \
  "$SERVER_DEB_ROOT/usr/bin" \
  "$SERVER_DEB_ROOT/usr/lib/radiovault-server" \
  "$SERVER_DEB_ROOT/usr/share/applications" \
  "$SERVER_DEB_ROOT/usr/share/icons/hicolor/512x512/apps"

cp -R "$CLIENT_BUNDLE"/. "$CLIENT_DEB_ROOT/usr/lib/radiovault-client/"
cp -R "$SERVER_BUNDLE"/. "$SERVER_DEB_ROOT/usr/lib/radiovault-server/"
cp "$CLIENT_BUNDLE/RadioVault.png" "$CLIENT_DEB_ROOT/usr/share/icons/hicolor/512x512/apps/radiovault.png"
cp "$SERVER_BUNDLE/RadioVaultServer.png" "$SERVER_DEB_ROOT/usr/share/icons/hicolor/512x512/apps/radiovault-server.png"

cat > "$CLIENT_DEB_ROOT/DEBIAN/control" <<EOF
Package: radiovault-client
Version: $VERSION
Section: sound
Priority: optional
Architecture: $DEB_ARCH
Depends: libc6, libx11-6, libxrandr2, libxi6, libgl1, libfontconfig1, libfreetype6, mpv
Maintainer: Radio Vault <ghrobson@me.com>
Description: Radio Vault desktop client
 Listen to and organise a Radio Vault collection from a Linux desktop.
EOF
cat > "$SERVER_DEB_ROOT/DEBIAN/control" <<EOF
Package: radiovault-server
Version: $VERSION
Section: sound
Priority: optional
Architecture: $DEB_ARCH
Depends: libc6, libx11-6, libxrandr2, libxi6, libgl1, libfontconfig1, libfreetype6
Maintainer: Radio Vault <ghrobson@me.com>
Description: Radio Vault collection server
 Organise a radio archive and make it available to Radio Vault clients.
EOF
cat > "$CLIENT_DEB_ROOT/usr/bin/radiovault" <<'EOF'
#!/usr/bin/env bash
exec /usr/lib/radiovault-client/TheRadioVault "$@"
EOF
cat > "$SERVER_DEB_ROOT/usr/bin/radiovault-server" <<'EOF'
#!/usr/bin/env bash
exec /usr/lib/radiovault-server/RadioVault.Server "$@"
EOF
cat > "$CLIENT_DEB_ROOT/usr/share/applications/radiovault.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Radio Vault
Comment=Listen to and organise your radio collection
Exec=radiovault
Icon=radiovault
Terminal=false
Categories=Audio;AudioVideo;
EOF
cat > "$SERVER_DEB_ROOT/usr/share/applications/radiovault-server.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Radio Vault Server
Comment=Manage and share your Radio Vault collection
Exec=radiovault-server
Icon=radiovault-server
Terminal=false
Categories=Audio;AudioVideo;
EOF
chmod 0755 \
  "$CLIENT_DEB_ROOT/usr/bin/radiovault" \
  "$SERVER_DEB_ROOT/usr/bin/radiovault-server" \
  "$CLIENT_DEB_ROOT/usr/lib/radiovault-client/TheRadioVault" \
  "$SERVER_DEB_ROOT/usr/lib/radiovault-server/RadioVault.Server"

dpkg-deb --build --root-owner-group "$CLIENT_DEB_ROOT" "$ARTIFACT_ROOT/RadioVault.Client-$VERSION-$DEB_ARCH.deb"
dpkg-deb --build --root-owner-group "$SERVER_DEB_ROOT" "$ARTIFACT_ROOT/RadioVault.Server-$VERSION-$DEB_ARCH.deb"

echo "Linux Client: $ARTIFACT_ROOT/RadioVault.Client-$VERSION-$RID.tar.gz"
echo "Linux Server: $ARTIFACT_ROOT/RadioVault.Server-$VERSION-$RID.tar.gz"
echo "Linux Client installer: $ARTIFACT_ROOT/RadioVault.Client-$VERSION-$DEB_ARCH.deb"
echo "Linux Server installer: $ARTIFACT_ROOT/RadioVault.Server-$VERSION-$DEB_ARCH.deb"
