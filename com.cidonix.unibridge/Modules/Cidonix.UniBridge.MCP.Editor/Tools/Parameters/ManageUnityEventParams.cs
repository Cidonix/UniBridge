#nullable disable
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.Tools.Parameters
{
    public enum UnityEventManageAction
    {
        Inspect,
        AddPersistentCall,
        SetPersistentCalls,
        ClearPersistentCalls
    }

    public record ManageUnityEventParams
    {
        [McpDescription("Operation to perform: Inspect, AddPersistentCall, SetPersistentCalls, or ClearPersistentCalls.", Required = false, Default = UnityEventManageAction.Inspect)]
        public UnityEventManageAction Action { get; set; } = UnityEventManageAction.Inspect;

        [McpDescription("Scene GameObject name, hierarchy path, or object id that owns the UnityEvent. If omitted, current selection is used.", Required = false)]
        public string Target { get; set; }

        [McpDescription("How to resolve Target: by_name, by_id, by_path, by_component, or by_id_or_name_or_path. Default auto-resolves id/name/path.", Required = false)]
        public string SearchMethod { get; set; }

        [McpDescription("Optional component type that owns the UnityEvent, e.g. Button, Toggle, Animator, or a custom MonoBehaviour.", Required = false)]
        public string Component { get; set; }

        [McpDescription("UnityEvent member name/path such as onClick, m_OnClick, onValueChanged, or a custom serialized UnityEvent field. Inspect can omit this to list all events on the target.", Required = false)]
        public string EventProperty { get; set; }

        [McpDescription("Persistent calls to set. Accepts a structured object { persistentCalls: [...] } or an array of call objects.", Required = false)]
        public JToken PersistentCalls { get; set; }

        [McpDescription("Single persistent call object for AddPersistentCall or SetPersistentCalls. Fields: target, component, methodName, argument, callState.", Required = false)]
        public JObject PersistentCall { get; set; }

        [McpDescription("Listener target GameObject/object for the convenience single-call form. Defaults to the UnityEvent owner GameObject.", Required = false)]
        public string EventTarget { get; set; }

        [McpDescription("Component type on EventTarget for the convenience single-call form.", Required = false)]
        public string EventComponent { get; set; }

        [McpDescription("Method name for the convenience single-call form, e.g. Ping, SetActive, set_enabled.", Required = false)]
        public string MethodName { get; set; }

        [McpDescription("Static argument for the convenience single-call form. Supports int, float, string, bool, and UnityEngine.Object references.", Required = false)]
        public JToken Argument { get; set; }

        [McpDescription("UnityEvent call state for added calls: Off, EditorAndRuntime, or RuntimeOnly. Default RuntimeOnly.", Required = false, Default = "RuntimeOnly")]
        public string CallState { get; set; }

        [McpDescription("For AddPersistentCall, clear existing listeners before adding this call. SetPersistentCalls always replaces the full list.", Required = false, Default = false)]
        public bool? ClearExisting { get; set; }

        [McpDescription("Include inactive scene objects while resolving Target/EventTarget. Default true.", Required = false, Default = true)]
        public bool? IncludeInactive { get; set; }

        [McpDescription("Select the UnityEvent owner GameObject after the operation. Default false.", Required = false, Default = false)]
        public bool? Select { get; set; }

        [McpDescription("Preview the operation without modifying the scene. Default false.", Required = false, Default = false)]
        public bool? DryRun { get; set; }
    }
}
