# AI Feedback from Domovyk Port

Дата старту нотаток: 2026-05-18

Джерело спостережень: Codex / AI-агент, який працює над портом гри Domovyk у проекті `H:\Repos\UnityRepos\Domovyk`.

Пов'язані Unity-проекти:

- Основний проект: `H:\Repos\UnityRepos\Domovyk`
- Референсний проект прототипу: `H:\Repos\UnityRepos\DomovykPrototype`
- Пакет / relay: `H:\Repos\Cidonix\UniBridge`

## Робоча домовленість

Для задач Domovyk агент має:

- пряме копіювання файлів між проектами виконувати через файлову систему, коли це безпечніше і прозоріше;
- програмування, роботу зі сценами, ієрархіями, компонентами, консоллю Unity, імпортами, перевірками сцени та runtime/editor-станом виконувати через UniBridge MCP;
- перед будь-якою дією в Unity перевіряти, до якого проекту підключений MCP, через `GetProjectRoot` або server info;
- усі спостереження про поведінку UniBridge, невідповідності між очікуваною та реальною дією, UX-проблеми, побажання до API й ідеї покращень дописувати в цей файл.

## Початкові спостереження

### 2026-05-18 - один relay без project-id може перемикнутися не туди

Що робив AI:

- Перевіряв UniBridge MCP підключення, коли одночасно були відкриті `Domovyk` і `DomovykPrototype`.
- Викликав server info / reconnect і `Unity_ManageEditor.GetProjectRoot`.
- Додатково перевіряв named pipes через файлову систему Windows.

Що побачив:

- У системі одночасно існували два pipe-и:
  - `unity-mcp-835bb044-76964`
  - `unity-mcp-f6b43037-29072`
- Один MCP namespace `mcp__unibridge__` мав тільки один `selectedConnection`.
- Спочатку relay був на `Domovyk`, після `reconnect` перемкнувся на `DomovykPrototype`.
- Інструменти Unity після цього працювали вже з `H:/Repos/UnityRepos/DomovykPrototype`, хоча робочий каталог Codex лишався `H:\Repos\UnityRepos\Domovyk`.

Висновок:

- Для двох відкритих Unity-проектів один relay без `--project-id` небезпечний: агент може випадково почати читати або змінювати не той проект.
- Модель `1 relay process = 1 Unity project target` виглядає правильною.

Пропозиція:

- Для робочих сценаріїв з Domovyk створити два named MCP server entries:
  - `unibridge_domovyk` з `--project-id 9e06e2a3f11c456fba6a4f50d9cd83c9`
  - `unibridge_domovykprototype` з `--project-id 98ba710e44ec47c4a76d198a1ce7423f`
- У server info бажано явно показувати не тільки `selectedConnection`, а й список усіх знайдених candidate connections з `projectName`, `projectPath`, `projectId`, `editorPid`, `pipe`.
- Якщо relay запущено без `--project-id` і знайдено більше одного live Unity project, бажано повертати warning у server info, наприклад: "Multiple Unity projects detected; use --project-id for deterministic routing."

Чому це важливо:

- У задачах портування Domovyk агент часто має одночасно порівнювати сцену/ієрархію/компоненти прототипу і основного проекту.
- Помилковий target може зіпсувати сцену або зробити висновки по неправильному проекту.

## Майбутні нотатки

Нові спостереження буду додавати нижче окремими датованими секціями з форматом:

- що робив AI;
- що очікував;
- що реально зробив UniBridge;
- чи це помилка, UX-проблема, або побажання;
- пропозиція, як покращити.

### 2026-05-18 - MCP tool namespace може зникнути між викликами в Codex

Що робив AI:

- Після створення цього файлу спробував ще раз перевірити поточний target UniBridge через `_server_info(status)`.

Що очікував:

- Отримати `projectName`, `projectPath`, `selectedConnection`, як у попередніх викликах.

Що сталося:

- Виклик повернув повідомлення, що `mcp__unibridge___server_info` зараз недоступний, хоча раніше в цій же сесії інструмент вже працював.
- Повідомлення виглядало як placeholder від Codex/tool runtime: implementation is not currently available / retry after tool becomes available.

Попередній висновок:

