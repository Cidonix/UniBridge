"""Cancel only this run's owned Unity save-failure dialog during qualification.

The elevated watcher never dismisses generic Editor dialogs. It requires the
verified Editor PID/image, exact native dialog title/class, WM_GETTEXT destination
inside this run's owned fixture, and the direct Cancel child from that same PID.
Only one BM_CLICK is posted. No application is started and Force Quit is unused.
"""
import ctypes
import os
import re
import threading
import time
from ctypes import wintypes
from datetime import datetime, timezone
from pathlib import Path


def _utc_now():
    return datetime.now(timezone.utc).isoformat()


def _normalize(path):
    return str(path).replace("\\", "/").rstrip("/").casefold()


def match_owned_save_dialog(dialog, editor_pid, project, run_id):
    """Pure strict matcher, also usable by regression cases without any UI calls."""
    root = _normalize(project)
    if dialog.get("pid") != editor_pid or dialog.get("class") != "#32770" or dialog.get("title") != "Moving file failed":
        return None
    if not dialog.get("visible") or not dialog.get("enabled"):
        return None
    destination = root + "/assets/__unibridgerestoresnapshot_" + run_id + "/extra.unity"
    body_pattern = (r"\Amoving " + re.escape(root + "/temp/unitytempfile-") + r"[a-f0-9]{32} to\s+"
                    + re.escape(destination) + r": access is denied\.\s*\Z")
    edits = [child for child in dialog.get("children", []) if child.get("class") == "Edit"
             and child.get("parent") == dialog.get("handle") and child.get("pid") == editor_pid
             and re.fullmatch(body_pattern, child.get("text", "").replace("\\", "/").casefold())]
    buttons = [child for child in dialog.get("children", []) if child.get("class") == "Button"
               and child.get("text") in ("Cancel", "&Cancel") and child.get("parent") == dialog.get("handle")
               and child.get("pid") == editor_pid and child.get("visible") and child.get("enabled")]
    if len(edits) != 1 or len(buttons) != 1:
        return None
    return {"dialog": dialog["handle"], "button": buttons[0]["handle"], "title": dialog["title"],
            "body": edits[0]["text"], "editorPid": editor_pid, "destination": destination}


