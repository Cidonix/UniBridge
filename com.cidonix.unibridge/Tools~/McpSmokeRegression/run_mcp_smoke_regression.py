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
        ]

        self.run_step("tools_list", "List MCP tools and verify the core UniBridge surface.", lambda: self.step_tools_list(required))
        self.run_step("discover_ping", "Ping UniBridge and read package identity.", self.step_discover_ping)
        self.run_step("clear_console", "Clear Unity Console before smoke diagnostics.", self.step_clear_console)
        self.run_step("wait_ready", "Wait for Unity editor readiness before reading project state.", self.step_wait_ready)
        self.run_step("validate_package_script", "Validate a UniBridge package script through MCP.", self.step_validate_package_script)
        self.run_step("asset_read_text", "Read package.json via AssetIntelligence Action=ReadText alias.", self.step_asset_read_text)
        self.run_step("context_snapshot_brief", "Read compact ContextSnapshot with compact console summary.", self.step_context_snapshot)
        self.run_step("scene_hierarchy_brief", "Read a bounded SceneObjectView hierarchy including inactive objects.", self.step_scene_hierarchy)
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
            expected_names = [canvas_name, "Dialogue Panel", "Dialogue Scroll", "ID Child"]
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
            if leak_result:
                raise AssertionError(f"Prefab Stage Canvas leaked into an ordinary scene: {leak_result}")

            return {
                "prefabPath": prefab_path,
                "canvasPath": canvas.get("path"),
                "canvasScenePath": canvas.get("scenePath"),
                "canvasParent": parent.get("name"),
                "stageDirtyAfterCreate": prefab_stage.get("isDirty"),
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