- Це може бути не помилка самого UniBridge, а поведінка Codex deferred tool loading або зміна доступного tool set між turns.
- Але для користувача і агента це виглядає як нестабільність MCP-доступу, тому варто мати просту діагностичну рекомендацію: якщо UniBridge tool namespace зник, агент має повторити tool discovery і не робити висновок, що Unity bridge зламаний.

Пропозиція:

- У документації UniBridge для Codex додати короткий troubleshooting пункт: "If MCP tools disappear in Codex, run tool discovery / reconnect the MCP server entry before restarting Unity."
- Якщо можливо з боку relay, корисно мати дуже легкий зовнішній healthcheck або зрозумілий лог у Unity, який дозволяє відрізнити "relay не бачить Unity" від "Codex не підвантажив tool namespace".

### 2026-05-18 - Український MarkerLabel у ReadConsole пошкоджується у відповіді

Що робив AI:

- У проекті `Domovyk` через `UniBridge_ReadConsole` створив console marker перед перевіркою після видалення старої Ori Turbulence-системи.
- Виклик мав `MarkerLabel`: `Після видалення Turbulence: refresh/compile check`.

Що очікував:

- У відповіді `marker.label` і `markerMessage` мали повернути той самий читабельний український текст.

Що сталося:

- Маркер створився успішно і подальший фільтр `AfterMarkerId` працював.
- Але українська частина label у JSON-відповіді повернулась як набір псевдографіки/пошкоджених символів на кшталт `\u2568...`, тоді як англійська частина лишилась читабельною.

Попередній висновок:

- Функціонально marker/AfterMarkerId працює нормально.
- Є проблема кодування або подвійного перекодування Unicode у відповіді/console marker serialization для не-ASCII тексту.

Пропозиція:

- Перевірити шлях формування `MarkerLabel` і `markerMessage`: бажано, щоб JSON-відповідь повертала Unicode без втрати, або хоча б стабільно escaped UTF-16/UTF-8, який Codex нормально декодує.
- Додати невеликий тест з кирилицею для `ReadConsole.MarkSession`, бо для українських проектів це буде часто зустрічатись у label/comment полях.

### 2026-05-18 - RefreshAssets може пережити MCP timeout і лишитись active exclusive

Що робив AI:

- У проекті `Domovyk` після видалення старої системи `EnvironmentLight` викликав `UniBridge_ManageEditor` з `Action=RefreshAssets`, `Force=true`, `WaitForCompletion=true`.

Що очікував:

- Або отримати відповідь протягом timeout, або після timeout побачити завершену/скасовану операцію в scheduler diagnostics.

Що сталося:

- Codex tool call впав по timeout після 120 секунд.
- `UniBridge_ExecutionStatus Snapshot` одразу після цього показав, що `UniBridge_ManageEditor` досі висить як `activeExclusive` з policy `CompileReload`, elapsed приблизно 270 секунд, хоча початковий call уже завершився помилкою на стороні Codex.
- Ще через коротке очікування scheduler звільнився, `WaitForReady` відповів миттєво і Unity була ready/not compiling.

Попередній висновок:

- Ймовірно, Unity/AssetDatabase refresh реально тривав довше за MCP-call timeout, а relay/scheduler не має явного cancellation/timeout propagation для вже запущеної exclusive operation.
- Для агента це виглядає як зависання, але операція може просто дозавершитись пізніше.

Пропозиція:

- Для довгих `RefreshAssets`/compile/reload операцій або підняти tool timeout, або повертати проміжний статус/operation id, щоб агент міг poll-ити завершення без помилки.
- Якщо зовнішній MCP timeout стався, бажано в `ExecutionStatus` позначати первинну операцію як "client timed out, still running" або мати окрему команду safe cancel/forget для stale exclusive operations.

### 2026-05-18 - ReadConsole marker може зникнути з backlog після refresh

Що робив AI:

- Створив console marker `after EnvironmentLight removal`, потім викликав `RefreshAssets`, а після готовності Unity запросив `DiagnosticSummary` з `AfterMarkerId`.

Що очікував:

- Отримати тільки повідомлення після marker.

Що сталося:

- `DiagnosticSummary` повідомив, що marker entry не знайдено у поточному Unity Console backlog, але marker відомий у Unity session, тому використав fallback: current backlog.
- Через fallback у summary потрапили старі warning-и, які не обов'язково з'явилися саме після marker.

Пропозиція:

