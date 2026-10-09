using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnityEngine
{
    public class Object { public string name = ""; }
    public class Component : Object { public GameObject gameObject = null!; }
    public class ScriptableObject : Object { public static T CreateInstance<T>() where T : ScriptableObject,new() => new T(); }
    public class GameObject : Object
    {
        public UnityEngine.SceneManagement.Scene scene;
        public GameObject(string value) { name=value; }
        public T? GetComponent<T>() where T:Component => null;
    }
    public struct Vector2Int { public int x,y; public Vector2Int(int x,int y) { this.x=x;this.y=y; } }
    public static class Application { public static string dataPath=""; }
    public static class Debug
    {
        public static readonly List<string> Errors=new();
        public static void LogError(object value) => Errors.Add(value?.ToString()??"");
        public static void LogWarning(object value) { }
        public static void Log(object value) { }
    }
    public enum LogType { Error,Assert,Warning,Log,Exception }
}
namespace UnityEngine.SceneManagement { public struct Scene { } }
namespace UnityEngine.UIElements
{
    [AttributeUsage(AttributeTargets.Class,Inherited=true)] public class UxmlObjectAttribute : Attribute { }
    [UxmlObject] public class DataBinding : UnityEngine.Object
    {
        public static int ConstructorCount;
        public DataBinding() { ConstructorCount++;throw new InvalidOperationException("Validation must not execute UXML object constructors."); }
    }
    public class VisualElement : UnityEngine.Object { }
    public class Label : VisualElement { }
    public class Button : VisualElement { }
    public class ScrollView : VisualElement { }
    public class TextField : VisualElement { }
    public class Toggle : VisualElement { }
    public class VisualTreeAsset : UnityEngine.ScriptableObject
    {
        public static int InstantiationCount;
        public bool importedWithErrors { get; set; }
        public bool importedWithWarnings { get; set; }
        public VisualElement CloneTree() { InstantiationCount++;return new(); }
        public VisualElement Instantiate() { InstantiationCount++;return new(); }
    }
    public class StyleSheet : UnityEngine.ScriptableObject
    {
        public bool importedWithErrors { get; set; }
        public bool importedWithWarnings { get; set; }
    }
    public enum PanelScaleMode { ConstantPixelSize,ConstantPhysicalSize,ScaleWithScreenSize }
    public enum PanelScreenMatchMode { MatchWidthOrHeight,Shrink,Expand }
    public class PanelSettings : UnityEngine.ScriptableObject
    {
        public PanelScaleMode scaleMode;
        public UnityEngine.Vector2Int referenceResolution;
        public PanelScreenMatchMode screenMatchMode;
        public float match;
    }
    public class UIDocument : UnityEngine.Component
    {
        public VisualTreeAsset? visualTreeAsset;
        public PanelSettings? panelSettings;
        public float sortingOrder;
    }
}
namespace UnityEditor.UIElements { public class Toolbar : UnityEngine.UIElements.VisualElement { } }
namespace UnityEditor
{
    [Flags] public enum ImportAssetOptions { Default=0,ForceUpdate=1,ForceSynchronousImport=2 }
    public static class AssetDatabase
    {
        public static int ImportCount,FolderCount,LoadCount,DisallowCount,AllowCount;
        public static Action<string>? OnImport;
        public static Action? OnAllowAutoRefresh;
        public static readonly Dictionary<string,UnityEngine.Object?> Assets=new(StringComparer.OrdinalIgnoreCase);
        public static readonly Dictionary<string,AssetImporters.ImportLog?> ImportLogs=new(StringComparer.OrdinalIgnoreCase);
        public static void Reset() { ImportCount=FolderCount=LoadCount=DisallowCount=AllowCount=0;OnImport=null;OnAllowAutoRefresh=null;Assets.Clear();ImportLogs.Clear(); }
        public static void ImportAsset(string path,ImportAssetOptions options=ImportAssetOptions.Default)
        {
            ImportCount++;
            if (OnImport!=null) { OnImport(path);return; }
            Assets[path]=path.EndsWith(".uss",StringComparison.OrdinalIgnoreCase)
                ?new UnityEngine.UIElements.StyleSheet():new UnityEngine.UIElements.VisualTreeAsset();
        }
        public static T? LoadAssetAtPath<T>(string path) where T:UnityEngine.Object
        { LoadCount++;return Assets.TryGetValue(path,out var value)?value as T:null; }
        public static UnityEngine.Object? LoadAssetAtPath(string path,Type type)
        { LoadCount++;return Assets.TryGetValue(path,out var value)&&type.IsInstanceOfType(value)?value:null; }
        public static UnityEngine.Object? LoadMainAssetAtPath(string path)
        { LoadCount++;return Assets.TryGetValue(path,out var value)?value:null; }
        public static UnityEngine.Object[] LoadAllAssetsAtPath(string path)
        { var value=LoadMainAssetAtPath(path);return value==null?Array.Empty<UnityEngine.Object>():new[]{value}; }
        public static string GetAssetPath(UnityEngine.Object value) => Assets.FirstOrDefault(pair=>ReferenceEquals(pair.Value,value)).Key??"";
        public static string AssetPathToGUID(string path) => "00000000000000000000000000000001";
        public static string[] FindAssets(string filter) => Array.Empty<string>();
        public static string GUIDToAssetPath(string guid) => "";
        public static bool IsValidFolder(string path) => Directory.Exists(Absolute(path));
        public static string CreateFolder(string parent,string name)
        { FolderCount++;Directory.CreateDirectory(Absolute(parent+"/"+name));return "fixture-folder-guid"; }
        public static void CreateAsset(UnityEngine.Object asset,string path) { Assets[path]=asset; }
        public static void SaveAssets() { }
        public static void SaveAssetIfDirty(UnityEngine.Object value) { }
        public static void DisallowAutoRefresh() => DisallowCount++;
        public static void AllowAutoRefresh() { AllowCount++;OnAllowAutoRefresh?.Invoke(); }
        public static void Refresh() { }
        public static string Absolute(string path) => Path.Combine(Directory.GetParent(UnityEngine.Application.dataPath)!.FullName,path.Replace('/',Path.DirectorySeparatorChar));
    }
    public class AssetImporter
    {
        public static AssetImporters.ImportLog? GetImportLog(string path) => AssetDatabase.ImportLogs.TryGetValue(path,out var value)?value:new AssetImporters.ImportLog();
        public static AssetImporter GetAtPath(string path) => new();
    }
    public static class EditorUtility { public static void SetDirty(UnityEngine.Object value) { } }
    public static class EditorApplication { public static bool isCompiling,isUpdating,isPlayingOrWillChangePlaymode; }
    public static class Selection { public static UnityEngine.GameObject? activeGameObject; }
    public static class Undo
    {
        public static void RecordObject(UnityEngine.Object value,string name) { }
        public static void RegisterCreatedObjectUndo(UnityEngine.Object value,string name) { }
        public static T AddComponent<T>(UnityEngine.GameObject go) where T:UnityEngine.Component,new() => new T{gameObject=go};
    }
}
namespace UnityEditor.AssetImporters
{
    [Flags] public enum ImportLogFlags { None=0,Error=1,Warning=2 }
    public class ImportLog
    {
        public class ImportLogEntry { public ImportLogFlags flags;public string message="",file="";public int line; }
        public ImportLogEntry[] logEntries=Array.Empty<ImportLogEntry>();
    }
}
namespace UnityEditor.SceneManagement
{
    public static class EditorSceneManager { public static bool MarkSceneDirty(UnityEngine.SceneManagement.Scene scene) => true; }
}
namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    [AttributeUsage(AttributeTargets.Method)] public sealed class McpSchemaAttribute:Attribute { public McpSchemaAttribute(string name) { } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class McpOutputSchemaAttribute:Attribute { public McpOutputSchemaAttribute(string name) { } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class McpToolAttribute:Attribute
    {
        public string[] Groups=Array.Empty<string>();public bool EnabledByDefault;
        public McpToolAttribute(string name,string description,string title) { }
    }
}
namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    public static class VersionControlUtility
    {
        public static int PrepareCount;
        public static bool Blocked;
        public static Action? OnPrepare;
        public class CheckoutResult { public bool canWrite=true;public string? error; }
        public static CheckoutResult EnsureAssetEditable(string path,bool checkout,bool throwOnBlocked=false)
        {
            PrepareCount++;
            OnPrepare?.Invoke();
            if (Blocked&&throwOnBlocked) throw new IOException("Fixture checkout denied.");
            return new CheckoutResult{canWrite=!Blocked,error=Blocked?"Fixture checkout denied.":null};
        }
    }
    public static class UnityApiAdapter { public static string GetObjectId(UnityEngine.Object value) => "fixture-object-id"; }
    public static class SceneObjectLocator
    {
        public class Options { public bool IncludeInactive,IncludePrefabStage; }
        public static string GetHierarchyPath(UnityEngine.GameObject value) => "fixture";
        public static UnityEngine.GameObject? FindObject(string value,string? method,Options options) => null;
    }
}
namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static class WorkSession
    {
        public static Action? OnBeginWrite;
        public class WriteTrackingToken { }
        public class WriteTrackingResult { public bool Succeeded=true;public int RecordedCount=1,ConflictCount;public string[] Issues=Array.Empty<string>(); }
        public static WriteTrackingToken? BeginWrite(string[] paths,string source) { OnBeginWrite?.Invoke();return null; }
        public static WriteTrackingResult CompleteWrite(WriteTrackingToken? token,IDictionary<string,string> after) => new();
        public static string ComputeWriteSha256(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
