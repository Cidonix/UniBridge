#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    public static partial class BatchActions
    {
        static ValidationReport ValidateStep(BatchStep step, Dictionary<string, ToolSettingsEntry> toolSettings)
        {
            var report = new ValidationReport();

            if (string.IsNullOrWhiteSpace(step.ToolName))
            {
                report.Error("Step tool is required.");
                return report;
            }

            if (!BatchActionToolCatalog.IsAllowed(step.ToolName))
            {
                report.Error($"Tool '{step.ToolName}' is not allowed in UniBridge_BatchActions.");
                return report;
            }

            if (!McpToolRegistry.HasTool(step.ToolName))
            {
                report.Error($"Tool '{step.ToolName}' is not registered in the current Unity Editor.");
                return report;
            }

            if (toolSettings.TryGetValue(step.ToolName, out var entry) && !entry.IsEnabled)
            {
                report.Error($"Tool '{step.ToolName}' is disabled in UniBridge settings.");
                return report;
            }

            switch (step.ToolName)
            {
                case "UniBridge_ManageGameObject":
                    ValidateGameObjectStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageAsset":
                    ValidateAssetStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageAssetImporter":
                    ValidateAssetImporterStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageMaterial":
                    ValidateMaterialStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageScriptableObject":
                    ValidateScriptableObjectStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageScene":
                    ValidateSceneStep(step.Parameters, report);
                    break;
                case "UniBridge_ManagePrefab":
                    ValidatePrefabStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageAnimatorController":
                    ValidateAnimatorControllerStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageAnimationClip":
                    ValidateAnimationClipStep(step.Parameters, report);
                    break;
                case "UniBridge_ScopedEdit":
                    report.Error("Nested UniBridge_ScopedEdit calls are intentionally blocked inside BatchActions. Put the scope on the outer UniBridge_ScopedEdit call.");
                    break;
                case "UniBridge_BehaviourContext":
                    report.Info("Behaviour context is read-only and has no additional batch validation.");
                    break;
                case "UniBridge_ManageTilemap2D":
                    ValidateTilemapStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageInputActions":
                    ValidateInputActionsStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageTimeline":
                    ValidateTimelineStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageConstraints":
                    ValidateConstraintsStep(step.Parameters, report);
                    break;
                case "UniBridge_ManagePhysics2D":
                    ValidatePhysics2DStep(step.Parameters, report);
                    break;
                case "UniBridge_DomainCatalog":
                    report.Info("Domain catalog is read-only and has no additional batch validation.");
                    break;
                case "UniBridge_ManagePhysics3D":
                    ValidatePhysics3DStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageNavigation":
                    ValidateNavigationStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageRendering":
                    ValidateRenderingStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageUIToolkit":
                    ValidateUIToolkitStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageUI":
                    ValidateUIStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageUnityEvent":
                    ValidateUnityEventStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageEditor":
                    ValidateEditorStep(step.Parameters, report);
                    break;
                case "UniBridge_ManageShader":
                    ValidateShaderStep(step.Parameters, report);
                    break;
                case "UniBridge_AssetIntelligence":
                    report.Info("Asset intelligence is read-only and has no additional batch validation.");
                    break;
                case "UniBridge_ScriptIntelligence":
                    report.Info("Script intelligence is read-only and has no additional batch validation.");
                    break;
                case "UniBridge_Discover":
                    report.Info("UniBridge_Discover is read-only in BatchActions; it can be used as a workflow ping/discovery step.");
                    break;
                case "UniBridge_ValidateAdditiveSceneRegistration":
                    ValidateAdditiveSceneRegistrationStep(step.Parameters, report);
                    break;
                case "UniBridge_ValidateScript":
                    ValidateScriptValidationStep(step.Parameters, report);
                    break;
                case "UniBridge_CaptureView":
                    ValidateCaptureStep(step.Parameters, report);
                    break;
                case "UniBridge_CaptureAsset":
                    ValidateCaptureAssetStep(step.Parameters, report);
                    break;
                case "UniBridge_CaptureUIToolkit":
                    ValidateCaptureUIToolkitStep(step.Parameters, report);
                    break;
                case "UniBridge_VisualSceneAudit":
                    ValidateVisualSceneAuditStep(step.Parameters, report);
                    break;
                case "UniBridge_SceneObjectView":
                    ValidateSceneObjectViewStep(step.Parameters, report);
                    break;
                case "UniBridge_TypeSchema":
                    ValidateTypeSchemaStep(step.Parameters, report);
                    break;
                case "UniBridge_UnitySearch":
                    ValidateUnitySearchStep(step.Parameters, report);
                    break;
                case "UniBridge_ContextSnapshot":
                    report.Info("Context snapshot is read-only and has no additional batch validation.");
                    break;
                case "UniBridge_RuntimeProfiler":
                    report.Info("Runtime profiler is read-only and uses bounded sampling. Action=Sample requires Play Mode unless RequirePlayMode=false is passed.");
                    break;
                case "UniBridge_RuntimeStateProbe":
                    report.Info("Runtime state probe is read-only and samples component fields/properties without executing arbitrary project code. Action=Sample and Action=Assert require Play Mode unless RequirePlayMode=false is passed; Action=Assert can fail the batch when required assertions fail.");
                    break;
                case "UniBridge_EditorSnapshot":
                    ValidateEditorSnapshotStep(step.Parameters, report);
                    break;
            }

            return report;
        }

        static void ValidateTilemapStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Tilemap2D step requires 'Action'.");
                return;
            }

            var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect", "creategrid", "createlayer", "createtileasset", "createtile",
                "paintcells", "paint", "erasecells", "erase", "clear", "cleartilemap",
                "compressbounds", "compress"
            };
            if (!knownActions.Contains(normalized))
                report.Error($"Unknown Tilemap2D action '{action}'.");

            if (normalized == "createtileasset" || normalized == "createtile")
            {
                var tilePath = GetString(parameters, "TilePath", "tilePath", "tile_path", "Path", "path");
                if (!string.IsNullOrWhiteSpace(tilePath) && !tilePath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    report.Error($"TilePath '{tilePath}' must end with .asset.");
            }

            if (normalized == "paintcells" || normalized == "paint" || normalized == "erasecells" || normalized == "erase")
            {
                if (parameters["Cells"] == null && parameters["cells"] == null)
                    report.Warning("Paint/erase Tilemap2D actions usually require Cells.");
            }
        }

        static void ValidateInputActionsStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("InputActions step requires 'Action'.");
                return;
            }

            var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect", "create", "createorupdate", "upsert", "addmap",
                "addaction", "addbinding", "addcontrolscheme", "wireplayerinput", "wire",
                "wireplayerinputmanager", "playerinputmanager", "joinmanager",
                "wireuiinputmodule", "uiinputmodule", "inputsystemui",
                "addonscreenbutton", "onscreenbutton", "addonscreenstick", "onscreenstick",
                "addvirtualmouse", "virtualmouse", "addmultiplayereventsystem", "multiplayereventsystem"
            };
            if (!knownActions.Contains(normalized))
                report.Error($"Unknown InputActions action '{action}'.");

            var path = GetString(parameters, "Path", "path", "InputActionsPath", "inputActionsPath", "input_actions_path");
            if (!string.IsNullOrWhiteSpace(path) && !path.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
                report.Error($"InputActions path '{path}' must end with .inputactions.");

            if (!normalized.StartsWith("wire", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("add", StringComparison.OrdinalIgnoreCase) &&
                normalized != "playerinputmanager" &&
                normalized != "joinmanager" &&
                normalized != "uiinputmodule" &&
                normalized != "inputsystemui" &&
                normalized != "onscreenbutton" &&
                normalized != "onscreenstick" &&
                normalized != "virtualmouse" &&
                normalized != "multiplayereventsystem" &&
                string.IsNullOrWhiteSpace(path))
                report.Warning("InputActions asset actions usually require Path/InputActionsPath.");
        }

        static void ValidateConstraintsStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Constraints step requires 'Action'.");
                return;
            }

            var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect", "addconstraint", "add", "create", "setsources", "sources",
                "clearsources", "clear", "applypreset", "preset"
            };
            if (!knownActions.Contains(normalized))
                report.Error($"Unknown Constraints action '{action}'.");

            if (normalized != "inspect" && parameters["Target"] == null && parameters["target"] == null)
                report.Warning("Constraint authoring actions usually require Target unless the object is selected elsewhere.");
        }

        static void ValidateTimelineStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Timeline step requires 'Action'.");
                return;
            }

            var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect", "createasset", "create", "createorupdate", "addtrack",
                "addclip", "createdirector", "binddirector", "bind"
            };
            if (!knownActions.Contains(normalized))
                report.Error($"Unknown Timeline action '{action}'.");

            var path = GetString(parameters, "Path", "path", "TimelinePath", "timelinePath", "timeline_path");
            if (!string.IsNullOrWhiteSpace(path) && !path.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
                report.Error($"Timeline path '{path}' must end with .playable.");

            if ((normalized == "addtrack" || normalized == "addclip" || normalized == "inspect") && string.IsNullOrWhiteSpace(path))
                report.Warning("Timeline asset actions usually require Path/TimelinePath.");
        }

        static void ValidatePhysics2DStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Physics2D step requires 'Action'.");
                return;
            }

            var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect", "applypreset", "preset", "addrigidbody", "rigidbody",
                "addcollider", "collider", "addjoint", "joint", "addeffector",
                "effector", "creatematerial", "createphysicsmaterial", "material"
            };
            if (!knownActions.Contains(normalized))
                report.Error($"Unknown Physics2D action '{action}'.");

            if (normalized == "creatematerial" || normalized == "createphysicsmaterial" || normalized == "material")
            {
                var path = GetString(parameters, "Path", "path", "MaterialPath", "materialPath", "material_path");
                if (!string.IsNullOrWhiteSpace(path) &&
                    !path.EndsWith(".physicsMaterial2D", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    report.Warning("PhysicsMaterial2D paths usually end with .physicsMaterial2D or .asset.");
                }
            }
            else if (parameters["Target"] == null && parameters["target"] == null)
            {
                report.Warning("Physics2D scene actions usually require Target unless a GameObject is selected.");
            }
        }

        static void ValidatePhysics3DStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Physics3D step requires 'Action'.");
                return;
            }

            var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect", "applypreset", "preset", "addrigidbody", "rigidbody",
                "addcollider", "collider", "addjoint", "joint", "addcharactercontroller",
                "charactercontroller", "controller", "creatematerial", "createphysicmaterial", "material"
            };
            if (!knownActions.Contains(normalized))
                report.Error($"Unknown Physics3D action '{action}'.");

            if (normalized == "creatematerial" || normalized == "createphysicmaterial" || normalized == "material")
            {
                var path = GetString(parameters, "Path", "path", "MaterialPath", "materialPath", "material_path");
                if (!string.IsNullOrWhiteSpace(path) &&
                    !path.EndsWith(".physicMaterial", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    report.Warning("PhysicsMaterial paths usually end with .physicMaterial or .asset.");
                }
            }
            else if (parameters["Target"] == null && parameters["target"] == null)
            {
                report.Warning("Physics3D scene actions usually require Target unless a GameObject is selected.");
            }
        }

        static void ValidateNavigationStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Navigation step requires 'Action'.");
                return;
            }

            var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect", "applypreset", "preset", "addagent", "agent", "addobstacle",
                "obstacle", "addoffmeshlink", "offmeshlink", "addsurface", "surface",
                "addmodifier", "modifier", "addmodifiervolume", "modifiervolume",
                "addnavmeshlink", "navmeshlink", "bakesurface", "bake", "buildnavmesh",
                "clearsurface", "clear", "removedata"
            };
            if (!knownActions.Contains(normalized))
                report.Error($"Unknown Navigation action '{action}'.");

            if (parameters["Target"] == null && parameters["target"] == null)
                report.Warning("Navigation scene actions usually require Target unless a GameObject is selected.");
        }

        static void ValidateRenderingStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Rendering step requires 'Action'.");
                return;
            }

            var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect", "applypreset", "preset", "createcamera", "camera", "createlight",
                "light", "createvolume", "volume", "createrig", "rig", "lightrig",
                "setupscenelighting", "scenelighting", "rendersettings",
                "addpixelperfectcamera", "pixelperfectcamera", "pixelperfect",
                "addlight2d", "light2d", "addshadowcaster2d", "shadowcaster2d",
                "addspriteshaperenderer", "spriteshaperenderer", "setup2dscene",
                "setup2drendering", "2drendering", "adddecalprojector",
                "decalprojector", "decal", "addlensflare", "lensflare", "flare",
                "addflarelayer", "flarelayer", "addwindzone", "windzone", "wind",
                "addprojector", "projector", "addlodgroup", "lodgroup", "lod",
                "addreflectionprobe", "reflectionprobe", "reflprobe",
                "addlightprobegroup", "lightprobegroup", "probegrid",
                "addlightprobeproxyvolume", "lightprobeproxyvolume", "probeproxyvolume", "lppv"
            };
            if (!knownActions.Contains(normalized))
                report.Error($"Unknown Rendering action '{action}'.");
        }

        static void ValidateUIToolkitStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("UI Toolkit step requires 'Action'.");
                return;
            }

            var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect", "createdocument", "createuxml", "uxml", "createstylesheet",
                "createuss", "uss", "stylesheet", "createpanelsettings", "panelsettings",
                "attachdocument", "attach", "uidocument", "addelement", "element",
                "setclasses", "classes", "setclasslist", "setinlinestyle", "style", "setstyle"
            };
            if (!knownActions.Contains(normalized))
                report.Error($"Unknown UI Toolkit action '{action}'.");

            var path = GetString(parameters, "Path", "path", "UxmlPath", "uxmlPath", "uxml_path", "DocumentPath", "documentPath", "document_path");
            if (!string.IsNullOrWhiteSpace(path) && !path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) && (normalized.Contains("document") || normalized == "uxml" || normalized == "addelement" || normalized.StartsWith("set")))
                report.Warning("UXML document paths usually end with .uxml.");
        }

        static void ValidateUIStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Info("UI action not specified; Inspect will be used.");
                action = "Inspect";
            }

            var normalizedAction = action.ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect",
                "createcanvas",
                "create_canvas",
                "ensureeventsystem",
                "ensure_event_system",
                "createelement",
                "create_element",
                "createtemplate",
                "create_template",
                "template",
                "createpanel",
                "create_panel",
                "createmodal",
                "create_modal",
                "createtoolbar",
                "create_toolbar",
                "createlist",
                "create_list",
                "createcardgrid",
                "create_card_grid",
                "cardgrid",
                "card_grid",
                "createhud",
                "create_hud",
                "createscrollview",
                "create_scroll_view",
                "scrollview",
                "scroll_view",
                "createscrollrect",
                "create_scroll_rect",
                "addscrollitem",
                "add_scroll_item",
                "addlistitem",
                "add_list_item",
                "listitem",
                "list_item",
                "setgraphic",
                "set_graphic",
                "graphic",
                "setimage",
                "set_image",
                "seticon",
                "set_icon",
                "setbuttonevent",
                "set_button_event",
                "button_event",
                "onclick",
                "on_click",
                "clearbuttonevents",
                "clear_button_events",
                "clear_onclick",
                "clear_on_click",
                "setselectabletransition",
                "set_selectable_transition",
                "selectable_transition",
                "settransition",
                "set_transition",
                "setrecttransformlayout",
                "set_rect_transform_layout",
                "setlayout",
                "set_layout",
                "setrecttransform",
                "set_rect_transform",
                "setrect",
                "set_rect",
                "setlayoutgroup",
                "set_layout_group",
                "layoutgroup",
                "layout_group",
                "setcontentsizefitter",
                "set_content_size_fitter",
                "contentsizefitter",
                "content_size_fitter",
                "setlayoutelement",
                "set_layout_element",
                "layoutelement",
                "layout_element",
                "validate",
                "validateui",
                "validate_ui",
                "validation",
                "audit",
                "auditlayout",
                "audit_layout",
                "checkui",
                "check_ui",
                "repairplan",
                "repair_plan",
                "planrepair",
                "plan_repair",
                "repairui",
                "repair_ui",
                "autofix",
                "auto_fix",
                "fixui",
                "fix_ui"
            };

            if (!knownActions.Contains(normalizedAction))
            {
                report.Error($"Unknown UI action '{action}'.");
                return;
            }

            ValidateVector(parameters, report, "AnchorMin", "anchorMin", "anchor_min");
            ValidateVector(parameters, report, "AnchorMax", "anchorMax", "anchor_max");
            ValidateVector(parameters, report, "Pivot", "pivot");
            ValidateVector(parameters, report, "AnchoredPosition", "anchoredPosition", "anchored_position");
            ValidateVector(parameters, report, "SizeDelta", "sizeDelta", "size_delta");
            ValidateVector(parameters, report, "OffsetMin", "offsetMin", "offset_min");
            ValidateVector(parameters, report, "OffsetMax", "offsetMax", "offset_max");
            ValidateVector(parameters, report, "LocalScale", new[] { "localScale", "local_scale" }, minLength: 2, maxLength: 3);
            ValidateVector(parameters, report, "Color", new[] { "color" }, minLength: 3, maxLength: 4);
            ValidateVector(parameters, report, "BackgroundColor", new[] { "backgroundColor", "background_color" }, minLength: 3, maxLength: 4);
            ValidateVector(parameters, report, "NormalColor", new[] { "normalColor", "normal_color" }, minLength: 3, maxLength: 4);
            ValidateVector(parameters, report, "HighlightedColor", new[] { "highlightedColor", "highlighted_color", "hoverColor", "hover_color" }, minLength: 3, maxLength: 4);
            ValidateVector(parameters, report, "PressedColor", new[] { "pressedColor", "pressed_color", "downColor", "down_color" }, minLength: 3, maxLength: 4);
            ValidateVector(parameters, report, "SelectedColor", new[] { "selectedColor", "selected_color" }, minLength: 3, maxLength: 4);
            ValidateVector(parameters, report, "DisabledColor", new[] { "disabledColor", "disabled_color" }, minLength: 3, maxLength: 4);
            ValidateVector(parameters, report, "Padding", new[] { "padding" }, minLength: 4, maxLength: 4);
            ValidateVector(parameters, report, "Spacing", new[] { "spacing", "gap" }, minLength: 1, maxLength: 2);
            ValidateVector(parameters, report, "CellSize", new[] { "cellSize", "cell_size" }, minLength: 2, maxLength: 2);

            if (normalizedAction is "createcanvas" or "create_canvas" or "ensureeventsystem" or "ensure_event_system")
            {
                report.Info("UI action can create scene objects and supports DryRun.");
                return;
            }

            if (normalizedAction is "createelement" or "create_element" or "createtemplate" or "create_template" or "template" or "createpanel" or "create_panel" or "createmodal" or "create_modal" or "createtoolbar" or "create_toolbar" or "createlist" or "create_list" or "createcardgrid" or "create_card_grid" or "cardgrid" or "card_grid" or "createhud" or "create_hud" or "createscrollview" or "create_scroll_view" or "scrollview" or "scroll_view" or "createscrollrect" or "create_scroll_rect")
            {
                var elementType = GetString(parameters, "ElementType", "elementType", "element_type", "type", "Type");
                if (!string.IsNullOrWhiteSpace(elementType)
                    && (elementType.IndexOf("tmp", StringComparison.OrdinalIgnoreCase) >= 0
                        || elementType.IndexOf("textmeshpro", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    report.Info("TextMesh Pro element creation requires TMPro.TextMeshProUGUI to be available in the Unity project.");
                }

                var parent = GetString(parameters, "Parent", "parent") ?? GetString(parameters, "Target", "target");
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    var matches = FindGameObjects(parent, GetString(parameters, "SearchMethod", "search_method"));
                    if (matches.Count == 0)
                    {
                        report.Error($"UI parent target '{parent}' was not found.");
                    }
                    else if (!matches.Any(go => go.GetComponent<RectTransform>() != null))
                    {
                        report.Warning($"UI parent target '{parent}' was found but does not have a RectTransform. Unity will still allow parenting, but Canvas UI layout may not behave as expected.");
                    }
                }
                else if (!GetBool(parameters, true, "CreateParentCanvas", "createParentCanvas", "create_parent_canvas"))
                {
                    report.Error($"{action} requires Parent/Target when CreateParentCanvas is false.");
                }

                report.Info($"{action} supports DryRun and can create Canvas/EventSystem when requested.");
                return;
            }

            if (normalizedAction is "setcontentsizefitter" or "set_content_size_fitter" or "contentsizefitter" or "content_size_fitter" &&
                parameters["HorizontalFit"] == null && parameters["horizontalFit"] == null && parameters["horizontal_fit"] == null &&
                parameters["VerticalFit"] == null && parameters["verticalFit"] == null && parameters["vertical_fit"] == null)
            {
                report.Warning("SetContentSizeFitter has no fit mode; execution will require HorizontalFit, VerticalFit, or both.");
            }

            if (normalizedAction is "setbuttonevent" or "set_button_event" or "button_event" or "onclick" or "on_click")
            {
                var method = GetString(parameters, "EventMethod", "eventMethod", "event_method", "method", "methodName", "method_name", "onClick", "on_click");
                if (string.IsNullOrWhiteSpace(method))
                {
                    report.Error("SetButtonEvent requires EventMethod/method.");
                }
            }

            var target = GetString(parameters, "Target", "target");
            if (string.IsNullOrWhiteSpace(target))
            {
                report.Warning($"UI action '{action}' has no Target; execution will use the current Unity selection if possible.");
                return;
            }

            var targetMatches = FindGameObjects(target, GetString(parameters, "SearchMethod", "search_method"));
            if (targetMatches.Count == 0)
            {
                report.Error($"UI target '{target}' was not found.");
                return;
            }

            var rectMatches = targetMatches.Where(go => go.GetComponent<RectTransform>() != null).ToArray();
            if (rectMatches.Length == 0)
            {
                report.Error($"UI target '{target}' does not have a RectTransform.");
                return;
            }

            if (normalizedAction is "setrecttransformlayout" or "set_rect_transform_layout" or "setlayout" or "set_layout" &&
                rectMatches.All(go => go.transform.parent is not RectTransform))
            {
                report.Error($"UI target '{target}' needs a RectTransform parent for layout presets.");
            }
        }

        static void ValidateUnityEventStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action");
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Info("UnityEvent action not specified; Inspect will be used.");
                action = "Inspect";
            }

            var normalizedAction = action.Trim()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

            if (normalizedAction is not ("inspect" or "addpersistentcall" or "addlistener" or "add" or "setpersistentcalls" or "setlisteners" or "replace" or "clearpersistentcalls" or "clearlisteners" or "clear"))
            {
                report.Error($"Unsupported UnityEvent action '{action}'.");
                return;
            }

            var target = GetString(parameters, "Target", "target", "gameObject", "game_object");
            if (string.IsNullOrWhiteSpace(target))
            {
                report.Warning("UnityEvent action has no Target; execution will use the current Unity selection if possible.");
            }
            else
            {
                var matches = FindGameObjects(target, GetString(parameters, "SearchMethod", "search_method", "searchMethod"));
                if (matches.Count == 0)
                {
                    report.Error($"UnityEvent target '{target}' was not found.");
                }
            }

            if (normalizedAction is "inspect" or "clearpersistentcalls" or "clearlisteners" or "clear")
            {
                return;
            }

            var hasCalls = HasAnyToken(parameters,
                "PersistentCalls", "persistentCalls", "persistent_calls",
                "PersistentCall", "persistentCall", "persistent_call",
                "MethodName", "methodName", "method_name", "method", "EventMethod", "eventMethod", "event_method");

            if (!hasCalls)
            {
                report.Error("UnityEvent add/set requires PersistentCalls, PersistentCall, or MethodName.");
            }

            if (string.IsNullOrWhiteSpace(GetString(parameters, "EventProperty", "eventProperty", "event_property", "property", "event")))
            {
                report.Warning("UnityEvent mutation has no EventProperty; if the target has multiple UnityEvents, execution will fail as ambiguous.");
            }
        }

        static void ValidateVector(JObject parameters, ValidationReport report, string canonicalName, params string[] aliases)
        {
            ValidateVector(parameters, report, canonicalName, aliases, minLength: 2, maxLength: 2);
        }

        static void ValidateVector(JObject parameters, ValidationReport report, string canonicalName, string[] aliases, int minLength, int maxLength)
        {
            var names = new[] { canonicalName }.Concat(aliases ?? Array.Empty<string>());
            var token = names.Select(name => parameters[name]).FirstOrDefault(value => value != null && value.Type != JTokenType.Null);
            if (token == null)
            {
                return;
            }

            if (token is not JArray array)
            {
                report.Error($"{canonicalName} must be an array.");
                return;
            }

            if (array.Count < minLength || array.Count > maxLength)
            {
                report.Error($"{canonicalName} must contain {minLength}" + (minLength == maxLength ? "" : $"..{maxLength}") + " numeric values.");
                return;
            }

            foreach (var item in array)
            {
                if (item.Type != JTokenType.Integer && item.Type != JTokenType.Float)
                {
                    report.Error($"{canonicalName} must contain only numeric values.");
                    return;
                }
            }
        }

        static void ValidateEditorSnapshotStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Info("Editor snapshot action not specified; Capture will be used.");
                return;
            }

            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "capture",
                "restore",
                "list",
                "inspect",
                "delete",
                "clear"
            };

            if (!knownActions.Contains(action))
            {
                report.Error($"Unknown editor snapshot action '{action}'.");
                return;
            }

            if (action is "inspect" or "delete")
            {
                var snapshotId = GetString(parameters, "SnapshotId", "snapshotId", "snapshot_id");
                if (string.IsNullOrWhiteSpace(snapshotId))
                {
                    report.Error($"Editor snapshot action '{action}' requires SnapshotId.");
                }
            }

            if (action == "restore")
            {
                var snapshotId = GetString(parameters, "SnapshotId", "snapshotId", "snapshot_id");
                var snapshotJson = GetString(parameters, "SnapshotJson", "snapshotJson", "snapshot_json");
                if (string.IsNullOrWhiteSpace(snapshotId) && string.IsNullOrWhiteSpace(snapshotJson))
                {
                    report.Error("Editor snapshot restore requires SnapshotId or SnapshotJson.");
                }

                if (!(GetBool(parameters, true, "DryRun", "dryRun", "dry_run")))
                {
                    report.Warning("Editor snapshot restore may change scenes, selection, Scene View, Prefab Stage, Prefab autosave settings, active dock tabs, and focused window.");
                }
            }
        }

        static void ValidateGameObjectStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "action", "Action")?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("GameObject step requires 'action'.");
                return;
            }

            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "create", "modify", "delete", "find", "get_components", "get_component",
                "add_component", "remove_component", "set_component_property"
            };
            if (!knownActions.Contains(action))
            {
                report.Error($"Unsupported GameObject action '{action}'.");
                return;
            }

            if (action == "create")
            {
                var name = GetString(parameters, "name", "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    report.Warning("Create GameObject without 'name' will use tool defaults if any.");
                }
                else
                {
                    var existing = FindGameObjects(name, "by_name");
                    if (existing.Count > 0)
                    {
                        report.Warning($"A GameObject named '{name}' already exists ({existing.Count} match/es).");
                    }
                }

                ValidateComponentList(parameters["components_to_add"] ?? parameters["ComponentsToAdd"], report, "components_to_add");
                return;
            }

            if (action == "find")
            {
                var searchTerm = GetString(parameters, "search_term", "SearchTerm", "target", "Target");
                if (string.IsNullOrWhiteSpace(searchTerm))
                {
                    report.Error("Find action requires 'search_term' or 'target'.");
                }
                return;
            }

            var target = parameters["target"] ?? parameters["Target"];
            if (target == null || target.Type == JTokenType.Null)
            {
                report.Error($"GameObject action '{action}' requires 'target'.");
            }
            else
            {
                var matches = FindGameObjects(target, GetString(parameters, "search_method", "SearchMethod"));
                if (matches.Count == 0)
                {
                    report.Error($"Target GameObject '{target}' was not found.");
                }
                else if (matches.Count > 1)
                {
                    report.Warning($"Target '{target}' matches {matches.Count} GameObjects; execution may use the first match.");
                }
            }

            if (action == "add_component")
            {
                var componentName = GetString(parameters, "component_name", "ComponentName");
                if (string.IsNullOrWhiteSpace(componentName))
                {
                    ValidateComponentList(parameters["components_to_add"] ?? parameters["ComponentsToAdd"], report, "components_to_add");
                }
                else
                {
                    ValidateComponentType(componentName, report);
                }
            }
            else if (action == "remove_component" || action == "set_component_property" || action == "get_component")
            {
                var componentName = GetString(parameters, "component_name", "ComponentName");
                if (string.IsNullOrWhiteSpace(componentName))
                {
                    report.Error($"GameObject action '{action}' requires 'component_name'.");
                }
                else
                {
                    ValidateComponentType(componentName, report);
                }
            }
        }

        static void ValidateAssetStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Asset step requires 'Action'.");
                return;
            }

            var normalizedAction = action.ToLowerInvariant();
            var path = GetString(parameters, "Path", "path");
            var destination = GetString(parameters, "Destination", "destination");
            var assetType = GetString(parameters, "AssetType", "assetType", "asset_type");

            if (normalizedAction != "search" && string.IsNullOrWhiteSpace(path))
            {
                report.Error($"Asset action '{action}' requires 'Path'.");
                return;
            }

            var normalizedPath = path;
            if (!string.IsNullOrWhiteSpace(path) && !TryNormalizeAssetPath(path, out normalizedPath, out var pathError))
            {
                report.Error(pathError);
                return;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                path = normalizedPath;
            }

            var exists = !string.IsNullOrWhiteSpace(path) && AssetDatabase.LoadAssetAtPath<Object>(path) != null;

            switch (normalizedAction)
            {
                case "create":
                    if (string.IsNullOrWhiteSpace(assetType))
                        report.Error("Asset Create requires 'AssetType'.");
                    if (exists)
                        report.Error($"Asset already exists at '{path}'.");
                    break;
                case "createorupdate":
                case "create_or_update":
                case "upsert":
                    if (string.IsNullOrWhiteSpace(assetType))
                        report.Error("Asset CreateOrUpdate requires 'AssetType'.");
                    if (exists)
                        report.Info($"Asset exists at '{path}' and will be updated if its type matches.");
                    break;
                case "createfolder":
                case "create_folder":
                    if (AssetDatabase.IsValidFolder(path))
                        report.Warning($"Folder already exists at '{path}'.");
                    break;
                case "delete":
                case "modify":
                case "import":
                case "getinfo":
                case "get_info":
                case "getcomponents":
                case "get_components":
                    if (!exists && !AssetDatabase.IsValidFolder(path))
                        report.Error($"Asset not found at '{path}'.");
                    break;
                case "duplicate":
                case "move":
                case "rename":
                    if (!exists && !AssetDatabase.IsValidFolder(path))
                        report.Error($"Source asset not found at '{path}'.");
                    ValidateDestinationPath(destination, report);
                    break;
                case "search":
                    report.Info("Search is read-only.");
                    break;
                default:
                    report.Error($"Unsupported Asset action '{action}'.");
                    break;
            }
        }

        static void ValidateAssetImporterStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Info("AssetImporter action not specified; Inspect will be used.");
                action = "Inspect";
            }

            var normalizedAction = action.ToLowerInvariant().Replace("-", "_");
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect",
                "setproperties",
                "set_properties",
                "set",
                "patch",
                "applypreset",
                "apply_preset",
                "preset",
                "reimport",
                "import"
            };

            if (!knownActions.Contains(normalizedAction))
            {
                report.Error($"Unsupported AssetImporter action '{action}'.");
                return;
            }

            var path = GetString(parameters, "Path", "path", "asset_path", "assetPath");
            var guid = GetString(parameters, "Guid", "guid", "assetGuid", "asset_guid");
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(guid))
            {
                report.Error("AssetImporter step requires Path or Guid.");
                return;
            }

            string normalizedPath = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!TryNormalizeImporterAssetPath(path, out normalizedPath, out var pathError))
                {
                    report.Error(pathError);
                    return;
                }
            }
            else
            {
                normalizedPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    report.Error($"Asset GUID '{guid}' was not found.");
                    return;
                }
            }

            if (AssetImporter.GetAtPath(normalizedPath) == null)
            {
                report.Error($"Asset at '{normalizedPath}' does not have an AssetImporter.");
                return;
            }

            var mutates = normalizedAction is "setproperties" or "set_properties" or "set" or "patch" or "applypreset" or "apply_preset" or "preset" or "reimport" or "import";
            if (mutates && normalizedPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) &&
                !GetBool(parameters, false, "AllowPackages", "allowPackages", "allow_packages"))
            {
                report.Warning($"AssetImporter mutation targets a Packages/... asset. Execution will require AllowPackages=true.");
            }

            if (normalizedAction is "setproperties" or "set_properties" or "set" or "patch" &&
                !HasAnyToken(parameters, "Properties", "properties"))
            {
                report.Error("AssetImporter SetProperties requires Properties.");
            }

            if (normalizedAction is "applypreset" or "apply_preset" or "preset")
            {
                var preset = GetString(parameters, "Preset", "preset");
                if (string.IsNullOrWhiteSpace(preset))
                {
                    report.Error("AssetImporter ApplyPreset requires Preset.");
                }
            }
        }

        static void ValidateMaterialStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Info("Material action not specified; Inspect will be used.");
                action = "Inspect";
            }

            var normalizedAction = action.ToLowerInvariant().Replace("-", "_");
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "inspect",
                "validate",
                "createorupdate",
                "create_or_update",
                "upsert",
                "create",
                "setshader",
                "set_shader",
                "setproperties",
                "set_properties",
                "set",
                "patch",
                "applypreset",
                "apply_preset",
                "preset"
            };

            if (!knownActions.Contains(normalizedAction))
            {
                report.Error($"Unsupported Material action '{action}'.");
                return;
            }

            var path = GetString(parameters, "Path", "path", "asset_path", "assetPath");
            var guid = GetString(parameters, "Guid", "guid", "assetGuid", "asset_guid");
            var shader = GetString(parameters, "Shader", "shader", "shaderPath", "shader_path");

            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(guid))
            {
                if (!(normalizedAction == "validate" && !string.IsNullOrWhiteSpace(shader)))
                {
                    report.Error("Material step requires Path or Guid.");
                    return;
                }
            }

            string normalizedPath = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!TryNormalizeMaterialAssetPath(path, out normalizedPath, out var pathError))
                {
                    report.Error(pathError);
                    return;
                }
            }
            else if (!string.IsNullOrWhiteSpace(guid))
            {
                normalizedPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    report.Error($"Asset GUID '{guid}' was not found.");
                    return;
                }
            }

            var material = !string.IsNullOrWhiteSpace(normalizedPath)
                ? AssetDatabase.LoadAssetAtPath<Material>(normalizedPath)
                : null;
            var existing = !string.IsNullOrWhiteSpace(normalizedPath)
                ? AssetDatabase.LoadAssetAtPath<Object>(normalizedPath)
                : null;

            var mutates = normalizedAction is "createorupdate" or "create_or_update" or "upsert" or "create" or "setshader" or "set_shader" or "setproperties" or "set_properties" or "set" or "patch" or "applypreset" or "apply_preset" or "preset";
            if (mutates && !string.IsNullOrWhiteSpace(normalizedPath) &&
                normalizedPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) &&
                !GetBool(parameters, false, "AllowPackages", "allowPackages", "allow_packages"))
            {
                report.Warning($"Material mutation targets a Packages/... asset. Execution will require AllowPackages=true.");
            }

            if (existing != null && material == null)
            {
                report.Error($"Asset at '{normalizedPath}' is '{existing.GetType().FullName}', not a Material.");
                return;
            }

            switch (normalizedAction)
            {
                case "inspect":
                    if (material == null)
                        report.Error($"Material not found at '{normalizedPath}'.");
                    break;

                case "validate":
                    if (material == null && string.IsNullOrWhiteSpace(shader))
                        report.Warning("Validate will need a material shader or explicit Shader to fully validate shader properties.");
                    break;

                case "createorupdate":
                case "create_or_update":
                case "upsert":
                case "create":
                    if (material == null &&
                        string.IsNullOrWhiteSpace(shader) &&
                        string.IsNullOrWhiteSpace(GetString(parameters, "Preset", "preset")))
                    {
                        report.Error("Material CreateOrUpdate requires Shader or Preset when the material does not exist.");
                    }
                    break;

                case "setshader":
                case "set_shader":
                    if (material == null)
                        report.Error($"Material not found at '{normalizedPath}'.");
                    if (string.IsNullOrWhiteSpace(shader) && !HasAnyToken(parameters, "Properties", "properties"))
                        report.Error("Material SetShader requires Shader or Properties.shader.shaderPath.");
                    break;

                case "setproperties":
                case "set_properties":
                case "set":
                case "patch":
                    if (material == null)
                        report.Error($"Material not found at '{normalizedPath}'.");
                    if (!HasAnyToken(parameters, "Properties", "properties", "Color", "color", "TexturePath", "texturePath", "texture_path") &&
                        !HasAnyToken(parameters, "EnableInstancing", "enableInstancing", "DoubleSidedGI", "doubleSidedGI", "RenderQueue", "renderQueue", "Keywords", "keywords", "EnableKeywords", "enableKeywords", "DisableKeywords", "disableKeywords"))
                    {
                        report.Error("Material SetProperties requires Properties or material setting parameters.");
                    }
                    break;

                case "applypreset":
                case "apply_preset":
                case "preset":
                    if (material == null)
                        report.Error($"Material not found at '{normalizedPath}'.");
                    if (string.IsNullOrWhiteSpace(GetString(parameters, "Preset", "preset")))
                        report.Error("Material ApplyPreset requires Preset.");
                    break;
            }
        }

        static bool TryNormalizeMaterialAssetPath(string path, out string normalizedPath, out string error)
        {
            normalizedPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Material path is empty.";
                return false;
            }

            var candidate = path.Replace('\\', '/').Trim();
            if (candidate.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring("unity://path/".Length);
            }

            if (candidate.Contains("../", StringComparison.Ordinal) ||
                candidate.Contains("/..", StringComparison.Ordinal) ||
                candidate.Contains(":", StringComparison.Ordinal) ||
                Path.IsPathRooted(candidate))
            {
                error = $"Material path must not contain traversal or absolute roots: '{path}'.";
                return false;
            }

            if (!candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "Assets/" + candidate.TrimStart('/');
            }

            if (string.IsNullOrWhiteSpace(Path.GetExtension(candidate)))
                candidate += ".mat";

            if (!candidate.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Material path must use .mat extension: '{candidate}'.";
                return false;
            }

            normalizedPath = candidate.TrimEnd('/');
            return true;
        }

        static void ValidateScriptableObjectStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("ScriptableObject step requires 'Action'.");
                return;
            }

            var normalizedAction = action.ToLowerInvariant();
            var path = GetString(parameters, "Path", "path", "asset_path", "assetPath");
            var guid = GetString(parameters, "Guid", "guid", "assetGuid", "asset_guid");
            var typeName = GetString(parameters, "ScriptableObjectType", "scriptableObjectType", "scriptable_object_type", "Type", "type", "scriptClass", "script_class");
            var hasProps = HasAnyToken(parameters, "Properties", "properties", "props") || HasAnyToken(parameters, "ScriptableObject", "scriptableObject", "scriptable_object");

            string normalizedPath = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!TryNormalizeScriptableObjectAssetPath(path, appendExtension: normalizedAction is "createorupdate" or "create_or_update" or "upsert" or "create", out normalizedPath, out var pathError))
                {
                    report.Error(pathError);
                    return;
                }
            }
            else if (!string.IsNullOrWhiteSpace(guid))
            {
                normalizedPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    report.Error($"Asset GUID '{guid}' was not found.");
                    return;
                }
            }

            var asset = !string.IsNullOrWhiteSpace(normalizedPath)
                ? AssetDatabase.LoadAssetAtPath<ScriptableObject>(normalizedPath)
                : null;
            var existing = !string.IsNullOrWhiteSpace(normalizedPath)
                ? AssetDatabase.LoadMainAssetAtPath(normalizedPath)
                : null;

            if (existing != null && asset == null)
            {
                report.Error($"Asset at '{normalizedPath}' is '{existing.GetType().FullName}', not a ScriptableObject.");
                return;
            }

            var mutates = normalizedAction is "createorupdate" or "create_or_update" or "upsert" or "create" or "setproperties" or "set_properties" or "set" or "patch";
            if (mutates && !string.IsNullOrWhiteSpace(normalizedPath) &&
                normalizedPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) &&
                !GetBool(parameters, false, "AllowPackages", "allowPackages", "allow_packages"))
            {
                report.Warning($"ScriptableObject mutation targets a Packages/... asset. Execution will require AllowPackages=true.");
            }

            switch (normalizedAction)
            {
                case "listtypes":
                case "list_types":
                case "types":
                case "catalog":
                    break;

                case "inspect":
                case "get":
                case "info":
                    if (string.IsNullOrWhiteSpace(normalizedPath))
                        report.Error("ScriptableObject Inspect requires Path or Guid.");
                    else if (asset == null)
                        report.Error($"ScriptableObject asset not found at '{normalizedPath}'.");
                    break;

                case "validate":
                case "check":
                    if (string.IsNullOrWhiteSpace(normalizedPath) && string.IsNullOrWhiteSpace(typeName))
                        report.Warning("Validate without Path/Guid or ScriptableObjectType can only check request shape.");
                    break;

                case "createorupdate":
                case "create_or_update":
                case "upsert":
                case "create":
                    if (string.IsNullOrWhiteSpace(normalizedPath))
                        report.Error("ScriptableObject CreateOrUpdate requires Path or Guid.");
                    if (asset == null && string.IsNullOrWhiteSpace(typeName) && !TryExtractScriptableObjectType(parameters, out _))
                        report.Error("ScriptableObject CreateOrUpdate requires ScriptableObjectType when the asset does not exist.");
                    break;

                case "setproperties":
                case "set_properties":
                case "set":
                case "patch":
                    if (string.IsNullOrWhiteSpace(normalizedPath))
                        report.Error("ScriptableObject SetProperties requires Path or Guid.");
                    else if (asset == null)
                        report.Error($"ScriptableObject asset not found at '{normalizedPath}'.");
                    if (!hasProps)
                        report.Error("ScriptableObject SetProperties requires Properties or ScriptableObject.props.");
                    break;
            }
        }

        static bool TryExtractScriptableObjectType(JObject parameters, out string typeName)
        {
            typeName = GetString(parameters, "ScriptableObjectType", "scriptableObjectType", "scriptable_object_type", "Type", "type", "scriptClass", "script_class");
            if (!string.IsNullOrWhiteSpace(typeName))
                return true;

            var scriptableObject = parameters["ScriptableObject"] as JObject
                ?? parameters["scriptableObject"] as JObject
                ?? parameters["scriptable_object"] as JObject;
            typeName = scriptableObject?["scriptableObjectType"]?.ToString()
                ?? scriptableObject?["type"]?.ToString()
                ?? scriptableObject?["scriptClass"]?.ToString();
            return !string.IsNullOrWhiteSpace(typeName);
        }

        static bool TryNormalizeScriptableObjectAssetPath(string path, bool appendExtension, out string normalizedPath, out string error)
        {
            normalizedPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "ScriptableObject path is empty.";
                return false;
            }

            var candidate = path.Replace('\\', '/').Trim();
            if (candidate.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring("unity://path/".Length);
            }

            if (candidate.Contains("../", StringComparison.Ordinal) ||
                candidate.Contains("/..", StringComparison.Ordinal) ||
                candidate.Contains(":", StringComparison.Ordinal) ||
                Path.IsPathRooted(candidate))
            {
                error = $"ScriptableObject path must not contain traversal or absolute roots: '{path}'.";
                return false;
            }

            if (!candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "Assets/" + candidate.TrimStart('/');
            }

            if (appendExtension && string.IsNullOrWhiteSpace(Path.GetExtension(candidate)))
                candidate += ".asset";

            normalizedPath = candidate.TrimEnd('/');
            return true;
        }

        static bool TryNormalizeImporterAssetPath(string path, out string normalizedPath, out string error)
        {
            normalizedPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Asset path is empty.";
                return false;
            }

            var candidate = path.Replace('\\', '/').Trim();
            if (candidate.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring("unity://path/".Length);
            }

            if (candidate.Contains("../", StringComparison.Ordinal) ||
                candidate.Contains("/..", StringComparison.Ordinal) ||
                candidate.Contains(":", StringComparison.Ordinal) ||
                Path.IsPathRooted(candidate))
            {
                error = $"Asset path must not contain traversal or absolute roots: '{path}'.";
                return false;
            }

            if (!candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "Assets/" + candidate.TrimStart('/');
            }

            normalizedPath = candidate.TrimEnd('/');
            return true;
        }

        static void ValidateSceneStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Scene step requires 'Action'.");
                return;
            }

            var normalizedAction = action.ToLowerInvariant();
            var name = GetString(parameters, "Name", "name");
            var path = GetString(parameters, "Path", "path");
            var buildIndex = parameters["BuildIndex"] ?? parameters["buildIndex"] ?? parameters["build_index"];

            switch (normalizedAction)
            {
                case "create":
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        report.Error("Scene Create requires 'Name'.");
                        return;
                    }
                    var scenePath = BuildScenePath(name, path);
                    if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) != null)
                    {
                        report.Error($"Scene already exists at '{scenePath}'.");
                    }
                    break;
                case "load":
                    if (buildIndex != null && buildIndex.Type != JTokenType.Null)
                    {
                        if (!int.TryParse(buildIndex.ToString(), out var index) || index < 0 || index >= SceneManager.sceneCountInBuildSettings)
                        {
                            report.Error($"BuildIndex '{buildIndex}' is outside 0..{SceneManager.sceneCountInBuildSettings - 1}.");
                        }
                    }
                    else
                    {
                        var loadPath = BuildScenePath(name, path);
                        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(path))
                            report.Error("Scene Load requires Name/Path or BuildIndex.");
                        else if (AssetDatabase.LoadAssetAtPath<SceneAsset>(loadPath) == null)
                            report.Error($"Scene asset not found at '{loadPath}'.");
                    }
                    break;
                case "save":
                case "gethierarchy":
                case "get_hierarchy":
                case "getactive":
                case "get_active":
                case "getbuildsettings":
                case "get_build_settings":
                    report.Info($"Scene action '{action}' is valid.");
                    break;
                default:
                    report.Error($"Unsupported Scene action '{action}'.");
                    break;
            }
        }

        static void ValidatePrefabStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "action", "Action")?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Prefab step requires 'action'.");
                return;
            }

            switch (action)
            {
                case "create":
                    ValidatePrefabCreate(parameters, report);
                    break;
                case "create_from_asset":
                case "createfromasset":
                    ValidateExistingAssetPath(GetString(parameters, "asset_path", "assetPath", "AssetPath", "source_asset_path", "sourceAssetPath"), report, "asset_path");
                    ValidatePrefabDestination(GetString(parameters, "prefab_path", "prefabPath", "PrefabPath"), report, required: true);
                    break;
                case "instantiate":
                    ValidateExistingAssetPath(GetPrefabSourcePath(parameters), report, "prefab_path/asset_path");
                    break;
                case "get_status":
                case "getstatus":
                case "status":
                case "diff_overrides":
                case "diffoverrides":
                case "list_overrides":
                case "listoverrides":
                case "inspect_overrides":
                case "inspectoverrides":
                    ValidatePrefabStatusTarget(parameters, report);
                    break;
                case "apply_overrides":
                case "applyoverrides":
                case "revert_overrides":
                case "revertoverrides":
                case "unpack":
                    ValidateRequiredGameObjectTarget(parameters, report, "target");
                    break;
                case "apply_override":
                case "applyoverride":
                case "apply_selected_overrides":
                case "applyselectedoverrides":
                case "revert_override":
                case "revertoverride":
                case "revert_selected_overrides":
                case "revertselectedoverrides":
                    ValidateRequiredGameObjectTarget(parameters, report, "target");
                    ValidatePrefabOverrideSelector(parameters, report);
                    break;
                case "remove_unused_overrides":
                case "removeunusedoverrides":
                case "cleanup_overrides":
                case "cleanupoverrides":
                    ValidateRequiredGameObjectTarget(parameters, report, "target");
                    break;
                case "create_variant":
                case "createvariant":
                    ValidateExistingAssetPath(GetString(parameters, "source_prefab_path", "sourcePrefabPath", "SourcePrefabPath", "source_asset_path", "sourceAssetPath"), report, "source_prefab_path/source_asset_path");
                    ValidatePrefabDestination(GetString(parameters, "variant_path", "variantPath", "VariantPath"), report, required: true);
                    break;
                case "open_stage":
                case "openstage":
                    ValidateExistingAssetPath(GetPrefabSourcePath(parameters), report, "prefab_path/asset_path");
                    break;
                case "save_stage":
                case "savestage":
                case "close_stage":
                case "closestage":
                    report.Info($"Prefab stage action '{action}' is valid.");
                    break;
                default:
                    report.Error($"Unsupported Prefab action '{action}'.");
                    break;
            }
        }

        static void ValidatePrefabOverrideSelector(JObject parameters, ValidationReport report)
        {
            var hasSelector =
                !string.IsNullOrWhiteSpace(GetString(parameters, "override_id", "overrideId", "id")) ||
                HasAnyToken(parameters, "override_ids", "overrideIds", "ids") ||
                !string.IsNullOrWhiteSpace(GetString(parameters, "override_kind", "overrideKind", "kind")) ||
                !string.IsNullOrWhiteSpace(GetString(parameters, "object_path", "objectPath", "path")) ||
                !string.IsNullOrWhiteSpace(GetString(parameters, "component_type", "componentType", "type")) ||
                !string.IsNullOrWhiteSpace(GetString(parameters, "property_path", "propertyPath"));

            if (!hasSelector)
            {
                report.Error("Selected prefab override actions require override_id, override_ids, override_kind, object_path, component_type, or property_path.");
            }

            var overrideKind = GetString(parameters, "override_kind", "overrideKind", "kind");
            if (!string.IsNullOrWhiteSpace(overrideKind))
            {
                var normalized = overrideKind.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
                var validKinds = new HashSet<string>
                {
                    "property",
                    "property_override",
                    "object",
                    "object_override",
                    "added_component",
                    "removed_component",
                    "added_gameobject",
                    "added_game_object",
                    "removed_gameobject",
                    "removed_game_object"
                };

                if (!validKinds.Contains(normalized))
                {
                    report.Error($"Unsupported prefab override kind '{overrideKind}'.");
                }
            }
        }

        static void ValidateAnimatorControllerStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "action", "Action")?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Animator Controller step requires 'action'.");
                return;
            }

            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "search", "list",
                "inspect", "get_info", "getinfo",
                "validate",
                "create",
                "apply_graph", "applygraph", "create_or_update_graph", "createorupdategraph",
                "add_parameter", "addparameter",
                "remove_parameter", "removeparameter",
                "add_layer", "addlayer",
                "remove_layer", "removelayer",
                "add_state_machine", "addstatemachine", "add_sub_state_machine", "addsubstatemachine",
                "remove_state_machine", "removestatemachine", "remove_sub_state_machine", "removesubstatemachine",
                "add_state", "addstate",
                "remove_state", "removestate",
                "set_state_motion", "setstatemotion",
                "create_blend_tree", "createblendtree",
                "configure_blend_tree", "configureblendtree",
                "add_blend_child", "addblendchild",
                "set_blend_children", "setblendchildren",
                "clear_blend_children", "clearblendchildren",
                "set_default_state", "setdefaultstate",
                "add_transition", "addtransition",
                "add_entry_transition", "addentrytransition",
                "remove_transition", "removetransition",
                "remove_entry_transition", "removeentrytransition"
            };

            if (!knownActions.Contains(action))
            {
                report.Error($"Unsupported Animator Controller action '{action}'.");
                return;
            }

            if (action == "search" || action == "list")
            {
                return;
            }

            var path = GetString(parameters, "path", "Path", "controller_path", "controllerPath", "asset_path", "assetPath");
            if (string.IsNullOrWhiteSpace(path))
            {
                report.Error("Animator Controller action requires 'path' or 'controller_path'.");
                return;
            }

            if (!TryNormalizeAssetPath(path, out var normalizedPath, out var pathError))
            {
                report.Error(pathError);
                return;
            }

            if (!normalizedPath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
            {
                report.Error($"Animator Controller path must end with .controller: '{normalizedPath}'.");
                return;
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(normalizedPath);
            if (action == "create")
            {
                if (controller != null)
                    report.Error($"Animator Controller already exists at '{normalizedPath}'.");

                var parent = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(parent) || !AssetDatabase.IsValidFolder(parent))
                    report.Warning($"Parent folder '{parent}' does not exist; execution will create it if possible.");
                return;
            }

            if (IsApplyAnimatorGraphAction(action))
            {
                var createIfMissing = GetBool(parameters, true, "create_if_missing", "createIfMissing");
                if (controller == null && !createIfMissing)
                    report.Error($"Animator Controller not found at '{normalizedPath}' and create_if_missing is false.");

                if (controller == null)
                {
                    var parent = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(parent) || !AssetDatabase.IsValidFolder(parent))
                        report.Warning($"Parent folder '{parent}' does not exist; execution will create it if possible.");
                }

                ValidateAnimatorGraphSpec(parameters, controller, report);
                return;
            }

            if (controller == null)
            {
                report.Error($"Animator Controller not found at '{normalizedPath}'.");
                return;
            }

            switch (action)
            {
                case "inspect":
                case "get_info":
                case "getinfo":
                case "validate":
                    report.Info($"Animator Controller action '{action}' is valid.");
                    break;

                case "add_parameter":
                case "addparameter":
                    ValidateAnimatorParameter(parameters, controller, report, mustExist: false);
                    var type = GetString(parameters, "type", "Type", "parameter_type", "parameterType");
                    if (!IsKnownAnimatorParameterType(type))
                        report.Error($"Animator parameter type must be Float, Int, Bool, or Trigger. Received '{type}'.");
                    break;

                case "remove_parameter":
                case "removeparameter":
                    ValidateAnimatorParameter(parameters, controller, report, mustExist: true);
                    break;

                case "add_layer":
                case "addlayer":
                    var newLayer = GetString(parameters, "name", "Name", "layer", "Layer", "layer_name", "layerName");
                    if (string.IsNullOrWhiteSpace(newLayer))
                        report.Error("Add layer requires 'name' or 'layer'.");
                    else if (controller.layers.Any(layer => string.Equals(layer.name, newLayer, StringComparison.Ordinal)))
                        report.Error($"Animator layer '{newLayer}' already exists.");
                    break;

                case "remove_layer":
                case "removelayer":
                    if (!HasAnyToken(parameters, "layer_index", "layerIndex", "LayerIndex") &&
                        string.IsNullOrWhiteSpace(GetString(parameters, "layer", "Layer", "name", "Name", "layer_name", "layerName")))
                    {
                        report.Error("Remove layer requires 'layer_index', 'layer', or 'name'.");
                    }
                    ValidateAnimatorLayerReference(parameters, controller, report, allowNameAsLayer: true);
                    break;

                case "add_state":
                case "addstate":
                    ValidateAnimatorLayerReference(parameters, controller, report);
                    var stateToAdd = GetString(parameters, "state", "State", "name", "Name");
                    if (string.IsNullOrWhiteSpace(stateToAdd))
                    {
                        report.Error("Add state requires 'state' or 'name'.");
                    }
                    else if (TryResolveLayerIndex(parameters, controller, out var addLayerIndex) &&
                             AnimatorStateExists(controller.layers[addLayerIndex].stateMachine, stateToAdd))
                    {
                        report.Error($"Animator state '{stateToAdd}' already exists in layer '{controller.layers[addLayerIndex].name}'.");
                    }
                    ValidateOptionalMotion(parameters, report);
                    break;

                case "add_state_machine":
                case "addstatemachine":
                case "add_sub_state_machine":
                case "addsubstatemachine":
                    ValidateAnimatorLayerReference(parameters, controller, report);
                    if (string.IsNullOrWhiteSpace(GetString(parameters, "state_machine", "stateMachine", "StateMachine", "name", "Name")))
                        report.Error("Add state machine requires 'state_machine' or 'name'.");
                    break;

                case "remove_state_machine":
                case "removestatemachine":
                case "remove_sub_state_machine":
                case "removesubstatemachine":
                    ValidateAnimatorLayerReference(parameters, controller, report);
                    if (string.IsNullOrWhiteSpace(GetString(parameters, "state_machine", "stateMachine", "StateMachine", "name", "Name")))
                        report.Error("Remove state machine requires 'state_machine' or 'name'.");
                    break;

                case "remove_state":
                case "removestate":
                case "set_default_state":
                case "setdefaultstate":
                    ValidateAnimatorLayerReference(parameters, controller, report);
                    ValidateExistingAnimatorState(parameters, controller, report, GetString(parameters, "state", "State", "name", "Name"));
                    break;

                case "set_state_motion":
                case "setstatemotion":
                    ValidateAnimatorLayerReference(parameters, controller, report);
                    ValidateExistingAnimatorState(parameters, controller, report, GetString(parameters, "state", "State", "name", "Name"));
                    ValidateRequiredMotion(parameters, report);
                    break;

                case "create_blend_tree":
                case "createblendtree":
                    ValidateAnimatorLayerReference(parameters, controller, report);
                    ValidateAnimatorBlendTreeCreate(parameters, controller, report);
                    break;

                case "configure_blend_tree":
                case "configureblendtree":
                case "add_blend_child":
                case "addblendchild":
                case "set_blend_children":
                case "setblendchildren":
                case "clear_blend_children":
                case "clearblendchildren":
                    ValidateAnimatorLayerReference(parameters, controller, report);
                    ValidateExistingAnimatorBlendTree(parameters, controller, report);
                    ValidateAnimatorBlendTreeSettings(parameters, controller, report);
                    if (action == "add_blend_child" || action == "addblendchild")
                        ValidateAnimatorBlendChild(parameters, controller, report);
                    if (action == "set_blend_children" || action == "setblendchildren")
                        ValidateAnimatorBlendChildren(parameters["children"] ?? parameters["Children"], controller, report, required: true);
                    break;

                case "add_transition":
                case "addtransition":
                case "add_entry_transition":
                case "addentrytransition":
                    ValidateAnimatorLayerReference(parameters, controller, report);
                    ValidateAnimatorTransition(parameters, controller, report, adding: true);
                    break;

                case "remove_transition":
                case "removetransition":
                case "remove_entry_transition":
                case "removeentrytransition":
                    ValidateAnimatorLayerReference(parameters, controller, report);
                    ValidateAnimatorTransition(parameters, controller, report, adding: false);
                    break;
            }
        }

        static void ValidateAnimationClipStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("AnimationClip step requires 'Action'.");
                return;
            }

            var normalizedAction = action.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "search",
                "list",
                "inspect",
                "getinfo",
                "create",
                "createorupdate",
                "upsert",
                "setcurves",
                "setcurve",
                "setevents",
                "setevent",
                "clearcurves",
                "clearevents"
            };

            if (!knownActions.Contains(normalizedAction))
                report.Error($"Unknown AnimationClip action '{action}'.");

            if (normalizedAction != "search" && normalizedAction != "list")
            {
                var path = GetString(parameters, "Path", "path", "AssetPath", "assetPath", "asset_path");
                if (string.IsNullOrWhiteSpace(path))
                    report.Warning("AnimationClip write/inspect actions usually require Path.");
                else if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                    report.Error($"AnimationClip Path '{path}' must end with .anim.");
            }
        }

        static void ValidateEditorStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Error("Editor step requires 'Action'.");
                return;
            }

            var normalizedAction = action.ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "play", "requestplaymodenowait", "request_play_mode_no_wait", "request_play_no_wait", "play_no_wait", "enter_play_mode_no_wait",
                "waitforplaymode", "wait_for_play_mode", "waitplay", "wait_play",
                "waitforeditmode", "wait_for_edit_mode", "waitedit", "wait_edit",
                "pause", "stop", "exitplaymode", "exit_play_mode",
                "getstate", "get_state", "getplaymodestate", "get_play_mode_state", "getprojectroot", "get_project_root",
                "getwindows", "get_windows", "getactivetool", "get_active_tool", "getselection", "get_selection",
                "getprefabstage", "get_prefab_stage", "setactivetool", "set_active_tool",
                "addtag", "add_tag", "removetag", "remove_tag", "gettags", "get_tags",
                "addlayer", "add_layer", "removelayer", "remove_layer", "getlayers", "get_layers",
                "selectasset", "select_asset", "selectgameobject", "select_game_object",
                "clearselection", "clear_selection", "pingselection", "ping_selection",
                "frameselection", "frame_selection", "waitforready", "wait_for_ready",
                "waitforreadyafterreload", "wait_for_ready_after_reload", "waitforreconnect", "wait_for_reconnect", "wait_after_reload",
                "waitidle", "wait_idle",
                "refreshassets", "refresh_assets", "requestscriptcompilation", "request_script_compilation",
                "requestscriptcompilationnowait", "request_script_compilation_no_wait", "compile_no_wait",
                "compile", "saveall", "save_all", "saveassets", "save_assets", "generatesolutionfiles", "generate_solution_files",
                "generatesolutionfile", "generate_solution_file",
                "getcompilationdiagnostics", "get_compilation_diagnostics", "compilationdiagnostics", "compilation_diagnostics",
                "reloadcheckpoint", "reload_checkpoint", "reloadfromcheckpoint", "reload_from_checkpoint", "refreshandreload", "refresh_and_reload",
                "syncsolution", "sync_solution"
            };

            if (!knownActions.Contains(normalizedAction))
            {
                report.Error($"Unsupported Editor action '{action}'.");
                return;
            }

            if (normalizedAction == "setactivetool" || normalizedAction == "set_active_tool")
            {
                var toolName = GetString(parameters, "ToolName", "toolName", "tool_name");
                if (string.IsNullOrWhiteSpace(toolName))
                    report.Error("SetActiveTool requires 'ToolName'.");
            }
            else if (normalizedAction == "addtag" || normalizedAction == "add_tag" || normalizedAction == "removetag" || normalizedAction == "remove_tag")
            {
                var tagName = GetString(parameters, "TagName", "tagName", "tag_name");
                if (string.IsNullOrWhiteSpace(tagName))
                    report.Error($"{action} requires 'TagName'.");
            }
            else if (normalizedAction == "addlayer" || normalizedAction == "add_layer" || normalizedAction == "removelayer" || normalizedAction == "remove_layer")
            {
                var layerName = GetString(parameters, "LayerName", "layerName", "layer_name");
                if (string.IsNullOrWhiteSpace(layerName))
                    report.Error($"{action} requires 'LayerName'.");
            }
            else if (normalizedAction == "selectasset" || normalizedAction == "select_asset")
            {
                var assetPath = GetString(parameters, "AssetPath", "assetPath", "asset_path", "Target", "target", "path");
                if (string.IsNullOrWhiteSpace(assetPath))
                    report.Error("SelectAsset requires 'AssetPath' or 'Target'.");
            }
            else if (normalizedAction == "selectgameobject" || normalizedAction == "select_game_object")
            {
                var gameObjectPath = GetString(parameters, "GameObjectPath", "gameObjectPath", "game_object_path", "Target", "target", "name");
                var instanceId = GetString(parameters, "InstanceID", "instanceID", "instanceId", "instance_id", "id");
                if (string.IsNullOrWhiteSpace(gameObjectPath) && string.IsNullOrWhiteSpace(instanceId))
                    report.Error("SelectGameObject requires 'GameObjectPath', 'Target', or 'InstanceID'.");
            }
        }

        static void ValidateScriptValidationStep(JObject parameters, ValidationReport report)
        {
            var uri = GetString(parameters,
                "Uri", "uri", "URI",
                "Path", "path",
                "AssetPath", "assetPath", "asset_path",
                "ScriptPath", "scriptPath", "script_path",
                "File", "file");

            if (string.IsNullOrWhiteSpace(uri))
            {
                report.Error("ValidateScript step requires 'Uri' or a script path.");
                return;
            }

            if (!TryNormalizeValidateScriptUri(uri, out var scriptPath, out var pathError))
            {
                report.Error(pathError);
                return;
            }

            if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                report.Error($"ValidateScript Uri '{uri}' must resolve to a .cs file.");
            }

            if (!scriptPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                report.Error($"ValidateScript Uri '{uri}' must resolve under Assets/.");
            }
            else if (AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath) == null && !File.Exists(ToProjectAbsolutePath(scriptPath)))
            {
                report.Error($"Script file was not found at '{scriptPath}'.");
            }

            var level = GetString(parameters, "Level", "level", "ValidationLevel", "validationLevel", "validation_level") ?? "basic";
            if (!string.Equals(level, "basic", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(level, "standard", StringComparison.OrdinalIgnoreCase))
            {
                report.Error("ValidateScript Level must be 'basic' or 'standard'.");
            }

            report.Info("ValidateScript is read-only in BatchActions; no Undo group or rollback snapshot is required for this step.");
        }

        static void ValidateAdditiveSceneRegistrationStep(JObject parameters, ValidationReport report)
        {
            var scenePath = GetString(parameters, "ScenePath", "scenePath", "scene_path", "Path", "path");
            var sceneName = GetString(parameters, "SceneName", "sceneName", "scene_name", "Name", "name");

            if (string.IsNullOrWhiteSpace(scenePath) && string.IsNullOrWhiteSpace(sceneName))
            {
                report.Error("ValidateAdditiveSceneRegistration requires ScenePath or SceneName.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(scenePath))
            {
                if (!TryNormalizeAssetPath(scenePath, out var normalizedScenePath, out var pathError))
                {
                    report.Error(pathError);
                }
                else if (!normalizedScenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    report.Error($"ScenePath '{scenePath}' must resolve to a .unity asset.");
                }
                else if (!File.Exists(ToProjectAbsolutePath(normalizedScenePath)))
                {
                    report.Error($"ScenePath '{normalizedScenePath}' was not found.");
                }
            }

            var metadataPath = GetString(parameters, "MetadataAssetPath", "metadataAssetPath", "metadata_asset_path", "MetadataPath", "metadataPath");
            if (!string.IsNullOrWhiteSpace(metadataPath))
            {
                if (!TryNormalizeAssetPath(metadataPath, out var normalizedMetadataPath, out var metadataError))
                {
                    report.Error(metadataError);
                }
                else if (!normalizedMetadataPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    report.Error($"MetadataAssetPath '{metadataPath}' must resolve to a .asset file.");
                }
            }

            var scenesManagerPath = GetString(parameters, "ScenesManagerPrefabPath", "scenesManagerPrefabPath", "scenes_manager_prefab_path");
            if (!string.IsNullOrWhiteSpace(scenesManagerPath))
            {
                if (!TryNormalizeAssetPath(scenesManagerPath, out var normalizedScenesManagerPath, out var scenesManagerError))
                {
                    report.Error(scenesManagerError);
                }
                else if (!normalizedScenesManagerPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    report.Error($"ScenesManagerPrefabPath '{scenesManagerPath}' must resolve to a .prefab file.");
                }
            }

            report.Info("ValidateAdditiveSceneRegistration is read-only in BatchActions; no Undo group or rollback snapshot is required for this step.");
        }

        static void ValidateShaderStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            var name = GetString(parameters, "Name", "name");
            var path = GetString(parameters, "Path", "path") ?? "Assets/Shaders";

            if (string.IsNullOrWhiteSpace(action))
                report.Error("Shader step requires 'Action'.");
            if (string.IsNullOrWhiteSpace(name))
                report.Error("Shader step requires 'Name'.");

            if (!string.IsNullOrWhiteSpace(path) && !TryNormalizeAssetPath(path, out _, out var pathError, allowFolder: true))
                report.Error(pathError);

            var normalizedAction = action?.ToLowerInvariant();
            if (normalizedAction == "create" || normalizedAction == "update")
            {
                var hasContents = !string.IsNullOrEmpty(GetString(parameters, "Contents", "contents")) ||
                                  !string.IsNullOrEmpty(GetString(parameters, "EncodedContents", "encodedContents", "encoded_contents"));
                if (!hasContents)
                    report.Error($"Shader {action} requires Contents or EncodedContents.");
            }
        }

        static void ValidateCaptureStep(JObject parameters, ValidationReport report)
        {
            var target = GetString(parameters, "Target", "target");
            if (!string.IsNullOrWhiteSpace(target))
            {
                var matches = FindGameObjects(target, GetString(parameters, "SearchMethod", "search_method"));
                if (matches.Count == 0)
                {
                    report.Error($"Capture target '{target}' was not found.");
                }
            }

            var action = GetString(parameters, "Action", "action");
            if (string.Equals(action, "CaptureGameCamera", StringComparison.OrdinalIgnoreCase))
            {
                var cameraName = GetString(parameters, "Camera", "camera");
                if (!string.IsNullOrWhiteSpace(cameraName) && FindGameObjects(cameraName, GetString(parameters, "SearchMethod", "search_method")).Count == 0)
                {
                    report.Warning($"Camera target '{cameraName}' was not found as a GameObject; CaptureView may still resolve it by Camera search rules.");
                }
            }
        }

        static void ValidateVisualSceneAuditStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action")?.Trim();
            var normalizedAction = (action ?? "AuditCapture")
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();
            var knownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "auditcapture", "capture", "auditimage", "image", "auditscene", "scene"
            };

            if (!knownActions.Contains(normalizedAction))
                report.Error($"Unsupported VisualSceneAudit action '{action}'.");

            var target = GetString(parameters, "Target", "target");
            if (!string.IsNullOrWhiteSpace(target))
            {
                var matches = FindGameObjects(target, GetString(parameters, "SearchMethod", "search_method", "searchMethod"));
                if (matches.Count == 0)
                    report.Error($"VisualSceneAudit target '{target}' was not found.");
            }

            var camera = GetString(parameters, "Camera", "camera");
            if (!string.IsNullOrWhiteSpace(camera) &&
                FindGameObjects(camera, GetString(parameters, "SearchMethod", "search_method", "searchMethod")).Count == 0)
            {
                report.Warning($"VisualSceneAudit camera '{camera}' was not found as a GameObject; the tool may still resolve it by Camera search rules.");
            }

            if (normalizedAction is "auditimage" or "image" &&
                string.IsNullOrWhiteSpace(GetString(parameters, "ImagePath", "imagePath", "image_path", "Path", "path")))
            {
                report.Error("AuditImage requires ImagePath/path.");
            }
        }

        static void ValidateCaptureAssetStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action");
            var normalizedAction = (action ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(action) &&
                normalizedAction is not ("capture" or "render" or "preview" or "capturegrid" or "grid" or "browse" or "capturecontactsheet" or "assetcontactsheet" or "contactsheet" or "multiview" or "multiviewsheet"))
            {
                report.Error($"Unsupported CaptureAsset action '{action}'.");
            }

            var isGrid = normalizedAction is "capturegrid" or "grid" or "browse";
            if (isGrid)
            {
                var hasGridInput = HasAnyToken(parameters,
                    "Path", "path", "asset_path", "assetPath",
                    "Guid", "guid", "assetGuid", "asset_guid",
                    "Paths", "paths",
                    "Guids", "guids",
                    "Folder", "folder",
                    "Folders", "folders",
                    "Query", "query",
                    "SearchPattern", "searchPattern", "search_pattern",
                    "Types", "types", "assetTypes", "asset_types");

                if (!hasGridInput)
                    report.Error("CaptureGrid requires Paths/Guids, Path/Guid, Folder/Folders, Query/SearchPattern, or Types.");

                var maxResultsToken = parameters["MaxResults"] ?? parameters["maxResults"] ?? parameters["max_results"] ?? parameters["Limit"] ?? parameters["limit"];
                if (maxResultsToken != null &&
                    int.TryParse(maxResultsToken.ToString(), out var maxResults) &&
                    (maxResults < 1 || maxResults > 100))
                {
                    report.Warning("CaptureGrid MaxResults is clamped to 1..100.");
                }

                return;
            }

            var path = GetString(parameters, "Path", "path", "asset_path", "assetPath");
            var guid = GetString(parameters, "Guid", "guid", "assetGuid", "asset_guid");
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(guid))
            {
                report.Error("CaptureAsset requires Path or Guid.");
                return;
            }

            string normalizedPath = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!TryNormalizeCaptureAssetPath(path, out normalizedPath, out var pathError))
                {
                    report.Error(pathError);
                    return;
                }
            }
            else
            {
                normalizedPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    report.Error($"Asset GUID '{guid}' was not found.");
                    return;
                }
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(normalizedPath);
            if (asset == null)
            {
                report.Error($"Asset not found at '{normalizedPath}'.");
                return;
            }

            if (asset is not GameObject &&
                asset is not Mesh &&
                asset is not Material &&
                asset is not Texture2D &&
                asset is not Sprite)
            {
                report.Warning($"Asset '{normalizedPath}' is '{asset.GetType().FullName}'. CaptureAsset currently renders GameObject/prefab/model, Mesh, Material, Texture2D, and Sprite assets.");
            }
        }

        static void ValidateCaptureUIToolkitStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action");
            var normalizedAction = (action ?? "Capture").Trim()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

            if (normalizedAction is not ("capture" or "inspect" or "listuxml" or "list" or "search"))
            {
                report.Error($"Unsupported CaptureUIToolkit action '{action}'.");
                return;
            }

            if (normalizedAction is "listuxml" or "list" or "search")
            {
                ValidateOptionalLimit(parameters, report, "Limit", 1, 200, "CaptureUIToolkit Limit is clamped to 1..200.");
                ValidateCaptureUIToolkitFolders(parameters, report);
                return;
            }

            var path = GetString(parameters, "Path", "path", "documentPath", "document_path", "uxml", "Uxml");
            var guid = GetString(parameters, "Guid", "guid", "assetGuid", "asset_guid");
            var target = GetString(parameters, "Target", "target", "gameObject", "game_object");
            var query = GetString(parameters, "Query", "query", "search", "Search");
            if (string.IsNullOrWhiteSpace(path) &&
                string.IsNullOrWhiteSpace(guid) &&
                string.IsNullOrWhiteSpace(target) &&
                string.IsNullOrWhiteSpace(query))
            {
                report.Error("CaptureUIToolkit requires Path, Guid, Target, or Query.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!TryNormalizeCaptureAssetPath(path, out var normalizedPath, out var pathError))
                {
                    report.Error(pathError);
                    return;
                }

                if (AssetDatabase.GetMainAssetTypeAtPath(normalizedPath) != typeof(UnityEngine.UIElements.VisualTreeAsset))
                    report.Error($"Path '{normalizedPath}' is not a UXML VisualTreeAsset.");
            }

            if (!string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(AssetDatabase.GUIDToAssetPath(guid)))
                report.Error($"Asset GUID '{guid}' was not found.");

            ValidateOptionalLimit(parameters, report, "MaxTreeDepth", 1, 32, "CaptureUIToolkit MaxTreeDepth is clamped to 1..32.");
            ValidateOptionalLimit(parameters, report, "MaxTreeItems", 1, 1000, "CaptureUIToolkit MaxTreeItems is clamped to 1..1000.");
            ValidateOptionalLimit(parameters, report, "Width", 64, 4096, "CaptureUIToolkit Width is clamped to 64..4096.");
            ValidateOptionalLimit(parameters, report, "Height", 64, 4096, "CaptureUIToolkit Height is clamped to 64..4096.");
            ValidateOptionalLimit(parameters, report, "RenderPasses", 1, 8, "CaptureUIToolkit RenderPasses is clamped to 1..8.");

            var readbackMode = GetString(parameters, "ReadbackMode", "readbackMode", "readback_mode", "captureBackend", "capture_backend");
            if (!string.IsNullOrWhiteSpace(readbackMode) &&
                !Enum.TryParse<CaptureReadbackMode>(readbackMode, ignoreCase: true, out _))
            {
                report.Error($"Unsupported CaptureUIToolkit ReadbackMode '{readbackMode}'. Use Immediate or GpuReadback.");
            }
        }

        static void ValidateCaptureUIToolkitFolders(JObject parameters, ValidationReport report)
        {
            var folders = parameters["Folders"] ?? parameters["folders"];
            if (folders is not JArray array)
                return;

            foreach (var token in array)
            {
                var folder = token?.ToString();
                if (string.IsNullOrWhiteSpace(folder))
                    continue;
                if (!TryNormalizeCaptureAssetPath(folder, out var normalizedFolder, out var error))
                {
                    report.Warning(error);
                    continue;
                }

                if (!AssetDatabase.IsValidFolder(normalizedFolder))
                    report.Warning($"CaptureUIToolkit folder '{normalizedFolder}' was not found.");
            }
        }

        static void ValidateSceneObjectViewStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action");
            var normalizedAction = (action ?? "Hierarchy").Trim()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(action))
                report.Info("SceneObjectView action not specified; Hierarchy will be used.");

            if (normalizedAction is not ("hierarchy" or "tree" or "view" or "inspect" or "object" or "objects" or "selection" or "selected"))
            {
                report.Error($"Unsupported SceneObjectView action '{action}'.");
                return;
            }

            if (normalizedAction is "view" or "inspect" or "object" or "objects")
            {
                var target = GetString(parameters, "Target", "target", "gameObject", "game_object", "path", "Path", "name", "Name");
                var targets = parameters["Targets"] ?? parameters["targets"] ?? parameters["gameObjects"] ?? parameters["game_objects"];
                if (string.IsNullOrWhiteSpace(target) && targets == null)
                    report.Info("SceneObjectView View has no Target/Targets; it will inspect the current Unity selection if available.");
            }

            ValidateOptionalLimit(parameters, report, "MaxDepth", 0, 12, "SceneObjectView MaxDepth is clamped to 0..12.");
            ValidateOptionalLimit(parameters, report, "MaxObjects", 1, 2000, "SceneObjectView MaxObjects is clamped to 1..2000.");
            ValidateOptionalLimit(parameters, report, "MaxChildren", 0, 500, "SceneObjectView MaxChildren is clamped to 0..500.");
            ValidateOptionalLimit(parameters, report, "MaxRoots", 1, 1000, "SceneObjectView MaxRoots is clamped to 1..1000.");
            ValidateOptionalLimit(parameters, report, "MaxSerializedProperties", 1, 500, "SceneObjectView MaxSerializedProperties is clamped to 1..500.");
        }

        static void ValidateOptionalLimit(JObject parameters, ValidationReport report, string name, int min, int max, string warning)
        {
            var token = parameters[name] ?? parameters[char.ToLowerInvariant(name[0]) + name.Substring(1)];
            if (token != null &&
                int.TryParse(token.ToString(), out var value) &&
                (value < min || value > max))
            {
                report.Warning(warning);
            }
        }

        static void ValidateTypeSchemaStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action");
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Info("TypeSchema action not specified; Inspect will be used.");
                action = "Inspect";
            }

            var normalizedAction = action.Trim()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

            if (normalizedAction is not ("inspect" or "schema" or "get" or "info" or
                                         "listtypes" or "types" or "search" or "catalog" or
                                         "inspectshader" or "shaderschema" or
                                         "inspectasset" or "assetschema" or
                                         "inspectgameobject" or "gameobjectschema" or "components"))
            {
                report.Error($"Unsupported TypeSchema action '{action}'.");
                return;
            }

            var hasTypeInput = HasAnyToken(parameters, "TypeName", "typeName", "type", "component", "componentType", "class",
                "TypeNames", "typeNames", "types");
            var hasAssetInput = HasAnyToken(parameters, "Path", "path", "asset_path", "assetPath", "Guid", "guid", "assetGuid", "asset_guid");
            var hasShaderInput = HasAnyToken(parameters, "Shader", "shader", "shaderPath", "shader_path", "shaderName", "shader_name");
            var hasTargetInput = HasAnyToken(parameters, "Target", "target", "gameObject", "game_object", "gameObjectPath", "game_object_path");

            switch (normalizedAction)
            {
                case "listtypes":
                case "types":
                case "search":
                case "catalog":
                    ValidateTypeSchemaLimit(parameters, report);
                    return;
                case "inspectshader":
                case "shaderschema":
                    if (!hasShaderInput && !hasAssetInput)
                        report.Error("InspectShader requires Shader or Path/Guid.");
                    ValidateOptionalTypeSchemaAsset(parameters, report);
                    return;
                case "inspectasset":
                case "assetschema":
                    if (!hasAssetInput)
                        report.Error("InspectAsset requires Path or Guid.");
                    ValidateOptionalTypeSchemaAsset(parameters, report);
                    return;
                case "inspectgameobject":
                case "gameobjectschema":
                case "components":
                    ValidateTypeSchemaTarget(parameters, report);
                    ValidateComponentList(parameters["ComponentTypes"] ?? parameters["componentTypes"] ?? parameters["component_types"], report, "ComponentTypes");
                    return;
            }

            if (!hasTypeInput && !hasAssetInput && !hasShaderInput && !hasTargetInput)
            {
                report.Error("Inspect requires TypeName/TypeNames, Shader, Path/Guid, or Target.");
                return;
            }

            if (hasTargetInput)
            {
                ValidateTypeSchemaTarget(parameters, report);
                ValidateComponentList(parameters["ComponentTypes"] ?? parameters["componentTypes"] ?? parameters["component_types"], report, "ComponentTypes");
            }

            ValidateOptionalTypeSchemaAsset(parameters, report);
            ValidateTypeSchemaLimit(parameters, report);
        }

        static void ValidateTypeSchemaTarget(JObject parameters, ValidationReport report)
        {
            var target = GetString(parameters, "Target", "target", "gameObject", "game_object", "gameObjectPath", "game_object_path");
            if (string.IsNullOrWhiteSpace(target))
            {
                report.Error("InspectGameObject requires Target.");
                return;
            }

            var matches = FindGameObjects(target, GetString(parameters, "SearchMethod", "searchMethod", "search_method"));
            if (matches.Count == 0)
                report.Error($"TypeSchema target GameObject '{target}' was not found.");
            else if (matches.Count > 1)
                report.Warning($"TypeSchema target '{target}' matches {matches.Count} GameObjects; execution may use the first match.");
        }

        static void ValidateOptionalTypeSchemaAsset(JObject parameters, ValidationReport report)
        {
            var path = GetString(parameters, "Path", "path", "asset_path", "assetPath");
            var guid = GetString(parameters, "Guid", "guid", "assetGuid", "asset_guid");

            if (!string.IsNullOrWhiteSpace(guid))
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(guidPath))
                    report.Error($"Asset GUID '{guid}' was not found.");
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!TryNormalizeCaptureAssetPath(path, out var normalizedPath, out var pathError))
            {
                report.Error(pathError);
                return;
            }

            if (AssetDatabase.LoadMainAssetAtPath(normalizedPath) == null &&
                AssetImporter.GetAtPath(normalizedPath) == null)
            {
                report.Warning($"Asset/importer was not found at '{normalizedPath}'. TypeSchema execution will fail unless Unity can resolve the input differently.");
            }
        }

        static void ValidateTypeSchemaLimit(JObject parameters, ValidationReport report)
        {
            var limitToken = parameters["Limit"] ?? parameters["limit"];
            if (limitToken != null &&
                int.TryParse(limitToken.ToString(), out var limit) &&
                (limit < 1 || limit > 500))
            {
                report.Warning("TypeSchema Limit is clamped to 1..500.");
            }

            var serializedLimitToken = parameters["MaxSerializedProperties"] ?? parameters["maxSerializedProperties"] ?? parameters["max_serialized_properties"];
            if (serializedLimitToken != null &&
                int.TryParse(serializedLimitToken.ToString(), out var serializedLimit) &&
                (serializedLimit < 1 || serializedLimit > 1000))
            {
                report.Warning("TypeSchema MaxSerializedProperties is clamped to 1..1000.");
            }
        }

        static void ValidateUnitySearchStep(JObject parameters, ValidationReport report)
        {
            var action = GetString(parameters, "Action", "action");
            if (string.IsNullOrWhiteSpace(action))
            {
                report.Info("UnitySearch action not specified; Search will be used.");
                action = "Search";
            }

            var normalizedAction = action.Trim()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

            if (normalizedAction is not ("search" or "find" or "lookup" or "resolve" or "best" or "selection" or "selected"))
            {
                report.Error($"Unsupported UnitySearch action '{action}'.");
                return;
            }

            if (normalizedAction is "selection" or "selected")
                return;

            var hasInput = HasAnyToken(parameters,
                "Query", "query", "search", "Search", "SearchPattern", "searchPattern", "search_pattern",
                "Path", "path", "asset_path", "assetPath",
                "Guid", "guid", "assetGuid", "asset_guid",
                "Target", "target", "gameObject", "game_object");

            if (!hasInput)
                report.Error("UnitySearch requires Query, Path, Guid, or Target.");

            var limitToken = parameters["Limit"] ?? parameters["limit"] ?? parameters["maxResults"] ?? parameters["max_results"];
            if (limitToken != null &&
                int.TryParse(limitToken.ToString(), out var limit) &&
                (limit < 1 || limit > 300))
            {
                report.Warning("UnitySearch Limit is clamped to 1..300.");
            }

            var perSourceToken = parameters["PerSourceLimit"] ?? parameters["perSourceLimit"] ?? parameters["per_source_limit"];
            if (perSourceToken != null &&
                int.TryParse(perSourceToken.ToString(), out var perSourceLimit) &&
                (perSourceLimit < 1 || perSourceLimit > 150))
            {
                report.Warning("UnitySearch PerSourceLimit is clamped to 1..150.");
            }
        }

        static bool TryNormalizeCaptureAssetPath(string path, out string normalizedPath, out string error)
        {
            normalizedPath = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Asset path is empty.";
                return false;
            }

            var candidate = path.Replace('\\', '/').Trim();
            if (candidate.StartsWith("unity://path/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring("unity://path/".Length);
            }

            if (candidate.Contains("../", StringComparison.Ordinal) ||
                candidate.Contains("/..", StringComparison.Ordinal) ||
                candidate.Contains(":", StringComparison.Ordinal) ||
                Path.IsPathRooted(candidate))
            {
                error = $"Asset path must not contain traversal or absolute roots: '{path}'.";
                return false;
            }

            if (!candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "Assets/" + candidate.TrimStart('/');
            }

            normalizedPath = candidate.TrimEnd('/');
            return true;
        }
    }
}