- Було б корисно мати marker-фільтр, який зберігає entry id/clock вододіл поза Unity Console backlog, щоб після refresh/clear/recompile можна було точно відрізнити нові повідомлення від старих.
- Якщо точна фільтрація неможлива, відповідь уже добре попереджає про fallback; можливо, варто додати коротке поле `filterPrecision: exact|fallback`.

### 2026-05-18 - RequestScriptCompilation може закрити Unity connection під час domain reload

Що робив AI:

- У проекті `Domovyk` після великої зачистки legacy Ori DOF/fog коду викликав через `UniBridge_BatchActions` editor action `RequestScriptCompilation` з `WaitForCompletion=true`.

Що очікував:

- Отримати або завершений compile result, або стабільний статус, що Unity компілює/перезавантажує домен.

Що сталося:

- Tool call повернув помилку `Unity connection lost: Unity connection closed`.
- Одразу після цього `UniBridge_ExecutionStatus` і `UniBridge_ContextSnapshot` знову успішно відповідали, тобто з'єднання відновилось, а Unity не залишилась у зламаному стані.
- Наступна перевірка консолі показала, що компіляційні помилки були прибрані, залишились тільки unrelated warning-и.

Попередній висновок:

- Схоже на нормальний Unity domain reload / reconnect window, який зараз виглядає для агента як hard failure.

Пропозиція:

- Для compile/reload operations корисно перехоплювати коротке закриття Unity pipe як очікуваний reconnectable стан: retry/poll `ExecutionStatus` певний час перед тим, як віддавати агенту остаточну помилку.
- У відповіді варто явно розрізняти `connection lost during reload, retrying` і справжню втрату Unity process/relay.

### 2026-05-18 - Ukrainian Console MarkSession label повертається mojibake

Що робив AI:

- У проекті `Domovyk` перед перевіркою після видалення legacy UberShader effects викликав `UniBridge_ReadConsole` з `Action=MarkSession`.
- `MarkerLabel` був українською: `Перед перевіркою після видалення UberShader ефектів`.

Що очікував:

- У відповіді marker label буде повернутий тим самим читабельним Unicode-текстом.

Що сталося:

- У JSON-відповіді поле `label` та `message` повернулися у вигляді mojibake з box-drawing символами на кшталт `\u2568...`.
- Сам marker створився і його `markerId` можна було використати, але людиночитабельний label у відповіді зіпсований.

Пропозиція:

- Перевірити шлях серіалізації/десеріалізації UTF-8 для текстових параметрів MCP tool calls, особливо для `MarkerLabel`.
- Добре було б додати regression test на кирилицю/український текст у tool input/output.

### 2026-05-18 - RequestScriptCompilation знову обірвав connection, але Unity відновилась

Що робив AI:

- У проекті `Domovyk` після великого видалення legacy UberShader scripts/components викликав `UniBridge_ManageEditor` з `Action=RequestScriptCompilation`, `WaitForCompletion=true`, `Force=true`.

Що сталося:

- Tool call через приблизно 111 секунд завершився помилкою `Unity connection lost: Unity connection closed`.
- Після цього `UniBridge_ExecutionStatus`, `WaitForReady`, `ReadConsole`, `GetCompilationDiagnostics` знову успішно відповідали.
- `GetCompilationDiagnostics` показав `totalRetained: 0`, `errors: 0`, `warnings: 0`; Unity не залишилась у пошкодженому стані.

Пропозиція:

- Це підтверджує попереднє спостереження: для `RequestScriptCompilation` варто мати reconnect/retry window після domain reload, щоб агент не трактував очікуваний reload як hard failure.

### 2026-05-18 - Console DiagnosticSummary показав 0 errors після повідомлення користувача про 61 errors

Що робив AI:

- У проекті `Domovyk` після видалення legacy Xbox/UWP/DVR/Trial/Difficulty/Leaderboard систем користувач повідомив, що в Unity Console видно 61 critical error.
- AI викликав `UniBridge_ReadConsole` з `Action=DiagnosticSummary`, `Types=["Error","Exception"]`, а потім `UniBridge_ContextSnapshot` з `IncludeConsole=true`.

Що сталося:

- Обидва UniBridge-виклики повернули 0 errors / 0 exceptions.
- `ContextSnapshot` також показав `isCompiling=false`, `isUpdating=false`, editor ready.
- Після `Assets/Refresh` через `UniBridge_ManageMenuItem` і `UniBridge_WaitForEvent(EditorReady)` повторна перевірка знову показала 0 errors / 0 exceptions.

