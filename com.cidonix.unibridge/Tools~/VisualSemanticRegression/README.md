# Visual semantic regression

This is real, offline text-to-image CLIP retrieval. The independent opaque 101–104
fixtures contain a red car, a green tree, a red apple and a yellow banana. Names
are not ranking inputs; red car/red apple distinguish content with the same color.
English is the qualified query language. Cosine scores are not probabilities.

Use an administrator terminal on Windows. The runners never start a Unity Editor.
All paths are explicit portable inputs; reports require fresh names.

## Native worker

The self-contained worker is in package `VisualWorker~/<rid>/`. Models are not
bundled into the package. `status` and all queries are offline. The model installer
is a separate, explicit operation and checks the four pinned public artifacts'
lengths and SHA-256 from `VisualWorker~/model-manifest.json`.

One-time Windows installation (or use tool `InstallModel`, `AllowDownload=true`):

```powershell
'{"action":"install","requestId":"explicit-model-install","deadlineMs":600000,"allowDownload":true}' | & '<package-root>/VisualWorker~/win-x64/unibridge_visual_worker.exe' --stdio
```

The default generated cache is `.unibridge/models/clip-vit-b32-int8-v1/<revision>`
under the current user's profile. Worker CLI `modelDirectory` and regression
`--model-directory` optionally select an explicit external cache; no machine path
is tracked. No cloud account, Python ML library or Python image library is needed.

```powershell
python -B '<package-root>/Tools~/VisualSemanticRegression/qualify_worker.py' --worker '<package-root>/VisualWorker~/win-x64/unibridge_visual_worker.exe' --report '<owned-evidence-root>/worker-01.json'
```

This checks fixture SHA/lengths, four real top-ranked image matches, the public
CLIP BPE contract, install authorization, image-job containment, empty query,
missing model and the worker's native deadline. It does not install/download
models or modify fixture assets. An already installed pinned model is required.

## Actual Mono 6.6 / ordinary MCP

First install the production package and per-action scheduler policy: Status and
Search are ReadOnly, Index is Capture, InstallModel/ClearIndex are Mutating.
Inject `Native/VisualSemanticNativeFixture.cs` under a fresh owned Test
`Assets/Editor` directory using nonoverwriting data/meta SHA journaling; the
fixture under Tools~ is intentionally ignored by Unity. The running authorized
Test Editor must finish importing/compiling and advertise fresh tool schemas.

```powershell
python -B '<package-root>/Tools~/VisualSemanticRegression/Native/run_native_visual_mcp.py' --run --project '<UniBridge_Test_Project-root>' --relay '<package-root>/RelayApp~/unibridge_relay_win.exe' --smoke-module '<package-root>/Tools~/McpSmokeRegression/run_mcp_smoke_regression.py' --validator-root '<owned-jsonschema-dependency-root>' --report '<owned-evidence-root>/native-01.json'
```

`jsonschema==4.25.1` is an independent qualification-only dependency. Supply its
existing install directory via `--validator-root`. Optional `--author-baseline`
accepts a root baseline JSON with `UniBridge_Test_Project` path-to-SHA entries;
without it the runner hashes current Assets/ProjectSettings before its own work.
Read `Native/README.md` for exact ownership, cancellation and cleanup coverage.
The live Index gate also sends both uppercase and lowercase GUID references to
the same imported asset, requiring one canonical lowercase identity/cache entry.
Malformed explicit references/types/caps must fail before default enumeration or
native/cache work; the gate requires isError, processed=0 and unchanged index SHA.
Only the relay's exact internal expected-project-root field is accepted outside
the closed business schema; its value must be a nonempty string. Ordinary relay
Status proves acceptance, while unknown business/reserved-prefix fields reject.
An owned same-path replacement with a distinct GUID forces cached non-GUID checks
current. The exact production native predicate must accept the new GUID and reject
the old GUID; Search excludes the old row. Stale native path lookup is recorded
separately and claimed only when actually observed in that Editor.
Mutating calls are issued once; a failed mutation is never replayed by the runner.

Keep worker qualification, SDK compilation and live frontend acceptance separate.
Windows x64 worker behavior is qualified; foreign RID binaries are cross-published
and require physical platform tests before making matching runtime claims. Unity
7/CoreCLR and the canceled Mono crash investigation are outside this regression.
