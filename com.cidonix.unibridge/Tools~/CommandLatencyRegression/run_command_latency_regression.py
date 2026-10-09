"""Observe actual Unity command latency in one externally prepared window state.

The owner must explicitly approve the target test project. This Windows-only
runner reads native process/window state; it never changes focus or placement.
Actual MCP tool calls and fresh reports are confined to the approved project.
"""
import argparse
import ctypes
import hashlib
import json
import math
import os
import queue
import re
import statistics
import subprocess
import threading
import time
import uuid
from ctypes import wintypes as W
from datetime import datetime, timezone
from pathlib import Path

SNAPSHOT_ARGS = {
    "Depth": "Brief", "IncludeSelection": True, "IncludeConsole": False,
    "IncludeHierarchy": False, "IncludeAssets": False, "IncludeTools": False,
    "IncludeWindows": False, "IncludeBuildSettings": False,
    "IncludeProjectRoots": False, "IncludeProjectSettings": False,
    "IncludePackageDependencies": False, "IncludeAgentBrief": False,
}


def utc():
    return datetime.now(timezone.utc).isoformat()


def norm(value):
    if value is None or not str(value).strip():
        return ""
    text = str(value).replace("\\", "/").rstrip("/")
    return os.path.normcase(os.path.abspath(text))


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(data)
    return digest.hexdigest()


def checkpoint(report):
    path = Path(report["reportDirectory"]) / "performance-checkpoint.json"
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


def identity_preflight(args):
    path = args.project / "ProjectSettings/UniBridge/project.json"
    identity = json.loads(path.read_text(encoding="utf-8-sig"))
    if identity.get("schema_version") != 1 or identity.get("project_id") != args.project_id or identity.get("project_name") != args.project.name or not isinstance(identity.get("created_date"), str) or not identity["created_date"].strip():
        raise RuntimeError("Project identity would require GetOrCreate migration/write; probe refused.")
    return sha(path)


PUMP_SOURCES = (
    "Modules/Cidonix.UniBridge.MCP.Editor/Bridge.cs",
    "Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/ToolExecutionScheduler.cs",
    "Modules/Cidonix.UniBridge.MCP.Editor/Tools/ContextSnapshot.cs",
    "Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManageEditor.cs",
    "Modules/Cidonix.UniBridge.Toolkit.Async/EditorTask.cs",
    "Modules/Cidonix.UniBridge.Toolkit.Async/EditorAsyncKeepAliveScope.cs",
)


def pump_source_hashes(args):
    package_root = Path(__file__).resolve().parents[2]
    installed = args.project / "Packages/com.cidonix.unibridge"
    shipping = {name: sha(package_root / name) for name in PUMP_SOURCES}
    target = {name: sha(installed / name) for name in PUMP_SOURCES}
    manifest = json.loads((installed / "package.json").read_text(encoding="utf-8-sig"))
    if manifest.get("name") != "com.cidonix.unibridge" or manifest.get("version") != args.package_version:
        raise RuntimeError("Installed embedded target package identity/version differs from the explicit expected version.")
    if shipping != target:
        raise RuntimeError("Shipping command-pump sources differ from the installed target; measure one frozen implementation.")
    return {"packageRoot": str(package_root), "installedPackageRoot": str(installed),
            "shipping": shipping, "installed": target, "sourceParity": True}


def redact(text):
    # No process command lines/environment are collected. Defensive stderr filter.
    return re.sub(r"(?i)((?:access[_-]?token|authorization|password|secret)\s*[=:]\s*)\S+", r"\1<redacted>", str(text))


class POINT(ctypes.Structure):
    _fields_ = [("x", W.LONG), ("y", W.LONG)]


class RECT(ctypes.Structure):
    _fields_ = [("left", W.LONG), ("top", W.LONG), ("right", W.LONG), ("bottom", W.LONG)]


class PLACEMENT(ctypes.Structure):
    _fields_ = [("length", W.UINT), ("flags", W.UINT), ("showCmd", W.UINT),
                ("min", POINT), ("max", POINT), ("normal", RECT)]


class LASTINPUT(ctypes.Structure):
    _fields_ = [("cbSize", W.UINT), ("dwTime", W.DWORD)]


class Steering(RuntimeError):
    pass


class RequestTimeout(TimeoutError):
    pass


