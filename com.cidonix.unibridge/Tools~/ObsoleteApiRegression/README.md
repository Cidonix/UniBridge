# Semantic obsolete API regression

Run from an **Administrator** terminal with Python and the .NET 10 SDK installed:

```powershell
& .\com.cidonix.unibridge\Tools~\ObsoleteApiRegression\Run-ObsoleteApiRegression.ps1 -Report .\Temp\obsolete-api-regression.json
```

The suite compiles the entire unchanged `Helpers/ObsoleteApiAnalyzer.cs` into an isolated temporary executable. It references UniBridge's bundled Roslyn 4.13 and shared dependency assemblies; it downloads no packages and supplies no replacement semantic implementation. The .NET runtime can unify shared framework dependencies to its installed versions; the report records the actual loaded versions and paths. `-SourceRef` optionally selects a committed collector for comparisons.

Fixtures verify resolved API identity, attribute messages and error severity, overloads, aliases, constructors and type references, fields/properties/events/indexers/operators/conversions, extension methods, metadata and cross-file symbols, warning suppression, active preprocessor definitions, Unicode/CRLF locations, ambiguity, hint limits and return type changes. Replacement tests cover plain messages, UnityUpgradable markers, qualified names, type targets, exact overload choice, ambiguous/missing targets and obsolete conversions back to the old result type. Negative fixtures cover comments, strings, unrelated custom attributes, current same-name APIs and overrides, declarations, inactive branches and source types replacing stale metadata.

The JSON report retains assertion failures, build/runtime logs and source/dependency SHA-256 values. All child processes inherit the elevated terminal token. No Unity project is launched or connected, and no source assets are changed by this suite.

Optional `-UnityEditorData 'C:\Program Files\Unity\Hub\Editor\6000.6.5f1\Editor\Data'` compiles the whole production collector, Unity context wrapper and path resolver against that installation's .NET 4.8/core module references. It adds a clearly labeled compilation-only assertion to the report and executes no Unity code.

These standalone checks qualify the semantic collector. A separate live Editor test must qualify project references, Unity compilation definitions, MCP response integration and cache refresh after compilation.

Live qualification is available through `run_live_obsolete_api_regression.py`:

```powershell
python .\com.cidonix.unibridge\Tools~\ObsoleteApiRegression\run_live_obsolete_api_regression.py --project 'H:\Repos\UnityRepos\UniBridge_Test_Project' --project-id ae4e323353aa487f9ea74d566b38eaac --relay 'C:\Users\Cidonix\.unibridge\relay\unibridge_relay_win_1.1.0-build.21.exe' --report .\Temp\obsolete-api-live.json
```

Run only against an authorized test project with a clean, idle Editor and
UniBridge 0.2.59. The elevated project-scoped client creates one uniquely named
plain C# fixture directly under Assets, validates metadata and actual Unity
API replacements, previews edits, checks rejection/no-op/failure reports,
updates its own source and verifies post-reload context and bulk coverage.
Cleanup checks its last owned hash before deleting the fixture/metadata;
authored scenes and selection are never saved or changed. Reports retain every
MCP response and final compilation/state checks. No global configuration is edited.
