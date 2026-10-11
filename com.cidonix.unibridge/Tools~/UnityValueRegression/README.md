# Unity mathematical value regression

Run from an Administrator Windows shell. Both runners retain earlier reports and
record exact candidate source hashes. `--candidate` is an optional mirrored source
root; omit it after promoting the candidate to the repository.

```powershell
python -B .\com.cidonix.unibridge\Tools~\UnityValueRegression\run_unity_value_regression.py --repo H:/Repos/Cidonix/UniBridge --newtonsoft <Unity-Newtonsoft.Json.dll> --unity-core <UnityEngine.CoreModule.dll> --report <new-report.json>
python -B .\com.cidonix.unibridge\Tools~\UnityValueRegression\run_legacy_value_sdk_regression.py --repo H:/Repos/Cidonix/UniBridge --newtonsoft <Newtonsoft-net20.dll> --report <new-legacy-report.json>
```

The first runner uses real Unity SDK mathematical types in a standalone .NET10
process. It links the whole candidate math helper and McpJson, plus the unchanged
candidate reflection conversion/set/read/compare method bodies. Object-reference,
nested setter and logging adapters are declared in provenance and are not covered
by this suite. Matrix orientation uses sixteen different values. Negative cases
prove malformed/nonfinite inputs do not change the target property. The suite
also checks caller converter precedence, poison global factories and concurrent
serializers. Its result does not prove physical Editor or Camera behavior.

The second runner compiles unchanged math helpers and converters with Roslyn
C#7.3 and .NET2 references against five real legacy Unity SDKs. This is neither a
full legacy-adapter compile nor a native runtime result. It launches no Editor.

The first runner also links the whole production ActionOutputSchema builder with
the actual McpJson math converter. Plain and explicitly shaped nested DTOs remain
closed; applicable Unity/custom converters and dynamic object fields still require
explicit producer schemas. Global serializer factories remain ignored.

Native qualification must separately exercise `Camera.projectionMatrix` and the
nonserialized properties in the owned `UnityValuePropertyProbe` template through
ordinary MCP calls. Verify physical values, `applied/readbackVerified`, canonical
output, and rejection without a property change. An independent native legacy
gate covers the exact converters. Import the template only into the approved test
project with ownership/hash guards, wait for actual readiness after reload, and
remove only the runner's unchanged fixture and metadata. Do not save author scenes.