class NativeTarget:
    def __init__(self, args, report):
        self.args, self.pid, self.report = args, args.editor_pid, report
        self.u = ctypes.WinDLL("user32", use_last_error=True)
        self.k = ctypes.WinDLL("kernel32", use_last_error=True)
        def bind(dll, name, result, args):
            f = getattr(dll, name)
            f.restype, f.argtypes = result, args
        bind(self.k, "OpenProcess", W.HANDLE, [W.DWORD, W.BOOL, W.DWORD])
        bind(self.k, "CloseHandle", W.BOOL, [W.HANDLE])
        bind(self.k, "WaitForSingleObject", W.DWORD, [W.HANDLE, W.DWORD])
        bind(self.k, "QueryFullProcessImageNameW", W.BOOL, [W.HANDLE, W.DWORD, W.LPWSTR, ctypes.POINTER(W.DWORD)])
        bind(self.k, "GetProcessTimes", W.BOOL, [W.HANDLE] + [ctypes.POINTER(W.FILETIME)] * 4)
        bind(self.u, "GetWindowThreadProcessId", W.DWORD, [W.HWND, ctypes.POINTER(W.DWORD)])
        bind(self.u, "GetWindowTextW", ctypes.c_int, [W.HWND, W.LPWSTR, ctypes.c_int])
        bind(self.u, "GetClassNameW", ctypes.c_int, [W.HWND, W.LPWSTR, ctypes.c_int])
        bind(self.u, "GetWindow", W.HWND, [W.HWND, W.UINT])
        bind(self.u, "GetForegroundWindow", W.HWND, [])
        for name in ("IsWindow", "IsWindowVisible", "IsWindowEnabled", "IsIconic", "IsZoomed"):
            bind(self.u, name, W.BOOL, [W.HWND])
        bind(self.u, "GetWindowPlacement", W.BOOL, [W.HWND, ctypes.POINTER(PLACEMENT)])
        bind(self.u, "GetLastInputInfo", W.BOOL, [ctypes.POINTER(LASTINPUT)])
        bind(self.u, "GetDpiForWindow", W.UINT, [W.HWND])
        self.cbtype = ctypes.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
        bind(self.u, "EnumWindows", W.BOOL, [self.cbtype, W.LPARAM])
        self.handle = self.k.OpenProcess(0x1000 | 0x100000, False, self.pid)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            self.created = self.process_identity()
            rows = self.windows()
            candidates = [r for r in rows if r["visible"] and not r["owner"] and r["class"] == "UnityContainerWndClass" and self.args.project.name in r["title"]]
            if len(candidates) != 1:
                self.close()
                raise RuntimeError("Expected exactly one ownerless approved project Editor root window.")
            self.hwnd, self.thread = candidates[0]["hwnd"], candidates[0]["thread"]
            self.original = self.state()
            self.original_foreground = self.foreground_identity()
            self.expected = self.original
            self.expected_foreground = self.original_foreground["hwnd"]
            self.input_token = self.last_input()
            report["nativeBaseline"] = {"processCreationFileTime": self.created, "window": self.original, "foreground": self.original_foreground}
            checkpoint(report)
            self.validate()
        except BaseException:
            self.close()
            raise

    def process_identity(self):
        if self.k.WaitForSingleObject(self.handle, 0) != 258:
            raise RuntimeError("Retained Editor process handle is no longer alive.")
        size, image = W.DWORD(32768), ctypes.create_unicode_buffer(32768)
        if not self.k.QueryFullProcessImageNameW(self.handle, 0, image, ctypes.byref(size)):
            raise ctypes.WinError(ctypes.get_last_error())
        if norm(image.value) != norm(self.args.unity_exe):
            raise RuntimeError("Exact installed Unity.exe image mismatch.")
        times = [W.FILETIME() for _ in range(4)]
        if not self.k.GetProcessTimes(self.handle, *[ctypes.byref(v) for v in times]):
            raise ctypes.WinError(ctypes.get_last_error())
        return (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime

    def window_identity(self, hwnd):
        pid = W.DWORD()
        thread = self.u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        return {"hwnd": int(hwnd or 0), "pid": pid.value, "thread": thread}

    def foreground_identity(self):
        return self.window_identity(self.u.GetForegroundWindow())

    def windows(self):
        rows = []
        def collect(hwnd, unused):
            item = self.window_identity(hwnd)
            if item["pid"] == self.pid:
                title, klass = ctypes.create_unicode_buffer(2048), ctypes.create_unicode_buffer(256)
                self.u.GetWindowTextW(hwnd, title, len(title))
                self.u.GetClassNameW(hwnd, klass, len(klass))
                rows.append(dict(item, title=title.value, **{"class": klass.value}, owner=int(self.u.GetWindow(hwnd, 4) or 0), visible=bool(self.u.IsWindowVisible(hwnd))))
            return True
        callback = self.cbtype(collect)
        if not self.u.EnumWindows(callback, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        return rows

    def placement(self):
        p = PLACEMENT()
        p.length = ctypes.sizeof(p)
        if not self.u.GetWindowPlacement(self.hwnd, ctypes.byref(p)):
            raise ctypes.WinError(ctypes.get_last_error())
        return p

    @staticmethod
    def placement_dict(p):
        return {"length": p.length, "flags": p.flags, "showCmd": p.showCmd,
                "min": [p.min.x, p.min.y], "max": [p.max.x, p.max.y],
                "normal": [p.normal.left, p.normal.top, p.normal.right, p.normal.bottom]}

    def state(self):
        return {"hwnd": self.hwnd, "pid": self.pid, "thread": self.thread,
                "placement": self.placement_dict(self.placement()), "iconic": bool(self.u.IsIconic(self.hwnd)),
                "zoomed": bool(self.u.IsZoomed(self.hwnd)), "dpi": self.u.GetDpiForWindow(self.hwnd)}

    def last_input(self):
        item = LASTINPUT(ctypes.sizeof(LASTINPUT), 0)
        if not self.u.GetLastInputInfo(ctypes.byref(item)):
            raise ctypes.WinError(ctypes.get_last_error())
        return item.dwTime

    def validate(self):
        if self.process_identity() != self.created:
            raise RuntimeError("Editor process identity changed.")
        if self.window_identity(self.hwnd) != {"hwnd": self.hwnd, "pid": self.pid, "thread": self.thread}:
            raise RuntimeError("Editor HWND identity changed.")
        if not self.u.IsWindowEnabled(self.hwnd) or any(r["visible"] and r["class"] == "#32770" for r in self.windows()):
            raise RuntimeError("Unexpected modal dialog; measurements refused.")

    def guard(self):
        if time.perf_counter() >= self.report["overallDeadlineMonotonic"]:
            raise TimeoutError("Whole-run bounded deadline reached.")
        self.validate()
        if self.last_input() != self.input_token:
            raise Steering("User/session input arrived; suite stopped without fighting focus.")
        if self.state() != self.expected:
            raise Steering("Target placement/state changed outside runner ownership.")
        if self.foreground_identity()["hwnd"] != self.expected_foreground:
            raise Steering("Foreground changed outside runner ownership.")

    def wait(self, seconds):
        deadline = time.perf_counter() + seconds
        while time.perf_counter() < deadline:
            self.guard()
            time.sleep(min(0.05, max(0, deadline - time.perf_counter())))




    def audit(self):
        # Single-state controller never owns UI writes. Read-only comparison
        # cannot call placement/show/focus APIs, even when another actor steers.
        try:
            self.validate()
            state, foreground = self.state(), self.foreground_identity()
            unchanged = state == self.original and foreground == self.original_foreground
            return {"status": "unchanged" if unchanged else "preserved_external_steering", "window": state, "foreground": foreground, "uiMutationsPerformedByRunner": 0, "restoreAttempted": False}
        except Exception as exc:
            return {"status": "readonly_audit_failed", "error": redact(exc), "uiMutationsPerformedByRunner": 0, "restoreAttempted": False}

    def close(self):
        if getattr(self, "handle", None):
            self.k.CloseHandle(self.handle)
            self.handle = None


def author_manifest(args):
    rows = {}
    def scan_error(error):
        raise error
    def ensure_local(path):
        if not path.exists():
            raise RuntimeError("Required authored tree is missing: " + str(path))
        if path.is_symlink() or (getattr(path.lstat(), "st_file_attributes", 0) & 0x400):
            raise RuntimeError("Authored reparse/symlink/junction requires explicit inventory policy: " + str(path))
    for folder in ("Assets", "Packages", "ProjectSettings"):
        ensure_local(args.project / folder)
        for base, directories, files in os.walk(args.project / folder, onerror=scan_error, followlinks=False):
            ensure_local(Path(base))
            directories.sort()
            for directory in directories:
                ensure_local(Path(base, directory))
            for name in sorted(files):
                path = Path(base, name)
                ensure_local(path)
                before = path.stat()
                digest = sha(path)
                after = path.stat()
                if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                    raise RuntimeError("Authored file changed while hashing; no restoration performed.")
                rows[path.relative_to(args.project).as_posix()] = {"sha256": digest, "bytes": after.st_size}
    return rows


def discovery_match(args):
    pid = args.editor_pid
    root = Path.home() / ".unibridge/mcp/connections"
    matches = []
    for discovery_path in sorted(root.glob("*.json")):
        try:
            value = json.loads(discovery_path.read_text(encoding="utf-8-sig"))
        except (ValueError, OSError):
            continue
        project_root = value.get("project_root")
        if not project_root and isinstance(value.get("project_path"), str):
            raw_project_path = value["project_path"].replace("\\", "/").rstrip("/")
            project_root = raw_project_path[:-7] if raw_project_path.lower().endswith("/assets") else raw_project_path
        if value.get("editor_pid") == pid and isinstance(value.get("project_id"), str) and value["project_id"].replace("-", "").lower() == args.project_id and norm(project_root) == norm(args.project):
            matches.append({"sourceFile": str(discovery_path), "editorPid": pid, "projectId": args.project_id, "projectRoot": str(args.project), "connectionType": value.get("connection_type"), "connectionPath": value.get("connection_path")})
    if len(matches) != 1 or not matches[0]["connectionPath"]:
        raise RuntimeError("Expected exactly one discovery record matching approved Editor PID + project ID + root.")
    return matches[0]


class Client:
    def __init__(self, args, report, native, label):
        self.args, self.report, self.native, self.label = args, report, native, label
        self.next_id, self.messages, self.stderr, self.q = 1, [], [], queue.Queue()
        self.process, self.pending, self.closed = None, None, False
        self.readers = []

    def start(self):
        self.native.guard()
        if identity_preflight(self.args) != self.report["identityFileSha256"]:
            raise Steering("Project identity changed before owned relay launch.")
        started = time.perf_counter()
        env = os.environ.copy()
        env["UNIBRIDGE_CLIENT_NAME"] = "UniBridge Command Latency Regression"
        self.process = subprocess.Popen([str(self.args.relay), "--mcp", "--instance-id", str(self.args.editor_pid), "--project-id", self.args.project_id, "--project-path", str(self.args.project), "--name", "UniBridge Command Latency Regression"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace", bufsize=1, env=env, creationflags=subprocess.CREATE_NO_WINDOW)
        row = {"label": self.label, "pid": self.process.pid, "startedUtc": utc(), "processStartMonotonic": started, "processSpawnMs": (time.perf_counter() - started) * 1000, "administratorInheritedFromController": True, "initialize": None, "cleanup": None, "stderr": self.stderr, "messages": self.messages}
        self.report["sessions"].append(row)
        self.row = row
        def pump(stream, stderr=False):
            for line in stream:
                item = {"utc": utc(), "monotonic": time.perf_counter(), "line": redact(line.rstrip("\r\n"))}
                if stderr:
                    self.stderr.append(item)
                else:
                    self.messages.append(item)
                    self.q.put(item)
        self.readers = [threading.Thread(target=pump, args=(self.process.stdout,), daemon=True), threading.Thread(target=pump, args=(self.process.stderr, True), daemon=True)]
        for reader in self.readers:
            reader.start()
        result, attempt = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "UniBridge Command Latency Regression", "version": "1"}}, self.args.initialize_timeout, category="initialize")
        try:
            initialize_check(result)
        except BaseException:
            attempt["status"] = "validation_failed"
            raise
        attempt["processStartToInitializeMs"] = (time.perf_counter() - started) * 1000
        row["initialize"] = attempt
        self.notify("notifications/initialized")
        # Initialized schedules a lazy connection; initialize and cached status
        # do not await it. tools/list is a separately retained startup request
        # whose actual relay implementation awaits connection/catalog refresh.
        catalog_response, catalog_attempt = self.request("tools/list", {}, self.args.initialize_timeout, category="startup_catalog")
        try:
            row["catalog"] = catalog_check(catalog_response)
        except BaseException:
            catalog_attempt["status"] = "validation_failed"
            raise
        catalog_attempt["processStartToCatalogMs"] = (time.perf_counter() - started) * 1000
        row["catalogAttempt"] = catalog_attempt
        info, identity_attempt = self.call("_server_info", {"action": "status"}, measured=False)
        identity_attempt["category"] = "startup_identity"
        try:
            relay_status_check(info, self.args, row["catalog"])
        except BaseException:
            identity_attempt["status"] = "validation_failed"
            raise
        row["relayIdentity"] = info

    def notify(self, method):
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method, "params": {}}) + "\n")
        self.process.stdin.flush()

    def request(self, method, params, timeout, category):
        self.native.guard()
        if self.pending is not None:
            raise RuntimeError("Previous response remains pending; automatic retries forbidden.")
        rid = self.next_id
        self.next_id += 1
        packet = {"jsonrpc": "2.0", "id": rid, "method": method, "params": params}
        start = time.perf_counter()
        attempt = {"session": self.label, "requestId": rid, "category": category, "startedUtc": utc(), "sendMonotonic": start, "timeoutSeconds": timeout, "request": packet, "status": "pending", "windowBefore": self.native.state()}
        self.report["attempts"].append(attempt)
        if category == "initialize":
            self.row["initialize"] = attempt
        checkpoint(self.report)
        self.pending = rid
        try:
            line = json.dumps(packet, ensure_ascii=False) + "\n"
            # Do not charge observer disk/checkpoint work to measured tool RTT.
            start = time.perf_counter()
            attempt["sendMonotonic"] = start
            self.process.stdin.write(line)
            self.process.stdin.flush()
            deadline = start + timeout
            while time.perf_counter() < deadline:
                self.native.guard()
                if time.perf_counter() >= self.report["overallDeadlineMonotonic"]:
                    raise TimeoutError("Whole-run bounded deadline reached.")
                try:
                    item = self.q.get(timeout=min(0.05, max(0.001, deadline - time.perf_counter())))
                except queue.Empty:
                    if self.process.poll() is not None:
                        raise RuntimeError("Owned relay exited before response.")
                    continue
                try:
                    message = json.loads(item["line"])
                except ValueError:
                    continue
                if message.get("id") != rid:
                    continue
                self.pending = None
                attempt.update(status="received", receiveMonotonic=item["monotonic"], latencyMs=(item["monotonic"] - start) * 1000, response=message, windowAfter=self.native.state())
                checkpoint(self.report)
                if "error" in message:
                    attempt["status"] = "protocol_error"
                    raise RuntimeError("MCP protocol error; inspect retained exact response.")
                return message, attempt
            raise RequestTimeout("Actual request deadline reached; suite will stop and close this owned relay without retry.")
        except BaseException as exc:
            attempt.update(status="read_timeout" if isinstance(exc, RequestTimeout) else "abandoned_read" if self.pending is not None else attempt["status"], error=redact(exc), failureType=type(exc).__name__, elapsedAtFailureMs=(time.perf_counter() - start) * 1000)
            checkpoint(self.report)
            raise

    def call(self, name, arguments, measured=True):
        if identity_preflight(self.args) != self.report["identityFileSha256"]:
            raise Steering("Project identity changed before actual tool call.")
        values = dict(arguments, __unibridge_expected_project_root=str(self.args.project))
        response, attempt = self.request("tools/call", {"name": name, "arguments": values}, self.args.call_timeout, "measured_tool" if measured else "identity_or_baseline")
        try:
            payload = tool_payload_check(response, name, self.args)
            attempt["payload"] = payload
        except (ValueError, RuntimeError, TypeError) as exc:
            attempt["status"] = "validation_failed"
            attempt["validationError"] = redact(exc)
            raise
        self.native.guard()
        return payload, attempt

    def close(self):
        if self.closed or not self.process:
            return
        self.closed = True
        pending = self.pending
        try:
            self.process.stdin.close()
        except OSError:
            pass
        forced = False
        try:
            self.process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            forced = True
            self.process.kill()  # Popen owns retained child handle; never Editor/PID-list termination.
            self.process.wait(timeout=2)
        for reader in self.readers:
            reader.join(timeout=1)
        self.row["cleanup"] = {"utc": utc(), "exitCode": self.process.returncode, "forcedOwnedRelayOnly": forced, "pendingReadRequestAbandoned": pending, "readersStillAlive": sum(r.is_alive() for r in self.readers)}
        if self.row["cleanup"]["readersStillAlive"]:
            raise RuntimeError("Owned relay exited but stream readers have not drained; evidence incomplete.")


