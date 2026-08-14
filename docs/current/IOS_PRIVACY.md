# Radio Vault for iPhone privacy notice

Radio Vault is a client for a Radio Vault Server controlled by the person using
the app. The Radio Vault project does not operate a hosted library service and
does not receive analytics, advertising identifiers, listening history or the
contents of a user's archive.

The app stores the paired server address and an access credential on the
iPhone so it can connect to that server. Credentials are kept in the iOS
Keychain. Library metadata, artwork, playback decisions and requested audio
downloads are cached in the app's private container to support offline use.
This information is exchanged only with the server chosen by the user.

Radio Vault contains no advertising or tracking SDK and does not sell or share
personal information. It does not contain AI features. AI tools have been used
during development, as disclosed in the project README.

Diagnostics can be exported from Settings only when the user explicitly asks
the app to create and share a report. Exported reports redact network
addresses, access tokens, pairing codes, passwords and certificates. The user
chooses the destination using the standard iOS share sheet.

Deleting the app removes its local cache and downloaded audio. The paired
Radio Vault Server remains under the user's control and has its own data and
backup settings.

Questions can be raised through the public Radio Vault project at
https://github.com/GHRobson/TheRadioVault/issues.
