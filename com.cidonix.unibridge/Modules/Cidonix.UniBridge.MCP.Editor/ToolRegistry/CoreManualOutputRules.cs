using System;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    // Independently authored TEMP rule map. SOURCE FAMILY PARTIAL, not whole-action
    // qualification and not a production provider patch. Integration must compose
    // the parent's transport/recovery/projection contracts separately. No Unity API.
    public static class CoreManualOutputRules
    {
        static JObject Type(string type, bool nullable = false) => new JObject { ["type"] = nullable ? new JArray(type, "null") : (JToken)new JValue(type) };
        static JObject Obj(params (string Name, JObject Schema)[] fields)
        {
            var props = new JObject();
            foreach (var field in fields) props[field.Name] = field.Schema;
            return new JObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = true };
        }
        static JObject Arr(JObject item = null, bool nullable = false) => new JObject { ["type"] = nullable ? new JArray("array", "null") : (JToken)new JValue("array"), ["items"] = item ?? new JObject() };
        static JObject Union(params JObject[] schemas) => new JObject { ["anyOf"] = new JArray(schemas) };
        static JObject Required(JObject schema, params string[] names) { schema["required"] = new JArray(names); return schema; }
        static JObject HintReport() => Obj(("status", Type("string")), ("hints", Arr()), ("limitations", Arr(Type("string"))));
        static JObject NullableObject() => Type("object", true);
        static JObject Edits() => Arr(Type("object"));
        static JObject Ownership() => new JObject(); // Helper-owned semantic outcome intentionally opaque.

        public static bool RequiresSuccessData(string owner) => owner != "UniBridge_ManageEditor" && owner != "UniBridge_ReadConsole";

        public static JObject SuccessDataSchema(string owner)
        {
            switch (owner)
            {
                case "UniBridge_ApplyTextEdits":
                    return Required(Obj(("normalizedEdits", Edits()), ("preview", Type("boolean")),
                        ("applyTextEditsDetailInfo", Type("object")), ("obsoleteApiHints", HintReport())), "normalizedEdits");
                case "UniBridge_CreateScript":
                    return Required(Obj(("uri", Type("string")), ("scheduledRefresh", Type("boolean")),
                        ("obsoleteApiHints", HintReport()), ("workSessionOwnership", Ownership())), "uri", "scheduledRefresh");
                case "UniBridge_DeleteScript":
                    return Required(Obj(("deleted", Type("boolean")), ("workSessionOwnership", Ownership())), "deleted");
                case "UniBridge_GetSha":
                    // The explicit minimal JObject branch uses nullable lookups.
                    return Required(Obj(("sha256", Type("string", true)), ("lengthBytes", Type("integer", true)),
                        ("uri", Type("string")), ("path", Type("string")), ("lastModifiedUtc", Type("string"))), "sha256", "lengthBytes");
                case "UniBridge_ImportExternalModel":
                    return Required(Obj(("importDirectory", Type("string")), ("sceneObject", NullableObject()),
                        ("prefabObject", NullableObject()), ("prefabPath", Type("string"))), "importDirectory", "sceneObject", "prefabObject", "prefabPath");
                case "UniBridge_ManageEditor":
                    // State/selection/tool/prefab DTOs are PascalCase. Play-mode,
                    // readiness, compilation, save/reload producers use lower case.
                    // Their dynamic nested data remains open; no whole-action claim.
                    var editorObject = Obj(("IsPlaying", Type("boolean")), ("IsPlayingOrWillChangePlaymode", Type("boolean")),
                        ("IsPaused", Type("boolean")), ("IsCompiling", Type("boolean")), ("IsUpdating", Type("boolean")),
                        ("IsReady", Type("boolean")), ("ApplicationPath", Type("string", true)), ("ApplicationContentsPath", Type("string", true)),
                        ("TimeSinceStartup", Type("number")), ("ActiveTool", Type("string", true)), ("IsCustom", Type("boolean")),
                        ("PivotMode", Type("string", true)), ("PivotRotation", Type("string", true)), ("HandleRotation", Arr(Type("number"))), ("HandlePosition", Arr(Type("number"))),
                        ("ActiveObject", Type("string", true)), ("ActiveGameObject", Type("string", true)), ("ActiveTransform", Type("string", true)),
                        ("ActiveInstanceID", Type("integer")), ("Count", Type("integer")), ("Objects", Arr()), ("GameObjects", Arr()), ("AssetGUIDs", Arr(Type("string"))),
                        ("IsOpen", Type("boolean")), ("AssetPath", Type("string", true)), ("PrefabRootName", Type("string", true)), ("Mode", Type("string", true)), ("IsDirty", Type("boolean")),
                        ("projectRoot", Type("string")), ("isPlaying", Type("boolean")), ("isPaused", Type("boolean")), ("isPlayingOrWillChangePlaymode", Type("boolean")),
                        ("readiness", Type("object")), ("waitSucceeded", Type("boolean", true)), ("waitResult", NullableObject()));
                    // GetLayers is an object whose JSON keys are decimal layer IDs.
                    // It needs a separate open object-map branch, not invented layers.
                    var layers = new JObject { ["type"] = "object", ["additionalProperties"] = Type("string") };
                    var window = Required(Obj(("Title", Type("string", true)), ("TypeName", Type("string", true)),
                        ("IsFocused", Type("boolean")), ("InstanceID", Type("integer")), ("Position", Required(Obj(("X", Type("number")), ("Y", Type("number")), ("Width", Type("number")), ("Height", Type("number"))), "X", "Y", "Width", "Height"))), "Title", "TypeName", "IsFocused", "InstanceID", "Position");
                    return Union(editorObject, layers, Arr(window), Arr(Type("string")));
                case "UniBridge_ManageScene":
                    var sceneObject = Obj(("name", Type("string")), ("path", Type("string")), ("buildIndex", Type("integer")),
                        ("isLoaded", Type("boolean")), ("isDirty", Type("boolean")), ("rootCount", Type("integer")), ("workSessionOwnership", Ownership()));
                    var sceneEntry = Obj(("name", Type("string")), ("path", Type("string")), ("guid", Type("string")),
                        ("enabled", Type("boolean")), ("buildIndex", Type("integer")), ("children", Arr(NullableObject())));
                    sceneEntry["type"] = new JArray("object", "null"); // Hierarchy helper returns null for null GameObjects.
                    return Union(sceneObject, Arr(sceneEntry));
                case "UniBridge_ManageShader":
                    return Required(Obj(("name", Type("string")), ("path", Type("string")), ("contents", Type("string")),
                        ("encodedContents", Type("string", true))), "name", "path");
                case "UniBridge_ReadConsole":
                    var entry = Required(Obj(("Message", Type("string", true)), ("Type", Type("string", true)),
                        ("File", Type("string", true)), ("Line", Type("integer", true)), ("StackTrace", Type("string", true))), "Message", "Type", "File", "Line", "StackTrace");
                    var action = Type("string");
                    action["enum"] = new JArray("mark_session", "overview", "groups", "group_details", "timeline", "timeline_window", "diagnostic_summary", "important_ranges");
                    var report = Required(Obj(("action", action), ("marker", NullableObject()), ("markerEntryId", Type("integer", true)),
                        ("compressedEvents", Arr(null, true)), ("events", Arr()), ("groups", Arr()), ("recent", Arr()), ("samples", Arr()),
                        ("summary", Type("object")), ("totals", Type("object")), ("ranges", Arr())), "action");
                    return Union(Arr(entry), report);
                case "UniBridge_Script":
                    // All eight current SUCCESS producer families are objects.
                    // invalid_params string data belongs to the ERROR branch.
                    return Obj(("uri", Type("string")), ("path", Type("string")), ("scheduledRefresh", Type("boolean")),
                        ("contents", Type("string")), ("encoded_contents", Type("string", true)), ("contents_encoded", Type("boolean")),
                        ("deleted", Type("boolean")), ("editsApplied", Type("integer")), ("editsPreviewed", Type("integer")),
                        ("preview", Type("boolean")), ("no_op", Type("boolean")), ("noChangesApplied", Type("boolean")),
                        ("sha256", Type("string")), ("lengthBytes", Type("integer")), ("lastModifiedUtc", Type("string")),
                        ("diagnostics", Arr(Obj(("severity", Type("string", true)), ("message", Type("string", true)), ("line", Type("integer")), ("col", Type("integer"))))),
                        ("obsoleteApiHints", HintReport()), ("workSessionOwnership", Ownership()));
                case "UniBridge_ScriptApplyEdits":
                    return Obj(("uri", Type("string")), ("path", Type("string")), ("editsApplied", Type("integer")),
                        ("sha256", Type("string")), ("scheduledRefresh", Type("boolean")), ("no_op", Type("boolean")),
                        ("normalizedEdits", Edits()), ("computedTextEdits", Edits()), ("routing", Type("string")),
                        ("executionModel", Type("string")), ("diff", Type("string", true)), ("obsoleteApiHints", HintReport()), ("workSessionOwnership", Ownership()));
                case "UniBridge_ScriptCapabilities":
                    return Required(Obj(("ops", Arr(Type("string"))), ("text_ops", Arr(Type("string"))), ("max_edit_payload_bytes", Type("integer")),
                        ("guards", Required(Obj(("using_guard", Type("boolean"))), "using_guard")), ("extras", Required(Obj(("get_sha", Type("boolean"))), "get_sha"))), "ops", "text_ops", "max_edit_payload_bytes", "guards", "extras");
                case "UniBridge_ValidateScript":
                    return Required(Obj(("warnings", Type("integer")), ("errors", Type("integer")), ("obsoleteApiHints", HintReport()),
                        ("diagnostics", Arr(Obj(("severity", Type("string", true)), ("message", Type("string", true)), ("line", Type("integer")), ("col", Type("integer"))))),
                        ("summary", Required(Obj(("warnings", Type("integer")), ("errors", Type("integer"))), "warnings", "errors")),
                        ("path", Type("string", true)), ("absolutePath", Type("string")), ("pathResolution", Type("object"))), "warnings", "errors", "summary");
                default: throw new ArgumentException("Owner has no reviewed manual provider: " + owner, nameof(owner));
            }
        }

        // A business-only response fixture for independent validation. Parent owns
        // production context/transport failure schemas and known mutation outcomes.
        public static JObject BuildBusinessResponseSchema(string owner)
        {
            var success = new JObject { ["properties"] = new JObject { ["success"] = new JObject { ["const"] = true }, ["data"] = SuccessDataSchema(owner) }, ["required"] = RequiresSuccessData(owner) ? new JArray("success", "message", "data") : new JArray("success", "message") };
            var error = new JObject { ["properties"] = new JObject { ["success"] = new JObject { ["const"] = false }, ["data"] = new JObject() }, ["required"] = new JArray("success", "code", "error") };
            return new JObject { ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object", ["properties"] = new JObject { ["success"] = Type("boolean"), ["message"] = Type("string", true), ["code"] = Type("string", true), ["error"] = Type("string", true), ["projectContext"] = Type("object"), ["_meta"] = new JObject() }, ["anyOf"] = new JArray(success, error), ["additionalProperties"] = true, ["description"] = "Source-reviewed manual data family correction candidate; partial, not whole-action-data-qualified." };
        }
    }
}
