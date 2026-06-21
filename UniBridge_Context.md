# UniBridge Context

Останнє оновлення: 2026-06-21, Europe/Kiev.

Цей файл створено як переносний контекст для нового проєкту `UniBridge`.
Мета: зберегти, що було знайдено у пакеті Unity AI Assistant / Unity MCP, які локальні правки важливі, і на чому зупинилась розмова.

## 2026-06-21: UniBridge 0.2.32 Assembly Freshness v2

Другий крок planned polish: зробити stale assembly diagnostics корисними для
реальних проектів з asmdef/package/editor assemblies, а не тільки для
`Assembly-CSharp.dll`.

Зміна:

- `assemblyFreshness.version = 2`;
- старі поля `assemblyPath`, `assemblyExists`, `latestAssetScriptPath` лишені
  для сумісності;
- top-level `assemblyFreshness.staleLikely` тепер відображає весь v2 assembly
  map;
- додано `assemblyFreshness.assemblyCSharpStaleLikely` для старого single-dll
  check;
- v2 використовує `CompilationPipeline.GetAssemblies()` і для кожної Unity
  script assembly порівнює `outputPath` з найновішим існуючим `sourceFiles`;
- v2 покриває asmdef, package, runtime і editor assemblies;
- у відповіді є `staleAssemblyCount`, `missingOutputAssemblyCount`,
  `v2.summary`, `v2.staleAssemblies`, `v2.newestSourceAssemblies`;
- `compileHealth.healthy` автоматично враховує новий broadened
  `assemblyFreshness.staleLikely`.

Очікуваний результат: якщо Unity/Bee/import failure не перебудував конкретну
asmdef/editor/package assembly, агент побачить це у `GetCompilationDiagnostics`
або `WaitForReadyAfterReload`, а не тільки у випадку `Assembly-CSharp.dll`.

## 2026-06-21: UniBridge 0.2.31 Diagnostics Response Cleanup

Перший крок planned polish після 0.2.30 hotfix: зробити reload checkpoint
відповідь менш шумною для AI, не прибираючи важливі build-system guardrails.

Зміна:

- `UniBridge_ManageEditor Action=WaitForReadyAfterReload` більше не дублює
  `buildSystemHealth` і `assemblyFreshness` у nested
  `compilationDiagnostics`;
- `buildSystemHealth`, `assemblyFreshness` і compact `compileHealth`
  повертаються один раз на top-level;
- nested `compilationDiagnostics` позначено як
  `diagnosticScope = retained_compilation_events` і лишено для retained
  `CompilationPipeline` / editor event diagnostics;
- standalone `GetCompilationDiagnostics` лишається повним deep-diagnostics
  action з `buildSystemHealth`, `assemblyFreshness` і `compileHealth`;
- `compileHealth.healthy` тепер враховує не тільки build-system critical
  issues, а й `assemblyFreshness.staleLikely`.
- bundled relay піднято до `1.1.0-build.16`; recovery envelopes після refresh,
  compile і Play Mode reload boundary прибирають вкладені `structuredContent`
  mirrors з embedded Unity tool results, але top-level MCP structured content
  не змінено.

Очікуваний результат: агенти швидше читають результат після reload/compile
boundary, але не втрачають сигнали про Bee/BuildProgram failures або stale
runtime assembly.

## 2026-06-21: UniBridge 0.2.30 Bee/BuildProgram Diagnostics Hotfix

У StarFreelancer під Unity 6000.5.0f1 Unity Bee/BuildProgram падав через
Windows Code Integrity / Application Control policy block:

- blocked file:
  `C:/Program Files/Unity/Hub/Editor/6000.5.0f1/Editor/Data/Tools/BuildPipeline/NiceIO.dll`;
- Unity console first line:
  `Internal build system error. BuildProgram exited with code -532462766.`;
- реальна причина була у stack trace:
  `System.IO.FileLoadException`, `Application Control policy has blocked this file`.

Старий UniBridge 0.2.29 помилково показував чисту картину:

- `GetCompilationDiagnostics`: 0 errors / 0 warnings, бо дивився лише на
  `CompilationPipeline` diagnostics;
- `ReadConsole DiagnosticSummary`: 1 log, 0 errors, бо Unity записала перший
  рядок як `Log`, а критичний текст був у stack trace;
- `WaitForReadyAfterReload` міг виглядати успішним, хоча
  `Library/ScriptAssemblies/Assembly-CSharp.dll` лишався stale.

Hotfix 0.2.30:

- `ReadConsole` тепер класифікує як critical build-system issues fingerprints:
  `Internal build system error`, `BuildProgram exited with code`,
  `ScriptCompilationBuildProgram`, `FileLoadException`, `Application Control
  policy has blocked this file`, `Code Integrity`, `NiceIO.dll`,
  `Could not load file or assembly`;
- `DiagnosticSummary` має піднімати такі записи у `criticalIssues`, навіть якщо
  Unity Console mode був `Log`;
- `ManageEditor.GetCompilationDiagnostics` додає `buildSystemHealth` поруч із
  retained C# diagnostics;
- `WaitForReadyAfterReload` додає `buildSystemHealth` і `assemblyFreshness`;
- `assemblyFreshness` порівнює
  `Library/ScriptAssemblies/Assembly-CSharp.dll` із найновішим `Assets/*.cs` і
  ставить `staleLikely=true`, якщо runtime assembly старіший за source script.
- `ReadConsole Search` тепер шукає не тільки у видимому першому рядку message,
  а й у full console payload / stack trace. Тому `Application Control policy`,
  `NiceIO.dll` та схожі stacktrace-only сигнали можна знайти прямо через MCP.

Важливо: це не лікує сам Windows policy block. Якщо OS блокує Unity
`NiceIO.dll`, Unity все одно не зможе зібрати проект, але UniBridge більше не
має повертати хибне “compilation clean”.

## 2026-06-17: UniBridge 0.2.29 Unity 6.5 MCP Smoke Polish

Після повного live MCP smoke у `UniBridge_Test_Project` під Unity 6000.5.0f1
зроблено фінальний polish для сценаріїв, які реально вилізли під час тесту:

- package version піднято до `0.2.29`;
- `BatchActions.Steps` тепер приймає nested step payload у `arguments`, а не
  тільки в `parameters` / `params` / `args`. Це важливо для MCP-клієнтів, які
  природно формують payload саме як `arguments`;
- Unity 6 EntityId може бути більшим за JavaScript safe integer, тому
  serializers тепер повертають string variants:
  `objectIdString`, `instanceIdString`, `parentObjectIdString` тощо;
- `UniBridge_ManageSceneHierarchy` отримав `ObjectIdString`,
  `ParentObjectIdString`, `ObjectIdStrings` і `ParentObjectIdString` для
  безпечного reparent/container workflow з JS/JSON клієнтів;
- Play Mode queue/wait лишився reload-safe, але прибрано надмірно агресивну
  early-boundary евристику: якщо Unity просто компілює або вікно не у фокусі,
  tool більше не має поспішати трактувати це як reload boundary;
- `QueuePlayModeChange` ставить команду через one-shot
  `EditorApplication.update` з `delayCall` fallback, щоб Play/Edit request
  гарантовано доходив після MCP response.

Live smoke report:

- проект: `H:/Repos/UnityRepos/UniBridge_Test_Project`;
- Unity: `6000.5.0f1`;
- report:
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Library/UniBridge/unity65-full-smoke-20260617031139.json`;
- `tools/list`: 67 UniBridge tools;
- результат: 54 pass, 0 fail;
- 2 warnings були очікуваними cleanup-attempts для неіснуючого старого smoke
  root, тобто не є проблемами пакету;
- перевірено: `Discover`, `ReadConsole`, `ManageEditor` refresh/compile/play
  workflows, `DomainCatalog`, `ContextSnapshot`, `UnitySearch`,
  `AssetIntelligence`, `ScriptIntelligence`, `TypeSchema`,
  `SceneObjectView`, `SceneHierarchyExport`, `ManageGameObject`,
  `ManageSceneHierarchy` через `ObjectIdString`, `BatchActions arguments`,
  `VisualSceneAudit`, `CaptureView`;
- Play Mode smoke проходив з явним фокусом на Unity window:
  `Play queue -> WaitForPlayMode -> WaitForReady RequireNotPlaying=false ->
  DiagnosticSummary -> ExitPlayMode queue -> WaitForEditMode ->
  WaitForReady RequireNotPlaying=true`.

## 2026-06-17: UniBridge 0.2.28 Unity 6000.5 Compatibility Hotfix

Domovyk було оновлено до Unity 6000.5.0f1, після чого пакет UniBridge почав
падати compile-time errors через obsolete-as-error API:

- `EditorUtility.InstanceIDToObject(int)` у `UnitySearch.cs`;
- `Object.GetInstanceID()` у `TypeSchema.cs`.

Hotfix:

- package version піднято до `0.2.28`;
- `UnitySearch` більше не викликає `EditorUtility.InstanceIDToObject`
  напряму, а використовує `UnityApiAdapter.GetObjectFromNativeSearchId`;
- `UnityApiAdapter.GetObjectFromNativeSearchId` спершу пробує EntityId-style
  lookup, а потім legacy instance-id provider payload через reflection fallback
  без прямого obsolete API call;
- `TypeSchema` object references тепер беруть `instanceId` через
  `UnityApiAdapter.GetObjectId`;
- Roslyn вирівняно на сумісний з Unity 6000.5 набір:
  `Microsoft.CodeAnalysis` / `Microsoft.CodeAnalysis.CSharp` `4.13.0.0`,
  `System.Collections.Immutable` / `System.Reflection.Metadata` `8.0.0.0`;
- `Cidonix.UniBridge.MCP.Editor.asmdef` явно references
  `System.Reflection.Metadata.dll`, щоб не брати старі копії з інших Unity
  packages;
- `ManageRendering` і `SceneObjectView` приглушують intentional
  `LightProbeProxyVolume` CS0618 warnings у Unity 6000.5, залишаючи підтримку
  legacy scenes/components;
- release notes, changelog і package README оновлено під hotfix.

Expected result:

- Unity 6000.5 не має більше видавати CS0619 errors у `UnitySearch.cs` і
  `TypeSchema.cs`;
- native `UniBridge_UnitySearch` scene results мають залишитися сумісними як з
  EntityId-style, так і з legacy instance-id payloads від Unity Search
  provider.
- CS1705 від `Microsoft.CodeAnalysis 5.3.0` проти Unity reference
  `System.Collections.Immutable 8.0.0.0` має зникнути, бо Roslyn 4.13.0 сам
  очікує dependency version 8.0.0.0.

## 2026-06-16: UniBridge 0.2.27 Semantic Asset Diff

Закрито останній Locus-inspired read-only polish: `UniBridge_AssetIntelligence`
отримав `Action=SemanticDiff` для порівняння Unity YAML/text assets без
мутацій проекту.

Що додано:

- `AssetIntelligenceAction.SemanticDiff`;
- параметри `OtherPath`, `OtherGuid`, `MaxDiffItems`,
  `MaxChangedPropertiesPerDocument`, `IncludeLineDiff`, `MaxLineDiffs`,
  `MaxGuidReferenceDiffs`;
- read-only helper `AssetSemanticDiff`, який підтримує `Assets/...`,
  `Packages/...`, `ProjectSettings/...`, `unity://path/...` і абсолютні paths;
- semantic YAML parsing за document headers `--- !u!<classId> &<fileId>`;
- result summary: created/deleted/modified YAML documents, class/fileID
  changes, changed properties, GUID/script reference deltas, risk summary і
  bounded line diff hunks;
- discoverability aliases:
  `semantic_asset_diff`, `asset_semantic_diff`, `yaml_semantic_diff`,
  `unity_yaml_diff`, `prefab_semantic_diff`, `asset_diff`, `semantic_diff`;
- `ToolGuide`, `Discover`, `DomainCatalog`, package docs і release notes
  оновлені під новий workflow.

Навіщо це потрібно:

- агент може перевірити prefab/scene/material/.asset YAML зміни семантично, не
  читаючи шумний raw diff;
- перед звітом або ризиковою asset-правкою видно, чи змінились `m_Script`,
  GUID references, component/fileID blocks, transform/hierarchy/sorting поля;
- це закриває практичну прогалину між `WorkSession Diff` як text diff і
  Unity-aware asset review.

Testing status:

- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj`: success,
  `0 warnings`, `0 errors`;
- пакет синхронізовано у
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`
  і `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`;
- перший live Unity refresh виявив compile error `CS0136` у
  `AssetSemanticDiff.cs`, змінну `fileId` перейменовано на `referencedFileId`;
- повторний `UniBridge_ManageEditor Action=RefreshAssets WaitForCompletion=true`
  у тестовому проекті перетнув reload boundary, reconnect відпрацював, Unity
  повернула ready;
- `UniBridge_ManageEditor Action=GetCompilationDiagnostics`: `errors=0`,
  `warnings=0`;
- `UniBridge_BatchActions DryRun=true` з nested
  `asset_intelligence Action=SemanticDiff`: validation passed;
- `UniBridge_BatchActions DryRun=false IncludeConsoleDelta=true` на двох real
  prefab YAML assets:
  `Assets/UniBridgeManual/Prefabs/UB_Override_Source_20260511_031211.prefab`
  ->
  `Assets/UniBridgeManual/Prefabs/UB_Override_Source_20260511_031305.prefab`
  повернув semantic result:
  `4 created`, `4 deleted`, `0 modified` YAML document(s), GUID delta,
  risk summary і bounded line hunks;
- batch console delta: `warnings=0`, `errors=0`, `exceptions=0`;
- `ToolGuide Workflow Topic=asset_semantic_diff` знаходиться;
- `DomainCatalog InspectDomain Assets` показує `SemanticDiff` у first calls,
  risk controls і aliases;
- `Discover Aliases Query=semantic` повертає aliases для
  `UniBridge_AssetIntelligence`;
- фінальний `ReadConsole DiagnosticSummary`: `totalEntries=0`, `warnings=0`,
  `errors=0`, `exceptions=0`.

## 2026-06-16: UniBridge 0.2.26 Agent Playbook Polish

Фінальний Locus-inspired polish закрито без додавання ще одного великого tool:
замість дублювання існуючих можливостей дополіровано agent-facing playbooks у
`ToolGuide`, `DomainCatalog`, `ContextSnapshot`, `Discover` і batch aliases.

Що додано:

- `UniBridge_ToolGuide Action=Workflow Topic=agent_playbook` як компактний
  default operating protocol для нових агентів;
- `ToolGuide Overview` тепер повертає `agentPlaybook` з read-before-modify,
  scope awareness, safe execution і verification ladder;
- `UniBridge_DomainCatalog` повертає `globalRiskControls`, а кожен домен має
  локальні `riskControls`;
- `UniBridge_ContextSnapshot` у `agentBrief` повертає `operatingProtocol` і
  `verificationLadder`;
- `UniBridge_Discover Action=Workflows` показує workflow `agent_playbook`;
- `BatchActions` aliases доповнено:
  `agent_playbook`, `playbook`, `agent_rules`, `read_before_modify`,
  `verification_ladder`, `risk_controls`, `operating_protocol`;
- package/docs оновлено до `0.2.26`.

Навіщо це потрібно:

- новий агент швидше розуміє "як працювати через UniBridge", а не просто бачить
  довгий список tools;
- перед ризиковими змінами агент отримує явні правила: спочатку прочитати
  current state/reference sites/domain schema, потім dry-run, потім verification;
- Locus-pass можна поки вважати закритим: корисні workflow-ідеї перенесені у
  нашу архітектуру без копіювання коду і без прив'язки до чужої оболонки.

Testing status:

- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj`: success,
  `0 warnings`, `0 errors`;
- пакет синхронізовано у
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `UniBridge_ManageEditor Action=WaitForReadyAfterReload`: ready,
  `isCompiling=false`, `isPlaying=false`;
- `UniBridge_ManageEditor Action=GetCompilationDiagnostics`: `errors=0`,
  `warnings=0`;
- `UniBridge_ReadConsole Action=DiagnosticSummary`: `totalEntries=0`,
  `warnings=0`, `errors=0`, `exceptions=0`;
- `UniBridge_ToolGuide Action=Workflow Topic=agent_playbook`: workflow found,
  aliases і verification calls присутні;
- `UniBridge_ToolGuide Action=Overview`: `agentPlaybook` і workflow topic
  `agent_playbook` присутні;
- `UniBridge_DomainCatalog Action=Overview`: `globalRiskControls` і
  domain-level `riskControls` присутні;
- `UniBridge_ContextSnapshot Depth=Brief IncludeAgentBrief=true
  ConsoleSummaryMode=Compact`: `operatingProtocol`, `verificationLadder` і
  recommended call до `ToolGuide Workflow=agent_playbook` присутні;
- `UniBridge_Discover Action=Workflows Query=agent_playbook`: workflow
  повертається;
- `UniBridge_BatchActions DryRun=true` з alias `agent_playbook` і
  `tool_domains`: validation passed, мутацій не було.

## 2026-06-16: UniBridge 0.2.25 RuntimeProfiler Hierarchy Export

Третій Locus-inspired пункт реалізовано як read-only profiler hierarchy /
top-sample export у вже існуючому `UniBridge_RuntimeProfiler`, без копіювання
чужої архітектури й без залежності від внутрішнього ProfilerWindow UI.

Новий режим:

- `UniBridge_RuntimeProfiler Action=Hierarchy`;
- використовує bounded `ProfilerRecorder` marker handles;
- повертає compact summary, category summary, top markers і синтетичне дерево
  marker path;
- може зберігати повний JSON у
  `Library/UniBridge/RuntimeProfiler` через `SaveToFile=true`;
- за замовчуванням потребує Play Mode, але для editor-time smoke можна
  передати `RequirePlayMode=false`;
- output явно маркується як `dataSource="ProfilerRecorderMarkers"`, тобто це
  marker hierarchy/top-sample view, а не повне call tree з Unity ProfilerWindow.

Навіщо це потрібно:

- агент отримує конкретні "гарячі" marker samples кадру замість загальної
  фрази "поганий frame time / GC / render";
- результат придатний для AI-debug workflow: спочатку `Snapshot`/`Metrics`,
  потім `Hierarchy`, потім за потреби bounded `Sample`;
- великі raw exports не роздувають MCP-відповідь, бо пишуться у файл.

Параметри:

- `ProfilerCategories`, `MarkerFilters`, `ExcludeMarkerFilters`;
- `MaxProfilerMarkers`, `MaxHierarchySamples`, `MaxHierarchyDepth`;
- `MinHierarchySampleMs`, `IncludeCounters`, `ReturnSamples`, `SaveToFile`.

Discoverability:

- `BatchActions` aliases:
  `profiler_hierarchy`, `marker_hierarchy`, `runtime_hierarchy`,
  `frame_export`, `frame_hierarchy`, `top_markers`, `hot_markers`;
- `Discover`, `ToolGuide`, `DomainCatalog` runtime/debug workflows тепер
  показують `RuntimeProfiler Action=Hierarchy`;
- `ToolGuide Workflow runtime_profiler` окремо пояснює, що це bounded
  ProfilerRecorder marker view, не повний ProfilerWindow call tree.

Package/docs:

- package version піднято до `0.2.25`;
- оновлено `CHANGELOG.md`, `RELEASE_NOTES.md`, root/package `README.md`,
  `Documentation~/unibridge.md`;
- пакет синхронізовано у
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`.

Testing status:

- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj`: success,
  `0 warnings`, `0 errors`;
- `UniBridge_ManageEditor Action=RefreshAssets WaitForCompletion=true`
  у тестовому проєкті перетнув reload boundary, перепідключився і повернув
  ready state;
- compilation diagnostics після refresh: `errors=0`, `warnings=0`;
- live MCP smoke через `UniBridge_BatchActions`:
  `runtime_profiler Action=Hierarchy RequirePlayMode=false SampleFrames=1
  MaxProfilerMarkers=80 MaxHierarchySamples=12 SaveToFile=true`;
- результат: `Captured profiler marker hierarchy with 12 top marker(s)`,
  `schema=unibridge.runtimeProfiler.hierarchy.summary.v1`,
  `dataSource=ProfilerRecorderMarkers`, `available=4394`, `selected=80`,
  `recorderAvailable=80`, `recorderUnavailable=0`;
- saved JSON:
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Library/UniBridge/RuntimeProfiler/20260616-174844548-runtime-hierarchy.json`;
- `ToolGuide Workflow runtime_profiler` показує новий call і aliases;
- `UniBridge_ReadConsole Action=DiagnosticSummary`: `totalEntries=0`,
  `warnings=0`, `errors=0`, `exceptions=0`.

## 2026-06-16: UniBridge 0.2.24 Script ChangeImpact Preflight

Другий Locus-inspired пункт реалізується як read-only preflight у вже
існуючому `UniBridge_ScriptIntelligence`, без hot reload і без копіювання
чужої архітектури.

Новий режим:

- `UniBridge_ScriptIntelligence Action=ChangeImpact`;
- target: `Path`, `Guid`, `TypeName` або `Query`;
- candidate source: `ProposedSource` або `ProposedPath`;
- результат показує syntax diagnostics, source delta, type/member shape diff,
  public API risk, serialized field risk, Unity callback risk і очікуваний
  refresh/compile/domain-reload boundary;
- результат повертає `suggestedNextCalls` для `CodeUsages`, `MemberUsages`,
  `ValidateScript`, `RefreshAssets`, `RequestScriptCompilationNoWait`,
  `WaitForReadyAfterReload`, `GetCompilationDiagnostics`,
  `ReadConsole DiagnosticSummary`.

Навіщо це потрібно:

- перед великим script edit агент може побачити, що саме зміниться в API,
  serialized fields і Unity callbacks, ще до застосування текстових правок;
- не замінює `CodeUsages`/`MemberUsages`, а доповнює їх як preflight
  "що зміниться у файлі";
- не робить hot reload і не пише файли, лише дає risk summary і наступні
  безпечні кроки.

Discoverability:

- `BatchActions` aliases:
  `change_impact`, `script_change_impact`, `script_preflight`, `hot_diff`,
  `reload_risk`, `script_reload_risk`, `api_change_impact`;
- `Discover`, `ToolGuide`, `DomainCatalog` scripts workflows тепер радять
  `ChangeImpact` поруч із `Usages`, `MemberUsages`, `CodeUsages`.

Package/docs:

- package version піднято до `0.2.24`;
- оновлено `CHANGELOG.md`, `RELEASE_NOTES.md`, root/package `README.md`,
  `Documentation~/unibridge.md`;
- `ScriptIntelligence` source-shape summaries тепер зберігають
  `declaringType` / `declaringTypeFullName` для parsed fields/properties/
  methods, щоб member diffs були зрозуміліші.

Testing status:

- implementation complete;
- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj`: success,
  `0 warnings`, `0 errors`;
- package sync у
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `UniBridge_Discover Action=Ping`: package version `0.2.24`, Unity
  `6000.4.11f1`, project `UniBridge_Test_Project`;
- `ToolGuide Workflow scripts` показує
  `UniBridge_ScriptIntelligence ChangeImpact ProposedSource=<candidateSource>`
  і aliases `change_impact`, `script_preflight`, `hot_diff`, `reload_risk`;
- live batch smoke через MCP:
  `script_intelligence Action=ChangeImpact
  Path=Assets/UniBridgeScriptFixtures/Runtime/UniBridgeFixtureMover.cs`
  із candidate source, де `speed` перейменовано у `moveSpeed`, а
  `MoveBy` у `MoveByWorld`;
- результат: `riskLevel=High`, `syntaxErrors=0`, `syntaxWarnings=0`,
  `possibleMemberRenames=2`, `serializedFieldRiskCount=1`,
  `apiRiskCount=1`, `highRisks=2`;
- risk output без дублювання removed/added noise:
  `publicMemberPossibleRename` для `MoveBy -> MoveByWorld` і
  `serializedFieldPossibleRename` для `speed -> moveSpeed`;
- `declaringTypeFullName` у member diff/risk коректно містить
  `Cidonix.UniBridge.TestFixtures.UniBridgeFixtureMover`;
- `postActionDiagnostics.consoleDelta`: `totalEntries=0`, `warnings=0`,
  `errors=0`, `exceptions=0`;
- `UniBridge_ManageEditor Action=GetCompilationDiagnostics`: `errors=0`,
  `warnings=0`;
- `UniBridge_ReadConsole Action=DiagnosticSummary`: `totalEntries=0`.

## 2026-06-16: UniBridge 0.2.23 C# CodeUsages Caller Scan

Перший наступний Locus-inspired пункт реалізовано без копіювання чужого коду:
додано read-only caller/impact scan у вже існуючий
`UniBridge_ScriptIntelligence`, а не окремий дублюючий tool.

Новий режим:

- `UniBridge_ScriptIntelligence Action=CodeUsages`;
- target: `Path`, `Guid`, `TypeName` або `Query`;
- optional `Member=<methodOrField>`;
- bounds: `MaxScanScripts`, `MaxReferences`;
- flags: `IncludeSelfReferences`, `IncludeStringReferences`.

Навіщо це потрібно:

- перед rename/delete/signature-change агент може знайти C# call sites, а не
  покладатись лише на текстовий grep;
- `Usages` лишається для prefab/scene YAML references до script GUID;
- `MemberUsages` лишається для serialized UnityEvent/AnimationEvent/field
  references;
- `CodeUsages` покриває C# callers/type references:
  method invocation, member access, conditional access, `nameof(...)`,
  possible identifier references і string callbacks типу `SendMessage`,
  `Invoke`, `StartCoroutine`.

Результат повертає:

- `path`, `guid`, `fullName`, `scriptKind`;
- `line`, `column`, `usageKind`;
- `confidence`: `Exact`, `Possible`, `RuntimeResolved`;
- `symbol`, `context`, `note`, `preview`.

Discoverability:

- `BatchActions` aliases:
  `code_usages`, `caller_scan`, `callers`, `member_callers`,
  `code_member_usages`;
- `Discover`, `ToolGuide`, `DomainCatalog` scripts workflows тепер радять
  `CodeUsages` як третій preflight поруч із `Usages` і `MemberUsages`.

Package/docs:

- package version піднято до `0.2.23`;
- оновлено `CHANGELOG.md`, `RELEASE_NOTES.md`, package `README.md`,
  `Documentation~/unibridge.md`;
- старі 0.2.21 notes уточнено: `script_usages`/`asset_script_usages`/
  `guid_usages` означають script GUID/YAML usage, а `code_usages` тепер
  означає саме C# source caller scan.

Testing status:

- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj`: success,
  `0 warnings`, `0 errors`;
- package sync у `H:/Repos/UnityRepos/UniBridge_Test_Project`, Unity
  `6000.4.11f1`;
- `UniBridge_ManageEditor Action=RefreshAssets WaitForCompletion=true
  Force=true` пройшов через reload boundary, relay reconnect спрацював,
  editor ready;
- `UniBridge_ManageEditor Action=GetCompilationDiagnostics`: `errors=0`,
  `warnings=0`;
- `UniBridge_Discover Action=Ping`: package version `0.2.23`;
- `ToolGuide Workflow scripts` показує
  `UniBridge_ScriptIntelligence CodeUsages Member=<methodOrField>`;
- `Discover Tools Query="CodeUsages caller_scan member_callers
  code_member_usages"` повертає `UniBridge_ScriptIntelligence` і нові aliases;
- batch smoke через MCP:
  `script_intelligence Action=CodeUsages
  Path=Assets/CorgiEngine/Common/Scripts/Managers/InputManager.cs
  MaxReferences=12` знайшов 12 C# type references, `12 Exact`,
  `ParseErrors=0`, без дублювання `TypeReference`/`TypeIdentifier`,
  console delta clean;
- member smoke через MCP:
  `script_intelligence Action=CodeUsages
  Path=Assets/CorgiEngine/Common/Scripts/Agents/CharacterAbilities/CharacterAbility.cs
  Member=SetInputManager` знайшов 6 method call sites з `line`, `column`,
  `context`, `preview`, console delta clean.

## 2026-06-16: UniBridge 0.2.22 Serialized Member Usages

Після звірки з Locus-style `unity_code_usages` додано не окремий дублюючий
tool, а новий read-only режим в існуючий `UniBridge_ScriptIntelligence`:
`Action=MemberUsages`.

Навіщо це потрібно:

- перед перейменуванням або видаленням callback/method/field агент може знайти
  serialized Unity references, які звичайний C# text search не бачить;
- покриває `UnityEvent` persistent method bindings, `AnimationEvent`
  `functionName` і serialized field entries у prefab/scene/asset YAML;
- exact-match позначається тільки коли UnityEvent target резолвиться до
  MonoBehaviour з потрібним script GUID;
- possible/runtime matches лишаються окремо позначеними, щоб агент не робив
  небезпечних висновків без перевірки;
- результат містить `assetPath`, `line`, `column`, `propertyPath`,
  `objectPath`, `indexedObjectPath`, `componentType`, `scriptType`,
  `usageKind`, `confidence`, `note`, preview і resolved event target.

Нові параметри:

- `UniBridge_ScriptIntelligence Action=MemberUsages`;
- `Member=<methodOrField>`;
- `IncludePossibleMemberUsages=true|false`;
- існуючі bounds: `MaxScanAssets`, `MaxUsageLocations`.

Discoverability:

- `BatchActions` aliases:
  `member_usages`, `serialized_member_usages`, `serialized_member_search`,
  `unity_event_usages`, `animation_event_usages`, `serialized_field_usages`;
- `ToolGuide` workflows `asset_reference_locations`, `scripts`, `search`
  тепер явно радять `ScriptIntelligence MemberUsages`;
- `DomainCatalog` Scripts domain показує `MemberUsages` у first calls.

Live smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project` через MCP:

- package sync -> `UniBridge_Discover Action=Ping` показав package version
  `0.2.22`;
- після очищення помилкової вкладеної копії пакета
  `Packages/com.cidonix.unibridge/com.cidonix.unibridge` refresh пройшов через
  expected reload boundary, relay reconnect спрацював, editor ready;
- compilation diagnostics: `errors=0`, `warnings=0`;
- `ToolGuide Workflow asset_reference_locations` і `ToolGuide Workflow scripts`
  повернули нові `MemberUsages` calls/aliases/notes;
- `Discover Tools Query="ScriptIntelligence MemberUsages member_usages"`
  повернув `UniBridge_ScriptIntelligence` з новими aliases;
- batch smoke:
  `script_intelligence Action=MemberUsages Path=.../AIActionDoNothing.cs
  Member=PerformAction` виконався успішно, bounded scan без помилок;
- positive exact smoke:
  `script_intelligence Action=MemberUsages
  Path=Assets/CorgiEngine/Common/Scripts/Managers/InputManager.cs
  Member=RunButtonDown MaxScanAssets=12000 MaxUsageLocations=10`
  знайшов 10 serialized UnityEvent usages: 9 `Exact`, 1 `Possible`;
  приклад exact location:
  `Assets/CorgiEngine/Common/Prefabs/GUI/UICamera.prefab`, `line=421`,
  `objectPath=/UICamera/Canvas/Buttons/ButtonX`,
  `componentType=MoreMountains.Tools.MMTouchButton`,
  event target `MoreMountains.CorgiEngine.InputManager`;
- `UniBridge_ReadConsole DiagnosticSummary`: `totalEntries=0`.

## 2026-06-16: UniBridge 0.2.21 Location-Aware Reference Sites

Після аналізу Locus-style workflows додано не новий дублюючий tool, а
розширення існуючих `UniBridge_AssetIntelligence` і
`UniBridge_ScriptIntelligence`: agent тепер може бачити точні YAML-місця, де
asset або script GUID реально використовується.

Навіщо це потрібно:

- перед `rename/move/delete` asset агент бачить не тільки список dependents, а
  конкретний prefab/scene YAML рядок, property і object path;
- перед міграцією або видаленням MonoBehaviour script агент бачить, які prefab
  або scene objects тримають `m_Script` reference;
- для duplicate sibling names повертається `indexedObjectPath`, щоб агент міг
  працювати не лише по неунікальному імені;
- це read-only і bounded, тому підходить як safety-first preflight перед
  ризиковими змінами.

Нові параметри:

- `UniBridge_AssetIntelligence`:
  - `IncludeReferenceLocations=true`;
  - `MaxReferenceLocations`;
  - працює з `Action=ReferenceGraph`, `Dependents`, `Impact`;
- `UniBridge_ScriptIntelligence`:
  - `IncludeUsageLocations=true`;
  - `MaxUsageLocations`;
  - працює з `Action=Usages` і `Analyze IncludeUsages=true`.

Reference location payload містить:

- `assetPath`, `targetGuid`, `targetPath`;
- `line`, `column`, `preview`;
- `propertyPath`;
- `yamlDocument.type/classId/fileId`;
- `objectPath`, `indexedObjectPath`, `gameObjectName`;
- `componentType`, `scriptType`, `scriptAssetPath`.

Discoverability:

- `BatchActions` aliases:
  `asset_ref_search`, `asset_reference_search`, `asset_usages`,
  `reference_graph`, `reference_locations`, `script_usages`,
  `asset_script_usages`, `guid_usages`;
- `Discover` workflow `asset_reference_locations`;
- `ToolGuide` workflow `asset_reference_locations`;
- `DomainCatalog` Assets/Scripts domains mention exact reference locations.

Live smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project` через MCP:

- package sync -> `UniBridge_Discover Action=Ping` показав package version
  `0.2.21`;
- `UniBridge_BatchActions` з `RefreshAssets WaitForCompletion=true` потрапив у
  expected reload boundary, relay reconnect спрацював, editor ready,
  compilation diagnostics `errors=0`, `warnings=0`;
- `Discover Action=Workflows Query="asset_reference_locations
  reference_locations asset_script_usages"` повернув новий workflow;
- `ToolGuide Workflow asset_reference_locations` повернув правильний workflow,
  aliases і нотатки;
- `AssetIntelligence Structure` для
  `Assets/CorgiEngine/Common/Prefabs/GUI/DialogueBox.prefab` повернув 5
  objects, `DialogueBox` component summary і чисту консоль;
- `ScriptIntelligence Usages` для
  `Assets/CorgiEngine/Common/Scripts/GUI/Dialogue/DialogueBox.cs` повернув
  usage у `DialogueBox.prefab` з exact location:
  `line=510`, `column=38`, `propertyPath=MonoBehaviour.m_Script`,
  `objectPath=/DialogueBox`, `indexedObjectPath=/DialogueBox`,
  `componentType=MoreMountains.CorgiEngine.DialogueBox`;
- `AssetIntelligence ReferenceGraph IncludeReferenceLocations=true` для того
  самого script повернув той самий incoming location у
  `referenceLocations.incoming`;
- `UniBridge_ReadConsole DiagnosticSummary`: `totalEntries=0`.

Пакет піднято до `0.2.21`; relay лишився `1.1.0-build.15`.

## 2026-06-16: UniBridge 0.2.20 AssetIntelligence Structure

Додано read-only `UniBridge_AssetIntelligence Action=Structure` як
Locus-inspired asset structure workflow, але в існуючій UniBridge-архітектурі
замість нового окремого tool.

Навіщо це потрібно:

- агент може отримати компактну карту prefab або вже завантаженої scene asset,
  не читаючи весь YAML і не міняючи editor state;
- є duplicate-safe `indexedPath` для sibling-дублікатів;
- можна швидко знайти об'єкт по name/path/component/tag/layer/prefab source і,
  за потреби, serialized field names/values;
- можна drill-down в один об'єкт перед edit workflow: transform, компоненти,
  renderer sorting, child summaries, bounded serialized properties.

Нові параметри `UniBridge_AssetIntelligence`:

- `StructureMode=List|Search|Read`;
- `ObjectPath`, `PathPrefix`, `ComponentFilter`, `MatchFields`;
- `MaxStructureDepth`, `MaxStructureItems`, `MaxFieldDepth`,
  `MaxArrayItems`.

Поведінка:

- `.prefab`: читає відкритий Prefab Stage, якщо він відповідає asset path, або
  тимчасово `LoadPrefabContents` / `UnloadPrefabContents`;
- `.unity`: працює тільки якщо scene вже loaded in Editor; unloaded scene не
  відкривається автоматично, щоб read-only запит не міняв стан редактора;
- `List` повертає compact hierarchy та `summary`;
- `Search` повертає matches і `matchedBy`;
- `Read` повертає деталі одного object за `path` або `indexedPath`, а якщо path
  неоднозначний, просить використати `indexedPath`.

Discoverability:

- `Discover` має workflow `asset_structure`;
- `ToolGuide` має workflow `asset_structure`;
- `DomainCatalog` Assets domain згадує structure workflow;
- `BatchActions` має aliases: `asset_structure`, `prefab_structure`,
  `scene_asset_structure`, `structure_search`, `serialized_asset_search`,
  `read_yaml`.

Додаткова поліровка після live test:

- `UniBridge_Discover Action=Tools Query=...` тепер token-aware: запит
  `AssetIntelligence Structure asset_structure` знаходить
  `UniBridge_AssetIntelligence`, бо токени зіставляються по
  name/title/description/aliases, а не як одна довга substring-фраза;
- `UniBridge_ToolGuide Action=Workflow Topic=asset_structure` тепер спочатку
  шукає exact workflow key, і тільки потім aliases, тому `asset_structure` не
  перехоплюється загальним workflow `search`.

Live smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project` через MCP:

- sync package -> `UniBridge_ManageEditor RefreshAssets WaitForCompletion=true`
  двічі пройшов reload-safe boundary, editor ready, compilation diagnostics
  `errors=0`, `warnings=0`;
- `UniBridge_Discover Action=Tools Query="AssetIntelligence Structure
  asset_structure"` повернув `UniBridge_AssetIntelligence`,
  `batchAllowed=true`, aliases включно з `asset_structure`;
- `UniBridge_BatchActions DryRun=false` з alias `asset_structure` на
  `Assets/CorgiEngine/Common/Prefabs/GUI/DialogueBox.prefab`:
  `StructureMode=List` повернув 5 objects і component summary,
  `StructureMode=Search Query=Text MatchFields=all` повернув 5 matches із
  `matchedBy`, `StructureMode=Read ObjectPath=/DialogueBox/TextPanel/Text`
  повернув transform, компоненти і bounded serialized properties;
- `UniBridge_ToolGuide Workflow asset_structure` повернув правильний workflow;
- `UniBridge_DomainCatalog InspectDomain Assets` показав `AssetIntelligence`
  aliases `asset_structure`, `prefab_structure`, `scene_asset_structure`;
- `UniBridge_ReadConsole DiagnosticSummary`: `totalEntries=0`.

Нюанс для поточного Codex thread: deferred `tool_search` може кешувати старий
client-side schema прямого `UniBridge_AssetIntelligence` до перезапуску
агента/MCP-клієнта. Live `_server_info action=tools` у Unity вже показує нові
`Action=Structure` параметри, а batch alias працює одразу.

Пакет піднято до `0.2.20`.

## 2026-06-16: UniBridge 0.2.19 TypeSchema TypeIndex/Fingerprint

Додано cacheable loaded-type map у вже існуючий `UniBridge_TypeSchema`, щоб
майбутній агент міг швидко знайти правильний Unity/C# тип перед
`AddComponent`, ScriptableObject/importer/asset authoring або generic property
patching.

Нові actions:

- `Action=TypeFingerprint`: повертає fingerprint завантажених assemblies,
  `assemblyCount`, `kind/query/indexKey` і підказку, чи можна повторно
  використовувати збережений type index після reload;
- `Action=TypeIndex`: повертає compact sample типів і summary:
  `kindCounts`, `topAssemblies`, `topAmbiguousSimpleNames`;
- `TypeIndex WriteToFile=true`: пише bounded full JSON index у
  `Library/UniBridge/TypeIndex`, а MCP-відповідь лишає компактною;
- entries містять `simpleName`, `fullName`, `assembly`,
  `assemblyQualifiedName`, `kind`, `domainTags`, `baseType`, flags і
  `resolveHints` для наступного `TypeSchema Inspect`, `AddComponent` або
  ScriptableObject authoring.

Додатково:

- `TypeSchemaParams` отримав `IncludeNonPublicTypes`, `WriteToFile`,
  `MaxTypeIndexEntries`;
- `Discover` має workflow `type_index`;
- `ToolGuide` має workflow `type_schema`;
- `DomainCatalog` і search aliases згадують `TypeIndex`, `type map`,
  `type fingerprint`;
- `BatchActions` validation/normalization приймає `TypeIndex` /
  `TypeFingerprint` і aliases `type_index`, `type_map`, `fingerprint`.

Smoke-test caveat:

- перший MCP smoke у тестовому проекті показав regression у batch layer:
  `UniBridge_BatchActions` відхилив `TypeFingerprint` як unsupported, хоча
  сам `TypeSchema` вже мав новий enum;
- виправлено `BatchActions.Validation.cs` whitelist і
  `BatchActions.Steps.cs` normalization aliases.

Перевірка через MCP на
`H:/Repos/UnityRepos/UniBridge_Test_Project`:

- package 0.2.19 скопійовано в
  `Packages/com.cidonix.unibridge` без `RelayApp~`;
- `ManageEditor RefreshAssets WaitForCompletion=true Force=true` двічі
  пройшов reload boundary і повернув reload-safe reconnect result;
- retained compilation diagnostics: errors 0, warnings 0;
- `UniBridge_BatchActions DryRun=false IncludeImpact=false
  IncludeWorkSessionReview=false` з 4 read-only `UniBridge_TypeSchema` steps
  виконався успішно:
  - `TypeFingerprint` повернув fingerprint `250fa2b9fe626638`,
    `assemblyCount=302`;
  - `TypeIndex Kind=Component Query=Camera Limit=5` знайшов 33 types,
    включно з `UnityEngine.Camera`, і виявив ambiguity для
    `PixelPerfectCamera`;
  - `TypeIndex Kind=Any Query=Camera WriteToFile=true MaxTypeIndexEntries=200`
    записав файл
    `H:/Repos/UnityRepos/UniBridge_Test_Project/Library/UniBridge/TypeIndex/20260616-004943-Any-Camera-250fa2b9fe626638.json`;
  - `Inspect TypeName=UnityEngine.Camera IncludePatchExamples=true`
    повернув schema та patch hint;
- `Discover Action=Workflows Query=type_index` повернув workflow
  `type_index`;
- `ToolGuide Action=Workflow Topic=type_schema` повернув workflow з
  `TypeFingerprint`, `TypeIndex`, `WriteToFile`;
- `ReadConsole DiagnosticSummary`: totals 0 logs/warnings/errors.

## 2026-06-16: UniBridge 0.2.18 BatchActions post-action diagnostics

Додано opt-in self-check прямо в `UniBridge_BatchActions`, щоб агент після
multi-step workflow одразу бачив нові console/editor сигнали, не роблячи
окремий ручний marker/read цикл.

Нові top-level параметри `UniBridge_BatchActions`:

- `IncludeConsoleDelta=false` за замовчуванням;
- `ConsoleDeltaMarkerLabel`;
- `ConsoleDeltaMaxIssues=5`;
- `ConsoleDeltaMaxSamples=5`;
- `IncludeEditorEventDelta=false` за замовчуванням;
- `EditorEventDeltaLimit=25`.

Поведінка:

- якщо `IncludeConsoleDelta=true`, batch перед виконанням створює
  `UniBridge_ReadConsole Action=MarkSession`, а після кроків додає
  `data.postActionDiagnostics.consoleDelta`;
- console delta повертає compact `DiagnosticSummary` лише для entries після
  marker: totals, dominant issue, critical groups, warning groups, likely spam,
  recent samples; `timelineHighlights` навмисно опущено, щоб не роздувати
  відповідь;
- якщо `IncludeEditorEventDelta=true`, batch бере
  `EditorEventHistory.LatestId()` перед виконанням і після кроків додає
  `data.postActionDiagnostics.editorEventDelta` з bounded snapshot;
- self-check не має валити весь batch, якщо сам diagnostic read не вдався:
  помилка йде в `postActionDiagnostics.warnings`.

Навіщо це:

- агент після scene/UI/material/object batch-змін отримує власний короткий
  “чи я щось зламав у консолі/редакторі” блок;
- не треба вручну пам'ятати sequence `MarkSession -> BatchActions ->
  DiagnosticSummary AfterMarkerId`;
- це доповнює, а не дублює `ReadConsole` і `EditorEvents`.

Discoverability/docs:

- `Discover` має workflow `batch_self_check`;
- `ToolGuide` batch workflow радить
  `DryRun=false IncludeConsoleDelta=true IncludeEditorEventDelta=true`;
- `DomainCatalog` Safety domain згадує console/editor event deltas;
- search aliases доповнено `console delta`, `post action diagnostics`,
  `batch self check`;
- package version піднято до `0.2.18`;
- оновлено `README.md`, `RELEASE_NOTES.md`, `CHANGELOG.md`,
  `Documentation~/unibridge.md`, `package.json`.

Перевірка через MCP на
`H:/Repos/UnityRepos/UniBridge_Test_Project`:

- package 0.2.18 скопійовано в
  `Packages/com.cidonix.unibridge` без `RelayApp~`;
- `ManageEditor RefreshAssets WaitForCompletion=true Force=true` пройшов
  через Unity reload boundary і повернув reload-safe result:
  `recoveredAfterRefreshReload=true`, `reloadBoundary=true`,
  `reloadSafe=true`, compile diagnostics errors 0, warnings 0;
- `UniBridge_BatchActions DryRun=true IncludeConsoleDelta=true
  IncludeEditorEventDelta=true` повернув `data.postActionDiagnostics`:
  console marker створено, totals 0 logs/warnings/errors, compact summary
  порожній, `timelineHighlights` опущено, editor event delta count 0;
- `UniBridge_BatchActions DryRun=false IncludeImpact=false
  IncludeWorkSessionReview=false IncludeConsoleDelta=true
  IncludeEditorEventDelta=true` з read-only editor diagnostics step виконався
  успішно: rollback committed, console delta 0 logs/warnings/errors,
  editor event delta clean, compilation diagnostics errors 0, warnings 0.

## 2026-06-16: UniBridge 0.2.17 RuntimeStateProbe assertions/watch rules

Додано `UniBridge_RuntimeStateProbe Action=Assert` як read-only pass/fail gate
для AI workflow. Це продовження `RuntimeStateProbe`: агент може не тільки
подивитися live component state, а й формально перевірити очікування перед тим,
як рухатись далі.

Що вміє `Action=Assert`:

- приймає `Assertions` масив із rules:
  `name`, `member/memberPath`, optional `valuePath`, `operator`,
  `value/expected`, `min/max`, `mode`, `required`, `tolerance`;
- operators: `exists`, `not_exists`, `==`, `!=`, `>`, `>=`, `<`, `<=`,
  `between`, `contains`, `not_contains`, `matches`, `is_null`, `not_null`,
  `changed`, `stable`;
- modes: `Last`, `First`, `Any`, `All`, `Changed`, `Stable`;
- за замовчуванням `FailOnFailedAssertions=true`, тобто required failed
  assertion повертає `success=false` і може зупинити `BatchActions`;
- якщо передати `FailOnFailedAssertions=false`, tool повертає `success=true`,
  але `passed=false`, що корисно для non-blocking diagnostics;
- full raw assertion payload пишеться у
  `Library/UniBridge/RuntimeStateProbe` при `SaveToFile=true`.

Discoverability:

- додано aliases: `runtime_assert`, `watch_assert`, `state_assert`,
  `expect_state`;
- `Discover`, `ToolGuide`, `DomainCatalog`, `BatchActionToolCatalog`,
  `BatchActions.Steps`, `BatchActions.Validation` описують новий assert
  workflow;
- docs оновлено в `README.md`, `RELEASE_NOTES.md`,
  `CHANGELOG.md`, `Documentation~/unibridge.md`, `package.json`;
- package version піднято до `0.2.17`.

Важливий edge case, який зловив smoke:

- `Transform.localScale.x` може читатись як reflected path `localScale.x`,
  тоді як base `localScale` також може мати serialized representation
  `m_LocalScale`;
- assertion expansion тепер додає і original dotted member, і normalized
  base/subpath форму, щоб rules типу `member=localScale.x` не губилися між
  serialized/reflection шляхами.

Перевірка через MCP на
`H:/Repos/UnityRepos/UniBridge_Test_Project`:

- package 0.2.17 скопійовано в
  `Packages/com.cidonix.unibridge` без `RelayApp~`;
- `ManageEditor RefreshAssets WaitForCompletion=true Force=true` двічі
  проходив через Unity reload boundary і щоразу повертав reload-safe result:
  `recoveredAfterRefreshReload=true`, `reloadBoundary=true`,
  `reloadSafe=true`, editor ready;
- `GetCompilationDiagnostics`: errors 0, warnings 0;
- `ReadConsole DiagnosticSummary`: 0 logs/warnings/errors/exceptions/asserts;
- `ToolGuide Action=Tool Tool=runtime_assert` резолвить alias у
  `UniBridge_RuntimeStateProbe`, enabled true, batchAllowed true;
- `DomainCatalog InspectDomain RuntimeDebug` показує `runtime_assert` /
  `watch_assert` у runtime-debug domain;
- `Discover Action=Tools Query=RuntimeStateProbe` і
  `Discover Action=Tools Query="runtime assert"` повертають
  `UniBridge_RuntimeStateProbe`;
- `Discover Action=Aliases Query=runtime_assert` повертає alias
  `runtime_assert -> UniBridge_RuntimeStateProbe`;
- positive assertion smoke:
  `Transform.localScale.x == 1` і `Transform.position.y > -1000` -
  `passed=true`, 3 samples, raw JSON saved;
- non-blocking failure smoke:
  `Transform.localScale.x == 999` з `FailOnFailedAssertions=false` -
  tool `success=true`, `passed=false`;
- fail-fast smoke:
  та сама required failure без `FailOnFailedAssertions=false` повертає
  structured `success=false`, `stopReason="Step 1 execution failed."`, без
  MCP transport/connection error.

## 2026-06-16: UniBridge 0.2.16 RuntimeStateProbe для live component state debugging

Додано другий Locus-inspired runtime/debugging блок, якого в UniBridge реально
не вистачало після `RuntimeProfiler`: read-only state probe для живих
GameObject/component значень.

Новий tool:

- `UniBridge_RuntimeStateProbe`;
- actions:
  - `Snapshot` - один раз читає поточні значення компонентів/полів;
  - `Sample` - читає ті самі значення протягом bounded editor ticks,
    повертає компактний `changeSummary`, optional raw JSON пише у файл;
  - `ListMembers` - показує readable `SerializedProperty` paths і reflected
    fields/properties для компонента;
- не виконує arbitrary C# у проекті; це принципова відмінність від Locus
  Roslyn/JIT-style execution;
- читає Unity `SerializedObject`/`SerializedProperty` значення через
  `SerializedPropertyPatcher.SerializePropertyValue` і reflection-only
  public/readable members;
- explicit member paths можуть читати конкретні reflected fields/properties;
- lookup використовує shared `SceneObjectLocator`, тому підтримує inactive
  objects, Prefab Stage, instance IDs, hierarchy paths, ByComponent,
  component short/full names, MonoScript GUID/path і serialized editor class
  identifiers;
- `Action=Sample` за замовчуванням вимагає Play Mode, але має
  `RequirePlayMode=false` для editor-time smoke;
- full sample JSON пишеться в
  `Library/UniBridge/RuntimeStateProbe` при `SaveToFile=true`;
- tool read-only і batch-safe.

Discoverability:

- `Discover`, `ToolGuide`, `DomainCatalog` отримали runtime state aliases:
  `RuntimeStateProbe`, `runtime_state`, `runtime_probe`,
  `runtime_state_probe`, `state_probe`, `watch_state`, `watch_variables`,
  `component_state`, `monobehaviour_state`, `runtime_fields`;
- `runtime` лишається alias для `UniBridge_RuntimeProfiler`;
- `runtime_state` тепер веде до `UniBridge_RuntimeStateProbe`, бо це точніше
  для сценарію “подивись значення компонентів”;
- `BatchActions` allow-list вже підтримував tool, додано parameter aliases для
  `RuntimeProfiler` і `RuntimeStateProbe`;
- package version піднято до `0.2.16`;
- оновлено `CHANGELOG.md`, `RELEASE_NOTES.md`, `README.md`,
  `Documentation~/unibridge.md`, `package.json`.

Важлива різниця між runtime tools:

- `UniBridge_RuntimeProfiler` - “що відбувається з продуктивністю/кадрами/GC”;
- `UniBridge_RuntimeStateProbe` - “які live значення у конкретних
  GameObject/Component/MonoBehaviour полях протягом кадрів”.

Перевірка:

- package скопійовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`
  без `RelayApp~`, бо relay exe був заблокований відкритим Unity Editor;
- перший MCP refresh виявив реальну compile-помилку:
  `RuntimeStateProbe.cs`: `Cannot yield a value in the body of a catch clause`;
- помилку виправлено, package повторно синхронізовано в тестовий проект;
- `ManageEditor RefreshAssets WaitForCompletion=true Force=true` пройшов через
  очікуваний reload boundary і повернув reload-safe результат:
  `recoveredAfterRefreshReload=true`, `reloadBoundary=true`,
  `reloadSafe=true`, editor ready;
- `_server_info` після reload показав `toolCount=66`;
- `Discover Action=Tools Query=RuntimeStateProbe` підтвердив
  `UniBridge_RuntimeStateProbe`, enabled true, batchAllowed true;
- `ToolGuide Action=Tool Tool=runtime_probe` резолвить alias у
  `UniBridge_RuntimeStateProbe` і показує workflow `runtime_state_probe`;
- `DomainCatalog InspectDomain RuntimeDebug` показує `RuntimeProfiler` і
  `RuntimeStateProbe` у runtime-debug домені;
- реальний MCP batch smoke:
  - `runtime_probe Action=ListMembers Component=Transform` - success,
    повертає SerializedProperty paths і reflected properties;
  - `runtime_probe Action=Snapshot Component=Transform Members=[m_LocalPosition,
    position, localPosition, localScale]` - success;
  - `runtime_probe Action=Sample SampleFrames=5 RequirePlayMode=false` -
    success, 5 rows, raw JSON saved under
    `Library/UniBridge/RuntimeStateProbe`;
  - `runtime_profiler Action=Sample SampleFrames=3 RequirePlayMode=false` -
    success, raw JSON saved under `Library/UniBridge/RuntimeProfiler`;
- `GetCompilationDiagnostics`: errors 0, warnings 0;
- `ReadConsole DiagnosticSummary`: 0 logs/warnings/errors/exceptions/asserts.

Нотатка:

- Codex `tool_search` у вже відкритій MCP/Codex сесії може не одразу expose-ити
  новий direct callable після Unity reload, але Unity-side registry, `Discover`,
  `ToolGuide`, `DomainCatalog` і `BatchActions` вже бачать tool. Для поточної
  сесії найстабільніший виклик - через `BatchActions` alias `runtime_probe`.

## 2026-06-16: UniBridge 0.2.15 RuntimeProfiler для runtime/performance triage

Додано перший Locus-inspired блок, якого в UniBridge реально не вистачало:
read-only runtime/profiler inspection tool для агента.

Новий tool:

- `UniBridge_RuntimeProfiler`;
- actions:
  - `Snapshot` - компактний стан Editor/Play Mode, loaded scenes, object totals,
    behaviour type counts, memory snapshot, supported metrics;
  - `Metrics` - список підтриманих profiler metric aliases/category/name;
  - `Sample` - bounded `ProfilerRecorder` sampling з avg/p50/p95/max/last,
    spike summary і optional raw JSON output;
- `Action=Sample` за замовчуванням вимагає Play Mode, але має
  `RequirePlayMode=false` для editor-time smoke;
- full sample JSON пишеться в
  `Library/UniBridge/RuntimeProfiler` при `SaveToFile=true`;
- tool є read-only і не виконує arbitrary C# у проекті.

Підтримані метрики на момент додавання:

- `main_thread_ms`, `render_thread_ms`, `gc_alloc_bytes`,
  `gc_reserved_mb`, `system_used_memory_mb`, `total_used_memory_mb`,
  `batches_count`, `setpass_calls`, `triangles_count`, `vertices_count`,
  `script_update_ms`, `physics_simulate_ms`, `physics2d_simulate_ms`.

Discoverability:

- `Discover`, `ToolGuide`, `DomainCatalog` отримали runtime/profiler aliases:
  `runtime_profiler`, `runtime`, `profiler`, `performance`, `fps`,
  `frame_time`, `spikes`, `gc_profile`, `memory_profile`,
  `playmode_profiler`;
- `BatchActions` allow-list і aliases тепер підтримують
  `runtime_profiler`, тож агенти можуть запускати profiler snapshot/sample
  в batch workflow;
- package version піднято до `0.2.15`;
- оновлено `CHANGELOG.md`, `RELEASE_NOTES.md`, `README.md`,
  `Documentation~/unibridge.md`, `package.json`.

Перевірка:

- package скопійовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `ManageEditor RefreshAssets WaitForCompletion=true Force=true` у тестовому
  проекті пройшов reload-safe recovery:
  `recoveredAfterRefreshReload=true`, `reloadBoundary=true`,
  `reloadSafe=true`, editor ready;
- `_server_info` у тестовому проекті показав relay `1.1.0-build.15`,
  Unity connected true, toolCount `65`;
- `Discover Action=Tools Query=RuntimeProfiler` підтвердив
  `UniBridge_RuntimeProfiler`, enabled true, batchAllowed true;
- реальний MCP batch smoke:
  - `runtime_profiler Action=Metrics` - success, 13 metric entries;
  - `runtime_profiler Action=Snapshot` - success, active scene
    `Assets/CorgiEngine/Demos/Minimal/FeaturesPlatforms.unity`, 363
    GameObjects, 0 missing scripts;
  - `runtime_profiler Action=Sample SampleFrames=5 RequirePlayMode=false`
    - success, 5 rows, raw JSON saved under
    `Library/UniBridge/RuntimeProfiler`;
- `GetCompilationDiagnostics`: errors 0, warnings 0;
- `ReadConsole DiagnosticSummary`: 0 logs/warnings/errors/exceptions/asserts.

Нотатка:

- Codex `tool_search` може не одразу expose-ити новий direct callable після
  Unity reload, але Unity-side registry і `BatchActions` вже бачать tool.
  Для нового агента найстабільніший шлях до появи tool - перезапуск MCP/agent
  session або використання `BatchActions` alias `runtime_profiler`.

## 2026-06-09: UniBridge 0.2.10 RefreshAssets reload-safe recovery

Виправлено Domovyk repro, де після успішного `ValidateScript` виклик
`UniBridge_ManageEditor Action=RefreshAssets WaitForCompletion=true Force=true`
повертав hard MCP failure:
`Unity connection lost: Unity connection closed`.

Зміна:

- relay оновлено до `1.1.0-build.15`;
- `UniBridge.Relay` тепер розпізнає `RefreshAssets` як reload recovery
  candidate для `UniBridge_ManageEditor` і nested `UniBridge_BatchActions`;
- якщо Unity закриває pipe під час `AssetDatabase.Refresh`, relay:
  - reconnect-иться до нового bridge;
  - викликає `WaitForReadyAfterReload` з fallback на `WaitForReady`;
  - читає `GetCompilationDiagnostics`;
  - повертає structured result з `reloadBoundary=true`,
    `reconnectRequired=true`, `reloadSafe=true`,
    `recoveredAfterRefreshReload=true`, `nextSuggestedCalls`;
  - не повертає transport-level MCP error для очікуваного refresh/import
    boundary;
- package version піднято до `0.2.10`;
- оновлено `CHANGELOG.md`, `RELEASE_NOTES.md`, `README.md`,
  `Documentation~/unibridge.md`, `package.json`, `RelayApp~/relay.json`;
- cross-platform relay binaries перепубліковано для `win-x64`, `linux-x64`,
  `osx-x64`, `osx-arm64`.

Синхронізація:

- package скопійовано в:
  `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/DomovykPrototype/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- в усіх трьох проектах `package.json` показує `0.2.10`,
  `RelayApp~/relay.json` показує `1.1.0-build.15`;
- installed relay файл `%USERPROFILE%/.unibridge/relay/unibridge_relay_win.exe`
  також оновлено до `1.1.0-build.15`.
- перевірено новий installed relay шляхом прямого MCP старту з
  `%USERPROFILE%/.unibridge/relay/unibridge_relay_win.exe --mcp --project-path H:/Repos/UnityRepos/Domovyk`:
  server version `1.1.0-build.15`, `UniBridge_Discover Action=Ping` повертає
  package `0.2.10`, connected true.
- sanity ping до `UniBridge_Test_Project` через новий relay стартує server
  version `1.1.0-build.15`, але live Unity bridge для цього project path не
  знайдений, бо тестовий Unity project на момент перевірки не був запущений;
  discovery показує тільки відкриті `Domovyk` і `DomovykPrototype`.

Перевірка в Domovyk через новий package relay:

- `initialize`: server version `1.1.0-build.15`;
- `UniBridge_Discover Action=Ping`: package `0.2.10`, connected true;
- `UniBridge_ValidateScript`
  `Uri=Assets/_Domovyk/Scripts/Cinematics/DarknessDarkEntityRevealCutscene.cs`
  `Level=standard IncludeDiagnostics=true`: success, `diagnostics=[]`;
- `RefreshAssets WaitForCompletion=true Force=true RequireNotPlaying=true`:
  Unity реально закрила connection, relay recover-нув відповідь:
  `recoveredAfterRefreshReload=true`, `reloadBoundary=true`,
  `reconnectRequired=true`, `reloadSafe=true`, `elapsedMs=8155`;
- nested `waitResult` після reconnect:
  `isReady=true`, `isCompiling=false`, `isUpdating=false`,
  `isPlaying=false`;
- `compilationDiagnostics`: errors 0, warnings 0;
- `RequestScriptCompilationNoWait Force=true`: queued, `reloadSafe=true`;
- `WaitForReadyAfterReload`: success, editor ready;
- `GetCompilationDiagnostics`: retained errors 0, warnings 0;
- `ReadConsole Action=DiagnosticSummary`: totals 0 logs/warnings/errors/
  exceptions/asserts, no critical/warning groups.

Важлива operational нотатка:

- вже запущені MCP relay процеси можуть залишатися старою версією в пам'яті;
  нові MCP server launches підхоплюють installed relay `1.1.0-build.15`.
  Якщо агент у Domovyk продовжує бачити стару поведінку, треба перезапустити
  MCP server/AI agent session.

## 2026-06-09: Roslyn 5.3.0 dependency experiment

Перед експериментом створено локальний Git checkpoint:

- `ec6ed0d Checkpoint UniBridge 0.2.9 stable`;
- GitHub repo `Cidonix/UniBridge` наразі public і його `README.md` каже, що
  package source/relay binaries не публікуються, тому source не пушився в
  public GitHub без окремого підтвердження.

Зміна:

- `Plugins/CodeAnalysis/Microsoft.CodeAnalysis.dll` оновлено з 3.11.x до
  5.3.0;
- `Plugins/CodeAnalysis/Microsoft.CodeAnalysis.CSharp.dll` оновлено з 3.11.x
  до 5.3.0;
- `.meta` GUID-и DLL збережено;
- `System.Collections.Immutable.dll` і `System.Reflection.Metadata.dll` не
  чіпались, бо в пакеті вже лежать 10.0.x, а Roslyn 5.3.0 restore просив
  9.0.0;
- changelog і `_upm.changelog` оновлено як dependency hotfix.

Перевірка після оновлення:

- package синхронізовано в:
  `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/DomovykPrototype/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- Domovyk live MCP smoke:
  - `RefreshAssets Force=true WaitForCompletion=true`: success;
  - `GetCompilationDiagnostics IncludeWarnings=true`: retained errors 0,
    warnings 0;
  - `ValidateScript Uri=Assets/Ed/Scripts/FPSCounter.cs`: success,
    `diagnostics=[]`;
  - `UniBridge_Discover Action=Ping`: success, package `0.2.9`;
- DomovykPrototype live MCP smoke:
  - `RefreshAssets Force=true WaitForCompletion=true`: success;
  - `GetCompilationDiagnostics IncludeWarnings=true`: retained errors 0,
    warnings 0;
  - positive probe з `"Frame " + Time.frameCount` усередині `Update()`:
    warning повернувся з точним `line=7`, `col=19` і expression snippet;
  - temp probe script видалено, повторна compilation diagnostics чиста;
- UniBridge_Test_Project package фізично синхронізовано, але live MCP bridge
  connection для нього не був знайдений у discovery на момент перевірки, тому
  саме live smoke там не виконувався.

Висновок: Roslyn 5.3.0 у двох live Unity проектах не погіршив роботу
`ValidateScript` і не дав compile diagnostics. Якщо з'являться regressions,
відкат можливий до checkpoint `ec6ed0d`.

## 2026-06-09: ValidateScript Update string-allocation false-positive hotfix

Виправлено false positive у `UniBridge_ValidateScript` для Domovyk repro:

- виклик:
  `UniBridge_ValidateScript Uri=Assets/Ed/Scripts/FPSCounter.cs Level=standard IncludeDiagnostics=true`;
- до правки повертався generic warning:
  `String concatenation in Update() can cause garbage collection issues`
  з `line=0`, `col=0`;
- у фактичному `FPSCounter.cs` `Update()` не містить string interpolation,
  string `+`, `string.Concat` або `ToString(...)`; label оновлюється через
  `StringBuilder` і `TextMeshProUGUI.SetText(StringBuilder)` у
  `RefreshLabel`, тобто поза `Update()`.

Причина:

- старий fallback-чек у `ManageScript.ValidateScriptSyntaxUnity` дивився весь
  файл:
  `contents.Contains("Update()") && contents.Contains("\"") && contents.Contains("+")`;
- через це будь-який `+` і будь-які лапки в інших методах файлу могли
  створити warning ніби проблема в `Update()`.

Зміна:

- warning logic замінено на Roslyn syntax-tree pass, scoped тільки до
  `void Update()` без параметрів;
- warning створюється лише для конкретних expressions усередині `Update()`:
  string interpolation, очевидний string `+`, `string.Concat(...)`,
  `ToString(...)`;
- diagnostics parser тепер читає `(Line X, Column Y)` і повертає `col`;
- warning message містить expression snippet, щоб це було actionable;
- якщо в `Update()` немає конкретної string allocation expression, warning не
  повертається.

Документація:

- `CHANGELOG.md` і `package.json` `_upm.changelog` оновлено як `0.2.9`
  hotfix без version bump.

Перевірка:

- package синхронізовано в:
  `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/DomovykPrototype/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- Domovyk live MCP:
  - `RefreshAssets WaitForCompletion=true`: success;
  - `UniBridge_ValidateScript Uri=Assets/Ed/Scripts/FPSCounter.cs
    Level=standard IncludeDiagnostics=true`: success,
    `diagnostics=[]`;
  - `GetCompilationDiagnostics`: retained errors 0, warnings 0;
- DomovykPrototype live MCP:
  - `RefreshAssets WaitForCompletion=true`: success after relay reconnect;
  - тимчасовий `Assets/UniBridgeSmoke/ValidateScriptStringAllocProbe.cs`
    з `Debug.Log("Frame " + Time.frameCount);` у `Update()` повернув warning
    з `line=7`, `col=19` і expression snippet
    `"Frame " + Time.frameCount`;
  - probe script видалено через MCP, `.cs` і `.meta` відсутні на диску;
  - `GetCompilationDiagnostics`: retained errors 0, warnings 0.

## 2026-06-09: ValidateAdditiveSceneRegistration stale-reference scope hotfix

Виправлено false positive у
`UniBridge_ValidateAdditiveSceneRegistration CheckStaleReferences`, коли
агент передавав `TemplateSceneName`.

Проблема:

- для `darkness12` з `TemplateSceneName=darkness4` validator сканував весь
  `scenesManager.prefab`;
- знаходив легальний окремий runtime entry `- Scene: darkness4`;
- повертав `STALE_REFERENCE_FOUND`, хоча це не stale reference у `darkness12`,
  а валідний запис іншої сцени;
- аналогічно для `darkness13` + `TemplateSceneName=darkness5`.

Зміна:

- stale-reference scan тепер scoped до:
  - target scene YAML;
  - target metadata asset;
  - target entry поточної сцени в `scenesManager.prefab`;
- весь `scenesManager.prefab` глобально більше не сканується для
  `TemplateSceneName`/template GUID checks;
- response додає info code
  `STALE_REFERENCE_SCENES_MANAGER_SCOPE`, щоб агент бачив, що scan був
  обмежений target entry;
- changelog і `_upm.changelog` оновлено як `0.2.9` hotfix без окремого
  version bump.

Перевірка:

- package синхронізовано в:
  `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/DomovykPrototype/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- Domovyk live MCP:
  - `RefreshAssets WaitForCompletion=true` успішний, relay пережив reconnect;
  - `ScenePath=Assets/_Domovyk/Scenes/darkness/darkness12.unity`,
    `TemplateSceneName=darkness4`:
    `success=true`, `passed=true`, `errorCount=0`,
    `staleReferences.matchCount=0`, `errorCodes=[]`;
  - `ScenePath=Assets/_Domovyk/Scenes/darkness/darkness13.unity`,
    `TemplateSceneName=darkness5`:
    `success=true`, `passed=true`, `errorCount=0`,
    `staleReferences.matchCount=0`, `errorCodes=[]`;
  - `GetCompilationDiagnostics`: retained errors 0, warnings 0;
  - `ReadConsole DiagnosticSummary`: no critical/warning groups.

## 2026-06-09: UniBridge 0.2.9 discoverability and additive scene validation

Додано polish-реліз `0.2.9`, сфокусований на тому, щоб нові Codex/AI
агенти стабільно знаходили UniBridge tools через MCP/tool_search і мали
read-only перевірку additive scene setup у Domovyk-подібних проєктах.

Зміни:

- додано read-only tool `UniBridge_Discover`:
  - actions: `Ping`, `Workflows`, `Aliases`, `Tools`, `Status`;
  - опис містить searchable aliases:
    `UniBridge`, `Unity`, `ValidateScript`, `RefreshAssets`,
    `RequestScriptCompilationNoWait`, `WaitForReadyAfterReload`,
    `GetCompilationDiagnostics`, `ReadConsole`, `DiagnosticSummary`,
    `ClearConsole`, `PlayMode`, `WaitForPlayMode`, `WaitForEditMode`,
    `ValidateAdditiveSceneRegistration`;
  - `ToolGuide` overview тепер рекомендує `UniBridge_Discover` як ранній
    first ping для нового агента;
- додано read-only tool `UniBridge_ValidateAdditiveSceneRegistration`:
  - перевіряє `.unity`/`.meta` GUID, metadata `.asset`/`.meta` GUID,
    scene-to-metadata YAML refs, `ProjectSettings/EditorBuildSettings.asset`,
    `scenesManager.prefab`, `SceneBoundaries`,
    `SceneLoadingBoundaries`, `ScenePaddingBoundaries`,
    `ScenePaddingWideScreenExpansion`, stale template/old refs і optional
    neighbor sanity;
  - без Undo/rollback, read-only, придатний для BatchActions;
- `BatchActions` allow-list/aliases оновлено:
  `discover`, `mcp_ping`, `tool_discovery`, `validate_additive_scene`,
  `validate_additive_scene_registration`, `additive_scene_validation`,
  `scene_registration`, `scene_metadata_validation`;
- `ToolExecutionScheduler` позначає обидва нові tools як read-only;
- `ToolGuide`, `DomainCatalog`, README, docs, release notes і changelog
  описують workflow:
  `ValidateScript -> RefreshAssets -> RequestScriptCompilationNoWait ->
  WaitForReadyAfterReload -> GetCompilationDiagnostics ->
  ReadConsole/DiagnosticSummary`;
- Play Mode boundary workflow задокументовано:
  `ClearConsole -> Play WaitForCompletion=true -> WaitForPlayMode ->
  WaitForReady RequireNotPlaying=false -> DiagnosticSummary ->
  ExitPlayMode -> WaitForEditMode -> WaitForReady RequireNotPlaying=true`;
- relay піднято до `1.1.0-build.14`; bundled binaries перепубліковано для
  win-x64, linux-x64, osx-x64, osx-arm64;
- `McpToolRegistry` max readable tool-name cap піднято з 42 до 43 символів,
  бо `UniBridge_ValidateAdditiveSceneRegistration` має 43 символи і до цього
  хешувався як `UniBridge_ValidateAdditiveSceneRe_...`, що було погано для
  нового агента.

Перевірка:

- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj -c Release`:
  0 errors, 0 warnings;
- package синхронізовано в:
  `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/DomovykPrototype/Packages/com.cidonix.unibridge`,
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- Domovyk live MCP smoke через relay:
  - `tools/list`: 64 tools;
  - `UniBridge_Discover` присутній і повертає package `0.2.9`;
  - `UniBridge_ValidateAdditiveSceneRegistration` присутній під повною
    стабільною назвою, без hashed suffix;
  - direct validation
    `ScenePath=Assets/_Domovyk/Scenes/darkness/darkness12.unity`:
    `passed=true`, 0 errors, Build Settings present, metadata asset found,
    `scenesManager.prefab` entry found;
  - `UniBridge_BatchActions DryRun=true` з alias `validate_additive_scene`
    для `darkness13` проходить validation;
  - compile diagnostics workflow по
    `DarknessCutsceneTrigger.cs` і
    `DarknessDarkEntityRevealCutscene.cs`:
    `ValidateScript` чистий, `RequestScriptCompilationNoWait` queued,
    `WaitForReadyAfterReload` ready, retained diagnostics 0, console summary
    без critical/warning groups;
  - Play Mode boundary workflow не падає transport-level:
    `Play WaitForCompletion=true` і `ExitPlayMode WaitForCompletion=true`
    повертають queued boundary responses, reconnect/wait працюють.
    Під час Play Domovyk консоль показала project runtime error
    `GrabZone must be child of PushableObject!`, це не UniBridge failure;
- DomovykPrototype після `RefreshAssets`/domain reload:
  - `tools/list`: 64 tools;
  - `UniBridge_Discover` і
    `UniBridge_ValidateAdditiveSceneRegistration` присутні;
  - `UniBridge_Discover Action=Ping` повертає package `0.2.9`;
- поточний Codex `tool_search` після refresh більше не повертає 0 tools
  на workflow query; він знаходить UniBridge namespaces/tools. Точний query
  по новому validator у цьому вже відкритому thread ранжується не завжди
  прямо до validator, але `ToolGuide Action=Tool Tool=validate_additive_scene`
  резолвить alias у `UniBridge_ValidateAdditiveSceneRegistration`;
- `UniBridge_Test_Project` package фізично оновлений до `0.2.9`, але під час
  перевірки bridge discovery file для нього не був опублікований у
  `%USERPROFILE%/.unibridge/mcp/connections`, тому live MCP підключення до
  тестового проєкту не відбулось. Domovyk і DomovykPrototype bridge були live.

## 2026-05-15: VisualSceneAudit self-check for post-work visual QA

Додано `UniBridge_VisualSceneAudit` як перший практичний шар
самоконтролю після будь-якої видимої роботи в Unity: сцена, матеріали,
камера, світло, VFX або UI staging.

Зміни:

- новий tool `UniBridge_VisualSceneAudit` з діями:
  `AuditCapture`, `AuditImage`, `AuditScene`;
- `AuditCapture` рендерить камеру в PNG, `AuditImage` аналізує готовий
  PNG, `AuditScene` перевіряє scene metadata без запису PNG;
- audit перевіряє:
  fallback-magenta-like pixel dominance, near-white block dominance,
  single-color/low-diversity captures, надто темний/яскравий кадр,
  missing/broken materials/shaders, magenta material colors,
  target camera framing і console diagnostics через
  `UniBridge_ReadConsole Action=DiagnosticSummary`;
- `success=true` означає, що audit виконався, а якість повертається як
  `data.passed`; `FailOnIssues=true` робить failing audit MCP error;
- додано batch aliases:
  `visual_audit`, `visual_scene_audit`, `scene_audit`,
  `presentation_audit`, `self_check`, `visual_qa`;
- `ToolGuide`, `DomainCatalog`, `BatchActions`, scheduler capture policy і
  `Documentation~/unibridge.md` оновлено так, щоб нові агенти бачили
  VisualSceneAudit як рекомендований фінальний self-check після видимої
  роботи, а не тільки як smoke test фічі.

Перевірка:

- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лишився тільки старий
  `System.Threading.Tasks.Extensions` warning;
- Unity 6 obsolete warnings, які з'явились від нового
  `FindObjectsByType` overload, прибрано до фінального build;
- live Unity smoke виконано тільки через MCP relay для
  `UniBridge_Test_Project`:
  - після `RefreshAssets` нова MCP-сесія показала `tools/list`: 60 tools,
    `UniBridge_VisualSceneAudit` присутній, schema actions:
    `AuditCapture`, `AuditImage`, `AuditScene`;
  - clean demo `__UniBridgeProbeDemo_CLEAN_VISIBLE` через
    `AuditCapture Target=... Camera="Demo Overview Camera"`:
    `passed=true`, `score=76`, 0 errors, 2 м'які warnings по темному
    однотонному фону (`high_single_color_area`, `low_color_diversity`);
  - PNG clean audit відкрито через vision і візуально підтверджено:
    акуратна dark scene з маркерами/об'єктами, без magenta fallback або
    білого placeholder block;
  - навмисно поганий temporary scene через MCP BatchActions:
    великий magenta plane + білий cube; `AuditCapture` повернув
    `passed=false`, `fallback_magenta_dominance`,
    `magentaRatio=0.4367`, 1 error, 2 warnings;
  - PNG bad audit відкрито через vision і візуально підтверджено саме той
    клас провалу, який tool має ловити;
  - `AuditImage` на bad PNG з `FailOnIssues=true` повернув
    `success=false`, `passed=false`, issue codes:
    `fallback_magenta_dominance`, `low_color_diversity`;
  - batch alias `visual_audit` dry-run успішно пройшов validation;
  - bad smoke root прибрано через MCP BatchActions cleanup;
  - фінальний `UniBridge_ManageEditor Action=GetCompilationDiagnostics`:
    0 retained errors/warnings;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`:
    73 logs, 0 warnings/errors/exceptions/asserts.

## 2026-05-15: EditorSnapshot dock tabs and Prefab autosave state

Реалізовано шостий пункт фінального pass: `UniBridge_EditorSnapshot`
краще зберігає і повертає Unity Editor workspace state.

Зміни:

- snapshot capture тепер може включати активний tab у кожному Unity dock
  area (`dockTabs`) і Prefab Mode autosave settings (`prefabAutoSave`);
- нові параметри:
  `IncludeDockTabs`, `RestoreDockTabs`,
  `IncludePrefabAutoSave`, `RestorePrefabAutoSave`;
- restore dry-run/plan тепер явно показує дії для Prefab autosave і active
  dock tabs;
- restore намагається re-show captured active dock tabs без переписування
  Unity layout files, а focused window restore лишається консервативним;
- Prefab autosave restore працює через public `EditorSettings` і
  reflection-safe access до internal StageNavigationManager, якщо він
  доступний у конкретній Unity версії;
- `BatchActions` aliases/validation, `ToolGuide`, `DomainCatalog` і
  `Documentation~/unibridge.md` оновлено.

Перевірка:

- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лише старий `System.Threading.Tasks.Extensions`
  warning;
- live Unity smoke виконано тільки через MCP relay для editor pid `55180`:
  - `tools/list`: `UniBridge_EditorSnapshot` schema містить
    `IncludeDockTabs`, `RestoreDockTabs`, `IncludePrefabAutoSave`,
    `RestorePrefabAutoSave`;
  - `UniBridge_EditorSnapshot Action=Capture Persist=true` зберіг snapshot
    `editor_20260515_000944_678`, `dockTabs_count=6`,
    `prefabAutoSave={prefabModeAllowAutoSave:true,
    stageNavigationAutoSave:true}`;
  - `Inspect` по snapshot id успішний;
  - `Restore DryRun=true` повернув plan без blockers, включно з
    `Restore Prefab Mode autosave settings` і
    `Restore active dock tabs (6 tab(s))`;
  - `UniBridge_BatchActions DryRun=true` з alias `editor_snapshot`,
    `restore_dock_tabs=true`, `restore_prefab_auto_save=true` успішний;
  - snapshot видалено через `UniBridge_EditorSnapshot Action=Delete`;
  - `UniBridge_ManageEditor Action=GetCompilationDiagnostics`:
    0 retained errors/warnings;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`:
    23 logs, 0 warnings/errors/exceptions/asserts.

## 2026-05-15: UI Toolkit capture reliability and layout diagnostics

Реалізовано п'ятий пункт фінального pass: `UniBridge_CaptureUIToolkit`
отримав додаткову стабілізацію render/readback і корисніші issue hints для
агентської UI Toolkit перевірки.

Зміни:

- додано параметри `ReadbackMode=Immediate|GpuReadback` і `RenderPasses`;
- UI Toolkit capture тепер виконує керовану кількість forced
  render/update passes перед readback і повертає metadata
  `render.passesRequested`, `passesCompleted`, `editorUpdatesWaited`,
  `requestedReadbackMode`, `readbackMode`;
- `GpuReadback` використовує synchronous `AsyncGPUReadback` з
  `ReadPixels` fallback, так само як інші visual capture tools;
- `IncludeIssues` тепер додатково знаходить:
  `TEXT_MAY_OVERFLOW_WIDTH` / `TEXT_MAY_OVERFLOW_HEIGHT` і
  `VISIBLE_ELEMENTS_OVERLAP`;
- `BatchActions` aliases/validation оновлено для `readback_mode` і
  `render_passes`;
- `ToolGuide`, `DomainCatalog` і `Documentation~/unibridge.md` оновлено,
  щоб нові агенти бачили цей шлях як рекомендовану UI Toolkit verification
  практику;
- прямий пошук по package знову не знаходить package-facing згадок
  `Bezi`, `Sidekick`, `reference implementation`, `reference-shaped`,
  `reference-informed`, або старого method provenance wording.

Перевірка:

- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лише старий `System.Threading.Tasks.Extensions`
  warning;
- live Unity smoke виконано тільки через MCP relay для editor pid `55180`:
  - `tools/list`: `UniBridge_CaptureUIToolkit` schema містить
    `ReadbackMode` і `RenderPasses`;
  - через `UniBridge_ManageUIToolkit` створено тимчасовий
    `Assets/UniBridgeUIToolkitCaptureSmoke/OverlapOverflow.uxml` з
    навмисним overlap і завузьким label;
  - `UniBridge_CaptureUIToolkit Action=Capture`, `ReadbackMode=GpuReadback`,
    `RenderPasses=3`, `Width=320`, `Height=220` повернув success,
    `render.readbackMode=GpuReadback`, `passesCompleted=3`,
    `editorUpdatesWaited=3`;
  - issue codes включали `TEXT_MAY_OVERFLOW_WIDTH` і
    `VISIBLE_ELEMENTS_OVERLAP`, PNG не blank;
  - `CaptureDiff` self-check на отриманому PNG: success,
    `changedPixels=0`;
  - `UniBridge_BatchActions DryRun=true` з alias `uxml_capture`,
    `readback_mode=GpuReadback`, `render_passes=3` validated successfully;
  - smoke folder очищено через `UniBridge_ManageAsset Action=Delete`;
  - `UniBridge_ManageEditor Action=GetCompilationDiagnostics`:
    0 retained errors/warnings;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`:
    47 logs, 0 warnings/errors/exceptions/asserts.

## 2026-05-15: package provenance cleanup

Реалізовано перший пункт фінального cleanup/pass перед закриттям
reference-аудиту:

- з package-facing source/docs/schema descriptions прибрано згадки
  `reference implementation`, `reference-shaped`, `reference-informed`,
  `Sidekick` і старі provenance формулювання;
- wording замінено на нейтральні UniBridge-native описи:
  `structured`, `agent-oriented`, `contact sheet`, `native search`,
  `one-call asset context`;
- зміни торкнулись tool descriptions, parameter descriptions,
  `Documentation~/unibridge.md` і package changelog у `package.json`;
- прямий пошук по `com.cidonix.unibridge` більше не знаходить старих
  provenance рядків.

Перевірка:

- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лишився тільки старий
  `System.Threading.Tasks.Extensions` warning;
- live Unity smoke виконано тільки через MCP relay для editor pid `55180`:
  `tools/list` повернув 59 tools, forbidden provenance strings у tool
  payload не знайдено;
- `UniBridge_ManageEditor Action=WaitIdle` успішний;
- `UniBridge_ReadConsole Action=DiagnosticSummary`: 33 logs,
  0 warnings/errors/exceptions/asserts.

## 2026-05-15: smart asset profiles expansion

Реалізовано другий пункт фінального pass: `AssetSnapshotSerializer` і
`TypeSchema` отримали ширший набір компактних smart profiles для noisy Unity
assets, щоб агент бачив суть asset-а без повного serialized dump.

Зміни:

- `AssetSnapshotSerializer` тепер має профілі для:
  `RenderTexture`, `TerrainLayer`, `AvatarMask`,
  `ShaderVariantCollection`, `TMP_FontAsset`, `AudioMixer`,
  `AudioMixerGroup`, `AudioMixerSnapshot`, `TimelineAsset`,
  `InputActionAsset`, `SpriteAtlas`, `Tile`, `VideoClip`,
  `VisualEffectAsset`, UI Toolkit `VisualTreeAsset`/`StyleSheet`/
  `ThemeStyleSheet`/`PanelSettings`, `Mesh`, `Shader`, `ComputeShader`;
- optional package типи читаються reflection-based, без жорстких compile
  dependencies на TMP/Timeline/Input System/VFX;
- text-like typed assets (`.inputactions`, `.uxml`, `.uss`, `.asset`
  з TMP/PanelSettings тощо) тепер можуть повертати JSON envelope з `profile`
  плюс bounded raw text, а не тільки plain text;
- `TypeSchema.BuildContextProfile` підключено до того самого smart profile
  generator, тому `InspectAsset` і `AssetIntelligence Serialize/Context`
  дають узгоджені summaries;
- `DomainCatalog` і документацію оновлено, щоб новий агент знав про ширше
  покриття smart profiles.

Перевірка:

- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лише старий `System.Threading.Tasks.Extensions`
  warning; нові Unity 6 obsolete warnings прибрано;
- live Unity refresh зробив очікуваний domain reload, після чого нова MCP
  сесія підключилась до editor pid `55180`;
- MCP smoke створив тимчасові assets у
  `Assets/UniBridgeSmartProfileSmoke_20260515`:
  `RenderTexture`, `TerrainLayer`, `AvatarMask`,
  `ShaderVariantCollection`, `.inputactions`;
- `UniBridge_AssetIntelligence Serialize` повернув профілі:
  `renderTexture`, `terrainLayer`, `avatarMask`,
  `shaderVariantCollection`, `inputActionAsset`,
  `uiToolkitVisualTreeAsset` для package UXML і `tmpFontAsset` для
  `LiberationSans SDF.asset`;
- `UniBridge_TypeSchema InspectAsset` для RenderTexture повернув
  `contextProfile=renderTexture`;
- smoke assets очищено через MCP, `SaveAssets` виконано;
- фінальний `UniBridge_ReadConsole DiagnosticSummary`: 0 warnings,
  0 errors, 0 exceptions/asserts.

## 2026-05-15: CaptureView contact sheet

Реалізовано третій пункт фінального pass: `UniBridge_CaptureView` отримав
`Action=CaptureContactSheet` для multi-view Scene View capture в один PNG.

Зміни:

- додано `CaptureContactSheet` у `CaptureViewAction`, input schema,
  parameter descriptions і `ToolGuide` workflow `visual_capture`;
- нові параметри:
  `Views` / `views` / `view_directions`,
  `ContactSheetColumns` / `contact_sheet_columns`,
  `IncludeContactSheetLabels` / `include_contact_sheet_labels`;
- `Views` приймає array або comma/space-separated string, підтримує alias
  `MultiView`/`Default`;
- default contact sheet без явних `Views` використовує
  `Iso`, `Front`, `Top`, `Right`; якщо задано один `View`, tool робить
  контактний лист саме з цього ракурсу;
- `SeriesCount` для contact sheet означає time slices per view і за
  замовчуванням дорівнює 1, щоб випадково не створювати 12 кадрів;
- stitched PNG має компактні labels у клітинках, per-cell metadata,
  `capturedCount`, `errorCount`, `columns`, `rows`, `cellWidth`,
  `cellHeight`, `requestedFramesPerView`, `framesPerView`;
- додано safety caps: max 64 cells і max stitched texture dimension 8192 px.

Перевірка:

- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лише старий `System.Threading.Tasks.Extensions`
  warning;
- live Unity smoke виконано тільки через MCP relay для editor pid `55180`:
  - `UniBridge_ManageEditor Action=RefreshAssets WaitForCompletion=true`:
    editor ready;
  - `tools/list`: 59 tools, schema `UniBridge_CaptureView` містить
    `CaptureContactSheet` і `Views`;
  - `UniBridge_ReadConsole Action=Clear`;
  - `UniBridge_ManageEditor Action=WaitIdle`: editor ready;
  - `UniBridge_CaptureView Action=CaptureContactSheet` для `Main Camera`,
    `Views=[Iso,Front]`, `SeriesCount=2`, `Width=320`, `Height=200`,
    `ContactSheetColumns=2`, `Overlay=true`: success,
    PNG `648x408`, `capturedCount=4`, `errorCount=0`,
    `renderMode=ContactSheet`;
  - `CaptureDiff` self-check на отриманому contact sheet PNG: success,
    `changedPixels=0`;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`:
    13 logs, 0 warnings/errors/exceptions/asserts.

## 2026-05-15: CaptureAsset contact sheet

Реалізовано четвертий пункт фінального pass: `UniBridge_CaptureAsset`
отримав `Action=CaptureContactSheet` для multi-view asset preview в один
PNG.

Зміни:

- додано `CaptureAssetAction.CaptureContactSheet`;
- нові параметри `Views`, `SeriesCount`, `SeriesIntervalSeconds`;
- `CaptureContactSheet` рендерить один asset у кількох view/time-slice
  клітинках і повертає stitched PNG з metadata mapping;
- для 3D assets без явних `Views` default:
  `Iso`, `Front`, `Top`, `Right`;
- для 2D sprite/texture assets без явних `Views` default лишається `Front`,
  щоб агент випадково не отримував тонке ребро спрайта;
- використовуються існуючі `CellWidth`, `CellHeight`, `Columns`,
  `IncludeLabels`, `Padding`, `TransparentBackground`, `AdvanceMs`;
- додано safety caps: max 64 cells і max stitched texture dimension 8192 px;
- `BatchActions` aliases/validation оновлено:
  `asset_contact_sheet`, `asset_multiview`, `multi_view`,
  `capture_contact_sheet` -> `UniBridge_CaptureAsset`
  `Action=CaptureContactSheet`.

Перевірка:

- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лише старий `System.Threading.Tasks.Extensions`
  warning;
- live Unity smoke виконано тільки через MCP relay для editor pid `55180`:
  - `UniBridge_ManageEditor Action=RefreshAssets WaitForCompletion=true`:
    editor ready;
  - `tools/list`: 59 tools, schema `UniBridge_CaptureAsset` містить
    `CaptureContactSheet`, `Views`, `SeriesCount`;
  - `UniBridge_ReadConsole Action=Clear`;
  - `UniBridge_ManageEditor Action=WaitIdle`: editor ready;
  - `UniBridge_CaptureAsset Action=CaptureContactSheet` для
    `Assets/Sprites/Item_0000.png`, `Views=[Front,Iso]`,
    `SeriesCount=2`, `CellWidth=160`, `CellHeight=160`, `Columns=2`:
    success, PNG `328x328`, `renderMode=ContactSheet`, `rendered=4`;
  - `UniBridge_BatchActions DryRun=true` з alias `asset_contact_sheet`
    і `Action=multi_view`: success, action normalized to
    `CaptureContactSheet`;
  - `CaptureDiff` self-check на отриманому asset contact sheet PNG:
    success, `changedPixels=0`;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`:
    15 logs, 0 warnings/errors/exceptions/asserts.

## 2026-05-14: UniBridge execution status diagnostics

Реалізовано п'ятий пункт scheduler/agent ergonomics polish: read-only
діагностика виконання MCP tools.

Зміни:

- `ToolExecutionScheduler` тепер веде lightweight telemetry:
  active operations, recent operations, pending reader/exclusive counters,
  wait/duration ms, timeout ms, outcome і error info;
- додано totals: started/completed/faulted/timedOut, а також limits для
  scheduler timeout/history;
- `BuildAnnotation` тепер підказує timeout parameters
  `ExecutionTimeoutMs` / `SchedulerTimeoutMs`;
- додано `UniBridge_ExecutionStatus` з `ExecutionPolicy=Observer`, щоб він
  міг дивитися scheduler state навіть коли інший tool тримає exclusive gate;
- actions:
  - `Snapshot` - active/pending/recent/totals/limits;
  - `Recent` - bounded recent execution history;
  - `Policies` - per-tool execution policy summary, включно з disabled tools
    при `IncludeDisabled=true`;
- `ToolGuide` orientation/editor-control workflow тепер радить
  `UniBridge_ExecutionStatus`, якщо tool здається queued/blocked/timeout.

Перевірка:

- змінені package-файли синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лишився тільки старий
  `System.Threading.Tasks.Extensions` warning;
- перший Unity compile pass показав помилку `CS0103: Response does not exist`
  у новому `ExecutionStatus.cs`; виправлено missing using
  `Cidonix.UniBridge.MCP.Editor.Helpers`, повторний build і MCP compile
  чисті;
- live Unity smoke виконано тільки через MCP relay:
  - `tools/list`: 59 tools, `UniBridge_ExecutionStatus` присутній;
  - tool annotation має `policy=Observer`, `exclusive=false`;
  - `ExecutionStatus Snapshot` повернув `activeOperations`, `pending`,
    `totals` і побачив власний active observer call;
  - після `UniBridge_UnitySearch` і dry-run `UniBridge_BatchActions`
    `ExecutionStatus Recent` показав обидва calls з `waitMs`/`durationMs`;
  - `ExecutionStatus Policies IncludeDisabled=true` показав
    `ExecutionStatus=Observer`, `CaptureView=Capture`,
    `ManageGameObject=Mutating`;
  - `ToolGuide Action=Tool Tool=UniBridge_ExecutionStatus` резолвить tool і
    пов'язує його з `editor_control`;
  - `ManageEditor GetCompilationDiagnostics`: 0 retained errors/warnings;
  - фінальний `UniBridge_ReadConsole DiagnosticSummary`: 0 warnings,
    0 errors, 0 exceptions, 0 asserts.

## 2026-05-14: SerializedProperty edge-case unification

Реалізовано четвертий пункт safety/ergonomics polish: `TypeSchema`,
`AssetSnapshotSerializer`, `ManageScriptableObject`, `ManageAssetImporter`,
`SceneObjectView` і `BehaviourContext` тепер використовують одну shared
мову значень для Unity `SerializedProperty`.

Зміни:

- `SerializedPropertyPatcher` навчився серіалізувати `Generic` arrays,
  fixed buffers і generic child objects компактними JSON payloads з
  inline `items` / `children`;
- додано write/read support для `Hash128`, а також structured fixed-buffer
  patching через `GetFixedBufferElementAtIndex`;
- exposed/object reference metadata у schemas тепер має
  `objectReferenceType`, а serialized entries мають `aliases`,
  `isFixedBuffer`, `fixedBufferSize` і `patchHint`;
- `patchHint` повертає `preferredPath`, accepted value shapes і sample value,
  щоб новому агенту не треба було вгадувати формат для patch call;
- `TypeSchema` value serialization тепер делегує в
  `SerializedPropertyPatcher`, тому read shapes збігаються з write shapes;
- `AssetSnapshotSerializer` більше не ковтає serializer edge failures як
  `null`: повертає structured error payload, якщо Unity property не вдалося
  прочитати;
- локальні serializer-и у `ManageScriptableObject`, `ManageAssetImporter`,
  `SceneObjectView` і `BehaviourContext` переведено на shared patcher, щоб
  LayerMask/Hash128/Curve/Gradient/arrays всюди виглядали однаково.

Перевірка:

- змінені package-файли синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лишився тільки старий
  `System.Threading.Tasks.Extensions` warning;
- live Unity smoke виконано тільки через MCP relay для editor pid `55180`;
- `tools/list`: 58 tools;
- `UniBridge_TypeSchema InspectGameObject` для `Main Camera/Camera` повернув
  serialized `patchHint`, `aliases` і `acceptedShapes`;
- через MCP створено тимчасовий ScriptableObject/MonoBehaviour smoke script
  з полями `LayerMask`, `AnimationCurve`, `Gradient`, `Hash128`, `RectInt`,
  `BoundsInt`, `char`, `int[]`;
- `UniBridge_ManageScriptableObject CreateOrUpdate` успішно пропатчив усі
  edge fields і прочитав назад `Hash128`, named `LayerMask` та array items;
- `UniBridge_TypeSchema InspectAsset`, `UniBridge_AssetIntelligence Serialize`,
  `UniBridge_SceneObjectView View` і `UniBridge_BehaviourContext` прочитали
  ті самі values без `SerializedProperty value serialization failed`;
- temp scene object, asset і script folder очищено через MCP;
- фінальний `UniBridge_ReadConsole DiagnosticSummary`: 0 warnings,
  0 errors, 0 exceptions, 0 asserts.

## 2026-05-14: editor deltas, schema examples, serializer profiles, and capture readback polish

Після додаткового порівняння з reference-плагіном реалізовано 8
практичних покращень для нових агентів:

- `UniBridge_EditorEvents` став значно багатшим:
  selection payload тепер містить object/gameObject summaries, hierarchy
  path, indexAtPath, scenePath/sceneGuid, asset path/GUID, active scene,
  assetGUIDs; додано `IncludeDiagnostics` і `IncludeAssetChanges`;
- додано AssetPostprocessor-backed asset deltas:
  imported/deleted/moved records з oldPath/path/GUID, history id і hint
  refreshити `UniBridge_AssetIntelligence ReferenceGraph` після asset moves;
- compilation/package diagnostics тепер збираються без obsolete Unity 6 API:
  `assemblyCompilationFinished` дає retained compiler diagnostics
  file/line/column/severity/assemblyPath, а package registration events
  читаються reflection-safe через доступні поля конкретної Unity версії;
- `UniBridge_ManageEditor` отримав `GetCompilationDiagnostics`, що
  повертає retained compiler diagnostics через той самий event history;
- `UniBridge_TypeSchema` отримав `Action=PatchExamples`,
  `IncludePatchExamples` і `ExampleLimit`: для live GameObject component,
  ScriptableObject asset або AssetImporter він повертає ready-to-call
  payloads для відповідного `Manage*` tool; serialized property entries
  тепер мають `managedReference` / `exposedReference` metadata;
- `SerializedPropertyPatcher` підтримує `ExposedReference` і безпечний
  мінімум для `ManagedReference`: set-null, set concrete assignable type,
  optional nested property patching після створення managed reference
  instance; помилки повертаються явно, без guesswork;
- `AssetSnapshotSerializer` отримав compact smart profiles для noisy assets
  і components: AnimationClip, Material, Texture/TextureImporter, Sprite,
  AudioClip, ParticleSystem, AnimatorController hints; Generic/array
  properties тепер серіалізуються маленьким inline зрізом дітей/елементів
  замість тільки `{ arraySize }`;
- `UniBridge_CaptureView` і prefab/model branch `UniBridge_CaptureAsset`
  отримали `ReadbackMode=Immediate|GpuReadback`: `GpuReadback` використовує
  synchronous `AsyncGPUReadback` з `ReadPixels` fallback і повертає фактичний
  `readbackMode` у metadata;
- `UniBridge_BatchActions` impact block тепер містить per-step plans:
  tool/action, likely asset paths, project settings, scene object references,
  validationProvider name і rollbackHint; це полегшує review dry-run
  новими агентами.

Оновлено `ToolGuide`, `DomainCatalog`, `BatchActionToolCatalog`:

- додано workflow `editor_events`;
- orientation/editor/asset/script workflows тепер радять `EditorEvents`
  для deltas і compiler diagnostics;
- capture workflow згадує `ReadbackMode=GpuReadback`;
- batch workflow згадує per-step impact plans;
- додано batch aliases `editor_events`, `asset_events`,
  `selection_events`, `compile_events`, `patch_examples`,
  `property_examples`.

Unity/Test project:

- користувач оновив тестовий проєкт до Unity `6000.4.7f1`;
- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лишився тільки старий `System.Threading.Tasks.Extensions`
  warning;
- під час build прибрано Unity 6 obsolete warning:
  `CompilationPipeline.assemblyCompilationStarted` більше не використовується;
- live smoke виконано тільки через MCP relay
  `unibridge_relay_win.exe --mcp --project-id ae4e323353aa487f9ea74d566b38eaac`;
- MCP smoke summary:
  57 tools, `EditorEvents` schema має `IncludeDiagnostics` /
  `IncludeAssetChanges`, `CaptureView` schema має `ReadbackMode`;
  `EditorEvents Snapshot` повернув 8 event records, latestId 11,
  4 asset change records, diagnostics block;
  `TypeSchema PatchExamples` для `Main Camera/Camera` повернув 3 examples;
  `BatchActions DryRun IncludeImpact=true` повернув 1 per-step plan з
  validationProvider;
  `CaptureView CaptureSceneView ReadbackMode=GpuReadback` успішний і
  metadata повернула `readbackMode=GpuReadback`;
  `AssetIntelligence Serialize` для `Assets/Sprites/Item_0000.png` повернув
  serialized data з `profile`;
  фінальний `UniBridge_ReadConsole Overview`: 28 logs, 0 warnings,
  0 errors, 0 exceptions, 0 asserts.

## 2026-05-14: six Unity workflow parity/ergonomics improvements

Реалізовано наступні 6 практичних покращень для майбутніх агентів:

- `UniBridge_BatchActions` отримав `IncludeImpact` / impact block:
  dry-run і real batch тепер показують likely touched tools/actions,
  Assets/Packages paths, loaded scenes, ProjectSettings hints і scene object
  references; impact scanner ігнорує input binding strings типу
  `<Keyboard>/space`, щоб не кидати `Illegal characters in path`;
- `SceneObjectLocator` і `UniBridge_SceneObjectView` отримали
  `IncludeDontDestroyOnLoad`: у play mode runtime objects із hidden
  `DontDestroyOnLoad` scene тепер беруться в search/view/query pool;
- `UniBridge_ManageInputActions` розширено з `.inputactions`/`PlayerInput`
  до scene wiring helpers: `WirePlayerInputManager`, `WireUIInputModule`,
  `AddOnScreenButton`, `AddOnScreenStick`, `AddVirtualMouse`,
  `AddMultiplayerEventSystem`; Input System types лишились optional через
  reflection;
- додано `UniBridge_ManageConstraints`: `ParentConstraint`,
  `PositionConstraint`, `RotationConstraint`, `ScaleConstraint`,
  `AimConstraint`, `LookAtConstraint`, structured `Sources`, `Weight`,
  `ConstraintActive`, `Locked`, best-effort `MaintainOffset`;
- `UniBridge_ManageRendering` отримав 2D rendering helpers:
  `Setup2DScene`, `AddPixelPerfectCamera`, `AddLight2D`,
  `AddShadowCaster2D`, `AddSpriteShapeRenderer`, з optional package checks;
- `UniBridge_ScriptIntelligence` / asset script summaries стали більш
  interface-oriented: brace-depth фільтр відсікає local method-body matches,
  `sourceShape.interfaceSummary` дає compact public/inspector/callback API
  summary, а `AssetSnapshotSerializer` додає fields/properties/interfaces.

Додатково оновлено `BatchActionToolCatalog`, `DomainCatalog` і `ToolGuide`,
щоб новий агент швидко знаходив constraints, input scene wiring і 2D
rendering helpers.

Перевірка:

- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лишився тільки старий `System.Threading.Tasks.Extensions`
  warning;
- live smoke виконано тільки через MCP relay
  `unibridge_relay_win.exe --mcp --project-id ae4e323353aa487f9ea74d566b38eaac`;
- `tools/list`: 57 tools, required tools присутні, включно з
  `UniBridge_ManageConstraints`, а `UniBridge_BatchActions` schema має
  `IncludeImpact`;
- `BatchActions` dry-run з input binding `<Keyboard>/space` успішний:
  impact показав 4 asset candidates, 1 loaded scene і
  `UniBridge_ManageConstraints` у tools impact;
- scene authoring smoke: створено source/target GameObjects, додано
  `ParentConstraint` із 1 source, створено `.inputactions`, застосовано
  `Setup2DScene`, перевірено `SceneObjectView IncludeDontDestroyOnLoad`,
  `ScriptIntelligence Analyze` повернув `sourceShape.interfaceSummary`;
- cleanup temp GameObjects і `Assets/UniBridgeSmoke` виконано через MCP;
- після `ReadConsole Clear` і session marker фінальний
  `UniBridge_ReadConsole Overview`: 28 logs, 0 warnings, 0 errors,
  0 exceptions.

## 2026-05-14: scene ergonomics, masks, UI controls, audio/VFX authoring

Після чергового reference-audit користувач попросив реалізувати ще шість
практичних покращень, які зменшують ручну оркестрацію для майбутніх агентів:

- `UniBridge_ManageGameObject` отримав `StaticEditorFlags` і hierarchy
  placement controls: `Sibling`, `Placement`, `SiblingIndex`,
  `WorldTransformStays`; підтримуються PascalCase і snake_case aliases;
- `SerializedPropertyPatcher` і `SceneObjectView` отримали name-friendly
  `LayerMask` / `renderingLayerMask` handling: `Everything`, `Nothing`,
  single/array layer names і readable serialized values;
- asset-mutating tools отримали VCS editable preflight/checkout через
  `VersionControlUtility`: `ManageAsset`, `ManageMaterial`,
  `ManageScriptableObject`, `ManageAssetImporter`, а TagManager mutations у
  `ManageEditor` тепер теж перевіряють `ProjectSettings/TagManager.asset`;
- `UniBridge_SceneObjectView` отримав `Action=Query` з AND filters:
  `NameContains`/`ExactName`, `ComponentType`, `Tag`, `Layer`, `Offset`;
- `UniBridge_ManageUI` отримав high-level uGUI/TMP controls:
  `Toggle`, `Slider`, `Dropdown`, `InputField`, `Scrollbar`,
  `TextMeshProInputField`, `TextMeshProDropdown`, `ToggleGroup`;
- додано `UniBridge_ManageAudio` і `UniBridge_ManageVFX` для scene authoring:
  `AudioSource`, listener, reverb/filter presets, `ParticleSystem`,
  `TrailRenderer`, `LineRenderer`, optional `VisualEffect` і `VideoPlayer`
  через reflection.

Додатково нові audio/VFX tools додано до `BatchActionToolCatalog` і
`DomainCatalog`. Під час live smoke знайдено Unity fake-null bug у нових
audio/VFX helpers: `??` не можна використовувати для component acquisition,
бо missing компоненти можуть поводитись як fake-null wrappers. Виправлено
через явний `GetOrAddComponent`.

Перевірка:

- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  успішний: 0 errors, лишився тільки старий `System.Threading.Tasks.Extensions`
  assembly conflict warning;
- Unity refresh виконано через MCP `UniBridge_ManageEditor RefreshAssets`,
  після domain reload `WaitForReady` показав `isReady=true`,
  `isCompiling=false`, `isUpdating=false`;
- full live smoke через
  `unibridge_relay_win.exe --mcp --project-id ae4e323353aa487f9ea74d566b38eaac`
  виконав 49 MCP `tools/call`:
  - `tools/list`: 56 tools, required tools присутні;
  - створено/оновлено temp material asset, `ManageAssetImporter Reimport`,
    `AddTag`/`RemoveTag`, потім temp asset folder видалено;
  - створено scene root, static child із `BatchingStatic/OccluderStatic`,
    sibling reorder `C before B`, і перевірено `SceneObjectView View`;
  - пропатчено `Camera.m_CullingMask=Everything` і
    `SpriteRenderer.m_RenderingLayerMask=["Default"]`;
  - `SceneObjectView Query` з AND filters знайшов 2 sprite renderers, а
    `Offset=1 MaxObjects=1` повернув 1;
  - створено Canvas і всі нові UI controls, включно з TMP input/dropdown;
  - створено Audio rig (`AmbientLoop3D`, `LowPass`) і VFX rig
    (`SimpleBurst`, Trail, Line), обидва inspected;
  - cleanup scene root, UI canvas і temp asset folder виконано через MCP;
  - фінальний `UniBridge_ReadConsole DiagnosticSummary` після marker:
    94 logs, 0 warnings/errors/exceptions/asserts.

## 2026-05-14: Domain catalog, Physics3D, Navigation, Rendering, UI Toolkit authoring

Після повторного reference-audit користувач попросив реалізувати шість
пунктів, які реально покращують роботу майбутніх агентів з Unity:

- `UniBridge_DomainCatalog`: read-only agent-facing каталог доменів
  (`Physics3D`, `Navigation`, `Rendering`, `UIToolkit`, `UI`, `Assets`,
  `Scripts`, etc.) з first/edit/verify tool sequences, batch aliases і
  curated type hints;
- `UniBridge_ManagePhysics3D`: authoring presets/tools для `Rigidbody`,
  `Collider`, `Joint`, `CharacterController`, `PhysicsMaterial`;
- `UniBridge_ManageNavigation`: `NavMeshAgent`, `NavMeshObstacle`,
  `OffMeshLink`, а також optional AI Navigation `NavMeshSurface/Modifier`
  через reflection;
- `UniBridge_ManageRendering`: camera/light/render settings/Volume tools,
  2D/isometric/3D camera presets і preview/three-point lighting rigs;
- `UniBridge_ManageUIToolkit`: UXML/USS/PanelSettings authoring,
  UIDocument wiring, add element, class і inline style patching;
- generic `SerializedPropertyPatcher`, `TypeSchema` і
  `AssetSnapshotSerializer` тепер підтримують JSON-safe `AnimationCurve` і
  `Gradient`.

Додатково:

- нові tools додано в `BatchActionToolCatalog` allowlist/aliases;
- `BatchActions.Validation` і `BatchActions.Steps` отримали нормалізацію та
  basic validation для нових tools;
- `ToolGuide` отримав workflows `physics3d`, `navigation`, `rendering`,
  `uitoolkit`, а orientation loop тепер пропонує `DomainCatalog`;
- `Documentation~/unibridge.md` і `package.json` оновлено;
- package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`.

Live MCP validation у `H:/Repos/UnityRepos/UniBridge_Test_Project` завершено:

- embedded package синхронізовано з source package;
- Unity 6 auto-upgrade змінив 3D physics material API з legacy
  `PhysicMaterial` на `PhysicsMaterial`; це перенесено назад у source;
- `tools/list`: 54 tools, усі нові tools присутні;
- після `RefreshAssets` Unity іноді закривала relay connection під час
  domain reload; після reconnect editor ready, `IsCompiling=false`,
  `IsUpdating=false`;
- фінальний smoke `UB_Smoke_1778754921723`:
  - `UniBridge_DomainCatalog` успішно inspected `Rendering` і listed
    `Physics3D` types;
  - `UniBridge_ManagePhysics3D` створив `PhysicsMaterial` з
    `dynamicFriction=0.12`, `staticFriction=0.1`, `bounciness=0.82` і
    застосував `BouncyDynamic` preset з `Rigidbody + SphereCollider`;
  - `UniBridge_ManageNavigation` створив `NavMeshAgent`, окремий
    `NavMeshObstacle`, а також guard-блокування поганої комбінації
    `NavMeshAgent + NavMeshObstacle` на одному GameObject без Unity warning;
  - `UniBridge_ManageRendering` створив orthographic camera, directional
    light і scene lighting; decimal/color JSON values застосувались точно;
  - `UniBridge_ManageUIToolkit` створив USS/UXML/PanelSettings, додав
    `smoke-badge` label і прив'язав `UIDocument`;
  - фінальна `ReadConsole DiagnosticSummary`: 69 log entries,
    0 warnings/errors/exceptions/asserts.
- окремий curve/gradient smoke:
  - через MCP створено `UBCurveGradientSmoke1778755097081.cs`;
  - `UniBridge_ManageGameObject SetComponentProperty` пропатчив
    `AnimationCurve` і `Gradient`;
  - `UniBridge_TypeSchema IncludeValues=true` прочитав назад curve з 3 keys
    (`value=0.8` на середньому key) і gradient з `alpha=0.3`,
    другим color key `g=0.4`;
  - фінальна console summary: 0 warnings/errors/exceptions/asserts.

Після smoke додано culture-safe JSON number parsing у нових authoring tools
і в `SerializedPropertyPatcher`, щоб дробові значення не ламались на
локалях із десятковою комою.

## 2026-05-14: Scope-aware gameplay authoring six-pack

Після уточнення користувача реалізовано саме шість практичних пунктів із
останнього reference-audit списку, без package-facing згадок старої назви
reference source:

- `UniBridge_ScopedEdit`: відкриває `.unity` або `.prefab` scope, запускає
  bounded `UniBridge_BatchActions` всередині, optionally saves scope і
  restores previous editor state/selection/prefab stage;
- `UniBridge_BehaviourContext`: для target/selection GameObject повертає
  attached MonoBehaviour list, missing scripts, script asset path/GUID,
  bounded source text і JSON-safe serialized fields;
- `UniBridge_ManageTilemap2D`: створює `Grid`, Tilemap layers, Tile assets
  from sprites, paint/erase cells, inspect cells/bounds, add collider/composite
  collider setup;
- `UniBridge_ManageInputActions`: authoring `.inputactions` JSON assets,
  action maps/actions/bindings/control schemes і optional `PlayerInput`
  wiring через reflection;
- `UniBridge_ManageTimeline`: reflection-based TimelineAsset authoring,
  tracks/default clips, PlayableDirector creation і track binding;
- `UniBridge_ManagePhysics2D`: PhysicsMaterial2D assets і presets/tools для
  Rigidbody2D, Collider2D, Joint2D, Effector2D.

Додатково:

- нові tools додано до `BatchActionToolCatalog` aliases/allowlist;
- `BatchActions.Validation` отримав domain validation для нових tools і
  блокує nested `UniBridge_ScopedEdit` всередині batch;
- `ToolGuide` отримав workflows `scoped_editing`, `behaviour_context`,
  `tilemap2d`, `input_actions`, `timeline`, `physics2d`;
- `Documentation~/unibridge.md` оновлено для нових agent-facing workflows;
- `package.json` dependencies/changelog оновлено для director/physics2d/tilemap
  modules і нових інструментів;
- runtime фікси після MCP smoke: не використовувати C# `??` для Unity
  components через fake-null semantics; не повертати сирі Unity structs
  (`Vector3`, `Bounds`, `Color`, etc.) у JSON responses.

Живий MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з source repo;
- після `RefreshAssets`/domain reload: `tools/list` показав 49 tools і всі
  шість нових tools зареєстровані;
- фінальний smoke stamp `20260514_114100`:
  - `UniBridge_ScopedEdit` dry-run + executing scoped scene edit створив
    `UniBridgeSixPackRoot_20260514_114100` у `Assets/Scenes/SampleScene.unity`;
  - `UniBridge_BehaviourContext` прочитав
    `Cidonix.UniBridge.TestFixtures.UniBridgeFixtureMover`, source snippet і
    serialized fields без JSON self-reference loops;
  - `UniBridge_ManageTilemap2D` створив Grid/layer, Tile asset from
    `Assets/Sprites/Item_0000.png`, painted 3 cells і inspected occupied cells;
  - `UniBridge_ManageInputActions` створив
    `Assets/UniBridgeSmoke/SixPack/20260514_114100/SmokeControls.inputactions`
    з map `Player`, 2 actions, 3 bindings, 1 control scheme, і wired
    `PlayerInput`;
  - `UniBridge_ManageTimeline` створив Timeline asset, `ActivationTrack`,
    default clip, PlayableDirector і binding;
  - `UniBridge_ManagePhysics2D` створив PhysicsMaterial2D і застосував
    `BouncyDynamic` preset з `CircleCollider2D`;
  - `UniBridge_ManageEditor SaveAll` зберіг scene/assets;
  - фінальний `UniBridge_ReadConsole DiagnosticSummary`: 52 logs,
    0 warnings/errors/exceptions/asserts.

## 2026-05-14: Package-facing reference-name cleanup

Після зауваження користувача прибрано прямі згадки старої назви reference
source з package-facing файлів і контексту репозиторію. Очищено `package.json`,
документацію, MCP tool descriptions/comments, parameter descriptions і цей
контекстний файл; формулювання залишені як generic `reference implementation`
або `reference-shaped`, без прив'язки до старої назви.

Також виправлено identifier у `ManageScriptableObject`, який міг зламатися від
механічного перейменування, і перевірено `rg` по старій назві: 0 збігів.

## 2026-05-13: reference implementation parity pass - Animator, profiles, native search, checkpoint reload

Користувач попросив реалізувати ще 4 корисні пункти з reference implementation, які були
знайдені під час повторного аудиту. Перед змінами переглянуто reference implementation:

- `reference implementation source/Sidekick/CreateOrUpdateAnimatorControllerAction.cs`;
- `reference implementation source/Sidekick/SearchAssetsByQuery.cs`;
- `reference implementation source/Sidekick/ReloadFromCheckpoint.cs`;
- serializer coverage у reference implementation для navigation/input/tilemap/lighting/video-
  timeline доменів.

У UniBridge перенесено ідеї, але в MCP-native архітектуру:

- `UniBridge_ManageAnimatorController` отримав nested state machines,
  `add_state_machine` / `remove_state_machine`, Entry transitions,
  destination-state-machine transitions, ширші layer settings
  (`avatar_mask`, `ik_pass`, synced-layer fields), state parameter-driven
  fields і transition fields (`has_fixed_duration`,
  `interruption_source`, `ordered_interruption`,
  `can_transition_to_self`, `mute`, `solo`);
- `apply_graph` тепер рекурсивно створює/оновлює nested state machines,
  entry transitions, state/Any State/Exit transitions і transitions на
  destination state machine; preflight дозволяє Entry transition без
  conditions, як і має бути;
- `UniBridge_SceneObjectView` отримав профілі `Navigation`, `Input`,
  `Tilemap2D`, `Lighting`, `VideoTimeline` і reflection-based summaries для
  optional Unity packages без жорстких asmdef dependencies;
- `UniBridge_UnitySearch` отримав backend `NativeSearchService` на
  `UnityEditor.Search` providers `asset`/`scene`, а також `Hybrid`
  backend, що комбінує native search і UniBridge deterministic scan;
- `UniBridge_ManageEditor` отримав `ReloadCheckpoint`: reference-shaped refresh,
  detection of modified loaded scenes/prefab stage, optional save of
  unmodified dirty scenes, close/reopen prefab stage, reopen loaded scenes,
  repaint, and wait-ready support;
- `BatchActions` normalizer/validator оновлено для нових Animator,
  SceneObjectView, UnitySearch і ManageEditor параметрів.

Технічна перевірка:

- source compile-check через Unity-generated
  `Cidonix.UniBridge.MCP.Editor.rsp` з source package завершився без
  warnings/errors;
- package синхронізовано у
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`
  через non-destructive `robocopy /E`;
- після `RefreshAssets` через MCP був expected pipe close під час domain
  reload, після reconnect `tools/list` показав 40 tools і нові schema поля:
  `state_machine`, `destination_state_machine`, `entry`,
  `Backend`, `NativeTimeoutMs`, `Profile`, `ModifiedAssetPaths`;
- перший functional smoke знайшов bug: graph preflight помилково вимагав
  condition/exit time для Entry transition; виправлено в
  `ManageAnimatorController.cs`;
- фінальний live MCP smoke через
  `unibridge_relay_win.exe --mcp --project-id ae4e323353aa487f9ea74d566b38eaac`
  успішний:
  - `apply_graph dry_run` і реальний `apply_graph` створили controller
    `Assets/UniBridgeSmoke/ReferenceParityNested_20260513.controller`;
  - створено nested state machine `Locomotion`, Entry -> `Idle`,
    `Intro -> Locomotion` destination-state-machine transition і
    `Idle -> Move` з condition;
  - `validate` повернув `valid=true`, `issueCount=0`;
  - `inspect` підтвердив layer `ikPass=true`, nested state machine,
    Entry transition і destination-state-machine transition;
  - `UnitySearch Backend=NativeSearchService` знайшов `Main Camera`;
  - `UnitySearch Backend=Hybrid` знайшов створений controller asset;
  - через MCP створено тимчасовий `EventSystem` +
    `StandaloneInputModule`, `SceneObjectView Profile=Input` повернув
    `eventSystem` і `inputModule` summaries, потім об'єкт видалено;
  - `ManageEditor ReloadCheckpoint` на modified controller path успішно
    refresh/repaint/wait-ready без reopen scenes;
  - cleanup через MCP видалив тимчасовий controller asset;
  - фінальна `UniBridge_ReadConsole DiagnosticSummary`: 28 log entries,
    0 warnings/errors/exceptions/asserts.

## 2026-05-13: reference-informed editor control / selection / readiness

Після коміту першої трійки (`cs:103`) почато другу трійку reference-informed
покращень. Перед змінами повторно переглянуто reference implementation:

- `reference implementation source/Sidekick/SelectAsset.cs`;
- `reference implementation source/Sidekick/SelectGameObject.cs`;
- `reference implementation source/Sidekick/SelectionContext.cs`;
- `reference implementation source/Sidekick/GetPlayModeState.cs`;
- `reference implementation source/Sidekick/GetStatus.cs`;
- `reference implementation source/Sidekick/RequestScriptCompilation.cs`;
- `reference implementation source/Sidekick/SaveAssets.cs`;
- `reference implementation source/Sidekick/GenerateSolutionFile.cs`.

Висновок: у reference implementation сильна не складність реалізації, а прямі editor-control
операції, які агенту не треба збирати з menu item workarounds. У UniBridge
це перенесено в `UniBridge_ManageEditor`, без створення окремого tool:

- додано actions `SelectAsset`, `SelectGameObject`, `ClearSelection`,
  `PingSelection`, `FrameSelection`;
- додано readiness/project maintenance actions `WaitForReady`,
  `RefreshAssets`, `RequestScriptCompilation`, `SaveAll`,
  `GenerateSolutionFiles`;
- `GetState` тепер повертає `IsPlayingOrWillChangePlaymode` і `IsReady`;
- `GetSelection` повертає багатший selection context:
  asset/scene path, GUID, persistent flag, scene path, hierarchy path,
  active-in-hierarchy для GameObject;
- `SelectGameObject` підтримує `GameObjectPath`, `Target` і `InstanceID`;
- прямий obsolete Unity 6 виклик `EditorUtility.InstanceIDToObject` замінено
  reflection fallback через `EntityIdToObject`/legacy method, щоб не мати
  compiler warning;
- `BatchActions` normalizer/validator отримав aliases для нових editor
  actions (`editor`, `selection`, `ready`, `compile`, `save_all` тощо);
- `ToolGuide` отримав workflow `editor_control`;
- `Documentation~/unibridge.md` доповнено розділом `Control Editor State`.

Live MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- змінені package-файли перенесено в embedded package без mirror/delete;
- `Assets/Refresh` через `UniBridge_ManageMenuItem` пройшов;
- після першої компіляції Console показала 1 warning про obsolete
  `EditorUtility.InstanceIDToObject`; warning виправлено;
- повторний refresh через MCP спричинив expected pipe close під час domain
  reload, після reconnect тести продовжено;
- `tools/list` бачить 40 tools, а `UniBridge_ManageEditor` schema містить
  нові actions;
- `UniBridge_ManageEditor WaitForReady` повернув ready:
  `isReady=true`, `isCompiling=false`, `isUpdating=false`,
  `isPlaying=false`;
- `SelectAsset AssetPath=Assets/Sprites/Item_0000.png` успішно вибрав asset;
- через MCP створено тимчасовий `UB_EditorSelectionProbe2`,
  `SelectGameObject` успішно вибрав його за path/name, потім об'єкт видалено;
- `FrameSelection` успішний на тимчасовому об'єкті;
- `SaveAll SaveScenes=false SaveAssets=true` успішний;
- `UniBridge_BatchActions` dry-run з alias `editor` і action
  `wait_for_ready` успішний: 1 validated, 0 validation errors/warnings;
- `GenerateSolutionFiles` успішно викликав
  `UnityEditor.SyncVS.SyncSolution`;
- `RequestScriptCompilation` успішно запросив компіляцію, після reconnect
  `WaitForReady` підтвердив завершення;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 25 log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-13: reference-informed project context, asset reference graph, fuzzy recovery

Користувач попросив реалізувати перші три залишкові reference-informed
покращення перед переходом до наступних трьох. Перед змінами повторно
переглянуто reference implementation:

- `reference implementation source/Sidekick/EditorContext.cs`;
- `reference implementation source/Sidekick/GetProjectRoots.cs`;
- `reference implementation source/Sidekick/AssetReferenceManager.cs`;
- `reference implementation source/Sidekick/ReadAssets.cs`;
- `reference implementation source/Sidekick/FuzzySearch.cs`.

З reference implementation перенесено ідеї, але в UniBridge-native архітектуру:

- `UniBridge_ContextSnapshot` тепер може повертати:
  - project roots (`Assets`, `ProjectSettings`, `Packages`) і registered
    package roots;
  - package dependency overview з `Packages/packages-lock.json` або
    `Packages/manifest.json`;
  - Unity project environment: render pipeline, Built-in/URP/HDRP/custom
    classification, 2D/3D default behavior, tags, layers, sorting layers;
  - нові параметри `IncludeProjectRoots`, `IncludeProjectSettings`,
    `IncludePackageDependencies`;
- `UniBridge_AssetIntelligence` розширено діями:
  - `ReferenceGraph` - будує/читає кешований dependency graph,
    повертає dependencies, dependents, top referenced assets і optional
    edge samples;
  - `Impact` - оцінює ризик перед `Modify`, `Move`, `Rename`, `Delete`,
    `Reimport`, показує dependents/dependencies і guidance;
  - `ResolveMissing` - fuzzy recovery для stale/mistyped asset path;
- `Dependents` може використовувати `UseReferenceIndex=true`, а error
  responses для `Inspect`, `Read`, `Dependencies`, `Dependents`, `Preview`,
  `Serialize`/`Snapshot`, `Impact` тепер можуть включати fuzzy suggestions;
- додано параметри `RefreshReferenceIndex`, `UseReferenceIndex`,
  `IncludeReferenceEdges`, `MaxReferenceEdges`, `ImpactOperation`,
  `SuggestSimilar`, `MaxSuggestions`;
- `ToolGuide` і `Documentation~/unibridge.md` оновлено: новий агент має
  бачити ці можливості в orientation/search/assets workflows.

Live MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з source package;
- `Assets/Refresh` через `UniBridge_ManageMenuItem` пройшов;
- `tools/list` після refresh бачить 40 tools;
- перед фінальними перевірками Unity Console очищено через
  `UniBridge_ReadConsole Clear`;
- `UniBridge_ContextSnapshot Depth=Brief IncludeProjectRoots=true
  IncludeProjectSettings=true IncludePackageDependencies=true` успішний:
  - `project=UniBridge_Test_Project`;
  - `rootCount=4`, `ProjectSettings.exists=true`;
  - `registeredPackageCount=31`;
  - render pipeline `URP`, default mode `2D`;
  - `tags=7`, `layers=5`, `sortingLayers=1`;
  - package dependency count `67`;
- `UniBridge_AssetIntelligence ReferenceGraph` для
  `Assets/Sprites/Item_0000.png` успішний:
  - indexed assets `68`, graph edges `56`;
  - dependency count `0`, dependent count `2`;
  - returned edge samples з `Assets/Scenes/SampleScene.unity` і
    `Assets/UniBridgeMaterialSmoke/StandardMaterialSmoke.mat`;
- `UniBridge_AssetIntelligence Dependents UseReferenceIndex=true` повернув
  2 dependent assets;
- `UniBridge_AssetIntelligence Impact Path=Assets/Sprites/Item_0000.png
  ImpactOperation=Rename` повернув `risk=Medium`;
- `UniBridge_AssetIntelligence ResolveMissing` для
  `Assets/Sprites/Item_0000_mistyped.png` повернув 5 fuzzy suggestions,
  перший кандидат `Assets/Sprites/Item_0000.png`;
- звичайний `Inspect` для mistyped path повертає error із 3 fuzzy
  suggestions, перший кандидат `Assets/Sprites/Item_0000.png`;
- `UniBridge_ToolGuide Workflow assets_import` показує новий first call
  `UniBridge_AssetIntelligence Inspect or ReferenceGraph` і note про
  `Impact`/`ReferenceGraph` перед move/rename/delete;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 16 log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-13: Agent API cleanup, BatchActions split, smoke recipes

Закрито три наступні agent-facing пункти:

- `UniBridge_ManageGameObject` отримав PascalCase canonical API для нових
  агентів:
  - `Action`, `Target`, `SearchMethod`, `Name`, `Parent`, `Position`,
    `Rotation`, `Scale`;
  - `ComponentsToAdd`, `ComponentsToRemove`, `ComponentName`,
    `ComponentProperties`, `Properties`;
  - `SearchTerm`, `FindAll`, `SearchInactive`,
    `IncludeNonPublicSerialized`;
- legacy snake_case/camelCase aliases лишаються прийнятими через
  `NormalizeInput`, але schema/docs тепер показують PascalCase як основний
  шлях;
- `UniBridge_BatchActions` розбито на partial-файли:
  - `BatchActions.cs` - MCP schema, handler, execution loop;
  - `BatchActions.Validation.cs` - per-tool validators;
  - `BatchActions.ValidationHelpers.cs` - shared validation helpers;
  - `BatchActions.Steps.cs` - parsing, aliases, step normalization;
  - `BatchActions.Transaction.cs` - undo/file rollback transaction;
  - `BatchActions.Models.cs` - internal option/report models;
- `BatchActionToolCatalog` тепер дозволяє `UniBridge_ReadConsole` у batches
  з aliases `console`, `read_console`, `logs`, `diagnostics`;
- `UniBridge_WorkflowRecipes` отримав self-test recipes:
  - `RunCoreSmokeTest` - ToolGuide + console diagnostic + temporary
    GameObject create/inspect/delete;
  - `RunUISmokeTest` - temporary Canvas/template + UI audit + cleanup;
  - `RunAssetSmokeTest` - texture importer inspect + small asset contact
    sheet;
- `ToolGuide` і `Documentation~/unibridge.md` оновлено під нові recipes,
  PascalCase `ManageGameObject` API і batch `console` alias.

Перед змінами знову переглянуто reference implementation:

- `reference implementation source/Actions/CreateGameObjectProps.cs`;
- `reference implementation source/Actions/UpdateGameObjectProps.cs`;
- `reference implementation source/Actions/AddOrUpdateComponentProps.cs`;
- `reference implementation source/Sidekick/ActionExecutor.cs`;
- `reference implementation source/Sidekick/GameObjectPathQuery.cs`;
- `reference implementation source/Sidekick/ComponentQuery.cs`;
- `reference implementation source/Sidekick/ComponentProps.cs`.

Висновок: корисна ідея reference implementation тут не в копіюванні назв, а в тому, щоб мати
чіткі action props, resolve/validate до execution і інспектований batch
план. У UniBridge це збережено через `BatchActions` + recipes.

Live MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з source package;
- `Assets/Refresh` через `UniBridge_ManageMenuItem` пройшов;
- після компіляції `tools/list` бачить 40 tools;
- `UniBridge_ReadConsole DiagnosticSummary` після refresh: 15 log entries,
  0 warnings/errors/exceptions/asserts;
- direct PascalCase `ManageGameObject` smoke:
  - `Action=Create Name=UB_PascalProbe_* ComponentsToAdd=[{TypeName=BoxCollider}]`;
  - `Action=GetComponent ComponentName=BoxCollider`;
  - `Action=Delete`;
- `UniBridge_BatchActions` dry-run з aliases `console` і `game_object`
  пройшов validation, `allowedTools` містить `UniBridge_ReadConsole`;
- `WorkflowRecipes List` повернув 9 recipes;
- `BuildBatch` і `DryRun` для `RunCoreSmokeTest` успішні;
- `Execute RunCoreSmokeTest` успішний: 6/6 steps executed, 0 failures;
- `Execute RunUISmokeTest` успішний: 4/4 steps executed, 0 failures;
- `Execute RunAssetSmokeTest Folder=Assets/Sprites MaxAssets=2` успішний:
  2/2 steps executed, 0 failures;
- exact cleanup check через `ManageGameObject Find ByName` підтвердив 0
  залишків `UB_CoreSmokeLive` і `UB_UISmokeLive`;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 57 log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-13: ManageUI split and WorkflowRecipes

Після agent-facing refactor закрито наступні три пункти чистки:

- `ManageUI.cs` розбито на partial-файли за зонами відповідальності:
  - `ManageUI.Audit.cs` - `Audit`, `RepairPlan`, `AutoFix`, UI validation
    internals;
  - `ManageUI.Layout.cs` - layout preset helpers, anchors/pivots,
    layout groups/elements;
  - `ManageUI.Templates.cs` - high-level UI templates (`Panel`, `Modal`,
    `Toolbar`, `List`, `CardGrid`, `HUD`);
- з `ManageAssetImporter.cs` і `ManageScriptableObject.cs` прибрано старі
  локальні serialized-property setter-и; обидва шляхи тепер ідуть через
  shared `SerializedPropertyPatcher` плюс reflection fallback;
- додано `UniBridge_WorkflowRecipes` і
  `Tools/Parameters/WorkflowRecipesParams.cs`.

Перед реалізацією повторно переглянуто reference implementation:

- `reference implementation source/Sidekick/ExecuteActions.cs`;
- `reference implementation source/Sidekick/ResolveAndValidateActions.cs`;
- `reference implementation source/Sidekick/ActionExecutor.cs`;
- `reference implementation source/Actions/SetRectTransformLayoutAction.cs`;
- `reference implementation source/Actions/CreateGameObjectAction.cs`;
- `reference implementation source/Actions/UpdateGameObjectAction.cs`;
- `reference implementation source/Actions/CreateOrUpdateAssetAction.cs`;
- `reference implementation source/Sidekick/JSONButton.cs`.

Головна ідея з reference implementation: workflow має бути читабельним списком дій із
validate-before-execute і rollback/undo навколо execution. У UniBridge це
портовано як recipes поверх уже наявного `UniBridge_BatchActions`.

`UniBridge_WorkflowRecipes`:

- `Action=List`, `Describe`, `BuildBatch`, `DryRun`, `Execute`;
- recipes:
  - `CreateInventoryScreen`;
  - `ImportSpriteFolderAs2D`;
  - `CreateSpriteMaterialAndPreview`;
  - `CreateHUDFromAssets`;
  - `SetupClickableUIButton`;
  - `CreateScriptableConfigAndBindToScene`;
- `BuildBatch` повертає згенерований `UniBridge_BatchActions` payload;
- `DryRun` запускає batch з `DryRun=true`;
- `Execute` запускає batch з undo group, validation і asset rollback через
  `BatchActions`.

`ToolGuide` і `Documentation~/unibridge.md` оновлено, щоб новий агент
бачив `UniBridge_WorkflowRecipes` у стартовій карті та workflow `recipes`.

Live MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з source package;
- перший `Assets/Refresh` знайшов механічні split-артефакти `` `r`n `` у
  трьох partial-файлах; виправлено і повторено через MCP;
- після повторного refresh `tools/list` бачить 40 tools і
  `UniBridge_WorkflowRecipes`;
- `UniBridge_ReadConsole DiagnosticSummary` після compile: 0
  warnings/errors/exceptions/asserts;
- `UniBridge_ToolGuide Action=Workflow Topic=recipes` успішний;
- `WorkflowRecipes List` повертає 6 recipes;
- `Describe` і `BuildBatch` для `CreateInventoryScreen` успішні;
- `DryRun CreateInventoryScreen` успішний;
- `DryRun ImportSpriteFolderAs2D Folder=Assets/Sprites MaxAssets=2`
  успішний;
- `DryRun CreateScriptableConfigAndBindToScene` успішний;
- live `Execute SetupClickableUIButton`:
  - створено тимчасові canvas/button;
  - додано `Cidonix.UniBridge.TestFixtures.UniBridgeFixtureReferencer`;
  - recipe прив'язав `Button.onClick` до `Ping()`;
  - `UniBridge_ManageUnityEvent Inspect` підтвердив
    `persistentListenerCount=1`;
  - тимчасовий canvas видалено;
- додаткові patcher smoke:
  - `ManageAssetImporter ApplyPreset DryRun=true` для
    `Assets/Sprites/Item_0000.png`;
  - `ManageScriptableObject CreateOrUpdate DryRun=true` для fixture
    ScriptableObject;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 60 log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-13: Agent-facing refactor and ToolGuide

Після уточнення, що UniBridge має бути зручним не тільки поточному
агенту-розробнику, а й новим агентам, зроблено наступний рефакторинг без
зміни зовнішніх MCP контрактів.

Перед змінами повторно переглянуто reference implementation:

- `reference implementation source/Sidekick/SerializedPropertyUtils.cs`;
- `reference implementation source/Sidekick/ObjectSerializer.cs`;
- `reference implementation source/Sidekick/JSONUnityEvent.cs`.

Source package changes:

- `ManageScriptableObject.SetProperties` тепер використовує shared
  `SerializedPropertyPatcher`;
- `ManageAssetImporter.SetProperties` тепер використовує shared
  `SerializedPropertyPatcher` для importer serialized paths;
- `ManageUI.SetButtonEvent` переведено на shared
  `UnityEventPersistentCallUtility`, щоб button-specific шлях не мав
  окремої логіки persistent call binding;
- `UnityEventPersistentCallUtility` отримав `argumentType` hints
  (`Void`, `Int`, `Float`, `String`, `Bool`, `Object`) і коректний match
  `UnityEngine.Object` subclasses;
- `BatchActions` allow-list і aliases винесено в
  `Tools/BatchActionToolCatalog.cs`;
- додано read-only `UniBridge_ToolGuide`:
  - `Action=Overview` - стартова карта UniBridge workflow;
  - `Action=Workflow Topic=...` - конкретний workflow (`ui`,
    `assets_import`, `unity_events`, `scripts`, `batch`, `console`, etc.);
  - `Action=Tool Tool=...` - alias resolve + registry metadata;
- `Documentation~/unibridge.md` отримав розділ `Choose The Right Tool`;
- `UniBridge_ToolGuide` додано до batch allow-list та aliases
  `guide`, `tool_guide`, `toolguide`, `help`, `workflow`.

Live MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з source package;
- `Assets/Refresh` через `UniBridge_ManageMenuItem` пройшов;
- `tools/list` через relay показав 39 tools і `UniBridge_ToolGuide`;
- `UniBridge_ToolGuide Overview`, `Workflow Topic=unity_events` і
  `Tool Tool=asset_importer` успішні;
- `UniBridge_BatchActions` dry-run через alias `tool_guide` пройшов
  validation без помилок;
- `ManageUI SetButtonEvent` smoke:
  - створено тимчасові canvas/button;
  - додано `UniBridgeFixtureReferencer`;
  - button `onClick` прив'язано до `Ping()` через shared UnityEvent helper;
  - `UniBridge_ManageUnityEvent Inspect` підтвердив
    `persistentListenerCount=1`;
  - event очищено, тимчасовий canvas видалено, `UnitySearch` підтвердив 0
    залишків;
- `ManageScriptableObject` smoke:
  - створено тимчасовий
    `Assets/UniBridgeScriptableSmoke/SharedPatcherSmoke_*.asset`;
  - через serialized path `displayName` задано і оновлено значення;
  - inspect підтвердив `"Shared Patcher Smoke Updated"`;
  - asset видалено;
- `ManageAssetImporter` smoke:
  - `Inspect` і `SetProperties DryRun=true Reimport=false` для
    `Assets/Sprites/Item_0000.png`;
  - serialized property `m_IsReadable` пройшов через shared patcher;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 71 log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-13: reference-shaped UnityEvent persistent call tool

Після `SerializedPropertyPatcher` продовжено foundation/API refactor у
напрямку agent-friendly tools для нових інстансів. Наступний практичний
шматок із reference implementation - загальна робота з `UnityEventBase`, а не тільки
`Button.onClick`.

Перед реалізацією повторно переглянуто:

- `reference implementation source/Sidekick/JSONUnityEvent.cs`;
- існуючий `UniBridge_ManageUI` блок `SetButtonEvent` /
  `ClearButtonEvents` / `BuildUnityEventInfo`.

Корисна ідея reference implementation: UnityEvent треба представляти як JSON:

```text
{ persistentCalls: [ { target, methodName, callState, argument } ] }
```

У source package додано:

- `Modules/Cidonix.UniBridge.MCP.Editor/Helpers/UnityEventPersistentCallUtility.cs`;
- `Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManageUnityEvent.cs`;
- `Modules/Cidonix.UniBridge.MCP.Editor/Tools/Parameters/ManageUnityEventParams.cs`;
- `.meta` файли для нових класів.

Новий MCP tool: `UniBridge_ManageUnityEvent`.

Actions:

- `Inspect` - показати UnityEvent members на GameObject/component;
- `AddPersistentCall` - додати persistent listener;
- `SetPersistentCalls` - замінити повний список listener-ів reference-shaped
  `persistentCalls`;
- `ClearPersistentCalls` - очистити listeners.

Можливості helper/tool:

- працює з будь-яким `UnityEventBase` field/property, включно з
  `Button.onClick`, `m_OnClick`, `onValueChanged` і custom serialized
  UnityEvent fields;
- резолвить owner object через `SceneObjectLocator`;
- listener target можна задавати scene path/name/id, asset path/GUID або
  object reference;
- підтримує target component selection;
- persistent static arguments: `int`, `float`, `string`, `bool`,
  `UnityEngine.Object`;
- call state: `Off`, `EditorAndRuntime`, `RuntimeOnly`;
- `DryRun` показує заплановані listener-и без змін сцени.

Інтеграція:

- `ManageUI` тепер читає `Button.onClick` через shared
  `UnityEventPersistentCallUtility.BuildEventInfo`;
- `UniBridge_BatchActions` allow-list отримав `UniBridge_ManageUnityEvent`;
- batch aliases: `unity_event`, `unityevent`, `events`, `event`,
  `persistent_event`, `persistent_call`;
- batch validation/normalization додано для UnityEvent actions;
- `Documentation~/unibridge.md` оновлено.

Live MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з новим helper/tool/params, `ManageUI.cs`,
  `BatchActions.cs`, docs;
- `Assets/Refresh` через `UniBridge_ManageMenuItem` пройшов;
- після refresh `tools/list` бачить `UniBridge_ManageUnityEvent`, загалом
  38 tools;
- compile/console після refresh: 0 warnings/errors/exceptions/asserts;
- створено тимчасові `UniBridge_UnityEvent_Canvas_*` і
  `UniBridge_UnityEvent_Button_*`;
- на Button додано компонент
  `Cidonix.UniBridge.TestFixtures.UniBridgeFixtureReferencer`;
- `Inspect Component=Button EventProperty=onClick` показав 0 listeners;
- `AddPersistentCall` додав void listener
  `UniBridgeFixtureReferencer.Ping()` з `RuntimeOnly`;
- повторний `Inspect` підтвердив `persistentListenerCount=1`,
  `methodName=Ping`, `mode=Void`;
- `SetPersistentCalls` з reference-shaped object замінив listener на
  `GameObject.SetActive(false)`, `mode=Bool`, `argument=false`;
- `UniBridge_BatchActions` dry-run через alias `unity_event` для clear
  пройшов validation;
- `ClearPersistentCalls` очистив event до 0 listeners;
- тимчасовий canvas видалено, `UnitySearch` по SceneObjects повернув 0;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 28 log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-13: SerializedPropertyPatcher foundation refactor

Після `SceneObjectLocator` продовжено foundation-рефакторинг другим
шаром: shared `SerializedPropertyPatcher` для component property patches.

Перед реалізацією повторно переглянуто reference implementation:

- `reference implementation source/Sidekick/SerializedPropertyUtils.cs`;
- `reference implementation source/Sidekick/ObjectSerializer.cs`;
- `reference implementation source/Sidekick/JSONUnityEvent.cs`.

Корисна ідея reference implementation: агент має працювати з Unity `SerializedObject` /
`SerializedProperty`, а не тільки з C# reflection, бо саме serialized
property paths є стабільним контрактом для Inspector-facing fields.

У source package додано:

- `com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/SerializedPropertyPatcher.cs`;
- `.meta` для нового helper.

Можливості `SerializedPropertyPatcher`:

- шукає property за exact `propertyPath`, `name`, `displayName` або
  очищеним `m_` варіантом;
- підтримує `Integer`, `Boolean`, `Float`, `String`, `Color`, `Vector2`,
  `Vector3`, `Vector4`, `Vector2Int`, `Vector3Int`, `Rect`, `RectInt`,
  `Bounds`, `BoundsInt`, `Quaternion`, `Enum`, `LayerMask`, `Character`,
  `ObjectReference`, generic object children і arrays;
- read-only guard для `m_Script`;
- object references можна задавати asset path/GUID, instance id або
  `{ find, method }` scene-object reference;
- unsupported serialized types повертають контрольовану помилку, а не
  silent failure.

Інтеграція:

- `ManageGameObject.SetComponentPropertiesInternal` тепер спочатку пробує
  `SerializedPropertyPatcher` для кожної component property;
- якщо serialized property не знайдена, лишається старий reflection fallback
  (`SetProperty`), тому існуючі сценарії типу `localPosition` /
  `material.color` не ламаються;
- це робить `UniBridge_TypeSchema.serializedPropertyPaths` практично
  actionable для scene components.

Live MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з `SerializedPropertyPatcher.cs`,
  `.meta` і оновленим `ManageGameObject.cs`;
- `Assets/Refresh` через `UniBridge_ManageMenuItem` пройшов;
- compile/console після refresh: 0 warnings/errors/exceptions/asserts;
- створено тимчасовий object `UniBridge_SerializedPatch_Smoke_20260513`
  з компонентом `Cidonix.UniBridge.TestFixtures.UniBridgeFixtureMover`;
- через `UniBridge_ManageGameObject action=modify` задано:
  - `Transform.m_LocalPosition = {x:1.25,y:2.5,z:-3.75}`;
  - `UniBridgeFixtureMover.speed = 7.75`;
  - `UniBridgeFixtureMover.direction = {x:0,y:1,z:0}`;
  - `UniBridgeFixtureMover.DisplayName = "Serialized Patch Smoke"`;
- `UniBridge_TypeSchema InspectGameObject IncludeValues=true` підтвердив
  serialized values: `speed=7.75`, `direction=(0,1,0)`,
  `DisplayName="Serialized Patch Smoke"`;
- `ManageGameObject` response підтвердив transform position
  `(1.25, 2.5, -3.75)`;
- тимчасовий object видалено через MCP, `UnitySearch` повернув 0 results;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 0 warnings/errors/
  exceptions/asserts.

## 2026-05-13: SceneObjectLocator foundation refactor

Користувач уточнив, що UniBridge має бути зрозумілим не тільки поточному
агенту-розробнику, а й новим agent instances, які вперше бачать список MCP
tools. Після повторного порівняння reference implementation та UniBridge вирішено почати не з
масового перейменування tools, а з foundation-рефакторингу: спочатку
уніфікувати внутрішнє резолвлення scene objects, а потім вже будувати
чистіші agent-friendly tools/aliases.

Додано `SceneObjectLocator`:

- файл:
  `com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/SceneObjectLocator.cs`;
- підтримує пошук GameObject за `instanceId`, hierarchy path з `/` або без,
  exact name, tag, layer, component type і auto `by_id_or_name_or_path`;
- підтримує inactive objects, loaded scenes, current Prefab Mode stage,
  optional scene path scope і child-scope search;
- дає спільні helpers `GetHierarchyPath`, `GetRelativeHierarchyPath`,
  `GetLoadedScenes`, `IsEditableSceneObject`;
- ціль: прибрати різну поведінку `by_path`/`by_name` між tools і зменшити
  ризик, що агент змінить не той об'єкт.

Інтегровано без зміни зовнішнього MCP API:

- `ObjectsHelper` тепер є compatibility facade над `SceneObjectLocator`;
- `ManageGameObject.GetAllSceneObjects` використовує спільний locator;
- `SceneObjectView.View` використовує спільний locator для target resolve;
- `BatchActions` validation lookup для GameObject targets використовує
  спільний locator;
- `ManageUI.ResolveTargetGameObject` перейшов на спільний locator, але
  зберіг selection fallback і editable-scene filtering;
- `UnitySearch` scene object enumeration і hierarchy path тепер беруться зі
  спільного locator.

Live MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з новими/оновленими helper/tool files;
- `Assets/Refresh` через `UniBridge_ManageMenuItem` пройшов;
- перший compile pass показав Unity 6 warning у `SceneObjectLocator`
  через obsolete implicit conversion `SceneHandle -> int`; виправлено через
  `HashSet<Scene>` без використання obsolete handle conversion;
- повторний `Assets/Refresh` і `UniBridge_ReadConsole DiagnosticSummary`:
  0 warnings/errors/exceptions/asserts;
- `UniBridge_SceneObjectView View Target="/Main Camera" SearchMethod=by_path`
  повернув `Main Camera`;
- `UniBridge_TypeSchema InspectGameObject Target="/Main Camera"` повернув
  schema для `UnityEngine.Camera`;
- `UniBridge_ManageGameObject find target="/Main Camera" search_method=by_path`
  повернув 1 object;
- `UniBridge_UnitySearch Query="Main Camera" Sources=[SceneObjects]`
  повернув `Main Camera` як best result;
- `UniBridge_BatchActions` dry-run з `game_object/find` target
  `"/Main Camera"` пройшов: 1 validated, 0 validation errors/warnings;
- `UniBridge_ManageUI Inspect Target="UniBridge_Dashboard_Canvas/UB_Backdrop/UB_MainCard"`
  успішно знайшов inactive UI child через shared locator;
- фінальна Unity Console перевірка після UI smoke: 0 warnings/errors/
  exceptions/asserts.

## 2026-05-13: reference-informed Scene/Object view with detail levels

Користувач попросив перейти до останнього пункту з плану по reference implementation:
`Scene/Object view` з рівнями деталізації.

Перед реалізацією переглянуто reference implementation:

- `reference implementation source/Sidekick/ViewGameObjects.cs`;
- `reference implementation source/Sidekick/GetCurrentSceneHierarchy.cs`;
- `reference implementation source/Sidekick/Utils/SceneUtils.cs`;
- `reference implementation source/Sidekick/Utils/JSONGameObject.cs`;
- `reference implementation source/Sidekick/Utils/SceneSerializer.cs`;
- `reference implementation source/Models/LevelOfContext.cs`.

Корисні ідеї reference implementation:

- окремий read-only object view замість змішування з modify tool;
- hierarchy dump має бути компактним текстом, а не лише JSON-деревом;
- loaded scenes треба резолвити по `scenePath`, а без нього дивитись усі
  loaded scenes;
- для duplicate hierarchy paths корисна metadata колонка з index;
- component props треба включати тільки за запитом, бо повний dump сцени
  швидко стає занадто великим.

У source package додано `UniBridge_SceneObjectView`:

- actions: `Hierarchy`, `View`, `Selection`;
- detail levels: `Brief`, `Standard`, `Detailed`, `Full`;
- `Hierarchy` повертає reference-shaped flattened hierarchy text і опціонально
  structured roots;
- `View` резолвить GameObjects за id, hierarchy path, name, tag, layer або
  component type;
- `Selection` повертає поточну Unity selection у тому самому форматі;
- `Standard` додає tag/layer/static, transform, bounds, prefab summary,
  components і children;
- `Detailed` додає summaries для `Renderer`, `SpriteRenderer`, `Camera`,
  `Light`, `Collider`, `Rigidbody`, `Canvas`, `Graphic`, `UIDocument`,
  `MonoBehaviour` script refs;
- `Full` додає bounded `SerializedObject` properties для компонентів,
  з фільтром `IncludeComponentProperties`;
- додано ліміти `MaxDepth`, `MaxObjects`, `MaxChildren`, `MaxRoots`,
  `MaxSerializedProperties`, щоб tool був безпечним для великих сцен.

Інтеграція:

- `UniBridge_BatchActions` отримав allow-list entry, validation,
  normalization і aliases `scene_view`, `scene_object_view`,
  `object_view`, `view_object`, `view_objects`, `view_gameobjects`,
  `hierarchy_view`;
- `UniBridge_UnitySearch` для scene objects тепер підказує
  `suggestedTool = UniBridge_SceneObjectView`, `suggestedAction = View`;
- документацію `Documentation~/unibridge.md` і `package.json` оновлено.

Живий MCP smoke у
`H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з новим tool, params, `BatchActions.cs`,
  `UnitySearch.cs`, docs і `package.json`;
- `Assets/Refresh` виконано через `UniBridge_ManageMenuItem`;
- `tools/list` бачить `UniBridge_SceneObjectView`, загалом 37 tools;
- `Hierarchy Detail=Brief IncludeFlattened=true IndexDisplayMode=MetadataColumn`
  повернув flattened hierarchy для SampleScene;
- `UnitySearch Query=Main Camera Sources=[SceneObjects]` повернув
  `suggestedTool=UniBridge_SceneObjectView`;
- `View Target=Main Camera Detail=Detailed Select=true` повернув components
  `Transform`, `Camera`, `AudioListener`, `UniversalAdditionalCameraData`;
- `Selection Detail=Standard` повернув selected `Main Camera`;
- `View Target=Main Camera Detail=Full IncludeSerializedProperties=true`
  повернув bounded properties для `Transform` і `Camera`;
- `UniBridge_BatchActions` dry-run через alias `object_view` пройшов;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 0 warnings/errors/
  exceptions/asserts.

## 2026-05-13: reference-informed UI Toolkit / UXML capture

Після BatchActions rollback користувач запропонував перейти до UI Toolkit /
UXML capture за зразком reference implementation, з власними покращеннями для UniBridge.

Перед реалізацією переглянуто reference implementation:

- `reference implementation source/Sidekick/GetUIToolkitImage.cs`;
- `reference implementation source/Sidekick/ImageUtils.cs`
  (`ForceRenderUIDocument`);
- `reference implementation source/Sidekick/EditorUtils.cs`
  (`ForceRepaintAndWaitForEditorUpdateAsync`, Unity 6 focus workaround).

Корисна ідея reference implementation: для UXML capture треба створити тимчасові
`PanelSettings + UIDocument + RenderTexture`, завантажити `VisualTreeAsset`,
примусово викликати panel `ValidateLayout`, `Update`, `Repaint`, дочекатись
editor update і зробити GPU/readback з render target.

У source package додано `UniBridge_CaptureUIToolkit`:

- actions: `Capture`, `Inspect`, `ListUxml`;
- `Capture` рендерить UXML/UIDocument у PNG у
  `~/.unibridge/uitoolkit-captures/<project>`;
- `Inspect` повертає resolved `VisualElement` tree без запису PNG;
- `ListUxml` шукає `VisualTreeAsset` assets по `Query`/`Folders`;
- підтримка `Path`, `Guid`, `Target` з `UIDocument`, або `Query`;
- response містить PNG path/fileUri, document metadata, panel/theme metadata,
  bounded VisualElement tree, pixel stats і прості issue hints:
  `BLANK_CAPTURE`, `ZERO_SIZE_ELEMENT`, `INVISIBLE_OPACITY`,
  `OUTSIDE_RENDER_TARGET`;
- перенесено reference-pattern double render + editor update wait;
- додано Unity 6 Windows focus workaround, бо без нього runtime panel у live
  тесті мав layout, але `RenderTexture` лишався прозорим;
- додано theme fallback: explicit `ThemeStyleSheetPath`, target
  `PanelSettings.themeStyleSheet`, UI Builder theme, або
  `UnityDefaultRuntimeTheme.tss`, якщо є в проекті.

Інтеграція:

- `UniBridge_BatchActions` отримав allow-list entry, validation,
  normalization і aliases `uitoolkit_capture`, `capture_uitoolkit`,
  `ui_toolkit_capture`, `uxml_capture`, `capture_uxml`, `uxml`;
- `UniBridge_UnitySearch` для `.uxml` / `VisualTreeAsset` тепер підказує
  `suggestedTool = UniBridge_CaptureUIToolkit`;
- документацію `Documentation~/unibridge.md` і `package.json` оновлено.

Живий MCP smoke у
`H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з новим tool, params, `BatchActions.cs`,
  `UnitySearch.cs`, docs і `package.json`;
- створено тестовий UXML fixture
  `Assets/UniBridgeUIToolkitSmoke/SmokePanel.uxml`;
- перший compile smoke показав C# shadowing error у `ResolveDocument`
  (`path`), виправлено;
- перший runtime smoke показав blank PNG (`nonTransparentPixels=0`),
  виправлено reference-shaped editor update wait і Unity 6 focus workaround;
- explicit theme smoke показав, що без runtime theme текстові елементи мають
  zero height; додано default fallback на `UnityDefaultRuntimeTheme.tss`;
- фінальний default smoke без explicit theme:
  `ListUxml` returned 1, `Inspect issueCount=0`,
  `Capture issueCount=0`, `blankCapture=false`,
  `nonTransparentPixels=230400`, `coverage=1`, PNG bytes `25676`,
  theme `Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss`;
- `UnitySearch Query=SmokePanel Sources=[Assets]` повернув
  `suggestedTool=UniBridge_CaptureUIToolkit`;
- `UniBridge_BatchActions` dry-run через alias `uxml` пройшов;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 28 log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-13: reference-informed BatchActions transaction rollback

Після `UniBridge_UnitySearch` користувач нагадав, що третій запланований
інструмент - transaction rollback для `UniBridge_BatchActions`.

Перед реалізацією переглянуто reference implementation:

- `reference implementation source/Sidekick/ActionExecutor.cs`;
- `reference implementation source/Sidekick/ExecuteActions.cs`;
- `reference implementation source/Sidekick/ResolveAndValidateActions.cs`;
- `reference implementation source/Sidekick/ReloadFromCheckpoint.cs`;
- `reference implementation source/Sidekick/EditorSnapshot.cs`.

Корисна ідея reference implementation: batch actions мають виконуватись у Unity Undo group, а
при помилці робити `Undo.RevertAllDownToGroup` і прибирати створені asset /
folder paths. У UniBridge до цього `BatchActions` вже мав `UseUndoGroup`, але
лише collapse-ив undo operations, не відкотував batch на failure.

У source package оновлено `UniBridge_BatchActions`:

- нові опції `RollbackOnFailure` і `RollbackAssets`, обидві default `true`
  для executing batches;
- перед виконанням створюється Unity Undo group;
- для asset rollback збираються referenced `Assets/...` і `Packages/...`
  paths зі step parameters плюс open scene files;
- робиться bounded snapshot: до 600 файлів, до 50 MB сумарно, до 10 MB на
  файл;
- якщо required step падає або validation зупиняє batch після вже виконаних
  steps, batch викликає `Undo.RevertAllDownToGroup`, restore-ить captured
  files і видаляє newly-created referenced roots;
- response отримав `data.rollback` з `triggered`, `completed`, `undoGroup`,
  `undoReverted`, `assetRollback`, `warnings`, `errors`;
- під час smoke знайдено зручнісний дефект: `BatchActions` validate приймав
  `Name`, а `ManageGameObject` execute очікував `name`; додано нормалізацію
  common GameObject aliases (`Name`, `Target`, `SearchMethod`, transform,
  components, prefab fields тощо).

Живий MCP smoke у
`H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з оновленим `BatchActions.cs`;
- `Assets/Refresh` через `UniBridge_ManageMenuItem` пройшов;
- scene rollback test: batch створив
  `UniBridge_TxRollback_Object_Alias2_20260513`, другий step навмисно впав
  на missing asset validation, rollback завершився `completed=true`,
  `undoReverted=true`, після цього `UniBridge_UnitySearch` по SceneObjects
  повернув `total=0`;
- asset rollback test: batch створив folder
  `Assets/UniBridgeTxRollbackAssetFile_20260513`, material
  `RollbackMaterial.mat`, третій step навмисно впав на missing asset
  validation, rollback завершився `completed=true`, `deletedCreatedRoots=2`,
  після цього `UnitySearch` не знайшов ні folder, ні material;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 73 log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-13: reference-informed UnitySearch / unified discovery

Після завершення `UniBridge_TypeSchema` користувач попросив зробити коміт
і перейти до другого пункту з reference implementation backlog. TypeSchema зміни були
зафіксовані у Plastic як:

- `cs:86@/main` `Add Unity type schema inspection tool` - нові
  `TypeSchema.cs`, `TypeSchemaParams.cs` і `.meta`;
- `cs:87@/main` `Wire type schema workflow` - BatchActions/docs/package
  wiring і оновлення context.

Другим пунктом обрано reference-shaped search/discovery layer: один інструмент,
який шукає по assets, scene objects, scripts, loaded types, shaders і editor
menus, повертає ranked normalized results і підказує, який UniBridge tool
викликати далі.

Перед реалізацією переглянуто reference implementation:

- `reference implementation source/Sidekick/SearchAssetsByQuery.cs`;
- `reference implementation source/Sidekick/ViewAssets.cs`;
- `reference implementation source/Sidekick/GetGameObjectsByName.cs`;
- пов'язані `GameObjectUtils` і `SceneUtils`.

Корисна ідея reference implementation: AI-агенту потрібен не набір вузьких пошуків, а єдиний
discovery step, який приймає людський query і сам знаходить відповідні
Unity-об'єкти, asset paths, scene paths, menu commands або типи.

У source package додано `UniBridge_UnitySearch`:

- actions: `Search`, `Resolve`, `Selection`;
- sources: `Assets`, `SceneObjects`, `Scripts`, `Types`, `Shaders`, `Menus`;
- filters: `Types`, `Extensions`, `Labels`, `IncludePackages`,
  `IncludeInactive`, `IncludeComponents`, `Exact`, `Limit`,
  `PerSourceLimit`;
- normalized result містить `source`, `kind`, `name`, `path`, `guid`, `type`,
  `extension`, `labels`, `scenePath`, `objectId`, `menuPath`, `components`,
  `score`, `matchedFields`;
- кожен result може повертати `suggestedTool`/`suggestedAction`, наприклад
  `UniBridge_AssetIntelligence`, `UniBridge_ManageGameObject`,
  `UniBridge_ManageMaterial`, `UniBridge_ScriptIntelligence`,
  `UniBridge_TypeSchema`, `UniBridge_ManageMenuItem`;
- `Resolve` повертає best match і ambiguity metadata;
- `Selection` нормалізує поточну Unity selection у той самий result формат;
- `UniBridge_BatchActions` отримав allow-list entry і aliases:
  `unity_search`, `unified_search`, `find`, `lookup`, `project_search`,
  `scene_search`;
- документацію `Documentation~/unibridge.md` і `package.json` changelog
  оновлено.

Live MCP testing у
`H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано вручну із source package;
- `Assets/Refresh` через `UniBridge_ManageMenuItem` спершу показав compile
  issues у `UnitySearch.cs`: `UnityEditor.Selection` shadowing, відсутній
  generic `AssetDatabase.LoadMainAssetAtPath<T>` у поточній Unity 6 API,
  unassigned local `matched`;
- після source fix і повторної синхронізації `tools/list` бачить 35 tools і
  `UniBridge_UnitySearch`;
- під час функціонального smoke знайдено deferred LINQ bug у
  `SortFinalResults`: `results.Clear()` виконувався до materialize sorted
  sequence, тому sourceCounts були ненульові, а final results порожні;
- після fix через `ToList()` пошук почав повертати коректні ranked results;
- додано canonical source name `Assets` і built-in fallback для
  `Assets/Refresh`, щоб menu search стабільно знаходив refresh command.

Фінальний smoke через MCP:

- `Search Query=Item_0000 Sources=[Assets]` -> `Assets/Sprites/Item_0000.png`
  (`Texture2D`), suggested `UniBridge_AssetIntelligence/Inspect`;
- `Search Query=Main Camera Sources=[SceneObjects]` -> scene object
  `Main Camera`, suggested `UniBridge_ManageGameObject/Find`;
- `Search Query=UniBridgeFixtureMover Sources=[Scripts,Types]` -> script
  `Assets/UniBridgeScriptFixtures/Runtime/UniBridgeFixtureMover.cs`,
  suggested `UniBridge_ScriptIntelligence/Analyze`;
- `Search Query=NeonSpritePulse Sources=[Shaders,Assets]` -> material
  `Assets/UniBridgeMaterialSmoke/Materials/NeonSpritePulse.mat`, plus shader
  matches;
- `Search Query=Assets Refresh Sources=[Menus]` -> `Assets/Refresh` as best
  menu result, suggested `UniBridge_ManageMenuItem/Execute`;
- `Resolve Query=Main Camera Sources=[SceneObjects,Types]` -> best scene
  object `Main Camera`;
- `UniBridge_BatchActions` dry-run via alias `unity_search`: 1 validated,
  0 executed, 0 validation errors/warnings;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 131 log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-13: reference-informed TypeSchema / ComponentSchema

Після повторного порівняння UniBridge з reference implementation користувач підтвердив, що
починаємо з пункту 1: `TypeSchema / ComponentSchema`.

Перед реалізацією переглянуто reference implementation:

- `reference implementation source/Sidekick/GetTypescriptDefinitions.cs`;
- `reference implementation source/Sidekick/SerializableObjects.cs`;
- `reference implementation source/Sidekick/ShaderSchemaGenerator.cs`;
- `reference implementation source/Sidekick/TypeUtils.cs`.

Корисна ідея reference implementation: перед зміною компонентів, importer-ів,
ScriptableObject-ів, assets або material/shader props агент має отримати
точну схему доступних типів, writable fields/properties, enum values,
serialized property paths і shader property metadata.

У source package додано `UniBridge_TypeSchema`:

- actions: `Inspect`, `ListTypes`, `InspectShader`, `InspectAsset`,
  `InspectGameObject`;
- `Kind=Any/Component/MonoBehaviour/ScriptableObject/AssetImporter/Asset/Shader`;
- reflection schema для public/settable properties, public fields і private
  `[SerializeField]` fields;
- live `SerializedObject` property paths з bounded `MaxSerializedProperties`;
- shader schema з property name/type/default/range/flags/attributes,
  texture dimensions і material current values;
- asset schema повертає main asset schema, importer schema, material shader
  schema або shader schema;
- GameObject schema повертає схеми компонентів на target object;
- `UniBridge_BatchActions` отримав allow-list entry і aliases:
  `type_schema`, `component_schema`, `shader_schema`, `importer_schema`,
  `asset_schema`;
- документацію `Documentation~/unibridge.md` і `package.json` changelog
  оновлено.

Живий MCP smoke у
`H:/Repos/UnityRepos/UniBridge_Test_Project` після синхронізації embedded
package:

- перший `Assets/Refresh` показав compile error
  `TypeSchema.cs: 'Editor' is a namespace but is used like a type` і два
  Unity 6 warnings про deprecated `GetInstanceID`;
- у source виправлено namespace collision через `UnityEditor.Editor` і
  прибрано warning-и локальним `GetObjectInstanceId` wrapper з pragma;
- після повторного `Assets/Refresh` через MCP `tools/list` показав
  `UniBridge_TypeSchema`, загалом 34 tools;
- `ListTypes Kind=Component Query=SpriteRenderer` повернув
  `UnityEngine.SpriteRenderer`;
- `Inspect UnityEngine.SpriteRenderer` повернув writable names, зокрема
  `sprite`, `drawMode`, `size`, `color`, `flipX`;
- `InspectShader Shader=Sprites/Default` повернув 7 shader properties;
- `InspectAsset Assets/UniBridgeMaterialSmoke/Materials/NeonSpritePulse.mat`
  повернув `UnityEngine.Material`, importer і shader
  `UniBridge/NeonSpritePulse` з 6 props;
- `InspectAsset Assets/Sprites/Item_0000.png` повернув
  `UnityEngine.Texture2D` і `UnityEditor.TextureImporter` з 58 serialized
  properties;
- `InspectGameObject Target=Main Camera ComponentTypes=["Camera"]` повернув
  схему `UnityEngine.Camera`;
- `UniBridge_BatchActions` dry-run через alias `component_schema` пройшов;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 47 службових log
  entries, 0 warnings/errors/exceptions/asserts.

## 2026-05-12: Asset capture from reference implementation GetAssetImage pattern

Після ScriptableObject tooling користувач дозволив рухатись далі по reference implementation,
якщо інструмент буде корисний для агента. Наступним обрано reference-shaped
`GetAssetImage`, бо це дає AI-агенту не просто Unity thumbnail, а реальний
рендер asset-а до вставки в сцену.

Перед реалізацією переглянуто:

- `reference implementation source/Sidekick/GetAssetImage.cs`;
- поточні `UniBridge_CaptureView` і `UniBridge_AssetIntelligence Preview`.

Корисна ідея reference implementation: asset preview має інстансити asset у тимчасове візуальне
оточення, рахувати bounds, фреймити камерою і рендерити PNG, а не
покладатися тільки на `AssetPreview.GetAssetPreview`.

У source package додано `UniBridge_CaptureAsset`:

- окремий MCP tool замість роздування `CaptureView`;
- рендер у Unity preview scene через `EditorSceneManager.NewPreviewScene`;
- тимчасова камера, directional light, `RenderTexture` -> PNG для 3D preview;
- підтримка prefab/model `GameObject`, `Mesh`, `Material`, `Sprite`,
  `Texture2D`;
- `Material` рендериться на сфері;
- `Sprite` і `Texture2D` у `Auto`/`Front` рендеряться flat 2D напряму з
  texture rect, щоб preview не виглядав як повернутий 3D object;
- `Texture2D` для явних 3D views лишається textured quad;
- `View=Auto/Iso/Front/Back/Left/Right/Top/Bottom`;
- orthographic camera за замовчуванням, bounds-based framing, padding,
  transparent background;
- output під `~/.unibridge/asset-captures/<project>`;
- `UniBridge_BatchActions` отримав allow-list entry і aliases:
  `asset_capture`, `capture_asset`, `asset_preview`, `render_asset`,
  `asset_vision`.
- наступним етапом, після повторного перегляду reference implementation
  `SearchAssetsByQuery`, `ViewAssets`, `GetAssetImage` і
  `ImageUtils.StitchIntoSquareGrid`, у `UniBridge_CaptureAsset` додано
  `Action=CaptureGrid`: paths/guids або folder/query/types -> contact sheet
  PNG з numbered badges і mapping `index -> asset path` у metadata.

Документацію `Documentation~/unibridge.md` і `package.json` changelog
оновлено.

Живий MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- source-зміни синхронізовано в embedded package;
- після `Assets/Refresh` через MCP `tools/list` показав
  `UniBridge_CaptureAsset`, загалом 33 tools;
- перший 2D sprite capture виявив завеликий zoom і сценовий preview path;
- дефолтний `Padding` змінено з `1.18` на `1.4`;
- `Sprite`/`Texture2D Auto/Front` переведено на `renderMode=Flat2D`;
- повторний capture
  `Assets/UniBridgeImporterSmoke/Item_0000.png` створив:
  `C:/Users/Cidonix/.unibridge/asset-captures/UniBridge_Test_Project_ae4e3233/asset_capture_Item_0000_flat2d_default_padding_20260512_182513_301.png`;
- material preview
  `Assets/UniBridgeMaterialSmoke/Materials/NeonSpritePulse.mat`
  лишився 3D sphere preview і створив:
  `C:/Users/Cidonix/.unibridge/asset-captures/UniBridge_Test_Project_ae4e3233/asset_capture_NeonSpritePulse_material_after_flat_fix_20260512_182513_395.png`;
- `UniBridge_BatchActions` dry-run через alias `asset_capture`: 1 validated,
  0 validation errors/warnings;
- Unity Console debug logs вручну переглянуто через
  `ReadConsole Timeline/Groups/Search UniBridge/DiagnosticSummary`;
  були лише службові MCP debug/log записи, 0 warnings/errors/exceptions/
  asserts.

Додатковий MCP smoke для `Action=CaptureGrid` після синхронізації в
embedded package:

- після `Assets/Refresh` через MCP `tools/list` все ще показав
  `UniBridge_CaptureAsset`, загалом 33 tools;
- pre-test `ReadConsole DiagnosticSummary`: 16 log entries,
  0 warnings/errors/exceptions/asserts;
- explicit grid для `Assets/UniBridgeImporterSmoke/Item_0000.png` і
  `Assets/UniBridgeMaterialSmoke/Materials/NeonSpritePulse.mat`:
  2 selected/rendered, `520 x 256`, columns=2, rows=1, output
  `C:/Users/Cidonix/.unibridge/asset-captures/UniBridge_Test_Project_ae4e3233/asset_grid_contact_sheet_explicit_smoke_20260512_221345_064.png`;
- folder/type grid для `Assets/UniBridgeImporterSmoke` +
  `Types=["Sprite"]`: 1 selected/rendered, `192 x 192`, output
  `C:/Users/Cidonix/.unibridge/asset-captures/UniBridge_Test_Project_ae4e3233/asset_grid_contact_sheet_folder_sprite_smoke_20260512_221345_234.png`;
- `UniBridge_BatchActions` dry-run через alias `asset_grid`: 1 validated,
  0 validation errors/warnings;
- фінальна Unity Console після додаткового sanity check: 25 службових log entries,
  0 warnings/errors/exceptions/asserts.

## 2026-05-12: ScriptableObject tooling from reference implementation create/update pattern

Після material workflow користувач попросив зробити коміт і рухатись далі
за планом по reference implementation. Коміт попереднього етапу створено у Plastic:
`cs:82@/main` з message `Add material management MCP workflow`.

Наступна реальна прогалина після повторного порівняння з reference implementation:
`CreateOrUpdateScriptableObjectAction`. У reference implementation корисна ідея була така:
окремий action створює або оновлює `.asset`, перевіряє
`scriptableObjectType`, не дозволяє type mismatch, створює директорію і
десеріалізує `props` у реальний `ScriptableObject`.

У UniBridge почато MCP-native порт як `UniBridge_ManageScriptableObject`:

- дії: `Inspect`, `Validate`, `CreateOrUpdate`, `SetProperties`,
  `ListTypes`;
- `Path`/`Guid` resolution для `.asset` assets;
- type resolution за short/full/assembly-qualified type name через
  завантажені Unity types / `TypeCache`;
- reference-shaped input shape
  `{ scriptableObject: { scriptableObjectType, props } }`;
- `SerializedObject`-aware property patching для public і serialized fields:
  primitives, string, enum, color, vectors, rect/bounds/quaternion,
  `UnityEngine.Object` references і common arrays;
- `DryRun`, `AllowPackages`, bounded serialized property snapshots;
- `ListTypes` повертає ScriptableObject types із `CreateAssetMenu`
  metadata, щоб агент міг знайти правильний data/config type перед
  створенням asset-а.

`UniBridge_BatchActions` отримав allow-list entry, validation,
normalization і aliases: `scriptable_object`, `scriptableobject`,
`scriptable`, `so`, `data_asset`, `config_asset`.

Документацію `Documentation~/unibridge.md` і `_upm.changelog` оновлено.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- source package синхронізовано в embedded package;
- тест виконувався напряму через
  `unibridge_relay_win.exe --mcp --project-id ae4e323353aa487f9ea74d566b38eaac`;
- `Assets/Refresh` через MCP пройшов, після reconnect
  `tools/list` підтвердив `UniBridge_ManageScriptableObject`;
- створено тестовий `ScriptableObject` тип
  `Assets/UniBridgeScriptableSmoke/UniBridgeAgentTuningSmoke20260512.cs`;
- `ListTypes Query=UniBridgeAgentTuningSmoke20260512` знайшов тип і
  `CreateAssetMenu` metadata;
- `Validate` для
  `Assets/UniBridgeScriptableSmoke/AgentTuningSmoke.asset` з direct
  `Properties` пройшов: 10 planned changes, 0 warnings/errors;
- `CreateOrUpdate` створив `.asset` і записав `displayName`, `health`,
  `speed`, `tint`, `spawnOffset`, `tags[]`, private `[SerializeField]`
  `holographic`;
- `Inspect` підтвердив записані values, включно з `tags.arraySize=3` і
  `holographic=true`;
- `SetProperties` змінив `health=88`, `speed=5.25`, `Inspect` підтвердив;
- `UniBridge_BatchActions` dry-run через alias `so` пройшов:
  1 validated, 0 validation errors/warnings;
- reference-shaped shape
  `{ scriptableObject: { scriptableObjectType, props } }` пройшов
  `Validate`: 3 planned changes, 0 warnings/errors;
- фінальна `UniBridge_ReadConsole DiagnosticSummary` після очищення:
  0 warnings/errors/exceptions/asserts.

## 2026-05-12: Material tooling from reference implementation shader-property patterns

Перед реалізацією переглянуто розпаковану reference implementation source:

- `reference implementation source/Actions/CreateOrUpdateMaterialAction.cs`;
- `reference implementation source/Sidekick/JSONMaterial.cs`;
- `reference implementation source/Sidekick/JSONShaderProps.cs`;
- `reference implementation source/Sidekick/ShaderUtils.cs`.

Корисна ідея reference implementation: material authoring має йти не через сліпі рядки, а через
реальні `Shader` properties: property count, property name/type/flags,
hidden guard, texture refs і `_ST` scale/offset.

У source package додано `UniBridge_ManageMaterial`:

- дії: `Inspect`, `Validate`, `CreateOrUpdate`, `SetShader`,
  `SetProperties`, `ApplyPreset`;
- `Path`/`Guid` resolution для `.mat` assets;
- shader resolution через asset path або `Shader.Find`;
- reference-shaped input shape `{ shader: { shaderPath, props } }`;
- direct shader property patches: color, vector, float/range, int, texture,
  texture scale/offset (`_MainTex_ST`, `_BaseMap_ST`);
- material settings: `enableInstancing`, `doubleSidedGI`, `renderQueue`,
  keyword replace/enable/disable;
- `DryRun`, `AllowPackages`, `Select`;
- shader metadata у response: property names, display names, types, flags,
  ranges, texture dimensions, scale/offset support і current values;
- presets: `URPLit`, `URPUnlit`, `Standard`, `UnlitColor`,
  `SpriteDefault`, `UIDefault`, `Transparent`, `Cutout`;
- convenience `TexturePath` і `Color`, які підбирають common main texture /
  color property для конкретного shader-а.

`UniBridge_BatchActions` отримав allow-list entry, validation, normalization
і aliases: `material`, `materials`, `mat`, `mats`, `material_settings`,
`materialsettings`.

Документацію `Documentation~/unibridge.md` і `_upm.changelog` оновлено.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- source package синхронізовано в embedded package;
- тест виконувався напряму через
  `unibridge_relay_win.exe --mcp --project-id ae4e323353aa487f9ea74d566b38eaac`;
- `Assets/Refresh` через MCP пройшов, після refresh Unity Console:
  0 warnings/errors/exceptions;
- `tools/list` підтвердив `UniBridge_ManageMaterial`;
- `Validate` без material path, але з `Shader=Standard`, перевірив
  `_Color`, `_MainTex`, `_MainTex_ST` і повернув shader metadata;
- `CreateOrUpdate` створив
  `Assets/UniBridgeMaterialSmoke/StandardMaterialSmoke.mat`;
- material отримав shader `Standard`, texture
  `Assets/UniBridgeImporterSmoke/Item_0000.png`, `_MainTex_ST`
  `x=1, y=1, z=0.1, w=0.2`;
- `SetProperties` змінив `_Color`, `renderQueue=2450` і увімкнув
  `_ALPHATEST_ON`;
- `Inspect` підтвердив shader/property values:
  `_Color={r=0.2,g=0.9,b=0.75,a=1}`,
  `_MainTex` pointing to `Item_0000`;
- `UniBridge_BatchActions` dry-run через alias `material` пройшов:
  1 validated, 0 executed, 0 validation errors/warnings;
- після cleanup response-шуму повторний dry-run `SetProperties` для
  `_Glossiness=0.42` повернув один `would_change` без дубльованих
  `validated` records;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 0 warnings/errors/
  exceptions/asserts.

## 2026-05-11: AssetImporter tooling from reference implementation patterns

Перед реалізацією ще раз переглянуто розпаковану reference implementation source:
`reference implementation source`.

Корисні місця в reference implementation:

- `reference implementation source/Actions/UpdateAssetImporterAction.cs`:
  asset path -> importer type validation -> deserialize props into concrete
  `AssetImporter` -> `SaveAndReimport`;
- `Models/JSONModelImporter.cs`: великий typed contract для `ModelImporter`
  properties;
- `Models/JSONAudioImporter.cs`: `defaultSampleSettings`, `forceToMono`,
  `loadInBackground`, `ambisonic`;
- `ShaderUtils.cs`: практична ідея автоматично виставляти texture import
  type для текстур, коли asset треба використати як special-purpose input.

У UniBridge це портовано не як reference implementation JSON-модель імпортера, а як MCP-native
tool:

- додано `UniBridge_ManageAssetImporter`;
- дії: `Inspect`, `SetProperties`, `ApplyPreset`, `Reimport`;
- `Path`/`Guid` resolution для `Assets/...` і `Packages/...`;
- опційний `ImporterType` guard (`TextureImporter`, `ModelImporter`,
  `AudioImporter`, full type name тощо);
- package mutation guard: `Packages/...` не змінюються без
  `AllowPackages=true`;
- `Inspect` повертає common importer data, type-specific snapshots для
  texture/model/audio і bounded serialized property list;
- `SetProperties` спочатку пробує public property/field, потім
  `SerializedProperty` path;
- `ApplyPreset` підтримує `TextureSprite2D`, `TextureUI`,
  `TextureReadable`, `TextureNormalMap`, `ModelStatic`, `ModelAnimated`,
  `Audio2D`, `AudioStreaming`;
- `Properties` накладаються після preset, щоб агент міг змінити лише
  конкретні поля;
- mutating actions підтримують `DryRun` і `Reimport`.

`UniBridge_BatchActions` отримав allow-list entry, validation, normalization
і aliases: `asset_importer`, `assetimporter`, `importer`,
`import_settings`, `importsettings`, `importer_settings`.

Документацію `Documentation~/unibridge.md` і `_upm.changelog` оновлено.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- source package синхронізовано в embedded package;
- перший `Assets/Refresh` через MCP показав compile errors у Unity Console:
  `TextureImporter.spriteMeshType` і `TextureImporter.spriteAlignment`
  відсутні в Unity 6000.4.6f1;
- snapshot виправлено на reflection-safe читання цих полів через
  `TryGetPublicProperty`, після повторного `Assets/Refresh` консоль чиста;
- тест виконувався напряму через
  `unibridge_relay_win.exe --mcp --project-id ae4e323353aa487f9ea74d566b38eaac`;
- з `E:/GamesContent/GFX/Предметы/items_atlas_0000_Слой-2.png`
  імпортовано реальний PNG у
  `Assets/UniBridgeImporterSmoke/Item_0000.png`;
- `UniBridge_ManageAssetImporter Inspect` підтвердив `TextureImporter`;
- `ApplyPreset Preset=TextureSprite2D` з `SpritePixelsPerUnit=64`,
  `MaxTextureSize=256`, `IsReadable=true` застосувався і reimport-нув asset;
- повторний `Inspect` підтвердив `textureType=Sprite`,
  `spriteImportMode=Single`, `spritePixelsPerUnit=64`, `isReadable=true`,
  `maxTextureSize=256`;
- `SetProperties` успішно записав `userData`;
- `UniBridge_BatchActions` dry-run через alias `importer_settings` пройшов
  validation: 1 validated, 0 executed, 0 validation errors;
- штучний тимчасовий root PNG fixture видалено через MCP;
- фінальна `UniBridge_ReadConsole DiagnosticSummary`: 0 warnings/errors/
  exceptions/asserts.

## 2026-05-11: UI graphics, selectable transitions, and button events

Після `CreateTemplate` користувач попросив перейти до шару
`Button events, sprites/icons/materials` і ще раз нагадав правило:
для нових UI-фіч спочатку дивитися в розпаковану reference implementation source:
`reference implementation source`.

У reference implementation корисними були serializer-и:

- `JSONButton`: `interactable`, `transition`, `navigation`,
  `targetGraphic`, `colors`, `spriteState`, `animationTriggers`,
  `onClick`;
- `JSONSelectable`: той самий transition contract для будь-якого
  `Selectable`;
- `JSONImage`: `sprite`, `color`, `material`, `raycastTarget`,
  `type`, `preserveAspect`;
- `JSONRawImage`: `texture`, `color`, `material`, `raycastTarget`,
  `uvRect`;
- `JSONSpriteState`: highlighted/pressed/selected/disabled sprites;
- `JSONUnityEvent`: persistent calls з `target`, `methodName`,
  `callState`, `argument` через `UnityEventTools`.

У UniBridge це перенесено не як повну reference-серіалізацію компонентів, а
як MCP-native дії для агентської роботи з Unity UI:

- `UIAction.SetGraphic`:
  - додає або оновлює `Image`, `RawImage`, або наявний `Graphic`;
  - підтримує `SpritePath`, `TexturePath`, `MaterialPath`, `Color`,
    `BackgroundColor`, `ImageType`, `PreserveAspect`,
    `RaycastTarget`, `SetNativeSize`;
  - `SpritePath`/`TexturePath` приймають `Assets/...`, `Packages/...`
    або GUID;
  - для `SpritePath` fallback бере sprite sub-asset через
    `AssetDatabase.LoadAllAssetsAtPath`;
  - може одночасно проставити sprite state на `Selectable`;
- `UIAction.SetSelectableTransition`:
  - налаштовує `Selectable.transition`: `None`, `ColorTint`,
    `SpriteSwap`, `Animation`;
  - виставляє `targetGraphic`, `ColorBlock` і `SpriteState`;
- `UIAction.SetButtonEvent`:
  - додає persistent `Button.onClick` listener через
    `UnityEditor.Events.UnityEventTools`;
  - підтримує target `GameObject` або конкретний `EventComponent`;
  - підтримує public `void` methods з аргументами
    `Void`, `String`, `Int`, `Float`, `Bool`;
  - `ClearExistingEvents=true` замінює попередні persistent listeners;
- `UIAction.ClearButtonEvents` видаляє persistent `Button.onClick`
  listeners;
- `Inspect` тепер повертає `graphic`, `selectable`, `button.onClick`
  snapshots, включно з material/sprite/texture refs, transition,
  color block, sprite state і persistent call targets/methods.

`UniBridge_BatchActions` отримав aliases:

- `set_graphic`, `graphic`, `set_image`, `set_icon`;
- `set_button_event`, `button_event`, `onclick`, `on_click`;
- `clear_button_events`, `clear_onclick`;
- `set_selectable_transition`, `set_transition`;
- aliases для `SpritePath`, `TexturePath`, `MaterialPath`,
  `TargetGraphic`, transition colors, event target/component/method,
  argument type/value.

Документацію `Documentation~/unibridge.md` і `_upm.changelog` оновлено.

Live MCP smoke у `UniBridge_Test_Project`:

- source package синхронізовано в embedded package тестового проєкту;
- `dotnet build Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  завершився з 0 errors; лишився старий warning `MSB3277` про
  `System.Threading.Tasks.Extensions`;
- через MCP виконано `Assets/Refresh`, `tools/list` підтвердив нові
  action-и в `UniBridge_ManageUI`;
- створено тимчасовий canvas
  `UniBridge_UIActionsSmoke_Canvas_20260511181236`;
- через MCP створено material asset
  `Assets/UniBridgeManual/UI/UB_UIActionsSmoke_20260511181236.mat`;
- `SetGraphic` на `Image` успішно призначив color і material;
- `SetSelectableTransition` на `Button` успішно виставив `ColorTint`
  і color block;
- `SetButtonEvent` призначив persistent listener
  `Button.Select()`; `Inspect` підтвердив `persistentListenerCount=1`
  і `methodName=Select`;
- `UniBridge_BatchActions` dry-run з aliases `set_icon`,
  `set_transition`, `on_click` пройшов: 3 validated, 0 validation
  errors/warnings;
- `Validate` для smoke canvas повернув 0 errors/warnings і 1 info:
  `SCREEN_SPACE_OVERLAY_CAPTURE_CAVEAT` для ScreenSpaceOverlay;
- cleanup через MCP видалив тимчасовий canvas, material asset більше
  не знаходиться в asset search;
- post-clean MCP marker має 0 critical/warning console issues.

Додатковий MCP smoke для asset refs:

- через `UniBridge_ManageAsset Search` знайдено package sprite/texture
  assets;
- `SetGraphic SpritePath` успішно призначив sprite:
  `Packages/com.unity.2d.animation/Editor/Assets/ComponentIcons/Animation.IKCCD On.png`;
- `SetGraphic TexturePath` успішно створив `RawImage` і призначив
  texture:
  `Packages/com.unity.2d.animation/Editor/Assets/ComponentIcons/Animation.IKCCD On@32.png`;
- `Inspect` підтвердив `Image.sprite` і `RawImage.texture`;
- cleanup і post-clean console marker чисті.

Окремо перевірено `ClearButtonEvents` через MCP:

- перед clear: `persistentListenerCount=1`;
- після `ClearButtonEvents`: `persistentListenerCount=0`;
- cleanup і post-clean console marker чисті.

## 2026-05-11: UI templates / screen-level composition

Після повторного UI validation smoke користувач нагадав робоче правило:
перед новими Unity/UI фічами дивитись у розпаковану reference implementation source за шляхом
`reference implementation source` і
портувати хороші рішення в архітектуру UniBridge.

Для UI templates у reference implementation не знайдено готових прямих action-ів
`CreateHUD/CreateModal/CreateCardGrid`. Корисними виявились нижчі ідеї:
`SetRectTransformLayoutAction`, UI component serializers для `Canvas`,
`CanvasGroup`, `LayoutElement`, `ScrollRect`, `TextMeshProUGUI`,
layout groups і capture/validation-friendly UI state. У UniBridge це
перенесено як високорівневий composition layer поверх уже наявних
UI primitives.

Додано в source package:

- `UIAction.CreateTemplate`;
- `UITemplateType`: `Panel`, `Modal`, `Toolbar`, `List`, `CardGrid`, `HUD`;
- template params:
  `Title`, `Subtitle`, `ActionTexts`, `Columns`, `UseTextMeshPro`,
  `ValidateAfterCreate`;
- templates будуються через Canvas UI primitives, layout groups,
  `LayoutElement`, ScrollRect/GridLayoutGroup і TMP text/buttons, якщо TMP
  доступний;
- `ValidateAfterCreate=true` за замовчуванням, відповідь повертає
  `validation` summary для створеного template root;
- `UniBridge_BatchActions` отримав aliases:
  `create_template`, `template`, `create_panel`, `create_modal`,
  `create_toolbar`, `create_list`, `create_card_grid`, `create_hud`;
- alias-и на кшталт `action=create_card_grid` автоматично нормалізуються
  в `Action=CreateTemplate` і `TemplateType=CardGrid`;
- документацію `Documentation~/unibridge.md` і `_upm.changelog` оновлено.

Live MCP smoke у `UniBridge_Test_Project`:

- source files синхронізовано в embedded package тестового проєкту;
- `dotnet build Cidonix.UniBridge.MCP.Editor.csproj --no-restore`
  завершився з 0 errors; лишився старий warning `MSB3277` про
  `System.Threading.Tasks.Extensions` reference conflict між Unity/Roslyn;
- через MCP виконано `Assets/Refresh` і reconnect;
- `tools/list` підтвердив `UniBridge_ManageUI` з `Action=CreateTemplate`
  і всіма 6 `TemplateType`;
- створено тимчасовий canvas
  `UniBridge_TemplateSmoke_Canvas_20260511_200253`;
- через `UniBridge_ManageUI CreateTemplate` створено `Panel`, `Modal`,
  `Toolbar`, `List`, `CardGrid`, `HUD`;
- кожен template root повернув `validation.summary.issueCount=0`,
  `warningCount=0`, `errorCount=0`;
- `UniBridge_BatchActions` dry-run з alias `action=create_card_grid`
  пройшов: 1 validated, 0 validation errors/warnings;
- cleanup через MCP видалив тимчасовий canvas, фінальний MCP find повернув
  порожній список;
- Unity Console diagnostic summary після marker: 0 critical/warning issues,
  recent samples порожні.

## 2026-05-11: UI validation / Audit expansion

Після ScrollRect/TextMeshPro/UI tooling додано reference-informed UI validation шар у `UniBridge_ManageUI`.

У reference implementation для цього корисними виявились не окремі старі action-и, а component intelligence навколо UI: `RectTransform`, `Canvas`, `CanvasGroup`, `CanvasRenderer`, `CanvasScaler`, `LayoutGroup`, `ContentSizeFitter`, `TextMeshProUGUI`. Звідти перенесено концепцію, що UI треба валідити як по геометрії, так і по rendering/visibility contract.

Зміни у source package:

- додано явну read-only дію `Validate`, яка використовує той самий audit pipeline, що й `Audit`, але зручніша як post-generation gate;
- `Audit` тепер ловить invisible/hidden проблеми: inactive UI, zero local scale, disabled `Graphic`, alpha 0 на `Graphic`, alpha 0 на `CanvasRenderer`, hiding через `CanvasGroup`;
- `AuditCanvas` перевіряє disabled Canvas, `ScreenSpaceCamera` без `worldCamera`, low/negative sorting order, і конфлікти кількох root/override-sorted Canvas з однаковим sorting layer/order;
- audit response тепер має `summary.issueCodes`, щоб агент швидко бачив групи проблем без перечитування всіх entries;
- `RepairPlan` отримав manual recommendations для нових validation codes;
- `UniBridge_BatchActions` отримав aliases `validate`, `validate_ui`, `validateui`, `validation`;
- виправлено bug у `BatchActions.NormalizeAction`: fallback на `action` тепер використовується тільки для canonical `Action`, щоб `action=validate` не засмічував `ElementType`, `ScrollDirection`, `MovementType`.

Live smoke у `UniBridge_Test_Project`:

- створено тимчасовий зламаний `UniBridge_UIValidation_Test_Canvas`;
- `Validate` коректно знайшов `TEXT_OVERFLOW_RISK`, `SIBLING_OVERLAP`, `ZERO_SCALE`, `INVISIBLE_GRAPHIC_ALPHA`, `LOW_CANVAS_SORTING_ORDER`, `MANUAL_CONTAINER_LAYOUT`;
- додатково перевірено `UI_HIDDEN_BY_CANVAS_GROUP` через `CanvasGroup.alpha=0`;
- перевірено `UniBridge_BatchActions` dry-run alias `action=validate`;
- після чистих перевірок Unity Console після marker має 0 warnings/errors/exceptions;
- тимчасовий validation canvas прибрано з тестової сцени.

Повторний live MCP smoke 2026-05-11 19:35:

- тест виконано саме через `unibridge_relay_win.exe --mcp --project-id ae4e323353aa487f9ea74d566b38eaac`;
- relay підключився до живого `UniBridge_Test_Project`, Unity Editor PID `21060`, `tools/list` повернув `UniBridge_ManageUI` з `Action=Validate`;
- створено тимчасовий canvas `UniBridge_UIValidation_LiveSmoke_20260511_193419` і навмисно зламані UI елементи;
- `Validate` повернув 8 findings з кодами:
  `TEXT_OVERFLOW_RISK`, `INVISIBLE_GRAPHIC_ALPHA`,
  `MANUAL_CONTAINER_LAYOUT`, `SCREEN_SPACE_OVERLAY_CAPTURE_CAVEAT`,
  `SIBLING_OVERLAP`, `UI_HIDDEN_BY_CANVAS_GROUP`, `ZERO_SCALE`;
- `UniBridge_BatchActions` dry-run з alias `tool=ui`, `action=validate`
  пройшов: 1 validated, 0 validation errors/warnings;
- окремий sorting smoke на canvas
  `UniBridge_UISorting_LiveSmoke_20260511_193457` із `SortingOrder=-1`
  підтвердив `LOW_CANVAS_SORTING_ORDER`;
- cleanup через MCP видалив тимчасові canvases;
- фінальний MCP find по prefixes `UniBridge_UIValidation_LiveSmoke_20260511_`
  і `UniBridge_UISorting_LiveSmoke_20260511_` повернув 0 об'єктів;
- Unity Console diagnostic summary після smoke: 0 critical/warning issues,
  recent samples порожні.

## 2026-05-11: UI / RectTransform helper start

Після Editor snapshot/restore почато reference-informed UI / RectTransform helper. У старому reference implementation знайдено action `setRectTransformLayout`, який переносив ідею Unity anchor presets у структурований запит: горизонтальні режими `Left/Center/Right/Stretch`, вертикальні `Top/Middle/Bottom/Stretch`, опційне встановлення pivot і контроль того, чи зберігати візуальну позицію під час зміни anchors.

У source package додано `UniBridge_ManageUI`:

- `Inspect` повертає RectTransform anchors, pivot, offsets, size, world corners, Canvas, parent і children;
- `CreateCanvas` створює Canvas з CanvasScaler і GraphicRaycaster;
- `EnsureEventSystem` створює або ремонтує EventSystem з input module,
  сумісним із налаштуваннями проєкту;
- `CreateElement` створює Empty/Panel/Image/Text/Button UI objects;
- `SetRectTransformLayout` застосовує reference-informed layout presets без ручної математики anchors;
- `SetRectTransform` напряму задає anchors, pivot, anchored position, size, offsets і local scale;
- `DryRun` підтримано для create/layout/direct rect actions.

Додано dependency/reference на `com.unity.ugui` / `UnityEngine.UI`, бо helper працює з Canvas, Image, Text, Button, CanvasScaler, GraphicRaycaster і EventSystem. `UniBridge_BatchActions` отримав allow-list entry `UniBridge_ManageUI`, aliases `ui`, `rect_transform`, `recttransform`, `canvas`, `layout`, validation і parameter normalization. Документацію `Documentation~/unibridge.md` доповнено секцією `Work With UI And RectTransforms`.

Локальна compile-перевірка source package виконана через Unity-generated `Cidonix.UniBridge.MCP.Editor.rsp` з `UniBridge_Test_Project`, підставивши source package і нові файли `ManageUI.cs` / `ManageUIParams.cs`; `dotnet csc` завершився без помилок.

Під час живого UI smoke у тестовому проєкті додатково знайдено нюанс camera-space Canvas: scene objects можуть візуально заважати UI capture, якщо Canvas не має явного sorting order. `CreateCanvas` тепер підтримує `SortingOrder` і `OverrideSorting`; за замовчуванням `overrideSorting=true`, `sortingOrder=100`, щоб AI-created UI передбачувано рендерився поверх звичайного scene content.

Після першого dashboard smoke користувач справедливо помітив, що тестовий UI вийшов кривуватий: labels/values і progress bars накладались або вилазили за межі картки. Тестовий UI вручну виправлено через MCP: card розширено, labels/values рознесено по колонках, проценти винесено праворуч, кнопки вирівняно. Фінальний capture `unibridge_ui_dashboard_readable.png` читабельний і Unity Console має 0 warnings/errors/exceptions. З цього зроблено висновок для самого tool-а: `UniBridge_ManageUI` отримав `FontSize`, `Alignment`, `BestFit`, `MinFontSize`, `MaxFontSize`, а `UniBridge_BatchActions` отримав aliases для цих параметрів.

## 2026-05-11: Editor snapshot/restore

Після Prefab override intelligence почато reference-informed Editor snapshot/restore. У старому reference implementation знайдено концепцію `EditorSnapshot`: capture/restore loaded scenes, active scene, Scene View camera, selection, Prefab Stage, focused window і active tool. У UniBridge перенесено саме концепцію, але у локальному MCP-native вигляді без старої reference implementation action-системи.

Додано `UniBridge_EditorSnapshot`:

- `Capture` зберігає snapshot editor workspace у JSON;
- `List`, `Inspect`, `Delete`, `Clear` керують persisted snapshots;
- `Restore` підтримує `DryRun` і частковий restore сцен, Scene View, selection, Prefab Stage, active tool і focused window;
- snapshots пишуться у `<project>/Library/UniBridge/EditorSnapshots`, тобто не потрапляють у пакет/репозиторій користувача;
- restore блокує небезпечні випадки: compile/update, play mode для scene/prefab restore, dirty scene reload без явного `SaveDirtyScenes` або `AllowDirtySceneReload`;
- window restore навмисно консервативний: фокусує відомий тип вікна і опційно повертає maximized state, але не переписує Unity layout.

`UniBridge_BatchActions` отримав allow-list/aliases для `editor_snapshot`, `editorsnapshot`, `editor_state`, `workspace_snapshot` і dry-run validation. Документацію `Documentation~/unibridge.md` оновлено секцією `Preserve Editor Workspace State`.

Live smoke у `UniBridge_Test_Project` виконано напряму через `unibridge_relay_win.exe --mcp`: `tools/list` бачить `UniBridge_EditorSnapshot`; `Capture`, `List`, `Inspect`, `Restore` dry-run, частковий реальний `Restore` і `Delete` успішні. Після першого smoke виправлено Unity 6 warning: у 2D Scene View restore більше не ставить rotation, бо Unity фіксує його в identity. Фінальний smoke має 0 warnings/errors/exceptions у Unity Console.

## 2026-05-11: Prefab override intelligence

Після Animator Controller tooling почато reference-informed prefab diff/overrides шар. У старому reference implementation `SceneSerializer` було знайдено корисну концепцію серіалізації prefab instances як посилання на prefab asset плюс modifications, added/removed components і added/removed GameObjects. У UniBridge переноситься саме ця ідея, але в MCP-native вигляді.

Додано helper `PrefabOverrideUtility`:

- будує AI-readable prefab override diff для prefab instance root;
- групує overrides на `property`, `object`, `added_component`, `removed_component`, `added_game_object`, `removed_game_object`;
- повертає stable-for-current-state `id` для кожного override;
- повертає compact summary, counts, warnings і variant chain;
- підтримує selected apply/revert по `override_id`, `override_ids`, `override_kind`, `object_path`, `component_type`, `property_path`;
- підтримує `dry_run` для selected apply/revert;
- підтримує cleanup через `PrefabUtility.RemoveUnusedOverrides`.

`UniBridge_ManagePrefab` розширено action-ами:

- `diff_overrides` / `list_overrides` / `inspect_overrides`;
- `apply_override` / `apply_selected_overrides`;
- `revert_override` / `revert_selected_overrides`;
- `remove_unused_overrides` / `cleanup_overrides`.

`UniBridge_BatchActions` отримав validation для нових prefab override actions і aliases для відповідних параметрів. Документацію `Documentation~/unibridge.md` оновлено в секції `Work With Prefabs`.

Після перенесення в `UniBridge_Test_Project` виправлено два compile edge cases у `PrefabOverrideUtility`: `hasOverrides` більше не читає anonymous counts як `object.total`, а `objectInstanceId` переведено на `long`, щоб відповідати Unity 6 `EntityId` через `UnityApiAdapter.GetObjectId`.

Live smoke у `UniBridge_Test_Project` виконано напряму через `unibridge_relay_win.exe --mcp`:

- `tools/list` бачить `UniBridge_ManagePrefab`;
- створено тестовий prefab asset `Assets/UniBridgeManual/Prefabs/UB_Override_Source_20260511_031305.prefab`;
- створено prefab instance і навмисно зроблено overrides: property/object override, added component, removed component, added GameObject;
- `diff_overrides` повернув 6 non-default override entries, 11 default overrides були відфільтровані;
- dry-run selected `revert_override` / `apply_override` пройшов для property, added component, removed component і added GameObject;
- реальні selected mutations пройшли для property revert, added component apply, removed component apply і added GameObject revert;
- фінальний diff коректно зійшовся до 2 overrides;
- `remove_unused_overrides` dry-run виконався без помилок;
- Unity Console після тестів має 0 warnings/errors/exceptions.

## 2026-05-11: Animator Controller tooling

Після Script Intelligence почато Animator Controller tooling.

Оглянуто старий власний reference implementation Unity Plugin v0.85.2 на предмет Animator-підходу. У DLL знайдено концепцію `CreateOrUpdateAnimatorControllerAction` з DTO-подібними описами шарів, параметрів, станів, переходів, state machines і blend trees. У UniBridge переноситься саме концепція структурованої роботи з Animator Controller assets, без старої action-системи reference implementation і без прямого YAML-редагування.

Додано `UniBridge_ManageAnimatorController`:

- `search` / `inspect` / `validate` для пошуку, читання і перевірки `.controller` assets;
- `create` для створення Animator Controller assets під `Assets/`;
- `add_parameter` / `remove_parameter`;
- `add_layer` / `remove_layer`;
- `add_state` / `remove_state`;
- `set_state_motion` / `set_default_state`;
- `add_transition` / `remove_transition`, включно з Any State transitions і conditions.

Інструмент працює через `UnityEditor.Animations` API. `UniBridge_BatchActions` отримав allow-list entry, alias `animator_controller` і dry-run validation для Animator Controller кроків.

Перший шар був навмисно нижчорівневий і передбачуваний; поверх нього далі додано структурований full graph workflow.

Live smoke у `UniBridge_Test_Project` пройшов успішно через UniBridge relay JSON-RPC: створено `.controller`, додано/видалено параметри, layer, states, default state, state transitions, Any State transition і Exit transition; фінальна `validate` повернула `valid=true`, `issueCount=0`. `UniBridge_BatchActions` dry-run з alias `animator_controller` пройшов 3 steps без validation errors/warnings. Unity Console після smoke має 0 warnings/errors/exceptions.

Наступним проходом додано BlendTree tooling у той самий `UniBridge_ManageAnimatorController`:

- `create_blend_tree` — створює або перевикористовує BlendTree motion для Animator state;
- `configure_blend_tree` — налаштовує `blendType`, `blendParameter`, `blendParameterY`, thresholds;
- `add_blend_child` — додає один child motion або placeholder child;
- `set_blend_children` — замінює весь children list;
- `clear_blend_children` — очищає BlendTree children.

`inspect` тепер розгортає BlendTree як Motion snapshot з типом, параметрами, thresholds і children. `validate` перевіряє відсутні BlendTree parameters, порожні trees і direct blend child parameters. `UniBridge_BatchActions` отримав dry-run validation для BlendTree actions.

Live smoke у `UniBridge_Test_Project` пройшов успішно: створено `UniBridge_BlendTreeSmoke_20260510224623.controller`, додано `Speed`, `DirectionX`, `DirectionY`, створено `Locomotion` state з `Simple1D` BlendTree, переконфігуровано в `FreeformCartesian2D`, перевірено `add_blend_child`, `clear_blend_children`, `set_blend_children`, `inspect`, `validate` і batch dry-run. Фінальний BlendTree має `childCount=5`, `valid=true`, `issueCount=0`; Unity Console після smoke має 0 warnings/errors/exceptions.

Додано `apply_graph` у `UniBridge_ManageAnimatorController` як high-level create/update action поверх примітивів:

- один структурований запит описує `parameters`, `layers`, `states`, `motion_path` або `blend_tree`, `default_state`, layer-level transitions, state-level transitions і Any State transitions;
- `dry_run=true` повертає planned change report без змін в assets;
- за замовчуванням controller створюється, якщо відсутній (`create_if_missing=true`);
- `replace_transitions=true` робить repeated apply ідемпотентним: matching transitions замінюються, а не дублюються;
- pruning вимкнений за замовчуванням і вмикається тільки явно через `remove_missing_parameters`, `remove_missing_layers`, `remove_missing_states`;
- graph preflight виконується до будь-яких asset changes, щоб invalid graph не залишав частково створений controller;
- transitions без `has_exit_time=true` і без `conditions` відхиляються, бо Unity ігнорує такі transitions і пише warning у Console.

`UniBridge_BatchActions` навчився dry-run validation для `apply_graph`, включно з перевіркою parameter/layer/state refs, BlendTree refs і ignored-transition guard.

`package.json` `_upm.changelog` оновлено під `apply_graph` і batch graph validation.

Перевірка:

- source compile через Unity-generated `Cidonix.UniBridge.MCP.Editor.rsp` у `UniBridge_Test_Project` — exit code 0;
- live JSON-RPC smoke через `unibridge_relay_win.exe --mcp --project-id ae4e323353aa487f9ea74d566b38eaac`;
- `apply_graph dry_run` для нового controller повернув planned changes;
- `apply_graph` створив controller `Assets/UniBridgeAnimatorFixtures/UniBridge_GraphSmoke_Final_20260510231622.controller`;
- повторний `apply_graph` на той самий граф не створив duplicate issues;
- update-граф додав `Fall` parameter/state/transitions;
- фінальний `validate`: `valid=true`, `issueCount=0`;
- batch dry-run з `animator_controller apply_graph`: 1 validated, 0 validation errors/warnings;
- invalid transition без exit time/conditions коректно зловлено і direct `apply_graph`, і batch validation;
- direct invalid graph не залишив controller asset після preflight failure;
- Unity Console після smoke і cleanup має 0 warnings/errors/exceptions.

## 2026-05-10: Script Intelligence start

Після Asset Intelligence і asset snapshot/serializer почато Script Intelligence шар для UniBridge.

Додано `UniBridge_ScriptIntelligence` як read-only MCP tool для орієнтації агента перед редагуванням C#:

- `Catalog` — список MonoScript assets і compiled types з path, GUID, kind, assembly, base type, Unity callbacks, Inspector-facing fields;
- `Analyze` — детальний розбір одного script за path/GUID/type/query;
- `ReadTypes` — reference implementation source/AutoBot-inspired сценарій отримання source + summary для конкретних типів;
- `References` — bounded пошук по C# source;
- `Usages` — пошук prefab/scene/asset usages через script GUID;
- `Hotspots` — TODO/FIXME, file/class mismatch, obsolete Unity APIs, великі файли, UnityEditor у runtime Assets-папках;
- `Assemblies` — огляд Unity compilation assemblies і asmdefs;
- `Selection` — аналіз вибраних MonoScript assets;
- `Metrics` — агрегація по kind/assembly/folder/Unity callbacks.

`UniBridge_BatchActions` отримав read-only allow-list entry `UniBridge_ScriptIntelligence` і aliases `script_intelligence`, `scriptintel`, `script_search`.

Source compile перевірено через Unity-generated `Cidonix.UniBridge.MCP.Editor.rsp` з доданими новими source files; `dotnet csc` завершився з exit code 0.

Після першого live smoke у тестовому Unity-проєкті виявлено зависання Editor під час broad `Catalog`.
Причина: `AnalyzeSource` ще використовував старий whole-file regex pass для пошуку типів, полів,
properties і methods, хоча regex-поля вже були підготовлені як line regex.

Виправлено:

- `Catalog` тепер легкий за замовчуванням: `IncludeMembers=false`, member-summary треба вмикати явно;
- `AnalyzeSource` повністю переведено на bounded line-by-line parser;
- аналіз джерела обмежено `MaxSourceAnalysisChars=400000`, наддовгі рядки пропускаються;
- `MaxScanScripts` тепер обмежує реально переглянуті `MonoScript` assets, а не тільки кількість результатів після фільтрів;
- виправлено `SortRecords`: LINQ sort materialize-иться перед `records.Clear()`, тому `Catalog` більше не обнуляє результати;
- parser тепер бачить звичайні multi-line method declarations (`void Awake()` + `{` на наступному рядку), expression-bodied properties і inline attributes на кшталт `[SerializeField] float speed`;
- source compile-check після hardening пройшов через Unity-generated `Cidonix.UniBridge.MCP.Editor.rsp`.

## 2026-05-10: AutoBot-inspired Asset Snapshot / Serializer

Після переносу reference-informed Asset Intelligence додано наступний шар з UnityAutoBot: asset snapshot / serializer, але в UniBridge-native вигляді без старого RPC/action system.

`UniBridge_AssetIntelligence` розширено діями:

- `Serialize` — повертає bounded upload-style envelopes для assets;
- `Snapshot` — alias того ж механізму, коли агенту потрібен asset context snapshot.

Додано `AssetSnapshotSerializer`:

- prefab assets у `Standard`/`Full` режимах серіалізуються як hierarchy + components + transforms + bounded `SerializedProperty` дані;
- active scene може повертати bounded hierarchy snapshot;
- inactive `.unity` scenes повертаються як контрольований YAML/text payload, щоб не чіпати стан Editor;
- `MonoScript` повертає source text і компактний public interface summary;
- text-like assets (`.prefab`, `.unity`, `.mat`, `.asset`, `.json`, `.asmdef`, `.uss`, `.uxml`, `.cs` та інші) читаються з лімітами;
- generic Unity assets/importers серіалізуються через `SerializedObject` / `SerializedProperty` traversal з лімітами;
- є режими `Minimal`, `Standard`, `Full`, а також bounds `MaxSerializedProperties`, `MaxSerializedDepth`, `MaxSerializedItems`, `MaxTextChars`.

Важливий принцип: це read-only serializer для розуміння assets, а не restore/mutate layer.

Перевірка у `UniBridge_Test_Project`: schema містить `Serialize`/`Snapshot`/`SerializeMode`; prefab `UniBridge_DemoPrefab.prefab` у `Standard` режимі повернув JSON hierarchy з 5 компонентами і serialized properties; `Snapshot Minimal` повернув bounded YAML/text; package script `AssetSnapshotSerializer.cs` повернув C# text + interface summary; active `SampleScene.unity` повернула JSON hierarchy з 6 root objects; Unity Console після smoke має 0 warnings/errors/exceptions.

## 2026-05-10: reference-informed Asset Intelligence

Після огляду старого власного reference implementation Unity Plugin v0.85.2 вирішено переносити не старий WebSocket/action код, а концепцію окремого read-only asset intelligence шару для AI-агента.

Додано `UniBridge_AssetIntelligence`:

- `Search` — ranked AssetDatabase search з фільтрами `Query/SearchPattern`, `Types`, `Extensions`, `Labels`, `Path/Paths`, pagination і sort modes;
- `Inspect` — детальна інформація про asset: path, GUID, type, labels, bundle, size, modified time, importer metadata, optional sub-assets, dependencies, dependents, preview;
- `Read` — bounded читання text-like assets (`.cs`, `.prefab`, `.unity`, `.mat`, `.asset`, `.json`, `.asmdef`, `.uss`, `.uxml` та інші) з line/head/tail/pattern slicing;
- `Dependencies` / `Dependents` — залежності і assets, які посилаються на ціль;
- `Stats` / `Types` — project asset overview по типах, extension, folders, largest/recent files;
- `Selection` — inspection assets, виділених у Project view;
- `Preview` — PNG preview через Unity `AssetPreview`, output під `~/.unibridge/asset-previews/<project>`.

`UniBridge_BatchActions` розширено read-only `UniBridge_AssetIntelligence` step і aliases `asset_intelligence`, `assetintel`, `asset_search`, щоб batch workflow міг спочатку знайти/перевірити assets, а потім виконувати зміни іншими tools.

Після першого live smoke-test у тестовому Unity-проєкті уточнено пошук:

- `Types: ["Prefab"]` тепер трактується як людський alias для `.prefab`, бо Unity вантажить prefab asset як `GameObject`;
- extension-only пошук тепер використовує повний asset path scan, щоб надійніше знаходити assets у `Packages/...`, а не покладатися тільки на `AssetDatabase.FindAssets("")`.

Перевірка:

- editor assembly перевірено через Unity-generated `Cidonix.UniBridge.MCP.Editor.rsp`, підставивши source package з `H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge`;
- `dotnet csc` завершився з exit code 0.
- live smoke у `UniBridge_Test_Project`: `Stats`, `Types`, `Search` (`Types: ["Prefab"]`, `Extensions: ["cs"]` у `Packages/com.cidonix.unibridge`), `Inspect`, `Read`, `Dependencies`, `Dependents`, `Selection`, `Preview` і `UniBridge_BatchActions` aliases пройшли успішно; Unity Console після фінальної перевірки має 0 warnings/errors/exceptions.

## 2026-05-10: Vision / Capture Tools start

Почато перший шар Vision tooling для UniBridge.

Додано `UniBridge_CaptureView`:

- `CaptureSceneView` рендерить Scene View camera у PNG і може явно центрувати/фреймити target object перед знімком;
- `CaptureGameCamera` рендерить `Camera` через `RenderTexture` у PNG;
- `ListCameras` повертає список доступних камер з ID, hierarchy path і базовими параметрами;
- дефолтний output: `~/.unibridge/captures/<project>`;
- відповідь містить абсолютний шлях, `fileUri`, розміри, active scene, project identity, camera/target metadata;
- capture не читає пікселі робочого столу, тому Unity не мусить бути foreground-вікном, коли користувач чекає роботу агента поверх інших програм.
- старий `CaptureGameViewParams.cs` лишено як deprecated compatibility stub, бо Unity AssetDatabase може ще мати stale reference на цей файл після оновлення embedded package; runtime tool вже використовує `CaptureViewParams`.
- `UniBridge_CaptureView` переведено на ручний `JObject` parameter parser з custom schema, щоб стабільно приймати `Action/action`, `SearchMethod/search_method`, `FileName/file_name` та інші варіанти іменування від різних MCP clients.
- Scene View capture тепер тимчасово виходить з `in2DMode` перед застосуванням Iso/Front/Top ракурсів і відновлює попередній режим після знімка, щоб не плодити Unity 6 warning про rotation у 2D mode.
- Capture responses містять `request` metadata з реально застосованими параметрами, щоб агент міг одразу звірити requested vs captured без повторного читання контексту.
- PNG capture за замовчуванням пишеться opaque, щоб `CaptureGameCamera` збігався з візуальним Game View і показував колір фону камери. Прозорий PNG лишився доступним тільки через явний параметр `TransparentBackground`.
- `UniBridge_CaptureView` розширено у повніший AI Vision шар: додано `CaptureSelection`, `CaptureObject`, `CapturePrefabStage`, `CaptureSceneOverview`, `CaptureAroundObject`, `CaptureSeries`, `CaptureDiff`, `ClearCaptures`; додано optional overlay з bounds/labels, visible/nearby object metadata, PNG diff heatmap і cleanup capture-файлів.
- Overlay для target-based captures підписує тільки ціль, а сусідні об'єкти лишає як bounds у кадрі плюс JSON metadata, щоб скрін залишався читабельним для агента.
- Додано `SeparateOverlay`: основний PNG залишається чистим, bounds/labels пишуться окремим прозорим overlay layer, а для швидкого перегляду додатково створюється composite PNG.
- `SeparateOverlay` перевірено на тестовому проекті: створюються clean, overlay і composite PNG; консоль Unity лишається чистою.
- Overlay placement покращено для агента: біля bounds лишаються numeric markers з відступом від рамок, повні назви збираються в color-coded non-overlapping legend, а `annotations` metadata зв'язує marker з повною назвою, objectId, path і bounds.

Пакет переведено на стабільну semver-версію `0.1.0` без prerelease suffix, щоб Unity Package Manager показував UniBridge без увімкнення prerelease/preview пакетів.

## Основні шляхи

- Новий/тестовий пакет: `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant`
- Старіший тестовий пакет/проєкт для порівняння: `H:/Repos/UnityRepos/DomovykPrototype`
- Цільовий новий проєкт для продовження: `H:/Repos/Cidonix/UniBridge`
- Контекстний файл: `H:/Repos/Cidonix/UniBridge/UniBridge_Context.md`

## Стан UniBridge на старті

На 2026-05-05 у `H:/Repos/Cidonix/UniBridge` перенесено:

- `UniBridge_Context.md` - головний переносний контекст проєкту.
- `com.unity.ai.assistant` - Unity package `com.unity.ai.assistant` версії `2.7.0-pre.1`.

У цій директорії створено Plastic SCM workspace/repository:

```text
/main@UniBridge/UniBridge@alexander-shvets@unity
```

## 2026-05-06: MCP-only preview package pass

Поточне рішення: для внутрішньої перевірки ідеї робимо UniBridge як безкоштовний embedded/package-first продукт без Hub, інсталятора, активації та комерційного licensing. Мета цього етапу — довести local MCP workflow, toolbar indicator, relay installation/configuration і взаємодію зовнішніх AI coding agents з Unity.

Safety-first підхід:
- нічого legacy не видаляємо фізично;
- старі chat/generator assemblies вимикаємо через `defineConstraints`;
- старий код лишається донором логіки в репозиторії;
- якщо треба буде повернути частину старої функціональності для аналізу, це простіше зробити через define/точковий revert, ніж відновлювати багато файлів з історії Plastic.

Зроблений перший зріз:
- package version піднято до `0.1.0-pre.2`;
- вимкнено legacy chat UI assembly `Unity.AI.Assistant.UI.Editor`;
- вимкнено public chat API, annotations, profiler/sample assistant integrations;
- вимкнено legacy asset generation assemblies: Animate, Image, Mesh, PBR, Sound, ModelSelector, Generators.*, generation context menus/object picker, search/HF downloader;
- вимкнено legacy `Unity.AI.Assistant.Tools.Editor` assembly;
- MCP adapter для старих `[AgentTool]` tools і `Unity.RunCommand` додатково загорнуто в `UNIBRIDGE_LEGACY_ASSISTANT_TOOLS`, щоб у default preview MCP показував тільки native MCP tools;
- active MCP asmdef більше не посилається на disabled Assistant Tools, AssetGenerators або Toolkit Accounts assemblies;
- перевірено скриптом: active asmdefs не мають посилань на disabled legacy asmdefs.

Хмарний/relay runtime:
- `RelayService` більше не запускає старий `--relay` cloud/gateway процес автоматично;
- `RelayService.StartAsync()` у MCP-only режимі ігнорує запит старту legacy cloud relay;
- `AccessTokenRefreshUtility` більше не refresh-ить Unity cloud access token;
- `AssistantPackageAutoUpdater` більше не перевіряє Unity registry updates для старого Assistant;
- старий relay-managed MCP client service (`Editor/Assistant/MCP/McpInitializer.cs`) вимкнено;
- account dropdown `Open Assistant` hook у Toolkit Accounts вимкнено, щоб не провокувати встановлення/відкриття старого `com.unity.ai.assistant`.

Збережені речі:
- `Cidonix.UniBridge.MCP.Editor` і `Unity.AI.MCP.Runtime`;
- toolbar indicator UniBridge green/red;
- MCP Project Settings;
- relay installer для `--mcp`;
- discovery pipe/files для відкритих Unity Editor instances;
- старий `RelayService` клас лишається в коді, бо `Bridge.cs` ще має gateway/ACP compatibility references, але runtime cloud auto-start заблокований.

Важливий нюанс:
Це ще не повне фізичне видалення cloud/chat source. Це перший безпечний MCP-only preview pass. Наступний етап — поступово відв'язати `Cidonix.UniBridge.MCP.Editor` від `Unity.AI.Assistant.Editor`/ACP/analytics, після чого можна буде вимкнути або винести ще глибший legacy Assistant core.

## 2026-05-06: Subaquatica embedded package compile fix

Після додавання `com.cidonix.unibridge` як embedded package у `H:/Repos/UnityRepos/Subaquatica/Packages/com.cidonix.unibridge` Unity показала compile errors у `Unity.AI.Toolkit.Asset` і `Unity.AI.Toolkit.Accounts`:

- `EditorTask` не знаходився у `TemporaryAssetUtilities.cs`;
- extension method `ConfigureAwaitMainThread(...)` не знаходився у `FileIO.cs`;
- `Unity.AI.Toolkit.Accounts` потім не бачив `Unity.AI.Toolkit.Asset.AssetReference`, бо `Unity.AI.Toolkit.Asset` не компілювався.

Фактичні причини були дві:

- у двох source files не було `using Unity.AI.Toolkit;`, де оголошені `EditorTask` і extension methods;
- в embedded-копії Subaquatica Unity regenerated `.meta` GUID-и для asmdef-ів, а активні asmdef references все ще посилалися на GUID-и з source package. Через це `Unity.AI.Toolkit.Asset` фактично компілювався без reference на `Unity.AI.Toolkit.Async`, а `Unity.AI.Toolkit.Accounts` — без reference на `Unity.AI.Toolkit.Asset`.

Зроблений фікс:

- додано `using Unity.AI.Toolkit;` у `Modules/Unity.AI.Toolkit.Asset/TemporaryAssetUtilities.cs`;
- додано `using Unity.AI.Toolkit;` у `Modules/Unity.AI.Toolkit.Asset/FileIO.cs`;
- `EditorTask` у `Modules/Unity.AI.Toolkit.Async/EditorTask.cs` зроблено `public static`, бо це shared toolkit API;
- активні references у `Unity.AI.Toolkit.Asset.asmdef` і `Unity.AI.Toolkit.Accounts.asmdef` переведено з `GUID:...` на assembly names: `Unity.AI.Toolkit.Async` і `Unity.AI.Toolkit.Asset`;
- ті самі runtime-фікси тимчасово внесено і в embedded-копію пакета в Subaquatica, бо Unity компілювала саме її.

Перевірка:

- вручну прогнано Unity-generated compiler response file для `Unity.AI.Toolkit.Async`;
- вручну прогнано Unity-generated compiler response file для `Unity.AI.Toolkit.Asset` — без errors;
- вручну прогнано Unity-generated compiler response file для `Unity.AI.Toolkit.Accounts` — без errors, лишилися warnings `CS0162` про unreachable code у вимкненому старому Assistant menu;
- legacy USS/UXML warnings зі старими paths `Packages/com.unity.ai.assistant/...` лишаються окремим cleanup після глибшого вимкнення legacy UI/resources.

## 2026-05-06: MCP editor rename to UniBridge namespace

На прохання користувача MCP editor assembly/module перейменовано на `Cidonix.UniBridge.MCP.Editor`.

Зроблено:

- замінено namespace/usings/assembly references/API docs у source package `H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge`;
- перейменовано фізичний модуль `Modules/Cidonix.UniBridge.MCP.Editor`, його `.meta`, `Cidonix.UniBridge.MCP.Editor.asmdef`, `.asmdef.meta`, `.api` і `.api.meta`;
- ті самі зміни дзеркально внесено в embedded-копію Subaquatica `H:/Repos/UnityRepos/Subaquatica/Packages/com.cidonix.unibridge`;
- перевірено `rg`: старого dotted Unity MCP editor id більше немає ні в тексті, ні в шляхах поточного UniBridge repo/package;
- Unity згенерувала новий `Cidonix.UniBridge.MCP.Editor.rsp`, ручний `csc` прогін пройшов без errors; лишилися тільки `CS0618` warnings про obsolete Unity API (`GetInstanceID`, `FindObjectsSortMode`, `Collider2D.usedByComposite`);
- після compile fixes у Subaquatica Unity перестала показувати помилки, а toolbar indicator `UB` з червоною точкою з'явився в головному toolbar.

## 2026-05-06: Unity 6.4 warnings and legacy cloud account calls

Після успішної компіляції Subaquatica ще показала дві категорії повідомлень:

- package-side `CS0618` warnings у `Cidonix.UniBridge.MCP.Editor` через `Object.GetInstanceID()`, `FindObjectsSortMode` і `Collider2D.usedByComposite`;
- runtime `ApiNoLongerSupported` logs з `AccountApi` на `https://generators.ai.unity.com` для `GetPointsBalance` і `GetSettings`.

Рішення:

- для Unity 6.3+ MCP editor тепер використовує `EntityId.FromULong`, `EntityId.ToULong`, `Object.GetEntityId()` і overload `Object.FindObjectsByType(Type, FindObjectsInactive)`, що прибирає warnings у Unity 6.4;
- для `Collider2D` у Unity 6.3+ поле `usedByComposite` у відповіді обчислюється через `compositeOperation != None`, додатково повертається `compositeOperation`;
- legacy Unity account cloud refresh вимкнено в default UniBridge build через `AccountApi.CloudRequestsEnabled == false`; повернути стару поведінку можна тільки define-ом `UNIBRIDGE_LEGACY_CLOUD_ACCOUNTS`;
- `SettingsState` і `PointsBalanceState` у MCP-only режимі отримують локальні дефолти без мережевих запитів; `AccountController` не підписується на auto-refresh;
- ті самі зміни дзеркально внесено в embedded-копію Subaquatica.

Перевірка:

- ручний `csc` прогін Unity response file `Unity.AI.Toolkit.Accounts.rsp` у Subaquatica — exit code 0, без warnings;
- ручний `csc` прогін `Cidonix.UniBridge.MCP.Editor.rsp` — exit code 0, без warnings;
- warnings у `Assets/Bitgem/.../WaterVolumeBase.cs` лишаються поза пакетом UniBridge і не належать до нашого cleanup.

## 2026-05-06: Live MCP connection probe from Codex

Проведено реальну пробу підключення до відкритого `Subaquatica` через `relay_win.exe --mcp`.

Результат:

- Unity Editor process: PID `74184`;
- активний UniBridge discovery file: `C:/Users/Cidonix/.unibridge/mcp/connections/bridge-59f9f213-74184.json`;
- relay встановив named pipe connection до `\\.\pipe\unity-mcp-59f9f213-74184`;
- handshake: `protocol=unity-mcp`, `version=2.0`;
- relay отримав Unity MCP tools і успішно виконав `Unity_ManageEditor` з `Action=GetState`;
- `Unity_ManageScene` з `Action=GetActive` повернув активну сцену `SampleScene` за шляхом `Assets/_Game/Scenes/SampleScene.unity`, build index `0`, loaded, clean, root count `4`;
- `Unity_ManageScene` з `Action=GetHierarchy`, depth `3`, повернув root objects: `Global Light 2D`, `World`, `Player`, `Cameras`;
- Unity toolbar indicator став зеленим, popup показав `Status: Connected`, `Connected clients: 1`.

Toolbar status semantics updated after the live test:

- green dot means UniBridge MCP is ready for local AI clients: package enabled, bridge running, relay installed;
- green can show `Ready` when `Connected clients: 0`, so an idle project is not presented as broken;
- red dot is reserved for actionable states: disabled package, stopped bridge, or missing relay.

## 2026-05-06: UniBridge MCP-only relay rewrite

Started a clean Windows relay rewrite under `com.cidonix.unibridge/RelaySource~/UniBridge.Relay`.

Scope of the new relay:

- supported mode: `unibridge_relay_win.exe --mcp`;
- external protocol: MCP JSON-RPC over stdio, compatible with Codex-style clients;
- internal protocol: newline-delimited JSON over the Unity named pipe from the discovery file;
- discovery path: `~/.unibridge/mcp/connections` only; the temporary `.unity/mcp/connections` fallback was later removed;
- removed from the executable: legacy `--relay` cloud/chat WebSocket server, gateway/ACP provider launcher, credential/preferences bus, and cloud analytics posting.

Build result:

- `dotnet publish` self-contained single-file build succeeded to `C:/Temp/unibridge-relay-build/relay_win.exe`;
- produced exe size: about 37.6 MB, compared to about 119.9 MB for the old Bun relay;
- NativeAOT was attempted but blocked by missing Visual Studio Desktop Development for C++ linker prerequisites on this machine, so NativeAOT remains a hardening step for later.

Live test result:

- launched `C:/Temp/unibridge-relay-build/relay_win.exe --mcp --instance-id 74184 --debug`;
- connected to `\\.\pipe\unity-mcp-59f9f213-74184` through UniBridge discovery;
- received Unity MCP handshake with 19 tools;
- MCP `tools/list` returned the Unity tools;
- MCP `tools/call` for `Unity_ManageScene` / `GetActive` returned `SampleScene` from `Assets/_Game/Scenes/SampleScene.unity`.
- copied the new relay to the package `RelayApp~/relay_win.exe`, to the embedded Subaquatica package, and to `C:/Users/Cidonix/.unibridge/relay/relay_win.exe`;
- installed relay `--version` reports `unibridge-relay` version `1.1.0-build.1`;
- installed relay live test with MCP `tools/list` + `Unity_ManageScene/GetActive` succeeded against the open Subaquatica editor.

Subaquatica verification:

- after recompilation, the toolbar dot stays green while the project is open and UniBridge is ready even with `Connected clients: 0`.
- unsigned relay binaries currently report `Code Signed: No`; `ExecutableIdentityCollector` was adjusted so unsigned Windows executables no longer log noisy `Unable to decode certificate` warnings and instead quietly produce `SignatureValid = false`.
- Windows Authenticode validation now uses `WinVerifyTrust` instead of a raw `X509Chain.Build(cert)` check, because raw X509 validation ignores Authenticode timestamps and can incorrectly mark timestamped executables such as Node.js invalid after their short-lived signing certificate expires.
- expected Windows publisher for the UniBridge MCP server process is now `CN=Cidonix`;
- after `unibridge_relay_win.exe` is Authenticode-signed with a trusted Cidonix certificate, the connection dialog should show `Code Signed: Yes`, `Signature Valid: Yes`, and recognize the relay publisher.

Оновлений нюанс:

- old bundled relay `unity-ai-relay v1.0.12-build.96` only searched the legacy discovery path `C:/Users/Cidonix/.unity/mcp/connections`;
- UniBridge relay initially kept a legacy fallback during migration, but this was intentionally removed on 2026-05-06;
- current UniBridge relay reads only `C:/Users/Cidonix/.unibridge/mcp/connections`;
- current `ServerDiscovery` writes only the UniBridge discovery file and no longer dual-writes to `.unity/mcp/connections`.

## 2026-05-06: UniBridge Windows relay executable renamed

Щоб чітко відрізняти наш MCP-only relay від старого Unity Assistant relay, Windows executable перейменовано з `relay_win.exe` на `unibridge_relay_win.exe`.

Зроблено:

- `RelaySource~/UniBridge.Relay/UniBridge.Relay.csproj` тепер збирає assembly/output name `unibridge_relay_win`;
- help output relay показує `unibridge_relay_win.exe --mcp ...`;
- relay version піднято до `1.1.0-build.2`;
- `RelayApp~/relay.json` оновлено до `1.1.0-build.2`;
- `MCPConstants.InstalledServerMainFile` і `BundledRelayMainFile` для Windows тепер повертають `unibridge_relay_win.exe`;
- `ServerInstaller` копіює `unibridge_relay_win.exe` у `C:/Users/Cidonix/.unibridge/relay` і намагається прибрати legacy `relay_win.exe`, якщо він не залочений старим процесом;
- оновлено package docs і changelog;
- новий exe покладено у source package, embedded Subaquatica package і стабільну installed relay directory.
- локальний `C:/Users/Cidonix/.codex/config.toml` оновлено, щоб `unity_mcp` і `unibridge` MCP entries запускали `C:/Users/Cidonix/.unibridge/relay/unibridge_relay_win.exe`.

Перевірка:

- `dotnet publish` succeeded to `C:/Temp/unibridge-relay-build-renamed/unibridge_relay_win.exe`;
- `unibridge_relay_win.exe --version` reports `UniBridge Relay`, `unibridge-relay`, version `1.1.0-build.2`;
- SHA256 package binary: `07877E5EC8A8B9ED2A2022487E67F5E7012A096E5477873F1973DD14C79BB9D7`.

## 2026-05-06: Relay requestId cache fix

Під час тесту створення gameplay/audio змін через новий MCP relay знайшовся важливий баг:

- кожен новий процес `unibridge_relay_win.exe` починав Unity-side `requestId` з `1`;
- Unity bridge має короткий completed-command cache по `requestId`;
- через це fresh relay process міг отримати cached response від старого relay process, наприклад `Unity_CreateScript` повертав старий `get_available_tools`/`unchanged` payload.

Фікс:

- `UnityConnection` тепер додає до кожного Unity-side `requestId` унікальний prefix `ProcessId-Guid`;
- relay version піднято до `1.1.0-build.3`;
- перебрано і встановлено новий `C:/Users/Cidonix/.unibridge/relay/unibridge_relay_win.exe`;
- source package і embedded Subaquatica package оновлено новим binary/source metadata.

Перевірка:

- після фікса `Unity_CreateScript` через новий relay коректно створив `Assets/_Game/Scripts/SubaquaticaSurfaceOceanAudio.cs`;
- `Unity_ValidateScript` через relay повернув success без diagnostics;
- SHA256 package binary: `6833ABE9DB6B274338E07AB585CF7B0F74719216DC0B50CCD69580176AACFA82`.

Поточне завдання перед продовженням розробки:

1. Додати весь перенесений пакет і контекстний файл під Plastic SCM.
2. Зробити перший check-in як базову точку історії.
3. Надалі при кожній зміні в проєкті оновлювати цей файл контексту.

## Plastic SCM ignore

На прохання користувача додано root-level `ignore.conf`.

Призначення файлу:

- не показувати в pending/private змінах регенеровані Unity-директорії: `Library`, `Temp`, `Obj`, `Build`, `Builds`, `UserSettings`, `MemoryCaptures`, `Logs`;
- не чіпати обов'язкові Unity-директорії на кшталт `Assets`, `Packages`, `ProjectSettings`;
- ігнорувати IDE/autogenerated файли (`.vs`, `.idea`, `*.csproj`, `*.sln`, `*.user`, `*.tmp` тощо);
- ігнорувати локальні кеші tooling (`node_modules`, `.venv`, `__pycache__`, `.pytest_cache`);
- ігнорувати OS metadata (`.DS_Store`, `Thumbs.db`, `Desktop.ini`) і Plastic private/conflict-файли (`*.private`, `*.private.meta`, regex для `*.private.N`).

## Стан пакета у Subaquatica

Пакет у `Subaquatica` є embedded Unity package:

- package name: `com.unity.ai.assistant`
- displayName: `Assistant`
- version: `2.7.0-pre.1`
- Unity: `6000.0`
- repository revision: `14dadd76...`
- fingerprint: `84e99ed7b906...`

Основні залежності:

- `com.unity.nuget.newtonsoft-json`: `3.2.1`
- `com.unity.modules.unitywebrequest`
- `com.unity.modules.uielements`
- `com.unity.2d.sprite`
- `com.unity.cloud.gltfast`: `6.14.1`
- `com.unity.mathematics`: `1.3.2`

Орієнтовний масштаб пакета: 6192 файли.

Top-level директорії:

- `Documentation~`
- `Editor`
- `Modules`
- `Plugins`
- `RelayApp~`
- `Runtime`
- `ThirdParty~`

Найважливіші зони:

- `Runtime` - протоколи, socket workflow, моделі повідомлень, агенти, function calling.
- `Editor/Assistant` - головний Assistant provider, backend, relay, ACP, conversation lifecycle, public API.
- `Editor/UI` - UI Toolkit інтерфейс асистента.
- `Modules/Cidonix.UniBridge.MCP.Editor` - MCP bridge/server для зовнішніх клієнтів.
- `Modules/Unity.AI.MCP.Runtime` - runtime частина MCP.
- `Modules/Unity.AI.Assistant.Tools` - локальні tools для агента.
- `Modules/Unity.AI.Toolkit.Accounts` - акаунт, settings, entitlement limits.
- `RelayApp~` - зовнішній relay binary, який працює у режимах `--relay` і `--mcp`.

## Архітектурна модель

Пакет - це не просто чат-вікно, а Unity Editor AI platform із трьома головними осями:

1. Chat Assistant: UI, conversation backend, relay/websocket workflow.
2. Local tool execution: `AgentTool` / function calling / permissions.
3. Unity MCP: зовнішні AI clients можуть керувати Unity через MCP tools.

Спрощений потік Assistant:

```text
AssistantWindow
  -> Assistant
  -> AssistantRelayBackend
  -> RelayChatWorkflow
  -> RelayApp~ process
  -> server / AI Gateway / ACP
  -> stream відповіді назад у UI
```

Спрощений потік Unity MCP:

```text
Codex / Claude / Cursor / інший MCP client
  -> relay_win.exe --mcp
  -> named pipe / unix socket
  -> UnityMCPBridge / Bridge.cs
  -> McpToolRegistry
  -> Unity MCP tools
```

## RelayApp / relay_win.exe

На 2026-05-05 детально перевірено:

```text
H:/Repos/Cidonix/UniBridge/com.unity.ai.assistant/RelayApp~/relay_win.exe
```

Висновок: `relay_win.exe` - це універсальний локальний bridge/relay binary для Unity AI Assistant. Його робили не як випадковий helper, а як окремий процес поза Unity Editor domain reload, який з'єднує:

- Unity Assistant UI / RelayService;
- Unity cloud/backend websocket;
- AI Gateway / ACP provider sessions;
- локальні agent CLI, включно з bundled Codex ACP;
- зовнішні MCP clients на кшталт Codex, Claude, Cursor;
- Unity MCP Bridge всередині Editor через named pipe / Unix socket.

`relay.json` у `RelayApp~` показує:

```json
{
  "name": "unity-ai-relay",
  "version": "1.0.12-build.96",
  "protocolVersion": "1.0",
  "capabilities": ["acp", "replay"]
}
```

`relay_win.exe --help` підтвердив два головні режими:

```text
relay --relay [OPTIONS]       Start relay server
relay --mcp [OPTIONS]         Start MCP server
```

### Режим `--relay`

Unity запускає `relay_win.exe` через:

```text
H:/Repos/Cidonix/UniBridge/com.unity.ai.assistant/Editor/Assistant/Relay/RelayService.cs
```

Аргументи збираються приблизно так:

```text
--relay --port {port} --mcp-client-port {mcpClientPort} --editor-pid {editorPid} --shutdown-delay {seconds}
```

Сенс цього режиму:

- тримати локальний WebSocket server для Unity Editor;
- приймати `RELAY_SESSION_START` / `RELAY_SESSION_END` / ping / shutdown / recovery повідомлення;
- відкривати cloud backend websocket від імені Unity Assistant;
- переживати reload-и Editor-скриптів краще, ніж суто C# state всередині Unity;
- мати окремий MCP Client REST server на локальному порті;
- запускати AI Gateway / ACP provider sessions;
- керувати локальними credentials через OS keychain / Windows Credential Manager / libsecret;
- реєструвати MCP approval token-и для Gateway sessions, щоб Unity могла відрізняти authorized tool calls.

Ключові файли:

- `Editor/Assistant/Relay/RelayService.cs` - стартує relay process і вибирає platform executable.
- `Editor/Assistant/Relay/RelayWebSocketAdapter.cs` - відправляє `RELAY_SESSION_START` з `cloudBackendUri`, conversation id і credential headers.
- `Editor/Assistant/Relay/RelayConstants.cs` - relay/gateway message types.
- `Editor/Assistant/Relay/Bus/RelayChannels.cs` - bus methods/events: persistence, credentials, preferences, analytics, MCP session register/unregister.
- `Editor/Assistant/Relay/Acp/AcpClient.cs` - створення Gateway/ACP sessions через `gateway/session/create`.
- `Editor/Assistant/Relay/Acp/CredentialClient.cs` - secure credential storage через relay.

### Режим `--mcp`

У цьому режимі цей самий binary працює як MCP server executable для зовнішніх AI clients.

Потік:

```text
Codex / Claude / Cursor
  -> ~/.unity/relay/relay_win.exe --mcp
  -> discovery file у ~/.unity/mcp/connections
  -> \\.\pipe\unity-mcp-{projectHash}-{editorPid}
  -> UnityMCPBridge / Bridge.cs
  -> McpToolRegistry
  -> Unity tools
```

`ServerInstaller.cs` копіює binary з package folder:

```text
Packages/com.unity.ai.assistant/RelayApp~/relay_win.exe
```

у стабільне місце:

```text
C:/Users/Cidonix/.unity/relay/relay_win.exe
```

Саме цей шлях потім записується в MCP client configs. Для Codex це робить:

```text
Modules/Cidonix.UniBridge.MCP.Editor/Settings/Integration/CodexIntegration.cs
```

Поточна базова конфігурація Codex має вигляд:

```toml
[mcp_servers.unity_mcp]
command = "C:\\Users\\Cidonix\\.unity\\relay\\relay_win.exe"
args = ["--mcp"]
enabled = true
```

### Multi-project / multi-instance targeting

`relay_win.exe --mcp` підтримує вибір конкретного Unity instance:

```text
--project-path <path>         Connect to Unity instance with this project path
--instance-id <pid>           Connect to Unity instance with this editor PID
```

Це пов'язано з discovery layer у:

```text
Modules/Cidonix.UniBridge.MCP.Editor/Connection/ServerDiscovery.cs
```

Кожен відкритий Unity Editor пише discovery JSON до:

```text
C:/Users/Cidonix/.unity/mcp/connections/
```

У discovery є:

- `connection_type`
- `connection_path`
- `project_path`
- `protocol_version`
- `editor_pid`

На Windows endpoint має форму:

```text
\\.\pipe\unity-mcp-{projectHash}-{editorPid}
```

Тобто `relay_win.exe` потрібен ще й для того, щоб Codex або інший MCP client міг під'єднатися до правильного Unity-проєкту, якщо одночасно відкрито кілька Editor instances. За замовчуванням `args = ["--mcp"]` підключає перший знайдений instance, а для надійного таргетингу треба додавати `--project-path` або `--instance-id`.

Приклад project-specific entries для майбутньої доробки UniBridge:

```toml
[mcp_servers.unity_subaquatica]
command = "C:\\Users\\Cidonix\\.unity\\relay\\relay_win.exe"
args = ["--mcp", "--project-path", "H:/Repos/UnityRepos/Subaquatica"]
enabled = true

[mcp_servers.unity_domovyk]
command = "C:\\Users\\Cidonix\\.unity\\relay\\relay_win.exe"
args = ["--mcp", "--project-path", "H:/Repos/UnityRepos/Domovyk"]
enabled = true
```

### Чому це окремий exe

Ймовірна причина, чому relay зроблено окремим executable:

- зовнішні MCP clients очікують окремий stdio MCP server process, а не C# код всередині Unity;
- процес може жити окремо від Unity domain reload/script reload;
- він може керувати WebSocket/cloud connection і локальним REST/MCP bridge незалежно від Editor UI;
- він запускає local agent/provider sessions і bundled binaries з `RelayApp~/gateway`;
- він має доступ до OS credential storage;
- він дає стабільний cross-platform entrypoint: `relay_win.exe`, `relay_linux`, `relay_mac_*`;
- він дозволяє одному пакету підтримувати Assistant chat, AI Gateway/ACP і direct MCP clients через один локальний bridge.

Коротко: `relay_win.exe` - це серце локальної інтеграції між Unity Editor, Codex/MCP, cloud backend і Gateway/ACP.

## Relay source recovery

На 2026-05-05 було перевірено можливість відновити логіку `relay_win.exe`, бо оригінальний проєкт розробки relay поки не знайдено.

Висновок: логіку можна відновити майже без native decompilation. `relay_win.exe` є Bun-compiled executable:

- Windows metadata: `InternalName: bun`, `FileVersion: 1.3.13`;
- всередині exe знайдено readable Bun JavaScript bundle;
- bundle починається з marker-а `// @bun`;
- витягнутий clean bundle проходить `node --check`;
- після форматування Prettier отримано приблизно 95k рядків читабельного JavaScript.

Згенеровані файли були в:

```text
C:/Temp/unibridge-relay-recovery/relay_win.bundle.clean.mjs
C:/Temp/unibridge-relay-recovery/relay_win.bundle.pretty.mjs
```

Clean bundle SHA256:

```text
5613ECB9FC1B85668D8FFFBEC146CA61CA197E838EE3BB15F7EBE7D836D3EF3F
```

У репозиторій додано recovery folder:

```text
H:/Repos/Cidonix/UniBridge/RelayRecovery/
```

Він містить:

- `extract_relay_bundle.ps1` - відтворюваний extractor для Bun bundle з `relay_win.exe`;
- `README.md` - карта відновленої логіки: CLI, MCP mode, relay mode, bus, Gateway/ACP, providers, credentials, REST endpoints, logging.

Команда для повторного витягу:

```powershell
.\RelayRecovery\extract_relay_bundle.ps1 -FormatWithPrettier
```

Головне обмеження: оригінальні назви файлів, коментарі й TypeScript types без source maps не відновлюються точно. Але runtime behavior, protocols, endpoints, provider flow і майбутню модульну структуру можна відновити з високою впевненістю.

Рекомендована стратегія:

1. Відновлювати не byte-for-byte source, а чистий сучасний TypeScript rewrite.
2. Почати з `--mcp` режиму: discovery, IPC, MCP SDK server, tool proxy.
3. Потім `--relay`: WebSocket server, relay bus, cloud backend, domain reload recovery.
4. Потім Gateway/ACP: provider registry, Codex provider, approval-token flow, session resume/reuse.
5. Потім compile через Bun у нові `relay_win.exe`, `relay_linux`, `relay_mac_*`.

## UniBridge Relay local-only direction

Архітектурне рішення користувача для нової версії UniBridge Relay:

- вирізати логіку підключення до віддаленого Unity/cloud/backend server;
- не переносити assistant cloud WebSocket flow як головну частину нового relay;
- сфокусувати UniBridge Relay на локальному MCP-доступі до відкритих Unity Editor instances;
- цільові локальні AI clients: Codex, Claude Code, Gemini, Cursor та інші MCP clients;
- головна цінність: AI client бачить живий Unity Editor через MCP tools, а не лише статичні файли/metainfo.

Що зі старого relay варто залишити:

- `--mcp` mode;
- discovery файли `~/.unity/mcp/connections/bridge-*.json`;
- фільтрацію по `project_path` і `editor_pid`;
- named pipe / Unix socket transport до Unity Editor;
- MCP server wrapper, tool discovery, tool call proxy;
- reconnect / rediscovery logic;
- approval-token flow, якщо потрібні protected Unity tools.

Що зі старого relay можна не переносити або перенести пізніше:

- assistant cloud backend (`cloudBackendUri`, credentials headers, remote WebSocket);
- `assistant` protocol branch у relay message router;
- Gateway/ACP provider sessions, якщо Unity UI більше не запускає агентів сам;
- remote provider credentials/preferences;
- MCP Client REST server, якщо не потрібне керування сторонніми MCP subprocesses через Unity UI.

Важлива знахідка по multi-project:

- старий relay уже має базову підтримку розрізнення Unity instances через `--project-path` / `UNITY_PROJECT_PATH` і `--instance-id` / `UNITY_INSTANCE_ID`;
- discovery JSON пишеться з `project_path = Application.dataPath`, тобто фактично шлях до `Assets`;
- `editor_pid` дозволяє розрізняти навіть кілька Editor instances одного й того самого проєкту;
- старий `CodexIntegration.cs` автоматично пише тільки один server entry `[mcp_servers.unity_mcp]` з `args = ["--mcp"]`, тому multi-project для зовнішнього Codex не налаштований автоматично.

Рекомендований напрям для UniBridge Relay:

1. Простий режим: генерувати окремі MCP entries для кожного відкритого Unity-проєкту, наприклад `unity_project_1`, `unity_project_2`, кожен з власним `--project-path` або `--instance-id`.
2. Сильніший режим: зробити один UniBridge MCP router, який читає всі discovery файли, показує `unibridge_list_projects`, і проксіює Unity tools у конкретний проєкт через `projectId`.
3. Для задач типу "подивись сцену в проєкті 1 і створи копію в проєкті 2" найкращим буде router або кілька чітко названих MCP servers, щоб Codex не плутав однакові tool names.

## UniBridge distribution pipeline

Бажаний UX для користувача:

1. Користувач додає Unity package з GitHub через Unity Package Manager.
2. Після import/domain reload пакет автоматично піднімає Unity-side `UnityMCPBridge` у кожному відкритому проєкті.
3. Кожен Editor instance пише discovery JSON у per-user директорію, наприклад `~/.unity/mcp/connections` або майбутню `~/.unibridge/connections`.
4. Пакет перевіряє bundled UniBridge Router binary і встановлює/оновлює його у стабільне per-user місце:

```text
C:/Users/<User>/.unibridge/relay/unibridge-relay.exe
```

або:

```text
C:/Users/<User>/AppData/Local/UniBridge/relay/unibridge-relay.exe
```

5. Пакет показує setup wizard/menu item:

```text
Tools/UniBridge/Setup AI Clients
```

6. Wizard показує статус:

- Unity MCP Bridge running;
- Router installed and version;
- discovered Unity projects;
- Codex configured / not configured;
- Claude Code configured / not configured;
- Gemini configured / not configured;
- test MCP connection result.

7. Користувач натискає `Configure Codex` або `Configure all detected clients`.
8. Пакет додає один глобальний MCP entry, наприклад:

```toml
[mcp_servers.unibridge]
command = "C:\\Users\\<User>\\.unibridge\\relay\\unibridge-relay.exe"
args = ["--mcp-router"]
enabled = true
```

9. Коли Codex стартує MCP session, він сам запускає `unibridge-relay.exe --mcp-router` як stdio MCP server.
10. Router читає всі discovery JSON, відкидає stale/dead PIDs, підключається до потрібних Unity Editor pipes/sockets і показує tools з явним `projectId`.

Важливо:

- Unity-проєкти не запускають router process;
- Unity-проєкти тільки публікують локальні endpoints;
- router запускається on-demand самим AI client-ом;
- якщо Codex і Claude Code працюють одночасно, кожен може запустити власний router process - це нормально для MVP;
- "єдиний router" означає один MCP server entry на всі Unity-проєкти в межах одного AI client session, а не один глобальний daemon на всю систему.

Рекомендація для оновлень:

- перший MVP може копіювати binary у стабільний шлях і, якщо файл залочений, оновлювати після restart або через rename-old-copy-new strategy;
- більш надійна версія може мати versioned install folder + stable launcher;
- кілька Unity-проєктів з різними версіями пакета мають оновлювати global router через lock/mutex і не ламати активний процес.

Policy для другого/наступного Unity-проєкту:

1. Кожен проєкт при import/domain reload виконує `EnsureRelayInstalled`, а не безумовний install.
2. `EnsureRelayInstalled` бере global install lock/mutex, наприклад `UniBridgeRelayInstall`.
3. Пакет читає installed manifest біля стабільного relay:

```text
C:/Users/<User>/.unibridge/relay/unibridge-relay.json
```

4. Якщо installed relay тієї самої або новішої сумісної версії - пакет нічого не перезаписує.
5. Якщо bundled relay новіший - пакет оновлює global relay через atomic strategy.
6. Якщо stable executable зайнятий запущеним Codex/Claude/Gemini session, пакет не вбиває процес. Він або відкладає оновлення до restart, або встановлює нову версію у versioned folder і оновлює stable launcher.
7. Якщо другий проєкт має старішу версію пакета, він не має робити downgrade global relay.
8. Незалежно від install/update результату, кожен Unity-проєкт пише свій discovery JSON з власним `projectId`, `projectPath`, `editorPid`, `connectionPath`, `protocolVersion`.
9. Router при старті читає всі discovery записи і сам вирішує сумісність за `protocolVersion`.

Тобто другий встановлений Unity package не створює другий global router і не перезаписує executable навмання. Він тільки гарантує, що на машині є сумісний router, і додає до discovery registry ще один активний Unity Editor endpoint.

Stable launcher design:

Рекомендована структура global install:

```text
C:/Users/<User>/.unibridge/relay/
  unibridge-relay.exe
  current.json
  versions/
    1.0.0/
      unibridge-relay-core.exe
      manifest.json
    1.1.0/
      unibridge-relay-core.exe
      manifest.json
```

`unibridge-relay.exe` у корені - це маленький стабільний launcher, на який завжди дивляться Codex/Claude/Gemini configs. Основна логіка MCP router живе у versioned `unibridge-relay-core.exe`.

Launcher responsibilities:

1. Прийняти ті самі args, які передав AI client, наприклад `--mcp-router`.
2. Прочитати `current.json`.
3. Вибрати active core binary, наприклад `versions/1.1.0/unibridge-relay-core.exe`.
4. Запустити core з тими самими args і важливими env vars.
5. Прозоро прокинути `stdin`, `stdout`, `stderr`.
6. Для MCP mode не писати власні логи у `stdout`, бо `stdout` зайнятий JSON-RPC протоколом. Логи тільки у файл або `stderr`.
7. Завершитись з тим самим exit code, що й core.

Update responsibilities з боку Unity package:

1. Взяти global install lock/mutex.
2. Скопіювати bundled core у тимчасову versioned директорію.
3. Перевірити hash/manifest.
4. Atomic update: записати `current.json.tmp`, потім перейменувати в `current.json`.
5. Не чіпати вже запущені core processes.
6. Не робити downgrade, якщо новий пакет старіший за встановлений router.
7. Старі versioned folders видаляти тільки коли вони не використовуються, або тримати останні 2-3 версії для rollback.

Поведінка під час активної MCP session:

- Codex запустив launcher.
- Launcher запустив core `1.0.0`.
- Інший Unity-проєкт встановив пакет з core `1.1.0`.
- Пакет додав `versions/1.1.0` і оновив `current.json`.
- Поточна Codex session продовжує працювати на `1.0.0`.
- Нова Codex/Claude/Gemini session піде вже через `1.1.0`.

У цьому контексті "нова session" означає новий запуск MCP server process, тобто новий старт `unibridge-relay.exe` з боку AI client. Якщо Codex вже запустив launcher/core і тримає цей процес живим, він не переключиться на новий core сам по собі тільки через зміну `current.json`. Переключення станеться коли AI client завершить старий MCP process і запустить `unibridge-relay.exe` заново: після restart Codex, reconnect/reload MCP servers, відкриття нового Codex instance/workspace якщо він стартує окремий MCP process, або після аварійного/нормального завершення старого relay process.

Тому stable launcher прибирає проблему Windows file lock: основний configured path майже не оновлюється, а core binaries можуть співіснувати поруч у різних версіях.

## UniBridge migration start

Поточна база для міграції - Unity Assistant `2.7.0-pre.1`, але новий продукт має називатись UniBridge і рухатись у local-only MCP напрямку.

Перший практичний прохід:

- папка пакета перейменовується з `com.unity.ai.assistant` на `com.cidonix.unibridge`;
- `package.json` перейменовується у `com.cidonix.unibridge` / `UniBridge`;
- package version для нового продукту стартує як `0.1.0-pre.1`;
- user-facing README переписується під UniBridge;
- `MCPConstants.packageName` переходить на `com.cidonix.unibridge`;
- project settings path переходить на `Project/UniBridge/MCP`;
- MCP client config key переходить з `unity-mcp` / `unity_mcp` на `unibridge`;
- per-user директорії для нового напряму переходять з `~/.unity/...` на `~/.unibridge/...`;
- Codex TOML section для нових конфігів переходить на `[mcp_servers.unibridge]`.

Toolbar indicator decision:

- індикатор у Unity toolbar має тільки два стани;
- зелений означає, що Unity-side MCP bridge живий і є хоча б один підключений AI/MCP client;
- червоний означає все інше: bridge disabled, stopped, relay не піднявся, або AI client ще не підключений;
- toolbar button не замінює налаштування, а дає швидкий operational menu: package version, connected client count, relay installed path, start/stop bridge, print client info, print tool schemas, install/update relay, open MCP settings.

Міграційна обережність:

- перший toolbar/rename прохід не видаляє cloud/chat код;
- наступний великий етап має спершу відв'язати `Cidonix.UniBridge.MCP.Editor` від `Unity.AI.Assistant.*`, `Acp/Gateway`, `AIAssistantAnalytics`, `Account.settings`, `AssistantEditorPreferences`, `AgentToolMcpAdapter`, старого `ServerInstaller`;
- тільки після цього можна безпечно видаляти Assistant UI, cloud backend, in-Editor chat, asset generation cloud workflows і Gateway/ACP session management.

## Unity Assistant 2.7.0-pre.2 comparison

Source package found at:

```text
H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant
```

Package metadata:

- `com.unity.ai.assistant`;
- version `2.7.0-pre.2`;
- Unity repository revision `690686e7dd999474b60fc88e7a4e110c7bf1c0c2`;
- documented fixes: Video to Motion, Assistant window search overflow, FBX-to-GLB conversion for reference models, light mode references button, trial banner subscription link removal.

Relay check:

- `RelayApp~/relay_win.exe` is byte-identical to the current UniBridge copy;
- SHA256: `854F019D6C833F2CA8AB4DCACDE31D18765EC1C6C83203D00364347DE35CCD66`;
- size: `119875496`;
- `relay.json` is unchanged: `unity-ai-relay` `1.0.12-build.96`, protocol `1.0`, capabilities `acp`, `replay`.

Assessment:

- Do not replace the current `com.cidonix.unibridge` package wholesale with `2.7.0-pre.2`.
- The `pre2` fixes are mostly in cloud/chat/generator UI areas planned for removal: Animate, Mesh, ModelSelector, Assistant UI styles, Account trial banner.
- `pre2` does not provide a newer relay.
- In the MCP module, current UniBridge has a useful local change that `pre2` does not: `GameObjectSerializer.GetComponentSummaryData` plus `ManageGameObject` using JSON-safe component summaries for `get_components` / `get_component`. This is valuable for local scene/object inspection because it avoids arbitrary Unity component reflection and reduces the risk of editor hangs during JSON serialization.
- `pre2` uses entitlement-driven MCP direct connection caps through `Account.settings.AllowedMcpConnections`; current UniBridge keeps direct MCP unlimited, which is closer to the local-only plan. This code should later be simplified further by removing ACP/Gateway/account wiring.

Selective carry-forward only:

- If cloud asset generators survive temporarily during transition, the `pre2` FBX-to-GLB and Video-to-Motion fixes can be cherry-picked into those modules.
- For the planned MCP-only product, these fixes are not worth resetting the base or undoing the UniBridge rename/toolbar work.

## UniBridge Hub / Dispatcher concept

New product direction:

- UniBridge should have a system-installed desktop application, tentatively `UniBridge Hub` or `UniBridge Dispatcher`;
- the app runs in the Windows tray and starts with the user session;
- the app is commercial and requires one-time activation against a private backend;
- after activation it can install UniBridge into selected Unity projects, update installed packages, update the local relay/router, and update itself;
- releases are hosted on a private dedicated VPS controlled by Cidonix.

Important naming distinction:

- `UniBridge Hub` / `Dispatcher` - tray desktop application, licensing, updates, project management, diagnostics;
- `unibridge-relay.exe` stable launcher - tiny MCP stdio launcher used by Codex/Claude/Gemini;
- `unibridge-relay-core.exe` - versioned MCP router core;
- Unity package `com.cidonix.unibridge` - Unity-side MCP bridge and Editor tooling.

Recommended installed layout:

```text
C:/Users/<User>/AppData/Local/UniBridge/
  Hub/UniBridgeHub.exe
  licenses/license.json
  cache/packages/com.cidonix.unibridge-0.1.0-pre.1.tgz
  relay/unibridge-relay.exe
  relay/current.json
  relay/versions/<version>/unibridge-relay-core.exe
  logs/
```

Hub responsibilities:

1. Activation:
   - accept license key or account login;
   - call VPS activation API over HTTPS;
   - store signed local license file;
   - support offline grace period after successful activation.
2. Updates:
   - poll a signed release manifest;
   - update Hub, Unity package tarballs, relay launcher/core, and metadata;
   - verify SHA256 and release signatures before installing anything;
   - expose stable/preview channels later if useful.
3. Unity project management:
   - discover Unity projects by `ProjectSettings/ProjectVersion.txt` and `Packages/manifest.json`;
   - let user choose a project folder;
   - install/update `com.cidonix.unibridge` by editing `Packages/manifest.json` or by copying a local package/tarball;
   - show installed package version per project;
   - avoid silently modifying projects without explicit user action.
4. MCP client setup:
   - configure Codex/Claude/Gemini/Cursor to point at the stable relay launcher;
   - keep one global MCP server entry named `unibridge`;
   - show whether clients are configured and when restart/reconnect is needed.
5. Diagnostics:
   - show open Unity projects discovered through `~/.unibridge/mcp/connections`;
   - show active MCP client count;
   - show relay version and package protocol compatibility;
   - open logs and run health checks.

VPS release manifest sketch:

```json
{
  "channel": "stable",
  "generatedAt": "2026-05-06T00:00:00Z",
  "hub": {
    "version": "0.1.0",
    "url": "https://updates.example.com/unibridge/hub/0.1.0/UniBridgeHubSetup.exe",
    "sha256": "...",
    "signature": "..."
  },
  "relay": {
    "version": "0.1.0",
    "protocolVersion": "unibridge-router-v1",
    "url": "https://updates.example.com/unibridge/relay/0.1.0/unibridge-relay-win-x64.zip",
    "sha256": "...",
    "signature": "..."
  },
  "unityPackage": {
    "name": "com.cidonix.unibridge",
    "version": "0.1.0-pre.1",
    "url": "https://updates.example.com/unibridge/packages/com.cidonix.unibridge-0.1.0-pre.1.tgz",
    "sha256": "...",
    "signature": "..."
  }
}
```

Commercial/security requirements:

- use HTTPS plus artifact signatures, not just HTTPS;
- sign Windows executables if possible to reduce SmartScreen friction;
- activation should not put secrets into Unity projects;
- collect minimal machine identity and document it;
- keep the Unity package usable only through local MCP, not through remote project access, unless explicitly designed later;
- never let automatic update rewrite an active Unity project without user confirmation.

Product assessment:

- This direction is stronger for a paid product than GitHub-only distribution because it controls updates, activation, relay compatibility, and user onboarding.
- The tradeoff is more engineering scope: desktop app, installer, updater, signing, backend activation API, release pipeline, and support diagnostics.
- Recommended MVP order: local MCP package first, then tray Hub with activation/update/install, then relay router versioning, then polished self-update and multi-channel releases.

## Важливі файли

Assistant:

- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Editor/Assistant/Assistant.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Editor/Assistant/AssistantProtocolInitializer.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Editor/Assistant/Api/AssistantApi.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Editor/UI/Scripts/AssistantWindow.cs`

Backend / workflow:

- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Editor/Assistant/Backend/BaseWebSocketBackend.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Editor/Assistant/Backend/AssistantWebSocketBackend.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Editor/Assistant/Relay/AssistantRelayBackend.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Runtime/Socket/Workflows/Chat/BaseChatWorkflow.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Runtime/Socket/Workflows/Chat/ChatWorkflow.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Editor/Assistant/Relay/RelayChatWorkflow.cs`

Tool/function calling:

- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Runtime/FunctionCalling/ToolRegistry.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Runtime/FunctionCalling/AttributeBasedFunctionSource.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Runtime/FunctionCalling/Attributes/AgentToolAttribute.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Runtime/FunctionCalling/Functions/Local/LocalAssistantFunction.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Runtime/FunctionCalling/AIAssistantFunctionCaller.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Runtime/FunctionCalling/Permissions/ToolPermissions.cs`

MCP:

- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/UnityMCPBridge.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Bridge.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Connection/ServerDiscovery.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Connection/ConnectionCensus.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Connection/AcpEntitlementWiring.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/McpToolRegistry.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Tools`

Codex integration:

- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Settings/Integration/CodexIntegration.cs`
- `H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Data/McpClients.cs`
- `C:/Users/Cidonix/.codex/config.toml`

## Local testing entitlement bypass

Користувач хотів повернути новішу версію пакета до локального безліміту direct MCP connections, як у старішій версії `2.6.0-pre.1`.

У новішому пакеті серверні/акаунтні ліміти приходять із:

- `Account.settings.AllowedMcpConnections`
- `Account.settings.AllowedGatewayConnections`

Правильне місце для локального bypass:

```text
H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Connection/AcpEntitlementWiring.cs
```

Стан, який був знайдений у коді:

```csharp
ConnectionCensus.SetPolicy(new ConnectionPolicy(
    MaxDirect: -1,
    MaxGateway: Account.settings.AllowedGatewayConnections
));
```

Сенс:

- `MaxDirect: -1` означає unlimited direct MCP connections.
- `MaxGateway` лишається entitlement-controlled через `AllowedGatewayConnections`.
- Це дозволяє локально тестувати MCP/Codex без готового сервера платних функцій.
- Gateway/ACP частина при цьому не вимикається повністю.

Логіка лімітів знаходиться в:

```text
H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Connection/ConnectionCensus.cs
```

Там `-1` трактується як unlimited.

## Multi-project Unity MCP support

Було перевірено, чи пакет підтримує одночасно відкриті декілька Unity-проєктів для Codex/Unity MCP.

Висновок: основа для цього є.

Ключовий файл:

```text
H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Modules/Cidonix.UniBridge.MCP.Editor/Connection/ServerDiscovery.cs
```

Кожен Unity Editor instance створює окремий IPC endpoint:

```text
\\.\pipe\unity-mcp-{projectHash}-{editorPid}
```

і discovery JSON у:

```text
C:/Users/Cidonix/.unity/mcp/connections/
```

Формат discovery-файлу:

```json
{
  "connection_type": "named_pipe",
  "connection_path": "\\\\.\\pipe\\unity-mcp-59f9f213-70192",
  "created_date": "2026-05-04T11:29:51.3549080Z",
  "project_path": "H:/Repos/UnityRepos/Subaquatica/Assets",
  "protocol_version": "2.0",
  "editor_pid": 70192
}
```

Ідентифікація є по:

- `project_path`
- `editor_pid`

`editor_pid` доданий спеціально, щоб кілька інстансів навіть одного й того ж проєкту не конфліктували.

Relay binary підтримує таргетинг:

```text
relay_win.exe --mcp --project-path H:/Repos/UnityRepos/Subaquatica
relay_win.exe --mcp --instance-id 70192
```

`relay_win.exe --help` показав:

```text
--project-path <path>         Connect to Unity instance with this project path
--instance-id <pid>           Connect to Unity instance with this editor PID
```

Документація пакета також описує це в:

```text
H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant/Documentation~/integration/unity-mcp-get-started.md
```

Але Codex integration зараз автоматично не прописує таргетинг.

Поточний `C:/Users/Cidonix/.codex/config.toml` мав:

```toml
[mcp_servers.unity_mcp]
command = "C:\\Users\\Cidonix\\.unity/relay\\relay_win.exe"
args = ["--mcp"]
enabled = true
```

Тобто за замовчуванням relay підключається до першого знайденого Unity Editor instance.

Що варто допиляти у `UniBridge`:

1. Навчити Codex integration створювати project-specific MCP server entries.
2. Наприклад:

```toml
[mcp_servers.unity_subaquatica]
command = "C:\\Users\\Cidonix\\.unity\\relay\\relay_win.exe"
args = ["--mcp", "--project-path", "H:/Repos/UnityRepos/Subaquatica"]
enabled = true

[mcp_servers.unity_domovyk]
command = "C:\\Users\\Cidonix\\.unity\\relay\\relay_win.exe"
args = ["--mcp", "--project-path", "H:/Repos/UnityRepos/Domovyk"]
enabled = true
```

3. Або використовувати `--instance-id`, якщо потрібно вибрати конкретний відкритий Editor instance одного й того самого проєкту.

На момент перевірки discovery-файлів на машині були записи для:

- `H:/Repos/UnityRepos/Subaquatica/Assets`
- `H:/Repos/UnityRepos/Domovyk/Assets`
- `H:/Repos/UnityRepos/DomovykPrototype/Assets`
- `H:/Repos/UnityRepos/Kazka/Assets`
- `H:/Repos/UnityRepos/KWS Water System/Assets`

Живим виглядав `Domovyk` з PID `68624`.
Поточний MCP tool у Codex був підключений саме до `Domovyk`, бо `Unity_GetProjectData` повернув структуру `Assets/_Domovyk`.

## Важлива відмінність

У пакеті вже є:

- discovery кількох Unity instances;
- окремі pipe/socket endpoints;
- project path / instance ID targeting у relay;
- multi-client support для одного Unity instance.

Поки не виглядає реалізованим:

- автоматичне створення кількох Codex MCP entries на кожен відкритий Unity-проєкт;
- автоматичне runtime-перемикання Codex між Unity-проєктами з одного server name `unity_mcp`;
- зручна команда "підключись до Subaquatica" без попередньо прописаного MCP server entry.

## Можливі наступні задачі для UniBridge

1. Зробити окремий маленький manager, який читає `C:/Users/Cidonix/.unity/mcp/connections/*.json`.
2. Фільтрувати тільки живі Unity PIDs.
3. Показувати список відкритих Unity проєктів.
4. Генерувати/оновлювати `C:/Users/Cidonix/.codex/config.toml` з кількома entries:
   - `unity_subaquatica`
   - `unity_domovyk`
   - `unity_domovykprototype`
5. Для стабільного таргетингу використовувати `--project-path`.
6. Для вибору конкретного відкритого інстанса використовувати `--instance-id`.
7. Можливо, винести це в окремий Unity/Codex helper package або CLI.

## Переписка по темі

Нижче окремим блоком збережена розмова, яка привела до цього стану. Службові системні інструкції не включені.

```text
USER:
Дивись, задача така. Я тут пишу разом з тобою свій Unity AI Assistant і щось в новій версії наплутав. У версії "com.unity.ai.assistant": "2.6.0-pre.1" в мене йде підключення напряму, здається у AcpEntitlementWiring.cs, там, або в іншому скрипті має бути щось типу public int maxDirectConnections = -1; Цей пакет для тестування я додав в цей проект h:\Repos\UnityRepos\DomovykPrototype\, а в іншому тестовому проекті тут h:\Repos\UnityRepos\Subaquatica\ тестую більш нову версію, в якій я хочу додати платні функції через сервер, але поки хочу їх вимкнути для локального тестування, бо сервер ще не готовий. Там має бути щось типу MaxDirect: Account.settings.AllowedMcpConnections . Будь ласка поверни цю більш нову версію до локального безліміту як в попередній версії. Дякую

USER:
А взагалі проаналізуй оцей мій пакет тут "H:\Repos\UnityRepos\Subaquatica\Packages\com.unity.ai.assistant"

Цікаво твій детальний аналіз всього змісту папки, так щоб ти детально детально розписав мені як працює цей мій пакет, цікаво наскільки ти розумний

ASSIST:
О, тут пакет вже виглядає як ціла платформа, а не “віконце з чатом”. Я подивився H:/Repos/UnityRepos/Subaquatica/Packages/com.unity.ai.assistant: це embedded Unity package com.unity.ai.assistant версії 2.7.0-pre.1, приблизно 6192 файли, з великими зонами Editor, Runtime, Modules, Plugins, RelayApp~, Documentation~.

Загальна модель:
Пакет складається з кількох шарів:
1. Runtime — протоколи, socket workflow, моделі повідомлень, агенти, function calling.
2. Editor/Assistant — головний Assistant provider, backend, conversation lifecycle, public API.
3. Editor/UI — UI Toolkit вікно асистента, чат, налаштування, permission-панелі, attachments.
4. Modules/Unity.AI.MCP.* — MCP server для зовнішніх клієнтів і AI gateway/ACP інтеграції.
5. Modules/Unity.AI.Assistant.Tools — локальні інструменти агента: filesystem, console, scene/gameobject, packages, play mode, plan tools.
6. Modules/Unity.AI.Toolkit.* — акаунт, settings, entitlements, async, asset utilities, compliance.
7. Modules/Unity.AI.Image/Mesh/Pbr/Sound/Animate — генератори ассетів, окремий великий продукт всередині пакета.
8. RelayApp~ — зовнішній relay-процес, через який новий workflow ходить у gateway/ACP.

Як стартує:
При завантаженні Unity багато речей піднімаються через InitializeOnLoad:
- AssistantProtocolInitializer.cs реєструє локальні tools як backend capabilities.
- RelayService.cs стартує relay-процес і тримає з ним bus-з’єднання.
- UnityMCPBridge.cs піднімає MCP bridge, якщо він увімкнений у settings.
- McpInitializer.cs реєструє MCP server manager service.
- AcpEntitlementWiring.cs під’єднує account entitlements до лімітів MCP/gateway connections.

Головний Assistant:
Центральний facade — Assistant.cs. Він створює backend, credentials provider, function caller, permission bridge і підтримує режими Agent, Plan, Ask.

Типовий шлях такий:
AssistantWindow -> Assistant -> AssistantRelayBackend -> RelayChatWorkflow -> relay process -> server/gateway -> stream відповіді назад у UI.

Старий/direct шлях теж лишився: AssistantWebSocketBackend.cs і ChatWorkflow.cs. Але дефолт у цьому пакеті вже виглядає relay/gateway-орієнтованим.

Workflow чату:
BaseChatWorkflow.cs — це state machine. Він проходить стани на кшталт initialization, idle, awaiting response, streaming, canceling, closed. Сервер може просити capabilities, skills, function/tool call, acknowledge chat, stream/final response, disconnect.

Коли сервер просить tool call, workflow передає його в AIAssistantFunctionCaller.cs. Той перевіряє permissions, викликає локальний tool і повертає результат назад у workflow.

Tools:
Tool-система зроблена досить елегантно. Методи позначаються [AgentTool] через AgentToolAttribute.cs. Потім AttributeBasedFunctionSource.cs знаходить їх через Unity TypeCache, будує schema, і ToolRegistry.cs складає їх у toolbox.

Permissions винесені окремо в ToolPermissions.cs. Там є дозволи на filesystem, screen capture, Unity objects, code execution, asset generation, play mode. Це правильна межа безпеки: LLM не просто “щось викликає”, а проходить policy/interactions шар.

MCP і ACP:
Найскладніша частина пакета — MCP/gateway. Bridge.cs фактично є локальним MCP server: слухає transport, приймає клієнтів, валідовує процеси, керує approval, виконує tools, кешує результати, переживає domain reload.

McpToolRegistry.cs знаходить [McpTool] tools і генерує MCP-compatible schemas. Built-in MCP tools лежать у Modules/Cidonix.UniBridge.MCP.Editor/Tools: робота зі scripts, assets, scene, console, shader, resources, text edits.

Окремо є ACP/gateway шар у Editor/Assistant/Acp. Він потрібен для нових provider sessions і gateway-mediated tool calls. Relay реєструє/знімає ACP sessions, а MCP bridge і connection census ведуть облік, хто прямий клієнт, хто gateway-клієнт.

Твій локальний безліміт:
Те, що ти просив раніше, зараз стоїть у правильному місці: AcpEntitlementWiring.cs зараз задає MaxDirect: -1, MaxGateway: Account.settings.AllowedGatewayConnections.

Тобто direct MCP connections для локального тестування безлімітні, а gateway connections далі контролюються акаунтними entitlement settings. Це якраз хороший компроміс, поки сервер для платних фіч ще не готовий.

Сам ліміт застосовується в ConnectionCensus.cs. -1 там означає unlimited. Цей клас також дедуплікує logical clients, не рахує direct transport проти direct pool, якщо клієнт уже сидить у gateway pool, і вміє виганяти старі connection-и, якщо cap зменшився.

Моя оцінка:
Архітектурно пакет сильний: добре розбитий на assemblies, tools discoverable через атрибути, є capabilities registry, permissions abstraction, socket workflow/state machine, relay ізольований як окремий процес, connection accounting винесений в окремий клас.

Найбільші ризики — великі центральні класи типу Bridge.cs, Assistant.cs і relay stack. Там багато відповідальностей, багато lifecycle/domain reload логіки, і саме там найімовірніше народжуються “воно іноді не підключилось” баги. Але видно, що код уже пережив багато реальних edge cases.

Коротко: я б описав цей пакет як Unity Editor AI platform із трьома головними осями: chat assistant, local tool execution, MCP/gateway connectivity. А твоя правка з MaxDirect: -1 не випадкова латка, а точково правильне місце для local testing bypass.

USER:
Я ще здається робив так щоб декілька проектів одночасно могли працювати з кодексом через Unity MCP, тільки щоб ти, тобто кодекс, міг розрізняти проекти за ID чи якось так, тобто щоб ти міг коннектитись за потреби то до одного проекту то до іншого, якщо відкрито одночасно декілька. Подивись чи є це в коді? Бо я вже не пам'ятаю

ASSIST:
Так, це в коді є. Ти це справді робив, і зроблено воно через discovery-файли + PID/шлях проєкту.

Головне місце: ServerDiscovery.cs. Кожен відкритий Unity Editor генерує окремий pipe:

\\.\pipe\unity-mcp-{projectHash}-{editorPid}

і пише JSON у:

C:\Users\Cidonix\.unity\mcp\connections\

Формат файлу такий:

{
  "connection_type": "named_pipe",
  "connection_path": "\\\\.\\pipe\\unity-mcp-59f9f213-70192",
  "project_path": "H:/Repos/UnityRepos/Subaquatica/Assets",
  "protocol_version": "2.0",
  "editor_pid": 70192
}

Тобто ідентифікація є по двох речах: project_path і editor_pid. Hash у назві pipe/файлу рахується від Application.dataPath, а PID додається спеціально, щоб навіть кілька інстансів одного й того ж проєкту не конфліктували.

Relay теж це підтримує. Я запустив relay_win.exe --help, і там є:

--project-path <path>         Connect to Unity instance with this project path
--instance-id <pid>           Connect to Unity instance with this editor PID

Це також описано в unity-mcp-get-started.md: якщо відкрито кілька Unity, можна таргетити конкретний проєкт через --project-path або конкретний Editor через --instance-id.

Але є нюанс: твоя Codex-інтеграція зараз автоматично цього не прописує. CodexIntegration.cs пише в C:\Users\Cidonix\.codex\config.toml тільки:

[mcp_servers.unity_mcp]
command = "C:\\Users\\Cidonix\\.unity/relay\\relay_win.exe"
args = ["--mcp"]
enabled = true

Тобто за замовчуванням relay підключається до першого знайденого Unity Editor instance. Це не “розумне перемикання за запитом”, а радше “можливість таргетингу існує, але її треба явно використати”.

Я ще перевірив живі discovery-файли на машині. У тебе зараз є записи для Subaquatica, Domovyk, DomovykPrototype, Kazka, KWS Water System, але більшість PID уже мертві. Живим виглядає Domovyk з PID 68624. І поточний unity_mcp у Codex справді підключений до Domovyk: Unity_GetProjectData повернув структуру з Assets/_Domovyk.

Висновок: основа для кількох одночасних Unity-проєктів є. Codex може підключатися до потрібного, якщо MCP server запущений з:

args = ["--mcp", "--project-path", "H:/Repos/UnityRepos/Subaquatica"]

або:

args = ["--mcp", "--instance-id", "70192"]

Чого немає: автоматичного створення кількох Codex MCP entries типу unity_subaquatica, unity_domovyk, unity_domovykprototype і runtime-перемикання між ними з одного unity_mcp. Це можна допиляти в CodexIntegration.cs, щоб він писав project-specific server name і додавав --project-path поточного Unity-проєкту.

USER:
Зафіксуй все це що в цьому чаті ти знайшов в файлі UniBridge_Context.md в папці h:\Repos\Cidonix\UniBridge\
Збережи в нього окремим блоком всю нашу переписку, зможеш? Я просто хочу зробити новий проект, щоб ти там знав про що ми говорили і на чому зупинились, там вже продовжимо
```
```

## 2026-05-06 - Stable UniBridge Project ID targeting

Прийняте рішення: multi-project targeting і проблему перенесення папки проекту вирішуємо через стабільний UniBridge Project ID.

Unity-side зміни:

- Unity створює/читає `ProjectSettings/UniBridge/project.json`.
- У файлі зберігаються `project_id`, `project_name`, `created_date`, `updated_date`.
- `project_id` переживає перенос проекту в іншу папку, бо більше не залежить від `Application.dataPath`.
- Discovery JSON тепер має поля `project_id`, `project_name`, `project_root`, а також старі `project_path`, `editor_pid`, `connection_path`.
- Toolbar `UB` показує Project ID у tooltip/menu.
- Toolbar menu має дії `Copy Project ID` і `Regenerate Project ID`.
- `Regenerate Project ID` потрібен для випадку, коли проект скопіювали/форкнули і дві відкриті копії мають однаковий ID.

Relay зміни:

- Relay bump: `1.1.0-build.6`.
- Додано `--project-id <id>`.
- Додано env fallback: `UNIBRIDGE_PROJECT_ID`, `UNITY_PROJECT_ID`.
- Старі режими залишаються:
  - `--project-path <path>`
  - `--instance-id <pid>`
- Тимчасовий legacy fallback на `.unity/mcp/connections` прибрано; UniBridge relay читає тільки `.unibridge/mcp/connections`.
- Unity більше не dual-write-ить discovery у `.unity/mcp/connections`.
- Relay все ще дедуплікує повтори одного Editor PID, бо користувач може задати дубльовані discovery-директорії через env.
- Якщо кілька живих Unity Editor advertise той самий requested Project ID, relay не вибирає навмання, а падає з ясним повідомленням і списком кандидатів.

Client config зміни:

- Codex, Claude Code і generic JSON integration тепер пишуть `--project-id` автоматично.
- Приклад для Codex:

```toml
[mcp_servers.unibridge]
command = "C:\\Users\\Cidonix\\.unibridge\\relay\\unibridge_relay_win.exe"
args = ["--mcp", "--project-id", "<PROJECT_ID>"]
enabled = true
```

- Current local Codex config was also updated for `[mcp_servers.unibridge]` to target Subaquatica by Project ID `4a84f53ebce54815b6de16b648827b8e`.

Поведінка:

- `--project-id` тепер рекомендований основний спосіб таргетингу.
- `--project-path` лишається fallback/debug.
- `--instance-id` точний для конкретного відкритого Unity Editor, але тимчасовий, бо PID змінюється при кожному запуску.
- Голий `args = ["--mcp"]` нормальний тільки для single-project сценарію; при кількох відкритих Unity проектах він не гарантує правильний вибір.

Поточний relay:

- Source: `UniBridge.Relay`.
- Bundled binary: `com.cidonix.unibridge/RelayApp~/unibridge_relay_win.exe`.
- Installed binary: `C:\Users\Cidonix\.unibridge\relay\unibridge_relay_win.exe`.
- SHA256 для `1.1.0-build.6`: `3F1819E2A2D1FA064207FEEA7DD1A8DB112C0185DA69FA80BCAAE2A8C47DA8AC`.

## 2026-05-06 - Toolbar refresh freeze fix

Після multi-project тесту з двома відкритими Unity проєктами (`Subaquatica` і `TryToSurvive`) Unity інколи показувала modal progress:

```text
EditorApplication.....: Cidonix.UniBridge.MCP.Editor.Toolbar.UniBridgeToolbarInjector.RefreshStateNow
Waiting for user code in Cidonix.UniBridge.MCP.Editor.dll to finish executing.
```

Висновок: проблема була не в MCP relay і не у самій зеленій точці, а в lifecycle toolbar injector-а.

Причина:

- `UniBridgeToolbarInjector.TryInstallOnUpdate()` успішно додавав/знаходив кнопку `UB`, але не відписувався від `EditorApplication.update`;
- через це на кожному editor update знову викликався `EnsureBound()`;
- `EnsureBound()` щоразу планував `RefreshStateLater()`;
- `RefreshStateLater()` без coalescing додавав новий `EditorApplication.delayCall += RefreshStateNow`;
- `RefreshStateNow()` синхронно збирав статус, включно з `PackageInfo.FindForAssetPath(...)` і `ProjectIdentity.GetOrCreate()`;
- під час import/recompile/connection approval це могло створювати лавину refresh/delayCall/repaint і блокувати Unity UI.

Фікс у source package `H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge`:

- `UniBridgeToolbarInjector` тепер відписується від `EditorApplication.update` після успішного install/rebind кнопки;
- event subscriptions для toolbar status bind-яться один раз;
- toolbar status refresh coalesced: одночасно може бути тільки один pending `delayCall`;
- під час `EditorApplication.isCompiling` або `EditorApplication.isUpdating` status refresh пропускається;
- toolbar repaint delay також coalesced;
- `UniBridgeStatusSnapshot` кешує package version на 60 секунд і project identity на 30 секунд;
- після `Regenerate Project ID` кеш snapshot-а invalidated і кнопка оновлюється одразу.

Змінені файли:

- `com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Toolbar/UniBridgeToolbarInjector.cs`
- `com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Toolbar/UniBridgeStatusSnapshot.cs`
- `com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Toolbar/UniBridgeStatusButton.cs`

Ті самі зміни синхронізовано в embedded packages:

- `H:/Repos/UnityRepos/Subaquatica/Packages/com.cidonix.unibridge`
- `H:/Repos/UnityRepos/TryToSurvive/Packages/com.cidonix.unibridge`

Перевірка:

- вручну прогнано Unity Roslyn compiler response `Cidonix.UniBridge.MCP.Editor.rsp` у Subaquatica — exit code 0;
- вручну прогнано той самий response у TryToSurvive — exit code 0.

Нотатка по console warnings:

- obsolete `GetInstanceID` / `EntityId` / `FindFirstObjectByType` warnings і invalid USS paths не є прямою причиною цього зависання;
- вони залишаються cleanup/noise задачами після глибшого вирізання legacy Unity AI Assistant UI/generator modules.

## 2026-05-06 - Unity toolbar indicator removed

Після обговорення вирішено прибрати кнопку `UB` із Unity toolbar повністю, щоб UniBridge не втручався в головний toolbar Unity і не мав жодного toolbar refresh lifecycle.

Зроблено у source package `H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge`:

- видалено модуль `Modules/Cidonix.UniBridge.MCP.Editor/Toolbar`;
- видалено `Toolbar.meta`;
- більше немає `InitializeOnLoad` bootstrap-а для toolbar injection;
- більше немає `UniBridgeToolbarInjector`, `UniBridgeStatusButton`, `UniBridgeStatusSnapshot`;
- оновлено `CHANGELOG.md`;
- оновлено `Documentation~/integration/unity-mcp-get-started.md`, щоб Project ID більше не згадувався як доступний з `UB` toolbar menu.

Ті самі зміни синхронізовано в embedded packages:

- `H:/Repos/UnityRepos/Subaquatica/Packages/com.cidonix.unibridge`
- `H:/Repos/UnityRepos/TryToSurvive/Packages/com.cidonix.unibridge`

Перевірка:

- `rg` не знаходить `UniBridgeToolbar*`, `UniBridgeStatus*`, `ToolbarBootstrap` або `ToolbarInjector` у source package чи embedded copies;
- для Subaquatica створено тимчасовий Unity compiler response без видалених toolbar source files і `Cidonix.UniBridge.MCP.Editor` скомпілювався з exit code 0;
- для TryToSurvive той самий compiler check також пройшов з exit code 0.

Подальший UX напрям:

- статус і налаштування UniBridge лишаються через `Edit > Project Settings > UniBridge > MCP`;
- якщо потрібні швидкі дії, краще зробити окреме звичайне меню `Tools/UniBridge/...`, бо воно не чіпає Unity toolbar і не потребує постійних refresh/repaint callbacks.

## 2026-05-06 - Legacy Assistant modules physically removed

Почато фізичне очищення source package `H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge` від вимкнених legacy Unity Assistant модулів. Це вже не просто `UNIBRIDGE_LEGACY_ASSISTANT` gating: відповідні директорії та assets видалені з пакета.

Видалено legacy блоки:

- `Editor/UI`;
- `Editor/Unity.AI.Search.Editor`;
- `Editor/Assistant/Api`;
- `Editor/Assistant/Annotations`;
- `Editor/Assistant/AssetGenerators`;
- `Editor/Assistant/Integrations`;
- `Modules/Unity.AI.Animate`;
- `Modules/Unity.AI.Assistant.Tools`;
- `Modules/Unity.AI.Generators.*`;
- `Modules/Unity.AI.Image`;
- `Modules/Unity.AI.Mesh`;
- `Modules/Unity.AI.ModelSelector`;
- `Modules/Unity.AI.Pbr`;
- `Modules/Unity.AI.Sound`;
- `Modules/Unity.AI.Toolkit.GenerationContextMenu`;
- `Modules/Unity.AI.Toolkit.GenerationObjectPicker`;
- `Modules/Generators-README.md`;
- disabled MCP legacy adapter files `AgentToolMcpAdapter.cs`, `RunCommand.cs`, `RunCommandParams.cs`.

Залишено активний мінімальний шар, бо поточний `Cidonix.UniBridge.MCP.Editor` ще залежить від нього:

- `Editor/Assistant` core;
- `Editor/InternalBridge`;
- `Runtime`;
- `Modules/Unity.AI.MCP.Runtime`;
- `Modules/Cidonix.UniBridge.MCP.Editor`;
- `Modules/Unity.AI.Toolkit.Accounts`, `Asset`, `Async`, `Compliance`, `Utility`, `Sdk`;
- `Modules/Unity.AI.Tracing`.

Супутні правки:

- прибрано `Unity.AI.Toolkit.Chat` з `Unity.AI.Assistant.Editor.asmdef`;
- прибрано stale `InternalsVisibleTo` на видалені legacy assemblies;
- оновлено active package-path рядки з `com.unity.ai.assistant` на `com.cidonix.unibridge`;
- прибрано залежність MCP settings UXML від видаленого `Editor/UI/Styles/AssistantDisclaimer.uss`;
- додано локальні disclaimer styles у `MCPSettingsPanel.uss`;
- перейменовано видимі settings labels на `UniBridge MCP` / `Local Bridge`;
- оновлено `README.md`, `CHANGELOG.md`, `package.json` і ключові `Documentation~` згадки.

Перевірка після чистки:

- у source package більше немає asmdef з `UNIBRIDGE_LEGACY_ASSISTANT`;
- `rg` не знаходить активних посилань на видалені legacy assemblies/modules, окрім історичних рядків у `CHANGELOG.md` та `LICENSE.md`;
- перевірка `Packages/com.cidonix.unibridge/...` asset-path рядків не знайшла missing paths;
- перевірка локальних UXML/USS/TSS imports у MCP settings і Toolkit Accounts не знайшла missing style references;
- `Modules` схудли приблизно з `13.4 MB` до `2.46 MB`, `Editor` з `5.0 MB` до `1.47 MB`.

Важливо:

- `RelayApp~` ще не чіпався і все ще важить близько `395 MB` через старі relay/gateway бінарники. Це окремий наступний cleanup пункт.
- зміни поки зроблені у source package. Embedded копії у `Subaquatica` і `TryToSurvive` не синхронізувались у цьому кроці, щоб не запускати зайву перекомпіляцію відкритих Unity проєктів без окремої команди.

## 2026-05-06 - Duplicate ScreenWindowContextData cleanup

Після ручної синхронізації очищеного пакета в `Subaquatica` і `TryToSurvive`
Unity показала compile errors:

- `CS0101`: namespace `Unity.AI.Assistant.Editor.Data` already contains
  `ScreenWindowContextData`;
- `CS0579`: duplicate `Serializable` attribute.

Причина: після legacy cleanup у `Editor/Assistant/Data` лишилися два файли з
тим самим типом:

- `ScreenWindowContextData.cs`;
- `XmlParsable.cs`, який фактично дублював `ScreenWindowContextData`.

Виправлення:

- видалено зайвий `Editor/Assistant/Data/XmlParsable.cs`;
- видалено його `.meta`;
- таку саму правку синхронізовано в embedded copies:
  `H:/Repos/UnityRepos/Subaquatica/Packages/com.cidonix.unibridge` і
  `H:/Repos/UnityRepos/TryToSurvive/Packages/com.cidonix.unibridge`.

Після цього `TryToSurvive` перевидав live discovery file на актуальний Unity
PID `80144`, а relay зміг знайти обидва живі Unity bridge-и:

- `TryToSurvive` через project ID `2b6c2d9e30c14d149a349a2912752100`;
- `Subaquatica` через project ID `4a84f53ebce54815b6de16b648827b8e`.

Перевірка UniBridge relay:

- `C:/Users/Cidonix/.unibridge/relay/unibridge_relay_win.exe --mcp`;
- JSON-lines MCP framing;
- `tools/list` повернув 20 MCP tools;
- активні tool names мають underscore формат, наприклад
  `Unity_ManageScene` і `Unity_ManageGameObject`.

## 2026-05-06 - Relay installer identity guard

Оновлено `ServerInstaller` у source package
`H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge` і синхронізовано цю правку
в embedded package copies `Subaquatica` та `TryToSurvive`.

Що змінилось:

- старий session guard `ServerInstaller.CheckedThisSession` був булевим і
  блокував повторну перевірку relay у межах тієї самої Unity editor session;
- новий guard `ServerInstaller.CheckedRelayIdentityThisSession.v1` зберігає
  identity bundled relay: `relay.json` version + SHA256 bundled executable;
- якщо пакет замінити у вже відкритому Unity проекті і Unity зробить domain
  reload, installer повторно перевірить relay, якщо bundled version або hash
  змінився;
- `InstallOrUpdateRelay()` тепер повертає `bool`, щоб session identity
  записувався тільки після успішної перевірки/установки;
- якщо bundled relay має ту саму reported version, але інший executable hash,
  installer теж оновить файл у `C:/Users/USER/.unibridge/relay`;
- якщо встановлений relay має новішу версію, старіший bundled relay не
  даунгрейдить його.

Перевірка:

- source bundled relay і installed relay зараз мають однаковий SHA256:
  `3F1819E2A2D1FA064207FEEA7DD1A8DB112C0185DA69FA80BCAAE2A8C47DA8AC`;
- installed relay повертає `Version: 1.1.0-build.6`;
- останній хвіст `Editor.log` після правки не містить нових `error CS` або
  `Compilation failed` для `ServerInstaller.cs`.

## 2026-05-06 - Local-only package cut and single documentation page

Після уточнення, що `Project Settings > UniBridge > MCP` має залишитись
головною панеллю локального сервісу, зроблено глибшу local-only чистку source
package `H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge`.

Залишена активна структура пакета:

- `Modules/Cidonix.UniBridge.MCP.Editor`;
- `Modules/Unity.AI.MCP.Runtime`;
- `Modules/Unity.AI.Toolkit.Async`;
- `Modules/Unity.AI.Tracing`;
- `RelayApp~`;
- `Plugins`, `ThirdParty~`;
- один документ `Documentation~/unibridge.md`.

Фізично видалено старі cloud/assistant залишки:

- `Editor`;
- `Runtime`;
- `Modules/Unity.AI.Toolkit.Accounts`;
- `Modules/Unity.AI.Toolkit.Asset`;
- `Modules/Unity.AI.Toolkit.Compliance`;
- `Modules/Unity.AI.Toolkit.Sdk`;
- `Modules/Unity.AI.Toolkit.Utility`;
- старий `Documentation~` з Unity AI Assistant / generator / AI Gateway docs;
- `ValidationExceptions.json`;
- packaged `RelayApp~/gateway` ACP/codex launcher binaries;
- старі packaged relay binaries `relay_mac_x64`, `relay_mac_arm64`,
  `relay_linux`.

MCP editor code cleanup:

- `Bridge.cs` більше не використовує Unity Assistant analytics, ACP session
  registry, Unity Relay gateway або gateway approval path;
- `TransportStore` спрощено до local MCP transport state без ACP/gateway
  token fields;
- `ConnectionCensus` переписано на direct-only logical client counting;
- `AcpEntitlementWiring` замінено на `LocalConnectionPolicyWiring`;
- `MCPSettings` лишив тільки `connectionPolicies.direct`;
- MCP settings UI більше не показує gateway connections;
- `ToolCategories` більше не містить `Assistant` category;
- `TraceCategories` прибрано gateway/cloud/search categories;
- активний `Cidonix.UniBridge.MCP.Editor.asmdef` посилається тільки на
  `Unity.AI.MCP.Runtime`, `Unity.AI.Toolkit.Async`, `Unity.AI.Tracing`.

Relay:

- з `UniBridge.Relay/Program.cs` видалено відправку
  `set_acp_token` з `UNITY_ACP_SESSION_TOKEN` / `UNIBRIDGE_ACP_SESSION_TOKEN`;
- relay version bump: `1.1.0.6` -> `1.1.0.7`;
- `RelayApp~/relay.json` bump: `1.1.0-build.6` -> `1.1.0-build.7`;
- Windows relay перезібрано через `dotnet publish` і оновлено
  `RelayApp~/unibridge_relay_win.exe`;
- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj -c Release`
  пройшов без warnings/errors.

Документація:

- створено єдиний якісний документ `Documentation~/unibridge.md`;
- документ описує продуктову ціль, package layout, installation, Patreon
  distribution, first run, relay flow, MCP client config, multi-project
  targeting, Project Settings, relay updates, security notes,
  troubleshooting, limitations, build steps і release checklist;
- `README.md` тепер короткий і посилається на `Documentation~/unibridge.md`;
- `CHANGELOG.md` переписано під UniBridge-only історію без старого Unity AI
  Assistant changelog шуму.

Перевірка після чистки:

- `rg` по `Modules` і `UniBridge.Relay` не знаходить `Unity.AI.Assistant`,
  `Unity.Relay`, `Unity.AI.Toolkit.Accounts`, `AiEditorToolsSdk`,
  `set_acp_token`, `UNIBRIDGE_ACP`, `UNITY_ACP`, `MaxGateway`,
  `GatewayApproved`;
- `rg --files com.cidonix.unibridge/Documentation~` показує тільки
  `unibridge.md` і `unibridge.md.meta`;
- `rg --files com.cidonix.unibridge/RelayApp~` показує тільки
  `unibridge_relay_win.exe` і `relay.json`;
- embedded copies у `Subaquatica` та `TryToSurvive` у цьому кроці не
  синхронізувались.

## 2026-05-07 - Relay source moved outside distributable package

За рішенням залишити source-код рілею в репозиторії, але не всередині папки
пакета для розповсюдження, source винесено з
`com.cidonix.unibridge/RelaySource~` у сусідню кореневу папку
`UniBridge.Relay`.

Зміни структури:

- `UniBridge.Relay/Program.cs`, `UniBridge.Relay/UniBridge.Relay.csproj` і
  `UniBridge.Relay/README.md` тепер лежать на одному рівні з
  `com.cidonix.unibridge`;
- порожню папку `com.cidonix.unibridge/RelaySource~` видалено;
- build output `bin`/`obj` з перенесеного source-коду прибрано перед
  перевіркою;
- distributable Unity package тепер містить тільки packaged relay binary і
  manifest у `RelayApp~`, без source-коду рілею.

Оновлено:

- `UniBridge.Relay/README.md` з новими командами build/publish;
- `com.cidonix.unibridge/Documentation~/unibridge.md` з новим package layout,
  release pipeline і приміткою, що Patreon/package distribution не має
  включати `UniBridge.Relay`;
- `com.cidonix.unibridge/CHANGELOG.md` з нотаткою про винесення source-коду
  рілею з пакета.

## 2026-05-07 - Package license and third-party notices cleanup

Оновлено юридично-атрибуційний шар пакета після переходу від тимчасової
Unity Assistant заглушки до власного UniBridge/Cidonix пакета.

Зміни:

- `com.cidonix.unibridge/LICENSE.md` замінено з
  `com.unity.ai.assistant copyright © 2025 Unity Technologies` на
  proprietary UniBridge license: `Copyright (c) 2026 Cidonix. All rights
  reserved.`;
- у license додано базові правила використання, заборону перепродажу/
  перепублікації пакета без дозволу Cidonix, примітку про third-party
  components і disclaimer, що UniBridge не affiliated/endorsed/sponsored by
  Unity Technologies;
- `com.cidonix.unibridge/Third Party Notices.md` переписано під фактично
  bundled/required залежності поточного пакета;
- залишено notices для Markdig, Microsoft Roslyn, Microsoft .NET libraries,
  Microsoft.ML.Tokenizers, Google.Protobuf, Newtonsoft.Json і ripgrep;
- видалено старий шум із notices для BGE, HuggingFace Hub, SigLIP, ACP,
  codex/claude ACP wrappers, Bun, Gemini CLI, CoplayDev/unity-mcp,
  @modelcontextprotocol/sdk, glob, zod, arg;
- додано короткий розділ про Unity dependencies як пакети/модулі, що
  постачаються Unity Technologies за їхніми умовами, а не субліцензуються
  Cidonix.

Перевірка:

- `rg` по `LICENSE.md` і `Third Party Notices.md` більше не знаходить
  старий рядок `com.unity.ai.assistant copyright`;
- активні `Component Name` у notices тепер тільки: Markdig, Microsoft Roslyn,
  Microsoft .NET Libraries, Microsoft.ML.Tokenizers, Google.Protobuf,
  Newtonsoft.Json, ripgrep.

## 2026-05-07 - Cross-platform UniBridge relay build pass

Після рішення повернути Mac/Linux relay підтримку на новому MCP-only коді
зроблено продуманий cross-platform pass без runtime-тесту на target OS.

Relay source:

- `UniBridge.Relay/Program.cs` піднято до `1.1.0-build.8`;
- relay тепер підтримує два Unity-side transport-и:
  - Windows: named pipe;
  - Linux/macOS: Unix domain socket;
- якщо старий або помилковий discovery file на Unix платформі все ще пише
  `connection_type = "named_pipe"`, але шлях виглядає як Unix socket path,
  relay попереджає і fallback-ить на `unix_socket`;
- `UniBridge.Relay.csproj` тепер має RuntimeIdentifiers
  `win-x64;linux-x64;osx-x64;osx-arm64`;
- output names:
  - `unibridge_relay_win.exe`;
  - `unibridge_relay_linux`;
  - `unibridge_relay_mac_x64`;
  - `unibridge_relay_mac_arm64`.

Unity package:

- `ServerDiscovery` тепер пише `connection_type = "named_pipe"` тільки на
  Windows і `connection_type = "unix_socket"` на Linux/macOS;
- Unix socket path перенесено в app-owned temp directory:
  `/tmp/unibridge-mcp/unity-mcp-{hash}-{pid}.sock`, щоб не chmod-ити `/tmp`;
- `UnixSocketListener`, `UnixSocketTransport` і Unix tracing sink на macOS
  використовують `libSystem.dylib`, на Linux - `libc`;
- `MCPConstants` повертає platform-specific bundled/installed relay binary
  path для поточної ОС і macOS architecture;
- `ServerInstaller` більше не очікує старі `relay_linux` або macOS `.app`
  bundles, копіює один executable для поточної платформи, робить `chmod +x`
  на Unix/macOS і best-effort `xattr -d com.apple.quarantine` на macOS;
- installer чистить legacy relay leftovers:
  `relay_win.exe`, `relay_linux`, `relay_mac_x64`, `relay_mac_arm64`,
  `relay_mac_x64.app`, `relay_mac_arm64.app`.

Packaged binaries:

- `RelayApp~/relay.json` bump: `1.1.0-build.7` -> `1.1.0-build.8`;
- `RelayApp~/unibridge_relay_win.exe`;
- `RelayApp~/unibridge_relay_linux`;
- `RelayApp~/unibridge_relay_mac_x64`;
- `RelayApp~/unibridge_relay_mac_arm64`;
- package version bump: `0.1.0-pre.3` -> `0.1.0-pre.4`.

SHA256:

- Windows: `9EB1D94AB7E85D1C88F426750C07AB59D7CCF03A22991BBFB366A3A90CFD8BE3`;
- Linux x64: `8E647740CE2FC5F179B969F2E3723A83B8194840CFC94BE782CADD7F2C034F46`;
- macOS x64: `BA37DEB7B46A7D50D69289F1450756638F08C0E57B1BD664BE91CCE5C4E14AC5`;
- macOS arm64: `737F09C79BF2C76C77107FCA041A1240BCD015CF650F81AF688245104E1A2D07`.

Перевірка:

- `dotnet publish` пройшов успішно для `win-x64`, `linux-x64`, `osx-x64`,
  `osx-arm64`;
- packaged Windows relay `--version` і `--help` успішно запускаються на цій
  машині й показують `Version: 1.1.0-build.8`;
- Linux/macOS binaries поки тільки cross-built з Windows. Їх треба
  runtime-перевірити пізніше на Linux VM і реальному/доступному macOS.

## 2026-05-07 - Remaining active Unity.AI assemblies renamed

Після cross-platform relay pass зроблено брендову чистку останніх активних
`Unity.AI.*` runtime/helper assemblies, які вже не були cloud/chat логікою,
але плутали назвою.

Перейменовано через Plastic move, щоб зберегти історію файлів:

- `Modules/Unity.AI.MCP.Runtime` ->
  `Modules/Cidonix.UniBridge.MCP.Runtime`;
- `Unity.AI.MCP.Runtime.asmdef` ->
  `Cidonix.UniBridge.MCP.Runtime.asmdef`;
- `Modules/Unity.AI.Toolkit.Async` ->
  `Modules/Cidonix.UniBridge.Toolkit.Async`;
- `Unity.AI.Toolkit.Async.asmdef` ->
  `Cidonix.UniBridge.Toolkit.Async.asmdef`;
- `Modules/Unity.AI.Tracing` ->
  `Modules/Cidonix.UniBridge.Tracing`;
- `Unity.AI.Tracing.asmdef` ->
  `Cidonix.UniBridge.Tracing.asmdef`.

Namespace/API changes:

- `Unity.AI.MCP.Runtime.Serialization` ->
  `Cidonix.UniBridge.MCP.Runtime.Serialization`;
- `Unity.AI.Toolkit` -> `Cidonix.UniBridge.Toolkit`;
- `Unity.AI.Tracing` -> `Cidonix.UniBridge.Tracing`;
- `Cidonix.UniBridge.MCP.Editor.asmdef` references now point only to
  `Cidonix.UniBridge.MCP.Runtime`, `Cidonix.UniBridge.Toolkit.Async`, and
  `Cidonix.UniBridge.Tracing`;
- stale UI text `Missing Unity.AI.MCP Config` changed to
  `Missing UniBridge MCP Config`.

Package metadata:

- package version bump: `0.1.0-pre.4` -> `0.1.0-pre.5`;
- `Documentation~/unibridge.md` package layout now lists only
  `Cidonix.UniBridge.*` active module names.

Перевірка:

- `rg` по active package не знаходить `Unity.AI.MCP.Runtime`,
  `Unity.AI.Toolkit.Async`, `Unity.AI.Toolkit`, `Unity.AI.Tracing` або
  `Missing Unity.AI.MCP Config`;
- історичні згадки `Unity.AI.*` лишаються тільки в цьому context файлі та
  changelog/history як опис минулих етапів.

## 2026-05-07 - Unity API compatibility cleanup

Зроблено pass по Unity 6 obsolete API warnings у активному UniBridge MCP
коді. Мета: не міняти MCP-контракт для клієнтів, але прибрати прямі виклики
застарілих Unity API з tool/serializer логіки.

Зміни:

- `UnityApiAdapter` тепер централізує:
  - отримання MCP object id (`EntityId` для Unity 6000.0+, старий
    `InstanceID` fallback тільки всередині helper-а);
  - object id lookup назад у `UnityEngine.Object`;
  - active selection id;
  - `FindObjectsByType` без obsolete sort-mode overload у Unity 6;
  - `Collider2D` composite info через `compositeOperation` у Unity 6.
- `UnityTypeConverters` отримав локальний runtime serializer helper для
  object id та editor-only object lookup, щоб runtime assembly не залежав
  від MCP editor helper-а.
- `GameObjectSerializer`, `ObjectsHelper`, `ManageEditor`, `ManageAsset`,
  `ManageScene`, `ManageGameObject` більше не використовують напряму
  `GetInstanceID()`, `EntityId`, `FindObjectsSortMode` або
  `Collider2D.usedByComposite`.
- MCP response поля `instanceID`, `parentInstanceID`,
  `gameObjectInstanceID` лишились без перейменування для сумісності з
  існуючими клієнтами.

Package metadata:

- package version bump: `0.1.0-pre.5` -> `0.1.0-pre.6`;
- relay binaries і relay metadata не змінювались.

Перевірка:

- `rg` по активному пакету показує прямі obsolete/API-version виклики тільки
  у compatibility helper-ах (`UnityApiAdapter` і serializer helper);
- package changelog і документацію оновлено під `0.1.0-pre.6`.

## 2026-05-07 - Compile fix after local-only cleanup

Після ручного оновлення пакета в Subaquatica і TryToSurvive Unity показала
дві compile errors:

- `TransportStore.cs`: не знаходив `ValidationDecision`;
- `Bridge.cs`: не знаходив `TierDenialKind`.

Зроблено не як повернення legacy tier-коду, а як local-only cleanup:

- у `TransportStore.cs` додано відсутній `using
  Cidonix.UniBridge.MCP.Editor.Security;`, бо `ValidationDecision` є в
  активному security-шарі пакета;
- з `Bridge.cs` прибрано залежність від старого `TierDenialKind/TierDenial`;
- capacity denial message тепер будується локально:
  `Local MCP connection capacity reached ({count}/{cap}).`;
- коментарі оновлені з `tier-aware` на local MCP wording.

Package metadata:

- package version bump: `0.1.0-pre.6` -> `0.1.0-pre.7`;
- relay binaries і relay metadata не змінювались.

## 2026-05-07 - Connection trust terminology cleanup

Після обговорення naming у локальному security UI прибрано двозначне слово
`Tier`, щоб не плутати trust/security UI з product tiers або старою
capacity/tier логікою.

Перейменовано через Plastic move:

- `Security/SecurityTierClassifier.cs` ->
  `Security/ConnectionTrustClassifier.cs`;
- `Security/SecurityTierClassifier.cs.meta` ->
  `Security/ConnectionTrustClassifier.cs.meta`.

Code/API rename:

- `SecurityTierClassifier` -> `ConnectionTrustClassifier`;
- `SecurityTier` -> `ConnectionTrustLevel`;
- `DetermineTier()` -> `DetermineTrustLevel()`;
- `GetTierDescription()` -> `GetTrustLevelDescription()`.

UI rename:

- `m_TierWarning` -> `m_TrustWarning`;
- `tierWarningContainer` -> `trustWarningContainer`;
- `.umcp-tier-warning` -> `.umcp-trust-warning`;
- UXML/comment wording switched from security tier to connection trust.

Package metadata:

- package version bump: `0.1.0-pre.7` -> `0.1.0-pre.8`;
- relay binaries і relay metadata не змінювались.

## 2026-05-07 - Editor Mode auto-approve setting

Додано окрему настройку для користувача, який хоче дозволяти нові локальні
MCP-підключення у звичайному Unity Editor без ручного натискання `Allow`.

Зміни:

- у `MCPSettings` додано `autoApproveInEditorMode = false`;
- у `Project Settings > UniBridge > MCP` додано toggle
  `Auto-approve in Editor Mode` над існуючим
  `Auto-approve in Batch Mode`;
- `Auto-approve in Editor Mode` спрацьовує тільки коли Unity не в
  `Application.isBatchMode`;
- Batch Mode auto-approval лишився окремим незалежним setting-ом;
- explicit user rejection має пріоритет: якщо клієнт раніше був rejected,
  новий Editor Mode auto-approve не перепідтвердить його мовчки.

Package metadata:

- package version bump: `0.1.0-pre.8` -> `0.1.0-pre.9`;
- relay binaries і relay metadata не змінювались.

## 2026-05-07 - Documentation cleanup for clean UniBridge preview

Почищено користувацьку документацію від історичних згадок старого Unity
Assistant, щоб пакет описувався як самостійний UniBridge продукт.

Зміни:

- `README.md` тепер описує тільки local-only MCP bridge без порівняння зі
  старими cloud/chat/account сценаріями;
- `Documentation~/unibridge.md` переформульовано без згадок Unity Assistant і
  старого in-Editor chat/account UI;
- `CHANGELOG.md` очищено до порожнього каркаса для нового чистого changelog.

Код, package version і relay binaries не змінювались.

## 2026-05-07 - MCP tool descriptions refresh

Переписано top-level описи всіх 19 MCP tools у
`Modules/Cidonix.UniBridge.MCP.Editor/Tools`, тобто саме ті тексти, які
UniBridge показує у `Project Settings > UniBridge > MCP > Tools > Show
Details` і які MCP clients отримують у tool schema.

Принцип нового стилю:

- опис має бути корисний і людині в Project Settings, і AI agent-у в MCP
  schema;
- перший рядок коротко пояснює призначення tool-а;
- далі описано, коли саме tool варто використовувати;
- аргументи описані практично: що передавати і для яких сценаріїв;
- returns описані як `success`, `message` і action-specific `data`;
- прибрано стару мову на кшталт `Compatibility router`, `legacy script
  operations`, `resources/read`, старі шаблонні описи й warning emoji.

Оновлено описи:

- `Unity.ManageShader`;
- `Unity.ReadConsole`;
- `Unity.ListResources`;
- `Unity.ReadResource`;
- `Unity.FindInFile`;
- `Unity.ManageEditor`;
- `Unity.CreateScript`;
- `Unity.DeleteScript`;
- `Unity.GetSha`;
- `Unity.ApplyTextEdits`;
- `Unity.ImportExternalModel`;
- `Unity.ManageAsset`;
- `Unity.ManageGameObject`;
- `Unity.ManageMenuItem`;
- `Unity.ManageScene`;
- `Unity.ManageScript`;
- `Unity.ManageScript_capabilities`;
- `Unity.ScriptApplyEdits`;
- `Unity.ValidateScript`.

Також видалено застарілий generated API snapshot:

- `Modules/Cidonix.UniBridge.MCP.Editor/Cidonix.UniBridge.MCP.Editor.api`;
- `Modules/Cidonix.UniBridge.MCP.Editor/Cidonix.UniBridge.MCP.Editor.api.meta`.

Причина видалення: snapshot був неактуальний, містив старі tool descriptions і
вже видалений `Unity.RunCommand`. Коли знову знадобиться API validation,
snapshot треба згенерувати заново з актуальної збірки.

Пошук після змін не знаходить у пакеті:

- `Unity Assistant`;
- `Unity AI Assistant`;
- `com.unity.ai.assistant`;
- `Unity.RunCommand`;
- `IRunCommand`;
- `CommandScript`;
- `Compatibility router`;
- `legacy script operations`.

Package version і relay binaries не змінювались.

## 2026-05-07 - User-facing documentation trim

Скорочено і переписано `Documentation~/unibridge.md` як практичну
користувацьку документацію пакета.

Прибрано з документа:

- Patreon/distribution notes;
- release checklist;
- relay build commands;
- внутрішні source-path нотатки;
- rollback/archive wording;
- зайві preview/business/internal-release формулювання.

Залишено тільки те, що потрібно користувачу пакета:

- що робить UniBridge;
- requirements;
- installation;
- first connection;
- як працює local relay;
- рекомендована MCP configuration;
- multiple open Unity projects;
- Project Settings;
- validation level;
- Editor/Batch auto-approval;
- MCP tools overview;
- connection approval;
- практичні workflows;
- troubleshooting.

Документ скорочено приблизно до 154 рядків. Package version, код і relay
binaries не змінювались.

## 2026-05-07 - Relay stale pipe reconnect fix

Виправлено сценарій, коли окремий Codex/MCP чат тримав `unibridge_relay_win.exe`
підключеним до pipe старого Unity Editor PID після перезапуску Unity. Симптом:
`_server_info` показував `connected: true`, але будь-яка Unity-команда падала з
`Pipe is broken`.

Зміни в `UniBridge.Relay/Program.cs`:

- `IsConnected` тепер враховує завершений reader task, а не тільки
  `CanRead/CanWrite`;
- при втраті pipe relay закриває поточний transport, чистить pending requests і
  на наступній команді вибирає актуальний discovery entry;
- Unity tool call і `tools/list` роблять один automatic reconnect/retry при
  transport failure;
- `_server_info` отримав diagnostic action `reconnect`, який примусово скидає
  поточний Unity pipe і підключається до актуального bridge;
- startup/ensure connection повторно надсилають client info після reconnect.

Version bump:

- relay: `1.1.0-build.8` -> `1.1.0-build.9`;
- package: `0.1.0-pre.9` -> `0.1.0-pre.10`.

Оновлено `Documentation~/unibridge.md`: додано коротку troubleshooting нотатку
про stale pipes після перезапуску Unity.

Verification:

- `dotnet publish` успішно зібрав `win-x64`, `linux-x64`, `osx-x64`,
  `osx-arm64`;
- package binary `unibridge_relay_win.exe --version` показує
  `1.1.0-build.9`;
- smoke test напряму через package binary:
  `_server_info { action: "reconnect" }` підключився до Subaquatica pipe
  `\\.\pipe\unity-mcp-59f9f213-67356`;
- після reconnect `Unity_ManageScene` з `GetActive` повернув активну сцену
  `Assets/_Game/Scenes/Game.unity`.

## 2026-05-07 - Project-specific MCP server keys for multi-project Codex sessions

Знайдено проблему інтеграції Codex: `CodexIntegration` писав фіксований
TOML section `[mcp_servers.unibridge]`. Через це кожний Unity проект
перетирав один і той самий MCP server entry, а Codex бачив лише один
namespace `mcp__unibridge__`.

Зміни:

- додано `Settings/Utilities/McpServerKeyUtility.cs`;
- ключі MCP server тепер мають формат
  `unibridge_<project_slug>_<project_id_8>`, наприклад
  `unibridge_domovyk_9e06e2a3`;
- `CodexIntegration` тепер пише `[mcp_servers.<project_key>]` і видаляє
  старий unscoped `[mcp_servers.unibridge]`;
- generic JSON integrations/snippet також використовують project-specific
  key, щоб кілька проектів не перетирали одне одного;
- документацію оновлено: для кількох відкритих Unity проектів потрібен один
  MCP server entry на проект з відповідним `--project-id`;
- package version bump: `0.1.0-pre.10` -> `0.1.0-pre.11`.

Локальний Codex config `C:/Users/Cidonix/.codex/config.toml` оновлено вручну:

- видалено старий `[mcp_servers.unibridge]` без `--project-id`;
- додано `unibridge_domovyk_prototype_98ba710e` для project id
  `98ba710e44ec47c4a76d198a1ce7423f`;
- додано `unibridge_domovyk_9e06e2a3` для project id
  `9e06e2a3f11c456fba6a4f50d9cd83c9`;
- backup перед зміною:
  `C:/Users/Cidonix/.codex/config.toml.bak-20260507-230029`.

Щоб Codex побачив нові MCP namespaces, треба перезапустити Codex або
перезавантажити MCP servers у Codex. Unity проекти перезапускати не потрібно.

## 2026-05-09 - Unity console cleanup for embedded package test

У тестовому Unity проекті `H:/Repos/UnityRepos/UniBridge_Test_Project`
перевірено warning-и після встановлення `Packages/com.cidonix.unibridge`.

Знайдено 5 однакових warning-ів:

- Unity бачила `Packages/com.cidonix.unibridge/Documentation~.meta`, але
  папка `Documentation~` для package/service import поводиться як службова
  package documentation folder, через що Console повторювала повідомлення
  `A meta data file (.meta) exists but its folder ... can't be found`.

Виправлення в package source:

- видалено `com.cidonix.unibridge/Documentation~.meta`;
- видалено `com.cidonix.unibridge/Documentation~/unibridge.md.meta`;
- синхронізовано ці видалення в тестову копію пакета.

Додатково прибрано потенційне джерело інших Unity 6 warning-ів:

- видалено `Plugins/Shared/System.Runtime.CompilerServices.Unsafe.dll` і
  `.meta`;
- видалено explicit reference на `System.Runtime.CompilerServices.Unsafe.dll`
  з `Cidonix.UniBridge.MCP.Editor.asmdef`;
- оновлено `Third Party Notices.md`.

Причина: Unity 6/Collections уже приносить свою версію
`System.Runtime.CompilerServices.Unsafe`, а копія з UniBridge створювала
duplicate assembly warning під час import/reload.

## 2026-05-09 - Full MCP tool smoke after removing Unsafe.dll

Після видалення `System.Runtime.CompilerServices.Unsafe.dll` прогнано повний
smoke/stress через UniBridge MCP у тестовому проекті
`H:/Repos/UnityRepos/UniBridge_Test_Project`.

Покриття:

- усі 19 Unity MCP tools виконувались через relay
  `C:/Users/Cidonix/.unibridge/relay/unibridge_relay_win.exe`;
- створення/читання/редагування/валідація C# scripts;
- `Unity_ApplyTextEdits`, `Unity_ScriptApplyEdits`,
  `Unity_ManageScript` create/read/get_sha/validate/apply_text_edits/edit/update/delete;
- asset CRUD, material create/modify/duplicate/move/rename/search/reimport/delete;
- shader create/read/update/delete;
- scene/editor/menu/resource/console tools;
- GameObject create/find/get_components/add/remove/set component property/delete;
- prefab creation через `save_as_prefab`;
- external FBX import з локального Unity install;
- cleanup тимчасових assets, scripts, prefabs, imported model folder і scene objects.

Результат фінального smoke-прогону:

- 103 tool cases;
- 0 failures;
- 22 transient retries під час очікуваних Unity domain reload/recompile вікон;
- фінальна перевірка Console для `Warning/Error/Exception/Assert`: 0 записів.

Додаткові правки після smoke:

- `Unity_ReadConsole` тепер приймає `ConsoleLogType.Exception` і `Assert`,
  а `All` розгортається у `Error/Warning/Log/Exception/Assert`;
- виправлено `ManageAsset.MoveOrRenameAsset`: `AssetDatabase.MoveAsset`
  повертає порожній рядок при успіху, а не GUID;
- прибрано зайвий warning у `ManageGameObject.Create` для штатного сценарію
  `save_as_prefab=true`, коли `prefab_path` є цільовим шляхом нового prefab.

Окрема перевірка prefab-сценарію після правки:

- `Unity_ManageGameObject` створив prefab без warning-ів;
- Console після create: 0 warning/error/exception/assert;
- cleanup prefab/object/root folder успішний;
- фінальна Console: 0 записів.

Scratch JSON-звіти smoke-прогонів були видалені з кореня репозиторію після
перенесення підсумків у цей контекстний файл.

## 2026-05-09 - Legacy third-party library removal pass

Після smoke-перевірки `Unsafe.dll` виконано наступний агресивний cleanup
сторонніх залишків старого Unity AI Assistant/chat/cloud/tooling шару.

Фізично видалено з package source `com.cidonix.unibridge`:

- `Plugins/Markdig/` і `Plugins/Markdig.meta`;
- `Plugins/Tokenizer/` і `Plugins/Tokenizer.meta`;
- `Plugins/Shared/Google.Protobuf.dll` і `.meta`;
- `Plugins/Shared/Microsoft.Bcl.AsyncInterfaces.dll` і `.meta`;
- `Plugins/Shared/Microsoft.Bcl.HashCode.dll` і `.meta`;
- `Plugins/Shared/Microsoft.Bcl.Memory.dll` і `.meta`;
- `Plugins/Shared/System.Text.Encodings.Web.dll` і `.meta`;
- `Plugins/Shared/System.Text.Json.dll` і `.meta`;
- `Plugins/Shared/System.Threading.Tasks.Extensions.dll` і `.meta`;
- `ThirdParty~/ripgrep/` разом з порожнім `ThirdParty~/`.

Залишено тільки залежності, які зараз реально потрібні C# script tools:

- `Plugins/CodeAnalysis/Microsoft.CodeAnalysis.dll`;
- `Plugins/CodeAnalysis/Microsoft.CodeAnalysis.CSharp.dll`;
- `Plugins/Shared/System.Collections.Immutable.dll`;
- `Plugins/Shared/System.Reflection.Metadata.dll`.

`Third Party Notices.md` очищено від видалених компонентів. Активні notices
тепер стосуються Roslyn, мінімальних .NET/Roslyn dependencies,
Newtonsoft.Json через Unity package wrapper і Unity modules з `package.json`.

Такі ж видалення синхронізовано в embedded package copy тестового проекту
`H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`.

Перевірка після видалення:

- повний smoke через `unibridge_relay_win.exe --mcp --project-id
  ae4e323353aa487f9ea74d566b38eaac`;
- `tools/list` повернув `_server_info` + 19 Unity tools;
- пройдено 83 основні cases: editor, scene, menu, console, resources,
  asset CRUD, shader CRUD, C# create/read/hash/text edits/structured edits,
  Roslyn validation, ManageScript create/read/get_sha/validate, prefab,
  external FBX import і cleanup;
- усі не-GameObject tools відпрацювали успішно після видалення бібліотек;
- script/Roslyn сценарії підтвердили, що видалені BCL/System.Text.Json/
  Protobuf/Tokenizer/Markdig/ripgrep файли не потрібні для поточних MCP tools;
- фінальна Console після основного smoke: 0 `Warning/Error/Exception/Assert`.

У першому варіанті GameObject smoke було використано `primitive_type=Cube`
разом з `Rigidbody2D/CircleCollider2D`. Це коректно повернуло tool error:
об'єкт Cube має 3D collider, а 2D physics component не можна додавати до
GameObject з 3D physics/collider. Після цього GameObject блок перетестовано
правильним 2D-сценарієм:

- створено empty GameObject з `Rigidbody2D` і `CircleCollider2D`;
- перевірено find/get_components/get_component/add_component/
  set_component_property/modify/remove_component/delete;
- 11 GameObject retest cases;
- 0 failures;
- фінальна Console після retest: 0 `Warning/Error/Exception/Assert`;
- тимчасові `Assets/UniBridgeSmoke_*` і `Assets/ExternalModels/SmokeModel_*`
  cleanup-ом видалені.

Зведений висновок: після видалення legacy third-party залишків усі 19 Unity
MCP tools залишаються працездатними, а тестовий Unity проект не показує нових
console warning/error/exception/assert записів.

## 2026-05-09 - Internal tracing symbol cleanup

Прибрано останній старий compile symbol `ASSISTANT_INTERNAL` з активного коду
пакета. У `Cidonix.UniBridge.Tracing.TraceWriter` debug-only exception logging
тепер керується символом `UNIBRIDGE_INTERNAL`.

Це не змінює runtime-поведінку за замовчуванням, бо метод і далі викликається
тільки коли відповідний conditional compilation symbol визначено. Зміна
прибирає ще один брендовий артефакт старого Unity Assistant з UniBridge.

## 2026-05-09 - MCP tool names renamed to UniBridge

Публічні MCP tool names перейменовано з `Unity_*`/`Unity.*` на `UniBridge_*`
без runtime alias-ів для старих назв. MCP клієнти після оновлення tools list
мають бачити і викликати тільки нові назви:

- `UniBridge_ManageShader`;
- `UniBridge_ApplyTextEdits`;
- `UniBridge_CreateScript`;
- `UniBridge_DeleteScript`;
- `UniBridge_FindInFile`;
- `UniBridge_GetSha`;
- `UniBridge_ImportExternalModel`;
- `UniBridge_ListResources`;
- `UniBridge_ManageAsset`;
- `UniBridge_ManageEditor`;
- `UniBridge_ManageGameObject`;
- `UniBridge_ManageMenuItem`;
- `UniBridge_ManageScene`;
- `UniBridge_Script`;
- `UniBridge_ScriptCapabilities`;
- `UniBridge_ReadResource`;
- `UniBridge_ScriptApplyEdits`;
- `UniBridge_ValidateScript`;
- `UniBridge_ReadConsole`.

У всіх 19 tool attributes виставлено `EnabledByDefault = true`, щоб після
перейменування чистий або оновлений проект у Project Settings показував tools
увімкненими без ручного переналаштування старих `Unity_*` override-ів.

Внутрішні implementation detail класи на кшталт `ManageScript` поки залишено
без перейменування, бо вони не є публічною MCP назвою і використовуються
спеціалізованими script tools як спільний backend. Динамічну script tools
specification також оновлено під `UniBridge_*`, а фільтр scripting tools
зроблено case-insensitive, щоб нові назви `UniBridge_Script*` стабільно
потрапляли у specification.

## 2026-05-09 - UniBridge tool rename smoke findings

Після оновлення тестового проекту `UniBridge_Test_Project` relay успішно
підключився до проекту:

- relay `1.1.0-build.9`;
- project id `ae4e323353aa487f9ea74d566b38eaac`;
- `toolCount: 19`;
- `tools/list` показує нові публічні назви `UniBridge_*`.

Через MCP протестовано всі 19 tools: editor, scene, menu, console, resources,
asset operations, shader operations, create/read/hash/search/validate/edit
C# scripts, script router capabilities, structured script edits,
GameObject/component operations, prefab component inspection, external FBX
import і cleanup.

Під час тесту знайдено два дрібні polish issues і одразу виправлено в
першоджерелі пакета:

- schema generator для `List<Dictionary<string, object>>` більше не генерує
  хибну форму `Edits[].Item`; dictionary/json object items тепер описуються як
  вільні JSON objects;
- `UniBridge_Script.read` більше не пише `Warning` у Unity Console за
  звичайне використання підтримуваного read-route, а логить це як info/debug.

Додаткова перевірка після правок першоджерела:

- Roslyn syntax parse по 126 `.cs` файлах MCP Editor: `SYNTAX_ERRORS=0`.

Поточний smoke у тестовому проекті завершився з фінальною Unity Console
перевіркою: 0 `Warning`, `Error`, `Exception`, `Assert`. Для перевірки двох
polish-fix-ів потрібно ще раз оновити embedded package copy в тестовому
проекті з `H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge` і повторити smoke.

## 2026-05-09 - Retest after UniBridge tool polish fixes

Тестовий проект `H:/Repos/UnityRepos/UniBridge_Test_Project` оновлено з
актуального першоджерела пакета і повторно перевірено через Codex MCP.

Підключення:

- relay `1.1.0-build.9`;
- project id `ae4e323353aa487f9ea74d566b38eaac`;
- selected pipe `\\.\pipe\unity-mcp-336f26c7-93964`;
- Unity PID `93964`;
- `toolCount: 19`.

Підтверджені виправлення:

- server-side schema для `UniBridge_ApplyTextEdits` тепер описує `Edits` як
  масив вільних JSON objects (`additionalProperties: true`) без старого
  помилкового `Item`;
- `UniBridge_ApplyTextEdits` успішно застосував direct line/column edit object;
- `UniBridge_Script.read` успішно відпрацював без warning у Unity Console.

Повторний smoke покрив усі 19 публічних `UniBridge_*` tools:

- editor state/project root/windows/active tool/selection/prefab stage/tags/layers;
- active scene, hierarchy і build settings;
- menu cache/list/exists/execute;
- console clear/get/debug-log inspection;
- script create/read/resource search/SHA/text edits/structured edits/validate/delete;
- asset folder/material CRUD, search, duplicate, move, rename, prefab component
  inspection і cleanup;
- shader create/read/update/delete;
- GameObject create/find/get_components/get_component/set properties/add/remove
  component/modify/delete;
- external FBX import from `C:/Temp/unibridge-smoke/UBSmoke2Model.fbx` with
  scene object, prefab creation, and cleanup.

Результат:

- усі перевірені сценарії пройшли;
- фінальна Unity Console: 0 `Warning`, 0 `Error`, 0 `Exception`, 0 `Assert`;
- після cleanup не залишилось `UBSmoke2`, `UniBridgeSmoke2` або
  `ExternalModels` артефактів у `Assets`;
- під час створення/видалення C# скрипта Unity очікувано робив domain reload,
  relay перевибрав той самий live pipe і продовжив роботу.

## 2026-05-09 - UniBridge_ManagePrefab tool added

На основі prefab-шару зі старого `UnityAutoBot` додано окрему MCP-тулзу
`UniBridge_ManagePrefab` у першоджерело пакета:

- файл: `com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManagePrefab.cs`;
- додано стабільний Unity meta file `ManagePrefab.cs.meta`;
- групи тулзи: `core`, `assets`, `scene`;
- enabled by default, без legacy alias-ів у назві тулзи;
- публічні дії: `create`, `create_from_asset`, `instantiate`, `get_status`,
  `apply_overrides`, `revert_overrides`, `unpack`, `create_variant`,
  `open_stage`, `save_stage`, `close_stage`;
- підтримує prefab asset path/status/GUID, prefab instance roots, override
  counters, prefab stage snapshot, scene destination, parent target, transform
  placement і selection після instantiation;
- write-операції для prefab assets свідомо обмежені `Assets/...`, source assets
  можуть читатися з `Assets/...` або `Packages/...`.
- після оновлення пакета в Unity очікувана кількість enabled tools зміниться з
  19 на 20.

Документацію `Documentation~/unibridge.md` доповнено коротким розділом
`Work With Prefabs`, щоб користувачі розуміли різницю між
`UniBridge_ManageGameObject`, `UniBridge_ManageAsset` і
`UniBridge_ManagePrefab`.

Перевірка після додавання:

- Roslyn syntax parse по 127 `.cs` файлах MCP Editor: `SYNTAX_ERRORS=0`.

Ще потрібно після оновлення пакета у тестовому Unity-проекті перевірити
компіляцію Unity API і зробити smoke саме по `UniBridge_ManagePrefab`.

## 2026-05-09 - UniBridge_ManagePrefab smoke passed in test project

Тестовий проект `H:/Repos/UnityRepos/UniBridge_Test_Project` оновлено
актуальною копією пакета і перевірено через relay/MCP.

Підтвердження реєстрації:

- `tools/list` через relay повернув 21 entry разом із `_server_info`;
- публічних робочих `UniBridge_*` tools стало 20;
- `UniBridge_ManagePrefab` присутній у tool list і має очікувану schema.

Smoke для `UniBridge_ManagePrefab` пройшов 47 MCP-викликів:

- створено тимчасову папку `Assets/UBPrefabSmoke_1778342778572`;
- створено source GameObject і prefab через `create` + `connect_instance`;
- перевірено `get_status` по scene instance і prefab asset;
- перевірено `instantiate` з name/position/rotation/scale;
- додано override component і перевірено `apply_overrides`;
- додано інший override component і перевірено `revert_overrides`;
- створено prefab variant через `create_variant`;
- створено prefab copy через `create_from_asset`;
- перевірено `unpack` у режимі `completely`, після чого статус став
  `NotAPrefab`;
- відкрито, збережено і закрито Prefab Stage через `open_stage`,
  `save_stage`, `close_stage`;
- додатково перевірено `UniBridge_ListResources`,
  `UniBridge_ManageAsset.GetComponents`, `UniBridge_ManageAsset.Search`,
  cleanup scene objects і видалення тимчасової asset-папки.

Результат:

- hard failures: 0;
- final Unity Console: 0 `Warning`, 0 `Error`, 0 `Exception`, 0 `Assert`;
- тимчасових `UBPrefabSmoke_*` / `UBSmoke_*` asset artifacts після cleanup на
  диску не залишилось.

Окреме спостереження:

- шлях `Assets/UniBridgeSmoke` у Unity AssetDatabase в тестовому проекті дав
  stale-відповідь "asset, not folder", хоча на диску такого файла/папки немає.
  Щоб не змішувати цей старий артефакт із перевіркою нової prefab-тулзи, smoke
  виконано на унікальному шляху `Assets/UBPrefabSmoke_1778342778572`.

## 2026-05-09 - Prefab Mode editing via MCP fixed and verified

Під час ручної перевірки нової `UniBridge_ManagePrefab` створено демо-prefab
через MCP у тестовому проекті:

- asset path: `Assets/UniBridgeManual/Prefabs/UniBridge_DemoPrefab.prefab`;
- prefab asset GUID: `4f0ad51e4c833964d83b073241ddcf8c`;
- source scene object: `UniBridge_DemoPrefab_Source`;
- prefab root: `UniBridge_DemoPrefab`.

Після цього користувач попросив відкрити prefab у Unity Prefab Mode і додати
всередину текст. Перша спроба показала корисний дефект: `UniBridge_ManagePrefab`
коректно відкривав Prefab Stage, але `UniBridge_ManageGameObject` шукав target
objects тільки в `SceneManager.GetActiveScene()`. Через це він не бачив root
об'єкт поточного Prefab Stage і не міг додати child всередину відкритого
prefab asset.

Фікс внесено в першоджерело пакета і синхронізовано в embedded-копію тестового
проекту:

- файл: `Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManageGameObject.cs`;
- додано пошук root objects у `PrefabStageUtility.GetCurrentPrefabStage().scene`;
- Prefab Stage scene тепер перевіряється перед active scene;
- active scene лишається fallback-ом для звичайної роботи зі сценою;
- дублювання сцен відсікається, якщо Prefab Stage scene і active scene
  збігаються.

Після фікса через MCP виконано сценарій:

- `UniBridge_ManagePrefab.open_stage` відкрив
  `Assets/UniBridgeManual/Prefabs/UniBridge_DemoPrefab.prefab`;
- `UniBridge_ManageGameObject.create` створив child object
  `UniBridge_Demo_Label` під root `UniBridge_DemoPrefab`;
- до child object додано `TextMesh`;
- `TextMesh.text` виставлено в `UniBridge Prefab`;
- виставлено `fontSize=48`, `characterSize=0.12`, `anchor=MiddleCenter`,
  `alignment=Center`;
- `UniBridge_ManagePrefab.save_stage` зберіг prefab asset;
- `UniBridge_ManageEditor.GetPrefabStage` підтвердив відкритий Prefab Stage,
  root `UniBridge_DemoPrefab`, `dirty=false`.

Перевірка після фікса:

- Roslyn syntax parse по 127 `.cs` файлах MCP Editor: `SYNTAX_ERRORS=0`;
- Unity Console у тестовому проекті після операції:
  0 `Warning`, 0 `Error`, 0 `Exception`, 0 `Assert`.

Висновок: `UniBridge_ManagePrefab` тепер покриває не тільки asset/instance
операції, а й робочий workflow "відкрити prefab у Prefab Mode -> редагувати
його вміст через звичайні GameObject tools -> зберегти prefab".

## 2026-05-09 - Full 20-tool smoke after prefab workflow

Після фікса Prefab Stage пошуку виконано повний smoke-прогін усіх публічних
MCP tools у тестовому Unity-проекті
`H:/Repos/UnityRepos/UniBridge_Test_Project`.

Перед фінальним прогоном перший smoke виявив три корисні моменти:

- `UniBridge_ScriptApplyEdits` треба тестувати структурованим edit object з
  `op`, наприклад `replace_range`; raw line edit без `op` коректно
  відхиляється як неправильний формат;
- `UniBridge_ManageGameObject.modify` не треба використовувати для повторного
  пошуку щойно вимкненого inactive object без окремої inactive-aware
  стратегії;
- `UniBridge_Script.update` і `UniBridge_Script.edit` були
  backward-compatible routes, але писали deprecation як `Warning` у Unity
  Console. Це створювало хибний шум під час нормального smoke.

Зроблений package-фікс:

- `ManageScript.cs` тепер логить `UniBridge_Script.update is deprecated...`
  через `McpLog.Log`, а не `McpLog.Warning`;
- `UniBridge_Script.edit is deprecated...` також переведено з
  `Debug.LogWarning` на `McpLog.Log`;
- правку внесено в source package і embedded package copy тестового проекту;
- Roslyn syntax parse source package: `FILES=127 SYNTAX_ERRORS=0`;
- Roslyn syntax parse embedded test package copy: `FILES=127 SYNTAX_ERRORS=0`.

Фінальний smoke-прогін:

- `tools/list` повернув 21 entry: `_server_info` + 20 публічних
  `UniBridge_*` tools;
- покрито всі 20 tools:
  `UniBridge_ApplyTextEdits`, `UniBridge_CreateScript`,
  `UniBridge_DeleteScript`, `UniBridge_FindInFile`, `UniBridge_GetSha`,
  `UniBridge_ImportExternalModel`, `UniBridge_ListResources`,
  `UniBridge_ManageAsset`, `UniBridge_ManageEditor`,
  `UniBridge_ManageGameObject`, `UniBridge_ManageMenuItem`,
  `UniBridge_ManagePrefab`, `UniBridge_ManageScene`,
  `UniBridge_ManageShader`, `UniBridge_ReadConsole`,
  `UniBridge_ReadResource`, `UniBridge_Script`,
  `UniBridge_ScriptApplyEdits`, `UniBridge_ScriptCapabilities`,
  `UniBridge_ValidateScript`;
- 93 smoke cases;
- hard failures: 0;
- Unity Console findings під час і після smoke:
  0 `Warning`, 0 `Error`, 0 `Exception`, 0 `Assert`.

Покриті сценарії:

- editor/project/window/selection/prefab-stage/tags/layers state;
- scene active/hierarchy/build-settings/save;
- menu cache/list/exists/execute;
- console clear/get/log sampling;
- resource list/read/find/SHA;
- C# lifecycle: create, exact text edit, structured `replace_range`, validate,
  compatibility `UniBridge_Script.update`, capabilities, delete;
- asset CRUD: folders, material create/info/modify/duplicate/move/search/reimport;
- shader create/read/update/delete;
- GameObject create/find/get components/get component/set property/add/remove
  component/modify/delete;
- prefab create/connect/status/instantiate/apply overrides/revert overrides/
  create variant/create from asset/unpack/open stage/edit child TextMesh/save
  stage/close stage/component inspection;
- external FBX import from a local Unity install FBX, scene object cleanup, and
  imported asset folder cleanup.

Під час C# script operations Unity очікувано проходила domain reload. Relay
stderr мав 9 коротких transient reconnect observations
`Unity not detected. No matching UniBridge discovery file was found`, але це
не потрапляло в Unity Console як warning/error і не ламало MCP calls.

Cleanup:

- тимчасова папка `Assets/UBSmokeFull_1778344886009` видалена;
- тимчасова папка `Assets/ExternalModels/UBSmokeFullModel_1778344886009`
  видалена;
- порожня `Assets/ExternalModels` після імпорт-тесту також видалена;
- фінальна перевірка `Assets` не знайшла `UBSmokeFull_*`, `UBPrefabSmoke_*`,
  `UniBridgeSmoke*` або `ExternalModels`;
- фінальна Unity Console перевірка: 0 проблемних записів.

## 2026-05-09 - Console Intelligence started in UniBridge_ReadConsole

Розпочато перенесення корисної концепції Console Intelligence з
`UnityAutoBot` у поточну MCP-first архітектуру UniBridge без перенесення
старого action system.

Зміни зроблено в першоджерелі пакета:

- `Modules/Cidonix.UniBridge.MCP.Editor/Tools/ReadConsole.cs`;
- `Modules/Cidonix.UniBridge.MCP.Editor/Tools/Parameters/ReadConsoleParams.cs`.

`UniBridge_ReadConsole` лишив старі сумісні дії `Get` і `Clear`, але отримав
нові режими:

- `Overview` - компактні totals, recent entries і top grouped issues;
- `Groups` - групування повідомлень за fingerprint з лічильниками повторів;
- `GroupDetails` - деталізація конкретної групи по `Fingerprint`;
- `Timeline` - зріз поточної Unity Console у порядку backlog entries;
- `DiagnosticSummary` - AI-oriented summary: critical issues, warnings,
  likely spam, recent samples і timeline highlights;
- `Search` - пошук console entries за `FilterText`.

Реалізація поки on-demand: кожен запит читає актуальний Unity Editor Console
через наявний internal `UnityEditor.LogEntries` reflection path і будує
структурований зріз у пам'яті. Фоновий durable-запис на диск спеціально не
додано на цьому етапі, щоб не ускладнювати пакет і не створювати зайвих
перформанс-ризиків.

Локальна перевірка:

- Roslyn syntax parse по 127 `.cs` файлах MCP Editor: `SYNTAX_ERRORS=0`;
- у Plastic змінені тільки `ReadConsole.cs` і `ReadConsoleParams.cs`.

## 2026-05-09 - Console Intelligence smoke in Unity test project

Оновлені `ReadConsole.cs` і `ReadConsoleParams.cs` синхронізовано в embedded
package тестового проекту `H:/Repos/UnityRepos/UniBridge_Test_Project`.

Важливий нюанс тестування:

- після копіювання файлів Unity не одразу перебудувала
  `Cidonix.UniBridge.MCP.Editor.dll`, тому `UniBridge_ReadConsole` спочатку
  ще показував стару schema тільки з `Get`/`Clear`;
- м'який compile trigger через тимчасовий `Assets/__UniBridgeCompileTrigger.cs`
  змусив Unity перебудувати assembly;
- після rebuild завантажений enum `ConsoleAction` містив:
  `Get`, `Clear`, `Overview`, `Groups`, `GroupDetails`, `Timeline`,
  `DiagnosticSummary`, `Search`;
- тимчасовий compile trigger script після перевірки видалено.

Через окремий процес `unibridge_relay_win.exe --mcp --project-id
ae4e323353aa487f9ea74d566b38eaac --name ConsoleIntelligenceSmoke2`
перевірено MCP `tools/list` і нові дії:

- `tools/list` для `UniBridge_ReadConsole` повернув усі 8 actions;
- `Action` description оновлено і більше не каже тільки `get or clear`;
- `Overview`: success, 34 filtered entries, 27 groups, 5 top groups;
- `Groups`: success, 5 returned groups із 27;
- `Timeline`: success, 10 returned events;
- `DiagnosticSummary`: success, 0 critical, 0 warnings, 0 spam у чистій
  тестовій консолі;
- `Search` з `FilterText=UniBridge`: success, 42 matches;
- `GroupDetails`: success, повернув 5 samples для вибраного fingerprint.

Після smoke перевірено Unity Console через `UniBridge_ReadConsole.Get`:

- 0 `Warning`;
- 0 `Error`;
- 0 `Exception`;
- 0 `Assert`.

Source package і embedded package test copy для двох змінених файлів
ідентичні (`Compare-Object` count 0).

Додатковий payload-polish після першого smoke:

- `Groups` більше не рахує console groups двічі для одного запиту;
- `DiagnosticSummary.summary.likelySpam` тепер повертає lowercase поля,
  узгоджені з рештою Console Intelligence payload:
  `fingerprint`, `type`, `count`, `representativeMessage`, `firstEntryId`,
  `lastEntryId`.

Повторний smoke після payload-polish:

- 30 однакових `UniBridge_ManageEditor.GetState` викликів створили тестовий
  повторюваний log pattern;
- `Overview`: success, 79 filtered entries, 20 groups;
- `Groups`: success, first group count 30;
- `Timeline`: success, 10 events;
- `DiagnosticSummary`: success, 0 critical, 0 warnings, 2 likely spam groups;
- `likelySpam` field check підтвердив lowercase field names;
- `Search`: success, 87 matches;
- `GroupDetails`: success, 5 samples;
- фінальна Unity Console перевірка: 0 `Warning`, 0 `Error`, 0 `Exception`,
  0 `Assert`.

## 2026-05-09 - Console Intelligence tested after manual package update

Після ручного оновлення пакета користувачем у тестовому проекті
`H:/Repos/UnityRepos/UniBridge_Test_Project` виконано живий MCP smoke через
актуальний embedded package.

Перед тестом перевірено:

- relay підключений до `UniBridge_Test_Project`, project id
  `ae4e323353aa487f9ea74d566b38eaac`;
- Unity PID `93964`;
- `Cidonix.UniBridge.MCP.Editor.dll` реально містить `ConsoleAction` values:
  `Get`, `Clear`, `Overview`, `Groups`, `GroupDetails`, `Timeline`,
  `DiagnosticSummary`, `Search`;
- source package і embedded package test copy для `ReadConsole.cs` та
  `ReadConsoleParams.cs` ідентичні;
- тимчасових `Assets/__UniBridgeCompileTrigger.cs` файлів не лишилось.

Тестовий прогін:

- консоль очищено через `UniBridge_ReadConsole.Clear`;
- окремий `unibridge_relay_win.exe --mcp --project-id
  ae4e323353aa487f9ea74d566b38eaac --name ConsoleIntelligenceProjectTest`
  успішно підключився;
- `tools/list`: 21 tools total (`_server_info` + 20 public tools);
- `UniBridge_ReadConsole` schema повернула 8 actions:
  `Get`, `Clear`, `Overview`, `Groups`, `GroupDetails`, `Timeline`,
  `DiagnosticSummary`, `Search`;
- 30 `UniBridge_ManageEditor.GetState` calls створили повторюваний debug-log
  pattern для перевірки group/spam detection;
- `Overview`: success, 74 filtered entries, 16 total groups, 5 top groups,
  10 recent entries;
- `Groups`: success, 5 returned groups, first group count 30;
- `Timeline`: success, 10 events;
- `DiagnosticSummary`: success, 0 critical, 0 warnings, 2 likely spam groups;
- `likelySpam` fields: `fingerprint`, `type`, `count`,
  `representativeMessage`, `firstEntryId`, `lastEntryId`;
- `Search` з `FilterText=UniBridge`: success, 82 matches;
- `GroupDetails`: success, group count 30, 5 samples.

Фінальна перевірка Unity Console після smoke:

- 0 `Warning`;
- 0 `Error`;
- 0 `Exception`;
- 0 `Assert`.

## 2026-05-09 - Console Intelligence large-log stress test

На питання користувача перевірено саме сценарій "1000+ записів у Unity
Console, але AI не має отримувати весь сирий шум".

Тест виконано на `H:/Repos/UnityRepos/UniBridge_Test_Project` через окремий
relay process:

- `unibridge_relay_win.exe --mcp --project-id
  ae4e323353aa487f9ea74d566b38eaac --name ConsoleIntelligenceLargeLogTest`;
- перед тестом console очищено через `UniBridge_ReadConsole.Clear`;
- виконано 1050 однакових `UniBridge_ManageEditor.GetState` calls;
- додатково виконано `UniBridge_ManageScene.GetActive` і
  `UniBridge_ListResources`;
- через debug logs це створило 2114 filtered console entries.

Результати Console Intelligence:

- schema `UniBridge_ReadConsole` підтвердила 8 actions:
  `Get`, `Clear`, `Overview`, `Groups`, `GroupDetails`, `Timeline`,
  `DiagnosticSummary`, `Search`;
- `Overview`: 2114 entries, 16 groups, 5 top groups, 10 recent entries;
- `Groups`: 8 returned groups із 16, перша група count 1050;
- top groups показали головний шум без повного дампу:
  `Tool 'UniBridge_ManageEditor' completed successfully` x1050 і
  `Executing tool 'UniBridge_ManageEditor'` x1050;
- `DiagnosticSummary`: 0 critical, 0 warning issues, 2 likely spam groups;
- `Search` по `Executing tool`: 1057 matches;
- `Timeline`: 12 останніх events;
- `GroupDetails` для найбільшої групи: group count 1050, але повернуто тільки
  5 representative samples.

Висновок:

- large console backlog не треба передавати AI повністю;
- `Overview` дає компактний стан;
- `Groups` стискає повторювані повідомлення у fingerprint groups;
- `DiagnosticSummary` піднімає шумові групи в `likelySpam`;
- `GroupDetails` дозволяє дозовано відкрити конкретну групу;
- `Timeline` дає короткий хвіст подій.

Фінальна Unity Console перевірка після stress test:

- 0 `Warning`;
- 0 `Error`;
- 0 `Exception`;
- 0 `Assert`.

## 2026-05-09 - Post-run console timeline analysis added

Після обговорення з користувачем прийнято рішення не робити runtime capture:

- не підписуватись на `Application.logMessageReceivedThreaded`;
- не тримати власний великий buffer у пам'яті під час Play Mode;
- не писати JSONL/log файл під час роботи сцени;
- аналізувати вже існуючий Unity Console backlog тільки on-demand після
  тестового прогону або після зупинки Play Mode.

У `UniBridge_ReadConsole` додано другий шар Console Intelligence для сценарію:
"у Console 1000+ записів, треба знайти корисну ділянку, не передаючи все AI".

Зміни в параметрах:

- новий action `TimelineWindow`;
- новий action `ImportantRanges`;
- `StartEntryId` / `EndEntryId` для точного діапазону timeline;
- `CenterEntryId` + `ContextBefore` / `ContextAfter` для вікна навколо події;
- `Fingerprint` тепер можна використовувати не тільки для `GroupDetails`, а й
  для `TimelineWindow`;
- `CollapseRepeats` / `CollapseThreshold` для run-length стискання
  послідовних однакових console entries;
- `MaxRanges` для обмеження кількості автоматично знайдених важливих ділянок.

Нові режими:

- `Timeline` лишився сумісним tail/range timeline, але тепер може враховувати
  `StartEntryId`, `EndEntryId`, `CenterEntryId` і додатково повертати
  `compressedEvents`;
- `TimelineWindow` повертає focused window за range/center/fingerprint,
  `window` totals, raw `events` з лімітом `MaxEvents` і compact
  `compressedEvents`;
- `ImportantRanges` автоматично шукає ділянки навколо warning/error/exception,
  довгі repeated runs, зміни патерну після спаму і прості alternating patterns.

Важлива гарантія:

- ці зміни не додають runtime overhead під час гри;
- весь аналіз запускається тільки коли MCP client явно викликає
  `UniBridge_ReadConsole`;
- навіть широкий range не повинен випадково віддати весь backlog сирим списком,
  бо raw `events` лімітується `MaxEvents`, а стиснений огляд іде через
  `compressedEvents`, `groups`, `noiseGroups` і `ranges`.

Локальна перевірка:

- Roslyn syntax parse по 127 `.cs` файлах MCP Editor: `SYNTAX_ERRORS=0`.

Живий stress/smoke на тестовому Unity-проекті:

- embedded package у `H:/Repos/UnityRepos/UniBridge_Test_Project` оновлено
  для перевірки поточних source змін;
- через MCP створено тимчасові editor-only emitters з контрольними
  `UB_STRESS` / `UB_STRESS_MID` markers;
- додатково виконано 560 MCP calls після фінального compile, що створило
  1200 console backlog entries;
- `Overview`: 1200 entries, 49 groups;
- totals: 1194 logs, 2 warnings, 2 errors, 2 exceptions, 0 asserts;
- `DiagnosticSummary` підняв контрольні exceptions/errors/warnings нагору і
  окремо показав spam groups:
  `UniBridge_ManageEditor completed successfully` x547 і
  `Executing tool 'UniBridge_ManageEditor'` x547;
- `ImportantRanges`: знайшов critical context range `0..59` з 60 events,
  2 warnings, 2 errors і 2 exceptions, а також alternating-pattern ranges для
  службового шуму;
- `Search` з `FilterText="[UB_STRESS]"`: 9 entries, 0 false positives;
- `Search` з `FilterText="[UB_STRESS_MID]"`: 7 entries, 0 false positives;
- `TimelineWindow` за `CenterEntryId=14` повернув focused window `10..19`
  і дозволив побачити саме послідовність навколо конфлікту;
- `TimelineWindow` за important range повернув 30 raw events із 60 total,
  тобто raw output лишився обмеженим;
- `GroupDetails` для noise fingerprint повернув 5 samples, а не весь spam.

Під час stress тесту знайдено і виправлено:

- `FilterText` тепер шукає тільки в `entry.Message`, а не в повному
  `FullMessage` зі stacktrace, тому `Search` більше не підмішує службові
  `UniBridge_ReadConsole` записи як false positives;
- `Debug.LogException(...)` у Unity 6 може приходити як `Error` за mode bits,
  тому тип уточнюється через `Debug.LogException` або перший рядок message;
  при цьому звичайні UniBridge logs більше не стають `Exception` лише через
  `System.Exception` у stack-frame signatures.

Після тесту:

- тимчасові файли `Assets/__UniBridgeConsoleStressEmitter.cs`,
  `Assets/__UniBridgeConsoleStressEmitter2.cs` і
  `Assets/__UniBridgeCompileTrigger.cs` видалені з тестового проекту;
- Unity Console очищено;
- фінальна перевірка після cleanup: 0 warnings/errors/exceptions.

## 2026-05-10 - Console session markers added

На прохання користувача додано session-marker workflow для сценарію:
"поставити мітку перед тестом/Play Mode, потім аналізувати тільки записи
після неї".

Зміни в `UniBridge_ReadConsole`:

- новий action `MarkSession`;
- новий параметр `AfterMarkerId`;
- новий параметр `IncludeMarker`;
- новий параметр `MarkerLabel`;
- `MarkSession` пише у Unity Console спеціальний log:
  `[UniBridge Console Marker] id=... label="..." utc=...`;
- marker id також зберігається у `UnityEditor.SessionState`, щоб після
  domain reload/refresh marker лишався відомим поточній Unity-сесії;
- усі основні режими (`Get`, `Search`, `Overview`, `Groups`, `GroupDetails`,
  `Timeline`, `TimelineWindow`, `DiagnosticSummary`, `ImportantRanges`) можуть
  фільтрувати дані через `AfterMarkerId`;
- якщо marker entry ще є в Console backlog, фільтр відсікає все до нього;
- якщо marker entry зник після domain reload, але marker id є в SessionState,
  інструмент аналізує поточний backlog і явно повертає `fallbackReason`.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- source package і embedded test package синхронізовані;
- schema `UniBridge_ReadConsole` показала actions:
  `Get`, `Clear`, `Overview`, `Groups`, `GroupDetails`, `Timeline`,
  `TimelineWindow`, `DiagnosticSummary`, `ImportantRanges`, `Search`,
  `MarkSession`;
- schema також показала `AfterMarkerId`, `IncludeMarker`, `MarkerLabel`;
- domain reload fallback smoke:
  - `MarkSession` створив marker `ubm-20260509220723-e5da435b`;
  - тимчасовий `__UniBridgeMarkerSmokeEmitter2.cs` створив після reload
    log/warning/error/exception;
  - `Overview` з `AfterMarkerId` повернув `marker.found=true`,
    `markerEntryFound=false`, `fallbackReason=...`;
  - totals після marker: 27 logs, 1 warning, 1 error, 1 exception;
  - `Search` по `[UB_MARKER_SMOKE_2]`: 4 entries;
  - `ImportantRanges`: знайшов critical context;
- no-reload smoke:
  - `MarkSession` створив marker `ubm-20260509220851-93f309dd`;
  - marker entry знайдено (`markerEntryFound=true`, `markerEntryId=2`);
  - після 5 `UniBridge_ManageEditor.GetState` calls `AfterMarkerId`
    повернув 15 filtered entries;
  - `Search` marker prefix з `IncludeMarker=false`: 0 entries;
  - `Search` marker prefix з `IncludeMarker=true`: 1 entry.

Після тесту:

- тимчасові marker smoke scripts видалені з тестового проекту;
- Unity Console очищено;
- фінальна перевірка: 0 warnings/errors/exceptions;
- source `ReadConsole.cs` / `ReadConsoleParams.cs` і embedded test package
  copies ідентичні (`DiffCount=0`).

## 2026-05-10 - Context snapshot tool in progress

Почато додавання `UniBridge_ContextSnapshot` як орієнтаційного інструмента
для ШІ-агента перед роботою з проєктом.

Запланована і реалізована в source package структура:

- новий параметричний record `ContextSnapshotParams`;
- режими `Brief`, `Standard`, `Detailed`;
- секції snapshot: project identity, package version, editor state, active tool,
  loaded scenes, prefab stage, selection, bounded hierarchy, console diagnostics,
  asset summary, tool summary, optional editor windows/build settings;
- hierarchy summary обмежений `HierarchyDepth` і `MaxSceneObjects`, без
  глибокої серіалізації компонентів;
- console summary використовує існуючий `UniBridge_ReadConsole`
  `DiagnosticSummary`, щоб не дублювати логіку console intelligence.

Документацію `Documentation~/unibridge.md` доповнено коротким розділом
`Build A Context Snapshot`.

Наступний крок: синхронізувати пакет у тестовий Unity-проєкт і перевірити
компіляцію та runtime-виклик нового MCP tool.

Перевірка у `H:/Repos/UnityRepos/UniBridge_Test_Project` виконана:

- source package і embedded test package синхронізовані для нових файлів;
- `Assets/Refresh` пройшов, Unity Console після recompilation:
  0 warnings/errors/exceptions;
- direct JSON-RPC через `unibridge_relay_win.exe --mcp --project-id
  ae4e323353aa487f9ea74d566b38eaac` успішно викликав
  `UniBridge_ContextSnapshot`;
- перевірені режими `Brief`, `Standard`, `Detailed`;
- snapshot повертає `project=UniBridge_Test_Project`,
  `activeScene=SampleScene`, `packageVersion=0.1.0`;
- `Brief` повернув 6 hierarchy objects, `Standard`/`Detailed` повернули 8;
- `assets.totalAssets=9`, `tools.total=22`;
- `Detailed` включив `windows`;
- після runtime smoke test Unity Console має 0 warnings/errors/exceptions.

## 2026-05-10 - Batch actions tool in progress

Почато реалізацію `UniBridge_BatchActions`.

Мета: дати ШІ-агенту можливість виконувати кілька пов'язаних Unity-операцій
одним планованим workflow, але з dry-run і валідацією перед змінами.

Поточна реалізація в source package:

- новий tool `UniBridge_BatchActions`;
- `DryRun=true` за замовчуванням, щоб випадковий виклик не змінював проєкт;
- `ValidateBeforeExecute=true`;
- `StopOnError=true`;
- `UseUndoGroup=true` для реального виконання;
- ліміт 50 steps;
- підтримка `Steps` / `actions`;
- підтримка компактної форми step без вкладеного `parameters`;
- allow-list замість довільного виклику будь-якого MCP tool:
  `UniBridge_ManageGameObject`, `UniBridge_ManageAsset`,
  `UniBridge_ManageScene`, `UniBridge_ManagePrefab`,
  `UniBridge_ManageEditor`, `UniBridge_ManageShader`,
  `UniBridge_CaptureView`, `UniBridge_ContextSnapshot`;
- aliases для steps: `game_object`, `asset`, `scene`, `prefab`, `editor`,
  `shader`, `capture`, `context`;
- скриптові edit tools навмисно не включені в batch layer, бо вони мають
  використовувати окремий SHA/precondition workflow.

Валідація першої версії:

- перевіряє allow-list, наявність tool і чи tool увімкнений у settings;
- перевіряє required action/target/path/component поля для основних scene,
  asset, prefab, editor і shader дій;
- шукає GameObject targets по id/name/path у loaded scenes і Prefab Stage;
- перевіряє, що asset/prefab paths лишаються під `Assets/`;
- перевіряє component type names через `TypeCache`.

Документацію `Documentation~/unibridge.md` доповнено розділом
`Run Batch Actions`.

Наступний крок: перенести пакет у тестовий Unity-проєкт, перевірити
компіляцію, dry-run, виконання batch і чистоту Console.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project` виконано:

- `BatchActions.cs` і `.meta` перенесені в embedded package;
- `Assets/Refresh` після першого переносу показав compile error
  `CS0165: Use of unassigned local variable 'normalizedPath'`;
- flow-analysis помилку виправлено явною ініціалізацією `normalizedPath`;
- повторний `Assets/Refresh`: 0 warnings/errors/exceptions;
- direct JSON-RPC через `unibridge_relay_win.exe --mcp --project-id
  ae4e323353aa487f9ea74d566b38eaac`;
- valid dry-run batch:
  - `game_object create` тимчасового `UniBridge_BatchProbe_*`
    з `Rigidbody2D` і `CircleCollider2D`;
  - `asset CreateFolder` тимчасової папки `Assets/UniBridgeBatchSmoke_*`;
  - `context Brief`;
  - результат: success=true, 3 validated, 0 executed, 0 validation errors;
- invalid dry-run batch з компонентом `DefinitelyMissingComponent`:
  - результат: success=false;
  - validation error коректно вказав, що component type не знайдений;
- execute batch з тими ж valid steps:
  - success=true;
  - 3 validated, 3 executed, 3 succeeded;
  - подальші `ManageGameObject.find` і `ManageAsset.GetInfo` підтвердили
    створені об'єкт і папку;
- cleanup batch:
  - видалив тимчасовий GameObject;
  - видалив тимчасову Assets-папку;
  - success=true, 2 executed, 2 succeeded;
- фінальна Unity Console перевірка: 0 warnings/errors/exceptions.

## 2026-05-11 - UI layout primitives

Почато наступний крок розвитку `UniBridge_ManageUI`: layout-примітиви,
щоб ШІ-агент міг будувати Unity UI через стандартні Unity UI layout
components, а не тільки через ручні `RectTransform` координати.

У source package додано:

- `UIAction.SetLayoutGroup`;
- `UIAction.SetContentSizeFitter`;
- `UIAction.SetLayoutElement`;
- параметри для `HorizontalLayoutGroup`, `VerticalLayoutGroup`,
  `GridLayoutGroup`, `ContentSizeFitter` і `LayoutElement`;
- `Inspect` тепер повертає `layoutComponents`;
- `UniBridge_BatchActions` отримав aliases і базову validation-підтримку
  для нових UI layout actions;
- `Documentation~/unibridge.md` оновлено розділом про layout groups,
  content size fitter і layout elements.

Мета: зробити створений агентом UI стійкішим до різної довжини тексту,
роздільної здатності екрана і зміни кількості дочірніх елементів.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- зміни перенесені в embedded package;
- `Assets/Refresh` пройшов без compile warnings/errors;
- `tools/list` показав нові параметри `UniBridge_ManageUI`;
- створено test canvas з `VerticalLayoutGroup`, `GridLayoutGroup`,
  `LayoutElement` і `ContentSizeFitter`;
- перевірено `Inspect` для container/grid і `UniBridge_BatchActions`
  dry-run aliases `layout_group` / `layout_element`;
- створено camera-space видимий canvas для `CaptureGameCamera`;
- capture path:
  `C:/Users/Cidonix/.unibridge/captures/UniBridge_Test_Project_ae4e3233/unibridge_ui_layout_visible_20260511105649.png`;
- фінальна Unity Console перевірка: 0 warnings/errors/exceptions.

## 2026-05-11 - UI audit

Почато другий крок після layout primitives: read-only дія
`UniBridge_ManageUI` -> `Audit`.

Мета: дати ШІ-агенту структурований звіт якості UI без зміни сцени.
Це потрібно, щоб після створення/правки Canvas можна було автоматично
знайти типові проблеми ще до screenshot/capture:

- `TEXT_OVERFLOW_RISK`;
- `SIBLING_OVERLAP`;
- `CHILD_OUTSIDE_PARENT`;
- `ZERO_OR_TINY_RECT`;
- `MULTIPLE_LAYOUT_GROUPS`;
- `MANUAL_CONTAINER_LAYOUT`;
- `MISSING_CANVAS_SCALER`;
- `MISSING_GRAPHIC_RAYCASTER`;
- `MISSING_EVENT_SYSTEM`;
- `SCREEN_SPACE_OVERLAY_CAPTURE_CAVEAT`.

У source package додано:

- `UIAction.Audit`;
- параметри `IncludeInactive`, `MaxIssues`, `AuditTolerance`;
- read-only scanner для Canvas, RectTransform, Text, Selectable і layout
  components;
- aliases для `UniBridge_BatchActions`: `audit`, `audit_layout`,
  `check_ui`;
- документацію доповнено описом UI audit workflow.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- зміни перенесені в embedded package;
- після `Assets/Refresh` relay тимчасово втратив discovery-файл під час
  recompilation, після `_server_info reconnect` підключення відновилось:
  `unityConnected=true`, `toolCount=28`;
- Unity Console після recompilation: 0 warnings/errors/exceptions;
- `tools/list` підтвердив наявність `Audit`;
- `Audit` на `UniBridge_Dashboard_Canvas` просканував 35 RectTransform,
  17 Text і 6 Button;
- результат audit: 2 info, 0 warnings, 0 errors;
- sample findings:
  - `MANUAL_CONTAINER_LAYOUT` для `UB_LeftRail`;
  - `MANUAL_CONTAINER_LAYOUT` для `UB_MainCard`;
- `UniBridge_BatchActions` dry-run з alias `check_ui` пройшов без
  validation errors/warnings;
- фінальна Unity Console перевірка: 0 warnings/errors/exceptions.

## 2026-05-11 - UI AutoFix

Почато третій крок UI tooling: консервативна дія
`UniBridge_ManageUI` -> `AutoFix`.

Концепція: `AutoFix` спирається на `Audit`, але за замовчуванням
виправляє тільки низькоризикові findings:

- `MISSING_CANVAS_SCALER` -> додає `CanvasScaler` з
  `ScaleWithScreenSize`, reference resolution `1920x1080`;
- `MISSING_GRAPHIC_RAYCASTER` -> додає `GraphicRaycaster`;
- `MISSING_EVENT_SYSTEM` -> створює `EventSystem` з input module,
  сумісним із проєктом: `InputSystemUIInputModule` для проєктів з
  Unity Input System, fallback на `StandaloneInputModule` для old-input-only;
- `TEXT_OVERFLOW_RISK` -> вмикає `Text.resizeTextForBestFit` з
  безпечними min/max font sizes.

`AutoFix` навмисно не рухає елементи, не вирішує overlaps і не додає
LayoutGroup у ручні контейнери за замовчуванням, бо це може сильно
змінити UI. Для цього лишаються audit findings/suggestions.

У source package додано:

- `UIAction.AutoFix`;
- параметри `FixCodes`, `MaxFixes`;
- `DryRun=true` preview для AutoFix;
- batch aliases `auto_fix`, `fix_ui`;
- документацію доповнено AutoFix workflow.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- зміни перенесені в embedded package;
- `Assets/Refresh` і `_server_info reconnect`: `unityConnected=true`,
  `toolCount=28`;
- Unity Console після recompilation: 0 warnings/errors/exceptions;
- створено контрольний broken canvas
  `UniBridge_AutoFix_BrokenCanvas_20260511112114`;
- перед AutoFix audit:
  - 4 warnings, 1 info;
  - codes: `SCREEN_SPACE_OVERLAY_CAPTURE_CAVEAT`,
    `MISSING_CANVAS_SCALER`, `MISSING_GRAPHIC_RAYCASTER`,
    `TEXT_OVERFLOW_RISK`, `SIBLING_OVERLAP`;
- `AutoFix DryRun` показав 3 механічні safe-fixes:
  - додати `CanvasScaler`;
  - додати `GraphicRaycaster`;
  - увімкнути Text BestFit;
- реальний `AutoFix` застосував ті самі 3 fixes;
- після AutoFix audit:
  - 1 warning, 2 info;
  - лишились тільки навмисно неавтоматичні/інформаційні findings:
    `SIBLING_OVERLAP`, `SCREEN_SPACE_OVERLAY_CAPTURE_CAVEAT`,
    `TEXT_OVERFLOW_RISK` вже як info, бо BestFit увімкнено;
- components canvas після fix: `RectTransform`, `Canvas`, `CanvasScaler`,
  `GraphicRaycaster`;
- `UniBridge_BatchActions` dry-run з alias `fix_ui` пройшов без validation
  errors/warnings;
- test canvas після перевірки видалено;
- фінальна Unity Console перевірка: 0 warnings/errors/exceptions.

## 2026-05-11 - UI EventSystem input module compatibility

Виправлено поведінку `UniBridge_ManageUI` при створенні або ремонті
`EventSystem`.

Проблема: у Unity-проєктах, де активний новий Input System, старий
`StandaloneInputModule` може сипати `InvalidOperationException` кожен
кадр:

```text
You are trying to read Input using the UnityEngine.Input class, but you
have switched active Input handling to Input System package.
```

Зміна:

- `EnsureEventSystemObject()` тепер створює `EventSystem` без жорстко
  заданого `StandaloneInputModule`;
- якщо в проєкті доступний `UnityEngine.InputSystem.UI.InputSystemUIInputModule`
  і проєкт не налаштований явно як old-input-only, UniBridge додає саме
  `InputSystemUIInputModule`;
- якщо існуючий `EventSystem` має `StandaloneInputModule`, а проєкту
  потрібен Input System module, UniBridge видаляє старий module і додає
  сумісний;
- якщо проєкт явно old-input-only, а на `EventSystem` лишився
  `InputSystemUIInputModule`, UniBridge також прибирає його і лишає
  `StandaloneInputModule`;
- fallback на `StandaloneInputModule` лишився для класичних Input Manager
  проєктів.

Оновлено документацію `Documentation~/unibridge.md` для опису цієї
поведінки.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- source `ManageUI.cs` перенесено в embedded package;
- після domain reload relay відновив підключення:
  `unityConnected=true`, `toolCount=28`;
- `UniBridge_ManageUI EnsureEventSystem` відремонтував сценовий
  `EventSystem`;
- capture metadata підтвердив components:
  `Transform`, `EventSystem`, `InputSystemUIInputModule`;
- повторна перевірка після мікроправки підтвердила, що `StandaloneInputModule`
  більше не присутній на `EventSystem`;
- тестовий canvas `UniBridge_UserBroken_Canvas_20260511113050` виправлено:
  додані `CanvasScaler` і `GraphicRaycaster`, текст скорочено, кнопки
  розведені, child повернено всередину parent;
- фінальний `Audit` для canvas:
  - 0 errors;
  - 0 warnings;
  - 1 info `MANUAL_CONTAINER_LAYOUT`;
- фінальна Unity Console після `Clear`: 0 warnings/errors/exceptions.

## 2026-05-11 - Agent-level UI RepairPlan and AutoFix modes

Продовжено розвиток `UniBridge_ManageUI` у напрямку зручної роботи
ШІ-агента з Unity UI без ручного підбору координат.

Додано:

- `UIAction.RepairPlan` - read-only план ремонту UI на основі `Audit`;
- `UIAutoFixMode`:
  - `Safe` - тільки низькоризикові setup/text fixes;
  - `Layout` - дозволяє локальні RectTransform правки для overlap/outside;
  - `Aggressive` - може конвертувати ручний контейнер у layout group;
- `AutoFixMode` параметр для `RepairPlan` і `AutoFix`;
- batch aliases `repair_plan`, `repair_ui`, `auto_fix_mode`, `fix_mode`;
- AutoFix для:
  - `MULTIPLE_LAYOUT_GROUPS`;
  - `CHILD_OUTSIDE_PARENT`;
  - `SIBLING_OVERLAP`;
  - `MANUAL_CONTAINER_LAYOUT`;
- автоматичне визначення `HorizontalLayoutGroup`, `VerticalLayoutGroup`
  або `GridLayoutGroup` для ручного контейнера;
- збереження розмірів direct UI children через `LayoutElement` перед тим,
  як Unity LayoutGroup починає керувати розкладкою.

Важливе виправлення audit-логіки: sibling overlap і child outside parent
тепер рахуються у локальному просторі батьківського `RectTransform`, а не
через world-space AABB. Це критично для ScreenSpaceCamera/WorldSpace UI,
де world-unit tolerance може приховати реальні UI проблеми.

Документацію `Documentation~/unibridge.md` оновлено: описано `RepairPlan`,
`AutoFixMode=Safe/Layout/Aggressive`, `DryRun` workflow і нюанс із
`LayoutElement` при агресивній конвертації manual layout у LayoutGroup.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- source-зміни перенесено в embedded package;
- створено контрольний canvas
  `UniBridge_AgentRepair_Canvas_20260511_agent_repair3`;
- початковий `Audit` знайшов:
  - `MANUAL_CONTAINER_LAYOUT`;
  - `TEXT_OVERFLOW_RISK`;
  - `CHILD_OUTSIDE_PARENT`;
  - `SIBLING_OVERLAP`;
- `RepairPlan AutoFixMode=Aggressive` повернув 4 auto-fixable proposals;
- `AutoFix AutoFixMode=Layout DryRun` preview-нув безпечні layout fixes і
  пропустив aggressive manual container conversion;
- `AutoFix AutoFixMode=Aggressive` застосував inferred `VerticalLayoutGroup`
  і додав `LayoutElement` direct children, не схлопнувши їхні розміри;
- після AutoFix audit: 0 warnings/errors, лишився тільки informational
  `TEXT_OVERFLOW_RISK` для BestFit text;
- ізольований game-camera capture:
  `C:/Users/Cidonix/.unibridge/captures/UniBridge_Test_Project_ae4e3233/unibridge_ui_repair3_after_autofix_isolated_20260511.png`;
- Unity Console після очищення і повторної перевірки:
  0 warnings/errors/exceptions.

## 2026-05-11 - TextMesh Pro support for UniBridge_ManageUI

Перед реалізацією переглянуто розпаковану reference implementation source:
`reference implementation source/Sidekick/JSONTextMeshProUGUI.cs`
та суміжні TMP serializers. Концептуально перенесено не старий код, а ідею
AI-readable TMP state: text, font asset/material, color, font size,
auto-sizing, overflow, alignment, rich text, margins/raycast.

У source package додано:

- `UIElementType.TextMeshProText`;
- `UIElementType.TextMeshProButton`;
- параметри `RichText`, `OverflowMode`, `FontAssetPath`,
  `CreateTmpFontAssetIfMissing`;
- створення TMP text/button через reflection до `TMPro.TextMeshProUGUI`,
  без hard dependency на TMP package;
- автоматичний вибір `TMP_FontAsset`:
  - explicit `FontAssetPath`, якщо заданий;
  - prefer main `LiberationSans SDF.asset` з TMP Essentials;
  - fallback на інші TMP font assets;
  - опційна спроба створити project-local default font asset;
- `Inspect` тепер повертає `textComponent` для legacy Text і TMP:
  font size, color, alignment, rich text, auto sizing, overflow mode,
  preferred size, overflow flag, raycast target, font asset path;
- `Audit` рахує TMP text разом із legacy Text і знаходить:
  `TEXT_OVERFLOW_RISK`, `TMP_MISSING_FONT_ASSET`, `VERY_SMALL_TEXT`;
- `AutoFix TEXT_OVERFLOW_RISK` для TMP вмикає `enableAutoSizing`
  з min/max font size;
- `UniBridge_BatchActions` отримав aliases для TMP element types і нових
  TMP параметрів;
- при створенні UI child всередині `LayoutGroup` явний `SizeDelta`
  зберігається як `LayoutElement`, щоб buttons/text blocks не схлопувались
  після layout recalculation.

Важливий практичний нюанс: якщо Unity показує `TMP Importer`, треба
імпортувати тільки `TMP Essentials`. `TMP Examples & Extras` для UniBridge
не потрібні. Під час тесту виявлено, що напряму обраний
`LiberationSans SDF - Fallback.asset` може провокувати TMP
`NullReferenceException` у layout calculation, тому selection logic
оновлено: fallback assets мають нижчий пріоритет, основний
`LiberationSans SDF.asset` вибирається першим.

Документацію `Documentation~/unibridge.md` оновлено: описано TMP element
types, параметри, TMP Essentials, font asset fallback і поведінку
`LayoutElement` у layout group containers.

Живий тест у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- source-зміни перенесено в embedded package;
- користувач імпортував `TMP Essentials`;
- створено контрольний canvas
  `UniBridge_TMP_Canvas_20260511_tmp4`;
- `TextMeshProText` і `TextMeshProButton` створені через
  `UniBridge_BatchActions`;
- `Inspect` підтвердив font asset:
  `Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset`;
- TMP Button має нормальний `RectTransform` width/height `380 x 66` і
  `LayoutElement preferredWidth=380 preferredHeight=66`;
- до AutoFix audit бачив TMP overflow warning;
- `AutoFix TEXT_OVERFLOW_RISK` увімкнув TMP auto sizing;
- після AutoFix: 0 warnings/errors, тільки info-level overflow notes для
  already-autosized TMP text;
- старі тестові canvases вимкнено, щоб не перекривали новий TMP smoke;
- ізольований capture:
  `C:/Users/Cidonix/.unibridge/captures/UniBridge_Test_Project_ae4e3233/unibridge_tmp_ui_final_isolated_20260511.png`;
- Unity Console після очищення і фінальної перевірки:
  0 warnings/errors/exceptions.

## 2026-05-11 - ScrollRect / viewport / content support

У `UniBridge_ManageUI` додано агентський workflow для списків і
прокручуваних панелей:

- `UIAction.CreateScrollView` створює стандартну ієрархію
  `ScrollView -> Viewport -> Content`;
- `Viewport` отримує `RectMask2D` за замовченням;
- `Content` отримує відповідний `VerticalLayoutGroup`,
  `HorizontalLayoutGroup` або `GridLayoutGroup` і `ContentSizeFitter`;
- підтримуються `ScrollDirection=Vertical/Horizontal/Both`,
  `MovementType=Clamped/Elastic/Unrestricted`, inertia, sensitivity,
  padding, spacing, grid cell size і constraint;
- `UIAction.AddScrollItem` додає один або кілька рядків у ScrollRect,
  приймаючи як target сам ScrollView, Viewport або Content;
- додано `ItemSizeDelta`, щоб розмір item не плутався з `SizeDelta`
  самого ScrollView container;
- `Inspect` тепер повертає `scrollRect`, `rectMask2D` і `mask` дані;
- `Audit` більше не вважає ScrollRect `Content`, який більший за viewport,
  помилкою `CHILD_OUTSIDE_PARENT`.

`UniBridge_BatchActions` отримав aliases для `CreateScrollView`,
`AddScrollItem`, scroll direction/movement параметрів і `ItemSizeDelta`.

Живий smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- створено `UniBridge_Scroll_Test_Canvas`;
- batch 5/5 успішно створив вертикальний inventory list, горизонтальну
  toolbar-панель і grid asset viewport;
- `Item 01` у вертикальному списку має `sizeDelta 402 x 44` і
  `LayoutElement preferredHeight=44`;
- horizontal toolbar item має `sizeDelta 140 x 106` і
  `LayoutElement preferredWidth=140`;
- grid content має `GridLayoutGroup cellSize=130 x 64`;
- усі viewports мають активний `RectMask2D`;
- `Audit`: 64 RectTransform, 44 text, 10 buttons, 0 issues;
- Unity Console після тесту: 0 warnings/errors/exceptions;
- clean camera capture:
  `C:/Users/Cidonix/.unibridge/captures/UniBridge_Test_Project_ae4e3233/unibridge_scrollrect_smoke_clean_20260511.png`.

## 2026-05-13 - Lifecycle polish, animated preview advance, generic asset authoring

Перед реалізацією ще раз переглянуто reference implementation:

- `reference implementation source/Sidekick/SceneObjectsAdvancer.cs`;
- `reference implementation source/Sidekick/GetSceneViewImage.cs`;
- `reference implementation source/Sidekick/GetGameViewImage.cs`;
- `reference implementation source/Sidekick/GetAssetImage.cs`;
- `reference implementation source/Actions/CreateOrUpdateAssetAction.cs`;
- `reference implementation source/Actions/CreateOrUpdateAssetActionProps.cs`;
- `reference implementation source/Sidekick/AssetProps.cs`.

У source package дополіровано reference-shaped editor lifecycle surface:

- `UniBridge_ManageEditor` тепер має очевидні direct actions
  `ExitPlayMode`, `GetPlayModeState`, `WaitIdle`, `SaveAssets`;
- додано singular alias `GenerateSolutionFile` до typed action API,
  не тільки до `BatchActions`;
- `BatchActions` нормалізує aliases `lifecycle`, `project_operations`,
  `wait_idle`, `save_assets`, `generate_solution_file`, `upsert`;
- `ToolGuide` і `Documentation~/unibridge.md` оновлено, щоб новий агент
  бачив ці lifecycle назви без здогадок.

Додано reference-informed `CaptureObjectAdvancer`:

- `CaptureView` і `CaptureAsset` приймають `AdvanceMs`,
  `SimulateParticles`, `SampleAnimations`;
- перед render helper best-effort симулює `ParticleSystem` і sample-ить
  `Animator` / legacy `Animation` через `AnimationMode`;
- response metadata повертає `advance`:
  `requested`, `advanceMs`, `simulateParticles`, `sampleAnimations`,
  `particleSystemsAdvanced`, `animatorsSampled`,
  `legacyAnimationsSampled`, `warnings`;
- `CaptureGrid` теж передає advance-параметри у per-cell preview render.

Розширено generic asset authoring у `UniBridge_ManageAsset`:

- додано `Action=CreateOrUpdate`;
- allowlist: `PhysicsMaterial`, `PhysicsMaterial2D`, `RenderTexture`,
  `TerrainLayer`, `AvatarMask`, `ShaderVariantCollection`;
- для `PhysicsMaterial2D`, `RenderTexture`, `AvatarMask` є прості
  практичні presets;
- update existing asset дозволений тільки якщо фактичний Unity type
  відповідає requested `AssetType`;
- `Modify` також вміє застосовувати generic properties до allowlisted
  asset types.

Живий MCP smoke у `H:/Repos/UnityRepos/UniBridge_Test_Project`:

- embedded package синхронізовано з source repo;
- `RefreshAssets`, `WaitIdle`, `RequestScriptCompilation`,
  `GetPlayModeState`, `SaveAssets`, `ExitPlayMode`,
  `GenerateSolutionFile` пройшли через MCP;
- `BatchActions DryRun` для lifecycle aliases validated 3/3;
- `ManageAsset CreateOrUpdate` створив і оновив smoke assets:
  `PhysicsMaterial2D`, `RenderTexture`, `AvatarMask`, `TerrainLayer`;
- `BatchActions DryRun` через `Tool=asset Action=upsert` validated 1/1;
- `CaptureView` і `CaptureAsset` на тимчасовому prefab з `ParticleSystem`
  повернули `particleSystemsAdvanced=1`;
- `CaptureView` на legacy `Animation` з package clip через serialized
  `m_Animation` повернув `legacyAnimationsSampled=1`;
- під час animation smoke Unity очікувано попередила, що package clip
  `HumanoidDefault` не marked Legacy; після cleanup консоль очищено;
- фінальна `ReadConsole DiagnosticSummary` після очищення:
  4 службових log entries, 0 warnings/errors/exceptions/asserts.

## 2026-05-13 - Asset context and scene view profile polish

Після повторного аудиту reference implementation port surface переглянуто:

- `reference implementation source/Sidekick/ReadAssets.cs`;
- `reference implementation source/Sidekick/ViewAssets.cs`;
- `reference implementation source/Sidekick/AssetSerializer.cs`;
- `reference implementation source/Sidekick/JSONAsset.cs`;
- `reference implementation source/Sidekick/ViewGameObjects.cs`;
- `reference implementation source/Sidekick/JSONGameObject.cs`;
- `reference implementation source/Actions/PrintGameObjectsAction.cs`.

У `UniBridge_AssetIntelligence` додано reference-shaped
`Action=Context`:

- один виклик повертає agent-friendly asset context:
  detail summary, text slice/chunks, serialized payload і guidance;
- `ContextProfile`: `Auto`, `Summary`, `Text`, `Serialized`, `Deep`;
- `Read` і `Context` підтримують `Chunks` для кількох точних line ranges
  в одному запиті;
- stale/mistyped asset paths у `Context` повертають fuzzy suggestions,
  а також optional `bestSuggestionContext`;
- `BuildSimilarAssetSuggestions` тепер явно повертає candidate `path`,
  щоб агенту не треба було діставати path із nested summary.

У `UniBridge_SceneObjectView` додано focus profiles:

- `Profile=Rendering`, `Physics2D`, `Physics3D`, `UI`, `Animation`,
  `VFX`, `Audio`, `Gameplay`;
- профілі автоматично додають корисні `IncludeComponentProperties`
  і вмикають focused serialized properties без Full-dump усього об'єкта;
- known component summaries розширено для:
  `Animator`, legacy `Animation`, `ParticleSystem`,
  `ParticleSystemRenderer`, `LineRenderer`, `TrailRenderer`,
  `AudioSource`, `SortingGroup`;
- optional package summaries через reflection:
  `UnityEngine.VFX.VisualEffect` і
  `UnityEngine.Playables.PlayableDirector`;
- component property filter тепер матчить не тільки exact type, а й base
  classes/interfaces, тому профільний filter `Renderer`, `Collider`,
  `MonoBehaviour` працює природно.

Оновлено `ToolGuide` і `Documentation~/unibridge.md`, щоб нові агенти бачили
`AssetIntelligence Context` і `SceneObjectView Profile` як рекомендовані
перші/verify calls.

Тестова перевірка:

- змінені package-файли синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  успішний: 0 errors;
- єдиний warning: старий Unity/Roslyn
  `System.Threading.Tasks.Extensions` version conflict, не пов'язаний зі
  змінами;
- live Unity MCP smoke виконано через UniBridge relay stdio:
  - `_server_info`: connected до
    `UniBridge_Test_Project`, editor pid `40252`;
  - `tools/list`: 40 tools, `UniBridge_AssetIntelligence` і
    `UniBridge_SceneObjectView` присутні;
  - `UniBridge_ManageEditor Action=WaitIdle`: Unity editor ready;
  - `UniBridge_AssetIntelligence Action=Context`
    для `Assets/Sprites/Item_0000.png`,
    `ContextProfile=Deep`: success, resolved 1, має `detail` і
    `serialized`;
  - `UniBridge_AssetIntelligence Action=Context`
    для missing path `Assets/Sprites/Item_0000_missing.png`:
    success, 3 fuzzy suggestions, `bestSuggestionContext=true`;
  - `UniBridge_SceneObjectView Action=Hierarchy Detail=Detailed Profile=VFX`:
    success, profile `VFX`, returned 20 objects with truncation metadata;
  - `UniBridge_ReadConsole Action=DiagnosticSummary`:
    27 log entries, 0 warnings/errors/exceptions/asserts.

## 2026-05-14 - MCP tool execution scheduler

Додано editor-wide execution scheduler для MCP tools:

- `ToolExecutionScheduler` класифікує tools як `ReadOnly`, `Mutating`,
  `Capture` або `CompileReload`;
- read-only calls можуть виконуватись паралельно;
- mutating/capture/compile lifecycle calls проходять через exclusive gate;
- fair queue gate не дає нескінченному потоку read-only calls голодом
  тримати writer/capture call;
- `McpToolAttribute.ExecutionPolicy` дозволяє явно перевизначати policy;
- `tools/list` тепер містить `annotations.uniBridgeExecution`, щоб новий
  агент одразу бачив, чи tool read-only, чи exclusive;
- `BatchActions` і `WorkflowRecipes` виконують вкладені tool calls через
  internal `ExecuteToolInsideCurrentLeaseAsync`, тобто wrapper бере lease один
  раз, а внутрішні кроки не можуть самі себе заблокувати.

Тестова перевірка:

- змінені package-файли синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  успішний: 0 errors;
- єдиний warning: старий Unity/Roslyn
  `System.Threading.Tasks.Extensions` version conflict, не пов'язаний зі
  змінами;
- live Unity MCP strict smoke виконано тільки через relay stdio:
  - `tools/list`: 57 tools, перевірено annotations для
    `UniBridge_UnitySearch`, `UniBridge_ManageGameObject`,
    `UniBridge_CaptureView`, `UniBridge_BatchActions`;
  - `UniBridge_ManageEditor Action=WaitIdle`: editor ready;
  - `UniBridge_UnitySearch Query=Sprite`: success;
  - `UniBridge_BatchActions DryRun=true`: 1 validated create step,
    0 executed, 0 failed;
  - `UniBridge_BatchActions DryRun=false`: create/delete scene object,
    2 executed, 0 failed; це перевіряє nested reentrant execution;
  - паралельно відправлено `UniBridge_CaptureView` і
    `UniBridge_UnitySearch`: обидва повернулись без deadlock/timeout;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`:
    23 log entries, 0 warnings/errors/exceptions/asserts.

## 2026-05-14 - Wait-for-event MCP tool

Додано `UniBridge_WaitForEvent` як явний event wait tool для агентів:

- працює поверх `EditorEventHistory`, який уже слухає selection, hierarchy,
  project assets, packages, compilation, assembly reload, play mode і
  `ObjectChangeEvents`;
- `WaitFor`: `AnyEvent`, `Kind`, `SelectionChanged`, `HierarchyChanged`,
  `ProjectChanged`, `ProjectAssetsChanged`, `CompilationStarted`,
  `CompilationFinished`, `AssemblyReloadBefore`, `AssemblyReloadAfter`,
  `PlayModeState`, `PackagesChanged`, `ObjectChanges`, `EditorReady`,
  `NextEditorUpdate`;
- `SinceId` + `StartFromLatest` дозволяють чекати саме майбутні події без
  сліпих sleep/poll loops;
- `QuietMs` чекає коротке затишшя після match, щоб Unity встигла опублікувати
  пов'язані події;
- `MessageContains`, `TextContains`, `AssetPathContains`, `Kind/Kinds`,
  `PlayModeState` додають фільтрацію;
- `HierarchyChanged` також слухає `objectChanges`, бо Unity при створенні
  об'єктів може публікувати `CreateGameObjectHierarchy` через
  `ObjectChangeEvents` без окремого `hierarchyChanged` callback;
- у scheduler додано `ToolExecutionPolicy.Observer`: wait tool не тримає
  execution gate, тому не блокує mutating tools, які мають спричинити подію.

Оновлено `ToolGuide`: workflow `editor_events` тепер явно радить
`UniBridge_WaitForEvent` замість blind sleeps.

Тестова перевірка:

- змінені package-файли синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  успішний: 0 errors;
- після `RefreshAssets` Unity очікувано зробила domain reload, після чого
  `tools/list` показав `UniBridge_WaitForEvent` з annotation
  `policy=Observer`, `exclusive=false`;
- live Unity MCP smoke виконано тільки через relay stdio:
  - `UniBridge_ReadConsole Action=Clear`;
  - `UniBridge_ManageEditor Action=WaitIdle`: editor ready;
  - `UniBridge_EditorEvents Action=Snapshot`: отримано актуальний `latestId`;
  - `UniBridge_WaitForEvent WaitFor=EditorReady`: immediate success;
  - `UniBridge_WaitForEvent WaitFor=NextEditorUpdate`: success за ~110 ms;
  - дві окремі MCP relay-сесії:
    одна чекала `WaitFor=HierarchyChanged SinceId=<latestId> QuietMs=100`,
    друга створила `__UniBridgeWaitForEventSmoke`;
  - wait повернувся за ~564 ms з `objectChanges/CreateGameObjectHierarchy`;
  - cleanup видалив тестовий object;
  - timeout-case `WaitFor=PackagesChanged TimeoutMs=250` повернув
    `success=false`, `timedOut=true` без Unity console error;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`:
    19 log entries, 0 warnings/errors/exceptions/asserts.

## 2026-05-14 - Exact Game View capture

Почато третій пункт safety/vision polish: точний post-render Game View
capture для випадків, коли `Camera.Render()` не показує фактичний Game tab
результат.

Зміни:

- у `UniBridge_CaptureView` додано `Action=CaptureGameView`;
- старий `CaptureGameCamera` лишився RenderTexture-based controlled camera
  render шляхом, без зміни поведінки;
- `CaptureGameView` фокусує/перемальовує Game View, чекає editor update,
  викликає Unity `ScreenCapture.CaptureScreenshot` у тимчасовий PNG і чекає
  фактичної появи валідного image file без blind sleep;
- додано `ScreenshotTimeoutMs` / `screenshot_timeout_ms`, дефолт 5000 ms,
  clamp до 30000 ms;
- відповідь повертає фактичний розмір Game View PNG, `source` =
  `ScreenCapture.CaptureScreenshot`, `readbackMode` = `ScreenCapture`,
  `gameView.exactPostRender=true`, `waitedMs`, `loadAttempts`,
  `tempFileSizeBytes`;
- overlay для цього action не малюється поверх картинки, щоб не псувати
  exact pixels; якщо агент попросив overlay, воно лишається як metadata-only
  пояснення у відповіді;
- `ToolGuide` workflow `visual_capture` тепер прямо розрізняє
  `CaptureGameView` для exact Game View pixels і `CaptureGameCamera` для
  контрольованого camera render.

Тестова перевірка:

- змінені package-файли синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  успішний: 0 errors;
- лишився тільки старий `System.Threading.Tasks.Extensions` warning, не
  пов'язаний із цією зміною;
- через MCP relay виконано `UniBridge_ManageEditor Action=RefreshAssets
  WaitForCompletion=true`, editor ready;
- live Unity MCP smoke виконано тільки через relay stdio:
  - `tools/list`: 58 tools, `UniBridge_CaptureView` schema містить
    `CaptureGameView` і `ScreenshotTimeoutMs`;
  - `UniBridge_ReadConsole Action=Clear`;
  - `UniBridge_ManageEditor Action=WaitIdle`: editor ready;
  - regression `CaptureGameCamera Width=640 Height=360`: success,
    `source=Camera.RenderTexture`, `readbackMode=Immediate`, PNG 50118 bytes;
  - exact `CaptureGameView ScreenshotTimeoutMs=10000 Overlay=true`: success,
    `source=ScreenCapture.CaptureScreenshot`, `readbackMode=ScreenCapture`,
    фактичний Game View PNG 1276x718, 193564 bytes, `waitedMs=213`,
    `loadAttempts=1`, overlay коректно metadata-only;
  - `CaptureDiff` self-check на отриманому Game View PNG: success, тобто PNG
    читається самим MCP tool;
  - додатковий regression: `CaptureGameCamera ReadbackMode=GpuReadback`
    успішний, 320x180 PNG, фактичний `readbackMode=GpuReadback`;
  - `CaptureGameView AdvanceMs=50 SimulateParticles=true
    SampleAnimations=true`: success, exact screenshot шлях сумісний з
    animated/VFX preview advance;
  - фінальні `UniBridge_ReadConsole Action=DiagnosticSummary`: 0 warnings,
    0 errors, 0 exceptions, 0 asserts.

## 2026-05-15 - Rendering layer context polish

Почато сьомий пункт фінального порівняння Unity-workflow можливостей:
людяний scene/component контекст для renderer sorting/rendering layers.

Зміни:

- додано спільний helper class `RenderingLayerUtility` (у вже компільованому
  helper-файлі), який читає rendering layers із
  `ProjectSettings/TagManager.asset` і серіалізує `m_RenderingLayerMask` як
  компактний об'єкт `{ mask, mode, names }`;
- `UniBridge_ContextSnapshot` тепер повертає `project.environment.renderingLayers`;
- `UniBridge_SceneObjectView` у detailed rendering summaries показує
  `renderingLayerMask` для `SpriteRenderer`, `ParticleSystemRenderer`,
  `LineRenderer`, `TrailRenderer`, generic `Renderer` і `Light`;
- renderer summaries додатково показують `shadowCastingMode`,
  `receiveShadows`, `lightProbeUsage`, `reflectionProbeUsage` і
  `motionVectorGenerationMode`;
- `UniBridge_AssetIntelligence` component smart profiles отримали окремі
  compact profiles для `Renderer`, `SpriteRenderer`,
  `ParticleSystemRenderer`, `LineRenderer`, `TrailRenderer`, `Camera` і
  `Light`;
- короткий `GameObjectSerializer` component summary також включає
  `renderingLayerMask`;
- docs / `ToolGuide` / `DomainCatalog` оновлено, щоб новий агент знав, що
  rendering layer masks доступні як named context.

Тестова перевірка:

- змінені package-файли синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  успішний: 0 errors;
- лишився тільки старий `System.Threading.Tasks.Extensions` warning;
- live Unity MCP smoke виконано тільки через relay stdio:
  - `tools/list`: 59 tools;
  - `UniBridge_ContextSnapshot` повертає
    `project.environment.renderingLayers[0].name=Default`;
  - створено тимчасовий `__UniBridgeRenderingLayerSmoke` через
    `UniBridge_ManageGameObject` з `SpriteRenderer` і `Light`;
  - `UniBridge_SceneObjectView Detail=Detailed Profile=Rendering` повернув
    `renderingLayerMask`, `shadowCastingMode`, `receiveShadows`, named
    `Default` і light summary;
  - `UniBridge_AssetIntelligence Action=Serialize` для active scene повернув
    live hierarchy зі smoke object, `spriteRenderer` profile, `light` profile
    і `renderingLayerMask`;
  - smoke object видалено через MCP;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`: 0 warnings,
    0 errors, 0 exceptions, 0 asserts.

## 2026-05-15 - Rendering layer mask authoring

Почато восьмий пункт фінального порівняння Unity-workflow можливостей:
симетричне authoring API для rendering layer masks.

Зміни:

- `RenderingLayerUtility` отримав читання mask input із integer,
  `Everything` / `Nothing`, одного rendering layer name, масиву names або
  object wrapper `{ names/layers/value }`; для rendering layers `Everything`
  мапиться на усі відомі named layers проєкту, а не небезпечний raw `-1`;
- `SerializedPropertyPatcher` переїхав на цей спільний parser для
  `m_RenderingLayerMask`;
- `UniBridge_ManageRendering` schema і description мають top-level
  `RenderingLayerMask`;
- `CreateLight` застосовує `RenderingLayerMask` до `Light`;
- `AddSpriteShapeRenderer` застосовує `RenderingLayerMask` до renderer-based
  component, разом із `SortingLayerName` / `SortingOrder`;
- `ManageRendering` summaries для `Light` і renderer-based 2D helpers
  повертають named `renderingLayerMask`;
- docs / `ToolGuide` / `DomainCatalog` оновлено, щоб новий агент бачив, що
  `RenderingLayerMask` можна author-ити назвами шарів, а не сирими бітами.

Тестова перевірка:

- змінені package-файли синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  успішний: 0 errors;
- лишився тільки старий `System.Threading.Tasks.Extensions` warning;
- через MCP relay виконано `UniBridge_ManageEditor Action=RefreshAssets
  WaitForCompletion=false`, Unity увійшла в compile/import і потім
  `WaitIdle` повернув ready;
- live Unity MCP smoke виконано тільки через relay stdio:
  - `tools/list`: 59 tools, `UniBridge_ManageRendering` schema містить
    `RenderingLayerMask`;
  - `UniBridge_ReadConsole Action=Clear`;
  - створено/оновлено `__UniBridgeRenderingMaskSmoke` через
    `UniBridge_ManageRendering Action=CreateLight`;
  - перевірено чотири форми input:
    `["Default"]`, `"Everything"`, `{ "Names": ["Default"] }`, `"Nothing"`;
  - `"Everything"` після parser fix мапиться на фактичний known named mask
    `Default`, а `"Nothing"` повертає mode `Nothing`;
  - `UniBridge_SceneObjectView Detail=Detailed Profile=Rendering` підтвердив
    authored `renderingLayerMask` на `Light`;
  - smoke object видалено через MCP;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`: 0 warnings,
    0 errors, 0 exceptions, 0 asserts.

## 2026-05-15 - Rendering extras authoring

Додано focused authoring для rendering extras у `UniBridge_ManageRendering`,
щоб агенту не доводилось вручну комбінувати `AddComponent` і сирий
`SerializedProperty` patching для поширених візуальних компонентів.

Зміни:

- `UniBridge_ManageRendering` schema/description отримали actions:
  `AddDecalProjector`, `AddLensFlare`, `AddFlareLayer`, `AddWindZone`,
  `AddProjector`;
- `AddDecalProjector` працює через reflection з URP
  `UnityEngine.Rendering.Universal.DecalProjector`, підтримує transform,
  `Material/MaterialPath`, `DrawDistance`, `FadeScale`, angle fade,
  `UvScale`, `UvBias`, `ScaleMode`, `Pivot`, `Size` і named
  `RenderingLayerMask`;
- `AddLensFlare` в `Auto` режимі віддає перевагу SRP
  `LensFlareComponentSRP`, з fallback на legacy `LensFlare`, і підтримує
  intensity/brightness, attenuation, occlusion, scale, off-screen flag,
  light override і asset references;
- `AddFlareLayer`, `AddWindZone`, `AddProjector` реалізовані як safe optional
  module/component actions: якщо відповідний Unity type недоступний для
  поточного editor/module контексту, tool повертає clean unavailable response
  без warning/error у Console;
- `ManageRendering Inspect` тепер повертає `renderingExtras` summaries для
  `DecalProjector`, `LensFlareComponentSRP`, `LensFlare`, `FlareLayer`,
  `WindZone`, `Projector`;
- `SceneObjectView` Rendering/Lighting profiles включають ці extra component
  type names;
- `BatchActions` aliases/validation знають нові rendering actions, включно з
  короткими aliases `decal`, `flare`, `wind`, `projector`;
- `ToolGuide` і `DomainCatalog` оновлено, щоб новий агент бачив rendering
  extras як явний сценарій.

Тестова перевірка:

- source package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  успішний: 0 errors;
- лишився старий `System.Threading.Tasks.Extensions` warning;
- через MCP relay виконано `RequestScriptCompilation Force=true` і domain
  reload, після чого editor повернувся в ready state;
- live Unity MCP smoke виконано тільки через relay stdio:
  - `tools/list`: 59 tools, `UniBridge_ManageRendering` schema містить усі
    5 нових actions;
  - `UniBridge_ReadConsole Action=Clear`;
  - `AddDecalProjector` створив `UnityEngine.Rendering.Universal.DecalProjector`
    з named `RenderingLayerMask=["Default"]`;
  - `AddLensFlare Mode=Auto` створив
    `UnityEngine.Rendering.LensFlareComponentSRP`;
  - `AddFlareLayer`, `AddWindZone`, `AddProjector` у цьому Unity 6000.4.7f1
    editor context повернули clean unavailable responses без Console warnings;
  - `UniBridge_ManageRendering Inspect` побачив 2 smoke objects і їхні
    `renderingExtras`;
  - `UniBridge_SceneObjectView Query ComponentType=DecalProjector
    Detail=Detailed Profile=Rendering` повернув `totalCount=1`;
  - `UniBridge_BatchActions Action=DryRun` з aliases `decal` і `wind`
    пройшов validation;
  - smoke objects видалено через MCP;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`: 0 warnings,
    0 errors, 0 exceptions, 0 asserts.

## 2026-05-15 - LODGroup rendering authoring

Додано focused authoring для `LODGroup` у `UniBridge_ManageRendering`, щоб
агент міг швидко створювати mesh LOD setup без ручного `AddComponent` і
низькорівневого patching.

Зміни:

- `UniBridge_ManageRendering` schema/description отримали action
  `AddLODGroup` з aliases `lodgroup` і `lod`;
- action підтримує `LocalReferencePoint`, `LODSize`, `FadeMode`,
  `AnimateCrossFading`, `LastLODBillboard`, `Enabled`,
  `RecalculateBounds`;
- LOD levels задаються через `Lods` / `LODLevels` / `Levels`, де кожен level
  може мати `ScreenRelativeTransitionHeight` або `Height`,
  `FadeTransitionWidth`, `Renderers`, `IncludeChildren`;
- якщо explicit `Lods` не передані, tool може автоматично зібрати child
  `Renderer` components у один LOD через `UseChildRenderers=true`;
- renderer references приймають GameObject path/name/id, component/object id,
  масиви references і object-form references;
- levels сортуються за спаданням `screenRelativeTransitionHeight`, щоб Unity
  отримувала валідний LOD порядок;
- `ManageRendering Inspect` тепер повертає `renderingExtras` summary для
  `UnityEngine.LODGroup`: `fadeMode`, `animateCrossFading`,
  `lastLODBillboard`, `localReferencePoint`, `size`, `lodCount`, renderer
  paths/id та renderer layer/sorting context;
- `SceneObjectView` Rendering/Lighting profiles включають `LODGroup`;
- `BatchActions` aliases/validation знають `AddLODGroup`, `lodgroup`, `lod`;
- `ToolGuide` і `DomainCatalog` оновлено, щоб новий агент бачив LODGroup як
  явний rendering workflow.

Тестова перевірка:

- source package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  успішний: 0 errors;
- лишився старий `System.Threading.Tasks.Extensions` warning;
- live Unity MCP smoke виконано тільки через relay stdio:
  - `tools/list`: 59 tools, `UniBridge_ManageRendering` schema містить
    `AddLODGroup`;
  - `UniBridge_ManageEditor Action=WaitIdle` повернув ready;
  - `UniBridge_ReadConsole Action=Clear`;
  - створено smoke root `__UniBridgeLODSmoke` і два primitive cube children;
  - `UniBridge_ManageRendering Action=AddLODGroup` створив
    `UnityEngine.LODGroup` з 2 explicit LOD levels, `FadeMode=CrossFade`,
    `AnimateCrossFading=true`, renderer counts `[1, 1]`;
  - `UniBridge_ManageRendering Inspect` підтвердив `renderingExtras`
    `LODGroup` з `lodCount=2`;
  - `UniBridge_SceneObjectView Action=Query ComponentType=LODGroup
    Detail=Detailed Profile=Rendering` повернув `totalCount=1`;
  - `UniBridge_BatchActions Action=DryRun` з alias `lod` пройшов validation;
  - smoke object видалено через MCP;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`:
    `logCount=44`, `warningCount=0`, `errorCount=0`, `exceptionCount=0`,
    `assertCount=0`.

## 2026-05-15 - Probe rendering authoring

Додано третій focused rendering пункт: authoring для reflection/light probe
setup у `UniBridge_ManageRendering`. До цього UniBridge вже добре інспектував
частину probe components через scene view profiles, але створення і
налаштування вимагали generic `AddComponent` + ручного property patching.

Перед змінами переглянуто відповідний reference implementation surface:

- `JSONReflectionProbe`: `mode`, `refreshMode`, `timeSlicingMode`,
  `renderDynamicObjects`, `customBakedTexture`, `importance`, `intensity`,
  `boxProjection`, `blendDistance`, `size`, `center`, `resolution`, `hdr`,
  `shadowDistance`, `clearFlags`, `backgroundColor`, `cullingMask`,
  `occlusionCulling`, `nearClipPlane`, `farClipPlane`, `enabled`;
- `JSONLightProbeGroup`: `probePositions`, `dering`, `enabled`;
- `JSONLightProbeProxyVolume`: `refreshMode`, `qualityMode`, `dataFormat`,
  `boundingBoxMode`, `sizeCustom`, `originCustom`, `resolutionMode`,
  `probeDensity`, `gridResolutionX/Y/Z`, `probePositionMode`, `enabled`.

Зміни:

- `UniBridge_ManageRendering` schema/description отримали actions:
  `AddReflectionProbe`, `AddLightProbeGroup`,
  `AddLightProbeProxyVolume`;
- додано aliases:
  `reflectionprobe`, `reflprobe`, `lightprobegroup`, `probegrid`,
  `lightprobeproxyvolume`, `probeproxyvolume`, `lppv`;
- `AddReflectionProbe` напряму конфігурує typed Unity API для
  `mode`, `refreshMode`, `timeSlicingMode`, `renderDynamicObjects`,
  `importance`, `intensity`, `boxProjection`, `blendDistance`, `size`,
  `center`, `resolution`, `hdr`, `shadowDistance`, `clearFlags`,
  `backgroundColor`, `cullingMask`, `near/far clip`, `enabled`;
- direct typed configuration потрібна, бо Unity 6000 API і generic serialized
  patching не завжди коректно змінюють reflection probe fields за public
  aliases;
- `AddLightProbeGroup` приймає explicit `ProbePositions` або створює default
  `Box` / `Tetrahedron` layout із `Size`, підтримує `Dering` і optional
  `Tetrahedralize` через reflection-safe `Lightmapping` call;
- `AddLightProbeProxyVolume` напряму конфігурує refresh/quality/data format,
  bounding box, custom size/origin, resolution mode, probe density,
  grid resolution і probe position mode;
- `ManageRendering Inspect` тепер повертає `reflectionProbes`,
  `lightProbeGroups`, `lightProbeProxyVolumes` summaries;
- `SceneObjectView` Rendering profile тепер включає
  `ReflectionProbe`, `LightProbeGroup`, `LightProbeProxyVolume`;
- `BatchActions` aliases/validation, `DomainCatalog` і `ToolGuide` оновлено.

Тестова перевірка:

- source package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `dotnet build H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  успішний: 0 errors;
- лишився старий `System.Threading.Tasks.Extensions` warning;
- через MCP relay виконано `UniBridge_ManageEditor Action=RefreshAssets
  WaitForCompletion=false Force=true`; під час domain reload relay очікувано
  тимчасово втратив discovery, після retry `WaitIdle` повернув ready;
- live Unity MCP smoke виконано тільки через relay stdio:
  - `tools/list`: 59 tools, `UniBridge_ManageRendering` schema містить
    `AddReflectionProbe`, `AddLightProbeGroup`,
    `AddLightProbeProxyVolume`;
  - `UniBridge_ReadConsole Action=Clear`;
  - створено root `__UniBridgeProbeSmoke`;
  - `AddReflectionProbe` створив `UnityEngine.ReflectionProbe` з
    `mode=Realtime`, `intensity=0.75`, `size=(4,3,5)`,
    `center=(0,0.5,0)`, `near=0.2`, `far=80`,
    `cullingMask=Default`;
  - `AddLightProbeGroup` створив group із 8 explicit probe positions і
    `dering=true`;
  - `AddLightProbeProxyVolume` створив custom bounding box
    `sizeCustom=(3,2,3)` і grid `2x2x2`;
  - `UniBridge_ManageRendering Inspect` окремо підтвердив reflection probe,
    light probe group і proxy volume summaries;
  - `UniBridge_SceneObjectView Action=Query ComponentType=ReflectionProbe
    Detail=Detailed Profile=Rendering` повернув `totalCount=1`;
  - `UniBridge_BatchActions Action=DryRun` з aliases
    `reflectionprobe`, `probegrid`, `lppv` пройшов validation;
  - smoke root видалено через MCP;
  - фінальний `UniBridge_ReadConsole Action=DiagnosticSummary`:
    `logCount=41`, `warningCount=0`, `errorCount=0`, `exceptionCount=0`,
    `assertCount=0`.

## 2026-05-16 - Full UniBridge MCP smoke: VFX retune assert fix

Під час ретельного live smoke через UniBridge MCP у тестовому проекті з
Corgi Engine 9.4 знайдено корисний console-сигнал у `UniBridge_ManageVFX`:
повторний `AddParticleSystem`/preset на вже активному `ParticleSystem`
міг викликати Unity assert:

`Setting the duration while system is still playing is not supported...`

Причина: tool міг міняти `ParticleSystem.main.duration` після попереднього
preview/capture advance, коли система ще грала або мала живі particles.

Зміна:

- `ManageVFX.ConfigureParticleSystem` тепер перед preset/parameter patching
  зупиняє і очищає активну систему через
  `ParticleSystem.Stop(withChildren: true,
  ParticleSystemStopBehavior.StopEmittingAndClear)`;
- для Unity 6000 додано дублюючий SerializedObject patch основних полів
  `lengthInSec`, `InitialModule.startLifetime.scalar`,
  `startSpeed.scalar`, `startSize.scalar`, `loop`, `startColor`, бо live
  smoke показав, що частина `ParticleSystem.MainModule` setters може не
  відображатися у serialized стані одразу після editor preview/capture
  advance, тоді як emission rate застосовувався коректно;
- у цьому ж smoke виявлено, що локальний `TryGetFloat` у `ManageVFX` міг
  втрачати дробові JSON numbers у не-invariant editor culture; тепер він
  спершу читає numeric `JToken` напряму, і лише потім парсить рядок через
  invariant/current culture fallback;
- це прибирає editor assert при повторному налаштуванні VFX і робить
  workflow `author -> capture with AdvanceMs -> tweak -> recapture`
  стабільнішим для агентів.

Повторний тест треба запускати після синхронізації пакету в
`H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`
та Unity refresh/compile.

## 2026-05-16 - Alternate full MCP smoke: selection leakage fix

Під час повторного повного live smoke іншим сценарієм у тестовому проекті з
Corgi Engine знайдено важливий workflow-недолік: частина authoring tools
надто сильно покладалася на поточний `Selection.activeGameObject`.

Практичний прояв:

- `UniBridge_ManageAudio` і `UniBridge_ManageVFX` при `Name` без `Target`
  могли налаштувати поточно вибраний об'єкт замість створення/використання
  названого об'єкта, хоча description обіцяє `Name` як create-if-missing
  shortcut;
- `UniBridge_ManageUI CreateTemplate` без явного `Parent/Target` міг
  випадково вбудувати HUD/template у вибраний або перший знайдений Canvas
  поточної сцени. На Corgi сцені це означало, що тестовий HUD міг опинитися
  всередині world-space SignPost canvas, а не у власному predictable screen
  canvas.

Зміни:

- `ManageAudio.ResolveTarget` і `ManageVFX.ResolveTarget` тепер мають
  пріоритет: explicit `Target` -> explicit `Name` existing-or-create ->
  selection fallback. Це робить сценарій `Action=ApplyPreset Name=...`
  детермінованим для агентів;
- `ManageUI.CreateTemplate` тепер використовує тільки explicit
  `Parent/Target`; якщо їх немає, template створює власний Canvas через
  вже наявний `CreateParentCanvas` шлях;
- `ResolveParentForCreate` більше не викликає selection fallback через
  порожній `Parent`/`Target`, а переходить до selection лише як справжнього
  implicit UI fallback.

Додаткове спостереження з smoke:

- `BatchActions` rollback фізично видаляє новостворений asset root
  (`deletedCreatedRoots=1`, `existsOnDisk=false` після rollback), але
  `AssetIntelligence Inspect` може все ще повернути stale AssetDatabase GUID
  для щойно видаленого asset. Для verification після rollback треба
  дивитися `existsOnDisk=false`, а не тільки `success=true`.

Потрібна перевірка після синхронізації у тестовий проект:

- build `Cidonix.UniBridge.MCP.Editor.csproj`;
- Unity refresh/reload через MCP;
- focused MCP retest: Audio/VFX `Name` не повинні зачіпати selection,
  UI template без `Parent/Target` має створювати власний Canvas,
  VFX retune after capture не має давати console assert/error.

## 2026-05-17 - CaptureView framed render camera fix

Після паузи продовжено alternate full smoke у
`H:/Repos/UnityRepos/UniBridge_Test_Project` через UniBridge MCP. Smoke
показав прихований дефект у `UniBridge_CaptureView`: для
`CaptureAroundObject` metadata правильно повертала target і frame bounds,
але PNG міг залишатися старим кадром Scene View з Corgi-рівнем. Просте
виставлення `SceneView.pivot/size/rotation` і навіть `SceneView.LookAt`
не гарантувало, що `sceneView.camera.Render()` одразу рендерить оновлений
frame.

Зміна:

- target/frame-based Scene View captures тепер рендеряться через
  тимчасову hidden camera `UniBridge Framed Capture Camera`;
- camera створюється з налаштувань Scene View camera, але позиція,
  rotation, projection, near/far clip і orthographic size обчислюються
  прямо з padded frame bounds;
- для orthographic framing додано bounds projection у camera-local axes,
  щоб size враховував поточну view direction і output aspect;
- відповідь capture тепер повертає source
  `FramedSceneCamera.RenderTexture` і `framedRenderCamera` metadata, коли
  був використаний контрольований framed render path;
- fallback без target/frame bounds лишається на реальній Scene View camera,
  щоб `CaptureSceneView` без target і далі відображав поточний editor view.

Перевірено в живому Unity через MCP:

- package синхронізовано в тестовий проект і `dotnet build
  H:/Repos/UnityRepos/UniBridge_Test_Project/Cidonix.UniBridge.MCP.Editor.csproj`
  проходить з 0 errors; лишається старий `System.Threading.Tasks.Extensions`
  warning;
- Unity refresh/reload через MCP, `GetCompilationDiagnostics`: 0 errors,
  0 warnings;
- far-cube test: target `UniBridge_CaptureFrameRetest_FarCube_20260517`
  на позиції `[100, 10, 0]`, `CaptureAroundObject View=Iso
  Orthographic=true Zoom=Close Overlay=true` повернув PNG, де target
  справді в кадрі, а не старий Corgi view;
- compact smoke: Audio/VFX `Name` при активній selection bait не чіпають
  bait object, UI `CreateTemplate` без explicit parent створює власний
  Canvas, `CaptureContactSheet` для target повертає 4 captures без errors;
- console diagnostic summary після cleanup: 0 warnings, 0 errors,
  0 exceptions.

Додатковий self-check:

- `VisualSceneAudit AuditImage` на contact sheet пройшов (`passed=true`),
  але дав warnings `high_single_color_area` і `low_color_diversity`,
  бо тестовий куб був темний і займав більшість кадру. Це не регресія
  capture-фреймінгу, але хороший приклад, що після visible work агент
  має дивитися не тільки на `success`, а й на visual audit warnings.

## 2026-05-17 - Full UniBridge MCP smoke and relay reload retry

Після наступної паузи запущено повний smoke усіх MCP tools у відкритому
`H:/Repos/UnityRepos/UniBridge_Test_Project` через direct JSON-RPC до
`unibridge_relay_win.exe --mcp --project-id
ae4e323353aa487f9ea74d566b38eaac`.

Перший прогін навмисно створював C# script і одразу переходив до наступних
tools. Це зловило реальний слабкий сценарій relay: під час Unity domain
reload discovery file тимчасово зникав, а relay повертав
`Unity not detected. No matching UniBridge discovery file was found.`
замість того, щоб коротко перечекати републікацію bridge. Через це
тимчасово падали наступні `tools/call`, хоча Unity через кілька секунд
знову була доступна.

Зміна:

- у `UniBridge.Relay/Program.cs` додано retry у
  `UnityConnection.ConnectAsync`: explicit targets (`--project-id`,
  `--instance-id`, `--project-path`) чекають до 15 секунд на повторну
  появу discovery file, untargeted mode - до 3 секунд;
- retry точковий: він не міняє MCP protocol і не ховає справжню відсутність
  Unity, а лише покриває нормальне reload-вікно після script create/delete;
- перебрано всі relay binaries через `dotnet publish` для `win-x64`,
  `linux-x64`, `osx-x64`, `osx-arm64` і оновлено `RelayApp~`;
- пакет знову синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`.

Повний повторний smoke після фіксу:

- `tools/list`: 60 tools;
- covered: 60/60 tools;
- MCP calls: 103;
- failures: 0;
- untested tools: 0;
- `CreateScript -> WaitIdle -> Validate/GetSha/Find/Read/ScriptIntelligence`
  пройшов після domain reload; `WaitIdle` дочекався Unity приблизно за
  10.8s;
- `DeleteScript -> WaitIdle -> cleanup` також пройшов після reload;
- scene/object authoring перевірено на GameObject, Physics2D/3D,
  Constraints, Navigation, Rendering, Audio, VFX, UI, UI Toolkit,
  Timeline, Tilemap, AnimatorController, AnimationClip, Prefab,
  ScopedEdit, BatchActions, ImportExternalModel;
- `CaptureView CaptureAroundObject`, `CaptureContactSheet` і
  `VisualSceneAudit` пройшли; ключові PNG:
  `C:/Users/Cidonix/.unibridge/captures/UniBridge_Test_Project_ae4e3233/aroundobject_fullsmoke_20260517192332_20260517_192356_905.png`
  і
  `C:/Users/Cidonix/.unibridge/captures/UniBridge_Test_Project_ae4e3233/view_contact_sheet_sheet_20260517192332_20260517_192357_190.png`;
- тимчасові assets і scene objects smoke були прибрані через MCP;
- фінальні `GetCompilationDiagnostics` і `ReadConsole DiagnosticSummary`:
  0 warnings, 0 errors, 0 exceptions.

Код-огляд після smoke:

- знайдений і виправлений лише реальний reliability gap у relay reconnect;
- додаткового рефакторингу заради рефакторингу не потрібно;
- на майбутнє варто пам'ятати: script lifecycle tests мають включати
  domain reload window, бо саме там часто проявляються проблеми MCP bridge.

Додатковий follow-up після аналізу smoke output:

- `UniBridge_ScriptApplyEdits` з `Preview=true` для швидкого convertible
  text path (`anchor_insert`, `replace_range`, `regex_replace` тощо)
  раніше міг пройти в `ConvertAndApplyTextEdits` і реально застосувати
  precise text edits до перевірки preview. Smoke помітив це по повідомленню
  `Applied 1 text edit(s)` у preview-call;
- виправлено `ScriptApplyEdits.cs`: `preview` і safety rule для
  `regex_replace` без `confirm=true` тепер обробляються перед
  `ManageScript apply_text_edits`;
- focused MCP retest:
  `CreateScript -> GetSha -> ScriptApplyEdits Preview=true anchor_insert
  -> GetSha` повернув `Preview only (no write)`, SHA до/після однаковий;
- cleanup і console diagnostic summary після focused test: 0 warnings,
  0 errors, 0 exceptions.

## 2026-05-18: Підготовка Patreon-релізу UniBridge 0.1.0

Проведено перевірку комплектації Unity package перед першим Patreon ZIP:

- package manifest валідний: `com.cidonix.unibridge`, display name
  `UniBridge`, version `0.1.0`, Unity minimum `6000.0`;
- обов'язкові релізні файли присутні:
  `package.json`, `README.md`, `CHANGELOG.md`, `RELEASE_NOTES.md`,
  `LICENSE.md`, `Third Party Notices.md`, `Documentation~/unibridge.md`;
- relay bundle присутній у `RelayApp~`: `relay.json`,
  `unibridge_relay_win.exe`, `unibridge_relay_linux`,
  `unibridge_relay_mac_x64`, `unibridge_relay_mac_arm64`;
- relay bundle version: `1.1.0-build.9`;
- перевірка на згадки `Bezi` в package: збігів немає;
- перевірка package на випадкові `Library/Temp/Obj/Bin/Build/Builds`
  артефакти всередині `com.cidonix.unibridge`: чисто;
- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj --nologo`:
  build succeeded, 0 warnings, 0 errors.

Оновлено релізне оформлення:

- `README.md` розширено до нормальної інструкції для release archive:
  опис продукту, склад пакета, вимоги, встановлення через
  `Package Manager > Add package from disk...`, перше підключення,
  посилання на документацію та known notes;
- `CHANGELOG.md` переписано як повний changelog для `0.1.0`, згрупований
  за основними доменами UniBridge;
- додано `RELEASE_NOTES.md` і `.meta` з короткими release notes для
  Patreon-поста: склад архіву, relay version, installation steps,
  capabilities, known limitations, verification summary;
- `Documentation~/unibridge.md` отримав посилання на `RELEASE_NOTES.md`;
- `package.json` description уточнено як agent-facing/local MCP опис.

Створено локальну папку релізних архівів:

- `Builds/` створено в корені repo;
- `ignore.conf` вже містить `Builds` і `builds`, тому папка й архіви не
  потрапляють у Plastic SCM status;
- зібрано архів:
  `H:/Repos/Cidonix/UniBridge/Builds/UniBridge-0.1.0.zip`;
- ZIP містить кореневу папку `com.cidonix.unibridge/`, 523 entries;
- required entries у ZIP перевірені, missing required: none;
- ZIP size: 131,508,540 bytes / 125.42 MB;
- ZIP SHA256:
  `6E28AE2AE91282D9EE653215DBE7F2D6C18A9C352828818B82F9DF10A92B6F7E`.

Follow-up уточнення для користувачів перед публікацією:

- `README.md`, `RELEASE_NOTES.md` і `Documentation~/unibridge.md`
  доповнені явним поясненням, що UniBridge може працювати з двома і
  більше одночасно відкритими Unity projects, якщо кожен проект має
  власний MCP server entry і власний `--project-id`;
- там же явно вказано, що після додавання/зміни UniBridge MCP entry у
  конфігурації треба перезапустити саме AI agent/MCP client, а не Unity,
  бо більшість MCP clients читають server configuration тільки на старті;
- після уточнень ZIP перезібрано:
  `H:/Repos/Cidonix/UniBridge/Builds/UniBridge-0.1.0.zip`;
- актуальний ZIP size: 131,508,974 bytes / 125.42 MB;
- актуальний ZIP SHA256:
  `2E0878CFA87AA45B0314CB98CA9BEAF9087F3797C4D9C5A405A018EBBBE1CA70`.

## 2026-05-19: GitHub landing repository

Створено/оформлено GitHub repository shell для UniBridge:

- repo: `Cidonix/UniBridge`;
- додано тільки public-facing `README.md` через GitHub contents API;
- код, package sources, relay binaries, release ZIP і локальний Plastic SCM
  history не заливалися;
- README описує UniBridge як local Unity MCP bridge, пояснює Patreon
  distribution, multiple open Unity projects через окремі `--project-id`,
  обов'язковий restart AI agent/MCP client після зміни MCP configuration,
  access через `https://patreon.com/unibridge`, proprietary license note;
- GitHub commit SHA для README-only commit:
  `9f8fa8a84f0ce1507b83b0a969988cbba285b11d`.

## 2026-05-30: UniBridge 0.2.1 large-scene polish

Після перевірки `SceneHierarchyExport` і `ManageSceneHierarchy` у Domovyk
додано polish для комфортнішої роботи AI з великими сценами:

- package version піднято до `0.2.1`;
- `package.json` отримав `author.name=Cidonix`, тому Unity Package Manager
  показує `By Cidonix` і групує пакет у `Packages - Cidonix`;
- `UniBridge_SceneHierarchyExport` тепер повертає компактний `summary` у
  кожній export-відповіді і записує той самий summary у JSON/JSONL export:
  `totalObjects`, `rootObjects`, `inactiveObjects`, `missingScriptsTotal`,
  `objectsWithMissingScripts`, `rendererCount`, `rendererCountByType`,
  `rendererCountBySortingLayer`, `light2DCount`, `prefabInstanceCount`,
  `duplicatePathGroupCount`, `topDuplicatePathGroups`;
- `CompareExports` став менш шумним: `IncludeDuplicateKeys=false` за
  замовчуванням, `MaxDuplicateKeys` обмежує verbose rows, duplicate details
  зведені в compact summary, а matching віддає перевагу `indexedPath`;
- якщо `IncludeDuplicateKeys=false`, verbose `duplicateKeys` rows взагалі
  не повертаються в payload;
- `ManageSceneHierarchy CreateContainer` у dry-run тепер додає до кожного
  planned move:
  `plannedParentContainerName`, `plannedParentPath`,
  `plannedParentObjectId=null`, `plannedParentWillBeCreated=true`;
- object-count validation для `Reparent` і `CreateContainer` явно повертає
  `validationMode`, `expectedObjectCountDelta`, `actualObjectCountDelta`,
  `objectCountValidationPassed`;
- сумісний прапорець `ValidateObjectCountUnchanged` лишився, але додано
  ясніший alias `ValidateExpectedObjectCountDelta`.

Пакет синхронізовано у live-проекти:

- `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`.

MCP smoke на `UniBridge_Test_Project`:

- `tools/list`: 62 callable MCP tools;
- `RefreshAssets -> WaitIdle -> GetCompilationDiagnostics`: 0 compile
  warnings, 0 compile errors;
- `SceneHierarchyExport` на Corgi scene `FeaturesPlatforms`: summary
  перевірено в MCP response і JSONL metadata row;
- test export: `totalObjects=365`, `rootObjects=9`, `rendererCount=196`,
  `duplicatePathGroupCount=2`;
- `CreateContainer` dry-run для двох smoke objects повернув planned parent
  `/UB_021_Container`, `plannedParentWillBeCreated=true`;
- executing `CreateContainer`: `expectedObjectCountDelta=1`,
  `actualObjectCountDelta=1`, `objectCountValidationPassed=true`;
- `CompareExports IncludeDuplicateKeys=false`: verbose `duplicateKeys` rows
  omitted, compact duplicate summary present, `comparedBy` includes
  `indexedPath`;
- cleanup виконано через `UniBridge_EditorSnapshot Restore`;
- фінальні compilation diagnostics: 0 warnings, 0 errors.

## 2026-05-30: UniBridge 0.2.2 final hierarchy UX polish

Після додаткового feedback із Domovyk дополіровано shape відповідей
`SceneHierarchyExport` / `ManageSceneHierarchy`, щоб AI швидше читав великі
сцени без дублювання й зайвого шуму:

- package version піднято до `0.2.2`;
- `SceneHierarchyExportParams` отримав `MaxDuplicateSamples` з default `3`;
- `SceneHierarchyExport Export` і `CompareExports` обмежують sample
  `objectIds`, `indexedPaths` і names у duplicate groups через
  `MaxDuplicateSamples`;
- `CompareExports IncludeDuplicateKeys=false` більше не дублює duplicate
  summary у `left.duplicates` / `right.duplicates`: повні compact duplicate
  details лишаються тільки в `summary.duplicates.left/right`;
- `left/right` у compare response за замовчуванням містять тільки
  `totalObjects`, `duplicateGroupCount`, `duplicateObjectCount`,
  `duplicateKeys=null`;
- `ManageSceneHierarchy Reparent` dry-run тепер для існуючого parent ставить
  `plannedParentObjectId`, `plannedParentPath`,
  `plannedParentWillBeCreated=false`, а
  `plannedParentContainerName=null`;
- `CreateContainer` dry-run зберігає попередню поведінку для майбутнього
  контейнера: `plannedParentObjectId=null`,
  `plannedParentWillBeCreated=true`;
- `ManageSceneHierarchy` результати отримали компактний
  `objectCountValidation` з alias-полями `mode`, `expectedDelta`,
  `actualDelta`, `plannedDelta` для dry-run і `passed`.

Пакет синхронізовано у live-проекти:

- `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`.

MCP smoke на `UniBridge_Test_Project` через direct relay
`codex-unibridge-022-polish-smoke`:

- `tools/list`: 62 callable MCP tools;
- `RefreshAssets -> WaitIdle -> GetCompilationDiagnostics`: 0 compile
  warnings, 0 compile errors;
- створено контрольні scene objects `UB_022_Parent`,
  `UB_022_TargetParent` і п'ять sibling-дублікатів `UB_022_Dupe`;
- `SceneHierarchyExport Format=Jsonl WriteToFile=true MaxDuplicateSamples=3`:
  export file створено, `summary.totalObjects=370`,
  `duplicatePathGroupCount=3`, усі duplicate sample arrays bounded до `3`;
- `ManageSceneHierarchy Reparent DryRun=true` для існуючого parent:
  `plannedParentObjectId=-33192`,
  `plannedParentPath=/UB_022_TargetParent`,
  `plannedParentWillBeCreated=false`,
  `objectCountValidation.passed=true`,
  `plannedDelta=0`, `actualDelta=null`;
- executing `Reparent`: `expectedDelta=0`, `actualDelta=0`,
  `objectCountValidation.passed=true`, `worldTransformPreserved=true`;
- `CompareExports IncludeDuplicateKeys=false MaxDuplicateSamples=3`:
  `left/right` fields =
  `totalObjects, duplicateGroupCount, duplicateObjectCount, duplicateKeys`,
  `duplicateKeys=null`, duplicate details present only in
  `summary.duplicates.left/right`, sample arrays bounded to `3`;
- cleanup виконано через `UniBridge_EditorSnapshot Restore`;
- post-sync MCP check після повторної синхронізації package docs:
  `RefreshAssets`, `WaitIdle`, `GetCompilationDiagnostics` і
  `ReadConsole DiagnosticSummary` успішні; diagnostics: 0 warnings,
  0 errors;

## 2026-05-30: UniBridge 0.2.3 final readability polish

Після ще одного feedback pass від Domovyk AI дополіровано читабельність
відповідей для великих сцен:

- package version піднято до `0.2.3`;
- `SceneHierarchyExportParams` отримав `IncludeDuplicateSummary=true` для
  керування top duplicate samples у `CompareExports`;
- `CompareExports IncludeDuplicateKeys=false` більше не повертає
  `duplicateKeys=null`; `left/right` тепер містять тільки
  `totalObjects`, `duplicateGroupCount`, `duplicateObjectCount`;
- `summary.duplicates` більше не має `left/right` вкладених summary:
  замість цього повертає flattened counters
  `leftGroupCount`, `rightGroupCount`, `leftObjectCount`,
  `rightObjectCount`;
- якщо duplicate groups зліва/справа збігаються, top samples повертаються
  один раз у `sharedTopGroups`; якщо ні, повертаються bounded
  `leftTopGroups` / `rightTopGroups`;
- `IncludeDuplicateSummary=false` дає count-only duplicate summary без
  top groups;
- `ToolExecutionScheduler` / `UniBridge_ExecutionStatus` recent operations
  тепер мають `mode` і `changedProject`; dry-run operations позначаються
  як `mode=DryRun`, `changedProject=false`, зберігаючи policy `Mutating`;
- `ContextSnapshot Depth=Brief` тепер повертає `registeredPackageCount`, але
  не розгортає `registeredPackages` за замовчуванням; full registered package
  roots повертаються для `Depth=Detailed` або
  `IncludePackageDependencies=true`.

Пакет синхронізовано у live-проекти:

- `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`.

MCP smoke на `UniBridge_Test_Project` через direct relay
`codex-unibridge-023-polish-smoke`:

- `tools/list`: 62 callable MCP tools;
- `RefreshAssets -> WaitIdle -> GetCompilationDiagnostics`: 0 compile
  warnings, 0 compile errors;
- `ContextSnapshot Depth=Brief`: `registeredPackageCount` є,
  `registeredPackages` відсутній;
- `ContextSnapshot Depth=Brief IncludePackageDependencies=true`:
  `registeredPackages` присутній;
- створено контрольні scene objects `UB_023_Parent`,
  `UB_023_TargetParent` і п'ять sibling-дублікатів `UB_023_Dupe`;
- `SceneHierarchyExport Format=Jsonl WriteToFile=true MaxDuplicateSamples=3`:
  export file створено, `summary.totalObjects=370`,
  `duplicatePathGroupCount=3`, duplicate samples bounded до `3`;
- `ManageSceneHierarchy Reparent DryRun=true` успішний;
- `ExecutionStatus Recent` для цього dry-run:
  `policy=Mutating`, `mode=DryRun`, `changedProject=false`;
- `CompareExports IncludeDuplicateKeys=false IncludeDuplicateSummary=true`:
  `left/right` fields =
  `totalObjects, duplicateGroupCount, duplicateObjectCount`,
  `summary.duplicates` fields =
  `leftGroupCount, rightGroupCount, leftObjectCount, rightObjectCount,
  sharedTopGroups`;
- `CompareExports IncludeDuplicateSummary=false`:
  duplicate summary count-only, без `sharedTopGroups`, `leftTopGroups`,
  `rightTopGroups`;
- `CompareExports IncludeDuplicateKeys=true MaxDuplicateKeys=2`:
  `duplicateKeys` присутній і bounded до `2`;
- cleanup виконано через `UniBridge_EditorSnapshot Restore`;
- фінальні diagnostics: 0 warnings, 0 errors;
- `ReadConsole DiagnosticSummary`: summary побудовано успішно;
- post-sync MCP check після фінальної синхронізації package docs:
  package version `0.2.3`, diagnostics 0 warnings / 0 errors,
  `ContextSnapshot Depth=Brief` має `registeredPackageCount` і не має
  `registeredPackages`.

## 2026-05-31: UniBridge 0.2.4 Domovyk authoring fixes

Задача прийшла з live-роботи AI над `Domovyk`: `ManageGameObject` не
знаходив inactive scene object, який стабільно знаходили `UnitySearch` і
scene view tools. Також були хвости у `BatchActions` diagnostics.

Зміни:

- package version піднято до `0.2.4`;
- relay bundle піднято до `1.1.0-build.10`;
- `UniBridge_ManageGameObject` для `Modify`, `GetComponents`,
  `GetComponent`, `AddComponent`, `RemoveComponent`,
  `SetComponentProperty`, `Delete` тепер використовує спільний
  `SceneObjectLocator` resolver із підтримкою `IncludeInactive` /
  `SearchInactive` і `IncludePrefabStage`;
- target-not-found errors тепер явно пишуть
  `IncludeInactive=True/False`, щоб dry-run/validation не виглядали як
  успішний сценарій при неправильному scope;
- `FindObjectByInstruction` для scene reference properties теж переведено
  на `SceneObjectLocator`;
- `BatchActions.Transaction.NormalizeAssetPath` тепер нормально приймає
  `Assets/...`, `/Assets/...`, `project:/Assets/...`,
  `project://Assets/...` і absolute paths під project root;
- `BatchActions.ValidationHelpers.BuildScenePath` і `UniBridge_ManageScene`
  теж навчені працювати з повним scene asset path у `Path`, а не тільки з
  папкою під `Assets`;
- `BatchActions DryRun=false` прокидає dry-run state у nested tools, які
  підтримують dry-run, і додає warning, якщо nested tool фактично лишився
  у dry-run;
- `BatchActions` validator приймає
  `ManageEditor.GetCompilationDiagnostics`;
- relay discovery cleanup видаляє stale `bridge-*.json` для мертвих Unity
  PID, якщо файл безпечно розпізнаний як discovery file.

Пакет синхронізовано у live-проекти:

- `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`.

MCP smoke на `Domovyk` після recompilation:

- `UnitySearch` знаходить inactive object
  `AllLevel/03_Intro_Cutscene_Leaf/DarknessLevel_Root/IntroSetup/NativeDomovykHandoffTriggerProxy`
  у `Assets/_Domovyk/Scenes/darkness/darkness1.unity`;
- `ManageGameObject Action=Modify SearchMethod=ByPath IncludeInactive=true`
  на цьому object успішний і повертає `activeSelf=false`,
  `activeInHierarchy=false`;
- той самий target з `IncludeInactive=false` очікувано не знаходиться і
  повертає explicit error з `(IncludeInactive=False)`;
- `GetComponents` на inactive target працює і повертає `Transform`;
- `SetComponentProperty`, `AddComponent`, `RemoveComponent` з відсутніми
  mutating parameters проходять target resolution і падають уже на
  очікувану validation-помилку параметрів, не на "object not found";
- `ManageGameObject Action=Modify SearchMethod=ById IncludeInactive=true`
  на returned instance id працює;
- `BatchActions DryRun=true` з scene step
  `Path=/Assets/_Domovyk/Scenes/darkness/darkness1.unity` успішний:
  impact показує `Assets/_Domovyk/Scenes/darkness/darkness1.unity`
  `exists=true`, validation errors `0`;
- `BatchActions DryRun=true` з
  `editor/GetCompilationDiagnostics` проходить validation;
- `BatchActions DryRun=true` з inactive `game_object/Modify` проходить
  validation і impact показує scene file `exists=true`;
- `BatchActions DryRun=false` no-op `game_object/Modify` виконав nested
  tool без `executionWarnings`, повернув
  `No modifications applied`, scene залишилась `isDirty=false`;
- `ManageEditor GetCompilationDiagnostics`: retained diagnostics
  `errors=0`, `warnings=0`;
- `ContextSnapshot Depth=Brief IncludeConsole=true` у Domovyk показує
  pre-existing project warnings/one Unity Graphs NullReference in console,
  але не compile diagnostics і не UniBridge compile errors.

## 2026-06-03: UniBridge 0.2.5 Prefab/component lookup polish

Задача прийшла з live-роботи AI над `Domovyk`: у Prefab Mode
`Find/SearchMethod=ByComponent` не знаходив `DialogueBubble` на prefab
`Assets/_Domovyk/Prefabs/Systems/CanvasGameplayWorldSpace.prefab`, хоча
YAML мав старий `m_EditorClassIdentifier: Assembly-CSharp::DialogueBubble`,
а runtime type уже переїхав у namespace `Domovyk.DialogueBubble`.

Зміни:

- package version піднято до `0.2.5`;
- `ComponentIdentity` додано як спільний helper для component matching:
  short type name, full type name, assembly-qualified name,
  `AssemblyName::TypeName`, MonoScript name/path/GUID і serialized
  `m_EditorClassIdentifier`;
- `SceneObjectLocator ByComponent` тепер сканує live components у сцені і
  Prefab Stage, включно з inactive objects, а не залежить лише від
  `Type.GetType`;
- `GameObjectSerializer`, `ManageGameObject GetComponents` і
  `TypeSchema InspectGameObject` повертають `scriptIdentity`,
  `serializedEditorClassIdentifier`, `scriptGuid` і
  `namespaceMigration` diagnostic, якщо serialized class id старий, а
  runtime type уже новий;
- `TypeSchema InspectGameObject` отримав `IncludeInactive` target lookup;
- `ManageGameObject AddComponent` для UI Graphic тепер дає actionable error,
  якщо на object уже є інший `Graphic` (`TextMeshProUGUI` перед `Image`);
- `ContextSnapshot IncludeConsole` за замовчуванням працює в
  `ConsoleSummaryMode=Compact`: totals, critical/warning/spam groups без
  довгих `timelineHighlights` / `recentSamples`;
- `ReadConsole` отримав agent-friendly aliases:
  `CreateMarker`, `ReadSinceMarker`, `ClearConsole`;
- `ManagePrefab save_stage` / `close_stage` повертають before/current
  Prefab Stage state і пояснюють, якщо Unity уже закрила stage.

Пакет синхронізовано у live-проекти:

- `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`.

MCP smoke на `Domovyk` після recompilation:

- `ManagePrefab open_stage` відкрив
  `Assets/_Domovyk/Prefabs/Systems/CanvasGameplayWorldSpace.prefab`;
- `ManageGameObject Find SearchMethod=ByComponent IncludeInactive=true`
  знаходить `UI_TalkLine` за `DialogueBubble`,
  `Domovyk.DialogueBubble` і `Assembly-CSharp::DialogueBubble`;
- `ManageGameObject GetComponents` для `UI_TalkLine` показує
  `runtimeType=Domovyk.DialogueBubble`,
  `monoScriptGuid=e3693741ff6a36643abf0f224782b529`,
  `serializedEditorClassIdentifier=Assembly-CSharp::DialogueBubble` і
  namespace migration message;
- `TypeSchema InspectGameObject` з component filter `DialogueBubble`
  повертає ті самі `scriptIdentity` / `namespaceMigration`;
- `ManageGameObject AddComponent ComponentName=Image` на object із
  `TextMeshProUGUI` падає очікувано і пояснює:
  remove existing `TextMeshProUGUI` Graphic before adding `Image`;
- `ContextSnapshot Depth=Brief IncludeConsole=true` повертає compact
  console summary і omits `recentSamples` / `timelineHighlights`;
- `ReadConsole CreateMarker` / `ReadSinceMarker` працюють;
- `ManagePrefab close_stage` після уже закритого stage повертає success із
  поясненням, а не misleading failure;
- фінальні `ManageEditor GetCompilationDiagnostics`: `errors=0`,
  `warnings=0`;
- post-marker `ReadConsole Overview` для Error/Exception/Assert:
  `filteredEntries=0`.

Direct bridge smoke на відкритому `UniBridge_Test_Project`:

- test Unity bridge знайдено через discovery file
  `bridge-336f26c7-86404.json`, pipe
  `\\.\pipe\unity-mcp-336f26c7-86404`;
- Codex tool discovery у цьому thread поки не експонував окремий namespace
  для test project, тому smoke виконано напряму через UniBridge
  newline-delimited JSON MCP bridge;
- handshake `protocol=unity-mcp`, `version=2.0`, `tools=61`;
- project root `H:/Repos/UnityRepos/UniBridge_Test_Project`;
- Unity `6000.4.9f1`, active scene
  `Assets/CorgiEngine/Demos/Minimal/FeaturesPlatforms.unity`;
- package version у snapshot `0.2.5`;
- `WaitForReady`: ready, not compiling, not playing;
- `GetCompilationDiagnostics`: `errors=0`, `warnings=0`;
- `ContextSnapshot Depth=Brief IncludeConsole=true` працює у
  `ConsoleSummaryMode=Compact`;
- direct `UniBridge_UnitySearch` по `Camera` працює;
- `BatchActions DryRun=true` з exact tool names
  `UniBridge_UnitySearch`, `UniBridge_ContextSnapshot`,
  `UniBridge_ManageEditor` validated `3/3`, failed `0`;
- post-marker console check для Error/Exception/Assert:
  `filteredEntries=0`.

## 2026-06-05 - project-scoped relay discovery для CreepyTale5/Domovyk collision

Запит: коли відкрито кілька Unity Editor instances, Codex іноді бачить MCP
namespace/bridge не поточного workspace (`CreepyTale5`), а іншого проекта
(`Domovyk`). Потрібно прибрати silent attach до чужого bridge і додати root
guard для мутацій.

Зроблено в UniBridge source:

- `CodexIntegration` тепер пише project-scoped Codex config не тільки з
  `--project-id`, а й з `--project-path <ProjectRoot>` та
  `--name <unibridge_project_shortid>`. Це прив'язує relay до Unity project
  root того проекту, з якого налаштовували integration.
- Relay отримав `--workspace-root` і env fallback
  `UNIBRIDGE_WORKSPACE_ROOT`, `CODEX_WORKSPACE_ROOT`,
  `CODEX_PROJECT_ROOT`, `CODEX_CWD`, `WORKSPACE_ROOT`.
- Relay discovery тепер:
  - фільтрує candidates по `project_root` або `Assets` path;
  - ранжує bridge records за explicit project path / workspace root /
    project id;
  - видаляє stale discovery files для dead Unity PIDs як і раніше;
  - відмовляється від неоднозначного вибору, якщо кілька live bridges
    матчаться без `--instance-id`, і повертає список `projectName`,
    short id, pid, root, assets path, source discovery file.
- `_server_info` тепер повертає `projectRoot`, `expectedProjectRoot`,
  `workspaceRoot` і `availableConnections`; action `all` тепер реально
  повертає status + connections + tools.
- Relay `serverInfo.name` став project-scoped:
  `unibridge_<project_slug>_<shortid/hash>` замість generic
  `unity-mcp-server`.
- Relay додає прихований параметр
  `__unibridge_expected_project_root` до Unity tool calls, якщо має
  `--project-path` або workspace root.
- Додано `ProjectContextGuard` у Unity package:
  - всі tool results отримують top-level `projectContext`
    (`id`, `name`, `root`, `assetsPath`, `settingsPath`);
  - mutating / compile-reload tools відмовляються виконуватися, якщо
    переданий/relay-injected expected root не збігається з
    `ProjectIdentity.ProjectRoot`;
  - error responses із `Bridge.ExecuteCommandAsync` теж містять
    `projectContext`.
- Tool discovery annotations тепер містять `uniBridgeProject`, щоб список
  tools сам показував project root/id/name bridge, який їх експонує.
- Relay version піднято до `1.1.0-build.11`, `RelayApp~/relay.json`
  оновлено, bundled relay binaries republished для:
  `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`.

Перевірка:

- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj -c Release`:
  `0 Warning(s)`, `0 Error(s)`;
- `unibridge_relay_win.exe --version` показує
  `Version: 1.1.0-build.11`.

Після перевірки пакет синхронізовано в
`H:/Repos/Experiments/CreepyTale5/Packages/com.cidonix.unibridge`; у
CreepyTale5 embedded relay також показує `1.1.0-build.11`, а
`CreepyTale5_context.md` доповнено записом про оновлення UniBridge package.

## 2026-06-06: UniBridge 0.2.6 BatchActions script validation

Запит від агента Domovyk: `UniBridge_ValidateScript` існував як окремий MCP
tool, але `UniBridge_BatchActions` відхиляв його allow-list помилкою. Це
заважало сценарію: validate кілька `.cs` файлів -> refresh assets -> request
compile -> wait editor ready -> read console.

Зроблено в source package:

- package version піднято до `0.2.6`;
- `BatchActionToolCatalog` отримав allow-list entry
  `UniBridge_ValidateScript`;
- додано batch aliases:
  `validate_script`, `validatescript`, `script_validation`,
  `script_validate`, `validate_cs`, `cs_validation`;
- `BatchActions.Steps` нормалізує `Uri` з `uri`, `Path`, `AssetPath`,
  `ScriptPath`, `File`, а також `Level` і `IncludeDiagnostics`;
- `BatchActions.Validation` отримав dedicated read-only validation для
  `UniBridge_ValidateScript`: перевіряє наявність `Uri`/path, `.cs`,
  `Assets/...`, існування script file і `Level=basic|standard`;
- `BatchActions.Transaction` тепер:
  - нормалізує `unity://path/...` і `file://...` у impact/rollback path scan;
  - маркує `ValidateScript` paths як `read`, а не `touch`;
  - показує rollback hint, що script validation read-only і не потребує
    Undo/rollback;
- `BatchActions` description, `ToolGuide`, `Documentation~/unibridge.md`,
  `README.md`, `CHANGELOG.md`, `RELEASE_NOTES.md` оновлено під batch-safe
  script validation workflow.

План перевірки після sync у Domovyk:

- sync `com.cidonix.unibridge` у
  `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`;
- дочекатися recompilation / `WaitForReady`;
- через MCP виконати `UniBridge_BatchActions DryRun=false`:
  - `UniBridge_ValidateScript` для
    `Assets/_Domovyk/Scripts/Cinematics/DarknessCutsceneTrigger.cs`;
  - alias `validate_script` для
    `Assets/_Domovyk/Scripts/Cinematics/DarknessDarkEntityRevealCutscene.cs`;
  - `UniBridge_ManageEditor RefreshAssets`;
  - `UniBridge_ManageEditor RequestScriptCompilation`;
  - `UniBridge_ManageEditor WaitForReady`;
  - `UniBridge_ManageEditor GetCompilationDiagnostics`;
  - `UniBridge_ReadConsole` compact error overview.

Фактична перевірка в Domovyk:

- пакет синхронізовано в
  `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`;
- `ContextSnapshot` підтвердив:
  - project `Domovyk`;
  - active scene `Assets/_Domovyk/Scenes/darkness/darkness1.unity`;
  - package `com.cidonix.unibridge` version `0.2.6`;
  - editor ready (`isCompiling=false`, `isUpdating=false`);
- `UniBridge_BatchActions DryRun=true` пройшов validation:
  - 7/7 steps validated;
  - no allow-list error;
  - `UniBridge_ValidateScript` і alias `validate_script` normalized to
    `UniBridge_ValidateScript`;
  - validation provider `ValidateScriptValidation`;
  - script paths marked with likely intent `read`;
  - rollback hint explicitly says read-only validation needs no Undo/rollback;
- `UniBridge_BatchActions DryRun=false` виконав весь workflow:
  - 7/7 steps executed;
  - 0 failed;
  - `DarknessCutsceneTrigger.cs`: diagnostics `[]`;
  - `DarknessDarkEntityRevealCutscene.cs`: one warning diagnostic:
    `String concatenation in Update() can cause garbage collection issues`;
  - `RefreshAssets`, `RequestScriptCompilation`, `WaitForReady`,
    `GetCompilationDiagnostics`, `ReadConsole Overview` all executed inside the
    same batch;
  - retained compilation diagnostics still contain an existing external error
    from `Library/PackageCache/com.esotericsoftware.spine.timeline...`
    (`CS0103: MixFrom does not exist in the current context`). This is not a
    UniBridge allow-list/regression issue and was already visible before the
    batch test.
- `UniBridge_ToolGuide Action=Workflow Topic=scripts` у Domovyk підтвердив,
  що майбутні агенти бачать:
  - `verifyCalls` з `UniBridge_ValidateScript IncludeDiagnostics=true`;
  - `batchAliases`: `validate_script`, `script_validate`, `cs_validation`;
  - note: `BatchActions supports read-only script validation; script text
    editing still uses dedicated SHA/precondition tools.`

## 2026-06-06 - UniBridge 0.2.7 reload-safe script compilation

Запит від агента Domovyk: у UniBridge 0.2.6 виклик
`UniBridge_ManageEditor Action=RequestScriptCompilation WaitForCompletion=true`
міг закрити Unity/MCP connection під час assembly/domain reload і повертати
transport-level помилку `Unity connection closed`.

Зроблено:

- package version піднято до `0.2.7`;
- relay version піднято до `1.1.0-build.12`;
- додано reload-safe editor actions:
  - `RequestScriptCompilationNoWait`;
  - `WaitForReadyAfterReload`;
- `RequestScriptCompilation` з `WaitForCompletion=true` тепер не чекає inline
  крізь Unity assembly reload, а повертає контрольований queued result:
  `inlineWaitSkipped=true`, `reconnectRequired=true`, `reloadSafe=true`;
- новий рекомендований workflow для агентів:
  `ValidateScript` -> `RefreshAssets` -> `RequestScriptCompilationNoWait` ->
  `WaitForReadyAfterReload` -> `GetCompilationDiagnostics` ->
  `ReadConsole DiagnosticSummary`;
- `BatchActions` підтримує aliases:
  `request_script_compilation_no_wait`, `compile_no_wait`,
  `wait_for_ready_after_reload`, `wait_for_reconnect`, `wait_after_reload`;
- relay recovery path для compile/reload failures reconnect-иться до Unity,
  викликає `WaitForReadyAfterReload`, читає `GetCompilationDiagnostics` і
  повертає structured result замість raw transport error;
- оновлено `ToolGuide`, `DomainCatalog`, `README.md`, `CHANGELOG.md`,
  `RELEASE_NOTES.md`, `Documentation~/unibridge.md`, `package.json`,
  `relay.json`, `UniBridge.Relay.csproj`.

Build/sync:

- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj -c Release` успішний:
  `0 warnings`, `0 errors`;
- relay опубліковано self-contained для:
  `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`;
- embedded package relay перевірено:
  `unibridge_relay_win.exe --version` -> `1.1.0-build.12`;
- пакет синхронізовано в:
  - `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`;
  - `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `package.json` в обох проектах підтверджує version `0.2.7`.

Живий MCP test у Domovyk:

- після refresh/reload `ContextSnapshot` підтвердив:
  - project `Domovyk`;
  - Unity `6000.4.10f1`;
  - active scene `Assets/_Domovyk/Scenes/darkness/darkness1.unity`;
  - package `com.cidonix.unibridge` version `0.2.7`;
  - editor ready (`isCompiling=false`, `isUpdating=false`);
- `ToolGuide Workflow scripts` показує новий workflow з
  `RequestScriptCompilationNoWait` і `WaitForReadyAfterReload`;
- `DomainCatalog EditorOps` теж рекомендує reload-safe compile flow;
- `BatchActions DryRun=true` для validate -> refresh -> compile no-wait ->
  wait-after-reload -> diagnostics пройшов validation без помилок;
- старий repro-виклик через `BatchActions`:
  `RequestScriptCompilation WaitForCompletion=true Force=true TimeoutMs=120000`
  більше не валить MCP connection, а повертає success/queued з
  `reconnectRequired=true`;
- після domain reload `WaitForReadyAfterReload` повернув ready-state,
  `GetCompilationDiagnostics` повернув `errors=0`, `warnings=0`;
- `ReadConsole DiagnosticSummary` успішно читається після reload. У консолі
  Domovyk лишаються project warnings, але compile errors/exception/assert
  по тесту UniBridge немає.

Обмеження перевірки:

- у поточному Codex tool discovery доступний лише MCP namespace Domovyk;
  тестовий проект синхронізовано файлово до `0.2.7`, але live MCP smoke саме
  тестового проекту в цьому сеансі не був доступний як окремий namespace.
- зовнішній `dotnet build` Unity generated `.csproj` у test project не є
  валідною перевіркою пакета: він падає на відсутніх Unity source-generator
  references для локального Unity install, не на коді UniBridge.

## 2026-06-06 - UniBridge 0.2.8 reload-safe Play Mode boundaries

Запит від агента Domovyk: batch smoke workflow
`ClearConsole -> Play WaitForCompletion -> WaitForReady RequireNotPlaying=false -> DiagnosticSummary`
падав transport-level помилкою `Unity not detected. No matching UniBridge discovery file was found`,
хоча після цього окремі UniBridge calls знову працювали, а Unity вже була в
Play Mode. Причина: вхід/вихід з Play Mode може спричинити domain reload і
тимчасово закрити поточний Unity/MCP bridge.

Зроблено:

- package version піднято до `0.2.8`;
- relay version піднято до `1.1.0-build.13`;
- додано reload-safe editor actions:
  - `RequestPlayModeNoWait`;
  - `WaitForPlayMode`;
  - `WaitForEditMode`;
- `Play` / `ExitPlayMode` з `WaitForCompletion=true` більше не чекають inline
  через можливий Play Mode domain reload, а повертають контрольований boundary:
  `status=queued`, `reconnectRequired=true`, `reloadSafe=true`,
  `batchBoundary=true`, `changedProject=false`, `nextSuggestedCalls`;
- `BatchActions` тепер успішно зупиняється на такому boundary і повертає
  `postReconnect` hints замість виконання наступних кроків на stale pre-reload
  bridge;
- додано aliases для batch editor steps:
  `request_play_mode_no_wait`, `play_no_wait`, `wait_for_play_mode`,
  `wait_play`, `wait_for_edit_mode`, `wait_edit`;
- relay recovery path для Play Mode connection loss reconnect-иться до Unity і
  повертає structured recovery result замість raw transport error;
- оновлено `ToolGuide`, `DomainCatalog`, `README.md`, `CHANGELOG.md`,
  `RELEASE_NOTES.md`, `Documentation~/unibridge.md`, `package.json`,
  `relay.json`, `UniBridge.Relay.csproj`.

Build/sync:

- `dotnet build UniBridge.Relay/UniBridge.Relay.csproj -c Release` успішний:
  `0 warnings`, `0 errors`;
- relay опубліковано self-contained для:
  `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`;
- embedded package relay перевірено:
  `unibridge_relay_win.exe --version` -> `1.1.0-build.13`;
- пакет синхронізовано в:
  - `H:/Repos/UnityRepos/Domovyk/Packages/com.cidonix.unibridge`;
  - `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `package.json` в обох проектах підтверджує version `0.2.8`.

Живий MCP test у Domovyk:

- після `RefreshAssets` `ContextSnapshot` підтвердив:
  - project `Domovyk`;
  - Unity `6000.4.10f1`;
  - active scene `Assets/_Domovyk/Scenes/darkness/darkness1.unity`;
  - package `com.cidonix.unibridge` version `0.2.8`;
  - editor ready (`isCompiling=false`, `isUpdating=false`);
- dry-run batch для старого repro пройшов validation:
  `ClearConsole -> Play WaitForCompletion -> WaitForReady RequireNotPlaying=false -> DiagnosticSummary`;
- executing batch для старого repro більше не падає MCP transport error:
  повернув `success=true`, message `Batch stopped at a reload-safe editor boundary`,
  `reloadBoundary=true`, `postReconnect.reconnectRequired=true`,
  `postReconnect.nextSuggestedCalls`;
- друга фаза після reconnect:
  `WaitForPlayMode -> WaitForReady RequireNotPlaying=false -> DiagnosticSummary`
  виконалась успішно, editor state показав `isPlaying=true`;
- exit Play Mode теж повернув контрольований boundary через
  `ExitPlayMode WaitForCompletion=true`;
- після reconnect:
  `WaitForEditMode -> WaitForReady RequireNotPlaying=true -> GetPlayModeState`
  виконались успішно, editor state показав `isPlaying=false`;
- impact/rollback diagnostics у batch коректно резолвлять loaded scene paths:
  `Assets/_Domovyk/Scenes/darkness/darkness1.unity exists=true`.

Обмеження перевірки:

- у поточному Codex MCP discovery доступний лише Domovyk namespace; тестовий
  проект синхронізовано файлово до `0.2.8`, але окремий live MCP namespace для
  нього не був доступний у цьому сеансі.
- у Domovyk під час Play Mode smoke лишаються runtime/editor project issues у
  консолі, але вони не є UniBridge transport/compile failure: ключова перевірка
  проходить, MCP не втрачається hard-error і workflow має керований boundary.

## 2026-06-06 - Multi-project package sync check

Після відкриття трьох Unity проектів одночасно перевірено discovery:

- `Domovyk`:
  - root `H:/Repos/UnityRepos/Domovyk`;
  - editor PID `35776`;
  - package `0.2.8`;
  - relay `1.1.0-build.13`.
- `DomovykPrototype`:
  - root `H:/Repos/UnityRepos/DomovykPrototype`;
  - editor PID `96976`;
  - package `0.2.8`;
  - relay `1.1.0-build.13`.
- `UniBridge_Test_Project`:
  - root `H:/Repos/UnityRepos/UniBridge_Test_Project`;
  - editor PID `62436`;
  - package `0.2.8`;
  - relay `1.1.0-build.13`.

Синхронізація:

- `robocopy` з `H:/Repos/Cidonix/UniBridge/com.cidonix.unibridge` у всі три
  `Packages/com.cidonix.unibridge`;
- `DomovykPrototype` реально оновив 44 файли, тобто був старіший за source
  package;
- `Domovyk` і `UniBridge_Test_Project` уже були на тій самій версії.

Targeted MCP refresh:

- окремий Codex namespace лишився прив'язаний до Domovyk, але relay executable
  підтримує `--project-path`, тому для Prototype/Test зроблено direct MCP stdio
  calls через `unibridge_relay_win.exe --mcp --project-path <project>`;
- `UniBridge_ManageEditor RefreshAssets Force=true WaitForCompletion=true`
  успішний для:
  - `DomovykPrototype`;
  - `UniBridge_Test_Project`;
- `UniBridge_ContextSnapshot Depth=Brief` після refresh підтвердив для обох:
  - package version `0.2.8`;
  - `isCompiling=false`;
  - `isUpdating=false`;
  - `isPlaying=false`.

## 2026-06-13 - UniBridge 0.2.11 WorkSession safety tool

Додано перший практичний крок після аналізу Locus-подібного workflow:
`UniBridge_WorkSession`.

Навіщо:

- дати AI-агенту project-local checkpoint перед роботою;
- швидко бачити, які файли змінились під час поточної сесії;
- отримувати компактний diff без ручного `git diff`;
- робити dry-run revert і реальний revert вибраних файлів із baseline;
- зменшити ризик "агент щось зробив, але не перевірив власний слід".

Основні дії:

- `Begin` - створює baseline snapshot у `Library/UniBridge/WorkSessions`;
- `Status` / `Review` - повертають summary і changed files;
- `Diff` - повертає компактний text diff;
- `Revert` - за замовчуванням dry-run, з `DryRun=false` відновлює/видаляє
  вибрані файли;
- `End` / `List` - завершує або показує сесії.

Деталі реалізації:

- baseline за замовчуванням сканує `Assets`, `ProjectSettings`,
  `Packages/manifest.json`, `Packages/packages-lock.json`;
- capturable text/YAML assets зберігаються під `Library`, не в git;
- `Revert` автоматично додає `.meta` для added asset, якщо
  `DeleteAddedMetaWithAsset=true`;
- changed-file output зроблено camelCase (`path`, `changeType`, `riskFlags`)
  для зручності майбутніх агентів;
- timestamp-only Unity import noise не рахується як зміна, якщо SHA256 збігається.

Docs/package:

- `package.json` піднято до `0.2.11`;
- оновлено `README.md`, `CHANGELOG.md`, `RELEASE_NOTES.md`,
  `Documentation~/unibridge.md`;
- `Discover`, `ToolGuide`, `DomainCatalog`, `ToolExecutionScheduler`
  отримали WorkSession aliases/workflow/domain.

Живий MCP smoke у `UniBridge_Test_Project`:

- sync source package у
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `UniBridge_ManageEditor RefreshAssets WaitForCompletion=true Force=true`
  успішно пережив reload boundary і повернув ready;
- `tools/list` показав `UniBridge_WorkSession`, загалом `65` tools;
- `GetCompilationDiagnostics`: `errors=0`, `warnings=0`;
- smoke workflow:
  `ClearConsole -> WorkSession Begin -> CreateScript Assets/UniBridgeWorkSessionProbe.cs
  -> WaitForReadyAfterReload -> WorkSession Review -> Diff -> Revert dry-run
  -> Revert DryRun=false -> Review -> End`;
- `Review` побачив рівно:
  - `Assets/UniBridgeWorkSessionProbe.cs`;
  - `Assets/UniBridgeWorkSessionProbe.cs.meta`;
- `Diff` містив marker `work-session-smoke`;
- dry-run plan показав delete для `.cs` і `.meta`;
- після real revert `Review` повернув `post_revert_changed_paths=[]`;
- фінальна консоль:
  `totalEntries=0`, `warningCount=0`, `errorCount=0`, `exceptionCount=0`.

## 2026-06-14 - UniBridge 0.2.13 WorkSession semantic smoke result

Після синхронізації пакета в
`H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`
Unity спершу бачила новий `package.json`, але тримала стару compiled assembly.
Через MCP виконано `RefreshAssets WaitForCompletion=true Force=true`:

- refresh перетнув Unity reload boundary;
- relay повернув reload-safe result:
  `recoveredAfterRefreshReload=true`, `reloadBoundary=true`,
  `reconnectRequired=true`, `reloadSafe=true`;
- `WaitForReadyAfterReload` підтвердив `isReady=true`,
  `isCompiling=false`, `isPlaying=false`;
- retained compilation diagnostics після reload:
  `errors=0`, `warnings=0`.

Live semantic smoke після reload:

- `UniBridge_Discover Action=Ping` підтвердив package version `0.2.13`;
- `UniBridge_WorkSession Action=Begin` створив semantic baseline:
  `sceneCount=1`, `objectCount=363`, `truncated=false`,
  `SnapshotPath=semantic/loaded-scenes-baseline.json`;
- створено `UniBridgeSemanticProbe` через `UniBridge_ManageGameObject`;
- `UniBridge_WorkSession Action=Review` повернув:
  - `semanticReview.enabled=true`;
  - `baselineObjects=363`;
  - `currentObjects=364`;
  - `commonObjects=363`;
  - `totalChanges=1`;
  - `objectsCreated=1`;
  - change `Created` з `objectId=-10184`, `afterPath=/UniBridgeSemanticProbe`,
    `afterIndexedPath=/UniBridgeSemanticProbe[7]`;
- `UniBridge_ExecutionStatus Action=Snapshot IncludeWorkSession=true` містив
  active WorkSession summary з `semanticReview.enabled=true`;
- після `Delete` того ж probe повторний `Review` повернув:
  `baselineObjects=363`, `currentObjects=363`, `totalChanges=0`,
  `objectsCreated=0`, `objectsDeleted=0`;
- session завершено через `WorkSession End`;
- фінальний `ReadConsole DiagnosticSummary`:
  `totalEntries=0`, `warningCount=0`, `errorCount=0`,
  `exceptionCount=0`;
- фінальний `GetCompilationDiagnostics`:
  `errors=0`, `warnings=0`.

Висновок: semantic WorkSession review реально корисний для AI self-check після
live scene edits: агент бачить не тільки file diff, а й конкретний semantic
результат у loaded scene, включно з `objectId`, `path` та `indexedPath`, і може
перевірити cleanup до нульового diff.

## 2026-06-14 - UniBridge 0.2.14 ContextSnapshot agent onboarding brief

Після питання користувача уточнено: окремий `ProjectBrief` tool не потрібен,
бо базова карта проєкту вже існує як `UniBridge_ContextSnapshot`. Замість
дублювання зроблено polish існуючого tool.

Зміни:

- `ContextSnapshot.cs` зроблено `partial`;
- додано `ContextSnapshot.AgentBrief.cs` і `.meta`;
- `UniBridge_ContextSnapshot` тепер за замовчуванням повертає
  `data.agentBrief`;
- додано параметр `IncludeAgentBrief` (`true` за замовчуванням,
  `false` повертає стару raw snapshot форму з `agentBrief=null`);
- `agentBrief` містить:
  - `summary` - короткий one-line project overview;
  - `projectShape` - total Assets files, counts by kind, loaded scenes,
    root-object count, dirty-scene count, active scene, scene scale;
  - `likelyFolders` - bounded folder hints для scenes/scripts/gameplay/UI/
    prefabs/art/audio/data;
  - `likelyImportantSystems` - package/render-pipeline/asset-folder/asmdef
    signals;
  - `activeWorkSession` - чи є активний `UniBridge_WorkSession`;
  - `riskFlags` - compiling/importing, Play Mode, dirty scenes, Prefab Stage,
    console issues, hierarchy truncation, large loaded scenes, missing
    WorkSession;
  - `guardrails` - короткі правила безпечної роботи;
  - `recommendedNextCalls` - що агенту викликати далі.
- `package.json` піднято до `0.2.14`;
- оновлено `README.md`, `CHANGELOG.md`, `RELEASE_NOTES.md`,
  `Documentation~/unibridge.md`.

Live MCP smoke у `UniBridge_Test_Project`:

- пакет синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`
  без `RelayApp~`, бо relay exe був зайнятий відкритим Unity;
- `RefreshAssets WaitForCompletion=true Force=true` перетнув reload boundary,
  relay відновив connection і повернув ready;
- `WaitForReadyAfterReload RequireNotPlaying=true`: success;
- `GetCompilationDiagnostics`: `errors=0`, `warnings=0`;
- `UniBridge_ContextSnapshot Depth=Brief IncludeAgentBrief=true`:
  - package version `0.2.14`;
  - `hasAgentBrief=true`;
  - summary:
    `Project has 4172 Assets/ files, small loaded-scene scale, key signals:
    Input System, Cinemachine, Timeline, URP; risk flags: 3.`;
  - `projectShape.totalAssets=4172`;
  - `loadedSceneCount=1`;
  - `loadedRootObjectCount=7`;
  - risk flags:
    `dirty_scenes`, `hierarchy_truncated_by_depth`,
    `no_active_work_session`;
  - `recommendedNextCalls` містить `WorkSession Begin`, `ToolGuide Overview`,
    `DomainCatalog SuggestTools`, `SceneObjectView`, `UnitySearch`,
    `CaptureView`;
  - `likelyImportantSystems` знайшов Input System, Cinemachine, Timeline, URP,
    UGUI, UI Toolkit, 2D Tilemap, TextMesh Pro, Corgi Engine і asmdef signals;
- `UniBridge_ContextSnapshot Depth=Brief IncludeAgentBrief=false`:
  - package version `0.2.14`;
  - `agentBriefIsNull=true`;
- фінальна консоль:
  `totalEntries=0`, `warningCount=0`, `errorCount=0`, `exceptionCount=0`.

Висновок: для нових агентів тепер є один obvious старт:
`UniBridge_ContextSnapshot Depth=Brief IncludeAgentBrief=true`. Він не замінює
сирі секції snapshot, а дає компактну actionable надбудову, щоб агент швидше
зорієнтувався і почав із WorkSession/ToolGuide/DomainCatalog замість хаотичного
набору викликів.

## 2026-06-14 - UniBridge 0.2.13 WorkSession semantic scene review

Мета: зробити `UniBridge_WorkSession` кориснішим саме як self-review шар після
Unity scene work. До цього WorkSession добре показував changed files, але агент
бачив лише `.unity changed` / `.prefab changed`, а не фактичні зміни у live
сцені.

Зміни:

- `WorkSession.cs` зроблено `partial`, семантичну частину винесено в
  `WorkSession.Semantics.cs`;
- `UniBridge_WorkSession Action=Begin` за замовчуванням створює compact
  loaded-scene semantic baseline у
  `Library/UniBridge/WorkSessions/<session>/semantic/loaded-scenes-baseline.json`;
- додано параметри:
  - `IncludeSceneSemantics`;
  - `MaxSemanticObjects`;
  - `IncludeSemanticReview`;
  - `MaxSemanticChanges`;
- `Action=Status` і `Action=Review` тепер додають `semanticReview`, якщо
  baseline доступний;
- helper `BuildCompactActiveReview` теж додає `semanticReview`, тому
  `BatchActions` auto-review і `ExecutionStatus` active WorkSession summary
  можуть показувати live-scene semantic changes;
- semantic diff працює по stable `UnityApiAdapter.GetObjectId` і повертає
  `path` / `indexedPath` для пояснення;
- зараз semantic review покриває:
  - created/deleted GameObjects;
  - moved/reparented/siblingIndex changes;
  - renamed objects;
  - active/tag/layer changes;
  - component list changes;
  - missing scripts introduced/resolved;
  - renderer sorting/material/enabled changes;
  - prefab source/status/root info changes;
  - local/world transform signature changes;
- file revert поведінка не змінювалась: `Action=Revert` досі відновлює або
  видаляє тільки файли зі snapshot, semantic review є visibility/self-check
  шаром для loaded scenes.

Docs/package:

- `package.json` піднято до `0.2.13`;
- додано `WorkSession.Semantics.cs.meta`;
- оновлено `README.md`, `CHANGELOG.md`, `RELEASE_NOTES.md`,
  `Documentation~/unibridge.md`.

План live smoke:

- синхронізувати пакет у `UniBridge_Test_Project`;
- `RefreshAssets` / compile diagnostics мають бути чисті;
- сценарій:
  `ClearConsole -> WorkSession Begin -> BatchActions create GameObject
  UniBridgeSemanticProbe -> WorkSession Review/ExecutionStatus -> delete probe
  -> Review -> End`;
- очікування: після create `semanticReview.summary.objectsCreated >= 1`, після
  delete semantic changes повертаються до нуля, а консоль без errors/warnings.

## 2026-06-13 - UniBridge 0.2.12 WorkSession auto-review polish

Мета: зробити WorkSession не просто окремим tool, а видимим самоконтролем
після mutating workflows, щоб агент менше покладався на пам'ять.

Зміни:

- `UniBridge_BatchActions` після executing batch (`DryRun=false`) автоматично
  додає `data.workSessionReview`, якщо активна `UniBridge_WorkSession`;
- додано `IncludeWorkSessionReview` і `WorkSessionReviewMaxChanged` для
  `UniBridge_BatchActions`;
- `UniBridge_ExecutionStatus Action=Snapshot|Recent` тепер включає активний
  WorkSession summary за замовчуванням;
- додано `IncludeWorkSession` і `WorkSessionMaxChanged` для
  `UniBridge_ExecutionStatus`;
- `UniBridge_WorkSession` отримав helper для компактного active review,
  який повертає `active`, `reviewAvailable`, session summary, changed counts,
  bounded changed files, warnings і suggested calls;
- `UniBridge_SceneHierarchyExport` більше не викликає obsolete
  `Object.GetInstanceID()` на Unity 6.4+, щоб не засмічувати compile diagnostics.

Docs/package:

- `package.json` піднято до `0.2.12`;
- оновлено `README.md`, `CHANGELOG.md`, `RELEASE_NOTES.md`,
  `Documentation~/unibridge.md`, `Discover`, `ToolGuide`, `DomainCatalog`.

Живий MCP smoke у `UniBridge_Test_Project`:

- source package синхронізовано в
  `H:/Repos/UnityRepos/UniBridge_Test_Project/Packages/com.cidonix.unibridge`;
- `RefreshAssets WaitForCompletion=true Force=true` перетнув reload boundary,
  relay відновив connection і повернув ready;
- `UniBridge_Discover Action=Ping` підтвердив package version `0.2.12`;
- `GetCompilationDiagnostics`: `errors=0`, `warnings=0`;
- сценарій:
  `ClearConsole -> WorkSession Begin -> BatchActions DryRun=false CreateFolder
  Assets/UniBridgeAutoReviewProbe -> ExecutionStatus Snapshot
  -> WorkSession Revert dry-run -> Revert DryRun=false -> Review -> End`;
- `BatchActions` повернув `data.workSessionReview.active=true` і зміни:
  `Assets/UniBridgeAutoReviewProbe.meta`;
- `ExecutionStatus Snapshot` повернув той самий active WorkSession summary;
- після real revert `WorkSession Review` повернув `post_revert_paths=[]`;
- фінальна консоль:
  `totalEntries=0`, `warningCount=0`, `errorCount=0`, `exceptionCount=0`.
