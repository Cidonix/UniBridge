"""Qualify strict dialog ownership matching without accessing any native window."""
import argparse
import copy
import ctypes
import hashlib
import json
import os
import time
import uuid
from pathlib import Path
from SnapshotOwnedDialog import OwnedSnapshotSaveDialogGuard, match_owned_save_dialog


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--editor-pid", type=int, help="Optionally qualify native watcher lifecycle with a fresh nonexistent RunId and no UI actions.")
    args = parser.parse_args()
    if os.name != "nt" or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error("Run from an Administrator terminal.")
    project = "H:/Repos/UnityRepos/UniBridge_Test_Project"
    run = "5943ce703d9b46229641a43fa91619a0"
    pid = 123932
    native = {"handle": 100, "class": "#32770", "title": "Moving file failed", "pid": pid,
              "visible": True, "enabled": True, "children": [
                  {"handle": 101, "parent": 100, "pid": pid, "class": "Edit", "visible": True, "enabled": True,
                   "text": f"Moving {project}/Temp/UnityTempFile-5ef5b6ff6117884429f6ccbd94b91aeb to \r\n{project}/Assets/__UniBridgeRestoreSnapshot_{run}/Extra.unity: Access is denied.\r\n\r\n"},
                  {"handle": 102, "parent": 100, "pid": pid, "class": "Button", "text": "Cancel", "visible": True, "enabled": True}]}
    cases = []

    def check(name, expected, mutation=None):
        candidate = copy.deepcopy(native)
        if mutation:
            mutation(candidate)
        matched = match_owned_save_dialog(candidate, pid, project, run)
        passed = bool(matched) == expected
        if matched:
            passed = passed and matched["button"] == 102 and matched["dialog"] == 100
        cases.append({"name": name, "passed": passed, "expectedMatch": expected, "actualMatch": bool(matched)})

    check("Verified exact owned dialog and Cancel", True)
    check("Normalized Windows path separators", True, lambda d: d["children"][0].update(text=d["children"][0]["text"].replace("/", "\\")))
    check("Accelerator Cancel label", True, lambda d: d["children"][1].update(text="&Cancel"))
    check("Other Editor PID", False, lambda d: d.update(pid=pid+1))
    check("Other dialog class", False, lambda d: d.update({"class": "OtherWindow"}))
    check("Other dialog title", False, lambda d: d.update(title="Save changes?"))
    check("Hidden dialog", False, lambda d: d.update(visible=False))
    check("Disabled dialog", False, lambda d: d.update(enabled=False))
    check("Other run destination", False, lambda d: d["children"][0].update(text=d["children"][0]["text"].replace(run, "0"*32)))
    check("Author asset destination", False, lambda d: d["children"][0].update(text=d["children"][0]["text"].replace("__UniBridgeRestoreSnapshot_" + run + "/Extra.unity", "AuthorScene.unity")))
    check("Other project destination", False, lambda d: d["children"][0].update(text=d["children"][0]["text"].replace("UniBridge_Test_Project/Assets", "Domovyk/Assets")))
    check("Other project temporary source", False, lambda d: d["children"][0].update(text=d["children"][0]["text"].replace("UniBridge_Test_Project/Temp", "Domovyk/Temp")))
    check("Destination name suffix", False, lambda d: d["children"][0].update(text=d["children"][0]["text"].replace("Extra.unity:", "Extra.unity.backup:")))
    check("Quoted arbitrary body containing owned path", False, lambda d: d["children"][0].update(text="Unrelated warning: " + d["children"][0]["text"]))
    check("Non-Edit body", False, lambda d: d["children"][0].update({"class": "Static"}))
    check("Body child from another PID", False, lambda d: d["children"][0].update(pid=pid+1))
    check("Body has another parent", False, lambda d: d["children"][0].update(parent=999))
    check("Blank Edit text", False, lambda d: d["children"][0].update(text=""))
    check("Cancel child from another PID", False, lambda d: d["children"][1].update(pid=pid+1))
    check("Cancel has another parent", False, lambda d: d["children"][1].update(parent=999))
    check("Hidden Cancel", False, lambda d: d["children"][1].update(visible=False))
    check("Disabled Cancel", False, lambda d: d["children"][1].update(enabled=False))
    check("Force Quit is never accepted", False, lambda d: d["children"][1].update(text="Force Quit"))
    check("Try Again is never accepted", False, lambda d: d["children"][1].update(text="Try Again"))
    check("Ambiguous duplicate Cancel", False, lambda d: d["children"].append(copy.deepcopy(d["children"][1])))
    check("Ambiguous duplicate body", False, lambda d: d["children"].append(copy.deepcopy(d["children"][0])))
    native_guard_evidence = None
    if args.editor_pid:
        with OwnedSnapshotSaveDialogGuard(args.editor_pid, project, uuid.uuid4().hex, timeout_seconds=1) as guard:
            time.sleep(0.1)
        native_guard_evidence = guard.evidence
        cases.append({"name": "Native exact-scope watcher stops and posts no actions for an absent run",
                      "passed": guard.evidence["threadStopped"] and not guard.evidence["errors"] and not guard.evidence["actions"]})
    failed = sum(not case["passed"] for case in cases)
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    matcher = Path(__file__).with_name("SnapshotOwnedDialog.py")
    args.report.write_text(json.dumps({"passed": failed == 0, "caseCount": len(cases), "failedCount": failed,
                                      "sourceSha256": hashlib.sha256(matcher.read_bytes()).hexdigest(),
                                      "scope": "strict pure dialog ownership matching; optional native watcher lifecycle with an absent run and no clicks",
                                      "nativeGuardEvidence": native_guard_evidence, "cases": cases}, indent=2), encoding="utf-8")
    print(json.dumps({"passed": len(cases)-failed, "failed": failed, "report": str(args.report)}))
    return int(failed != 0)


if __name__ == "__main__":
    raise SystemExit(main())
