"""Opt-in real Mono Editor scalar precision proof; never launches/restarts Editor.

reproduce mode records actual current defects and exits 0 only for a conclusive
reproduction with cleanup. It deliberately retains passed=false/qualification=false.
qualify mode requires exact wire reads, native write effects and rejected negatives.
"""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import struct
import time

MENU = "Tools/UniBridge Validation/Serialized Numeric/"
SCALARS = ["IntValue", "UIntValue", "LongValue", "ULongValue", "FloatValue", "DoubleValue"]


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def values_in_payload(value):
    """Read only actual canonical serialized rows; no text guessing/reflection fallback."""
    output = {}
    def visit(item):
        if isinstance(item, dict):
            path = item.get("path", item.get("memberPath"))
            if isinstance(path, str) and "value" in item and not isinstance(item["value"], (dict, list)):
                output[path] = item["value"]
            for child in item.values():
                visit(child)
        elif isinstance(item, list):
            for child in item:
                visit(child)
    visit(value)
    return output


def exact(expected, actual):
    if expected["kind"] in ("Int32", "UInt32", "Int64", "UInt64"):
        return type(actual) is int and actual == int(expected["value"])
    if type(actual) not in (int, float):
        return False
    try:
        format = ">f" if expected["kind"] == "Single" else ">d"
        return struct.pack(format, actual) == struct.pack(format, float(expected["value"]))
    except (OverflowError, struct.error):
        return False


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--mode", choices=["reproduce", "qualify"], required=True)
    p.add_argument("--project", type=Path, required=True)
    p.add_argument("--relay", type=Path, required=True)
    p.add_argument("--smoke-module", type=Path, required=True)
    p.add_argument("--validator-path", type=Path)
    p.add_argument("--author-baseline", type=Path, required=True)
    p.add_argument("--report", type=Path, required=True)
    a = p.parse_args()
    assert os.name == "nt" and ctypes.windll.shell32.IsUserAnAdmin(), "Administrator Windows shell required before relay launch."
    a.project = a.project.resolve(strict=True)
    assert not a.report.exists(), "Choose a fresh immutable report."
    if a.validator_path:
        sys.path.insert(0, str(a.validator_path.resolve(strict=True)))
    from jsonschema import Draft202012Validator
    spec = importlib.util.spec_from_file_location("numeric_smoke", a.smoke_module)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    class ModernClient(module.McpClient):
        def initialize(self):
            r = self.send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "UniBridge Serialized Numeric Regression", "version": "1.0"}})
            assert r.get("result", {}).get("protocolVersion") == "2025-11-25"
            self.send("notifications/initialized", {}, expect_response=False)
    root = a.project / "Library/AgentValidation/SerializedNumericRegression"
    pointer = root / "active.json"
    assert not pointer.exists(), "Existing fixture pointer must be preserved and inspected, never replaced."
    baseline = json.loads(a.author_baseline.read_text(encoding="utf-8-sig"))
    report = {"passed": False, "qualification": False, "mode": a.mode, "calls": [], "cases": [],
        "defects": [], "administrator": True, "editorLaunched": False, "protocol": "2025-11-25", "noMutationReplay": True}
    prepared = False
    journal = None
    request_ordinal = 0
    with ModernClient(a.relay, a.project, 120) as client:
        schemas = {t["name"]: Draft202012Validator(t["outputSchema"]) for t in client.list_tools() if "outputSchema" in t}
        def call(label, name, arguments, negative=False):
            r = client.call_tool(name, arguments, allow_error=True)
            report["calls"].append({"label": label, "name": name, "arguments": arguments, "reply": r})
            body = dict(r["data"])
            mirror = body.pop("structuredContent", None)
            structured = r["raw"].get("structuredContent")
            assert structured == body and (mirror is None or mirror == structured), label + ": exact text/machine mirror mismatch"
            assert name in schemas, "Registered output schema missing: " + name
            errors = list(schemas[name].iter_errors(structured))
            assert not errors, label + ": registered output schema rejected reply: " + "; ".join(e.message for e in errors[:4])
            if negative:
                assert r["raw"].get("isError") is True and body.get("success") is False, label + ": expected truthful error"
            else:
                assert r["raw"].get("isError") is not True and body.get("success") is True, label + ": actual tool error"
            return body
        def case(name, passed, detail=None):
            report["cases"].append({"name": name, "passed": bool(passed), "detail": detail})
            assert passed, name
        def menu(action, pattern):
            previous = set(journal.glob(pattern)) if journal else set()
            call("fixture " + action, "UniBridge_ManageMenuItem", {"Action": "Execute", "MenuPath": MENU + action})
            until = time.monotonic() + 30
            while time.monotonic() < until:
                if action == "Prepare" and pointer.exists():
                    pointer_body = json.loads(pointer.read_text(encoding="utf-8-sig"))
                    own = Path(pointer_body["journal"])
                    if (own / pattern).exists():
                        return own, json.loads((own / pattern).read_text(encoding="utf-8-sig"))
                elif journal:
                    fresh = set(journal.glob(pattern)) - previous
                    if len(fresh) == 1:
                        return journal, json.loads(next(iter(fresh)).read_text(encoding="utf-8-sig"))
                time.sleep(.1)
            raise TimeoutError("Owned native fixture phase unavailable: " + action)
        def observe():
            _, native = menu("Observe", "observed-*.json")
            assert native["sceneRetained"] and native["noFixtureSelectionWrites"] and native["globalSaveAssetsNeverCalledByFixture"]
            return native
        def read_component(native, label):
            body = call(label, "UniBridge_RuntimeStateProbe", {"Action": "Snapshot", "Target": native["gameObjectId"], "SearchMethod": "ById",
                "Component": native["componentType"], "Members": list(native["componentValues"]), "RequirePlayMode": False,
                "IncludeSerializedFields": True, "IncludeReadableMembers": False, "SaveToFile": False,
                "MaxTargets": 1, "MaxComponents": 1, "MaxMembers": 80, "MaxCollectionItems": 16})
            components = body["data"]["sample"]["targets"][0]["components"]
            assert len(components) == 1 and all(v["source"] == "serializedProperty" and v["error"] is None for v in components[0]["values"])
            return values_in_payload(components[0]["values"])
        def read_asset(native, label):
            body = call(label, "UniBridge_ManageScriptableObject", {"Action": "Inspect", "Guid": native["assetGuid"],
                "IncludeSerializedProperties": True, "MaxSerializedProperties": 100})
            return values_in_payload(body["data"]["scriptableObject"]["serializedProperties"]["properties"])
        def compare(label, expected, actual, record_defects=False):
            case(label + " all native field paths present", set(expected).issubset(actual),
                {"missingPaths": sorted(set(expected) - set(actual))})
            for path, wanted in expected.items():
                match = path in actual and exact(wanted, actual[path])
                if record_defects:
                    if not match:
                        report["defects"].append({"producer": label, "path": path, "native": wanted, "wire": actual.get(path), "present": path in actual})
                else:
                    case(label + " exact " + path, match, {"native": wanted, "wire": actual.get(path)})
        def component_write(native, label, props, negative=False):
            return call(label, "UniBridge_ManageGameObject", {"Action": "SetComponentProperty", "Target": native["gameObjectId"],
                "SearchMethod": "ById", "ComponentName": native["componentType"], "ComponentProperties": props}, negative)
        def asset_write(native, label, props, negative=False):
            nonlocal request_ordinal
            # Actual product conversion/validation over MCP, explicitly without global SaveAssets.
            dry_run = call(label + " (production MCP dry run)", "UniBridge_ManageScriptableObject", {"Action": "SetProperties", "Guid": native["assetGuid"],
                "Properties": props, "DryRun": True, "Select": False}, negative)
            if not negative:
                assert dry_run["data"]["dryRun"] is True
            request_ordinal += 1
            filename = "apply-request-" + format(request_ordinal, "04d") + ".json"
            request = {"run": native["run"], "ordinal": request_ordinal, "targetGuid": native["assetGuid"], "properties": props}
            request_path = journal / filename
            with request_path.open("x", encoding="utf-8") as stream:
                json.dump(request, stream, ensure_ascii=False, allow_nan=False)
            request_hash = sha(request_path)
            _, physical = menu("Apply Owned Asset Request", "apply-result-" + format(request_ordinal, "04d") + ".json")
            report.setdefault("ownedAssetApplications", []).append(physical)
            assert physical["requestSha256"] == request_hash and sha(request_path) == request_hash
            assert physical["run"] == native["run"] and physical["targetGuid"] == native["assetGuid"] and physical["ordinal"] == request_ordinal
            assert physical["globalSaveAssetsCalled"] is False
            assert physical["success"] is (not negative), label + ": actual shared native writer outcome differs"
            if not negative:
                assert physical["savedOnlyOwnedAsset"] is True
            report["scriptableObjectWriterConfidence"] = {"ordinaryMcpSetPropertiesDryRun": True,
                "actualSharedProductionPatcherNativeApplied": True, "targetedOwnedSaveAssetIfDirty": True,
                "fullSavedScriptableObjectMcpMutationExecuted": False, "globalSaveAssetsAvoided": True}
            return dry_run
        try:
            ping = call("project identity", "UniBridge_Discover", {"Action": "Ping"})
            case("Exact project root", Path(ping["data"]["unity"]["projectPath"]).resolve() == a.project)
            ready = call("actual Editor readiness", "UniBridge_ManageEditor", {"Action": "WaitForReadyAfterReload", "TimeoutMs": 60000, "RequireNotPlaying": True})
            case("Actual ready and healthy", ready["data"]["waitResult"]["data"]["readiness"]["isReady"] and ready["data"]["compileHealth"]["healthy"])
            prepared = True  # Own cleanup is required even if preparation reply/schema fails after native work.
            journal, native = menu("Prepare", "prepared.json")
            report["journal"] = str(journal); report["nativeInitial"] = native
            case("Actual x64 Mono Unity6.6", native["processBits"] == 64 and native["monoRuntime"] and native["unityVersion"].startswith("6000.6."))
            compare("Component serialized read", native["componentValues"], read_component(native, "initial Component read"), a.mode == "reproduce")
            compare("ScriptableObject serialized read", native["assetValues"], read_asset(native, "initial ScriptableObject read"), a.mode == "reproduce")
            if a.mode == "reproduce":
                for producer in ("Component serialized read", "ScriptableObject serialized read"):
                    paths = {d["path"] for d in report["defects"] if d["producer"] == producer}
                    case(producer + " confirms native scalar truncation", {"UIntValue", "LongValue", "ULongValue", "DoubleValue"}.issubset(paths))
                requested_double = 1.0000000000000004
                for owner, writer in (("component", component_write), ("asset", asset_write)):
                    writer(native, owner + " physical Double narrowing witness", {"DoubleValue": requested_double})
                    native = observe()
                    actual = float(native[owner + "Values"]["DoubleValue"]["value"])
                    case(owner + " proves Double writer narrowed to native 1.0", actual == 1.0 and actual != requested_double)
                    report["defects"].append({"producer": owner + " production writer", "path": "DoubleValue",
                        "requested": requested_double, "actualNative": native[owner + "Values"]["DoubleValue"], "returnedSuccess": True})
                    before = native[owner + "Values"]
                    writer(native, owner + " incorrectly rejects valid Int64 witness", {"LongValue": 9007199254740995}, True)
                    native = observe()
                    case(owner + " valid Int64 rejected without native mutation", native[owner + "Values"] == before)
                    report["defects"].append({"producer": owner + " production writer", "path": "LongValue",
                        "requested": 9007199254740995, "returnedErrorForValidTargetType": True, "allNativeValuesUnchanged": True})
            else:
                props = {"IntValue": 2147483647, "UIntValue": 4294967295, "LongValue": 9223372036854775806,
                    "ULongValue": 18446744073709551614, "FloatValue": 1.2345679, "DoubleValue": 1.2345678901234567,
                    "Nested.LongValue": -9007199254740993, "Nested.ULongValue": 9223372036854775808,
                    "Nested.DoubleValue": 1.0000000000000004, "Blocks.Array.data[0].LongValue": 9007199254740995,
                    "Blocks.Array.data[0].ULongValue": 18446744073709551613,
                    "Blocks.Array.data[0].DoubleValue": 1.0000000000000004,
                    "Longs.Array.data[1]": -9223372036854775807,
                    "ULongs.Array.data[1]": 18446744073709551612,
                    "Doubles.Array.data[1]": 1.2345678901234565}
                component_write(native, "exact Component scalar/nested/array write", props)
                native = observe()
                for path, wanted in props.items():
                    case("Component physical write " + path, exact(native["componentValues"][path], wanted))
                compare("Component post-write read", native["componentValues"], read_component(native, "post-write Component read"))
                asset_write(native, "exact ScriptableObject scalar/nested/array write", props)
                native = observe()
                for path, wanted in props.items():
                    case("ScriptableObject physical write " + path, exact(native["assetValues"][path], wanted))
                compare("ScriptableObject post-write read", native["assetValues"], read_asset(native, "post-write ScriptableObject read"))
                negatives = {"IntValue": 2147483648, "UIntValue": -1, "LongValue": 9223372036854775808,
                    "ULongValue": -1, "DoubleValue": "1e309"}
                for owner, writer in (("component", component_write), ("asset", asset_write)):
                    for path, invalid in negatives.items():
                        before = native[owner + "Values"]
                        writer(native, owner + " rejects " + path, {path: invalid}, True)
                        native = observe()
                        case(owner + " invalid " + path + " preserves all native values", native[owner + "Values"] == before)
        except Exception as error:
            report["error"] = type(error).__name__ + ": " + str(error)[:1000]
        finally:
            if prepared and pointer.exists():
                try:
                    if journal is None:
                        journal = Path(json.loads(pointer.read_text(encoding="utf-8-sig"))["journal"])
                    # Capture any own file bytes changed by completed production calls before cleanup.
                    if (journal / "prepared.json").exists():
                        report["nativeFinal"] = observe()
                    _, final = menu("Finish", "finished.json")
                    report["cleanup"] = final
                    assert final["sceneRetained"] and final["noFixtureSelectionWrites"] and final["ownAssetsQuarantined"]
                except Exception as error:
                    report["cleanupError"] = type(error).__name__ + ": " + str(error)[:1000]
            try:
                # Root-baseline format is verified before deciding which exact saved paths to hash.
                project_rows = baseline.get("projects", baseline)
                rows = project_rows.get("UniBridge_Test_Project", project_rows.get(str(a.project), project_rows.get(a.project.as_posix())))
                if rows is None:
                    raise ValueError("Unrecognized author baseline project key; do not infer hashes.")
                files = rows.get("files", rows) if isinstance(rows, dict) else rows
                if isinstance(files, dict):
                    entries = [(path, value.get("sha256") if isinstance(value, dict) else value) for path, value in files.items()]
                else:
                    entries = [(row["path"], row["sha256"]) for row in files]
                mismatches = [path for path, wanted in entries if not isinstance(wanted, str) or not (a.project / path).is_file() or sha(a.project / path) != wanted]
                report["authorFinal"] = {"protectedFiles": len(entries), "allOriginalSha256Matched": not mismatches, "mismatches": mismatches[:20]}
                assert not mismatches, "Original saved author hashes changed"
            except Exception as error:
                report["preservationError"] = type(error).__name__ + ": " + str(error)[:1000]
            clean = "error" not in report and "cleanupError" not in report and "preservationError" not in report and report.get("cleanup", {}).get("sceneRetained") is True
            report["reproduced"] = a.mode == "reproduce" and clean and bool(report["defects"])
            report["qualification"] = a.mode == "qualify" and clean
            report["passed"] = report["qualification"]
            a.report.parent.mkdir(parents=True, exist_ok=True)
            with a.report.open("x", encoding="utf-8") as stream:
                json.dump(report, stream, ensure_ascii=False, indent=2, allow_nan=False)
    print(json.dumps({"passed": report["passed"], "reproduced": report["reproduced"], "mode": a.mode, "cases": len(report["cases"]), "defects": len(report["defects"]), "report": str(a.report)}))
    return 0 if report["qualification"] or report["reproduced"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
