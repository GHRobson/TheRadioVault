#!/bin/zsh

set -euo pipefail

SCRIPT_DIR="${0:A:h}"
SOURCE_ROOT="${SCRIPT_DIR:h}"
PROJECT="$SOURCE_ROOT/TheRadioVault.Client.iOS/TheRadioVault.Client.iOS.csproj"
TEAM_ID="${RADIOVAULT_APPLE_TEAM_ID:-HX789R9856}"
SIGNING_KEY="${RADIOVAULT_IOS_DISTRIBUTION_KEY:-Apple Distribution}"
PROFILE="${RADIOVAULT_IOS_APPSTORE_PROFILE:-}"
OUTPUT="$SOURCE_ROOT/artifacts/ios/testflight"

if [[ -z "$PROFILE" ]]; then
    print "Set RADIOVAULT_IOS_APPSTORE_PROFILE to the App Store provisioning profile name."
    print "The script will create an archive/IPA but will never upload it automatically."
    exit 2
fi

mkdir -p "$OUTPUT"
cd "$SOURCE_ROOT"

print "Building Radio Vault for TestFlight packaging…"
print "Team: $TEAM_ID"
print "Profile: $PROFILE"

DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}" \
dotnet publish "$PROJECT" \
    -c Release \
    -r ios-arm64 \
    -warnaserror \
    -p:ArchiveOnBuild=true \
    -p:BuildIpa=true \
    -p:DevelopmentTeam="$TEAM_ID" \
    -p:CodesignKey="$SIGNING_KEY" \
    -p:CodesignProvision="$PROFILE" \
    -p:IpaPackageDir="$OUTPUT"

print
print "TestFlight package prepared in:"
print "$OUTPUT"
print "Nothing has been uploaded. Review and submit it with Apple Transporter or Xcode Organizer."