def tool_payload_check(response, name, args):
    result = response.get("result", {})
    texts = [entry.get("text", "") for entry in result.get("content", []) if isinstance(entry, dict) and entry.get("type") == "text"]
    if len(texts) != 1:
        raise RuntimeError("Expected one retained tool JSON text payload.")
    payload = json.loads(texts[0])
    if not isinstance(payload, dict) or result.get("isError") is True or payload.get("success") is False:
        raise RuntimeError("Actual tool returned failure; inspect retained exact response.")
    context = payload.get("projectContext")
    if name != "_server_info" and (not isinstance(context, dict) or context.get("id") != args.project_id or norm(context.get("root")) != norm(args.project)):
        raise RuntimeError("Actual tool context failed exact approved project identity confirmation.")
    return payload


def catalog_check(response):
    tools = response.get("result", {}).get("tools")
    if not isinstance(tools, list) or not tools:
        raise RuntimeError("Actual tools/list catalog is absent/empty; startup not proven.")
    names = [tool.get("name") if isinstance(tool, dict) else None for tool in tools]
    if any(not isinstance(name, str) or not name.strip() for name in names) or len(set(names)) != len(names):
        raise RuntimeError("Actual tool catalog names are missing/duplicated.")
    by_name = dict(zip(names, tools))
    for name in ("_server_info", "UniBridge_CommandStatus", "UniBridge_ContextSnapshot", "UniBridge_ManageEditor"):
        if name not in by_name:
            raise RuntimeError("Required actual relay/Unity descriptor absent: " + name)
    schema_count = sum(isinstance(tool.get("inputSchema"), dict) and tool["inputSchema"].get("type") == "object" and isinstance(tool["inputSchema"].get("properties"), dict) for tool in tools)
    if schema_count != len(tools):
        raise RuntimeError("Actual catalog contains an absent/non-object input schema.")
    properties = by_name["UniBridge_ContextSnapshot"]["inputSchema"]["properties"]
    if properties.get("Depth", {}).get("type") != "string" or "Brief" not in properties.get("Depth", {}).get("enum", []):
        raise RuntimeError("Actual ContextSnapshot descriptor does not support Brief depth.")
    for name in SNAPSHOT_ARGS:
        if name != "Depth" and properties.get(name, {}).get("type") != "boolean":
            raise RuntimeError("Actual ContextSnapshot descriptor lacks requested boolean: " + name)
    actions = by_name["UniBridge_ManageEditor"]["inputSchema"]["properties"].get("Action", {})
    if actions.get("type") != "string" or not {"GetState", "GetSelection"}.issubset(set(actions.get("enum", []))):
        raise RuntimeError("Actual ManageEditor descriptor lacks exact read actions.")
    return {"toolCount": len(tools), "inputSchemaCount": schema_count, "unityToolCount": len(tools) - 2, "toolNames": names, "handlerExecutionProvenByCatalog": False}


