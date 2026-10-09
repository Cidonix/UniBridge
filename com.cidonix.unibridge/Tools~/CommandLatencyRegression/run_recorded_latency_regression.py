"""Replay recorded command-latency contracts offline, without native/relay/Unity calls.

Supply each immutable successful state report explicitly. Output must be a fresh
file under caller-owned Temp, outside the package and all recorded author roots.
Fixtures are retained beside the output for review; no previous report is erased.
"""
import argparse
import ast
import contextlib
import copy
import ctypes
import hashlib
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import threading
import uuid
from collections import Counter
from datetime import datetime, timedelta
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


class ForbiddenBoundary(AssertionError):
    pass


def forbidden(*unused, **unused_keywords):
    raise ForbiddenBoundary("Offline qualification attempted a native/process/thread boundary.")


class ForbiddenLoader:
    def __getattr__(self, unused):
        return forbidden


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def stamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def change(value, path, replacement=None, remove=False):
    altered = copy.deepcopy(value)
    current = altered
    for key in path[:-1]:
        current = current[key]
    if remove:
        del current[path[-1]]
    else:
        current[path[-1]] = replacement
    return altered


def reject(action):
    try:
        action()
    except ForbiddenBoundary:
        raise
    except (RuntimeError, ValueError, TypeError, KeyError, AttributeError, OSError):
        return
    raise AssertionError("Missing/wrong evidence was accepted.")


def report_args(report):
    target = report["target"]
    return SimpleNamespace(project=Path(target["projectRoot"]), project_id=target["projectId"],
                           editor_pid=target["editorPid"], unity_version=target["unityVersion"],
                           package_version=target["packageVersion"], call_timeout=report["bounds"]["callTimeoutSeconds"])


def receive_record(report, attempt):
    sessions = [session for session in report["sessions"] if session["label"] == attempt["session"]]
    if len(sessions) != 1:
        raise ValueError("Recorded attempt does not identify one session.")
    matches = []
    for message in sessions[0]["messages"]:
        try:
            response = json.loads(message["line"])
        except ValueError:
            continue
        if response.get("id") == attempt["requestId"] and message["monotonic"] == attempt["receiveMonotonic"]:
            if response != attempt["response"]:
                raise ValueError("Recorded response differs from matched stdout message.")
            matches.append(message)
    if len(matches) != 1:
        raise ValueError("Recorded response does not identify one exact receive event.")
    return matches[0]


def safe_output(output, reports, package_root):
    if not output.is_absolute():
        raise ValueError("An explicit absolute output file is required.")
    resolved = output.resolve()
    if output.exists() or output.is_symlink():
        raise ValueError("Output already exists; retain earlier qualifications.")
    if not any(part.casefold() == "temp" for part in resolved.parts):
        raise ValueError("Offline output requires an explicit caller-owned Temp directory.")
    if any(part.casefold() in ("assets", "packages", "projectsettings") for part in resolved.parts):
        raise ValueError("Offline output cannot traverse author roots.")
    prohibited = [package_root.resolve()] + [Path(report["target"]["projectRoot"]).resolve() for report in reports]
    if any(resolved.is_relative_to(root) for root in prohibited):
        raise ValueError("Offline output must be outside shipping package and recorded Unity project roots.")
    current = output.parent
    while current.parent != current:
        if current.is_symlink() or (current.exists() and getattr(current.lstat(), "st_file_attributes", 0) & 0x400):
            raise ValueError("Offline output traverses a reparse point.")
        current = current.parent
    return resolved


