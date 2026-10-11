using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object { }
    public class Component : Object { internal GameObject Owner; }
    public class GameObject : Object
    {
        readonly Dictionary<Type, Component> components = new Dictionary<Type, Component>();
        public string name;
        public GameObject(string name, params Type[] types) { this.name = name; foreach (var type in types) Add(type); }
        public T GetComponent<T>() where T : Component { return (T)GetComponent(typeof(T)); }
        public Component GetComponent(Type type) { return components.TryGetValue(type, out var value) ? value : null; }
        internal Component Add(Type type) { var value = (Component)Activator.CreateInstance(type); value.Owner = this; components[type] = value; return value; }
        internal void Remove(Component component) { components.Remove(component.GetType()); }
    }
}
namespace UnityEngine.EventSystems
{
    public class EventSystem : UnityEngine.Component { }
    public class StandaloneInputModule : UnityEngine.Component { }
}
namespace UnityEngine.InputSystem.UI { public class InputSystemUIInputModule : UnityEngine.Component { } }
namespace UnityEditor
{
    public class PlayerSettings : UnityEngine.Object { }
    public static class AssetDatabase
    {
        public static UnityEngine.Object[] Settings = new UnityEngine.Object[] { new PlayerSettings() };
        public static bool FailRead;
        public static UnityEngine.Object[] LoadAllAssetsAtPath(string path)
        {
            if (path != "ProjectSettings/ProjectSettings.asset") throw new Exception("Unexpected settings path.");
            if (FailRead) throw new Exception("Fixture read failure.");
            return Settings;
        }
    }
    public class SerializedObject
    {
        public static int? ActiveInputHandler = 2;
        public SerializedObject(UnityEngine.Object target) { }
        public void Update() { }
        public SerializedProperty FindProperty(string name)
        {
            if (name != "activeInputHandler") throw new Exception("Unexpected property.");
            return ActiveInputHandler.HasValue ? new SerializedProperty { intValue = ActiveInputHandler.Value } : null;
        }
    }
    public class SerializedProperty { public int intValue; }
    public static class Undo
    {
        public static bool FailAdd;
        public static int Adds, Removes, Creates;
        public static UnityEngine.Component AddComponent(UnityEngine.GameObject target, Type type) { Adds++; return FailAdd ? null : target.Add(type); }
        public static void DestroyObjectImmediate(UnityEngine.Component component) { Removes++; component.Owner.Remove(component); }
        public static void RegisterCreatedObjectUndo(UnityEngine.GameObject target, string name) { Creates++; }
    }
    public static class Selection { public static UnityEngine.GameObject activeGameObject; }
    public static class GameObjectUtility { public static void EnsureUniqueNameForSibling(UnityEngine.GameObject target) { } }
}
namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public class ManageUIParams { public bool? DryRun, Select; }
}
