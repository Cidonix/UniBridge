#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// UI Toolkit UXML/USS/PanelSettings authoring and UIDocument wiring.
    /// </summary>
    public static class ManageUIToolkit
    {
        const string ToolName = "UniBridge_ManageUIToolkit";
        static readonly XNamespace UiNamespace = "UnityEngine.UIElements";
        static readonly XNamespace UieNamespace = "UnityEditor.UIElements";

        public const string Title = "Manage UI Toolkit assets";

        public const string Description = @"Create and edit UI Toolkit UXML/USS/PanelSettings assets and wire them to UIDocument scene objects.

Use this with UniBridge_CaptureUIToolkit for a complete authoring loop: create UXML/USS, attach a UIDocument, add small elements/classes/styles, then capture or inspect the resolved visual tree.

Args:
    Action: Inspect, ValidateUxml, ValidateUss, CreateDocument, CreateStyleSheet, CreatePanelSettings, AttachDocument, AddElement, SetClasses, or SetInlineStyle.
    Path/UxmlPath/DocumentPath: UXML asset path for document operations.
    StyleSheetPath: USS asset path.
    PanelSettingsPath: PanelSettings asset path.
    Target: GameObject name/path/id for UIDocument operations.
    Name, RootName, RootClass, Template, Elements: UXML creation controls.
    ElementType, ElementName, ParentName, Text, Classes, Style, Attributes, Children: element controls.
    ReferenceResolution, ScaleMode, ScreenMatchMode, Match: PanelSettings controls.
    Content: optional raw UXML/USS source for creation or validation.
    DryRun/Preview: validate the proposed UXML/USS without file writes, checkout, or import.
    FailOnImportWarnings: reject import warnings by default; false explicitly permits completed_with_warnings.

Returns:
    success, message, and data with asset paths, structured validation/import diagnostics, written/imported flags, or UIDocument summaries.
Preview checks structure only; Unity semantic import is not run. Failed imports never report successful creation.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", @enum = new[] { "Inspect", "ValidateUxml", "ValidateUss", "CreateDocument", "CreateStyleSheet", "CreatePanelSettings", "AttachDocument", "AddElement", "SetClasses", "SetInlineStyle" } },
                    Content = new { type = "string", description = "Exact raw UXML or USS source. Creation can also generate content from the existing controls." },
                    DryRun = new { type = "boolean", @default = false, description = "Validate UXML/USS without writes, checkout or import. Semantic import is not run." },
                    Preview = new { type = "boolean", @default = false },
                    FailOnImportWarnings = new { type = "boolean", @default = true },
                    Path = new { type = "string" },
                    UxmlPath = new { type = "string" },
                    DocumentPath = new { type = "string" },
                    StyleSheetPath = new { type = "string" },
                    PanelSettingsPath = new { type = "string" },
                    Target = new { anyOf = new object[] { new { type = "string" }, new { type = "integer" } } },
                    Name = new { type = "string" },
                    RootName = new { type = "string" },
                    RootClass = new { type = "string" },
                    Template = new { type = "string" },
                    Elements = new { type = "array", items = new { type = "object" } },
                    ParentName = new { type = "string" },
                    ElementType = new { type = "string" },
                    ElementName = new { type = "string" },
                    Text = new { type = "string" },
                    Classes = new { type = "array", items = new { type = "string" } },
                    AddClasses = new { type = "array", items = new { type = "string" } },
                    RemoveClasses = new { type = "array", items = new { type = "string" } },
                    Style = new { description = "USS style string or object of property/value pairs." },
                    Attributes = new { type = "object", additionalProperties = true },
                    ReferenceResolution = new { type = "array", items = new { type = "integer" }, minItems = 2, maxItems = 2 },
                    ScaleMode = new { type = "string" },
                    ScreenMatchMode = new { type = "string" },
                    Match = new { type = "number" }
                },
                required = new[] { "Action" },
                additionalProperties = true
            };
        }

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "assets", "ui", "uitoolkit" }, EnabledByDefault = true)]
        public static object HandleCommand(JObject parameters)
        {
            parameters ??= new JObject();
            var action = Normalize(GetString(parameters, "Action", "action") ?? "Inspect");
            try
            {
                if (IsPreview(parameters) && (action == "createpanelsettings" || action == "panelsettings" || action == "attachdocument" || action == "attach" || action == "uidocument"))
                    return Response.Error("UI_TOOLKIT_PREVIEW_UNSUPPORTED", new { status = "blocked", preview = true, written = false, imported = false, message = "Preview is supported for UXML/USS source operations; no scene or PanelSettings changes were made." });
                return action switch
                {
                    "inspect" => Inspect(parameters),
                    "validateuxml" => ValidateSource(parameters, isUxml: true),
                    "validateuss" => ValidateSource(parameters, isUxml: false),
                    "createdocument" or "createuxml" or "uxml" => CreateDocument(parameters),
                    "createstylesheet" or "createuss" or "uss" or "stylesheet" => CreateStyleSheet(parameters),
                    "createpanelsettings" or "panelsettings" => CreatePanelSettings(parameters),
                    "attachdocument" or "attach" or "uidocument" => AttachDocument(parameters),
                    "addelement" or "element" => AddElement(parameters),
                    "setclasses" or "classes" or "setclasslist" => SetClasses(parameters),
                    "setinlinestyle" or "style" or "setstyle" => SetInlineStyle(parameters),
                    _ => Response.Error($"Unknown UI Toolkit action '{GetString(parameters, "Action", "action")}'.")
                };
            }
            catch (UIToolkitValidationException ex)
            {
                return Response.Error("UI_TOOLKIT_VALIDATION_FAILED", new { status = "blocked", preview = IsPreview(parameters), written = false, imported = false, validation = ex.Validation });
            }
            catch (Exception ex)
            {
                return Response.Error("UI_TOOLKIT_OPERATION_FAILED", new { status = "blocked", preview = IsPreview(parameters), written = false, imported = false, message = $"UI Toolkit action '{action}' failed: {ex.Message}" });
            }
        }

        static object Inspect(JObject parameters)
        {
            var path = ResolveUxmlPath(parameters, required: false);
            var target = ResolveTarget(parameters, required: false);
            if (string.IsNullOrWhiteSpace(path) && target != null && target.GetComponent<UIDocument>() is UIDocument document && document.visualTreeAsset != null)
                path = AssetDatabase.GetAssetPath(document.visualTreeAsset);

            if (string.IsNullOrWhiteSpace(path))
            {
                var documents = AssetDatabase.FindAssets("t:VisualTreeAsset")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .Take(GetInt(parameters, 80, "Limit", "limit"))
                    .Select(BuildUxmlSummary)
                    .ToArray();
                return Response.Success("Listed UI Toolkit documents.", new { count = documents.Length, documents });
            }

            return Response.Success("Inspected UI Toolkit document.", BuildUxmlSummary(path));
        }

        static object CreateDocument(JObject parameters)
        {
            var path = ResolveUxmlPath(parameters, required: true);
            var raw = GetRawString(parameters, "Content", "content");
            if (raw != null) return WriteTextAsset(path, raw, isUxml: true, parameters);

            var document = new XDocument(new XDeclaration("1.0", "utf-8", null));
            var root = new XElement(UiNamespace + "UXML",
                new XAttribute(XNamespace.Xmlns + "ui", UiNamespace.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "uie", UieNamespace.NamespaceName));

            var styleSheetPath = NormalizeAssetPath(GetString(parameters, "StyleSheetPath", "styleSheetPath", "stylesheet", "ussPath", "uss_path"));
            if (!string.IsNullOrWhiteSpace(styleSheetPath))
                root.Add(BuildStyleReference(styleSheetPath));

            var rootElement = BuildRootElement(parameters);
            foreach (var element in BuildTemplateElements(parameters))
                rootElement.Add(element);
            if (GetToken(parameters, "Elements", "elements") is JArray elements)
                foreach (var element in elements.OfType<JObject>())
                    rootElement.Add(BuildElement(element));

            root.Add(rootElement);
            document.Add(root);
            return SaveUxmlDocument(path, document, parameters);
        }

        static object CreateStyleSheet(JObject parameters)
        {
            var path = ResolveSourceExtension(NormalizeAssetPath(GetString(parameters, "StyleSheetPath", "styleSheetPath", "Path", "path", "UssPath", "ussPath", "uss_path") ?? "Assets/UI/Toolkit/NewStyles.uss"), ".uss");
            var content = GetRawString(parameters, "Content", "content", "Text", "text") ?? BuildDefaultUss(parameters);
            return WriteTextAsset(path, content, isUxml: false, parameters);
        }

        static object CreatePanelSettings(JObject parameters)
        {
            var path = NormalizeAssetPath(GetString(parameters, "PanelSettingsPath", "panelSettingsPath", "panel_settings_path", "Path", "path") ?? "Assets/UI/Toolkit/NewPanelSettings.asset");
            EnsureParentDirectory(path);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            var created = false;
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, path);
                created = true;
            }
            else
            {
                VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true);
            }

            Undo.RecordObject(panel, "Configure PanelSettings");
            panel.scaleMode = ParseEnum(GetString(parameters, "ScaleMode", "scaleMode", "scale_mode"), panel.scaleMode);
            panel.referenceResolution = ParseVector2Int(GetToken(parameters, "ReferenceResolution", "referenceResolution", "reference_resolution")) ?? panel.referenceResolution;
            panel.screenMatchMode = ParseEnum(GetString(parameters, "ScreenMatchMode", "screenMatchMode", "screen_match_mode"), panel.screenMatchMode);
            panel.match = GetFloat(parameters, panel.match, "Match", "match");
            EditorUtility.SetDirty(panel);
            AssetDatabase.SaveAssetIfDirty(panel);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Response.Success(created ? "PanelSettings created." : "PanelSettings updated.", BuildPanelSettingsSummary(path, panel));
        }

        static object AttachDocument(JObject parameters)
        {
            var target = ResolveOrCreateTarget(parameters, GetString(parameters, "Name", "name") ?? "UniBridge UIDocument");
            var document = target.GetComponent<UIDocument>();
            if (document == null)
                document = Undo.AddComponent<UIDocument>(target);

            Undo.RecordObject(document, "Configure UIDocument");
            var uxmlPath = ResolveUxmlPath(parameters, required: true);
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (visualTree == null)
                return Response.Error($"VisualTreeAsset '{uxmlPath}' could not be loaded.");

            document.visualTreeAsset = visualTree;
            var panelPath = NormalizeAssetPath(GetString(parameters, "PanelSettingsPath", "panelSettingsPath", "panel_settings_path"));
            if (!string.IsNullOrWhiteSpace(panelPath))
            {
                var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
                if (panel != null)
                    document.panelSettings = panel;
            }

            document.sortingOrder = GetFloat(parameters, document.sortingOrder, "SortingOrder", "sortingOrder", "sorting_order");
            EditorUtility.SetDirty(document);
            EditorSceneManager.MarkSceneDirty(target.scene);
            return Response.Success("UIDocument attached or updated.", BuildDocumentSummary(target, document));
        }

        static object AddElement(JObject parameters)
        {
            var path = ResolveUxmlPath(parameters, required: true);
            var document = LoadUxmlDocument(path);
            var parent = FindElement(document, parameters, parentFallback: true);
            if (parent == null)
                return Response.Error("Parent element was not found.");

            var elementSpec = new JObject
            {
                ["type"] = GetString(parameters, "ElementType", "elementType", "element_type", "Type", "type") ?? "VisualElement",
                ["name"] = GetString(parameters, "ElementName", "elementName", "element_name", "Name", "name"),
                ["text"] = GetString(parameters, "Text", "text")
            };
            if (GetToken(parameters, "Classes", "classes", "Class", "class") is JToken classes)
                elementSpec["classes"] = classes.DeepClone();
            if (GetToken(parameters, "Style", "style") is JToken style)
                elementSpec["style"] = style.DeepClone();
            if (GetToken(parameters, "Attributes", "attributes") is JToken attrs)
                elementSpec["attributes"] = attrs.DeepClone();
            if (GetToken(parameters, "Children", "children") is JToken children)
                elementSpec["children"] = children.DeepClone();

            parent.Add(BuildElement(elementSpec));
            return SaveUxmlDocument(path, document, parameters);
        }

        static object SetClasses(JObject parameters)
        {
            var path = ResolveUxmlPath(parameters, required: true);
            var document = LoadUxmlDocument(path);
            var element = FindElement(document, parameters, parentFallback: false);
            if (element == null)
                return Response.Error("Element was not found.");

            var classes = new HashSet<string>(ReadStringArray(GetToken(parameters, "Classes", "classes", "Class", "class")) ?? ReadClasses(element), StringComparer.Ordinal);
            foreach (var cls in ReadStringArray(GetToken(parameters, "AddClasses", "addClasses", "add_classes")) ?? Array.Empty<string>())
                classes.Add(cls);
            foreach (var cls in ReadStringArray(GetToken(parameters, "RemoveClasses", "removeClasses", "remove_classes")) ?? Array.Empty<string>())
                classes.Remove(cls);
            SetAttribute(element, "class", string.Join(" ", classes.Where(item => !string.IsNullOrWhiteSpace(item))));
            return SaveUxmlDocument(path, document, parameters);
        }

        static object SetInlineStyle(JObject parameters)
        {
            var path = ResolveUxmlPath(parameters, required: true);
            var document = LoadUxmlDocument(path);
            var element = FindElement(document, parameters, parentFallback: false);
            if (element == null)
                return Response.Error("Element was not found.");

            var styles = ParseStyleAttribute(element.Attribute("style")?.Value);
            MergeStyle(styles, GetToken(parameters, "Style", "style"));
            SetAttribute(element, "style", string.Join("; ", styles.Select(pair => $"{pair.Key}: {pair.Value}")));
            return SaveUxmlDocument(path, document, parameters);
        }

        static XElement BuildRootElement(JObject parameters)
        {
            var rootName = GetString(parameters, "RootName", "rootName", "root_name") ?? "root";
            var rootClass = GetString(parameters, "RootClass", "rootClass", "root_class") ?? "screen-root";
            return new XElement(UiNamespace + "VisualElement",
                new XAttribute("name", rootName),
                new XAttribute("class", rootClass));
        }

        static IEnumerable<XElement> BuildTemplateElements(JObject parameters)
        {
            var template = Normalize(GetString(parameters, "Template", "template") ?? "Panel");
            if (template == "none" || template == "empty")
                yield break;

            if (template == "toolbar")
            {
                yield return BuildElement(new JObject { ["type"] = "VisualElement", ["name"] = "toolbar", ["classes"] = new JArray("toolbar"), ["children"] = new JArray(new JObject { ["type"] = "Button", ["name"] = "primary-action", ["text"] = "Action", ["classes"] = new JArray("toolbar-button") }) });
                yield break;
            }

            if (template == "list")
            {
                yield return BuildElement(new JObject { ["type"] = "Label", ["name"] = "title", ["text"] = GetString(parameters, "Title", "title") ?? "List", ["classes"] = new JArray("title") });
                yield return BuildElement(new JObject { ["type"] = "ScrollView", ["name"] = "items", ["classes"] = new JArray("list") });
                yield break;
            }

            yield return BuildElement(new JObject { ["type"] = "Label", ["name"] = "title", ["text"] = GetString(parameters, "Title", "title") ?? "Panel", ["classes"] = new JArray("title") });
            yield return BuildElement(new JObject { ["type"] = "VisualElement", ["name"] = "content", ["classes"] = new JArray("panel") });
        }

        static XElement BuildElement(JObject spec)
        {
            var type = GetString(spec, "type", "Type", "elementType", "ElementType") ?? "VisualElement";
            var element = new XElement(UiNamespace + type);
            var name = GetString(spec, "name", "Name", "elementName", "ElementName");
            if (!string.IsNullOrWhiteSpace(name))
                element.SetAttributeValue("name", name);

            var classes = ReadStringArray(GetToken(spec, "classes", "Classes", "class", "Class"));
            if (classes != null && classes.Length > 0)
                element.SetAttributeValue("class", string.Join(" ", classes));

            var text = GetString(spec, "text", "Text", "label", "Label");
            if (!string.IsNullOrEmpty(text))
                element.SetAttributeValue("text", text);

            if (GetToken(spec, "style", "Style") is JToken styleToken)
            {
                var styles = new Dictionary<string, string>(StringComparer.Ordinal);
                MergeStyle(styles, styleToken);
                element.SetAttributeValue("style", string.Join("; ", styles.Select(pair => $"{pair.Key}: {pair.Value}")));
            }

            if (GetToken(spec, "attributes", "Attributes") is JObject attrs)
                foreach (var attr in attrs.Properties())
                    if (!string.IsNullOrWhiteSpace(attr.Name) && attr.Value.Type != JTokenType.Null)
                        element.SetAttributeValue(attr.Name, attr.Value.ToString());

            if (GetToken(spec, "children", "Children") is JArray children)
                foreach (var child in children.OfType<JObject>())
                    element.Add(BuildElement(child));

            return element;
        }

        static XElement BuildStyleReference(string styleSheetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(styleSheetPath);
            var name = Path.GetFileNameWithoutExtension(styleSheetPath);
            var src = string.IsNullOrWhiteSpace(guid)
                ? styleSheetPath
                : $"project://database/{styleSheetPath}?fileID=7433441132597879392&guid={guid}&type=3#{name}";
            return new XElement(UiNamespace + "Style", new XAttribute("src", src));
        }

        static XDocument LoadUxmlDocument(string path)
        {
            var absolutePath = ProjectPathToAbsolutePath(path);
            if (!File.Exists(absolutePath))
                throw new FileNotFoundException($"UXML '{path}' was not found.", absolutePath);
            var sourceBytes = File.ReadAllBytes(absolutePath);
            string content;
            using (var sourceReader = new StreamReader(new MemoryStream(sourceBytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                content = sourceReader.ReadToEnd();
            var validation = ValidateUxml(content);
            if (!validation.valid) throw new UIToolkitValidationException(validation);
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            document.AddAnnotation(new UxmlSourcePrecondition { Sha256 = WorkSession.ComputeWriteSha256(sourceBytes) });
            return document;
        }

        sealed class UxmlSourcePrecondition { public string Sha256; }

        static object SaveUxmlDocument(string path, XDocument document, JObject parameters)
        {
            using var memory = new MemoryStream();
            using (var writer = XmlWriter.Create(memory, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n" }))
                document.Save(writer);
            return WriteTextAsset(path, Encoding.UTF8.GetString(memory.ToArray()), isUxml: true, parameters,
                document.Annotation<UxmlSourcePrecondition>()?.Sha256);
        }

        static object ValidateSource(JObject parameters, bool isUxml)
        {
            var path = isUxml ? ResolveUxmlPath(parameters, required: false) : NormalizeAssetPath(GetString(parameters, "StyleSheetPath", "styleSheetPath", "Path", "path", "UssPath", "ussPath", "uss_path"));
            if (path != null) path = ResolveSourceExtension(path, isUxml ? ".uxml" : ".uss");
            var content = GetRawString(parameters, "Content", "content");
            if (content == null)
            {
                if (path == null) return Response.Error("UI_TOOLKIT_SOURCE_REQUIRED", new { status = "blocked", written = false, imported = false });
                content = File.ReadAllText(ProjectPathToAbsolutePath(path));
            }
            var validation = isUxml ? ValidateUxml(content) : UIToolkitValidation.ValidateUss(content);
            var data = new { path, status = validation.valid ? "validated_structure" : "blocked", preview = true, written = false, imported = false, validation, import = new UIToolkitImportResult() };
            return validation.valid ? Response.Success("UI Toolkit source structure validated; semantic import was not run.", data) : Response.Error("UI_TOOLKIT_VALIDATION_FAILED", data);
        }

        static UIToolkitValidationResult ValidateUxml(string content)
        {
            return UIToolkitValidation.ValidateUxml(content, (ns, name) =>
            {
                if (ns.Length == 0 || ns == UiNamespace.NamespaceName)
                {
                    var type = typeof(VisualElement).Assembly.GetType(UiNamespace.NamespaceName + "." + name, false);
                    if (type != null && (typeof(VisualElement).IsAssignableFrom(type) || type.IsDefined(typeof(UxmlObjectAttribute), true)))
                        return true;
                }
                // Serialized-object property wrappers, custom factories and Editor elements may not match
                // a VisualElement type name. Defer unresolved tags to Unity rather than rejecting legal UXML.
                return null;
            });
        }

        static object WriteTextAsset(string path, string content, bool isUxml, JObject parameters, string expectedBeforeSha256 = null)
        {
            path = ResolveSourceExtension(path, isUxml ? ".uxml" : ".uss");
            var validation = isUxml ? ValidateUxml(content) : UIToolkitValidation.ValidateUss(content);
            var preview = IsPreview(parameters);
            if (!preview && validation.valid && (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode))
                return Response.Error("UI_TOOLKIT_EDITOR_BUSY", new { path, status = "blocked", preview = false, written = false, imported = false, validation });
            var bytes = new UTF8Encoding(false).GetBytes(content);
            var absolute = ProjectPathToAbsolutePath(path);
            var recoveryRoot = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", "UniBridge", "UIToolkitWrites");
            WorkSession.WriteTrackingToken token = null;
            UIToolkitAssetWriteResult result = null;
            var suppressed = false;
            try
            {
                if (!preview && validation.valid)
                {
                    token = WorkSession.BeginWrite(new[] { path }, "ManageUIToolkit/" + (isUxml ? "uxml" : "uss"));
                    AssetDatabase.DisallowAutoRefresh(); suppressed = true;
                }
                result = UIToolkitAssetWriter.Write(path, absolute, bytes, validation, preview, recoveryRoot,
                    () => { VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: true); EnsureParentDirectory(path); },
                    () => ImportTextAsset(path, isUxml), GetBool(parameters, true, "FailOnImportWarnings", "failOnImportWarnings"), expectedBeforeSha256);
            }
            finally
            {
                if (suppressed)
                {
                    try { AssetDatabase.AllowAutoRefresh(); }
                    catch (Exception error)
                    {
                        if (result == null) throw;
                        result.succeeded = false; result.status = result.written ? "partial" : "blocked";
                        result.diagnostics.Add(new UIToolkitDiagnostic { severity = "error", code = "AUTO_REFRESH_RELEASE_FAILED", message = error.Message, file = path });
                    }
                }
            }
            if (!preview && result.written)
            {
                try
                {
                    var current = WorkSession.ComputeWriteSha256(File.ReadAllBytes(absolute));
                    var expected = result.restoredBaseline ? result.beforeSha256 : result.expectedSha256;
                    result.currentSha256 = current;
                    if (current != expected)
                    {
                        result.succeeded = false; result.imported = false; result.restoredBaseline = false; result.status = "partial";
                        result.diagnostics.Add(new UIToolkitDiagnostic { severity = "error", code = "FINAL_SOURCE_CHANGED", message = "Source changed after the import scope; current bytes are preserved.", file = path });
                    }
                }
                catch (Exception error)
                {
                    result.succeeded = false; result.imported = false; result.restoredBaseline = false; result.currentSha256 = null; result.status = "partial";
                    result.diagnostics.Add(new UIToolkitDiagnostic { severity = "error", code = "FINAL_READBACK_FAILED", message = error.Message, file = path });
                }
            }
            var data = McpJson.ObjectFromObject(result);
            data["bytes"] = bytes.Length;
            data["exists"] = File.Exists(absolute);
            if (preview) data["proposedContent"] = content;
            if (result.imported && isUxml)
            {
                // Summarize the known imported payload, avoiding another importer/load callback after final readback.
                var elements = XDocument.Parse(content).Descendants().Where(element => element.Name.LocalName != "UXML").Take(200)
                    .Select(element => new { type = element.Name.LocalName, name = element.Attribute("name")?.Value,
                        classes = ReadClasses(element), text = element.Attribute("text")?.Value, childCount = element.Elements().Count() }).ToArray();
                data["visualTreeAssetLoaded"] = true;
                data["elementCount"] = elements.Length;
                data["elements"] = McpJson.ArrayFromObject(elements);
            }
            if (token != null && result.written)
            {
                try
                {
                    var finalKnownHash = result.restoredBaseline ? result.beforeSha256 : result.expectedSha256;
                    data["workSessionOwnership"] = McpJson.ObjectFromObject(WorkSession.CompleteWrite(token, new Dictionary<string, string> { [path] = finalKnownHash }));
                }
                catch (Exception error) { data["workSessionOwnership"] = McpJson.ObjectFromObject(new { Succeeded = false, Issues = new[] { error.Message } }); }
            }
            return result.succeeded
                ? Response.Success(preview ? "UI Toolkit source preview validated; semantic import was not run." : result.import.hasWarnings ? "UI Toolkit source written and imported with warnings; declarations may have been ignored." : "UI Toolkit source written, imported and read back successfully.", data)
                : Response.Error(validation.valid ? "UI_TOOLKIT_WRITE_OR_IMPORT_FAILED" : "UI_TOOLKIT_VALIDATION_FAILED", data);
        }

        static UIToolkitImportResult ImportTextAsset(string path, bool isUxml)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var result = new UIToolkitImportResult { guid = AssetDatabase.AssetPathToGUID(path) };
            var asset = isUxml ? (UnityEngine.Object)AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path) : AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            result.assetLoaded = asset != null;
            var log = AssetImporter.GetImportLog(path);
            result.@checked = true;
            if (log != null)
                foreach (var entry in log.logEntries ?? Array.Empty<ImportLog.ImportLogEntry>())
                {
                    var error = (entry.flags & ImportLogFlags.Error) != 0;
                    var warning = (entry.flags & ImportLogFlags.Warning) != 0;
                    result.hasErrors |= error; result.hasWarnings |= warning;
                    result.diagnostics.Add(new UIToolkitDiagnostic { severity = error ? "error" : warning ? "warning" : "info", code = "UNITY_IMPORT_LOG", message = entry.message, file = entry.file, line = entry.line });
                }
            if (asset is StyleSheet sheet)
            {
                result.hasErrors |= sheet.importedWithErrors;
                result.hasWarnings |= sheet.importedWithWarnings;
                if (sheet.importedWithErrors && !result.diagnostics.Any(item => item.severity == "error"))
                    result.diagnostics.Add(new UIToolkitDiagnostic { severity = "error", code = "USS_IMPORTED_WITH_ERRORS", message = "StyleSheet.importedWithErrors is true.", file = path });
                if (sheet.importedWithWarnings && !result.diagnostics.Any(item => item.severity == "warning"))
                    result.diagnostics.Add(new UIToolkitDiagnostic { severity = "warning", code = "USS_IMPORTED_WITH_WARNINGS", message = "StyleSheet.importedWithWarnings is true.", file = path });
            }
            if (asset is VisualTreeAsset tree)
            {
                result.hasErrors |= tree.importedWithErrors;
                result.hasWarnings |= tree.importedWithWarnings;
                if (tree.importedWithErrors && !result.diagnostics.Any(item => item.severity == "error"))
                    result.diagnostics.Add(new UIToolkitDiagnostic { severity = "error", code = "UXML_IMPORTED_WITH_ERRORS", message = "VisualTreeAsset.importedWithErrors is true.", file = path });
                if (tree.importedWithWarnings && !result.diagnostics.Any(item => item.severity == "warning"))
                    result.diagnostics.Add(new UIToolkitDiagnostic { severity = "warning", code = "UXML_IMPORTED_WITH_WARNINGS", message = "VisualTreeAsset.importedWithWarnings is true.", file = path });
            }
            result.semanticValidation = result.hasErrors || !result.assetLoaded ? "failed" : result.hasWarnings ? "warnings" : "passed";
            return result;
        }

        static XElement FindElement(XDocument document, JObject parameters, bool parentFallback)
        {
            var name = GetString(parameters, "ParentName", "parentName", "parent_name");
            if (!parentFallback)
                name = GetString(parameters, "ElementName", "elementName", "element_name", "Name", "name", "TargetName", "targetName", "target_name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var foundByName = document.Descendants().FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, name, StringComparison.Ordinal));
                if (foundByName != null)
                    return foundByName;
            }

            var className = GetString(parameters, "ParentClass", "parentClass", "parent_class", "Class", "class");
            if (!string.IsNullOrWhiteSpace(className))
            {
                var foundByClass = document.Descendants().FirstOrDefault(element => ReadClasses(element).Contains(className, StringComparer.Ordinal));
                if (foundByClass != null)
                    return foundByClass;
            }

            return parentFallback
                ? document.Descendants().FirstOrDefault(element => element.Name.LocalName == "VisualElement")
                : null;
        }

        static object BuildUxmlSummary(string path)
        {
            path = NormalizeAssetPath(path);
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            XDocument document = null;
            try
            {
                if (File.Exists(ProjectPathToAbsolutePath(path)))
                    document = XDocument.Load(ProjectPathToAbsolutePath(path));
            }
            catch
            {
                document = null;
            }

            var elements = document == null
                ? Array.Empty<object>()
                : document.Descendants()
                    .Where(element => element.Name.LocalName != "UXML")
                    .Take(200)
                    .Select(element => new
                    {
                        type = element.Name.LocalName,
                        name = element.Attribute("name")?.Value,
                        classes = ReadClasses(element),
                        text = element.Attribute("text")?.Value,
                        childCount = element.Elements().Count()
                    })
                    .ToArray<object>();

            return new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                exists = asset != null || File.Exists(ProjectPathToAbsolutePath(path)),
                visualTreeAssetLoaded = asset != null,
                elementCount = elements.Length,
                elements
            };
        }

        static object BuildPanelSettingsSummary(string path, PanelSettings panel)
        {
            return new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                panel.scaleMode,
                panel.referenceResolution,
                panel.screenMatchMode,
                panel.match
            };
        }

        static object BuildDocumentSummary(GameObject target, UIDocument document)
        {
            return new
            {
                gameObject = new
                {
                    name = target.name,
                    instanceId = UnityApiAdapter.GetObjectId(target),
                    path = SceneObjectLocator.GetHierarchyPath(target)
                },
                uidocument = new
                {
                    visualTreeAsset = document.visualTreeAsset != null ? AssetDatabase.GetAssetPath(document.visualTreeAsset) : null,
                    panelSettings = document.panelSettings != null ? AssetDatabase.GetAssetPath(document.panelSettings) : null,
                    document.sortingOrder
                }
            };
        }

        static string BuildDefaultUss(JObject parameters)
        {
            var rootClass = GetString(parameters, "RootClass", "rootClass", "root_class") ?? "screen-root";
            return $@"/* Generated by UniBridge ManageUIToolkit. */
.{rootClass} {{
    flex-grow: 1;
    padding: 24px;
    background-color: #151922;
}}

.panel {{
    flex-grow: 1;
    padding: 16px;
    background-color: rgba(255, 255, 255, 0.08);
    border-radius: 8px;
}}

.title {{
    font-size: 28px;
    -unity-font-style: bold;
    color: #ffffff;
    margin-bottom: 12px;
}}

.toolbar {{
    height: 48px;
    flex-direction: row;
    align-items: center;
}}

.toolbar-button {{
    min-width: 96px;
}}
";
        }

        static Dictionary<string, string> ParseStyleAttribute(string style)
        {
            return string.IsNullOrWhiteSpace(style) ? new Dictionary<string, string>(StringComparer.Ordinal) : UIToolkitValidation.ReadInlineDeclarations(style);
        }

        static void MergeStyle(Dictionary<string, string> styles, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return;
            if (token.Type == JTokenType.String)
            {
                foreach (var pair in ParseStyleAttribute(token.ToString()))
                    styles[pair.Key] = pair.Value;
                return;
            }
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                    styles[property.Name.StartsWith("--", StringComparison.Ordinal) ? property.Name : property.Name.ToLowerInvariant()] = property.Value.ToString();
            }
        }

        static void SetAttribute(XElement element, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                element.Attribute(name)?.Remove();
            else
                element.SetAttributeValue(name, value);
        }

        static string[] ReadClasses(XElement element)
        {
            return (element.Attribute("class")?.Value ?? string.Empty)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        static string[] ReadStringArray(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token is JArray arr)
                return arr.Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            return token.ToString().Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static GameObject ResolveOrCreateTarget(JObject parameters, string fallbackName)
        {
            var target = ResolveTarget(parameters, required: false);
            if (target != null)
                return target;

            var go = new GameObject(GetString(parameters, "Name", "name") ?? fallbackName);
            Undo.RegisterCreatedObjectUndo(go, "Create UIDocument");
            return go;
        }

        static GameObject ResolveTarget(JObject parameters, bool required)
        {
            var target = GetToken(parameters, "Target", "target", "GameObject", "gameObject", "game_object");
            if (target == null || target.Type == JTokenType.Null || string.IsNullOrWhiteSpace(target.ToString()))
            {
                if (Selection.activeGameObject != null)
                    return Selection.activeGameObject;
                if (required)
                    throw new InvalidOperationException("Target GameObject is required.");
                return null;
            }

            var go = SceneObjectLocator.FindObject(target.ToString(), GetString(parameters, "SearchMethod", "searchMethod", "search_method"), new SceneObjectLocator.Options { IncludeInactive = true, IncludePrefabStage = true });
            if (go == null && required)
                throw new InvalidOperationException($"Target GameObject '{target}' was not found.");
            return go;
        }

        static string ResolveUxmlPath(JObject parameters, bool required)
        {
            var path = NormalizeAssetPath(GetString(parameters, "Path", "path", "UxmlPath", "uxmlPath", "uxml_path", "DocumentPath", "documentPath", "document_path"));
            if (string.IsNullOrWhiteSpace(path))
            {
                if (required)
                    throw new InvalidOperationException("UXML Path is required.");
                return null;
            }

            return ResolveSourceExtension(path, ".uxml");
        }

        static void EnsureParentDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(directory) || AssetDatabase.IsValidFolder(directory))
                return;

            var parts = directory.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var normalized = path.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(normalized) || normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains(':') || normalized.Split('/').Any(segment => segment == "." || segment == ".."))
                throw new InvalidOperationException("UI Toolkit paths must be project-relative asset paths without traversal.");
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return "Assets/" + normalized;
        }

        static string ResolveSourceExtension(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("An asset path is required.");
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return path;
            if (Path.HasExtension(path)) throw new InvalidOperationException("The asset path must use " + extension + ".");
            return path + extension;
        }

        static string ProjectPathToAbsolutePath(string assetPath)
        {
            assetPath = NormalizeAssetPath(assetPath);
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            var root = Path.GetFullPath(projectRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var absolute = Path.GetFullPath(Path.Combine(root, assetPath));
            if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("UI Toolkit asset path escapes the project.");
            return absolute;
        }

        static Vector2Int? ParseVector2Int(JToken token)
        {
            if (token is JArray arr && arr.Count >= 2)
                return new Vector2Int(ReadInt(arr, 0), ReadInt(arr, 1));
            if (token is JObject obj)
                return new Vector2Int(ReadIntMember(obj, "x", 0), ReadIntMember(obj, "y", 0));
            return null;
        }

        static string Normalize(string value) => (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

        static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            return !string.IsNullOrWhiteSpace(value) && Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
        }

        static JToken GetToken(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
                if (obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
                    return token;
            return null;
        }

        static string GetString(JObject obj, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString().Trim();
        }

        static string GetRawString(JObject obj, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        static bool IsPreview(JObject parameters) => GetBool(parameters, false, "DryRun", "dryRun", "dry_run") || GetBool(parameters, false, "Preview", "preview");
        static bool GetBool(JObject parameters, bool fallback, params string[] keys)
        {
            var token = GetToken(parameters, keys);
            return token == null || token.Type == JTokenType.Null ? fallback : token.Type == JTokenType.Boolean ? token.Value<bool>() : bool.TryParse(token.ToString(), out var value) ? value : fallback;
        }

        static int GetInt(JObject obj, int defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            return token != null && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;
        }

        static float GetFloat(JObject obj, float defaultValue, params string[] keys)
        {
            var token = GetToken(obj, keys);
            if (token == null || token.Type == JTokenType.Null)
                return defaultValue;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<float>();
            var text = token.ToString();
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                   float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                ? value
                : defaultValue;
        }

        static int ReadInt(JArray arr, int index)
        {
            return arr.Count > index && int.TryParse(arr[index]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }

        static int ReadIntMember(JObject obj, string property, int defaultValue)
        {
            return obj.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var token) &&
                   int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;
        }
    }
}
