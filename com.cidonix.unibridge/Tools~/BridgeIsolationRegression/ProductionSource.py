"""Extract unchanged production bodies; record every input and every qualified seam."""
import hashlib
import re
from pathlib import Path


def block(source, pattern):
    matches = list(re.finditer(pattern, source, re.M))
    if len(matches) != 1:
        raise RuntimeError(f'Expected one unchanged source block: {pattern}; found {len(matches)}')
    start = matches[0].start()
    pos = source.index('{', start)
    depth, mode = 0, 'code'
    while pos < len(source):
        char, pair = source[pos], source[pos:pos + 2]
        if mode == 'line':
            if char == '\n': mode = 'code'
        elif mode == 'comment':
            if pair == '*/': mode = 'code'; pos += 1
        elif mode in ('string', 'char'):
            if char == '\\': pos += 1
            elif char == ('"' if mode == 'string' else "'"): mode = 'code'
        elif mode == 'verbatim':
            if pair == '""': pos += 1
            elif char == '"': mode = 'code'
        elif pair in ('//', '/*'):
            mode = 'line' if pair == '//' else 'comment'; pos += 1
        elif pair == '@"': mode = 'verbatim'; pos += 1
        elif char in ('"', "'"): mode = 'string' if char == '"' else 'char'
        elif char == '{': depth += 1
        elif char == '}':
            depth -= 1
            if depth == 0: return source[start:pos + 1]
        pos += 1
    raise RuntimeError('Unbalanced source block')


def model(source, name):
    return block(source, r'^[ \t]*(?:public[ \t]+)?(?:sealed[ \t]+)?class[ \t]+' + re.escape(name) + r'\s*\{')


def method(source, name):
    if name == 'Bridge':
        head = r'^[ \t]*public[ \t]+'
    else:
        head = r'^[ \t]*(?:(?:public|private|internal|protected|static|async|sealed|override)[ \t]+)*[^ \t\n;{}=()]+[ \t]+'
    return block(source, head + re.escape(name) + r'\s*\([^;{}]*\)\s*\{')


