# UniBridge.VisualWorker context

2026-10-12: Independently authored optional companion for UniBridge visual
semantic asset retrieval. .NET 10 self-contained sidecar uses Microsoft ONNX
Runtime 1.23.2 CPU and pinned int8 CLIP ViT-B/32 dual encoders. It is a separate
process; it does not add Unity Player assemblies or Sentis dependencies, and
does not implement deferred Unity 7/CoreCLR qualification.

Artifacts are explicitly installed once into an external per-user model cache,
with fixed revision, byte lengths and SHA-256. Queries are offline. The worker
never launches a shell, receives one bounded JSON operation, verifies protocol
request/model identity, normalizes finite 512-dimensional vectors, and exits.
English text is the model's qualified query language.

Native Windows x64 qualification used independent opaque-file procedural image
fixtures 101–104: red car, green tree, red apple, yellow banana. Four real text
queries ranked the expected actual image first, including red car vs red apple
same-color discrimination; five negative setup/input/deadline cases and the
public BPE token contract passed in the portable standard-library-only worker
runner (10/10 checks). Fixture SHA-256/lengths are verified, existing models are
read from the documented user cache or an explicit external override, and the
runner requires explicit worker/unique report arguments. This is backend proof,
not yet Unity/MCP integration
acceptance. Linux/macOS RIDs are cross-published with matching source; physical
platform behavior is not claimed without corresponding native qualification.

Sources/provenance: OpenAI CLIP MIT original; Xenova public ONNX model conversion
at immutable revision 6ef1ebc8b0766a7a8d11b146462c99cdf74dd22d; Microsoft ONNX
Runtime MIT, .NET runtime MIT. Full upstream licenses/notices are retained. No
Unity Assistant code, assets or dependencies are copied.

The opt-in native fixture and ordinary MCP runner are shipped as source under
package Tools~/VisualSemanticRegression. They import only uniquely owned opaque
fixture PNGs, journal each data/meta write, preserve exact scene/dirty state,
observe real child admission before cancellation, validate structured output
with an independent JSON Schema validator, and SHA-check saved author files.
Actual Unity 6.6 SDK compilation and runner syntax/import checks passed; live
Mono/MCP action acceptance remains the host coordinator's required gate.
Review corrected Editor GUID normalization before native resolution/dedup/cache
identity; the native gate now requires real uppercase/lowercase references to
produce a single canonical lowercase asset entry. Worker binaries are unchanged.
The Editor tool now enforces its own closed input contract before gate/native/
cache work because schema advertisement does not validate incoming JObject data.
Malformed explicit references cannot trigger omitted-reference default indexing;
native negatives check isError, processed=0 and unchanged generated-index SHA.
Search now matches the native resolved GUID as well as local file ID and source/
dependency hashes. A native own-path replacement test forces other cache checks
current, proves the exact production identity predicate accepts the new GUID and
rejects the deleted old GUID, and records stale path lookup separately.
The early validator recognizes only ProjectContextGuard's exact internal relay
expected-project-root parameter (nonempty string); business schema stays closed.
Native regression includes ordinary relay injection and unknown-key rejection.

### 2026-10-12 — final host qualification complete / 0.2.86

The actual Windows x64 Mono Unity6000.6.5f1 ordinary MCP integration gate49/49 passed61 calls; backend worker10/10 and exact Unity SDK compile4/4 passed. Physical child cancellation/exit and follow-up query, graphics main-thread cleanup, GUID identity/content invalidation, malformed-input closed contract, cache restoration and all9027 original Test file hashes were confirmed. The owned same-path replacement fixture now seeds exactly32 ASCII GUID bytes in its own original meta; every other byte is preserved and native identity is checked after forced import. Domovyk/Corgi .86 batch compilation passed. Other RID binaries are built; native Unix execution is unqualified. No Unity7/CoreCLR Editor qualification or canceled ProcessInfoCollector crash work was performed.
