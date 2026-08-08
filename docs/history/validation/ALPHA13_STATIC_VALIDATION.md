# Alpha 13 static validation

- Source version advanced to `0.28.0-alpha13-canonical-playback-transcript-cutover`.
- Canonical timeline location and source-to-logical timestamp mapping added.
- Desktop multipart playback retains one broadcast-level duration, position and progress identity.
- Transcript navigation resolves physical-file timestamps onto the canonical assembled timeline.
- Web API exposes complete canonical manifests and range-capable manifest-authorised media parts.
- Recording selection rejects incomplete or review-required options.
- Added smoke-test coverage for timeline mapping and stable canonical web routes.
- Schema remains 45; no destructive database or media-file migration was introduced.

The container does not include the .NET SDK, so Visual Studio compilation and runtime acceptance remain required.