def build(repo, work):
    package = repo / 'com.cidonix.unibridge'
    editor = package / 'Modules/Cidonix.UniBridge.MCP.Editor'
    provenance = {'wholeProductionFiles': [], 'sources': {}, 'extraction': {}, 'limitations': [
        'Lifecycle: unchanged production methods with deterministic owned listener/scheduler/discovery substrate; no native named-pipe handle acquisition.',
        'Persisted models and producers: unchanged declarations and SaveState/SaveWriteToken/SaveSnapshot bodies, owned storage-path adapters only.',
        'Runtime converter: unchanged UnityObjectId and UnityEngineObjectConverter with deterministic typed ID lookup substrate.',
    ]}

    def read(relative):
        path = package / relative
        raw = path.read_bytes()
        provenance['sources'][relative] = hashlib.sha256(raw).hexdigest()
        return raw.decode('utf-8-sig')

    def whole(relative):
        content = read(relative)
        (work / Path(relative).name).write_text(content, encoding='utf-8')
        provenance['wholeProductionFiles'].append(relative)

    for relative in (
        'Modules/Cidonix.UniBridge.MCP.Editor/Helpers/McpJson.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Models/Command.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/McpToolInfo.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Connection/MessageProtocol.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Connection/IConnectionTransport.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Connection/IConnectionListener.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Connection/ServerDiscovery.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Connection/TransportStore.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/IToolHandler.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/McpAttributes.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/IUnityMcpTool.cs',
        'Modules/Cidonix.UniBridge.Tracing/TraceJson.cs',
        'Modules/Cidonix.UniBridge.Tracing/TraceTypes.cs',
        'Modules/Cidonix.UniBridge.Tracing/TraceWriter.cs',
        'Modules/Cidonix.UniBridge.Tracing/TraceSinks.cs',
        'Modules/Cidonix.UniBridge.Tracing/TraceSinkConfigManager.cs',
        'Modules/Cidonix.UniBridge.Tracing/TraceConfigFileWriter.cs',
    ):
        whole(relative)

    bridge = read('Modules/Cidonix.UniBridge.MCP.Editor/Bridge.cs')
    names = ['Bridge', 'Start', 'Stop', 'Dispose', 'OnPlayModeStateChanged', 'CancelScheduledStart', 'ScheduleInitRetry', 'EnsureStartedOnEditorIdle',
             'InitializeAfterCompilation', 'OnBeforeAssemblyReload', 'OnAfterAssemblyReload',
             'OnToolsChanged', 'ComputeToolsSnapshotAndHash', 'IsCompiling', 'IsCurrentGeneration', 'RunForGeneration',
             'TryQueueCommand', 'CloseAndDisposeTransport', 'ListenerLoopAsync', 'ProcessCommands',
             'InjectRequestId', 'WaitForExistingAndComplete', 'CompleteQueuedCommandAsync',
             'WriteWithLockAsync', 'CreateTransportCancellation', 'DisposeWriteResourcesWhenIdle']
    bodies = [model(bridge, 'BridgeGeneration')] + [method(bridge, name) for name in names]
    provenance['extraction']['Bridge'] = names
    provenance['extraction']['BridgeModels'] = ['BridgeGeneration']
    provenance['limitations'].append('Task.Run, cancellation sources, linked registrations and semaphore are real .NET. Later discovery failure qualifies already-created gated worker rollback. Runtime allocation/OOM failures are not injected.')
    (work / 'BridgeProduction.cs').write_text('''using System; using System.Collections.Concurrent; using System.Collections.Generic; using System.Linq;
using System.Text; using System.Threading; using System.Threading.Tasks; using Newtonsoft.Json; using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Models; using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Helpers; using Cidonix.UniBridge.MCP.Editor.Connection;
namespace Cidonix.UniBridge.BridgeIsolationRegression { partial class Bridge {
''' + '\n'.join(bodies) + '\n}}', encoding='utf-8')

    session = read('Modules/Cidonix.UniBridge.MCP.Editor/Tools/WorkSession.cs')
    ownership = read('Modules/Cidonix.UniBridge.MCP.Editor/Tools/WorkSession.Ownership.cs')
    safety = read('Modules/Cidonix.UniBridge.MCP.Editor/Tools/WorkSession.RestoreSafety.cs')
    semantics = read('Modules/Cidonix.UniBridge.MCP.Editor/Tools/WorkSession.Semantics.cs')
    snapshot = read('Modules/Cidonix.UniBridge.MCP.Editor/Tools/EditorSnapshotTool.cs')
    parts = [model(session, name) for name in ('ScanOptions', 'SessionState', 'SessionBaseline', 'FileSnapshot')]
    parts += [model(ownership, name) for name in ('WriteTrackingToken', 'WriteFileState', 'OwnedWriteReceipt')]
    parts += [model(safety, 'RevertPreview')]
    parts += [model(semantics, name) for name in ('SessionSemanticBaseline', 'SceneSemanticCollection', 'SemanticSceneSnapshot', 'SemanticObjectSnapshot', 'SemanticRendererSnapshot', 'SemanticPrefabSnapshot')]
    parts += [method(session, name) for name in ('GetString', 'GetBool', 'GetInt', 'GetLong', 'SaveState')]
    parts += [method(ownership, 'SaveWriteToken')]
    (work / 'SessionProduction.cs').write_text('using System; using System.IO; using System.Collections.Generic; using Newtonsoft.Json; using Newtonsoft.Json.Linq; using Cidonix.UniBridge.MCP.Editor.Helpers;\nnamespace Golden { static partial class SessionProduction {\n' + '\n'.join(parts) + '\n}}', encoding='utf-8')
    snapshot_names = ('EditorSnapshotData', 'ProjectData', 'ScenesData', 'SceneData', 'SceneViewData', 'SelectionData', 'ObjectRefData', 'ActiveToolData', 'PrefabStageData', 'PrefabAutoSaveData', 'WindowData', 'DockTabData', 'Vector3Data', 'QuaternionData', 'RectData')
    parts = [model(snapshot, name) for name in snapshot_names]
    parts += [block(snapshot, r'^[ \t]*static readonly JsonSerializerSettings JsonSettings = new\(\)\s*\{') + ';', method(snapshot, 'SaveSnapshot')]
    (work / 'SnapshotProduction.cs').write_text('using System; using System.IO; using Newtonsoft.Json; using Cidonix.UniBridge.MCP.Editor.Helpers;\nnamespace Golden { static partial class SnapshotProduction {\n' + '\n'.join(parts) + '\n}}', encoding='utf-8')
    discovery = read('Modules/Cidonix.UniBridge.MCP.Editor/Connection/ServerDiscovery.cs')
    (work / 'DiscoveryModelProduction.cs').write_text('namespace Golden { static partial class DiscoveryProduction {\n' + model(discovery, 'ConnectionInfo') + '\n}}', encoding='utf-8')
    converter = read('Modules/Cidonix.UniBridge.MCP.Runtime/Serialization/UnityTypeConverters.cs')
    parts = [block(converter, r'^\s*static class UnityObjectId\s*\{'), block(converter, r'^\s*class UnityEngineObjectConverter\s*:[^{]+\{')]
    (work / 'UnityObjectConverterProduction.cs').write_text('using System; using Newtonsoft.Json; using Newtonsoft.Json.Linq; using UnityEngine; using UnityEditor;\nnamespace Cidonix.UniBridge.MCP.Runtime.Serialization {\n' + '\n'.join(parts) + '\n}', encoding='utf-8')
    return provenance