Попередній висновок:

- Можливо, користувач бачив старий стан Console до reload/refresh, але для тестування UniBridge корисно перевірити, чи tool читає той самий console backlog/filter state, що і візуальне Unity Console вікно.

Пропозиція:

- Додати у `ReadConsole`/`ContextSnapshot` окремий debug-прапорець або поле з поточним Unity Console filter/collapse/clear state, якщо Unity API це дозволяє.
- Можливо, корисно мати action на кшталт `SyncAndReadCompilerDiagnostics`, який перед читанням явно чекає завершення compilation/import і повертає retained compiler diagnostics разом із Console entries.

### 2026-05-19 - Domain reload after temporary editor tool deletion still returns as connection lost

Що робив AI:

- У проекті `Domovyk` тимчасово додав editor menu tool для одноразової чистки сценових об'єктів з `MapStone` у назві.
- Після виконання tool видалив тимчасовий `.cs` і `.meta`, потім викликав `UniBridge_BatchActions` з `RefreshAssets`, `RequestScriptCompilation`, `WaitForReady`, `SaveAssets`.

Що сталося:

- Batch call знову завершився `Unity connection lost: Unity connection closed` саме на refresh/domain reload.
- Одразу після цього окремий `UniBridge_ManageEditor Action=WaitForReady` успішно повернув ready-state.
- `UniBridge_ReadConsole DiagnosticSummary` показав 0 errors / 0 exceptions / 0 asserts.
- Зміни на диску й у Unity були застосовані; тимчасовий tool був видалений, а сцени збережені.

Пропозиція:

- Для batch workflows, що містять `RefreshAssets` або `RequestScriptCompilation`, варто автоматично чекати reconnect і продовжувати наступні кроки, якщо Unity process живий і editor після reload стає ready.
- Було б корисно, щоб `UniBridge_BatchActions` повертав частковий structured result: які steps встигли виконатися до reload, чи був reconnect successful, і чи треба агенту повторити тільки tail steps.

### 2026-05-19 - ScopedEdit rollback triggered by optional missing GameObject steps

Що робив AI:

- У проекті `Domovyk` відкрив prefab scope `Assets/_Domovyk/Prefabs/seinHUD.prefab` через `UniBridge_ScopedEdit`.
- Хотів видалити кілька legacy `mapStone` GameObject-ів після відновлення prefab з Plastic.
- Частина кроків була позначена `optional=true`, бо деякі об'єкти могли бути дочірніми до вже видаленого root або відсутніми після попереднього кроку.

Що сталося:

- Два реально знайдені GameObject-и (`mapStonePickup`, `mapstones`) успішно видалились.
- Optional-кроки з `Target GameObject ... was not found` були позначені як skipped/success, але загальний batch все одно повернув `success=false` і виконав rollback.
- Через rollback успішно видалені optional workflow об'єкти повернулись, тож довелось повторити `ScopedEdit` тільки з двома точно наявними target-ами.

Пропозиція:

- Якщо step має `optional=true`, validation/runtime "not found" для цього step не має робити весь batch unsuccessful і не має запускати rollback, якщо всі required steps успішні.
- У `ScopedEdit`/`BatchActions` бажано розвести поля `requiredSuccess` і `optionalIssueCount`, щоб агент бачив, що optional steps не застосовані, але transaction committed.

### 2026-05-27 - ContextSnapshot занадто легко переповнює відповідь під час роботи з двома проєктами

Що робив AI:

- У межах портування `Domovyk` порівнював відкриту сцену `Location_0_Darkness` у двох одночасно відкритих Unity-проєктах: `DomovykPrototype` і `Domovyk`.
- Викликав `ContextSnapshot` для обох MCP server entries, щоб підтвердити правильний target project, активну сцену, sorting layers і стан консолі.

Що сталося:

- Підключення до двох named MCP server entries працювало коректно: кожен relay відповідав за свій проєкт.
- `ContextSnapshot` у режимі `Standard` повернув дуже великий блок package dependencies і великий console backlog, через що відповідь була обрізана (`truncated`).
- Для задачі перевірки sorting layers потрібні були тільки project identity, active scene, sorting layers і компактний console summary; package dependency dump у цьому випадку радше заважав.