def relay_status_check(info, args, catalog):
    if info.get("unityConnected") is not True or type(info.get("editorPid")) is not int or info["editorPid"] != args.editor_pid or info.get("projectId") != args.project_id or norm(info.get("projectRoot")) != norm(args.project):
        raise RuntimeError("Relay status failed exact approved project identity confirmation after actual catalog startup.")
    if not isinstance(info.get("selectedConnection"), str) or not info["selectedConnection"].strip() or norm(info.get("expectedProjectRoot")) != norm(args.project):
        raise RuntimeError("Relay selected connection/expected project root missing or incorrect.")
    if type(info.get("toolCount")) is not int or info["toolCount"] != catalog["unityToolCount"]:
        raise RuntimeError("Relay status Unity tool count differs from the actual startup catalog.")


def snapshot_check(payload, args, seen, last_time, now=None):
    data = payload.get("data", {})
    if payload.get("success") is not True or data.get("project", {}).get("id") != args.project_id or norm(data.get("project", {}).get("root")) != norm(args.project):
        raise RuntimeError("Snapshot did not confirm approved project identity and success.")
    if data.get("project", {}).get("unityVersion") != args.unity_version or data.get("package", {}).get("version") != args.package_version:
        raise RuntimeError("Snapshot package/Unity version mismatch.")
    sid, created = data.get("snapshotId"), data.get("createdUtc")
    if not isinstance(sid, str) or not sid.strip() or sid in seen or not isinstance(created, str) or not created.strip():
        raise RuntimeError("Snapshot freshness nonce missing/repeated; cache/replay cannot count as execution.")
    stamp = datetime.fromisoformat(created.replace("Z", "+00:00"))
    if abs(((now or datetime.now(timezone.utc)) - stamp).total_seconds()) > args.call_timeout + 2:
        raise RuntimeError("Snapshot creation timestamp stale.")
    editor = data.get("editor", {})
    elapsed = editor.get("timeSinceStartup")
    if type(elapsed) not in (int, float) or not math.isfinite(elapsed) or elapsed < 0 or elapsed <= last_time:
        raise RuntimeError("Actual Editor startup clock failed to advance.")
    if any(editor.get(name) is not False for name in ("isPlaying", "isPlayingOrWillChangePlaymode", "isCompiling", "isUpdating")):
        raise RuntimeError("Editor is playing/transitioning/compiling/updating; latency gate invalid.")
    seen.add(sid)
    return data, elapsed


