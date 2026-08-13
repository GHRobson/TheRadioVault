#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
RUN_IOS=true
RUN_PACKAGE=false
RESTORE=true

if [[ "${RADIOVAULT_GATE_LOGGING:-0}" != "1" ]]; then
  LOG_ROOT="$ROOT/artifacts/local"
  mkdir -p "$LOG_ROOT"
  LOG_PATH="$LOG_ROOT/release-gate-$(date +%Y%m%d-%H%M%S).log"
  set +e
  RADIOVAULT_GATE_LOGGING=1 RADIOVAULT_GATE_LOG_PATH="$LOG_PATH" "$0" "$@" 2>&1 | tee "$LOG_PATH"
  GATE_STATUS=${PIPESTATUS[0]}
  set -e
  exit "$GATE_STATUS"
fi
LOG_PATH="${RADIOVAULT_GATE_LOG_PATH:?The parent release-gate process did not supply a log path.}"

usage() {
  cat <<'EOF'
Usage: ./local-release-gate.sh [--no-ios] [--package] [--no-restore]

Runs the macOS development release gate entirely on this Mac. It does not use
GitHub Actions and it does not push or merge any branch.

  --no-ios      Skip the iOS simulator build and mobile regression runner
  --package     Also create local Client and Server ZIP/DMG installers
  --no-restore  Reuse an existing successful NuGet restore
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-ios) RUN_IOS=false ;;
    --package) RUN_PACKAGE=true ;;
    --no-restore) RESTORE=false ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

PINNED_SDK="$(sed -nE 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$ROOT/global.json" | head -1)"