Пропозиція:

- Зробити `IncludePackageDependencies=false` більш агресивним дефолтом для `Standard`, або додати окремий `IncludePackageDependencies="SummaryOnly"` без повного списку package cache paths.
- Для `IncludeConsole=true` додати компактніший режим: counts + latest important issues без довгого timeline/backlog, якщо агент явно не просить detailed console.
- Було б зручно мати окремий легкий action на кшталт `ProjectIdentityAndSceneState`, який повертає тільки project id/name/root, active scenes, play/compile/update state і sorting layers.

### 2026-05-27 - Потрібна діагностика Light2D sorting layer ID після імпорту сцени між проєктами

Що робив AI:

- У двох одночасно відкритих Unity-проєктах `DomovykPrototype` і `Domovyk` порівнював сцену `Location_0_Darkness`.
- Сортувальні шари `SpriteRenderer`/`ParticleSystemRenderer`/`SpriteShapeRenderer` були перенесені коректно за name/order mapping.
- Візуал усе одно відрізнявся, тому AI перевірив serialized `Light2D.m_ApplyToSortingLayers`.

Що сталося:

- У перенесеній сцені `Domovyk` 114 зі 122 `Light2D` компонентів мають у `m_ApplyToSortingLayers` старі numeric IDs sorting layers з `DomovykPrototype`.
- Через це світла можуть не застосовуватись до потрібних шарів, хоча самі Renderer sorting layers виглядають правильними.
- `UniBridge_SceneObjectView` показує масив `m_ApplyToSortingLayers`, але зараз не підсвічує, що ці integer IDs не існують у поточному `SortingLayer.layers`.

Пропозиція:

- Додати в `SceneObjectView`/`VisualSceneAudit` lighting check: якщо `Light2D.m_ApplyToSortingLayers` містить IDs, яких немає в поточних `SortingLayer.layers`, повертати warning з GameObject path, old IDs і suggested matching layer names, якщо їх можна знайти за reference scene/project mapping.
- Для переносу між двома проєктами корисним був би tool/action `RemapSortingLayerReferences`, який вміє ремапити не тільки renderer `sortingLayerID`, а й `Light2D.m_ApplyToSortingLayers`, `Renderer2DData.m_CameraSortingLayersTextureBound` та інші serialized поля, які тримають internal sorting layer IDs.

### 2026-05-30 - Перевірка нового повного SceneHierarchyExport і dry-run scene hierarchy workflow

Що робив AI:

- У проєкті `Domovyk` перевіряв оновлений UniBridge після додавання інструментів для великих сцен.
- Активна сцена: `Assets/Ed/Domovyk_Mine/Scenes/Location_0_Darkness.unity`.
- Викликав `UniBridge_SceneHierarchyExport` з `IncludeInactive=true`, `IncludeComponents=true`, `IncludeRenderers=true`, `IncludePrefabInfo=true`, `IncludeLight2D=true`, `WriteToFile=true`, `Limit=12`.
- Перевірив pagination через `Cursor=12`, `Limit=5`.
- Перевірив `UniBridge_ManageSceneHierarchy` у dry-run режимі для `CreateContainer` і `Reparent` по `objectId`.
- Перевірив `CompareExports` на одному й тому самому export-файлі.

Що сталося:

- `SceneHierarchyExport` успішно експортував повну сцену: 1450 об'єктів, файл `Library/UniBridge/SceneHierarchyExports/Location_0_Darkness_20260530_090234_799.json`, приблизно 5.1 MB.
- Файл містить потрібні для безпечної роботи поля: `objectId`, `instanceId`, `path`, `indexedPath`, `parentObjectId`, `siblingIndex`, `activeSelf`, `activeInHierarchy`, `tag`, `layer`, `transform`, `components`, `missingScripts`, renderer sorting/material/sprite data, prefab source data, Light2D `applyToSortingLayers`.
- Pagination працює очікувано: при `Cursor=12`, `Limit=5` повернуло objects з `offset=12`, `count=5`, `hasMore=true`, `nextCursor=17`.
- `ManageSceneHierarchy CreateContainer` dry-run показав `objectCountBefore=1450`, expected delta `+1`, `expectedObjectCountAfter=1451`, `willDeleteObjects=false` і список об'єктів, які будуть перенесені.
- `ManageSceneHierarchy Reparent` dry-run показав expected delta `0`, `objectCountBefore=1450`, `expectedObjectCountAfter=1450`, `willDeleteObjects=false`, коректно прийняв parent по `ParentObjectId`.
- `CompareExports` при порівнянні export-файлу із самим собою повернув `onlyInLeft=0`, `onlyInRight=0`, `changed=0`, тобто базовий diff працює.

