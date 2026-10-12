# Native serialized numeric precision regression

The three C# files are opt-in transient qualification sources; the package does
not import them. In a unique owned Assets fixture folder, place
SerializedNumericComponent.cs and SerializedNumericAsset.cs under Models/ with
their own runtime asmdef (for the current gate: UniBridge.NumericModels.Qualification1).
The models reference only UnityEngine and must not be compiled as Editor-only
scripts: a real MonoBehaviour component cannot be replaced with a fabricated
fallback if AddComponent returns null. Place SerializedNumericNativeFixture.cs
under Editor/ with an Editor-only asmdef referencing that Models assembly,
Cidonix.UniBridge.MCP.Editor and Newtonsoft.Json.dll. Move existing owned model
scripts together with their unchanged .meta files to retain their GUID identity;
record the exact owned layout change for later guarded cleanup.
Do not add them to an authored scene. The Prepare menu creates its own unsaved
additive scene and one uniquely owned ScriptableObject asset, retains the exact
original scene handles, loaded/dirty flags, roots and active scene,
and records invariant native CLR scalar values independently of SerializedProperty.
Selection is recorded diagnostically, never assigned or restored by the fixture;
independent user selection changes do not prevent cleanup.

Native fields include int, uint, long, ulong, float, double, a nested struct,
a list of structs, and signed/unsigned/double arrays. Long values exceed the
double mantissa; ulong includes all bits; double includes 1.0000000000000002.
Ordinary MCP Component reads use RuntimeStateProbe with an asserted
serializedProperty route, not the reflection-only GetComponent summary.
Component writes use ordinary MCP ManageGameObject SetComponentProperty.
ScriptableObject conversion and truthful validation/errors use ordinary MCP
ManageScriptableObject SetProperties with DryRun=true. Its saved mutation branch
calls global SaveAssets, so that branch is deliberately not executed: dirty
author shader/assets are preserved without saving. Physical ScriptableObject
effects invoke the exact public production SerializedPropertyPatcher through
reflection over the owned object and save only it using SaveAssetIfDirty.
The report explicitly distinguishes MCP dry-run proof from native shared-writer
proof and does not claim a full saved ScriptableObject MCP mutation was tested.
Authored scenes are never saved.

The Python driver requires an elevated Windows shell and an already running
x64 Mono Unity 6000.6 Editor. It launches only the project-scoped relay, checks
2025-11-25 negotiation, validates actual raw structuredContent against registered
output schemas, verifies exact text/machine mirrors, and never retries mutations.
The jsonschema dependency must be compatible with the selected Python runtime.

Use --mode reproduce before production promotion. A conclusive current bug
report has reproduced=true and passed=false/qualification=false: exit 0 means
the defect was actually reproduced and owned cleanup/preservation completed,
not that the old package is qualified. This mode performs native preparation,
two ordinary serialized reads, native double-to-1.0 write loss and valid wide
Int64 rejection witnesses through the production Component MCP route and actual
native shared ScriptableObject writer with ordinary MCP dry-run, and own cleanup.

Use --mode qualify after production promotion. It requires exact wire reads,
physical native writer values, nested/array readbacks and truthful rejection
of integer out-of-range and Double overflow requests without any native field
change. Float comparisons use IEEE32 quantization, Double comparisons exact
IEEE64; existing Float nonfinite conversion policy is outside this focused change.
All original saved Assets/ProjectSettings baseline hashes are rechecked after
the exact owned asset folder is moved into its immutable Library quarantine.
Generated fixture files/reports use CreateNew; an existing active pointer or
unexpected file, identity, metadata or author state is preserved and blocks cleanup.
Physical owned ScriptableObject applications consume immutable per-run/GUID/ordinal
JSON requests exactly once, recording request SHA before production application;
requests, consumption records and results are retained. The request cannot name
another target or output path, and global SaveAssets is never called by the fixture.

Example (substitute the actual fresh report and baseline):

```powershell
& '<Python 3.12 executable>' .\run_native_numeric_mcp.py `
  --mode reproduce `
  --project '<approved-Test-root>' `
  --relay '<bundled-relay-executable>' `
  --smoke-module '<run_mcp_smoke_regression.py>' `
  --validator-path '<existing jsonschema directory>' `
  --author-baseline '<immutable author-baseline.json>' `
  --report '<fresh immutable report.json>'
```

This is Mono6.6 qualification. It does not qualify the deferred Unity7/CoreCLR
Editor profile and does not investigate native process-collector crashes.