@contextlib.contextmanager
def offline_boundaries():
    with contextlib.ExitStack() as stack:
        for name in ("Popen", "run", "call", "check_call", "check_output"):
            stack.enter_context(patch.object(subprocess, name, forbidden))
        for name in ("system", "startfile"):
            if hasattr(os, name):
                stack.enter_context(patch.object(os, name, forbidden))
        for name in ("WinDLL", "CDLL", "PyDLL", "OleDLL"):
            if hasattr(ctypes, name):
                stack.enter_context(patch.object(ctypes, name, forbidden))
        for name in ("windll", "cdll", "pydll", "oledll"):
            if hasattr(ctypes, name):
                stack.enter_context(patch.object(ctypes, name, ForbiddenLoader()))
        stack.enter_context(patch.object(threading.Thread, "start", forbidden))
        stack.enter_context(patch.object(sys, "dont_write_bytecode", True))
        yield


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--recorded-report", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    cli = parser.parse_args()
    runner = Path(__file__).resolve().with_name("run_command_latency_regression.py")
    package_root = runner.parents[2]
    try:
        paths = [path.resolve(strict=True) for path in cli.recorded_report]
        if len(paths) != len(set(paths)):
            raise ValueError("A recorded report cannot be supplied twice.")
        reports = [json.loads(path.read_text(encoding="utf-8-sig")) for path in paths]
        output = safe_output(cli.output, reports, package_root)
        for report in reports:
            if report.get("qualification") != "complete_measured_evidence" or report.get("completedMatrix") is not True or report.get("errors") != []:
                raise ValueError("Only explicitly successful immutable reports are positive inputs; retain failed runs separately.")
    except (OSError, ValueError, KeyError, TypeError) as exc:
        parser.error(str(exc))
    source_paths = [runner, Path(__file__).resolve()] + paths
    hashes = {str(path): sha(path) for path in source_paths}
    cases, provenance = [], []
    counts = Counter()

    def check(name, action, kind="altered_negative"):
        try:
            action()
            cases.append({"name": name, "kind": kind, "passed": True})
        except Exception as exc:
            cases.append({"name": name, "kind": kind, "passed": False, "errorType": type(exc).__name__, "detail": str(exc)})

    with offline_boundaries():
        spec = importlib.util.spec_from_file_location("portable_latency_contract_under_test", runner)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)  # Guarded main; every live boundary throws.
        snapshot_example = state_example = catalog_example = status_example = init_example = None
        seen = set()
        for path, report in zip(paths, reports):
            options = report_args(report)
            last_time = -1
            startup_count = Counter()
            for index, attempt in enumerate(report["attempts"]):
                category = attempt.get("category")
                if category not in ("measured_tool", "initialize", "startup_catalog", "startup_identity"):
                    continue
                message = receive_record(report, attempt)
                received = stamp(message["utc"])
                provenance.append({"report": str(path), "attemptIndex": index, "session": attempt["session"], "requestId": attempt["requestId"], "category": category, "receiveUtc": message["utc"], "receiveMonotonic": attempt["receiveMonotonic"]})
                label = path.parent.name + "/" + attempt["session"] + "/" + str(attempt["requestId"])
                if category == "initialize":
                    counts["actualInitializeResponses"] += 1
                    startup_count[category] += 1
                    check(label + " actual initialize shape", lambda a=attempt: module.initialize_check(a["response"]), "recorded_positive")
                    init_example = init_example or (attempt["response"], options)
                elif category == "startup_catalog":
                    counts["actualCatalogResponses"] += 1
                    startup_count[category] += 1
                    check(label + " actual catalog and requested input schemas", lambda a=attempt: module.catalog_check(a["response"]), "recorded_positive")
                    catalog_example = catalog_example or (attempt["response"], options)
                elif category == "startup_identity":
                    counts["actualConnectedStatuses"] += 1
                    startup_count[category] += 1
                    session = next(s for s in report["sessions"] if s["label"] == attempt["session"])
                    catalog = module.catalog_check(session["catalogAttempt"]["response"])
                    def actual_status(a=attempt, opts=options, cat=catalog):
                        payload = module.tool_payload_check(a["response"], "_server_info", opts)
                        assert payload == a["payload"]
                        module.relay_status_check(payload, opts, cat)
                    check(label + " actual connected identity after catalog", actual_status, "recorded_positive")
                    status_example = status_example or (attempt["payload"], options, catalog)
                else:
                    counts["actualMeasuredPayloads"] += 1
                    tool = attempt["request"]["params"]["name"]
                    counts[tool] += 1
                    prior = last_time
                    def actual_payload(a=attempt, opts=options, prior=prior, received=received, tool=tool):
                        payload = module.tool_payload_check(a["response"], tool, opts)
                        assert payload == a["payload"]
                        if tool == "UniBridge_ContextSnapshot":
                            module.snapshot_check(payload, opts, seen, prior, now=received)
                        elif tool == "UniBridge_ManageEditor":
                            assert a["request"]["params"]["arguments"]["Action"] == "GetState"
                            module.editor_state_check(payload, prior)
                        else:
                            raise AssertionError("Unexpected measured tool in recorded matrix.")
                    check(label + " actual " + tool + " at recorded receive time", actual_payload, "recorded_positive")
                    if tool == "UniBridge_ContextSnapshot":
                        last_time = attempt["payload"]["data"]["editor"]["timeSinceStartup"]
                        snapshot_example = snapshot_example or (attempt["payload"], attempt["response"], options, received)
                    else:
                        last_time = attempt["payload"]["data"]["TimeSinceStartup"]
                        state_example = state_example or (attempt["payload"], attempt["response"], options)
            check(path.parent.name + " independently recorded full startup triplets", lambda c=startup_count, r=report: None if c == {"initialize": len(r["sessions"]), "startup_catalog": len(r["sessions"]), "startup_identity": len(r["sessions"])} else (_ for _ in ()).throw(AssertionError("Missing startup triplet")), "recorded_positive")
            adapted = copy.deepcopy(report)
            if "windowAudit" not in adapted:
                adapted["windowAudit"] = adapted["nativeRestore"]  # Explicit historical key adapter only.
            def recorded_summary(adapted=adapted, original=report):
                module.summarize(adapted, original["wakeEvidence"]["predeclaredSlowThresholdMs"])
                assert adapted["latencySummary"] == original["latencySummary"]
                assert adapted["startupSummary"] == original["startupSummary"]
                assert adapted["wakeEvidence"]["defectObservedBeforeProductionMutation"] is False
            check(path.parent.name + " actual latency/startup summaries stay separate", recorded_summary, "recorded_positive")

        if not all((snapshot_example, state_example, catalog_example, status_example, init_example)):
            raise ValueError("Recorded inputs need actual snapshot, GetState and complete startup examples.")
        payload, envelope, options, received = snapshot_example
        def snapshot_reject(label, altered, nonce=None, previous=-1):
            check(label, lambda: reject(lambda: module.snapshot_check(altered, options, set() if nonce is None else nonce, previous, now=received)))
        for field, wrong in (("id", "0" * 32), ("root", str(options.project.parent / "WrongProject")), ("unityVersion", "wrong-version")):
            snapshot_reject("snapshot missing project." + field, change(payload, ("data", "project", field), remove=True))
            snapshot_reject("snapshot wrong project." + field, change(payload, ("data", "project", field), wrong))
        snapshot_reject("snapshot missing package version", change(payload, ("data", "package", "version"), remove=True))
        snapshot_reject("snapshot wrong package version", change(payload, ("data", "package", "version"), "wrong-version"))
        snapshot_reject("snapshot missing nonce", change(payload, ("data", "snapshotId"), remove=True))
        snapshot_reject("snapshot numeric nonce rejected", change(payload, ("data", "snapshotId"), 123))
        snapshot_reject("snapshot repeated nonce rejected", payload, {payload["data"]["snapshotId"]})
        snapshot_reject("snapshot stale at actual receive time", change(payload, ("data", "createdUtc"), (received - timedelta(seconds=options.call_timeout + 3)).isoformat()))
        snapshot_reject("snapshot missing creation time", change(payload, ("data", "createdUtc"), remove=True))
        for wrong in (None, True, "12", float("nan"), float("inf"), float("-inf"), -0.5):
            snapshot_reject("snapshot clock rejects " + repr(wrong), change(payload, ("data", "editor", "timeSinceStartup"), wrong))
        snapshot_reject("snapshot missing clock", change(payload, ("data", "editor", "timeSinceStartup"), remove=True))
        snapshot_reject("snapshot nonadvancing clock", payload, previous=payload["data"]["editor"]["timeSinceStartup"])
        for flag in ("isPlaying", "isPlayingOrWillChangePlaymode", "isCompiling", "isUpdating"):
            snapshot_reject("snapshot missing " + flag, change(payload, ("data", "editor", flag), remove=True))
            snapshot_reject("snapshot active " + flag, change(payload, ("data", "editor", flag), True))
        altered = copy.deepcopy(payload)
        altered["data"]["editor"]["TimeSinceStartup"] = altered["data"]["editor"].pop("timeSinceStartup")
        snapshot_reject("snapshot wrong DTO clock casing", altered)
        check("snapshot missing success", lambda: reject(lambda: module.snapshot_check(change(payload, ("success",), remove=True), options, set(), -1, now=received)))
        for target_path, wrong in ((("projectContext", "id"), "0" * 32), (("projectContext", "root"), str(options.project.parent / "WrongProject"))):
            for missing in (False, True):
                altered_payload = change(payload, target_path, wrong, remove=missing)
                altered_response = copy.deepcopy(envelope)
                altered_response["result"]["content"][0]["text"] = json.dumps(altered_payload)
                check("tool envelope " + ("missing " if missing else "wrong ") + ".".join(target_path), lambda r=altered_response: reject(lambda: module.tool_payload_check(r, "UniBridge_ContextSnapshot", options)))
        check("tool envelope MCP isError blocks success payload", lambda: reject(lambda: module.tool_payload_check(change(envelope, ("result", "isError"), True), "UniBridge_ContextSnapshot", options)))
        failed_envelope = copy.deepcopy(envelope)
        failed_envelope["result"]["content"][0]["text"] = json.dumps(change(payload, ("success",), False))
        check("tool envelope business failure blocked", lambda: reject(lambda: module.tool_payload_check(failed_envelope, "UniBridge_ContextSnapshot", options)))
        check("tool envelope duplicate text blocked", lambda: reject(lambda: module.tool_payload_check(change(envelope, ("result", "content"), envelope["result"]["content"] * 2), "UniBridge_ContextSnapshot", options)))

        state, state_envelope, state_options = state_example
        for wrong in (None, True, "12", float("nan"), float("inf"), float("-inf"), -0.5):
            check("GetState clock rejects " + repr(wrong), lambda wrong=wrong: reject(lambda: module.editor_state_check(change(state, ("data", "TimeSinceStartup"), wrong), -1)))
        check("GetState missing clock", lambda: reject(lambda: module.editor_state_check(change(state, ("data", "TimeSinceStartup"), remove=True), -1)))
        check("GetState nonadvancing clock", lambda: reject(lambda: module.editor_state_check(state, state["data"]["TimeSinceStartup"])))
        check("GetState missing success", lambda: reject(lambda: module.editor_state_check(change(state, ("success",), remove=True), -1)))
        for flag in ("IsReady", "IsPlaying", "IsPlayingOrWillChangePlaymode", "IsCompiling", "IsUpdating"):
            wrong = False if flag == "IsReady" else True
            check("GetState missing " + flag, lambda flag=flag: reject(lambda: module.editor_state_check(change(state, ("data", flag), remove=True), -1)))
            check("GetState wrong " + flag, lambda flag=flag, wrong=wrong: reject(lambda: module.editor_state_check(change(state, ("data", flag), wrong), -1)))
        altered = copy.deepcopy(state)
        altered["data"]["timeSinceStartup"] = altered["data"].pop("TimeSinceStartup")
        check("GetState lower-case clock is not typed DTO", lambda: reject(lambda: module.editor_state_check(altered, -1)))
        check("GetState outer wrong context blocked", lambda: reject(lambda: module.tool_payload_check(change(state_envelope, ("result", "content"), [{"type": "text", "text": json.dumps(change(state, ("projectContext", "id"), "0" * 32))}]), "UniBridge_ManageEditor", state_options)))

        catalog_response, _ = catalog_example
        for name in ("_server_info", "UniBridge_CommandStatus", "UniBridge_ContextSnapshot", "UniBridge_ManageEditor"):
            altered = change(catalog_response, ("result", "tools"), [tool for tool in catalog_response["result"]["tools"] if tool["name"] != name])
            check("catalog missing " + name, lambda altered=altered: reject(lambda: module.catalog_check(altered)))
        check("catalog empty rejected", lambda: reject(lambda: module.catalog_check(change(catalog_response, ("result", "tools"), []))))
        check("catalog duplicate rejected", lambda: reject(lambda: module.catalog_check(change(catalog_response, ("result", "tools"), catalog_response["result"]["tools"] + [catalog_response["result"]["tools"][0]]))))
        check("catalog missing input schema rejected", lambda: reject(lambda: module.catalog_check(change(catalog_response, ("result", "tools", 0, "inputSchema"), remove=True))))
        si = next(i for i, tool in enumerate(catalog_response["result"]["tools"]) if tool["name"] == "UniBridge_ContextSnapshot")
        ei = next(i for i, tool in enumerate(catalog_response["result"]["tools"]) if tool["name"] == "UniBridge_ManageEditor")
        for key in module.SNAPSHOT_ARGS:
            altered = change(catalog_response, ("result", "tools", si, "inputSchema", "properties", key), remove=True)
            check("catalog requested option absent " + key, lambda altered=altered: reject(lambda: module.catalog_check(altered)))
        check("catalog required GetState absent", lambda: reject(lambda: module.catalog_check(change(catalog_response, ("result", "tools", ei, "inputSchema", "properties", "Action", "enum"), ["GetSelection"]))))
        check("catalog required GetSelection absent", lambda: reject(lambda: module.catalog_check(change(catalog_response, ("result", "tools", ei, "inputSchema", "properties", "Action", "enum"), ["GetState"]))))
        info, status_options, catalog = status_example
        status_fields = {"unityConnected": False, "editorPid": status_options.editor_pid + 1, "projectId": "0" * 32, "projectRoot": str(status_options.project.parent / "WrongProject"), "expectedProjectRoot": str(status_options.project.parent / "WrongProject"), "selectedConnection": "", "toolCount": catalog["unityToolCount"] + 1}
        for key, wrong in status_fields.items():
            for missing in (False, True):
                altered = change(info, (key,), wrong, remove=missing)
                check("relay status " + ("missing " if missing else "wrong ") + key, lambda altered=altered: reject(lambda: module.relay_status_check(altered, status_options, catalog)))
        initialize, _ = init_example
        for path in (("jsonrpc",), ("result", "protocolVersion"), ("result", "capabilities", "tools"), ("result", "serverInfo", "name"), ("result", "serverInfo", "version")):
            check("initialize missing " + ".".join(path), lambda path=path: reject(lambda: module.initialize_check(change(initialize, path, remove=True))))
        check("initialize wrong protocol rejected", lambda: reject(lambda: module.initialize_check(change(initialize, ("result", "protocolVersion"), "wrong"))))
        check("initialize error envelope rejected", lambda: reject(lambda: module.initialize_check(change(initialize, ("error",), {"code": -32603, "message": "deliberate offline failure"}))))
        check("initialize missing server name not actual identity", lambda: reject(lambda: module.initialize_check(change(initialize, ("result", "serverInfo", "name"), ""))))

        for desired in ("foreground", "background", "minimized"):
            window = {"iconic": desired == "minimized", "fixture": True}
            focus = {"hwnd": 1 if desired == "foreground" else 2}
            native = SimpleNamespace(hwnd=1, original=window, original_foreground=focus,
                                     guard=lambda: None, validate=lambda: None, state=lambda w=window: w.copy(),
                                     foreground_identity=lambda f=focus: f.copy(), transition=forbidden, restore=forbidden,
                                     u=SimpleNamespace(SetWindowPlacement=forbidden, SetForegroundWindow=forbidden, ShowWindowAsync=forbidden))
            def observational(native=native, desired=desired):
                assert list(module.measurement_states(native, desired)) == [desired]
                audit = module.NativeTarget.audit(native)
                assert audit["status"] == "unchanged" and audit["uiMutationsPerformedByRunner"] == 0 and audit["restoreAttempted"] is False
            check(desired + " observation one state and zero UI corrections", observational, "fake_boundary_contract")
            for wrong in ("foreground", "background", "minimized"):
                if wrong != desired:
                    check(desired + " cannot masquerade as " + wrong, lambda native=native, wrong=wrong: reject(lambda: list(module.measurement_states(native, wrong))), "fake_boundary_contract")
            native.foreground_identity = lambda: {"hwnd": 99}
            check(desired + " external steering preserved without restore", lambda native=native: None if module.NativeTarget.audit(native)["status"] == "preserved_external_steering" else (_ for _ in ()).throw(AssertionError("Steering not preserved")), "fake_boundary_contract")

        tree = ast.parse(runner.read_text(encoding="utf-8"))
        def static_contract():
            allowed = {"OpenProcess", "CloseHandle", "WaitForSingleObject", "QueryFullProcessImageNameW", "GetProcessTimes", "GetWindowThreadProcessId", "GetWindowTextW", "GetClassNameW", "GetWindow", "GetForegroundWindow", "IsWindow", "IsWindowVisible", "IsWindowEnabled", "IsIconic", "IsZoomed", "GetWindowPlacement", "GetLastInputInfo", "GetDpiForWindow", "EnumWindows"}
            forbidden_exports = {"SetWindowPlacement", "SetForegroundWindow", "ShowWindow", "ShowWindowAsync", "SetWindowPos", "BringWindowToTop", "AttachThreadInput", "SendInput", "keybd_event", "mouse_event", "PostMessageW", "SendMessageW", "TerminateProcess", "SetProcessDpiAwareness", "SetThreadDpiAwarenessContext"}
            native_node = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "NativeTarget")
            exports = []
            for node in ast.walk(native_node):
                if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "bind" and len(node.args) >= 2 and isinstance(node.args[1], ast.Constant):
                    exports.append(node.args[1].value)
                if isinstance(node, ast.For) and isinstance(node.target, ast.Name) and node.target.id == "name" and isinstance(node.iter, (ast.Tuple, ast.List)):
                    exports.extend(item.value for item in node.iter.elts if isinstance(item, ast.Constant))
                if isinstance(node, ast.Attribute):
                    assert node.attr not in forbidden_exports, node.attr
            assert exports and set(exports).issubset(allowed), exports
            assert not set(exports) & forbidden_exports
            assert all(node.name not in ("transition", "restore") for node in native_node.body if isinstance(node, ast.FunctionDef))
            assert not any(isinstance(node, ast.Constant) and isinstance(node.value, str) and re.match(r"^[A-Za-z]:[\\/]", node.value) for node in ast.walk(tree)), "Hardcoded workstation path"
            main_node = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "main")
            main_text = ast.unparse(main_node)
            assert main_text.index("validate_arguments(") < main_text.index("NativeTarget(")
            assert "native.audit()" in main_text
        check("portable native binds observational and arguments gate precedes NativeTarget", static_contract, "static_code_contract")

        output.parent.mkdir(parents=True, exist_ok=True)
        fixtures = output.parent / ("offline-fixtures-" + uuid.uuid4().hex[:12])
        fixtures.mkdir()
        project = fixtures / "OfflineTestProject"
        for directory in ("Assets", "Packages/com.cidonix.unibridge", "ProjectSettings/UniBridge"):
            (project / directory).mkdir(parents=True)
        project_id = uuid.uuid4().hex  # This isolated fixture never identifies a live project.
        identity = {"schema_version": 1, "project_id": project_id, "project_name": project.name, "created_date": "2026-01-01T00:00:00Z"}
        identity_path = project / "ProjectSettings/UniBridge/project.json"
        identity_path.write_text(json.dumps(identity), encoding="utf-8")
        fixture_unity_version = "offline-editor-version"
        (project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: " + fixture_unity_version + "\n", encoding="utf-8")
        unity, relay = fixtures / "Unity.exe", fixtures / "relay.exe"
        unity.write_bytes(b"offline inert fixture, never executed")
        relay.write_bytes(b"offline inert fixture, never executed")
        arguments = ["--allow-test-project", "--project", str(project), "--project-id", project_id, "--unity-exe", str(unity), "--unity-version", fixture_unity_version, "--relay", str(relay), "--editor-pid", "1", "--package-version", "offline-test", "--report-directory", str(project / "Library/AgentValidation/CommandLatencyRegression/fresh-report"), "--single-state", "background"]
        base_args = module.build_parser().parse_args(arguments)
        check("portable explicit approved filesystem fixture arguments accepted", lambda: module.validate_arguments(copy.deepcopy(base_args)), "filesystem_fixture_contract")
        def no_approval():
            with contextlib.redirect_stderr(io.StringIO()):
                try:
                    module.build_parser().parse_args(arguments[1:])
                except SystemExit as exc:
                    assert exc.code == 2
                    return
            raise AssertionError("CLI accepts a target without explicit approval.")
        check("CLI refuses omitted explicit owner opt-in", no_approval, "filesystem_fixture_contract")
        cli_wrong = {"allow_test_project": False, "single_state": None, "editor_pid": 0, "project_id": "invalid", "unity_version": "wrong-version", "package_version": "", "project": Path("relative"), "unity_exe": fixtures / "MissingUnity.exe", "relay": fixtures / "MissingRelay.exe", "report_directory": project / "Assets/forbidden-report", "warm_repeats": 6, "short_sessions": 5, "idle_seconds": 11, "call_timeout": 21, "initialize_timeout": 31, "whole_run_seconds": 301, "slow_threshold_ms": float("nan")}
        for key, wrong in cli_wrong.items():
            altered = copy.deepcopy(base_args)
            setattr(altered, key, wrong)
            check("filesystem preflight refuses " + key, lambda altered=altered: reject(lambda: module.validate_arguments(altered)), "filesystem_fixture_contract")
        existing_args = copy.deepcopy(base_args)
        existing_args.report_directory.mkdir(parents=True)
        check("filesystem preflight never overwrites earlier report", lambda: reject(lambda: module.validate_arguments(existing_args)), "filesystem_fixture_contract")
        original_identity_bytes = identity_path.read_bytes()
        try:
            for key, wrong in (("project_id", "0" * 32), ("project_name", "OtherProject"), ("created_date", None)):
                identity_path.write_text(json.dumps(change(identity, (key,), wrong)), encoding="utf-8")
                check("project identity blocks " + key + " migration", lambda: reject(lambda: module.identity_preflight(base_args)), "filesystem_fixture_contract")
        finally:
            identity_path.write_bytes(original_identity_bytes)
        installed = project / "Packages/com.cidonix.unibridge"
        for name in module.PUMP_SOURCES:
            target = installed / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((package_root / name).read_bytes())
        manifest = installed / "package.json"
        manifest.write_text(json.dumps({"name": "com.cidonix.unibridge", "version": base_args.package_version}), encoding="utf-8")
        check("filesystem package source parity exact six files", lambda: module.pump_source_hashes(base_args), "filesystem_fixture_contract")
        parity_file = installed / module.PUMP_SOURCES[0]
        source_bytes = parity_file.read_bytes()
        try:
            parity_file.write_bytes(source_bytes + b"\n// offline deliberate mismatch\n")
            check("filesystem package differing source blocked", lambda: reject(lambda: module.pump_source_hashes(base_args)), "filesystem_fixture_contract")
        finally:
            parity_file.write_bytes(source_bytes)
        manifest.write_text(json.dumps({"name": "com.cidonix.unibridge", "version": "wrong-version"}), encoding="utf-8")
        check("filesystem package wrong installed version blocked", lambda: reject(lambda: module.pump_source_hashes(base_args)), "filesystem_fixture_contract")
        manifest.write_text(json.dumps({"name": "com.cidonix.unibridge", "version": base_args.package_version}), encoding="utf-8")
        home = fixtures / "OfflineHome"
        connections = home / ".unibridge/mcp/connections"
        connections.mkdir(parents=True)
        connection_file = connections / "owned-record.json"
        connection_file.write_text(json.dumps({"editor_pid": 1, "project_id": project_id, "project_path": str(project / "Assets"), "connection_type": "pipe", "connection_path": "offline-never-connected"}), encoding="utf-8")
        def actual_discovery_fallback():
            with patch.object(module.Path, "home", return_value=home):
                found = module.discovery_match(base_args)
            assert Path(found["sourceFile"]) == connection_file
            assert module.norm(found["projectRoot"]) == module.norm(project)
        check("filesystem discovery fallback retains actual JSON source path", actual_discovery_fallback, "filesystem_fixture_contract")

        aggregate = copy.deepcopy(reports[0])
        aggregate["attempts"] = [copy.deepcopy(attempt) for report in reports for attempt in report["attempts"]]
        aggregate["sessions"] = [copy.deepcopy(session) for report in reports for session in report["sessions"]]
        aggregate["windowAudit"] = aggregate.get("windowAudit", aggregate.get("nativeRestore"))
        module.summarize(aggregate, 1000)
        check("summary includes exactly recorded measured calls, excludes all startup", lambda: None if sum(row["attempts"] for row in aggregate["latencySummary"]) == counts["actualMeasuredPayloads"] else (_ for _ in ()).throw(AssertionError("Startup included in tool RTT")), "recorded_positive")
        measured_states = {attempt.get("state") for attempt in aggregate["attempts"] if attempt.get("category") == "measured_tool"}
        if {"foreground", "background"}.issubset(measured_states):
            delayed = copy.deepcopy(aggregate)
            for attempt in delayed["attempts"]:
                if attempt.get("category") == "measured_tool" and attempt.get("state") == "background" and attempt.get("clientKind") == "warm" and attempt["request"]["params"]["name"] == "UniBridge_ContextSnapshot":
                    attempt["latencyMs"] = 1250
            module.summarize(delayed, 1000)
            check("deliberately altered delay classification with explicit full preservation", lambda: None if delayed["wakeEvidence"]["defectObservedBeforeProductionMutation"] is True else (_ for _ in ()).throw(AssertionError("Altered delay control not recognized")), "altered_classification_control")
            for key in ("completedMatrix", "errors", "authorFileComparison", "authorStateBefore", "authorStateAfter", "fullSelectionBefore", "fullSelectionAfter", "pumpSourcesUnchanged", "windowAudit"):
                altered = copy.deepcopy(delayed)
                altered.pop(key, None)
                def missing_audit(altered=altered):
                    module.summarize(altered, 1000)
                    assert altered["wakeEvidence"]["preservationChecksAvailable"] is False
                    assert altered["wakeEvidence"]["defectObservedBeforeProductionMutation"] is False
                check("summary missing " + key + " cannot prove preservation/defect", missing_audit)
            for failure_type in ("Steering", "RuntimeError", "TimeoutError"):
                altered = copy.deepcopy(aggregate)
                attempt = next(a for a in altered["attempts"] if a.get("category") == "measured_tool" and a.get("state") == "background" and a["request"]["params"]["name"] == "UniBridge_ContextSnapshot")
                attempt.update(status="abandoned_read", failureType=failure_type)
                module.summarize(altered, 1000)
                check("summary " + failure_type + " never becomes wake delay", lambda altered=altered: None if not altered["wakeEvidence"]["actualDelayedOrFailedMainThreadCalls"] else (_ for _ in ()).throw(AssertionError("Wrong failure classified as wake delay")))
            altered = copy.deepcopy(aggregate)
            attempt = next(a for a in altered["attempts"] if a.get("category") == "measured_tool" and a.get("state") == "background" and a["request"]["params"]["name"] == "UniBridge_ContextSnapshot")
            attempt.update(status="read_timeout", failureType="RequestTimeout")
            altered["completedMatrix"] = False
            module.summarize(altered, 1000)
            check("altered actual-request timeout is candidate only with incomplete matrix", lambda: None if altered["wakeEvidence"]["actualDelayedOrFailedMainThreadCalls"] and not altered["wakeEvidence"]["defectObservedBeforeProductionMutation"] else (_ for _ in ()).throw(AssertionError("Timeout classification incorrect")), "altered_classification_control")

    check("shipping runner, offline qualifier and immutable recordings unchanged", lambda: None if hashes == {str(path): sha(path) for path in source_paths} else (_ for _ in ()).throw(AssertionError("Input/source changed during offline qualification")), "source_preservation")
    result = {"schema": "unibridge.command-latency.recorded-regression.v1", "passed": all(case["passed"] for case in cases), "passedCount": sum(case["passed"] for case in cases), "caseCount": len(cases), "sourceHashes": hashes, "recordedEvidenceCounts": dict(counts), "recordedReceiveEvents": provenance, "cases": cases, "fixturesDirectory": str(fixtures), "nativeOperations": 0, "relayLaunches": 0, "unityRequests": 0, "limits": ["Recorded positives are immutable historical payloads evaluated at matched recorded receive times; no fresh startup or main-thread measurement is made.", "Altered negatives and delay/timeout controls establish validation/classification behavior only and are never actual performance evidence.", "Fake native observations and filesystem fixtures execute actual shipping functions with every native/process/thread boundary forbidden.", "Historic nativeRestore is mapped to windowAudit only in a copied offline adapter; original recordings are never rewritten.", "Case count is a list of executed checks, not action/branch coverage or live-run count.", "Separate state reports retain separate warm clients; one warm connection across all states is not inferred."]}
    with output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
    print(json.dumps({"passed": result["passed"], "passedCount": result["passedCount"], "caseCount": result["caseCount"], "recordedEvidenceCounts": result["recordedEvidenceCounts"], "output": str(output)}))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