Що вже стало суттєво краще:

- Для задачі сортування великої сцени тепер є повна картина, а не обмежений snapshot.
- Можна планувати batch reparent по `objectId`, що критично для сцен з дубльованими назвами.
- Є dry-run з перевіркою кількості об'єктів, тож AI може перед виконанням показати, що не буде видалень.
- Renderer/Light2D дані в export вже достатньо детальні для аналізу візуальних відмінностей між сценою прототипу і основною сценою.

Що ще варто підкрутити:

- У `CompareExports` duplicate section зараз дуже шумний для UI-клонів з однаковими path, наприклад `InventorySlot(Clone)`. Було б зручніше:
  - порівнювати або хоча б репортити ключі через `indexedPath`, бо він уже є у export;
  - додати `MaxDuplicateKeys` або `IncludeDuplicateKeys=false`;
  - замість довгого списку повертати summary: скільки duplicate groups, топ-N груп, кількість об'єктів у кожній.
- Параметр `ValidateObjectCountUnchanged` трохи плутає для `CreateContainer`, бо там очікуваний delta `+1`, і це нормально. Краще назвати щось на кшталт `ValidateExpectedObjectCountDelta` або лишити старий параметр, але в результаті явно пояснювати: `Reparent expects 0, CreateContainer expects +1`.
- У dry-run `CreateContainer` для moved objects `parent` показаний як `null`, бо контейнер ще не створений. Було б зручно мати окреме поле `plannedParentContainerName` / `plannedParentPath`, щоб AI міг краще пояснити людині, куди саме підуть об'єкти.
- Для великих export-файлів корисна була б компактна summary-секція прямо в MCP-відповіді: root count, inactive count, missing script count, renderer count by sorting layer, Light2D count, prefab instance count. Зараз це можна витягнути з JSON, але для швидкого рішення доводиться парсити файл окремо.
- Було б корисно мати action `SummarizeExport`, який читає export-файл і повертає компактну статистику без повторного звернення до Unity.

Висновок:

- Новий workflow вже достатньо комфортний для безпечного сортування ієрархії сцени `Location_0_Darkness`.
- Для наступного реального сортування AI може спочатку зробити full export, локально скласти план по `objectId`, потім виконати `ManageSceneHierarchy` dry-run, показати diff людині, і тільки після підтвердження виконати reparent.

### 2026-05-30 - BatchActions не прокинув DryRun=false у вкладений ManageSceneHierarchy

Що робив AI:

- У проєкті `Domovyk` організовував сцену `Assets/Ed/Domovyk_Mine/Scenes/Location_0_Darkness.unity`.
- План був: 144 root-об'єкти розкласти у 9 службових контейнерів через `UniBridge_BatchActions`, де кожен step викликав `UniBridge_ManageSceneHierarchy` з `Action=CreateContainer`.
- На зовнішньому batch було встановлено `DryRun=false`.

Що сталося:

- `UniBridge_BatchActions` повернув success, але вкладені результати `ManageSceneHierarchy` залишилися `dryRun:true`.
- Контрольний `SceneHierarchyExport` після batch показав, що сцена не змінилася: все ще 1450 об'єктів і 144 root-об'єкти.
- Після цього AI виконав ті самі операції прямими викликами `UniBridge_ManageSceneHierarchy` з явним `DryRun:false` у кожному виклику.
- Прямий шлях спрацював коректно: сцена стала 1459 об'єктів, 9 root-контейнерів, `missingScriptsTotal=0`, `willDeleteObjects=false` у кожній операції.

Пропозиція:

- У `UniBridge_BatchActions` для nested tools, які мають власний `DryRun`, варто або автоматично прокидати зовнішній `DryRun=false`, або видавати validation warning/error, якщо step tool має default `DryRun=true`, але в `parameters` не передано явний `DryRun`.
- У batch execution summary бажано підсвічувати mismatch: зовнішній batch `DryRun=false`, але конкретний nested step фактично виконався як dry-run.
- Це особливо важливо для scene hierarchy/tools, бо відповідь "success" може виглядати як реальна зміна сцени, хоча вона була лише прев'ю.

