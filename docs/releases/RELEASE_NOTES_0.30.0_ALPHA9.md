# Radio Vault v0.30.0 Alpha 9 — Parity Audit & Hardening

Build identity: `0.30.0-alpha9-parity-audit-hardening-buildfix2`

Buildfix 2 adds bounded, diagnostic shutdown handling after acceptance testing found an intermittent quit hang. It does not change the Alpha 9 parity contract, schema or LAN capabilities.

Alpha 9 audits the normal Radio Vault interface surface by surface and closes the remaining high-value gaps between local operation and server/remote-client operation.

## What changed

### Metadata and artwork

The normal Metadata Studio now works against the server from a remote client. It loads broadcast identity, headline, edition, people, topics, description and notes through the paired connection, then saves through the existing server metadata mutation. Server artwork is downloaded securely and cached under a server-specific location for Library rows, Broadcast Info and Metadata Studio.

Headline-review decisions and artwork-file replacement remain server-owned because they alter server-maintained archive evidence. Alpha 9 blocks those paths explicitly; it never falls through to a remote client's local database.

### Research workspace

The normal Research workspace now reads the server's overview, research records, filters, source diagnostics, import history and record details. Global search can return server Research records and Explore can show server source publishers. Research pack preview/import/export remains server-backed from Alpha 6.

Advanced match reconciliation, repair, rollback and database-quality decisions remain deliberately server-owned. They are visible as such rather than appearing to succeed against unrelated local state.

### Parity diagnostics

The server publishes `/api/v1/federation/parity`, a bounded structured snapshot of the normal application surfaces available to remote clients. Connected Access Settings shows the result and can copy a diagnostic report containing server version, API, capability generation, Library revision, change sequence and each surface's access level.

### Compatibility

- Database schema: **45**
- LAN capability generation: **14**
- New capabilities: `lan.parity-audit`, `lan.research-workspace`
- Radio Vault Anywhere cache identities and web-shell generation are unchanged.

Alpha 9 must be installed on the server and remote client together. Alpha 8 caches are identity-bound and can be rebuilt safely after upgrade.
