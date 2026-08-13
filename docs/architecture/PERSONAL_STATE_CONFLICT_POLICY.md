# Personal-state conflict policy

Radio Vault Server is authoritative, but an iPhone may legitimately create changes while it is offline. Every replayed personal-state mutation carries a stable mutation ID and, where ordering matters, the time the user made the decision. The server records both acknowledgements and the latest accepted decision per device and broadcast so a delayed retry cannot silently undo newer intent.

| State | Merge rule | Important exception |
|---|---|---|
| Playback progress | Newer captured progress wins; routine stale progress cannot resurrect an older position. | An explicit seek/reset is a new user action and may move backwards. |
| Listened / unlistened | Last captured user action wins, with deterministic mutation-ID ordering for equal timestamps. | Implausibly future-dated client clocks are rejected. |
| Favourite | Last captured user action wins, with deterministic mutation-ID ordering for equal timestamps. | Implausibly future-dated client clocks are rejected. |
| Moment | Append-only and idempotent by stable mutation ID. | A retried Moment returns its existing result instead of creating a duplicate. |
| Queue | Serialized by the server; clients refresh the canonical queue after mutation. | Offline queue edits are not merged because ordering intent cannot be inferred safely. |

The server decision ledger is durable and bounded. A rejected stale write returns the canonical state and conflict metadata so the client can reconcile its cache. Mutation callbacks complete before a decision is recorded, ensuring a failed database write can be retried instead of being falsely acknowledged.

Multi-device regression coverage exercises stale retries, same-time decisions, clock skew, duplicate Moments and independent devices making competing changes.
