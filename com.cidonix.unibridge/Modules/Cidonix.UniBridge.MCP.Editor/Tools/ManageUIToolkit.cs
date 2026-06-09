#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Newtonsoft.Json.Linq;
using UnityEditor;
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
    Action: Inspect, CreateDocument, CreateStyleSheet, CreatePanelSettings, AttachDocument, AddElement, SetClasses, or SetInlineStyle.
    Path/UxmlPath/DocumentPath: UXML asset path for document operations.
    StyleSheetPath: USS asset path.
    PanelSettingsPath: PanelSettings asset path.
    Target: GameObject name/path/id for UIDocument operations.
    Name, RootName, RootClass, Template, Elements: UXML creation controls.
    ElementType, ElementName, ParentName, Text, Classes, Style, Attributes, Children: element controls.
    ReferenceResolution, ScaleMode, ScreenMatchMode, Match: PanelSettings controls.

Returns:
    success, message, and data with created asset paths, UIDocument summaries, or UXML element summaries.";

        [McpSchema(ToolName)]
        public static object GetInputSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    Action = new { type = "string", @enum = new[] { "Inspect", "CreateDocument", "CreateStyleSheet", "CreatePanelSettings", "AttachDocument", "AddElement", "SetClasses", "SetInlineStyle" } },
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
                return action switch
                {
                    "inspect" => Inspect(parameters),
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
            catch (Exception ex)
            {
                Debug.LogError($"[ManageUIToolkit] Action '{action}' failed: {ex}");
                return Response.Error($"UI Toolkit action '{action}' failed: {ex.Message}");
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
            EnsureParentDirectory(path);
            VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: false);

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
            document.Save(ProjectPathToAbsolutePath(path));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return Response.Success("UI Toolkit UXML created or updated.", BuildUxmlSummary(path));
        }

        static object CreateStyleSheet(JObject parameters)
        {
            var path = NormalizeAssetPath(GetString(parameters, "StyleSheetPath", "styleSheetPath", "Path", "path", "UssPath", "ussPath", "uss_path") ?? "Assets/UI/Toolkit/NewStyles.uss");
            EnsureParentDirectory(path);
            VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: false);
            var content = GetString(parameters, "Content", "content", "Text", "text");
            if (string.IsNullOrWhiteSpace(content))
                content = BuildDefaultUss(parameters);

            File.WriteAllText(ProjectPathToAbsolutePath(path), content);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            return Response.Success("UI Toolkit USS created or updated.", new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                exists = sheet != null,
                bytes = content.Length
            });
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
            AssetDatabase.SaveAssets();
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
            SaveUxmlDocument(path, document);
            return Response.Success("UI Toolkit element added.", BuildUxmlSummary(path));
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
            SaveUxmlDocument(path, document);
            return Response.Success("UI Toolkit element classes updated.", BuildUxmlSummary(path));
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
            SaveUxmlDocument(path, document);
            return Response.Success("UI Toolkit inline style updated.", BuildUxmlSummary(path));
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
                var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            return XDocument.Load(absolutePath, LoadOptions.PreserveWhitespace);
        }

        static void SaveUxmlDocument(string path, XDocument document)
        {
            VersionControlUtility.EnsureAssetEditable(path, checkout: true, throwOnBlocked: false);
            document.Save(ProjectPathToAbsolutePath(path));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
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
    unity-font-style: bold;
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
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(style))
                return result;
            foreach (var part in style.Split(';'))
            {
                var index = part.IndexOf(':');
                if (index <= 0)
                    continue;
                var key = part.Substring(0, index).Trim();
                var value = part.Substring(index + 1).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    result[key] = value;
            }
            return result;
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
                    styles[property.Name] = property.Value.ToString();
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

            if (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
                path += ".uxml";
            return path;
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
            var normalized = path.Trim().Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return normalized;
            return "Assets/" + normalized;
        }

        static string ProjectPathToAbsolutePath(string assetPath)
        {
            assetPath = NormalizeAssetPath(assetPath);
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            return Path.Combine(projectRoot ?? string.Empty, assetPath).Replace('/', Path.DirectorySeparatorChar);
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