### 2026-05-31 - Stale connection JSON блокує scene-edit tools як duplicate live project

Що робив AI:

- У проєкті `Domovyk` працював зі сценою `Assets/_Domovyk/Scenes/darkness/darkness1.unity`.
- `UniBridge_ContextSnapshot` успішно підключався до активного редактора Unity і бачив проект.
- Після цього `UniBridge_ManageGameObject` для сценового об'єкта повернув помилку `Multiple live Unity projects advertise UniBridge project ID`.

Що сталося:

- UniBridge повідомив два live editor PID для одного project ID:
  - живий `pid=64552`
  - старий `pid=32056`
- Перевірка процесів Windows показала, що основний `Unity.exe` для Domovyk був тільки один: `pid=64552`.
- У `C:\Users\Cidonix\.unibridge\mcp\connections` лежали stale-файли:
  - `bridge-835bb044-32056.json`
  - `bridge-835bb044-48044.json`
  - активний `bridge-835bb044-64552.json`
- Після видалення stale JSON для dead PID `32056` і `48044` `UniBridge_ManageGameObject` одразу почав працювати коректно.

Пропозиція:

- Relay або Unity-side bridge варто навчити автоматично чистити stale connection-файли, якщо `editor_pid` вже не існує як процес.
- Якщо існує кілька JSON для одного `project_id`, але частина PID мертва, tool-call не має падати з duplicate live projects.
- У повідомленні про duplicate project бажано додати підказку з конкретним шляхом до stale connection-файлів і статусом PID: `alive/dead/unknown`.
- Добре було б мати окремий read-only/maintenance action, наприклад `UniBridge_ConnectionDiagnostics`, який показує всі connection records, їх PID status, pipe path, project id, project root, last write time і пропонує/виконує cleanup stale records.

Висновок:

- Для агента це виглядало як нестабільне підключення до сцени, хоча фактична причина була в stale discovery-файлах.
- Автоматичний stale cleanup сильно зменшить ручну діагностику після крашів, перезапусків Unity або одночасної роботи з кількома проектами.

### 2026-05-31 - ToolGuide рекламує unsupported ManageEditor action

Що робив AI:

- У проєкті `Domovyk` після зміни C# скрипта перевіряв, чи Unity перекомпілювалася без нових помилок.
- `UniBridge_ToolGuide Action=Tool Tool=editor` описав `UniBridge_ManageEditor` і в списку доступних `Action` вказав `GetCompilationDiagnostics`.
- Після цього AI спробував виконати цей action через `UniBridge_BatchActions` як step `tool=editor`.

Що сталося:

- Batch validation повернула помилку: `Unsupported Editor action 'GetCompilationDiagnostics'.`
- Тобто документація/ToolGuide і фактичний validator/dispatcher для `ManageEditor` розійшлися.

Пропозиція:

- Узгодити `UniBridge_ToolGuide` з реальним enum/validator `UniBridge_ManageEditor`.
- Або додати реальну підтримку `GetCompilationDiagnostics`, або прибрати його з опису доступних дій.
- Корисна альтернатива: окремий простий action `GetCompileStatus`, який повертає `isCompiling`, retained compiler diagnostics, і короткий summary compile errors/warnings без потреби читати всю Console.

### 2026-05-31 - ManageGameObject не може модифікувати inactive scene object, хоча SceneObjectView його бачить

Що робив AI:

- У проєкті `Domovyk` налаштовував сцену `Assets/_Domovyk/Scenes/darkness/darkness1.unity`.
- Створив сценовий handoff trigger:
  - `/AllLevel/03_Intro_Cutscene_Leaf/DarknessLevel_Root/IntroSetup/NativeDomovykHandoffTriggerProxy`
- Після створення об'єкт був переведений у `activeSelf=false`, бо Ed `DarknessIntroManager` має активувати його тільки в момент handoff.

Що сталося:

- `UniBridge_UnitySearch` і `UniBridge_SceneObjectView` коректно знаходили inactive-об'єкт і повертали його path/objectId.
- `UniBridge_ManageGameObject Action=Modify` не знаходив той самий inactive-об'єкт ані через `SearchMethod=ByPath`, ані через `ByName`, ані через `ById`.
- У параметри передавався `IncludeInactive=true`, але для `Modify` це не допомогло.