def editor_state_check(payload, last_time):
    data = payload.get("data", {})
    elapsed = data.get("TimeSinceStartup")
    if payload.get("success") is not True or type(elapsed) not in (int, float) or not math.isfinite(elapsed) or elapsed < 0 or elapsed <= last_time:
        raise RuntimeError("GetState did not contain fresh actual typed Editor state.")
    if data.get("IsReady") is not True or any(data.get(name) is not False for name in ("IsPlaying", "IsPlayingOrWillChangePlaymode", "IsCompiling", "IsUpdating")):
        raise RuntimeError("GetState readiness invalid for performance comparison.")
    return elapsed


def author_state(data):
    return {"scenes": data["scenes"], "selection": data["selection"], "prefabStage": data["prefabStage"]}


def validate_prepared_single_state(native, desired):
    native.guard()
    iconic = native.original["iconic"]
    foreground = native.original_foreground["hwnd"] == native.hwnd
    valid = (desired == "foreground" and not iconic and foreground) or (desired == "background" and not iconic and not foreground) or (desired == "minimized" and iconic and not foreground)
    if not valid:
        raise RuntimeError("Prepared actual Editor window state differs from requested --single-state; runner performs no UI correction.")


def measurement_states(native, single_state):
    validate_prepared_single_state(native, single_state)
    yield single_state


