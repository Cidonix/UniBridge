#!/usr/bin/env python3
import argparse
import json
import os
import queue
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path


SUITE_NAME = "UniBridge MCP Smoke Regression"


if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")


def utc_now():
    return datetime.now(timezone.utc)


def iso(dt):
    return dt.isoformat().replace("+00:00", "Z")


def read_stream(stream, output_queue, sink):
    try:
        for line in stream:
            line = line.rstrip("\r\n")
            sink.append(line)
            output_queue.put(line)
    except Exception as exc:
        sink.append(f"<reader failed: {exc}>")


class McpClient:
    def __init__(self, relay_path, project_path, default_timeout, trace_path=None):
        self.relay_path = str(relay_path)
        self.project_path = str(project_path)
        self.default_timeout = default_timeout
        self.trace_path = Path(trace_path) if trace_path else None
        self.process = None
        self.stdout_queue = queue.Queue()
        self.stdout_lines = []
        self.stderr_lines = []
        self.next_id = 1

    def __enter__(self):
        args = [
            self.relay_path,
            "--mcp",
            "--project-path",
            self.project_path,
            "--name",
            "UniBridge MCP Smoke Regression",
        ]
        self.process = subprocess.Popen(
            args,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        threading.Thread(
            target=read_stream,
            args=(self.process.stdout, self.stdout_queue, self.stdout_lines),
            daemon=True,
        ).start()
        threading.Thread(
            target=read_stream,
            args=(self.process.stderr, queue.Queue(), self.stderr_lines),
            daemon=True,
        ).start()
        self.initialize()
        return self

    def __exit__(self, exc_type, exc, tb):
        if not self.process:
            return
        try:
            if self.process.stdin:
                self.process.stdin.close()
        except Exception:
            pass
        try:
            self.process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            self.process.kill()
            try:
                self.process.wait(timeout=2)
            except Exception:
                pass

    def trace(self, direction, payload):
        if not self.trace_path:
            return
        self.trace_path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(payload, str):
            line = payload
        else:
            line = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
        first = line.encode("utf-8", errors="replace")[:16].hex("-")
        with self.trace_path.open("a", encoding="utf-8") as f:
            f.write(f"{iso(utc_now())} [{direction}] firstBytesUtf8={first} line={line}\n")

    def send(self, method, params=None, expect_response=True, timeout=None):
        if not self.process or not self.process.stdin:
            raise RuntimeError("MCP relay process is not running.")
        request = {
            "jsonrpc": "2.0",
            "method": method,
            "params": params or {},
        }
        request_id = None
        if expect_response:
            request_id = self.next_id
            self.next_id += 1
            request["id"] = request_id
        line = json.dumps(request, ensure_ascii=False, separators=(",", ":"))
        self.trace("send", line)
        self.process.stdin.write(line + "\n")
        self.process.stdin.flush()
        if not expect_response:
            return None
        return self.wait_for_response(request_id, timeout or self.default_timeout)

    def wait_for_response(self, request_id, timeout):
        deadline = time.monotonic() + timeout
        seen = []
        while time.monotonic() < deadline:
            remaining = max(0.05, deadline - time.monotonic())
            try:
                line = self.stdout_queue.get(timeout=remaining)
            except queue.Empty:
                break
            if not line:
                continue
            self.trace("recv", line)
            seen.append(line)
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                continue
            if message.get("id") == request_id:
                if "error" in message:
                    raise RuntimeError(f"MCP error: {message['error'].get('message')}")
                return message
        tail = "\n".join(seen[-5:])
        raise TimeoutError(
            f"Timed out waiting for JSON-RPC response id={request_id} after {timeout}s. Output tail: {tail}"
        )

    def initialize(self):
        self.send(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "UniBridge MCP Smoke Regression", "version": "1.0"},
            },
        )
        self.send("notifications/initialized", {}, expect_response=False)

    def list_tools(self):
        response = self.send("tools/list", {})
        return response.get("result", {}).get("tools", [])

    def call_tool(self, name, arguments=None, timeout=None):
        response = self.send(
            "tools/call",
            {"name": name, "arguments": arguments or {}},
            timeout=timeout,
        )
        result = response.get("result", {})
        if result.get("isError"):
            raise RuntimeError(f"Tool {name} returned isError=true: {result}")
        text = ""
        for item in result.get("content", []):
            if item.get("type") == "text":
                text = item.get("text", "")
                break
        data = None
        if text:
            try:
                data = json.loads(text)
            except json.JSONDecodeError:
                data = None
        return {"text": text, "data": data, "raw": result}


def prop(data, *path, default=None):
    current = data
    for segment in path:
        if not isinstance(current, dict) or segment not in current:
            return default
        current = current[segment]
    return current


def count_value(value):
    if value is None:
        return None
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, int):
        return value
    if isinstance(value, (list, tuple, set)):
        return len(value)
    if isinstance(value, str):
        try:
            return int(value)
        except ValueError:
            return 1 if value else 0
    return None


def first_count(data, *paths):
    for path in paths:
        value = prop(data, *path)
        count = count_value(value)
        if count is not None:
            return count
    return None


def assert_zero_or_missing(value, message):
    count = count_value(value)
    if count is not None and count > 0:
        raise AssertionError(f"{message} Count={count}.")


def assert_console_healthy(data, require_evidence=False):
    errors = first_count(data, ("totals", "errorCount"), ("totals", "errors"), ("errors",), ("errorCount",))
    exceptions = first_count(data, ("totals", "exceptionCount"), ("totals", "exceptions"), ("exceptions",), ("exceptionCount",))
    asserts = first_count(data, ("totals", "assertCount"), ("totals", "asserts"), ("asserts",), ("assertCount",))
    if require_evidence and any(value is None for value in (errors, exceptions, asserts)):
        raise AssertionError(f"Console health evidence is incomplete: totals={prop(data, 'totals')}")
    assert_zero_or_missing(errors, "Console has errors.")
    assert_zero_or_missing(exceptions, "Console has exceptions.")
    assert_zero_or_missing(asserts, "Console has asserts.")
    assert_zero_or_missing(
        first_count(data, ("criticalIssues",), ("criticalIssueCount",), ("summary", "criticalIssues"), ("summary", "criticalIssueCount")),
        "Console has critical issues.",
    )


def assert_compilation_healthy(data, require_evidence=False):
    errors = first_count(data, ("diagnostics", "errors"), ("errors",), ("errorCount",), ("summary", "errors"), ("summary", "errorCount"))
    if require_evidence and errors is None:
        raise AssertionError(f"Compilation diagnostics did not include an error count: keys={sorted(data.keys())}")
    assert_zero_or_missing(errors, "Compilation has errors.")
    assert_zero_or_missing(first_count(data, ("buildSystemHealth", "criticalIssueCount"), ("buildSystemHealth", "criticalIssues")), "Build system health has critical issues.")
    assert_zero_or_missing(first_count(data, ("assemblyFreshness", "staleAssemblyCount"), ("compileHealth", "staleAssemblyCount")), "Assembly freshness reports stale assemblies.")
    if prop(data, "compileHealth", "healthy") is False:
        raise AssertionError(f"Compilation health is not healthy: {prop(data, 'compileHealth')}")
    if prop(data, "assemblyFreshness", "staleLikely") is True or prop(data, "assemblyFreshness", "assemblyCSharpStaleLikely") is True:
        raise AssertionError(f"Assembly freshness reports stale output: {prop(data, 'assemblyFreshness')}")


def response_payload(data):
    if isinstance(data, dict) and isinstance(data.get("data"), dict):
        return data["data"]
    return data or {}


