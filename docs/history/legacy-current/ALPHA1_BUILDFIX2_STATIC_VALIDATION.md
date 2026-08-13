# Alpha 1 Buildfix 2 static validation

- [x] Pairing client uses pre-serialised `ByteArrayContent`.
- [x] Pairing client disables `Expect: 100-continue` and requests HTTP/1.1-or-lower.
- [x] Built-in server recognises `Transfer-Encoding: chunked`.
- [x] Chunk decoder enforces the 16 KiB request-body limit.
- [x] Pairing 400/403 responses use the structured versioned JSON envelope.
- [x] Regression test sends a raw multi-chunk JSON mutation and expects HTTP 200.
- [x] Schema remains 45 and capability generation remains 8.
- [x] Anywhere shell/media cache identities remain unchanged.
