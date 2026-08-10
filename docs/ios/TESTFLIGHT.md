# Radio Vault iOS TestFlight preparation

Radio Vault can create an App Store-signed iPhone archive without uploading it or merging the iOS feature branch.

1. In Apple Developer, create an App Store provisioning profile for `com.ghrobson.theradiovault`. The profile must include the multicast networking capability used for local server discovery.
2. Install the matching Apple Distribution certificate and provisioning profile on the build Mac.
3. Set `RADIOVAULT_IOS_APPSTORE_PROFILE` to the profile name. If necessary, set `RADIOVAULT_APPLE_TEAM_ID` and `RADIOVAULT_IOS_DISTRIBUTION_KEY` too.
4. Run `tools/Build-iOS-TestFlight.command`.
5. Review the IPA in `artifacts/ios/testflight`, then upload it manually with Xcode Organizer or Transporter when testing is approved.

The packaging command never uploads a build and does not alter Git branches.