def summarize(report, threshold):
    groups = {}
    for attempt in report["attempts"]:
        if attempt.get("category") != "measured_tool":
            continue
        name = attempt["request"]["params"]["name"]
        key = (attempt.get("state"), attempt.get("clientKind"), name)
        groups.setdefault(key, []).append(attempt)
    rows = []
    for key, attempts in sorted(groups.items()):
        values = sorted(a["latencyMs"] for a in attempts if a.get("status") == "received")
        rows.append({"state": key[0], "clientKind": key[1], "tool": key[2], "attempts": len(attempts), "received": len(values), "errors": len(attempts) - len(values), "medianMs": statistics.median(values) if values else None, "p95Ms": values[max(0, math.ceil(len(values) * .95) - 1)] if values else None, "maxMs": max(values) if values else None})
    report["latencySummary"] = rows
    slow = [r for r in rows if r["state"] in ("background", "minimized") and r["tool"] == "UniBridge_ContextSnapshot" and ((r["received"] >= 3 and r["medianMs"] >= threshold) or any(a.get("state") == r["state"] and a.get("clientKind") == r["clientKind"] and a.get("category") == "measured_tool" and a.get("request", {}).get("params", {}).get("name") == "UniBridge_ContextSnapshot" and a.get("failureType") == "RequestTimeout" for a in report["attempts"]))]
    fast_fg = any(r["state"] == "foreground" and r["tool"] == "UniBridge_ContextSnapshot" and r["received"] >= 3 and r["errors"] == 0 and r["medianMs"] < threshold for r in rows)
    files = report.get("authorFileComparison", {})
    preservation = bool(
        report.get("completedMatrix") is True and report.get("errors") == []
        and report.get("pumpSourcesUnchanged") is True
        and type(files.get("beforeCount")) is int and files["beforeCount"] > 0
        and files.get("afterCount") == files["beforeCount"] and files.get("changes") == []
        and report.get("windowAudit", {}).get("status") == "unchanged"
        and isinstance(report.get("authorStateBefore"), dict)
        and report.get("authorStateAfter") == report["authorStateBefore"]
        and isinstance(report.get("fullSelectionBefore"), dict)
        and report.get("fullSelectionAfter") == report["fullSelectionBefore"])
    report["wakeEvidence"] = {"predeclaredSlowThresholdMs": threshold, "actualDelayedOrFailedMainThreadCalls": slow, "foregroundControlHealthy": fast_fg, "preservationChecksAvailable": preservation, "defectObservedBeforeProductionMutation": bool(slow and fast_fg and preservation and report["phase"] == "baseline"), "classification": "background_actual_handler_delay_observed" if slow and fast_fg else "not_proven_or_incomplete", "limits": "End-to-end actual tool RTT includes receive/pump/scheduler/tool/serialization. No numeric managed-thread identity or exclusive pump duration is measured; scheduler timing begins after pump dispatch. Ping/init/cached tools do not count. User steering, identity/readiness/schema failure and whole-run expiration do not prove a wake defect. An incomplete failed/pending matrix lacks final unsaved author-state verification and requires root's separate preservation audit before a production decision."}
    report["startupSummary"] = [{"session": session["label"], "processStartToInitializeMs": (session.get("initialize") or {}).get("processStartToInitializeMs"), "initializeRequestMs": (session.get("initialize") or {}).get("latencyMs"), "initializeStatus": (session.get("initialize") or {}).get("status", "failed_before_initialize"), "catalogRequestMs": (session.get("catalogAttempt") or {}).get("latencyMs"), "processStartToCatalogMs": (session.get("catalogAttempt") or {}).get("processStartToCatalogMs"), "catalogStatus": (session.get("catalogAttempt") or {}).get("status", "not_reached"), "actualToolCount": (session.get("catalog") or {}).get("toolCount"), "actualInputSchemaCount": (session.get("catalog") or {}).get("inputSchemaCount"), "exactRelayIdentityConfirmed": bool(session.get("relayIdentity"))} for session in report["sessions"]]


def build_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allow-test-project", action="store_true", required=True,
                        help="Owner explicitly authorizes actual read calls and report writes in this target test project.")
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--project-id", required=True)
    parser.add_argument("--unity-exe", type=Path, required=True)
    parser.add_argument("--unity-version", required=True)
    parser.add_argument("--relay", type=Path, required=True)
    parser.add_argument("--editor-pid", type=int, required=True)
    parser.add_argument("--package-version", required=True)
    parser.add_argument("--report-directory", type=Path, required=True)
    parser.add_argument("--phase", choices=["baseline", "candidate"], default="baseline")
    parser.add_argument("--single-state", choices=["foreground", "background", "minimized"], required=True,
                        help="Owner externally prepares this exact state; the runner only observes it.")
    parser.add_argument("--external-window-ready", action="store_true",
                        help="Owner opts into a nonce acknowledgment barrier after the author-file inventory.")
    parser.add_argument("--warm-repeats", type=int, default=3)
    parser.add_argument("--short-sessions", type=int, default=3)
    parser.add_argument("--idle-seconds", type=float, default=2)
    parser.add_argument("--call-timeout", type=float, default=8)
    parser.add_argument("--initialize-timeout", type=float, default=15)
    parser.add_argument("--whole-run-seconds", type=float, default=180)
    parser.add_argument("--slow-threshold-ms", type=float, default=1000)
    return parser


