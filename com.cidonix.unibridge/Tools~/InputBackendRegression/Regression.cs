using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.Tools;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using NewModule = UnityEngine.InputSystem.UI.InputSystemUIInputModule;

internal static class InputBackendRegression
{
    static readonly List<object> Checks = new List<object>();
    static int Failed;
    static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("REPORT EXPECTED_FALLBACK required.");
        foreach (var mode in new[] { InputBackendMode.Old, InputBackendMode.New, InputBackendMode.Both })
        {
            Run("serialized-" + mode + "-overrides-compile-flags", () => {
                SerializedObject.ActiveInputHandler = mode == InputBackendMode.Old ? 0 : mode == InputBackendMode.New ? 1 : 2;
                Equal(mode, InputBackendResolver.Resolve().Mode);
            });
        }
        Run("invalid-setting-does-not-guess-from-flags", () => { SerializedObject.ActiveInputHandler = 99; Equal(InputBackendMode.Unknown, InputBackendResolver.Resolve().Mode); });
        Run("unreadable-setting-does-not-guess-from-flags", () => { AssetDatabase.FailRead = true; Equal(InputBackendMode.Unknown, InputBackendResolver.Resolve().Mode); });
        Run("absent-property-uses-only-supported-fallback", () => { SerializedObject.ActiveInputHandler = null; Equal(Enum.Parse<InputBackendMode>(args[1]), InputBackendResolver.Resolve().Mode); });
        Run("absent-settings-uses-only-supported-fallback", () => { AssetDatabase.Settings = Array.Empty<UnityEngine.Object>(); Equal(Enum.Parse<InputBackendMode>(args[1]), InputBackendResolver.Resolve().Mode); });
        Run("unknown-setting-preserves-existing-module", () => {
            SerializedObject.ActiveInputHandler = 99;
            var original = SetExisting(typeof(StandaloneInputModule));
            var response = ManageUI.Run(new ManageUIParams());
            Success(response, false); Same(original, ManageUI.Existing.GetComponent<StandaloneInputModule>());
            Equal(0, Undo.Adds); Equal(0, Undo.Removes); Equal(0, Undo.Creates);
        });
        Run("unknown-setting-does-not-create-empty-event-system", () => { SerializedObject.ActiveInputHandler = 99; Success(ManageUI.Run(new ManageUIParams()), false); Equal(0, Undo.Creates); });
        Run("new-without-package-preserves-existing-module", () => {
            SerializedObject.ActiveInputHandler = 1; ManageUI.NewModuleAvailable = false;
            var original = SetExisting(typeof(StandaloneInputModule));
            Success(ManageUI.Run(new ManageUIParams()), false); Same(original, ManageUI.Existing.GetComponent<StandaloneInputModule>()); Equal(0, Undo.Removes);
        });
        Run("new-without-package-does-not-create-event-system", () => { SerializedObject.ActiveInputHandler = 1; ManageUI.NewModuleAvailable = false; Success(ManageUI.Run(new ManageUIParams()), false); Equal(0, Undo.Creates); });
        Run("old-with-package-keeps-old-module", () => {
            SerializedObject.ActiveInputHandler = 0; var original = SetExisting(typeof(StandaloneInputModule));
            Success(ManageUI.Run(new ManageUIParams()), true); Same(original, ManageUI.Existing.GetComponent<StandaloneInputModule>()); Equal(0, Undo.Adds); Equal(0, Undo.Removes);
        });
        Run("old-repairs-existing-new-module", () => {
            SerializedObject.ActiveInputHandler = 0; SetExisting(typeof(NewModule));
            Success(ManageUI.Run(new ManageUIParams()), true); Present(ManageUI.Existing.GetComponent<StandaloneInputModule>()); Equal(null, ManageUI.Existing.GetComponent<NewModule>()); Equal(1, Undo.Removes);
        });
        Run("new-repairs-existing-old-module", () => {
            SerializedObject.ActiveInputHandler = 1; SetExisting(typeof(StandaloneInputModule));
            var response = ManageUI.Run(new ManageUIParams()); Success(response, true); Present(ManageUI.Existing.GetComponent<NewModule>()); Equal(null, ManageUI.Existing.GetComponent<StandaloneInputModule>()); Equal(true, JsonSerializer.SerializeToElement(response).GetProperty("data").GetProperty("repaired").GetBoolean());
        });
        Run("failed-add-keeps-original-module", () => {
            SerializedObject.ActiveInputHandler = 1; var original = SetExisting(typeof(StandaloneInputModule)); Undo.FailAdd = true;
            Success(ManageUI.Run(new ManageUIParams()), false); Same(original, ManageUI.Existing.GetComponent<StandaloneInputModule>()); Equal(0, Undo.Removes);
        });
        foreach (var module in new[] { typeof(StandaloneInputModule), typeof(NewModule) })
            Run("both-preserves-" + module.Name, () => { var original = SetExisting(module); Success(ManageUI.Run(new ManageUIParams()), true); Same(original, ManageUI.Existing.GetComponent(module)); Equal(0, Undo.Adds); Equal(0, Undo.Removes); });
        Run("both-preserves-two-existing-compatible-modules", () => { SetExisting(typeof(StandaloneInputModule), typeof(NewModule)); Success(ManageUI.Run(new ManageUIParams()), true); Equal(0, Undo.Removes); Equal(0, Undo.Adds); });
        Run("both-without-package-adds-old-module", () => { ManageUI.NewModuleAvailable = false; SetExisting(); Success(ManageUI.Run(new ManageUIParams()), true); Present(ManageUI.Existing.GetComponent<StandaloneInputModule>()); });
        Run("dry-run-existing-does-not-repair-or-select", () => {
            SerializedObject.ActiveInputHandler = 1; var original = SetExisting(typeof(StandaloneInputModule));
            Success(ManageUI.Run(new ManageUIParams { DryRun = true, Select = true }), true); Same(original, ManageUI.Existing.GetComponent<StandaloneInputModule>()); Equal(0, Undo.Adds); Equal(0, Undo.Removes); Equal(null, Selection.activeGameObject);
        });
        Run("dry-run-new-does-not-create", () => { Success(ManageUI.Run(new ManageUIParams { DryRun = true }), true); Equal(0, Undo.Creates); });
        foreach (var setting in new[] { 0, 1, 2 })
            Run("create-uses-setting-" + setting, () => {
                SerializedObject.ActiveInputHandler = setting;
                var response = ManageUI.Run(new ManageUIParams { Select = true }); Success(response, true); Equal(1, Undo.Creates);
                Present(Selection.activeGameObject.GetComponent(setting == 0 ? typeof(StandaloneInputModule) : typeof(NewModule)));
            });
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { passed = Checks.Count - Failed, failed = Failed, checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Input backend: " + (Checks.Count - Failed) + "/" + Checks.Count + " PASS");
        return Failed == 0 ? 0 : 1;
    }
    static void Run(string name, Action action)
    {
        AssetDatabase.FailRead = false; AssetDatabase.Settings = new UnityEngine.Object[] { new PlayerSettings() }; SerializedObject.ActiveInputHandler = 2;
        ManageUI.Existing = null; ManageUI.NewModuleAvailable = true; ManageUI.Dirty = 0; Undo.FailAdd = false; Undo.Adds = Undo.Removes = Undo.Creates = 0; Selection.activeGameObject = null;
        try { action(); Checks.Add(new { name, passed = true }); }
        catch (Exception ex) { Failed++; Checks.Add(new { name, passed = false, detail = ex.ToString() }); }
    }
    static Component SetExisting(params Type[] types) { ManageUI.Existing = new GameObject("Existing EventSystem", types); return types.Length == 0 ? null : ManageUI.Existing.GetComponent(types[0]); }
    static void Success(object response, bool expected) { Equal(expected, JsonSerializer.SerializeToElement(response).GetProperty("success").GetBoolean()); }
    static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    static void Same(object expected, object actual) { if (!ReferenceEquals(expected, actual)) throw new Exception("Original component was replaced."); }
    static void Present(object value) { if (value == null) throw new Exception("Expected a component."); }
}
