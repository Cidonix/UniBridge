"""Compile unchanged production snapshot safety methods with a Unity substrate."""
import hashlib
import importlib.util
import re
from pathlib import Path


def build_snapshot_production(repo, source_ref, work):
    helper_path = repo / "com.cidonix.unibridge/Tools~/EditorWaitRegression/run_editor_wait_regression.py"
    spec = importlib.util.spec_from_file_location("snapshot_production_extraction", helper_path)
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    source_path = Path("com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools/EditorSnapshotTool.cs")
    params_path = Path("com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools/Parameters/EditorSnapshotParams.cs")
    source = helper.source_text(repo, source_path, source_ref)
    params = helper.source_text(repo, params_path, source_ref)
    methods = [helper.method(source, name) for name in (
        "Restore", "BuildRestorePlan", "RestoreScenes", "RestorePrefabStage", "GetLoadedScenes", "NormalizePath")]
    for optional in ("GetTargetScenePaths", "GetScenesToClose", "EnsureCleanPrefabStageForSceneChange", "OpenSceneIfExists"):
        if re.search(r"static\s+[^\n]+\b" + optional + r"\s*\(", source):
            methods.append(helper.method(source, optional))
    classes = []
    for match in re.finditer(r"^\s*sealed class\s+\w+[^\n]*\n\s*\{", source, re.MULTILINE):
        # Models contain no string braces or comments; bracket balance copies the
        # declarations unchanged, including the production options/defaults.
        start = source.index("{", match.start())
        depth = 0
        for end in range(start, len(source)):
            if source[end] == "{":
                depth += 1
            elif source[end] == "}":
                depth -= 1
                if depth == 0:
                    classes.append(source[match.start():end + 1])
                    break
    (work / "SnapshotProduction.cs").write_text(
        "using System; using System.Collections.Generic; using System.IO; using System.Linq;\n"
        "using Cidonix.UniBridge.MCP.Editor.Helpers;\n"
        "using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;\n"
        "using UnityEditor; using UnityEditor.SceneManagement; using UnityEngine; using UnityEngine.SceneManagement;\n"
        "namespace Cidonix.UniBridge.MCP.Editor.Tools { public static partial class EditorSnapshotTool {\n"
        + "\n".join(methods + classes) + "\n} }", encoding="utf-8")
    (work / "SnapshotParamsProduction.cs").write_text(params, encoding="utf-8")
    return {"snapshotToolSha256": hashlib.sha256(source.encode()).hexdigest(),
            "snapshotParamsSha256": hashlib.sha256(params.encode()).hexdigest(),
            "snapshotScope": "unchanged safety methods and models; Unity scene APIs and unrelated state steps substituted"}