def validate_arguments(args):
    if not args.allow_test_project:
        raise ValueError("Explicit --allow-test-project owner opt-in is required.")
    if args.single_state not in ("foreground", "background", "minimized"):
        raise ValueError("An explicit single window state is required.")
    if not (1 <= args.warm_repeats <= 5 and 1 <= args.short_sessions <= 4
            and 0.5 <= args.idle_seconds <= 10 and 1 <= args.call_timeout <= 20
            and 1 <= args.initialize_timeout <= 30 and 30 <= args.whole_run_seconds <= 300
            and math.isfinite(args.slow_threshold_ms) and args.slow_threshold_ms > 0):
        raise ValueError("Predeclared run bounds exceeded.")
    if type(args.editor_pid) is not int or args.editor_pid <= 0:
        raise ValueError("A positive exact Editor PID is required.")
    args.project_id = args.project_id.replace("-", "").lower()
    if not re.fullmatch(r"[0-9a-f]{32}", args.project_id):
        raise ValueError("Project ID must be an explicit GUID in the current project identity file.")
    if not args.unity_version.strip() or not args.package_version.strip():
        raise ValueError("Explicit nonempty Unity/package versions are required.")
    for name in ("project", "unity_exe", "relay", "report_directory"):
        if not getattr(args, name).is_absolute():
            raise ValueError("Explicit absolute paths are required: " + name)
    args.project = args.project.resolve(strict=True)
    args.unity_exe = args.unity_exe.resolve(strict=True)
    args.relay = args.relay.resolve(strict=True)
    if not args.project.is_dir() or not args.unity_exe.is_file() or not args.relay.is_file():
        raise ValueError("Project directory, Unity executable and relay executable must already exist.")
    version = args.project / "ProjectSettings/ProjectVersion.txt"
    editor_versions = re.findall(r"^m_EditorVersion:\s*(\S+)\s*$", version.read_text(encoding="utf-8-sig"), re.MULTILINE)
    if editor_versions != [args.unity_version]:
        raise ValueError("Configured Unity version differs from the target ProjectVersion.txt.")
    identity_preflight(args)
    allowed = args.project / "Library/AgentValidation/CommandLatencyRegression"
    candidate = args.report_directory.absolute()
    if norm(candidate.parent) != norm(allowed) or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_-]{0,80}", candidate.name):
        raise ValueError("Reports require a fresh named child of the approved target Library/AgentValidation/CommandLatencyRegression.")
    if candidate.exists() or candidate.is_symlink():
        raise ValueError("Report directory exists; earlier attempts must never be overwritten.")
    current = candidate.parent
    while norm(current) != norm(args.project):
        if current.is_symlink() or (current.exists() and getattr(current.lstat(), "st_file_attributes", 0) & 0x400):
            raise ValueError("Report path traverses a reparse point; writes refused.")
        if current.parent == current:
            raise ValueError("Report path is outside the approved target.")
        current = current.parent
    args.report_directory = candidate
    return args


def initialize_check(response):
    result = response.get("result", {})
    info = result.get("serverInfo", {})
    if response.get("jsonrpc") != "2.0" or "error" in response or result.get("protocolVersion") != "2025-06-18":
        raise RuntimeError("Initialize response does not confirm the requested MCP protocol.")
    if not isinstance(result.get("capabilities", {}).get("tools"), dict) or not all(isinstance(info.get(key), str) and info[key].strip() for key in ("name", "version")):
        raise RuntimeError("Initialize response lacks actual tool capability/server identity.")
    return {"protocolVersion": result["protocolVersion"], "serverName": info["name"], "relayVersion": info["version"], "handlerExecutionProvenByInitialize": False}


