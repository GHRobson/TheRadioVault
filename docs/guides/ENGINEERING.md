# Radio Vault engineering baseline

v0.29 Alpha 2 is built on the accepted v0.28.0 implementation and remains on schema 45. The validated desktop corpus contains 7,169 physical files represented by 4,330 canonical broadcasts and 6,736 recordings, with the laptop established as a healthy strict subset rather than a divergent archive.

## Current boundary

The desktop process remains authoritative for SQLite and physical media. The web project exposes versioned application contracts and never reveals arbitrary filesystem paths. The PWA consumes a bounded bootstrap snapshot, canonical broadcast/media endpoints and explicit mutation endpoints.

Alpha 2 adds no archive migration. It advances the web capability generation to 2, adds server-side date/status facets, makes canonical broadcast identity explicit, preserves navigation through transient disconnection and adds privacy-safe client diagnostics.

## Non-negotiable safety rules

- Database schema stays 45.
- Audio/artwork offline cache identities remain stable.
- Offline progress cannot rewind newer authoritative progress.
- Web/LAN clients act through application services and canonical identifiers.
- WPF-specific dependencies must not move into Core, Services, Data, Media, Research, Transcription or Web.
