using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class UniBridgePlayerIsolationGate
{
    [Serializable] private class Receipt
    {
        public bool passed, expectExcluded;
        public string unityVersion, scriptingBackend, buildResult, error;
        public string[] editorAssemblies, playerAssemblies, payloadAssemblies;
        public int buildErrors, buildWarnings;
    }
    public static void Build()
    {
        var output = Path.GetFullPath(Path.Combine(Application.dataPath, "../GateOutput"));
        Directory.CreateDirectory(output);
        var result = new Receipt { unityVersion = Application.unityVersion,
            expectExcluded = File.Exists(Path.Combine(Application.dataPath, "../expect-editor-only")),
            scriptingBackend = "Mono" };
        try
        {
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            result.editorAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Select(a => a.name).Where(n => n.StartsWith("Cidonix.UniBridge.")).OrderBy(n => n).ToArray();
            result.playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player).Select(a => a.name).Where(n => n.StartsWith("Cidonix.UniBridge.")).OrderBy(n => n).ToArray();
            foreach (var name in new[] {"Cidonix.UniBridge.MCP.Editor", "Cidonix.UniBridge.MCP.Runtime", "Cidonix.UniBridge.Tracing", "Cidonix.UniBridge.Toolkit.Async"})
                if (!result.editorAssemblies.Contains(name)) throw new Exception("Editor assembly missing: " + name);
            if (result.expectExcluded && result.playerAssemblies.Length != 0) throw new Exception("UniBridge still eligible for Player compilation");
            if (!result.expectExcluded && (!result.playerAssemblies.Contains("Cidonix.UniBridge.MCP.Runtime") || !result.playerAssemblies.Contains("Cidonix.UniBridge.Tracing"))) throw new Exception("Before-change eligibility trigger missing");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("QualificationCamera", typeof(Camera));
            SceneManager.MoveGameObjectToScene(camera, scene);
            if (!EditorSceneManager.SaveScene(scene, "Assets/PlayerIsolation.unity")) throw new Exception("Owned build scene save failed");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] {"Assets/PlayerIsolation.unity"},
                locationPathName = Path.Combine(output, "PlayerIsolation.exe"), target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development });
            result.buildResult = report.summary.result.ToString();
            result.buildErrors = report.summary.totalErrors;
            result.buildWarnings = report.summary.totalWarnings;
            result.payloadAssemblies = Directory.GetFiles(output, "Cidonix.UniBridge*.dll", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            if (report.summary.result != BuildResult.Succeeded || result.buildErrors != 0) throw new Exception("Player build failed");
            if (result.expectExcluded && result.payloadAssemblies.Length != 0) throw new Exception("UniBridge assembly found in Player payload");
            result.passed = true;
        }
        catch (Exception ex) { result.error = ex.ToString(); }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonUtility.ToJson(result, true));
        Debug.Log("UniBridge Player isolation qualification: " + result.passed);
        EditorApplication.Exit(result.passed ? 0 : 1);
    }
}
