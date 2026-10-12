# CoreCLR Editor qualification

UniBridge 0.2.88 qualifies the tested Windows x64 Unity **7000.0.0a8** Editor
with its bundled **.NET 10.0.10-dev / System.Private.CoreLib** runtime.
Point 12 is complete for that concrete Editor/runtime. Other Unity 7 revisions,
operating systems and Player builds require their own native qualification.

## Confirmed correction

CreateScript, UpdateScript and structured EditScript now pass their known full
source path to syntax/semantic validation. With USE_ROSLYN, a valid structured
Preview previously failed because the owning Unity assembly and its references
were missing. The corrected Preview uses that assembly, sibling sources and
compiler references; a semantic error returns CS0029 with its location.
Preview success and failure both leave source bytes unchanged.

## Native gates

| Gate | Confirmed behavior |
| --- | --- |
| Loaded assembly catalog: 6 cases | Missing identity never loads an assembly; runtime byte loads invalidate misses; short-name collisions are ambiguous and full identities resolve; a real missing dependency retains valid partial types; Reflection.Emit types remain inspectable without an invented physical metadata path; semantic references use the owning Unity target. |
| Collectible ALC | Two identical assembly identities in different contexts stay distinct. Index count changes 0 -> 2 -> 1 -> 0. Both contexts, assemblies and types physically collect: all six final weak references are false. Unload needs no hidden cache clearing or code reload. |
| Native Roslyn: 6 cases | Complete owning context, three sibling sources and 313 captured compiler references; valid strict validation; proposed CS0029; CS0618 versus advisory policy; runtime-only byte loads excluded from compiler references; alias-only binding; immutable PE metadata updates 41 -> 42 despite unchanged identity/path/length/timestamp. |
| Strict structured Preview | Valid proposed method succeeds without writing; invalid string-to-int method fails with CS0029; all owned source/meta bytes remain exact. |
| Normal Play lifecycle | One entry and exit with domain reload; reconnect/readiness and public schema reads succeed, with native assembly/context identity changing. |
| Disabled domain reload | One entry and exit with DisableDomainReload; bridge assembly/context identity remains unchanged, and public schema reads succeed. |
| MCP cancellation | Published admitted-request cancellation, client EOF/disconnect and event-wait cancellation probes prove released slots and subsequent successful reads; every short-lived relay exits. |

Registered MCP output schemas and raw structured/text mirrors are checked on
the native public routes, including expected negative results. Qualified managed
types can be inspected explicitly; ListTypes Kind=Any intentionally enumerates
Unity-relevant types rather than every arbitrary managed class.

Temporary USE_ROSLYN and Play options are restored through typed Unity APIs.
Original scenes and project configuration are not saved by the tests. Logical
scene/root/component state is compared across Play transitions; transient IDs
may change when Unity recreates the scene. Owned fixtures and their exact GUID
metadata are quarantined, then normal import/reload and bridge health are checked.

The 296-source Editor module also emits with zero errors against installed Unity
6.6 SDK references in Windows, Windows-Roslyn, Linux and OSX symbol profiles.
Those cross-platform emissions are compile checks; native CoreCLR evidence is
Windows x64 only. The installed Unity 6.6 Mono test project has a separate native
package health and author-state preservation gate.

## Evidence and repeat qualification

Local immutable native reports and failure evidence are retained under
`Temp/CoreClrPoint12-20261012`, with the earlier connectivity/cancellation
qualification under `Temp/Unity7CoreClrAlpha8-20261012`. The final receipt records
hashes, restoration/cleanup, actual release delivery and publication.

For another Editor revision, use fresh owned fixture identities and baselines;
run catalog, collectible unload, owning-context Roslyn and both Play profiles
in that actual Editor. Admit the exact project/runtime and package before each
phase. Do not replay an issued mutation across a lost reply or reload; reconnect
and observe its state. Retain original failure reports and distinguish fixture
expectation errors from confirmed product defects. Restore only journaled
test-owned settings/files and verify original author hashes at the end.
