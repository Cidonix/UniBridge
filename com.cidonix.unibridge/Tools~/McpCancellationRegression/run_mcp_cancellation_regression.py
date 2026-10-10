"""Compile the current scheduler and exact Bridge cancellation methods; no Unity project writes."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid
from xml.sax.saxutils import escape


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--newtonsoft', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        raise SystemExit('Administrator Windows shell required.')
    repo = Path(__file__).resolve().parents[3]
    report = args.report.resolve()
    if report.exists():
        raise SystemExit('Report exists; preserve evidence by choosing a fresh report.')
    report.parent.mkdir(parents=True, exist_ok=True)
    work = report.parent / ('cancellation-build-' + uuid.uuid4().hex)
    work.mkdir()
    sys.path.insert(0, str(repo / 'com.cidonix.unibridge/Tools~/BridgeIsolationRegression'))
    from ProductionSource import method, model
    editor = repo / 'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor'
    scheduler = editor / 'ToolRegistry/ToolExecutionScheduler.cs'
    bridge = editor / 'Bridge.cs'
    registry = editor / 'ToolRegistry/McpToolRegistry.cs'
    output_provider = editor / 'ToolRegistry/OutputContractProvider.cs'
    output_class = model(output_provider.read_text(encoding='utf-8-sig'), 'OutputContract')
    (work / 'OutputContract.cs').write_text('using System; using System.Collections.Generic; using Newtonsoft.Json.Linq; namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry {\n' + output_class + '\n}', encoding='utf-8')
    contract_sources = [editor / path for path in (
        'ToolRegistry/EditorObservationActionOutputRules.cs', 'ToolRegistry/ActionOutputSchema.cs',
        'ToolRegistry/ActionOutputValidation.cs', 'Tools/ExecutionStatus.cs', 'Helpers/Response.cs', 'Helpers/McpJson.cs')]
    for path in contract_sources:
        (work / path.name).write_bytes(path.read_bytes())
    bridge_text = bridge.read_text(encoding='utf-8-sig')
    source = '''using System; using System.Collections.Concurrent; using System.Threading;
namespace Cidonix.UniBridge.MCP.Editor {
internal partial class Bridge {
readonly ConcurrentDictionary<string, (IConnectionTransport client, string requestId, CancellationTokenSource source)> requestCancellations = new();
'''
    for name in ('CancelRequest', 'ReleaseRequestCancellation'):
        source += method(bridge_text, name) + '\n'
    source += '''public void Add(string id, IConnectionTransport client, string requestId, CancellationTokenSource source) => requestCancellations[id] = (client, requestId, source);
public void Cancel(IConnectionTransport client, string requestId) => CancelRequest(client, requestId);
public void Release(string id) => ReleaseRequestCancellation(id);
public int Count => requestCancellations.Count;
}}
'''
    (work / 'BridgeCancellation.cs').write_text(source, encoding='utf-8')
    registry_methods = '\n'.join(method(registry.read_text(encoding='utf-8-sig'), name) for name in ('ExecuteToolAsync', 'ExecuteToolCoreAsync'))
    (work / 'RegistryConsumer.cs').write_text('using System; using System.Collections.Generic; using System.Linq; using System.Reflection; using System.Threading; using System.Threading.Tasks; using Newtonsoft.Json.Linq; using Cidonix.UniBridge.MCP.Editor.Helpers; namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry { internal static partial class McpToolRegistry { ' + registry_methods + ' }}', encoding='utf-8')
    (work / 'ToolExecutionScheduler.cs').write_bytes(scheduler.read_bytes())
    (work / 'Regression.cs').write_bytes(Path(__file__).with_name('Regression.cs').read_bytes())
    project = work / 'Cancellation.csproj'
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>' + escape(str(args.newtonsoft.resolve())) + '</HintPath></Reference></ItemGroup></Project>', encoding='utf-8')
    (work / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding='utf-8')
    provenance = {'administrator': True, 'nativeEditor': False,
                  'wholeProductionFiles': [str(scheduler)] + [str(path) for path in contract_sources],
                  'exactProductionMethods': ['Bridge.CancelRequest', 'Bridge.ReleaseRequestCancellation', 'McpToolRegistry.ExecuteToolAsync', 'McpToolRegistry.ExecuteToolCoreAsync'],
                  'exactProductionModels': ['OutputContract'],
                  'sourceSha256': {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in (scheduler, bridge, registry, output_provider, *contract_sources)},
                  'substrate': 'Deterministic tool handlers, no-op log, Task.Delay editor yield, and transport identity. WorkSession/policy catalog and non-execution-status schema branches are excluded.'}
    provenance_path = work / 'provenance.json'
    provenance_path.write_text(json.dumps(provenance, indent=2), encoding='utf-8')
    run = subprocess.run(['dotnet', 'run', '--project', str(project), '--configuration', 'Release', '--', str(report), str(provenance_path)], capture_output=True, text=True)
    report.with_suffix('.build.log').write_text(run.stdout + run.stderr, encoding='utf-8')
    print(run.stdout + run.stderr)
    return run.returncode


if __name__ == '__main__':
    raise SystemExit(main())