find_dotnet() {
  local candidate
  for candidate in \
    "${RADIOVAULT_DOTNET:-}" \
    "${DOTNET_ROOT:-}/dotnet" \
    "$ROOT/.dotnet/dotnet" \
    "/usr/local/share/dotnet/dotnet" \
    "$(command -v dotnet 2>/dev/null || true)"; do
    [[ -n "$candidate" && -x "$candidate" ]] || continue
    if "$candidate" --list-sdks 2>/dev/null | awk '{print $1}' | grep -Fxq "$PINNED_SDK"; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

echo "Radio Vault local release gate"
echo "Branch: $(git -C "$ROOT" branch --show-current)"
echo "Commit: $(git -C "$ROOT" rev-parse --short HEAD)"
echo "SDK: $PINNED_SDK"
echo "Log: $LOG_PATH"

version_value() {
  sed -nE "s/.*<$2>([^<]+)<\/$2>.*/\1/p" "$1" | head -1
}

VERSION="$(tr -d '\r\n' < "$ROOT/VERSION.txt")"
DESKTOP_VERSION="$(version_value "$ROOT/TheRadioVault.Desktop.Avalonia/TheRadioVault.Desktop.Avalonia.csproj" Version)"
SERVER_VERSION="$(version_value "$ROOT/TheRadioVault.Server/TheRadioVault.Server.csproj" Version)"
IOS_VERSION="$(version_value "$ROOT/TheRadioVault.Client.iOS/TheRadioVault.Client.iOS.csproj" ApplicationDisplayVersion)"
IOS_PLIST_VERSION="$(plutil -extract CFBundleShortVersionString raw "$ROOT/TheRadioVault.Client.iOS/Info.plist")"
IOS_BUILD="$(version_value "$ROOT/TheRadioVault.Client.iOS/TheRadioVault.Client.iOS.csproj" ApplicationVersion)"
IOS_PLIST_BUILD="$(plutil -extract CFBundleVersion raw "$ROOT/TheRadioVault.Client.iOS/Info.plist")"

if [[ "$VERSION" != "$DESKTOP_VERSION" || "$VERSION" != "$SERVER_VERSION" || "$VERSION" != "$IOS_VERSION" || "$VERSION" != "$IOS_PLIST_VERSION" ]]; then
  echo "Version mismatch: VERSION=$VERSION desktop=$DESKTOP_VERSION server=$SERVER_VERSION iOS=$IOS_VERSION plist=$IOS_PLIST_VERSION" >&2
  exit 1
fi
if [[ "$IOS_BUILD" != "$IOS_PLIST_BUILD" ]]; then
  echo "iOS build mismatch: project=$IOS_BUILD plist=$IOS_PLIST_BUILD" >&2
  exit 1
fi
echo "Version consistency: $VERSION ($IOS_BUILD)"

git -C "$ROOT" diff --check
if rg -n '^(<<<<<<< .+|=======|>>>>>>> .+)$' "$ROOT" \
  --glob '!bin/**' --glob '!obj/**' --glob '!artifacts/**' --glob '!.git/**'; then
  echo "Merge conflict markers remain in the source." >&2
  exit 1
fi
while IFS= read -r -d '' xml_file; do
  xmllint --noout "$xml_file"
done < <(find "$ROOT" \
  -type d \( -name bin -o -name obj -o -name artifacts -o -name .git \) -prune -o \
  -type f \( -name '*.csproj' -o -name '*.props' -o -name '*.targets' -o -name '*.axaml' -o -name '*.xml' \) -print0)
plutil -lint "$ROOT/TheRadioVault.Client.iOS/Info.plist" >/dev/null
bash -n "$ROOT/local-release-gate.sh" "$ROOT/package-macos-local.sh" "$ROOT/package-macos-installers.sh" "$ROOT/tools/package-source-local.sh"
echo "Static source validation: passed"

if ! DOTNET_EXE="$(find_dotnet)"; then
  cat >&2 <<EOF
Radio Vault requires .NET SDK $PINNED_SDK, but it is not installed on this Mac.
Install the Apple Silicon .NET 10 SDK, then run this command again:
https://dotnet.microsoft.com/download/dotnet/10.0

The existing .NET 8 runtime should remain installed because published Radio
Vault builds can still require it.
EOF
  exit 1
fi
export RADIOVAULT_DOTNET="$DOTNET_EXE"

if [[ -d /Applications/Xcode.app/Contents/Developer ]]; then
  export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
elif [[ "$RUN_IOS" == true ]]; then
  echo "Xcode is required for the iOS simulator build." >&2
  exit 1
fi

if [[ "$RESTORE" == true ]]; then
  "$DOTNET_EXE" restore "$ROOT/TheRadioVault.sln"
  "$DOTNET_EXE" restore "$ROOT/TheRadioVault.Client.Mobile.Tests/TheRadioVault.Client.Mobile.Tests.csproj"
  if [[ "$RUN_IOS" == true ]]; then
    "$DOTNET_EXE" restore "$ROOT/TheRadioVault.Client.iOS/TheRadioVault.Client.iOS.csproj" -r iossimulator-arm64
  fi
fi

"$DOTNET_EXE" build "$ROOT/TheRadioVault.Desktop.Avalonia/TheRadioVault.Desktop.Avalonia.csproj" \
  -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
"$DOTNET_EXE" build "$ROOT/TheRadioVault.Server/TheRadioVault.Server.csproj" \
  -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
"$DOTNET_EXE" build "$ROOT/TheRadioVault.Tests/TheRadioVault.Tests.csproj" \
  -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
"$DOTNET_EXE" build "$ROOT/TheRadioVault.Data.Tests/TheRadioVault.Data.Tests.csproj" \
  -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
"$DOTNET_EXE" build "$ROOT/TheRadioVault.Services.Tests/TheRadioVault.Services.Tests.csproj" \
  -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
"$DOTNET_EXE" build "$ROOT/TheRadioVault.LibraryTruth.Tests/TheRadioVault.LibraryTruth.Tests.csproj" \
  -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
"$DOTNET_EXE" build "$ROOT/TheRadioVault.Transcription.Tests/TheRadioVault.Transcription.Tests.csproj" \
  -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
"$DOTNET_EXE" build "$ROOT/TheRadioVault.Web.Tests/TheRadioVault.Web.Tests.csproj" \
  -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
"$DOTNET_EXE" build "$ROOT/TheRadioVault.SourceChecks/TheRadioVault.SourceChecks.csproj" \
  -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true

"$DOTNET_EXE" run --project "$ROOT/TheRadioVault.SourceChecks/TheRadioVault.SourceChecks.csproj" \
  -c "$CONFIGURATION" --no-build

"$DOTNET_EXE" run --project "$ROOT/TheRadioVault.Tests/TheRadioVault.Tests.csproj" \
  -c "$CONFIGURATION" --no-build -- \
  "Mac client remains usable before server pairing" \
  "Mac Client uses native AVFoundation and existing server contracts" \
  "macOS and Linux packages preserve the shared client-server boundary" \
  "Product versions remain consistent" \
  "iOS Client preserves native platform and server boundaries" \
  "Repeated iPhone handoffs bypass dormant decoder gating" \
  "Playback startup succeeds only after decoder readiness" \
  "Playback startup reports a distinct decoder timeout" \
  "Playback startup distinguishes unavailable media" \
  "Playback startup preserves caller cancellation" \
  "Playback startup cancels and serializes superseded selections" \
  "Canonical personal state writes roll back atomically"

"$DOTNET_EXE" run --project "$ROOT/TheRadioVault.Data.Tests/TheRadioVault.Data.Tests.csproj" \
  -c "$CONFIGURATION" --no-build

"$DOTNET_EXE" run --project "$ROOT/TheRadioVault.Services.Tests/TheRadioVault.Services.Tests.csproj" \
  -c "$CONFIGURATION" --no-build

"$DOTNET_EXE" run --project "$ROOT/TheRadioVault.LibraryTruth.Tests/TheRadioVault.LibraryTruth.Tests.csproj" \
  -c "$CONFIGURATION" --no-build

"$DOTNET_EXE" run --project "$ROOT/TheRadioVault.Transcription.Tests/TheRadioVault.Transcription.Tests.csproj" \
  -c "$CONFIGURATION" --no-build

"$DOTNET_EXE" run --project "$ROOT/TheRadioVault.Web.Tests/TheRadioVault.Web.Tests.csproj" \
  -c "$CONFIGURATION" --no-build

if [[ "$RUN_IOS" == true ]]; then
  "$DOTNET_EXE" build "$ROOT/TheRadioVault.Client.Mobile.Tests/TheRadioVault.Client.Mobile.Tests.csproj" \
    -c "$CONFIGURATION" --no-restore -warnaserror -p:ContinuousIntegrationBuild=true
  "$DOTNET_EXE" run --project "$ROOT/TheRadioVault.Client.Mobile.Tests/TheRadioVault.Client.Mobile.Tests.csproj" \
    -c "$CONFIGURATION" --no-build
  "$DOTNET_EXE" build "$ROOT/TheRadioVault.Client.iOS/TheRadioVault.Client.iOS.csproj" \
    -c "$CONFIGURATION" -r iossimulator-arm64 --no-restore -warnaserror \
    -p:ContinuousIntegrationBuild=true -p:Deterministic=true
fi

if [[ "$RUN_PACKAGE" == true ]]; then
  "$ROOT/package-macos-local.sh"
fi

echo "Local release gate passed."
echo "Validation log: $LOG_PATH"