Приклад об'єкта:

- Path: `AllLevel/03_Intro_Cutscene_Leaf/DarknessLevel_Root/IntroSetup/NativeDomovykHandoffTriggerProxy`
- `SceneObjectView` бачив його як inactive, але `ManageGameObject Modify` повертав `Target GameObject not found`.

Пропозиція:

- Уніфікувати resolver scene objects між `SceneObjectView`, `UnitySearch` і `ManageGameObject`.
- `ManageGameObject` для `Modify`, `SetComponentProperty`, `AddComponent`, `RemoveComponent` має стабільно знаходити inactive scene objects, якщо передано `IncludeInactive=true`.
- Якщо конкретна дія не підтримує inactive lookup, validation має попереджати про це явно, а не проходити dry-run як успішний.
- Корисно також додати в `ManageGameObject` відповідь `candidateFoundButInactive=true`, якщо search без inactive не знайшов, але inactive candidate існує.

Додаткове спостереження:

- В impact-блоках `UniBridge_BatchActions` для відкритої сцени іноді показувалось `Assets/_Domovyk/Scenes/darkness/darkness1.unity exists=false`, хоча сцена була завантажена, активна і потім успішно збережена через `UniBridge_ManageScene Save`.
- Варто перевірити normalizer шляху в impact/rollback diagnostics для Unity asset paths.

### 2026-05-31 - Перевірка виправлення inactive lookup у ManageGameObject

Що робив AI:

- У проєкті `Domovyk` працював зі сценою `Assets/_Domovyk/Scenes/darkness/darkness1.unity`.
- Потрібно було змінити inactive scene object:
  - `AllLevel/03_Intro_Cutscene_Leaf/DarknessLevel_Root/IntroSetup/NativeDomovykHandoffTriggerProxy`
- Використав `UniBridge_BatchActions` зі step `tool=game_object`, `Action=Modify`, `SearchMethod=ByPath`, `SearchInactive=true`, `SetActive=false`.

Що побачив:

- На версії UniBridge `0.2.4` inactive object знайшовся і був змінений коректно.
- Позиція об'єкта після операції стала правильною:
  - `worldPosition = (-1369.247, -797.116, 0)`
  - `localPosition = (-0.407, -0.186, 0)`
- Тобто проблема з `ManageGameObject Modify` для inactive object, яку я фіксував раніше у фідбеку, виглядає виправленою.

Невелике додаткове спостереження:

- Після batch, де останнім кроком був `UniBridge_ManageScene Save`, короткий `UniBridge_ContextSnapshot` все ще показав `hasDirtyScenes=true`.
- Повторний прямий виклик `UniBridge_ManageScene Save` для цієї ж сцени прибрав dirty state (`hasDirtyScenes=false`).
- Варто перевірити, чи batch save іноді завершується до повного оновлення dirty state в Editor, або чи якийсь post-save refresh одразу повторно маркує сцену dirty.

### 2026-06-02 - ScriptApplyEdits застосував зміну при Preview=true

Що робив AI:

- У проєкті `Domovyk` виправляв C# метод `Depenetrate()` у `Assets/_Domovyk/Scripts/MoonCharacterController.cs`.
- Передав у `UniBridge_ScriptApplyEdits`:
  - `Preview=true`
  - `PreconditionSha256=...`
  - structured edit `replace_method`

Що сталося:

- Інструмент повернув `success=true`, але повідомлення було `Applied 1 structured edit(s)`.
- Файл справді змінився, тобто `Preview=true` не спрацював як dry-run.

Чому це важливо:

- Для агента `Preview=true` означає безпечну перевірку перед реальною зміною.
- Якщо preview фактично застосовує патч, це може призвести до небажаних змін, особливо коли агент перевіряє складний structured edit перед виконанням.

Пропозиція:

- Для `UniBridge_ScriptApplyEdits` зробити `Preview=true` строгим dry-run:
  - не змінювати файл;
  - не schedule refresh;
  - повертати preview diff / planned edit summary / validation result.
- Якщо preview не підтримується, tool має явно повертати validation error або warning на рівні schema/result, а не застосовувати зміну мовчки.