def await_external_window_ready(args, report):
    token = uuid.uuid4().hex
    ready = args.report_directory / "window-ready.json"
    value = {"token": token, "state": args.single_state, "editorPid": args.editor_pid,
             "projectId": args.project_id, "projectRoot": str(args.project), "utc": utc()}
    temporary = ready.with_suffix(".tmp")
    temporary.write_text(json.dumps(value), encoding="utf-8")
    os.replace(temporary, ready)
    deadline = min(time.perf_counter() + 60, report["overallDeadlineMonotonic"])
    acknowledgment = args.report_directory / "window-go.json"
    while not acknowledgment.exists():
        if time.perf_counter() >= deadline:
            raise TimeoutError("Owner window preparation acknowledgment deadline reached.")
        time.sleep(0.05)
    proof = json.loads(acknowledgment.read_text(encoding="utf-8-sig"))
    if proof != {"token": token, "state": args.single_state, "editorPid": args.editor_pid}:
        raise RuntimeError("External acknowledgment does not match this run; caller must publish the complete JSON atomically.")
    report["externalWindowAcknowledgment"] = proof


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        validate_arguments(args)
    except (ValueError, OSError, RuntimeError) as exc:
        parser.error(str(exc))
    if os.name != "nt" or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error("The controller must be launched from an Administrator process on Windows.")
    args.report_directory.mkdir(parents=True)
    report = {"schema": "unibridge.commandLatency.actual-performance.v1", "startedUtc": utc(),
              "phase": args.phase, "singleState": args.single_state,
              "uiMutationMode": "externally_prepared_readonly_observer",
              "warmClientScope": "one warm relay retained within this single state",
              "reportDirectory": str(args.report_directory), "administrator": True,
              "ownerApprovedTestProject": args.allow_test_project, "productionMutationsPerformedByRunner": 0,
              "target": {"projectRoot": str(args.project), "projectId": args.project_id, "editorPid": args.editor_pid,
                         "unityImage": str(args.unity_exe), "unityVersion": args.unity_version, "packageVersion": args.package_version},
              "runnerSha256": sha(__file__), "relaySha256": sha(args.relay), "sessions": [], "attempts": [], "errors": [],
              "overallDeadlineMonotonic": time.perf_counter() + args.whole_run_seconds,
              "bounds": {"warmRepeats": args.warm_repeats, "shortSessions": args.short_sessions, "idleSeconds": args.idle_seconds,
                         "callTimeoutSeconds": args.call_timeout, "initializeTimeoutSeconds": args.initialize_timeout,
                         "wholeRunSeconds": args.whole_run_seconds}}
    native, clients, warm, state_baseline, hashes_before = None, [], None, None, None
    seen, last_time, current_state, client_kind = set(), -1, "baseline", "warm"
    def measured(client, tool, values):
        nonlocal last_time
        start_index = len(report["attempts"])
        try:
            payload, attempt = client.call(tool, values)
            if tool == "UniBridge_ContextSnapshot":
                data, last_time = snapshot_check(payload, args, seen, last_time)
                if author_state(data) != state_baseline:
                    raise Steering("Authored scenes/selection/prefab state changed; no restoration attempted.")
            else:
                last_time = editor_state_check(payload, last_time)
            return payload
        except BaseException:
            for attempt in report["attempts"][start_index:]:
                if attempt.get("status") == "received":
                    attempt["status"] = "validation_failed"
            raise
        finally:
            for attempt in report["attempts"][start_index:]:
                attempt.update(state=current_state, clientKind=client_kind)
    try:
        report["identityFileSha256"] = identity_preflight(args)
        report["pumpSourceHashesBefore"] = pump_source_hashes(args)
        report["discovery"] = discovery_match(args)
        hashes_before = author_manifest(args)
        (args.report_directory / "author-before.json").write_text(json.dumps(hashes_before, indent=2), encoding="utf-8")
        if args.external_window_ready:
            await_external_window_ready(args, report)
        native = NativeTarget(args, report)
        validate_prepared_single_state(native, args.single_state)
        warm = Client(args, report, native, "warm")
        clients.append(warm)
        warm.start()
        payload, unused = warm.call("UniBridge_ContextSnapshot", SNAPSHOT_ARGS, measured=False)
        baseline, last_time = snapshot_check(payload, args, seen, last_time)
        state_baseline = author_state(baseline)
        report["authorStateBefore"] = state_baseline
        selection_payload, unused = warm.call("UniBridge_ManageEditor", {"Action": "GetSelection"}, measured=False)
        if selection_payload.get("success") is not True or not isinstance(selection_payload.get("data"), dict):
            raise RuntimeError("Full author selection baseline missing.")
        report["fullSelectionBefore"] = selection_payload["data"]
        for current_state in measurement_states(native, args.single_state):
            client_kind = "warm"
            for repetition in range(args.warm_repeats):
                native.wait(args.idle_seconds)  # No project tool traffic during this idle interval.
                measured(warm, "UniBridge_ContextSnapshot", SNAPSHOT_ARGS)
                native.wait(0.02)
                measured(warm, "UniBridge_ManageEditor", {"Action": "GetState"})
            client_kind = "new_session"
            for number in range(args.short_sessions):
                native.wait(args.idle_seconds)
                client = Client(args, report, native, f"{current_state}-short-{number + 1}")
                clients.append(client)
                try:
                    client.start()
                    measured(client, "UniBridge_ContextSnapshot", SNAPSHOT_ARGS)
                    native.wait(0.02)
                    measured(client, "UniBridge_ManageEditor", {"Action": "GetState"})
                finally:
                    client.close()
        report["completedMatrix"] = True
    except BaseException as exc:
        report["errors"].append({"utc": utc(), "type": type(exc).__name__, "message": redact(exc)})
        report["completedMatrix"] = False
    finally:
        for client in clients:
            if client is warm and report.get("completedMatrix") and client.pending is None:
                continue  # Keep the healthy warm session for a final read.
            try:
                client.close()
            except Exception as exc:
                report["errors"].append({"type": "OwnedRelayCleanup", "message": redact(exc)})
        if native:
            report["windowAudit"] = native.audit()
            if report.get("completedMatrix") and not report["errors"] and report["windowAudit"]["status"] in ("unchanged",) and warm and warm.pending is None and not warm.closed:
                try:
                    native.wait(0.05)
                    payload, final_attempt = warm.call("UniBridge_ContextSnapshot", SNAPSHOT_ARGS, measured=False)
                    final_attempt["category"] = "final_author_state"
                    data, last_time = snapshot_check(payload, args, seen, last_time)
                    report["authorStateAfter"] = author_state(data)
                    if report["authorStateAfter"] != state_baseline:
                        raise Steering("Final scenes/selection/prefab state changed; no authored restoration attempted.")
                    selection_payload, selection_attempt = warm.call("UniBridge_ManageEditor", {"Action": "GetSelection"}, measured=False)
                    selection_attempt["category"] = "final_full_selection"
                    report["fullSelectionAfter"] = selection_payload.get("data")
                    if selection_payload.get("success") is not True or report["fullSelectionAfter"] != report["fullSelectionBefore"]:
                        raise Steering("Full final selection changed; no authored restoration attempted.")
                except BaseException as exc:
                    report["errors"].append({"type": "FinalAuthorAudit", "message": redact(exc)})
            else:
                report["finalAuthorAudit"] = "not attempted after failure/pending read/steering; suite remains incomplete"
            if warm:
                try:
                    warm.close()
                except Exception as exc:
                    report["errors"].append({"type": "OwnedWarmRelayCleanup", "message": redact(exc)})
            native.close()
        try:
            after = author_manifest(args)
            (args.report_directory / "author-after.json").write_text(json.dumps(after, indent=2), encoding="utf-8")
            report["authorFileComparison"] = {"beforeCount": len(hashes_before or {}), "afterCount": len(after), "changes": [name for name in sorted(set(hashes_before or {}) | set(after)) if (hashes_before or {}).get(name) != after.get(name)]}
        except Exception as exc:
            report["errors"].append({"type": "AuthorHashAudit", "message": redact(exc)})
        report["finishedUtc"] = utc()
        try:
            report["pumpSourceHashesAfter"] = pump_source_hashes(args)
            report["pumpSourcesUnchanged"] = report.get("pumpSourceHashesBefore") == report["pumpSourceHashesAfter"]
        except Exception as exc:
            report["errors"].append({"type": "SourceProvenance", "message": redact(exc)})
        summarize(report, args.slow_threshold_ms)
        passed = report.get("completedMatrix") and not report["errors"] and report.get("authorStateAfter") == state_baseline and report.get("fullSelectionBefore") == report.get("fullSelectionAfter") and report.get("pumpSourcesUnchanged") and not report.get("authorFileComparison", {}).get("changes") and report.get("windowAudit", {}).get("status") in ("unchanged",)
        report["qualification"] = "complete_measured_evidence" if passed else "incomplete_or_failed_preserved"
        path = args.report_directory / "performance-report.json"
        path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"report": str(path), "qualification": report["qualification"], "attemptCount": len(report["attempts"]), "wakeDefectProven": report["wakeEvidence"]["defectObservedBeforeProductionMutation"], "errors": report["errors"]}, ensure_ascii=False))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
