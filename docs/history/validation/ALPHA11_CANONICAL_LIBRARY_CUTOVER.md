# Alpha 11 canonical-library cutover

## Authoritative read boundary

The user-facing library is loaded from the Library Truth run referenced by the
latest completed, commit-verified adoption record. This prevents a later shadow
analysis from silently changing the live UI contract before another guarded
adoption occurs.

The projection returns every broadcast in that adopted truth run exactly once:

1. adopted groups resolve through `episode_canonical_map` to their survivor;
2. review-recommended and blocked groups select a deterministic representative
   from the preferred recording while remaining marked `Needs attention`;
3. state is aggregated across the group's legacy members;
4. recording, segment and physical-file counts remain visible as structure, not
   top-level identity.

## Compatibility routing

Saved episode IDs remain valid. The retained map and held-group membership are
used by desktop search, Moments, queue entries, transcript links, research links,
archive-health links and remote playback. Adopted aliases resolve to the survivor.
Held members resolve to the group's deterministic representative.

Existing queue rows are resolved on read, and newly queued broadcasts store the
representative episode ID. Retained aliases therefore remain compatible without
reappearing as separate Library or queue identities.

## State writes

Adopted broadcasts write favourite and listening state to the survivor row. Held
groups propagate categorical favourite/played changes across their current
members where required, while active playback uses the deterministic
representative until the group is safely adopted.

For preferred recordings with ordered coverage, playback position and duration
use the complete logical broadcast timeline. Segment transitions do not reset
progress. Resume, seek, completion, Moments and remote handoff translate between
that logical position and the current physical file automatically.

Historical Alpha 10 multipart positions were merged from legacy per-file state
without enough evidence to reconstruct exact segment-relative offsets. Alpha 11
preserves that migrated value rather than attempting a risky retrospective
rewrite; new progress is logical from this release onward.

## Playback boundary

Desktop playback now consumes `CanonicalPlaybackPlan` for adopted broadcasts and
for held broadcasts whose preferred recording has direct, non-review coverage.
The plan selects an available physical source for each ordered segment and moves
to the next segment without exposing parts as separate episodes.

When a held group has unresolved coverage, Radio Vault deliberately falls back to
its representative physical file. Alpha 11 does not guess segment order from the
filename at playback time.

## Remaining cutovers

The next canonical phases are:

1. alternate-recording selection and recording management;
2. canonical multipart manifests for web playback and offline downloads;
3. transcript assembly and transcript-to-segment navigation;
4. canonical smart collections, recommendation evidence and LAN federation
   contracts where any legacy episode assumptions remain.
