# Radio Vault v0.30.0 Alpha 1

## Multi-Device Library Access begins

This release establishes the secure relationship between a Radio Vault server and another remote client application on the same private network.

### New

- Privacy-safe automatic LAN discovery.
- Recognisable server names.
- Temporary six-digit remote-client pairing codes.
- Separate revocable credentials for paired remote clients.
- Certificate-pinned HTTPS connections.
- Authenticated remote-client bootstrap verification.
- Saved remote-server identity and connection testing.
- Connected access settings for server and client roles.

### Not yet included

The main server Library and playback interface still uses the local database and local application services. Alpha 2 will introduce the remote application-service boundary and switch the remote-client shell between local and server modes.

### Compatibility

No database migration is included. Radio Vault Anywhere, offline downloads and the accepted v0.29.0 cache identities remain unchanged.