class SmokeSuite:
    def __init__(self, args):
        self.args = args
        self.started = utc_now()
        self.steps = []
        self.failures = []
        self.project_path = Path(args.project_path).resolve()
        self.relay_path = Path(args.relay_path).resolve() if args.relay_path else self.default_relay_path()
        self.report_path = Path(args.report_path).resolve() if args.report_path else self.default_report_path()
        self.report_path.parent.mkdir(parents=True, exist_ok=True)

    def default_relay_path(self):
        package_root = Path(__file__).resolve().parents[2]
        return package_root / "RelayApp~" / "unibridge_relay_win.exe"

    def default_report_path(self):
        stamp = utc_now().strftime("%Y%m%d-%H%M%S")
        return self.project_path / "Library" / "UniBridge" / f"mcp-smoke-regression-{stamp}.json"

    def make_client(self):
        return McpClient(
            self.relay_path,
            self.project_path,
            self.args.default_timeout_seconds,
            self.args.trace_transport_path,
        )

    def write_report(self, status):
        now = utc_now()
        report = {
            "suite": SUITE_NAME,
            "status": status,
            "startedUtc": iso(self.started),
            "updatedUtc": iso(now),
            "finishedUtc": None if status == "running" else iso(now),
            "durationMs": int((now - self.started).total_seconds() * 1000),
            "projectPath": str(self.project_path),
            "relayPath": str(self.relay_path),
            "options": {
                "skipRefresh": self.args.skip_refresh,
                "skipCompile": self.args.skip_compile,
                "includePlayMode": self.args.include_play_mode,
                "includeUiRecipe": self.args.include_ui_recipe,
                "includePrefabStageUi": self.args.include_prefab_stage_ui,
                "includeAssetRecipe": self.args.include_asset_recipe,
                "assetFolder": self.args.asset_folder,
                "defaultTimeoutSeconds": self.args.default_timeout_seconds,
                "reloadTimeoutSeconds": self.args.reload_timeout_seconds,
                "maxSteps": self.args.max_steps,
            },
            "summary": {
                "total": len(self.steps),
                "passed": sum(1 for step in self.steps if step["status"] == "passed"),
                "failed": len(self.failures),
                "failures": list(self.failures),
            },
            "steps": self.steps,
        }
        with self.report_path.open("w", encoding="utf-8") as f:
            json.dump(report, f, indent=2, ensure_ascii=False)

    def run_step(self, step_id, description, fn):
        if self.args.max_steps and len(self.steps) >= self.args.max_steps:
            return
        print(f"[{step_id}] {description}", flush=True)
        started = time.monotonic()
        try:
            result = fn()
            record = {
                "id": step_id,
                "description": description,
                "status": "passed",
                "durationMs": int((time.monotonic() - started) * 1000),
                "result": result,
            }
            self.steps.append(record)
            self.write_report("running")
            print(f"[{step_id}] passed in {record['durationMs']}ms", flush=True)
        except Exception as exc:
            record = {
                "id": step_id,
                "description": description,
                "status": "failed",
                "durationMs": int((time.monotonic() - started) * 1000),
                "error": str(exc),
            }
            self.steps.append(record)
            self.failures.append(f"{step_id}: {exc}")
            self.write_report("running")
            print(f"[{step_id}] FAILED: {exc}", flush=True)

    def run(self):
        if not self.project_path.exists():
            raise FileNotFoundError(f"ProjectPath does not exist: {self.project_path}")
        if not self.relay_path.exists():
            raise FileNotFoundError(f"RelayPath does not exist: {self.relay_path}")

        required = [
            "UniBridge_Discover",
            "UniBridge_ManageEditor",
            "UniBridge_ReadConsole",
            "UniBridge_ValidateScript",
            "UniBridge_CreateScript",
            "UniBridge_DeleteScript",
            "UniBridge_GetSha",
            "UniBridge_ReadResource",
            "UniBridge_ScriptApplyEdits",
            "UniBridge_AssetIntelligence",
            "UniBridge_ContextSnapshot",
            "UniBridge_SceneObjectView",
            "UniBridge_WorkflowRecipes",
            "UniBridge_BatchActions",
            "UniBridge_RuntimeStateProbe",
            "UniBridge_ExecutionStatus",
            "UniBridge_ManageAsset",
            "UniBridge_ManageGameObject",
            "UniBridge_ManagePrefab",
            "UniBridge_ManageUI",
            "UniBridge_VersionControl",
        ]

        self.run_step("tools_list", "List MCP tools and verify the core UniBridge surface.", lambda: self.step_tools_list(required))
        self.run_step("discover_ping", "Ping UniBridge and read package identity.", self.step_discover_ping)
        self.run_step("clear_console", "Clear Unity Console before smoke diagnostics.", self.step_clear_console)
        self.run_step("wait_ready", "Wait for Unity editor readiness before reading project state.", self.step_wait_ready)
        self.run_step("validate_package_script", "Validate a UniBridge package script through MCP.", self.step_validate_package_script)
        self.run_step("version_control_paths", "Verify VersionControl accepts one or many paths and reports invalid path sets clearly.", self.step_version_control_paths)
        self.run_step("script_apply_edits", "Verify structured and anchor ScriptApplyEdits previews are strictly no-write before actual apply.", self.step_script_anchor_edits)
        self.run_step("asset_read_text", "Read package.json via AssetIntelligence Action=ReadText alias.", self.step_asset_read_text)
        self.run_step("context_snapshot_brief", "Read compact ContextSnapshot with compact console summary.", self.step_context_snapshot)
        self.run_step("scene_hierarchy_brief", "Read a bounded SceneObjectView hierarchy including inactive objects.", self.step_scene_hierarchy)
        self.run_step(
            "batch_scene_hierarchy_paths",
            "Verify BatchActions keeps scene hierarchy references out of asset impact and rollback path resolution.",
            self.step_batch_scene_hierarchy_paths,
        )
        self.run_step("workflow_core_recipe", "Execute RunCoreSmokeTest recipe and cleanup its temporary object.", self.step_core_recipe)
        self.run_step("runtime_probe_timeout_releases_slot", "Verify a timed-out RuntimeStateProbe releases its read-only scheduler slot.", self.step_runtime_probe_timeout_releases_slot)

        if not self.args.skip_refresh:
            self.run_step("refresh_assets", "Run reload-safe RefreshAssets with WaitForCompletion.", self.step_refresh_assets)
        if not self.args.skip_compile:
            self.run_step("request_compile_no_wait", "Request script compilation as a reload boundary.", self.step_request_compile)
            self.run_step("wait_ready_after_reload", "Wait after refresh/compile reload boundary and read health evidence.", self.step_wait_ready_after_reload)

        self.run_step("runtime_probe_assertions_schema", "Verify RuntimeStateProbe Assertions is advertised as an array of objects.", self.step_runtime_probe_assertions_schema)
        self.run_step("runtime_probe_single_assertion_object", "Verify a single RuntimeStateProbe assertion object is normalized to a one-item array.", self.step_runtime_probe_single_assertion_object)
        self.run_step("compilation_diagnostics", "Read compilation diagnostics and build-system health.", self.step_compilation_diagnostics)

        if self.args.include_play_mode:
            self.run_step(
                "manage_game_object_serialized_component_properties",
                "Verify Create applies private serialized ComponentProperties in Edit and Play Mode.",
                self.step_manage_game_object_serialized_component_properties,
            )

        if self.args.include_ui_recipe:
            self.run_step("workflow_ui_recipe", "Execute RunUISmokeTest recipe and cleanup its temporary Canvas.", self.step_ui_recipe)
        if self.args.include_prefab_stage_ui:
            self.run_step(
                "prefab_stage_ui_creation",
                "Create UI through MCP inside an isolated Prefab Stage and verify strict parent resolution.",
                self.step_prefab_stage_ui_creation,
            )
        if self.args.include_asset_recipe:
            self.run_step("workflow_asset_recipe", "Execute RunAssetSmokeTest recipe on a small texture set.", self.step_asset_recipe)
        if self.args.include_play_mode:
            self.run_step("play_mode_enter", "Queue Play Mode and survive reload-safe boundary.", self.step_play_enter)
            self.run_step("play_mode_wait", "Wait until Unity reports Play Mode.", self.step_play_wait)
            self.run_step("play_mode_exit", "Exit Play Mode and survive reload-safe boundary.", self.step_play_exit)
            self.run_step("edit_mode_wait", "Wait until Unity returns to Edit Mode.", self.step_edit_wait)

        self.run_step("console_diagnostic_summary", "Read final compact console diagnostic summary.", self.step_console_summary)

        status = "passed" if not self.failures else "failed"
        self.write_report(status)
        print(f"{SUITE_NAME} {status}. Report: {self.report_path}", flush=True)
        return 0 if status == "passed" else 1

    def tool(self, name, args=None, timeout=None):
        with self.make_client() as client:
            result = client.call_tool(name, args or {}, timeout=timeout)
        envelope = result.get("data") if isinstance(result, dict) else None
        if isinstance(envelope, dict) and any(key in envelope for key in ("success", "message", "data")):
            result["envelope"] = envelope
            result["data"] = response_payload(envelope)
        return result

    def step_tools_list(self, required):
        with self.make_client() as client:
            tools = client.list_tools()
        names = [tool.get("name") for tool in tools]
        missing = [name for name in required if name not in names]
        if missing:
            raise AssertionError(f"Missing required tools: {', '.join(missing)}")
        return {"toolCount": len(names), "requiredTools": required}

    def step_runtime_probe_assertions_schema(self):
        with self.make_client() as client:
            tools = client.list_tools()
        tool = next((item for item in tools if item.get("name") == "UniBridge_RuntimeStateProbe"), None)
        if not tool:
            raise AssertionError("UniBridge_RuntimeStateProbe is missing from tools/list.")
        assertions = prop(tool, "inputSchema", "properties", "Assertions", default={}) or {}
        if assertions.get("type") != "array":
            raise AssertionError(f"RuntimeStateProbe Assertions schema type is not array: {assertions}")
        items = assertions.get("items") or {}
        if items.get("type") != "object":
            raise AssertionError(f"RuntimeStateProbe Assertions items schema type is not object: {items}")
        return {"type": assertions.get("type"), "itemType": items.get("type")}

    def step_discover_ping(self):
        data = self.tool("UniBridge_Discover", {"Action": "Ping"})["data"] or {}
        if data.get("connected") is not True or not prop(data, "package", "version"):
            raise AssertionError(f"UniBridge ping did not return a connected package identity: {data}")
        return {"packageVersion": prop(data, "package", "version"), "projectPath": prop(data, "unity", "projectPath"), "connected": prop(data, "connected")}

    def step_clear_console(self):
        data = self.tool("UniBridge_ReadConsole", {"Action": "ClearConsole"})["data"] or {}
        return {"message": prop(data, "message"), "action": prop(data, "action")}

    def step_wait_ready(self):
        data = self.tool("UniBridge_ManageEditor", {"Action": "WaitForReady", "TimeoutMs": 60000, "PollIntervalMs": 500, "RequireNotPlaying": True})["data"] or {}
        ready = prop(data, "readiness", "isReady", default=prop(data, "ready"))
        if ready is not True:
            raise AssertionError(f"Unity did not report ready state: {prop(data, 'readiness', default=data)}")
        return {"ready": ready, "readiness": prop(data, "readiness")}

    def step_validate_package_script(self):
        data = self.tool(
            "UniBridge_ValidateScript",
            {
                "Uri": "Packages/com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManageScript.Validation.cs",
                "Level": "standard",
                "IncludeDiagnostics": True,
            },
        )["data"] or {}
        errors = first_count(data, ("errors",), ("diagnostics", "errors"), ("summary", "errors"))
        assert_zero_or_missing(errors, "ValidateScript returned errors.")
        return {"message": prop(data, "message"), "errors": errors, "warnings": first_count(data, ("warnings",), ("diagnostics", "warnings"), ("summary", "warnings"))}

    def step_version_control_paths(self):
        valid_paths = [
            "Packages/com.cidonix.unibridge/package.json",
            "Packages/com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools/VersionControlTool.cs",
        ]
        missing_path = "Assets/UniBridgeSmoke/DefinitelyMissingVersionControlAsset.asset"

        with self.make_client() as client:
            tools = client.list_tools()
        tool = next((item for item in tools if item.get("name") == "UniBridge_VersionControl"), None)
        if not tool:
            raise AssertionError("UniBridge_VersionControl is missing from tools/list.")
        asset_paths_schema = prop(tool, "inputSchema", "properties", "AssetPaths", default={}) or {}
        if asset_paths_schema.get("type") != "array" or asset_paths_schema.get("minItems") != 1:
            raise AssertionError(f"VersionControl AssetPaths schema is not a non-empty array: {asset_paths_schema}")

        def require_success(result, label):
            envelope = result.get("envelope") or {}
            if envelope.get("success") is False:
                raise AssertionError(f"{label} failed: {envelope}")
            return result.get("data") or {}

        single = require_success(
            self.tool(
                "UniBridge_VersionControl",
                {"Action": "EnsureEditable", "AssetPath": valid_paths[0], "Checkout": False, "ThrowOnBlocked": True},
            ),
            "single-path EnsureEditable",
        )
        if prop(single, "count") != 1 or len(prop(single, "assets", default=[])) != 1:
            raise AssertionError(f"Single-path EnsureEditable did not return one per-asset result: {single}")

        multiple = require_success(
            self.tool(
                "UniBridge_VersionControl",
                {"Action": "EnsureEditable", "AssetPaths": valid_paths, "Checkout": False, "ThrowOnBlocked": True},
            ),
            "multi-path EnsureEditable",
        )
        if prop(multiple, "count") != 2 or len(prop(multiple, "assets", default=[])) != 2:
            raise AssertionError(f"Multi-path EnsureEditable did not return two per-asset results: {multiple}")

        checkout = require_success(
            self.tool(
                "UniBridge_VersionControl",
                {"Action": "Checkout", "AssetPaths": valid_paths, "ThrowOnBlocked": True},
            ),
            "multi-path Checkout",
        )
        if prop(checkout, "count") != 2 or len(prop(checkout, "assets", default=[])) != 2:
            raise AssertionError(f"Multi-path Checkout did not return two per-asset results: {checkout}")

        inspect_from_singular_action = require_success(
            self.tool("UniBridge_VersionControl", {"Action": "InspectAsset", "AssetPaths": valid_paths}),
            "InspectAsset with AssetPaths",
        )
        if prop(inspect_from_singular_action, "count") != 2:
            raise AssertionError(f"InspectAsset did not accept AssetPaths: {inspect_from_singular_action}")

        inspect_from_plural_action = require_success(
            self.tool("UniBridge_VersionControl", {"Action": "InspectAssets", "AssetPath": valid_paths[0]}),
            "InspectAssets with AssetPath",
        )
        if prop(inspect_from_plural_action, "count") != 1:
            raise AssertionError(f"InspectAssets did not accept AssetPath: {inspect_from_plural_action}")

        mixed = require_success(
            self.tool("UniBridge_VersionControl", {"Action": "InspectAssets", "AssetPaths": [valid_paths[0], missing_path]}),
            "partially invalid InspectAssets",
        )
        mixed_assets = prop(mixed, "assets", default=[])
        if prop(mixed, "count") != 2 or prop(mixed, "missingCount") != 1 or len(mixed_assets) != 2:
            raise AssertionError(f"Partially invalid InspectAssets did not preserve both results: {mixed}")
        if not any(item.get("assetPath") == missing_path and item.get("exists") is False for item in mixed_assets):
            raise AssertionError(f"Missing asset was not identified in the per-asset results: {mixed}")

        blocked_result = self.tool(
            "UniBridge_VersionControl",
            {"Action": "EnsureEditable", "AssetPaths": [valid_paths[0], missing_path], "Checkout": False, "ThrowOnBlocked": True},
        )
        blocked_envelope = blocked_result.get("envelope") or {}
        blocked_data = blocked_result.get("data") or {}
        if blocked_envelope.get("success") is not False or prop(blocked_data, "blockedCount") != 1:
            raise AssertionError(f"Partially invalid EnsureEditable did not return one blocked asset: {blocked_envelope}")

        empty_result = self.tool("UniBridge_VersionControl", {"Action": "EnsureEditable", "AssetPaths": []})
        empty_envelope = empty_result.get("envelope") or {}
        if empty_envelope.get("success") is not False or "at least one" not in json.dumps(empty_envelope, ensure_ascii=False).lower():
            raise AssertionError(f"Empty AssetPaths did not return a clear validation error: {empty_envelope}")

        return {
            "singleCount": prop(single, "count"),
            "multipleCount": prop(multiple, "count"),
            "checkoutCount": prop(checkout, "count"),
            "mixedMissingCount": prop(mixed, "missingCount"),
            "blockedCount": prop(blocked_data, "blockedCount"),
            "emptyArrayRejected": True,
            "assetPathsSchema": asset_paths_schema,
        }

    def step_script_anchor_edits(self):
        suffix = datetime.now().strftime("%H%M%S%f")[:9]
        script_name = "UBAnchorSmoke_" + suffix
        folder = "Assets/UniBridgeSmoke"
        script_path = f"{folder}/{script_name}.cs"
        source = (
            f"public sealed class {script_name}\n"
            "{\n"
            "    public const string LineFullyShown = \"line\";\n"
            "    public const string RemoveMe = \"remove\";\n"
            "    // ANCHOR_DUPLICATE\n"
            "    // ANCHOR_DUPLICATE\n"
            "\n"
            "    public void Update() { var marker = \"update\"; }\n"
            "    public void NormalFollowingMethod() { var marker = \"normal-following\"; }\n"
            "private void ResolveInput() { var marker = \"unindented-following\"; }\n"
            "    public void SelectBestCandidate() { var marker = \"select-best\"; }\n"
            "    public void Show() { var marker = \"show\"; }\n"
            "    public void RequireReleaseBeforeHold() { var marker = \"release\"; }\n"
            "}\n"
        )
        created = False

        def require_success(result, label):
            envelope = result.get("envelope") or result.get("data") or {}
            if isinstance(envelope, dict) and envelope.get("success") is False:
                raise AssertionError(f"{label} failed: {envelope}")
            return result.get("data") or {}

        def require_failure(result, expected_text, label):
            envelope = result.get("envelope") or {}
            if not isinstance(envelope, dict) or envelope.get("success") is not False:
                raise AssertionError(f"{label} unexpectedly succeeded: {envelope or result}")
            serialized = json.dumps(envelope, ensure_ascii=False).lower()
            if expected_text.lower() not in serialized:
                raise AssertionError(f"{label} did not report '{expected_text}': {envelope}")

        def read_script():
            result = self.tool("UniBridge_ReadResource", {"Uri": script_path})
            data = require_success(result, "ReadResource")
            text_value = prop(data, "text", default=prop(data, "Text"))
            sha_value = prop(data, "metadata", "sha256", default=prop(data, "Metadata", "Sha256"))
            if text_value is None or not sha_value:
                raise AssertionError(f"ReadResource returned incomplete script evidence: {data}")
            return text_value, sha_value

        def call_edit(edit, preview, sha):
            return self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Edits": [edit],
                    "Preview": preview,
                    "PreconditionSha256": sha,
                    "Options": {"validate": "standard", "refresh": "none"},
                },
            )

        def preview_then_apply(edit, expected_after):
            before_text, before_sha = read_script()
            preview_result = call_edit(edit, True, before_sha)
            preview_data = require_success(preview_result, f"{edit['op']} preview")
            if "preview" not in str(prop(preview_result.get("envelope") or {}, "message", default="")).lower():
                raise AssertionError(f"{edit['op']} did not identify its response as preview-only: {preview_result}")
            if expected_after not in str(prop(preview_data, "diff", default="")):
                raise AssertionError(f"{edit['op']} preview diff omitted expected text '{expected_after}': {preview_data}")
            after_preview_text, after_preview_sha = read_script()
            if after_preview_text != before_text or after_preview_sha != before_sha:
                raise AssertionError(f"{edit['op']} preview modified the script.")

            apply_result = call_edit(edit, False, before_sha)
            require_success(apply_result, f"{edit['op']} apply")
            after_text, after_sha = read_script()
            if expected_after not in after_text:
                raise AssertionError(f"{edit['op']} apply omitted expected text '{expected_after}'.")
            if after_sha == before_sha:
                raise AssertionError(f"{edit['op']} apply did not change the script SHA.")
            return after_text, after_sha

        try:
            require_success(
                self.tool("UniBridge_CreateScript", {"Path": script_path, "Contents": source}),
                "CreateScript",
            )
            created = True

            initial_text, initial_sha = read_script()

            update_only_preview = self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Preview": True,
                    "PreconditionSha256": initial_sha,
                    "Edits": [
                        {
                            "op": "replace_method",
                            "className": script_name,
                            "methodName": "Update",
                            "replacement": "public void Update()\n    {\n        var marker = \"update-only-preview\";\n    }",
                        }
                    ],
                    "Options": {"validate": "standard", "refresh": "immediate"},
                },
            )
            update_only_data = require_success(update_only_preview, "single replace_method preview")
            update_only_diff = str(prop(update_only_data, "diff", default=""))
            for unchanged_method in ("NormalFollowingMethod", "ResolveInput", "SelectBestCandidate"):
                if f"-    public void {unchanged_method}" in update_only_diff or f"+    public void {unchanged_method}" in update_only_diff:
                    raise AssertionError(f"replace_method diff falsely marked {unchanged_method} as changed: {update_only_diff}")
                if f"-private void {unchanged_method}" in update_only_diff or f"+private void {unchanged_method}" in update_only_diff:
                    raise AssertionError(f"replace_method diff falsely marked unindented {unchanged_method} as changed: {update_only_diff}")
            if prop(update_only_data, "editsApplied") != 0 or prop(update_only_data, "scheduledRefresh") is not False:
                raise AssertionError(f"Single replace_method preview was not strictly no-write: {update_only_data}")
            if read_script() != (initial_text, initial_sha):
                raise AssertionError("Single replace_method Preview changed script bytes or SHA.")

            unindented_boundary_preview = self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Preview": True,
                    "PreconditionSha256": initial_sha,
                    "Edits": [
                        {
                            "op": "replace_method",
                            "className": script_name,
                            "methodName": "NormalFollowingMethod",
                            "replacement": "public void NormalFollowingMethod()\n    {\n        var marker = \"before-unindented-preview\";\n    }",
                        }
                    ],
                    "Options": {"validate": "standard", "refresh": "none"},
                },
            )
            unindented_boundary_data = require_success(unindented_boundary_preview, "unindented method boundary preview")
            unindented_boundary_diff = str(prop(unindented_boundary_data, "diff", default=""))
            if "-private void ResolveInput" in unindented_boundary_diff or "+private void ResolveInput" in unindented_boundary_diff:
                raise AssertionError(f"replace_method crossed into an unindented following method: {unindented_boundary_diff}")
            if read_script() != (initial_text, initial_sha):
                raise AssertionError("Unindented-boundary replace_method Preview changed script bytes or SHA.")

            structured_edits = [
                {
                    "op": "replace_method",
                    "className": script_name,
                    "methodName": "Update",
                    "replacement": "public void Update()\n    {\n        var marker = \"structured-preview-update\";\n    }",
                },
                {
                    "op": "replace_method",
                    "className": script_name,
                    "methodName": "Show",
                    "replacement": "public void Show()\n    {\n        var marker = \"structured-preview-show\";\n    }",
                },
                {
                    "op": "replace_method",
                    "className": script_name,
                    "methodName": "RequireReleaseBeforeHold",
                    "replacement": "public void RequireReleaseBeforeHold()\n    {\n        var marker = \"structured-preview-release\";\n    }",
                },
            ]
            structured_preview = self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Preview": True,
                    "PreconditionSha256": initial_sha,
                    "Edits": structured_edits,
                    "Options": {"validate": "standard", "refresh": "immediate"},
                },
            )
            structured_preview_data = require_success(structured_preview, "replace_method preview")
            structured_message = str(prop(structured_preview.get("envelope") or {}, "message", default=""))
            if "previewed 3 structured edit" not in structured_message.lower() or "applied" in structured_message.lower():
                raise AssertionError(f"Structured preview returned a misleading message: {structured_message}")
            if prop(structured_preview_data, "preview") is not True:
                raise AssertionError(f"Structured preview did not identify itself as preview: {structured_preview_data}")
            if prop(structured_preview_data, "scheduledRefresh") is not False:
                raise AssertionError(f"Structured preview scheduled an AssetDatabase refresh: {structured_preview_data}")
            if prop(structured_preview_data, "editsApplied") != 0 or prop(structured_preview_data, "editsPreviewed") != 3:
                raise AssertionError(f"Structured preview reported incorrect edit counts: {structured_preview_data}")
            if prop(structured_preview_data, "currentSha256") != initial_sha:
                raise AssertionError(f"Structured preview returned the wrong current SHA: {structured_preview_data}")
            predicted_sha = prop(structured_preview_data, "predictedSha256")
            if not predicted_sha or predicted_sha == initial_sha:
                raise AssertionError(f"Structured preview did not return a changed predicted SHA: {structured_preview_data}")
            structured_diff = str(prop(structured_preview_data, "diff", default=""))
            if "structured-preview-update" not in structured_diff or "structured-preview-release" not in structured_diff:
                raise AssertionError(f"Structured preview diff omitted replacement evidence: {structured_preview_data}")
            after_structured_preview_text, after_structured_preview_sha = read_script()
            if (
                after_structured_preview_text.encode("utf-8") != initial_text.encode("utf-8") or
                after_structured_preview_sha != initial_sha
            ):
                raise AssertionError("replace_method Preview changed script bytes or SHA.")

            structured_apply = self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Preview": False,
                    "PreconditionSha256": initial_sha,
                    "Edits": structured_edits,
                    "Options": {"validate": "standard", "refresh": "none"},
                },
            )
            require_success(structured_apply, "replace_method apply")
            after_structured_apply_text, after_structured_apply_sha = read_script()
            if "structured-preview-update" not in after_structured_apply_text or after_structured_apply_sha == initial_sha:
                raise AssertionError("replace_method actual apply did not change the script and SHA.")

            structured_stale = self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Preview": True,
                    "PreconditionSha256": initial_sha,
                    "Edits": [structured_edits[0]],
                    "Options": {"validate": "standard", "refresh": "none"},
                },
            )
            require_failure(structured_stale, "stale_file", "structured stale SHA guard")
            if read_script() != (after_structured_apply_text, after_structured_apply_sha):
                raise AssertionError("A stale structured Preview modified the script.")

            initial_text, initial_sha = after_structured_apply_text, after_structured_apply_sha

            anchor_first_edits = [
                {
                    "op": "anchor_insert",
                    "anchor": "public const string LineFullyShown = \"line\";",
                    "position": "after",
                    "text": "\n    public const string AnchorFirstPreview = \"anchor-first\";",
                },
                {
                    "op": "replace_method",
                    "className": script_name,
                    "methodName": "Update",
                    "replacement": "public void Update()\n    {\n        var marker = \"anchor-first-update\";\n    }",
                },
            ]
            anchor_first_preview = self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Preview": True,
                    "PreconditionSha256": initial_sha,
                    "Edits": anchor_first_edits,
                    "Options": {"validate": "standard", "refresh": "none"},
                },
            )
            anchor_first_data = require_success(anchor_first_preview, "anchor_insert then replace_method preview")
            anchor_first_diff = str(prop(anchor_first_data, "diff", default=""))
            if "AnchorFirstPreview" not in anchor_first_diff or "anchor-first-update" not in anchor_first_diff:
                raise AssertionError(f"Anchor-first mixed preview omitted an operation: {anchor_first_data}")
            if prop(anchor_first_data, "currentSha256") != initial_sha:
                raise AssertionError(f"Anchor-first mixed preview returned the wrong current SHA: {anchor_first_data}")
            if read_script() != (initial_text, initial_sha):
                raise AssertionError("Anchor-first mixed Preview modified the script.")

            replace_first_edits = [
                {
                    "op": "replace_method",
                    "className": script_name,
                    "methodName": "Update",
                    "replacement": "public void Update()\n    {\n        var marker = \"replace-first-update\";\n    }",
                },
                {
                    "op": "anchor_insert",
                    "anchor": "public const string LineFullyShown = \"line\";",
                    "position": "after",
                    "text": "\n    public const string ReplaceFirstPreview = \"replace-first\";",
                },
            ]
            replace_first_preview = self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Preview": True,
                    "PreconditionSha256": initial_sha,
                    "Edits": replace_first_edits,
                    "Options": {"validate": "standard", "refresh": "none"},
                },
            )
            replace_first_data = require_success(replace_first_preview, "replace_method then anchor_insert preview")
            replace_first_diff = str(prop(replace_first_data, "diff", default=""))
            if "replace-first-update" not in replace_first_diff or "ReplaceFirstPreview" not in replace_first_diff:
                raise AssertionError(f"Replace-first mixed preview omitted an operation: {replace_first_data}")
            if read_script() != (initial_text, initial_sha):
                raise AssertionError("Replace-first mixed Preview modified the script.")

            mixed_edits = [
                {
                    "op": "anchor_insert",
                    "anchor": "public const string LineFullyShown = \"line\";",
                    "position": "after",
                    "text": "\n    public const string MixedAppliedAnchor = \"mixed-applied\";",
                },
                {
                    "op": "replace_method",
                    "className": script_name,
                    "methodName": "Show",
                    "replacement": "public void Show()\n    {\n        var marker = \"mixed-applied-show\";\n    }",
                },
                {
                    "op": "insert_method",
                    "className": script_name,
                    "replacement": "public string MixedInsertedMethod() => LineFullyShown;",
                    "position": "end",
                },
            ]
            mixed_preview = self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Preview": True,
                    "PreconditionSha256": initial_sha,
                    "Edits": mixed_edits,
                    "Options": {"validate": "standard", "refresh": "none"},
                },
            )
            mixed_data = require_success(mixed_preview, "anchor_insert replace_method insert_method preview")
            if prop(mixed_data, "routing") != "mixed/preview" or prop(mixed_data, "executionModel") != "single_in_memory_pipeline":
                raise AssertionError(f"Mixed preview did not use the combined in-memory route: {mixed_data}")
            mixed_diff = str(prop(mixed_data, "diff", default=""))
            for expected in ("MixedAppliedAnchor", "mixed-applied-show", "MixedInsertedMethod"):
                if expected not in mixed_diff:
                    raise AssertionError(f"Mixed preview diff omitted '{expected}': {mixed_data}")
            mixed_predicted_sha = prop(mixed_data, "predictedSha256")
            if (
                prop(mixed_data, "currentSha256") != initial_sha or
                not mixed_predicted_sha or
                mixed_predicted_sha == initial_sha or
                prop(mixed_data, "editsApplied") != 0 or
                prop(mixed_data, "scheduledRefresh") is not False
            ):
                raise AssertionError(f"Mixed preview returned incomplete no-write/SHA evidence: {mixed_data}")
            if read_script() != (initial_text, initial_sha):
                raise AssertionError("Three-operation mixed Preview modified the script.")

            mixed_apply = self.tool(
                "UniBridge_ScriptApplyEdits",
                {
                    "Name": script_name,
                    "Path": folder,
                    "Preview": False,
                    "PreconditionSha256": initial_sha,
                    "Edits": mixed_edits,
                    "Options": {"validate": "standard", "refresh": "none"},
                },
            )
            mixed_apply_data = require_success(mixed_apply, "mixed Preview/apply parity")
            mixed_applied_text, mixed_applied_sha = read_script()
            if mixed_applied_sha != mixed_predicted_sha:
                raise AssertionError(
                    f"Mixed Apply SHA {mixed_applied_sha} did not match Preview prediction {mixed_predicted_sha}."
                )
            if prop(mixed_apply_data, "editsApplied") != 3 or prop(mixed_apply_data, "routing") != "mixed/sequential":
                raise AssertionError(f"Mixed Apply returned incorrect execution evidence: {mixed_apply_data}")
            for expected in ("MixedAppliedAnchor", "mixed-applied-show", "MixedInsertedMethod"):
                if expected not in mixed_applied_text:
                    raise AssertionError(f"Mixed Apply omitted '{expected}'.")

            initial_text, initial_sha = mixed_applied_text, mixed_applied_sha

            _, sha = preview_then_apply(
                {
                    "op": "anchor_replace",
                    "anchor": "public const string LineFullyShown = \"line\";",
                    "text": "public const string LineFullyShown = \"line\";\n    public const string ContinueRequested = \"continue\";",
                },
                "ContinueRequested",
            )
            _, sha = preview_then_apply(
                {
                    "op": "anchor_insert",
                    "anchor": "public const string ContinueRequested = \"continue\";",
                    "position": "after",
                    "text": "\n    public const string Inserted = \"inserted\";",
                },
                "Inserted",
            )
            before_delete_text, before_delete_sha = read_script()
            delete_edit = {
                "op": "anchor_delete",
                "anchor": "\\s*public const string RemoveMe = \"remove\";",
            }
            delete_preview = call_edit(delete_edit, True, before_delete_sha)
            delete_preview_data = require_success(delete_preview, "anchor_delete preview")
            delete_diff = str(prop(delete_preview_data, "diff", default=""))
            if "RemoveMe" not in delete_diff or "-" not in delete_diff:
                raise AssertionError(f"anchor_delete preview omitted removed declaration evidence: {delete_preview_data}")
            if read_script() != (before_delete_text, before_delete_sha):
                raise AssertionError("anchor_delete Preview modified the script.")
            delete_apply = call_edit(delete_edit, False, before_delete_sha)
            require_success(delete_apply, "anchor_delete apply")
            after_delete_text, after_delete_sha = read_script()
            if "RemoveMe" in after_delete_text or after_delete_sha == before_delete_sha:
                raise AssertionError("anchor_delete Apply did not remove the declaration and change the SHA.")
            final_text, final_sha = read_script()
            if "RemoveMe" in final_text:
                raise AssertionError("anchor_delete left the matched declaration in the script.")

            stale_result = call_edit(
                {"op": "anchor_insert", "anchor": "LineFullyShown", "position": "after", "text": " // stale"},
                False,
                "0" * 64,
            )
            require_failure(stale_result, "stale_file", "stale SHA guard")
            if read_script() != (final_text, final_sha):
                raise AssertionError("A stale precondition modified the script.")

            missing_result = call_edit(
                {"op": "anchor_replace", "anchor": "ANCHOR_THAT_DOES_NOT_EXIST", "text": "never"},
                True,
                final_sha,
            )
            require_failure(missing_result, "anchor not found", "missing anchor validation")

            ambiguous_result = call_edit(
                {"op": "anchor_delete", "anchor": "ANCHOR_DUPLICATE"},
                True,
                final_sha,
            )
            require_failure(ambiguous_result, "ambiguous", "ambiguous anchor validation")

            validation = self.tool(
                "UniBridge_ValidateScript",
                {"Uri": script_path, "Level": "standard", "IncludeDiagnostics": True},
            )["data"] or {}
            assert_zero_or_missing(
                first_count(validation, ("errors",), ("diagnostics", "errors"), ("summary", "errors")),
                "Anchor smoke script validation returned errors.",
            )
            return {
                "script": script_path,
                "structuredPreviewNoWrite": True,
                "structuredPreviewNoRefresh": True,
                "structuredOperations": ["replace_method"],
                "structuredEditsPreviewed": 3,
                "singleMethodDiffIsScoped": True,
                "unindentedFollowingMethodBoundaryPreserved": True,
                "structuredStaleShaRejected": True,
                "previewNoWrite": True,
                "anchorOperations": ["anchor_insert", "anchor_delete", "anchor_replace"],
                "mixedOperations": ["replace_method", "anchor_insert", "insert_method"],
                "mixedPreviewNoWrite": True,
                "mixedOperationOrdersVerified": ["anchor_then_replace", "replace_then_anchor"],
                "mixedPreviewApplyParity": True,
                "staleShaRejected": True,
                "missingAnchorRejected": True,
                "ambiguousAnchorRejected": True,
            }
        finally:
            if created:
                delete_result = self.tool("UniBridge_DeleteScript", {"Uri": script_path})
                require_success(delete_result, "DeleteScript cleanup")

    def step_asset_read_text(self):
        data = self.tool(
            "UniBridge_AssetIntelligence",
            {"Action": "ReadText", "Path": "Packages/com.cidonix.unibridge/package.json", "StartLine": 1, "LineCount": 20, "MaxTextChars": 6000},
        )["data"] or {}
        return {"message": prop(data, "message"), "path": prop(data, "path"), "lineCount": prop(data, "lineCount")}

    def step_context_snapshot(self):
        data = self.tool(
            "UniBridge_ContextSnapshot",
            {
                "Depth": "Brief",
                "IncludeConsole": True,
                "ConsoleSummaryMode": "Compact",
                "IncludeTools": False,
                "IncludeProjectRoots": True,
                "IncludePackageDependencies": False,
                "MaxConsoleIssues": 10,
                "MaxAssets": 20,
            },
        )["data"] or {}
        return {"message": prop(data, "message"), "depth": prop(data, "snapshot", "depth"), "hints": prop(data, "hints")}

    def step_scene_hierarchy(self):
        data = self.tool(
            "UniBridge_SceneObjectView",
            {
                "Action": "Hierarchy",
                "Detail": "Brief",
                "IncludeInactive": True,
                "IncludeStructured": False,
                "IncludeFlattened": True,
                "MaxDepth": 1,
                "MaxRoots": 40,
                "MaxObjects": 120,
            },
        )["data"] or {}
        return {"message": prop(data, "message"), "totalReturned": prop(data, "totalReturned"), "truncatedByCount": prop(data, "truncatedByCount")}

    def step_batch_scene_hierarchy_paths(self):
        stamp = datetime.now().strftime("%H%M%S%f")[:10]
        root_name = f"<<UB_BATCH_SCENE_{stamp}>>"
        target_name = "Target"
        sibling_name = "Sibling"
        mover_name = "Mover"
        root_path = f"/{root_name}"
        target_path = f"{root_path}/{target_name}"
        sibling_path = f"{root_path}/{sibling_name}"
        mover_path = f"{root_path}/{mover_name}"

        def require_success(result, label):
            envelope = result.get("envelope") or result.get("data") or {}
            if isinstance(envelope, dict) and envelope.get("success") is False:
                raise AssertionError(f"{label} failed: {envelope}")
            return result.get("data") or {}

        def create(name, parent=None):
            args = {"Action": "Create", "Name": name}
            if parent:
                args["Parent"] = parent
            require_success(self.tool("UniBridge_ManageGameObject", args), f"Create {name}")

        def batch(dry_run, include_impact, rollback_assets, steps, label):
            return require_success(
                self.tool(
                    "UniBridge_BatchActions",
                    {
                        "Name": label,
                        "DryRun": dry_run,
                        "IncludeImpact": include_impact,
                        "IncludeWorkSessionReview": False,
                        "RollbackOnFailure": True,
                        "RollbackAssets": rollback_assets,
                        "Steps": steps,
                    },
                    timeout=self.args.reload_timeout_seconds,
                ),
                label,
            )

        try:
            create(root_name)
            create(target_name, root_path)
            create(sibling_name, root_path)
            create(mover_name, root_path)

            dry_run_steps = [
                {
                    "id": "hierarchy-target",
                    "tool": "game_object",
                    "parameters": {
                        "Action": "Modify",
                        "Target": target_path,
                        "SearchMethod": "ByPath",
                        "Position": [1.25, 2.5, 0.0],
                        "Targets": [target_path, sibling_path],
                        "ReferenceProbe": {"find": mover_path, "method": "by_path"},
                        "ReferenceAsset": "Assets/UniBridgeSmoke",
                    },
                },
                {
                    "id": "hierarchy-placement",
                    "tool": "game_object",
                    "parameters": {
                        "Action": "Modify",
                        "Target": mover_path,
                        "SearchMethod": "ByPath",
                        "Parent": root_path,
                        "Sibling": sibling_path,
                        "Placement": "Before",
                    },
                },
            ]
            dry_run = batch(True, True, True, dry_run_steps, "Hierarchy path impact dry-run")
            impact = prop(dry_run, "impact", default={}) or {}
            scene_references = prop(impact, "sceneObjectReferences", default=[]) or []
            normalized_references = {str(value).lower() for value in scene_references}
            for expected in (root_path, target_path, sibling_path, mover_path):
                if expected.lower() not in normalized_references:
                    raise AssertionError(f"Scene hierarchy reference was not classified: {expected}; got {scene_references}")

            asset_items = prop(impact, "assets", "items", default=[]) or []
            asset_paths = {
                str(item.get("path")).lower()
                for item in asset_items
                if isinstance(item, dict) and item.get("path")
            }
            if any("<<ub_batch_scene_" in path for path in asset_paths):
                raise AssertionError(f"Hierarchy references leaked into asset impact: {sorted(asset_paths)}")
            if "assets/unibridgesmoke" not in asset_paths:
                raise AssertionError(f"Explicit Assets path was not preserved in impact: {sorted(asset_paths)}")

            step_plans = prop(impact, "steps", default=[]) or []
            for plan in step_plans:
                likely_assets = [str(path).lower() for path in (plan.get("likelyAssetPaths") or [])]
                if any("<<ub_batch_scene_" in path for path in likely_assets):
                    raise AssertionError(f"Hierarchy path leaked into per-step asset impact: {plan}")

            execution_step = {
                "id": "execute-hierarchy-target",
                "tool": "game_object",
                "parameters": {
                    "Action": "Modify",
                    "Target": target_path,
                    "SearchMethod": "ByPath",
                    "IncludeInactive": True,
                    "Active": False,
                },
            }
            executed_with_rollback = batch(False, True, True, [execution_step], "Hierarchy path execute with rollback snapshot")
            if prop(executed_with_rollback, "summary", "failed", default=0) != 0:
                raise AssertionError(f"Hierarchy path execution with rollback snapshot failed: {executed_with_rollback}")

            execution_step["parameters"]["Active"] = True
            executed_without_rollback = batch(False, False, False, [execution_step], "Hierarchy path execute without impact")
            if prop(executed_without_rollback, "summary", "failed", default=0) != 0:
                raise AssertionError(f"Hierarchy path execution without impact failed: {executed_without_rollback}")

            return {
                "rootPath": root_path,
                "sceneObjectReferenceCount": len(scene_references),
                "assetPaths": sorted(asset_paths),
                "rollbackAssetsExecutionPassed": True,
                "includeImpactFalseExecutionPassed": True,
            }
        finally:
            try:
                self.tool(
                    "UniBridge_ManageGameObject",
                    {"Action": "Delete", "Target": root_path, "SearchMethod": "ByPath"},
                )
            except Exception:
                pass

    def step_core_recipe(self):
        data = self.tool(
            "UniBridge_WorkflowRecipes",
            {"Action": "Execute", "Recipe": "RunCoreSmokeTest", "Name": "UB_McpSmoke_" + datetime.now().strftime("%H%M%S%f")[:9], "DryRun": False},
            timeout=self.args.reload_timeout_seconds,
        )["data"] or {}
        return {"message": prop(data, "message"), "recipe": prop(data, "recipe"), "batchStatus": prop(data, "batchResult", "status")}

    def step_runtime_probe_single_assertion_object(self):
        result = self.tool(
            "UniBridge_RuntimeStateProbe",
            {
                "Action": "Assert",
                "Component": "Transform",
                "SearchMethod": "ByComponent",
                "MaxTargets": 1,
                "SampleFrames": 1,
                "TimeoutMs": 5000,
                "RequirePlayMode": False,
                "SaveToFile": False,
                "ReturnSamples": False,
                "FailOnFailedAssertions": False,
                "Assertions": {
                    "name": "single_object_schema_smoke",
                    "member": "localScale.x",
                    "operator": ">=",
                    "value": -1000000,
                },
            },
        )
        data = response_payload(result["data"])
        summary = prop(data, "assertionSummary", default={}) or {}
        if summary.get("total") != 1:
            raise AssertionError(f"Single assertion object was not normalized to one assertion: {summary}")
        return {
            "message": prop(result["data"] or {}, "message"),
            "assertionTotal": summary.get("total"),
            "assertionPassed": prop(data, "passed"),
        }

    def step_runtime_probe_timeout_releases_slot(self):
        error_text = None
        started = time.monotonic()
        try:
            self.tool(
                "UniBridge_RuntimeStateProbe",
                {
                    "Action": "Sample",
                    "Component": "Transform",
                    "SearchMethod": "ByComponent",
                    "MaxTargets": 50,
                    "MaxComponents": 100,
                    "MaxMembers": 500,
                    "SampleFrames": 600,
                    "TimeoutMs": 120000,
                    "SchedulerTimeoutMs": 120000,
                    "RequirePlayMode": False,
                    "SaveToFile": False,
                    "ReturnSamples": True,
                },
                timeout=0.2,
            )
        except (RuntimeError, TimeoutError) as exc:
            error_text = str(exc)

        if not error_text:
            raise AssertionError("RuntimeStateProbe client-cancel regression did not cancel the MCP request before completion.")

        duration_ms = int((time.monotonic() - started) * 1000)
        if duration_ms > 12000:
            raise AssertionError(f"RuntimeStateProbe client-cancel took too long to return: {duration_ms}ms")

        # Give the editor-side cancellation/finally path a few updates to settle before checking the scheduler.
        time.sleep(1.5)
        status = response_payload(self.tool("UniBridge_ExecutionStatus", {"Action": "Snapshot", "RecentLimit": 12})["data"])
        scheduler = prop(status, "scheduler", default={}) or {}
        active_readers = prop(scheduler, "activeReaders", default=-1)
        active_operations = prop(scheduler, "activeOperations", default=[]) or []
        recent_operations = prop(scheduler, "recentOperations", default=[]) or []
        active_runtime_probes = [
            operation for operation in active_operations
            if isinstance(operation, dict) and operation.get("tool") == "UniBridge_RuntimeStateProbe"
        ]
        recent_runtime_probes = [
            operation for operation in recent_operations
            if isinstance(operation, dict) and operation.get("tool") == "UniBridge_RuntimeStateProbe"
        ]

        if active_readers != 0:
            # One manual reap should recover genuinely stale legacy state, but a fresh cancellation should not need it.
            reaped = response_payload(self.tool("UniBridge_ExecutionStatus", {"Action": "ReapStale", "RecentLimit": 12, "GraceMs": 0})["data"])
            scheduler = prop(prop(reaped, "result", default={}) or {}, "scheduler", default={}) or {}
            active_readers = prop(scheduler, "activeReaders", default=-1)
            active_operations = prop(scheduler, "activeOperations", default=[]) or []
            active_runtime_probes = [
                operation for operation in active_operations
                if isinstance(operation, dict) and operation.get("tool") == "UniBridge_RuntimeStateProbe"
            ]

        if active_readers != 0:
            raise AssertionError(f"Scheduler still has activeReaders={active_readers} after client-canceled/timed-out RuntimeStateProbe.")
        if active_runtime_probes:
            raise AssertionError(f"Canceled RuntimeStateProbe is still active: {active_runtime_probes}")
        if not any((operation.get("outcome") in ("timedOut", "canceled")) for operation in recent_runtime_probes):
            raise AssertionError(f"No recent timedOut/canceled RuntimeStateProbe operation was recorded after client cancel: {recent_runtime_probes}")

        return {
            "clientCancelError": error_text[:300],
            "durationMs": duration_ms,
            "activeReaders": active_readers,
            "recentRuntimeProbeOutcomes": [operation.get("outcome") for operation in recent_runtime_probes[:3]],
        }

    def step_refresh_assets(self):
        data = self.tool(
            "UniBridge_ManageEditor",
            {"Action": "RefreshAssets", "WaitForCompletion": True, "Force": True, "TimeoutMs": 120000, "PollIntervalMs": 500, "RequireNotPlaying": True},
            timeout=self.args.reload_timeout_seconds,
        )["data"] or {}
        return {"message": prop(data, "message"), "reloadBoundary": prop(data, "reloadBoundary"), "reconnectRequired": prop(data, "reconnectRequired"), "ready": prop(data, "ready")}

    def step_request_compile(self):
        data = self.tool("UniBridge_ManageEditor", {"Action": "RequestScriptCompilationNoWait", "Force": True})["data"] or {}
        return {"message": prop(data, "message"), "reloadBoundary": prop(data, "reloadBoundary"), "reconnectRequired": prop(data, "reconnectRequired")}

    def step_wait_ready_after_reload(self):
        data = self.tool(
            "UniBridge_ManageEditor",
            {"Action": "WaitForReadyAfterReload", "TimeoutMs": 120000, "PollIntervalMs": 500, "RequireNotPlaying": True},
            timeout=self.args.reload_timeout_seconds,
        )["data"] or {}
        assert_compilation_healthy(data)
        ready = prop(data, "readiness", "isReady", default=prop(data, "ready"))
        if ready is not True:
            raise AssertionError(f"Unity was not ready after reload: {prop(data, 'readiness', default=data)}")
        return {"ready": ready, "readiness": prop(data, "readiness"), "compileHealth": prop(data, "compileHealth"), "buildSystemHealth": prop(data, "buildSystemHealth")}

    def step_compilation_diagnostics(self):
        data = self.tool("UniBridge_ManageEditor", {"Action": "GetCompilationDiagnostics"})["data"] or {}
        assert_compilation_healthy(data, require_evidence=True)
        return {"message": prop(data, "message"), "compileHealth": prop(data, "compileHealth"), "buildSystemHealth": prop(data, "buildSystemHealth")}

    def step_manage_game_object_serialized_component_properties(self):
        stamp = datetime.now().strftime("%H%M%S%f")[:10]
        class_name = f"UBSerializedFields_{stamp}"
        paused_class_name = f"UBPausedTimeProbe_{stamp}"
        namespace_name = "UniBridgeSmoke"
        full_type_name = f"{namespace_name}.{class_name}"
        paused_full_type_name = f"{namespace_name}.{paused_class_name}"
        folder = "Assets/UniBridgeSmoke"
        script_path = f"{folder}/{class_name}.cs"
        object_names = []
        play_mode = False
        script_created = False

        source = (
            "using UnityEngine;\n\n"
            f"namespace {namespace_name}\n"
            "{\n"
            f"    public sealed class {class_name} : MonoBehaviour\n"
            "    {\n"
            "        [SerializeField] private bool leaveOpenAfterRun;\n"
            "        [SerializeField] private float bootstrapTimeoutSeconds = 15f;\n"
            "    }\n"
            "\n"
            f"    public sealed class {paused_class_name} : MonoBehaviour\n"
            "    {\n"
            "        public float CurrentTimeScale => Time.timeScale;\n"
            "\n"
            "        private void Awake()\n"
            "        {\n"
            "            Time.timeScale = 0f;\n"
            "        }\n"
            "\n"
            "        private void OnDestroy()\n"
            "        {\n"
            "            Time.timeScale = 1f;\n"
            "        }\n"
            "    }\n"
            "}\n"
        )

        def require_success(result, label):
            envelope = result.get("envelope") or result.get("data") or {}
            if isinstance(envelope, dict) and envelope.get("success") is False:
                raise AssertionError(f"{label} failed: {envelope}")
            return result.get("data") or {}

        def require_failure(result, expected_text, label):
            envelope = result.get("envelope") or {}
            if not isinstance(envelope, dict) or envelope.get("success") is not False:
                raise AssertionError(f"{label} unexpectedly succeeded: {envelope or result}")
            serialized = json.dumps(envelope, ensure_ascii=False).lower()
            if expected_text.lower() not in serialized:
                raise AssertionError(f"{label} did not report '{expected_text}': {envelope}")
            return envelope

        def wait_after_script_change():
            self.tool(
                "UniBridge_ManageEditor",
                {
                    "Action": "RefreshAssets",
                    "WaitForCompletion": True,
                    "Force": True,
                    "TimeoutMs": 120000,
                    "PollIntervalMs": 500,
                    "RequireNotPlaying": True,
                },
                timeout=self.args.reload_timeout_seconds,
            )
            self.tool("UniBridge_ManageEditor", {"Action": "RequestScriptCompilationNoWait", "Force": True})
            ready = self.tool(
                "UniBridge_ManageEditor",
                {
                    "Action": "WaitForReadyAfterReload",
                    "TimeoutMs": 120000,
                    "PollIntervalMs": 500,
                    "RequireNotPlaying": True,
                },
                timeout=self.args.reload_timeout_seconds,
            )["data"] or {}
            assert_compilation_healthy(ready)
            diagnostics = self.tool(
                "UniBridge_ManageEditor",
                {"Action": "GetCompilationDiagnostics"},
                timeout=self.args.reload_timeout_seconds,
            )["data"] or {}
            assert_compilation_healthy(diagnostics, require_evidence=True)

        def create_and_verify(name, component_name, timeout_value, require_play_mode):
            object_names.append(name)
            created = require_success(
                self.tool(
                    "UniBridge_ManageGameObject",
                    {
                        "Action": "Create",
                        "Name": name,
                        "ComponentsToAdd": [component_name],
                        "ComponentProperties": {
                            component_name: {
                                "bootstrapTimeoutSeconds": timeout_value,
                                "leaveOpenAfterRun": True,
                            }
                        },
                    },
                ),
                f"Create {name}",
            )
            application = prop(created, "componentPropertyApplication", default={}) or {}
            if (
                prop(application, "appliedCount") != 2
                or prop(application, "skippedCount") != 0
                or prop(application, "allApplied") is not True
            ):
                raise AssertionError(f"Create did not report two verified applied properties: {application}")
            applied = prop(application, "applied", default=[]) or []
            bool_report = next(
                (item for item in applied if isinstance(item, dict) and item.get("requestedName") == "leaveOpenAfterRun"),
                None,
            )
            if not bool_report or bool_report.get("actualValue") is not True or bool_report.get("readbackVerified") is not True:
                raise AssertionError(f"Create did not verify false->true serialized bool readback: {applied}")

            asserted = require_success(
                self.tool(
                    "UniBridge_RuntimeStateProbe",
                    {
                        "Action": "Assert",
                        "Target": f"/{name}",
                        "SearchMethod": "ByPath",
                        "Component": full_type_name,
                        "Members": ["leaveOpenAfterRun", "bootstrapTimeoutSeconds"],
                        "Assertions": [
                            {"member": "leaveOpenAfterRun", "operator": "==", "value": True},
                            {
                                "member": "bootstrapTimeoutSeconds",
                                "operator": "==",
                                "value": timeout_value,
                                "tolerance": 0.001,
                            },
                        ],
                        "SampleFrames": 1,
                        "RequirePlayMode": require_play_mode,
                        "IncludeNonPublicFields": True,
                        "SaveToFile": False,
                        "ReturnSamples": True,
                    },
                    timeout=self.args.reload_timeout_seconds,
                ),
                f"Independent readback {name}",
            )
            if prop(asserted, "passed") is not True or prop(asserted, "assertionSummary", "failed") != 0:
                raise AssertionError(f"Independent RuntimeStateProbe readback failed: {asserted}")

            require_success(
                self.tool(
                    "UniBridge_ManageGameObject",
                    {
                        "Action": "Delete",
                        "Target": f"/{name}",
                        "SearchMethod": "ByPath",
                        "IncludeInactive": True,
                    },
                ),
                f"Delete {name}",
            )
            object_names.remove(name)
            return application

        def verify_rejected(name, component_properties, expected_text):
            object_names.append(name)
            result = self.tool(
                "UniBridge_ManageGameObject",
                {
                    "Action": "Create",
                    "Name": name,
                    "ComponentsToAdd": [full_type_name],
                    "ComponentProperties": component_properties,
                },
            )
            envelope = require_failure(result, expected_text, name)
            object_names.remove(name)
            return prop(envelope, "data", "skipped", default=[])

        try:
            require_success(
                self.tool("UniBridge_CreateScript", {"Path": script_path, "Contents": source}),
                "Create serialized-field probe script",
            )
            script_created = True
            wait_after_script_change()

            fqn_application = create_and_verify(
                f"__UB_ComponentProperties_Edit_FQN_{stamp}",
                full_type_name,
                21.5,
                False,
            )
            short_application = create_and_verify(
                f"__UB_ComponentProperties_Edit_Short_{stamp}",
                class_name,
                22.5,
                False,
            )

            skipped_unknown_field = verify_rejected(
                f"__UB_ComponentProperties_BadField_{stamp}",
                {full_type_name: {"missingSerializedField": True}},
                "not found",
            )
            skipped_bad_value = verify_rejected(
                f"__UB_ComponentProperties_BadValue_{stamp}",
                {full_type_name: {"leaveOpenAfterRun": "not-a-bool"}},
                "could not be set",
            )
            skipped_unknown_component = verify_rejected(
                f"__UB_ComponentProperties_BadComponent_{stamp}",
                {"UniBridgeSmoke.DoesNotExist": {"leaveOpenAfterRun": True}},
                "not found",
            )

            self.step_play_enter()
            self.step_play_wait()
            play_mode = True
            play_application = create_and_verify(
                f"__UB_ComponentProperties_Play_FQN_{stamp}",
                full_type_name,
                23.5,
                True,
            )

            paused_object_name = f"__UB_RuntimeProbe_TimeScaleZero_{stamp}"
            object_names.append(paused_object_name)
            require_success(
                self.tool(
                    "UniBridge_ManageGameObject",
                    {
                        "Action": "Create",
                        "Name": paused_object_name,
                        "ComponentsToAdd": [paused_full_type_name],
                    },
                ),
                "Create timeScale-zero runtime probe",
            )
            paused_sample = require_success(
                self.tool(
                    "UniBridge_RuntimeStateProbe",
                    {
                        "Action": "Sample",
                        "Name": "mcp_smoke_timescale_zero",
                        "Target": f"/{paused_object_name}",
                        "SearchMethod": "ByPath",
                        "Component": paused_full_type_name,
                        "Members": ["CurrentTimeScale"],
                        "SampleFrames": 180,
                        "TimeoutMs": 30000,
                        "ReturnSamples": False,
                        "SaveToFile": True,
                        "IncludeNonPublicFields": True,
                    },
                    timeout=self.args.reload_timeout_seconds,
                ),
                "Sample 180 editor ticks while Time.timeScale is zero",
            )
            paused_sampling = prop(paused_sample, "sampling", default={}) or {}
            if prop(paused_sample, "sampleRows") != 180 or prop(paused_sample, "timedOut") is not False:
                raise AssertionError(f"timeScale-zero sampling did not complete 180/180 rows: {paused_sample}")
            if paused_sampling.get("clock") != "EditorApplication.update":
                raise AssertionError(f"RuntimeStateProbe did not report the editor update clock: {paused_sampling}")
            if paused_sampling.get("observedZeroTimeScale") is not True:
                raise AssertionError(f"RuntimeStateProbe did not observe Time.timeScale == 0: {paused_sampling}")

            scheduler_status = response_payload(
                self.tool("UniBridge_ExecutionStatus", {"Action": "Snapshot", "RecentLimit": 12})["data"]
            )
            scheduler = prop(scheduler_status, "scheduler", default={}) or {}
            if prop(scheduler, "activeReaders", default=-1) != 0:
                raise AssertionError(f"RuntimeStateProbe left an active read slot after timeScale-zero sampling: {scheduler}")

            require_success(
                self.tool(
                    "UniBridge_ManageGameObject",
                    {
                        "Action": "Delete",
                        "Target": f"/{paused_object_name}",
                        "SearchMethod": "ByPath",
                        "IncludeInactive": True,
                    },
                ),
                "Delete timeScale-zero runtime probe",
            )
            object_names.remove(paused_object_name)

            self.step_play_exit()
            self.step_edit_wait()
            play_mode = False

            return {
                "componentType": full_type_name,
                "editModeFqnApplied": prop(fqn_application, "appliedCount"),
                "editModeShortApplied": prop(short_application, "appliedCount"),
                "playModeFqnApplied": prop(play_application, "appliedCount"),
                "privateBoolFalseToTrueVerified": True,
                "timeScaleZeroSampleRows": prop(paused_sample, "sampleRows"),
                "timeScaleZeroSamplingClock": paused_sampling.get("clock"),
                "timeScaleZeroReadSlotReleased": True,
                "unknownFieldRejected": bool(skipped_unknown_field),
                "invalidValueRejected": bool(skipped_bad_value),
                "unknownComponentRejected": bool(skipped_unknown_component),
            }
        finally:
            if play_mode:
                try:
                    self.step_play_exit()
                    self.step_edit_wait()
                except Exception:
                    pass
            for object_name in list(object_names):
                try:
                    self.tool(
                        "UniBridge_ManageGameObject",
                        {
                            "Action": "Delete",
                            "Target": f"/{object_name}",
                            "SearchMethod": "ByPath",
                            "IncludeInactive": True,
                        },
                    )
                except Exception:
                    pass
            if script_created:
                try:
                    self.tool("UniBridge_DeleteScript", {"Uri": script_path})
                    wait_after_script_change()
                except Exception:
                    pass

    def step_ui_recipe(self):
        data = self.tool(
            "UniBridge_WorkflowRecipes",
            {"Action": "Execute", "Recipe": "RunUISmokeTest", "Name": "UB_UiSmoke_" + datetime.now().strftime("%H%M%S%f")[:9], "DryRun": False},
            timeout=self.args.reload_timeout_seconds,
        )["data"] or {}
        return {"message": prop(data, "message"), "recipe": prop(data, "recipe"), "batchStatus": prop(data, "batchResult", "status")}

    def step_prefab_stage_ui_creation(self):
        stamp = datetime.now().strftime("%H%M%S%f")[:10]
        root_name = f"UB_PrefabStageUI_{stamp}"
        prefab_path = f"Assets/UniBridgeSmoke/{root_name}.prefab"
        canvas_name = f"UB_DialogueCanvas_{stamp}"
        stage_open = False
        font_asset_path = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset"
        speaker_text = "Мама"
        dialogue_text = "Мамо, світло повернулося додому."

        def response(name, arguments, timeout=None):
            result = self.tool(name, arguments, timeout=timeout)
            return result.get("envelope") or result.get("data") or {}

        def payload(result):
            return result.get("data") if isinstance(result, dict) and "data" in result else result

        def require_success(result, operation):
            if not result.get("success"):
                raise AssertionError(f"{operation} failed: {result}")
            return payload(result)

        try:
            before_scene = require_success(
                response("UniBridge_ManageScene", {"Action": "GetHierarchy", "Depth": 0}),
                "Read ordinary scene hierarchy before Prefab Stage smoke",
            )
            before_roots = [item.get("name") for item in before_scene if isinstance(item, dict)]

            require_success(
                response("UniBridge_ManagePrefab", {"action": "create", "prefab_path": prefab_path}),
                "Create temporary prefab",
            )
            require_success(
                response("UniBridge_ManagePrefab", {"action": "open_stage", "prefab_path": prefab_path}),
                "Open temporary Prefab Stage",
            )
            stage_open = True

            modal_result = response(
                "UniBridge_ManageUI",
                {
                    "Action": "CreateElement",
                    "ElementType": "Empty",
                    "Name": "Modal Layer",
                    "Parent": root_name,
                    "CreateParentCanvas": False,
                    "EnsureEventSystem": False,
                },
            )
            modal = require_success(modal_result, "Create Prefab Stage parent").get("element") or {}
            if modal.get("scenePath") != prefab_path or modal.get("isPrefabStageObject") is not True:
                raise AssertionError(f"CreateElement escaped the Prefab Stage: {modal}")
            parent_object_id = modal.get("objectIdString")
            if not parent_object_id:
                raise AssertionError(f"CreateElement did not return objectIdString: {modal}")

            canvas_result = response(
                "UniBridge_ManageUI",
                {
                    "Action": "CreateCanvas",
                    "Name": canvas_name,
                    "Parent": f"{root_name}/Modal Layer",
                    "RenderMode": "ScreenSpaceOverlay",
                    "SortingOrder": 420,
                    "OverrideSorting": True,
                    "EnsureEventSystem": True,
                },
            )
            canvas_payload = require_success(canvas_result, "Create Canvas in Prefab Stage")
            canvas = canvas_payload.get("canvas") or {}
            parent = canvas_payload.get("parent") or {}
            prefab_stage = canvas_payload.get("prefabStage") or {}
            if canvas.get("scenePath") != prefab_path or canvas.get("isPrefabStageObject") is not True:
                raise AssertionError(f"Canvas escaped the Prefab Stage: {canvas_payload}")
            if parent.get("name") != "Modal Layer":
                raise AssertionError(f"Canvas parent is not Modal Layer: {parent}")
            if prefab_stage.get("isDirty") is not True:
                raise AssertionError(f"Prefab Stage was not marked dirty by CreateCanvas: {prefab_stage}")
            if canvas_payload.get("sortingOrder") != 420:
                raise AssertionError(f"Canvas sorting order was not applied: {canvas_payload}")
            if canvas_payload.get("eventSystem") is not None or not canvas_payload.get("eventSystemSkippedReason"):
                raise AssertionError(f"CreateCanvas created or failed to explain EventSystem handling in Prefab Stage: {canvas_payload}")

            template_payload = require_success(
                response(
                    "UniBridge_ManageUI",
                    {
                        "Action": "CreateTemplate",
                        "TemplateType": "Panel",
                        "Name": "Dialogue Panel",
                        "Parent": f"{root_name}/Modal Layer/{canvas_name}",
                        "CreateParentCanvas": False,
                        "EnsureEventSystem": False,
                    },
                ),
                "CreateTemplate in Prefab Stage",
            )
            template_root = template_payload.get("root") or {}
            if template_root.get("scenePath") != prefab_path:
                raise AssertionError(f"CreateTemplate escaped the Prefab Stage: {template_root}")

            speaker_payload = require_success(
                response(
                    "UniBridge_ManageUI",
                    {
                        "Action": "CreateElement",
                        "ElementType": "TextMeshProText",
                        "Name": "Speaker",
                        "Text": "BROKEN_SPEAKER_TEXT",
                        "FontSize": 28,
                        "FontAssetPath": font_asset_path,
                        "Parent": f"{root_name}/Modal Layer/{canvas_name}/Dialogue Panel",
                        "CreateParentCanvas": False,
                        "EnsureEventSystem": False,
                    },
                ),
                "Create Prefab Stage TMP speaker text",
            )
            speaker = speaker_payload.get("element") or {}
            if speaker.get("scenePath") != prefab_path:
                raise AssertionError(f"TMP speaker escaped the Prefab Stage: {speaker}")

            speaker_update = require_success(
                response(
                    "UniBridge_ManageUI",
                    {
                        "Action": "SetGraphic",
                        "Target": f"{root_name}/Modal Layer/{canvas_name}/Dialogue Panel/Speaker",
                        "Text": speaker_text,
                        "FontSize": 30,
                        "FontAssetPath": font_asset_path,
                        "Color": [0.95, 0.85, 0.25, 1.0],
                        "Alignment": "MiddleLeft",
                        "RichText": True,
                        "RaycastTarget": False,
                    },
                ),
                "Update Prefab Stage TMP speaker text",
            )
            speaker_after = (speaker_update.get("after") or {}).get("text") or {}
            if speaker_after.get("text") != speaker_text or abs(float(speaker_after.get("fontSize") or 0) - 30.0) > 0.01:
                raise AssertionError(f"SetGraphic did not apply TMP speaker text/font size: {speaker_update}")
            if (speaker_after.get("fontAsset") or {}).get("path") != font_asset_path:
                raise AssertionError(f"SetGraphic did not apply TMP font asset: {speaker_update}")
            if speaker_update.get("noChangesApplied") is True:
                raise AssertionError(f"SetGraphic falsely reported no TMP changes: {speaker_update}")
            if (speaker_update.get("prefabStage") or {}).get("isDirty") is not True:
                raise AssertionError(f"SetGraphic did not mark Prefab Stage dirty: {speaker_update}")

            dialogue_payload = require_success(
                response(
                    "UniBridge_ManageUI",
                    {
                        "Action": "CreateElement",
                        "ElementType": "TextMeshProText",
                        "Name": "Dialogue Text",
                        "Text": "BROKEN_DIALOGUE_TEXT",
                        "FontSize": 24,
                        "FontAssetPath": font_asset_path,
                        "Parent": f"{root_name}/Modal Layer/{canvas_name}/Dialogue Panel",
                        "CreateParentCanvas": False,
                        "EnsureEventSystem": False,
                    },
                ),
                "Create Prefab Stage TMP dialogue text",
            )
            dialogue = dialogue_payload.get("element") or {}
            if dialogue.get("scenePath") != prefab_path:
                raise AssertionError(f"TMP dialogue escaped the Prefab Stage: {dialogue}")

            dialogue_update = require_success(
                response(
                    "UniBridge_ManageUI",
                    {
                        "Action": "SetGraphic",
                        "Target": f"{root_name}/Modal Layer/{canvas_name}/Dialogue Panel/Dialogue Text",
                        "Text": dialogue_text,
                        "FontSize": 27,
                        "FontAssetPath": font_asset_path,
                        "Alignment": "UpperLeft",
                        "RichText": False,
                        "RaycastTarget": False,
                    },
                ),
                "Update Prefab Stage TMP Ukrainian dialogue text",
            )
            dialogue_after = (dialogue_update.get("after") or {}).get("text") or {}
            if dialogue_after.get("text") != dialogue_text or abs(float(dialogue_after.get("fontSize") or 0) - 27.0) > 0.01:
                raise AssertionError(f"SetGraphic did not preserve Ukrainian TMP text: {dialogue_update}")

            scroll_payload = require_success(
                response(
                    "UniBridge_ManageUI",
                    {
                        "Action": "CreateScrollView",
                        "Name": "Dialogue Scroll",
                        "Parent": f"{root_name}/Modal Layer/{canvas_name}",
                        "CreateParentCanvas": False,
                        "EnsureEventSystem": False,
                    },
                ),
                "CreateScrollView in Prefab Stage",
            )
            scroll_root = scroll_payload.get("scrollView") or {}
            if scroll_root.get("scenePath") != prefab_path:
                raise AssertionError(f"CreateScrollView escaped the Prefab Stage: {scroll_root}")

            id_child_payload = require_success(
                response(
                    "UniBridge_ManageUI",
                    {
                        "Action": "CreateElement",
                        "ElementType": "Text",
                        "Name": "ID Child",
                        "Text": "Resolved by object id",
                        "ParentObjectIdString": str(parent_object_id),
                        "CreateParentCanvas": False,
                        "EnsureEventSystem": False,
                    },
                ),
                "CreateElement by ParentObjectIdString",
            )
            id_child = id_child_payload.get("element") or {}
            if id_child.get("scenePath") != prefab_path or not (id_child.get("path") or "").endswith("/Modal Layer/ID Child"):
                raise AssertionError(f"ParentObjectIdString resolved to the wrong object: {id_child}")

            for branch_name in ("Branch A", "Branch B"):
                require_success(
                    response(
                        "UniBridge_ManageUI",
                        {
                            "Action": "CreateElement",
                            "ElementType": "Empty",
                            "Name": branch_name,
                            "Parent": root_name,
                            "CreateParentCanvas": False,
                            "EnsureEventSystem": False,
                        },
                    ),
                    f"Create {branch_name}",
                )
                require_success(
                    response(
                        "UniBridge_ManageUI",
                        {
                            "Action": "CreateElement",
                            "ElementType": "Empty",
                            "Name": "DuplicateParent",
                            "Parent": f"{root_name}/{branch_name}",
                            "CreateParentCanvas": False,
                            "EnsureEventSystem": False,
                        },
                    ),
                    f"Create duplicate parent under {branch_name}",
                )

            missing = response(
                "UniBridge_ManageUI",
                {
                    "Action": "CreateElement",
                    "ElementType": "Empty",
                    "Name": "SHOULD_NOT_EXIST_MISSING",
                    "Parent": f"{root_name}/Missing Parent",
                    "CreateParentCanvas": False,
                },
            )
            missing_payload = payload(missing)
            if missing.get("success") is not False or missing_payload.get("noObjectsCreated") is not True:
                raise AssertionError(f"Missing parent did not fail safely: {missing}")

            ambiguous = response(
                "UniBridge_ManageUI",
                {
                    "Action": "CreateElement",
                    "ElementType": "Empty",
                    "Name": "SHOULD_NOT_EXIST_AMBIGUOUS",
                    "Parent": "DuplicateParent",
                    "CreateParentCanvas": False,
                },
            )
            ambiguous_payload = payload(ambiguous)
            if ambiguous.get("success") is not False or len(ambiguous_payload.get("candidates") or []) != 2:
                raise AssertionError(f"Ambiguous parent did not fail safely: {ambiguous}")

            require_success(response("UniBridge_ManagePrefab", {"action": "save_stage"}), "Save Prefab Stage")
            yaml_payload = require_success(
                response(
                    "UniBridge_AssetIntelligence",
                    {
                        "Action": "ReadText",
                        "Path": prefab_path,
                        "MaxTextChars": 500000,
                    },
                    timeout=self.args.reload_timeout_seconds,
                ),
                "Read saved prefab YAML",
            )

            def collect_strings(value):
                if isinstance(value, str):
                    return [value]
                if isinstance(value, dict):
                    result = []
                    for item in value.values():
                        result.extend(collect_strings(item))
                    return result
                if isinstance(value, list):
                    result = []
                    for item in value:
                        result.extend(collect_strings(item))
                    return result
                return []

            yaml_text = "\n".join(collect_strings(yaml_payload))

            def contains_unicode(value):
                escaped = json.dumps(value, ensure_ascii=True)[1:-1]
                compact_yaml = "".join(yaml_text.split()).lower()
                return (
                    value in yaml_text
                    or escaped.lower() in yaml_text.lower()
                    or "".join(value.split()).lower() in compact_yaml
                    or "".join(escaped.split()).lower() in compact_yaml
                )

            if not contains_unicode(speaker_text) or not contains_unicode(dialogue_text):
                tmp_lines = [
                    line.strip()
                    for line in yaml_text.splitlines()
                    if "m_text:" in line or "m_fontSize:" in line
                ]
                raise AssertionError(
                    "Saved prefab YAML did not preserve the requested Unicode TMP strings. "
                    f"Serialized TMP samples: {tmp_lines[:12]}"
                )
            if "m_fontSize: 30" not in yaml_text or "m_fontSize: 27" not in yaml_text:
                raise AssertionError("Saved prefab YAML did not preserve the requested TMP font sizes.")

            structure = require_success(
                response(
                    "UniBridge_AssetIntelligence",
                    {
                        "Action": "Structure",
                        "Path": prefab_path,
                        "StructureMode": "List",
                        "MaxStructureDepth": 8,
                        "MaxStructureItems": 300,
                        "IncludeSerializedProperties": False,
                    },
                    timeout=self.args.reload_timeout_seconds,
                ),
                "Read saved prefab structure",
            )
            structure_text = json.dumps(structure, ensure_ascii=False)
            expected_names = [canvas_name, "Dialogue Panel", "Dialogue Scroll", "ID Child", "Speaker", "Dialogue Text"]
            missing_names = [name for name in expected_names if name not in structure_text]
            if missing_names:
                raise AssertionError(f"Saved prefab structure is missing: {missing_names}")
            if "SHOULD_NOT_EXIST" in structure_text:
                raise AssertionError("A rejected UI create operation leaked into the saved prefab.")

            require_success(response("UniBridge_ManagePrefab", {"action": "close_stage"}), "Close Prefab Stage")
            stage_open = False

            after_scene = require_success(
                response("UniBridge_ManageScene", {"Action": "GetHierarchy", "Depth": 0}),
                "Read ordinary scene hierarchy after Prefab Stage smoke",
            )
            after_roots = [item.get("name") for item in after_scene if isinstance(item, dict)]
            if before_roots != after_roots:
                raise AssertionError(f"Ordinary scene roots changed during Prefab Stage smoke: before={before_roots}, after={after_roots}")

            leak_result = require_success(
                response(
                    "UniBridge_ManageGameObject",
                    {
                        "Action": "Find",
                        "SearchMethod": "ByName",
                        "SearchTerm": canvas_name,
                        "FindAll": True,
                        "IncludeInactive": True,
                    },
                ),
                "Search ordinary scenes for leaked Canvas",
            )
            ordinary_scene_leaks = [
                item for item in leak_result
                if isinstance(item, dict) and item.get("scenePath") != prefab_path
            ]
            if ordinary_scene_leaks:
                raise AssertionError(f"Prefab Stage Canvas leaked into an ordinary scene: {ordinary_scene_leaks}")

            return {
                "prefabPath": prefab_path,
                "canvasPath": canvas.get("path"),
                "canvasScenePath": canvas.get("scenePath"),
                "canvasParent": parent.get("name"),
                "stageDirtyAfterCreate": prefab_stage.get("isDirty"),
                "stageDirtyAfterSetGraphic": (speaker_update.get("prefabStage") or {}).get("isDirty"),
                "tmpUnicodeSpeaker": speaker_after.get("text"),
                "tmpUnicodeDialogue": dialogue_after.get("text"),
                "tmpYamlVerified": True,
                "objectIdParent": parent_object_id,
                "eventSystemSkippedReason": canvas_payload.get("eventSystemSkippedReason"),
                "missingParentRejected": True,
                "ambiguousParentRejected": True,
                "ordinarySceneRootsUnchanged": True,
            }
        finally:
            if stage_open:
                try:
                    response("UniBridge_ManagePrefab", {"action": "close_stage"})
                except Exception:
                    pass
            try:
                response(
                    "UniBridge_ManageAsset",
                    {"Action": "Delete", "Path": prefab_path, "GeneratePreview": False},
                    timeout=self.args.reload_timeout_seconds,
                )
            except Exception:
                pass

    def step_asset_recipe(self):
        data = self.tool(
            "UniBridge_WorkflowRecipes",
            {"Action": "Execute", "Recipe": "RunAssetSmokeTest", "Folder": self.args.asset_folder, "Name": "mcp_smoke_asset", "MaxAssets": 2, "DryRun": False},
            timeout=self.args.reload_timeout_seconds,
        )["data"] or {}
        return {"message": prop(data, "message"), "recipe": prop(data, "recipe"), "batchStatus": prop(data, "batchResult", "status")}

    def step_play_enter(self):
        data = self.tool("UniBridge_ManageEditor", {"Action": "Play", "WaitForCompletion": True, "TimeoutMs": 120000, "PollIntervalMs": 500, "RequireNotPlaying": False}, timeout=self.args.reload_timeout_seconds)["data"] or {}
        return {"message": prop(data, "message"), "reloadBoundary": prop(data, "reloadBoundary"), "reconnectRequired": prop(data, "reconnectRequired")}

    def step_play_wait(self):
        data = self.tool("UniBridge_ManageEditor", {"Action": "WaitForPlayMode", "TimeoutMs": 120000, "PollIntervalMs": 500, "RequireNotPlaying": False}, timeout=self.args.reload_timeout_seconds)["data"] or {}
        return {"message": prop(data, "message"), "playModeState": prop(data, "playModeState")}

    def step_play_exit(self):
        data = self.tool("UniBridge_ManageEditor", {"Action": "ExitPlayMode", "WaitForCompletion": True, "TimeoutMs": 120000, "PollIntervalMs": 500, "RequireNotPlaying": False}, timeout=self.args.reload_timeout_seconds)["data"] or {}
        return {"message": prop(data, "message"), "reloadBoundary": prop(data, "reloadBoundary"), "reconnectRequired": prop(data, "reconnectRequired")}

    def step_edit_wait(self):
        data = self.tool("UniBridge_ManageEditor", {"Action": "WaitForEditMode", "TimeoutMs": 120000, "PollIntervalMs": 500, "RequireNotPlaying": True}, timeout=self.args.reload_timeout_seconds)["data"] or {}
        return {"message": prop(data, "message"), "playModeState": prop(data, "playModeState")}

    def step_console_summary(self):
        data = self.tool("UniBridge_ReadConsole", {"Action": "DiagnosticSummary", "MaxIssues": 20, "MaxSamples": 3})["data"] or {}
        assert_console_healthy(data, require_evidence=True)
        return {"totals": prop(data, "totals"), "criticalIssues": prop(data, "summary", "criticalIssues")}


def parse_args(argv):
    parser = argparse.ArgumentParser(description=SUITE_NAME)
    parser.add_argument("--project-path", default=os.environ.get("UNIBRIDGE_PROJECT_PATH"))
    parser.add_argument("--relay-path")
    parser.add_argument("--report-path")
    parser.add_argument("--default-timeout-seconds", type=int, default=60)
    parser.add_argument("--reload-timeout-seconds", type=int, default=180)
    parser.add_argument("--skip-refresh", action="store_true")
    parser.add_argument("--skip-compile", action="store_true")
    parser.add_argument("--include-play-mode", action="store_true")
    parser.add_argument("--include-ui-recipe", action="store_true")
    parser.add_argument("--include-prefab-stage-ui", action="store_true")
    parser.add_argument("--include-asset-recipe", action="store_true")
    parser.add_argument("--asset-folder", default="Assets/Sprites")
    parser.add_argument("--max-steps", type=int, default=0)
    parser.add_argument("--trace-transport-path")
    args = parser.parse_args(argv)
    if not args.project_path:
        parser.error("--project-path is required or UNIBRIDGE_PROJECT_PATH must be set.")
    return args


def main(argv):
    args = parse_args(argv)
    suite = SmokeSuite(args)
    return suite.run()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