class OwnedSnapshotSaveDialogGuard:
    def __init__(self, editor_pid, project, run_id, timeout_seconds=120):
        if os.name != "nt" or not ctypes.windll.shell32.IsUserAnAdmin():
            raise RuntimeError("Owned snapshot dialog qualification requires an Administrator terminal.")
        self.editor_pid = int(editor_pid)
        self.project = Path(project).resolve(strict=True)
        self.run_id = run_id
        if self.editor_pid <= 0 or not re.fullmatch(r"[a-f0-9]{32}", run_id or ""):
            raise ValueError("Expected verified Editor PID and unique 32-character lowercase hex RunId.")
        if _normalize(self.project) != "h:/repos/unityrepos/unibridge_test_project":
            raise ValueError("This owned dialog guard is restricted to UniBridge_Test_Project.")
        if not 1 <= timeout_seconds <= 120:
            raise ValueError("Dialog watcher timeout must be between 1 and 120 seconds.")
        self.timeout_seconds = timeout_seconds
        self.stop_event = threading.Event()
        self.thread = None
        self.evidence = {"administrator": True, "editorPid": self.editor_pid, "project": str(self.project),
                         "runId": self.run_id, "timeoutSeconds": timeout_seconds,
                         "actions": [], "errors": [], "startedUtc": None, "stoppedUtc": None,
                         "threadStopped": False, "cancelPolicy": "exact owned native save-failure Cancel only; maximum one post"}
        self.user32 = ctypes.WinDLL("user32", use_last_error=True)
        self.kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        self.user32.EnumWindows.argtypes = [self.enum_proc, wintypes.LPARAM]
        self.user32.EnumWindows.restype = wintypes.BOOL
        self.user32.EnumChildWindows.argtypes = [wintypes.HWND, self.enum_proc, wintypes.LPARAM]
        self.user32.EnumChildWindows.restype = wintypes.BOOL
        self.user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        self.user32.GetWindowThreadProcessId.restype = wintypes.DWORD
        self.user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
        self.user32.GetClassNameW.restype = ctypes.c_int
        self.user32.GetParent.argtypes = [wintypes.HWND]
        self.user32.GetParent.restype = wintypes.HWND
        for name in ("IsWindow", "IsWindowVisible", "IsWindowEnabled"):
            function = getattr(self.user32, name)
            function.argtypes = [wintypes.HWND]
            function.restype = wintypes.BOOL
        self.user32.SendMessageTimeoutW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM,
                                                   wintypes.LPARAM, wintypes.UINT, wintypes.UINT,
                                                   ctypes.POINTER(ctypes.c_size_t)]
        self.user32.SendMessageTimeoutW.restype = ctypes.c_size_t
        self.user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
        self.user32.PostMessageW.restype = wintypes.BOOL
        self.kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel32.OpenProcess.restype = wintypes.HANDLE
        self.kernel32.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self.kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
        self.kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel32.CloseHandle.restype = wintypes.BOOL
        self._validate_process_image()

    def _validate_process_image(self):
        process = self.kernel32.OpenProcess(0x1000, False, self.editor_pid)
        if not process:
            raise OSError(ctypes.get_last_error(), "Cannot query the verified Editor process.")
        try:
            buffer = ctypes.create_unicode_buffer(32768)
            length = wintypes.DWORD(len(buffer))
            if not self.kernel32.QueryFullProcessImageNameW(process, 0, buffer, ctypes.byref(length)):
                raise OSError(ctypes.get_last_error(), "Cannot read the verified Editor image path.")
            if Path(buffer.value).name.casefold() != "unity.exe":
                raise ValueError("The provided PID does not identify Unity.exe.")
            self.evidence["editorImagePath"] = buffer.value
        finally:
            self.kernel32.CloseHandle(process)

    def _pid(self, handle):
        pid = wintypes.DWORD()
        self.user32.GetWindowThreadProcessId(handle, ctypes.byref(pid))
        return pid.value

    def _text(self, handle):
        # Native Edit controls do not reliably expose text through GetWindowText.
        # WM_GETTEXT is bounded so a hung unrelated window cannot stall cleanup.
        buffer = ctypes.create_unicode_buffer(8192)
        result = ctypes.c_size_t()
        ok = self.user32.SendMessageTimeoutW(handle, 0x000D, len(buffer),
                                             ctypes.cast(buffer, ctypes.c_void_p).value,
                                             0x0002, 100, ctypes.byref(result))
        return buffer.value if ok else ""

    def _window(self, handle):
        name = ctypes.create_unicode_buffer(256)
        self.user32.GetClassNameW(handle, name, len(name))
        return {"handle": int(handle), "parent": int(self.user32.GetParent(handle) or 0),
                "pid": self._pid(handle), "class": name.value, "text": self._text(handle),
                "visible": bool(self.user32.IsWindowVisible(handle)), "enabled": bool(self.user32.IsWindowEnabled(handle))}

    def _dialog(self, handle):
        dialog = self._window(handle)
        dialog["title"] = dialog["text"]
        dialog["children"] = []

        @self.enum_proc
        def add_child(child, unused):
            if self.stop_event.is_set():
                return False
            if self._pid(child) == self.editor_pid:
                dialog["children"].append(self._window(child))
            return True

        self.user32.EnumChildWindows(handle, add_child, 0)
        return dialog

    def _matching_dialogs(self):
        matches = []

        @self.enum_proc
        def inspect(handle, unused):
            if self.stop_event.is_set():
                return False
            if self._pid(handle) != self.editor_pid:
                return True
            # Avoid reading contents of ordinary Editor windows. Only the exact
            # native dialog class/title enters the child-control inventory.
            name = ctypes.create_unicode_buffer(256)
            self.user32.GetClassNameW(handle, name, len(name))
            if name.value != "#32770" or self._text(handle) != "Moving file failed":
                return True
            match = match_owned_save_dialog(self._dialog(handle), self.editor_pid, self.project, self.run_id)
            if match:
                matches.append(match)
            return True

        self.user32.EnumWindows(inspect, 0)
        return matches

    def _watch(self):
        started = time.monotonic()
        try:
            while not self.stop_event.is_set() and time.monotonic() - started < self.timeout_seconds:
                matches = self._matching_dialogs()
                if len(matches) == 1:
                    candidate = matches[0]
                    # Re-read the exact body/parent/PID/button immediately before
                    # posting. A changed/replaced modal receives no click.
                    confirmed = match_owned_save_dialog(self._dialog(candidate["dialog"]), self.editor_pid, self.project, self.run_id)
                    if confirmed and confirmed["button"] == candidate["button"] and not self.stop_event.is_set():
                        posted = bool(self.user32.PostMessageW(confirmed["button"], 0x00F5, 0, 0))
                        action = dict(confirmed, action="posted_cancel", posted=posted, utc=_utc_now(),
                                      win32Error=0 if posted else ctypes.get_last_error())
                        self.evidence["actions"].append(action)
                        if posted:
                            for _ in range(20):
                                if not self.user32.IsWindow(confirmed["dialog"]):
                                    action["dialogClosedObserved"] = True
                                    break
                                if self.stop_event.wait(0.1):
                                    break
                            action.setdefault("dialogClosedObserved", not bool(self.user32.IsWindow(confirmed["dialog"])))
                        break
                elif len(matches) > 1:
                    self.evidence["errors"].append("Multiple matching owned dialogs observed; no action taken.")
                    break
                self.stop_event.wait(0.2)
            self.evidence["watcherExpired"] = not self.stop_event.is_set() and time.monotonic() - started >= self.timeout_seconds
        except Exception as error:
            self.evidence["errors"].append(repr(error))
        finally:
            self.evidence["stoppedUtc"] = _utc_now()
            self.evidence["threadStopped"] = True

    def __enter__(self):
        if self.thread is not None:
            raise RuntimeError("An owned dialog guard cannot be started twice.")
        self.evidence["startedUtc"] = _utc_now()
        self.thread = threading.Thread(target=self._watch, name="UniBridge-owned-save-dialog", daemon=True)
        self.thread.start()
        return self

    def stop(self):
        self.stop_event.set()
        if self.thread is not None:
            self.thread.join(timeout=5)
            self.evidence["threadStopped"] = not self.thread.is_alive()
            if self.thread.is_alive():
                self.evidence["errors"].append("Owned dialog watcher did not stop within five seconds.")

    def __exit__(self, error_type, error, traceback):
        self.stop()
        return False
