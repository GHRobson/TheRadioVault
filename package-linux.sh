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

echo "Linux Client: $ARTIFACT_ROOT/RadioVault.Client-$VERSION-$RID.tar.gz"
echo "Linux Server: $ARTIFACT_ROOT/RadioVault.Server-$VERSION-$RID.tar.gz"
