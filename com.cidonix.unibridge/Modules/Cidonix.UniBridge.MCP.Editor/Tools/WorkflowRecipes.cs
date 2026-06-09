#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using Cidonix.UniBridge.MCP.Editor.ToolRegistry;
using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Cidonix.UniBridge.MCP.Editor.Tools
{
    /// <summary>
    /// Builds higher-level Unity workflow recipes as validated UniBridge_BatchActions plans.
    /// </summary>
    public static class WorkflowRecipes
    {
        const string ToolName = "UniBridge_WorkflowRecipes";
        const int DefaultMaxAssets = 24;
        const int HardMaxAssets = 100;

        public const string Title = "Run Unity workflow recipes";

        public const string Description = @"Build, dry-run, or execute higher-level Unity workflows using UniBridge_BatchActions.

Use this when an agent wants a complete, opinionated workflow instead of manually composing many lower-level tool calls. Recipes are readably expanded into BatchActions steps and keep BatchActions safety defaults: dry-run first, validation before execute, undo group, and asset rollback.

Actions:
    List: Show available recipes and their inputs.
    Describe: Show one recipe.
    BuildBatch: Return the generated UniBridge_BatchActions payload without running it.
    DryRun: Generate the batch and validate it with DryRun=true.
    Execute: Generate the batch and execute it with DryRun=false unless DryRun=true is explicitly supplied.

Recipes:
    CreateInventoryScreen
    ImportSpriteFolderAs2D
    CreateSpriteMaterialAndPreview
    CreateHUDFromAssets
    SetupClickableUIButton
    CreateScriptableConfigAndBindToScene
    RunCoreSmokeTest
    RunUISmokeTest
    RunAssetSmokeTest";

        [McpTool(ToolName, Description, Title, Groups = new[] { "core", "workflow", "editor", "assets", "ui" }, EnabledByDefault = true)]
        public static async Task<object> HandleCommand(WorkflowRecipesParams parameters)
        {
            parameters ??= new WorkflowRecipesParams();

            try
            {
                if (parameters.Action == WorkflowRecipeAction.List)
                {
                    return Response.Success("Listed UniBridge workflow recipes.", new
                    {
                        recipes = GetRecipeDefinitions().Select(ToDefinitionObject).ToArray()
                    });
                }

                var definition = ResolveRecipe(parameters.Recipe);
                if (definition == null)
                {
                    return Response.Error($"Unknown workflow recipe '{parameters.Recipe}'. Use Action=List to see available recipes.");
                }

                if (parameters.Action == WorkflowRecipeAction.Describe)
                {
                    return Response.Success($"Described workflow recipe '{definition.Name}'.", ToDefinitionObject(definition));
                }

                var build = BuildRecipeBatch(definition, parameters);
                if (build.Errors.Count > 0)
                {
                    return Response.Error($"Workflow recipe '{definition.Name}' is missing required input.", new
                    {
                        recipe = definition.Name,
                        errors = build.Errors.ToArray(),
                        warnings = build.Warnings.ToArray(),
                        definition = ToDefinitionObject(definition)
                    });
                }

                if (parameters.Action == WorkflowRecipeAction.BuildBatch)
                {
                    return Response.Success($"Built batch for workflow recipe '{definition.Name}'.", new
                    {
                        recipe = definition.Name,
                        warnings = build.Warnings.ToArray(),
                        batch = build.Batch
                    });
                }

                var dryRun = parameters.Action == WorkflowRecipeAction.DryRun || parameters.DryRun == true;
                build.Batch["DryRun"] = dryRun;

                var result = await McpToolRegistry.ExecuteToolInsideCurrentLeaseAsync("UniBridge_BatchActions", build.Batch);
                var resultJson = JObject.FromObject(result);
                var batchSucceeded = resultJson.Value<bool?>("success") ?? true;
                var message = dryRun
                    ? $"Workflow recipe '{definition.Name}' dry-run completed."
                    : $"Workflow recipe '{definition.Name}' execution completed.";
                var data = new
                {
                    recipe = definition.Name,
                    dryRun,
                    warnings = build.Warnings.ToArray(),
                    batch = build.Batch,
                    batchResult = resultJson
                };

                return batchSucceeded
                    ? Response.Success(message, data)
                    : Response.Error(message, data);
            }
            catch (Exception ex)
            {
                return Response.Error($"Workflow recipe failed: {ex.Message}");
            }
        }

        static RecipeBuild BuildRecipeBatch(RecipeDefinition definition, WorkflowRecipesParams parameters)
        {
            var build = new RecipeBuild
            {
                IncludePostCreateSteps = parameters.Action == WorkflowRecipeAction.Execute && parameters.DryRun != true
            };
            var steps = definition.Name switch
            {
                "CreateInventoryScreen" => BuildCreateInventoryScreen(parameters, build),
                "ImportSpriteFolderAs2D" => BuildImportSpriteFolderAs2D(parameters, build),
                "CreateSpriteMaterialAndPreview" => BuildCreateSpriteMaterialAndPreview(parameters, build),
                "CreateHUDFromAssets" => BuildCreateHudFromAssets(parameters, build),
                "SetupClickableUIButton" => BuildSetupClickableUIButton(parameters, build),
                "CreateScriptableConfigAndBindToScene" => BuildCreateScriptableConfigAndBindToScene(parameters, build),
                "RunCoreSmokeTest" => BuildRunCoreSmokeTest(parameters, build),
                "RunUISmokeTest" => BuildRunUiSmokeTest(parameters, build),
                "RunAssetSmokeTest" => BuildRunAssetSmokeTest(parameters, build),
                _ => Array.Empty<JObject>()
            };

            build.Batch = new JObject
            {
                ["DryRun"] = true,
                ["ValidateBeforeExecute"] = true,
                ["StopOnError"] = true,
                ["UseUndoGroup"] = true,
                ["RollbackOnFailure"] = true,
                ["RollbackAssets"] = true,
                ["Name"] = $"Recipe: {definition.Name}",
                ["Steps"] = new JArray(steps)
            };

            return build;
        }

        static JObject[] BuildCreateInventoryScreen(WorkflowRecipesParams parameters, RecipeBuild build)
        {
            var name = Clean(parameters.Name, "UniBridge Inventory");
            var items = ReadStringArray(parameters.Options, "items", "itemTexts")
                ?? parameters.AssetPaths
                ?? new[] { "Oxygen Tank x2", "Battery Cell x5", "Repair Kit", "Depth Beacon", "Sonar Chip" };
            var actions = ReadStringArray(parameters.Options, "actions", "actionTexts") ?? new[] { "Use", "Drop", "Close" };
            var size = ReadFloatArray(parameters.Options, "size", "sizeDelta") ?? new[] { 640f, 520f };

            var steps = new List<JObject>
            {
                Step("create_inventory_screen", "Create a list-based inventory UI template.", "ui", new JObject
                {
                    ["Action"] = "CreateTemplate",
                    ["TemplateType"] = "List",
                    ["Name"] = name,
                    ["Parent"] = NullIfEmpty(parameters.Parent),
                    ["Title"] = OptionString(parameters, "title", name),
                    ["Subtitle"] = OptionString(parameters, "subtitle", "Inventory"),
                    ["ItemTexts"] = new JArray(items),
                    ["ActionTexts"] = new JArray(actions),
                    ["SizeDelta"] = new JArray(size),
                    ["CreateParentCanvas"] = true,
                    ["UseTextMeshPro"] = true,
                    ["ValidateAfterCreate"] = true
                })
            };

            if (build.IncludePostCreateSteps)
            {
                steps.Add(Step("validate_inventory_screen", "Audit the created inventory UI.", "ui", new JObject
                {
                    ["Action"] = "Validate",
                    ["Target"] = name,
                    ["IncludeInactive"] = true,
                    ["MaxIssues"] = 80
                }));
            }
            else
            {
                build.Warnings.Add("Post-create UI audit is omitted from dry-run/build batches because the target UI does not exist until execution.");
            }

            return steps.ToArray();
        }

        static JObject[] BuildImportSpriteFolderAs2D(WorkflowRecipesParams parameters, RecipeBuild build)
        {
            var folder = NormalizeAssetPath(parameters.Folder);
            if (string.IsNullOrWhiteSpace(folder))
            {
                build.Errors.Add("Folder is required for ImportSpriteFolderAs2D.");
                return Array.Empty<JObject>();
            }

            var maxAssets = ClampMaxAssets(parameters.MaxAssets);
            var assets = FindTextureAssets(folder, maxAssets);
            if (assets.Length == 0)
            {
                build.Errors.Add($"No Texture2D assets found under '{folder}'.");
                return Array.Empty<JObject>();
            }

            var ppu = OptionFloat(parameters, 100f, "pixelsPerUnit", "ppu");
            var maxTextureSize = OptionInt(parameters, null, "maxTextureSize");
            var compressionQuality = OptionInt(parameters, null, "compressionQuality");

            var steps = assets.Select((path, index) =>
            {
                var args = new JObject
                {
                    ["Action"] = "ApplyPreset",
                    ["Path"] = path,
                    ["Preset"] = "TextureSprite2D",
                    ["SpritePixelsPerUnit"] = ppu,
                    ["Reimport"] = true
                };
                if (maxTextureSize.HasValue)
                    args["MaxTextureSize"] = maxTextureSize.Value;
                if (compressionQuality.HasValue)
                    args["CompressionQuality"] = compressionQuality.Value;

                return Step($"sprite_import_{index + 1:00}", $"Apply TextureSprite2D importer preset to {path}.", "asset_importer", args);
            }).ToList();

            steps.Add(Step("capture_sprite_grid", "Render a contact sheet for imported sprites.", "asset_capture", new JObject
            {
                ["Action"] = "CaptureGrid",
                ["Paths"] = new JArray(assets),
                ["Types"] = new JArray("Sprite", "Texture2D"),
                ["MaxResults"] = assets.Length,
                ["Tag"] = Clean(parameters.Name, "sprite_folder_recipe"),
                ["IncludeLabels"] = true
            }, optional: true));

            return steps.ToArray();
        }

        static JObject[] BuildCreateSpriteMaterialAndPreview(WorkflowRecipesParams parameters, RecipeBuild build)
        {
            var spritePath = NormalizeAssetPath(parameters.SpritePath ?? parameters.AssetPath);
            if (string.IsNullOrWhiteSpace(spritePath))
            {
                build.Errors.Add("SpritePath or AssetPath is required for CreateSpriteMaterialAndPreview.");
                return Array.Empty<JObject>();
            }

            var materialPath = NormalizeAssetPath(parameters.MaterialPath);
            if (string.IsNullOrWhiteSpace(materialPath))
            {
                var baseName = Clean(parameters.Name, Path.GetFileNameWithoutExtension(spritePath));
                materialPath = $"Assets/UniBridgeRecipes/Materials/{baseName}.mat";
            }

            var steps = new List<JObject>
            {
                Step("create_sprite_material", "Create or update a sprite-friendly material using the sprite/texture as main texture.", "material", new JObject
                {
                    ["Action"] = "CreateOrUpdate",
                    ["Path"] = materialPath,
                    ["Preset"] = OptionString(parameters, "preset", "SpriteDefault"),
                    ["TexturePath"] = spritePath,
                    ["Color"] = parameters.Options?["color"]?.DeepClone()
                })
            };

            if (build.IncludePostCreateSteps)
            {
                steps.Add(Step("preview_sprite_material", "Capture a material preview PNG.", "asset_capture", new JObject
                {
                    ["Action"] = "Capture",
                    ["Path"] = materialPath,
                    ["View"] = "Auto",
                    ["TransparentBackground"] = true,
                    ["Tag"] = Clean(parameters.Name, "sprite_material_recipe")
                }, optional: true));
            }
            else
            {
                build.Warnings.Add("Material preview capture is omitted from dry-run/build batches because the material asset may not exist until execution.");
            }

            return steps.ToArray();
        }

        static JObject[] BuildCreateHudFromAssets(WorkflowRecipesParams parameters, RecipeBuild build)
        {
            var name = Clean(parameters.Name, "UniBridge HUD");
            var labels = ReadStringArray(parameters.Options, "items", "itemTexts")
                ?? parameters.AssetPaths?.Select(Path.GetFileNameWithoutExtension).ToArray()
                ?? new[] { "Health 100", "Oxygen 100", "Depth 0m", "Signal Online" };
            var actions = ReadStringArray(parameters.Options, "actions", "actionTexts") ?? new[] { "Map", "Inventory", "Pause" };

            var steps = new List<JObject>
            {
                Step("create_hud", "Create a HUD template with status labels and action buttons.", "ui", new JObject
                {
                    ["Action"] = "CreateTemplate",
                    ["TemplateType"] = "HUD",
                    ["Name"] = name,
                    ["Parent"] = NullIfEmpty(parameters.Parent),
                    ["Title"] = OptionString(parameters, "title", name),
                    ["Subtitle"] = OptionString(parameters, "subtitle", null),
                    ["ItemTexts"] = new JArray(labels),
                    ["ActionTexts"] = new JArray(actions),
                    ["CreateParentCanvas"] = true,
                    ["UseTextMeshPro"] = true,
                    ["ValidateAfterCreate"] = true
                })
            };

            if (build.IncludePostCreateSteps)
            {
                steps.Add(Step("validate_hud", "Audit the created HUD UI.", "ui", new JObject
                {
                    ["Action"] = "Validate",
                    ["Target"] = name,
                    ["IncludeInactive"] = true,
                    ["MaxIssues"] = 80
                }));
            }
            else
            {
                build.Warnings.Add("Post-create HUD audit is omitted from dry-run/build batches because the target UI does not exist until execution.");
            }

            return steps.ToArray();
        }

        static JObject[] BuildSetupClickableUIButton(WorkflowRecipesParams parameters, RecipeBuild build)
        {
            var target = parameters.Target;
            if (string.IsNullOrWhiteSpace(target))
            {
                build.Errors.Add("Target is required for SetupClickableUIButton.");
            }
            if (string.IsNullOrWhiteSpace(parameters.EventMethod))
            {
                build.Errors.Add("EventMethod is required for SetupClickableUIButton.");
            }
            if (build.Errors.Count > 0)
            {
                return Array.Empty<JObject>();
            }

            var steps = new List<JObject>
            {
                Step("inspect_button", "Inspect the target button before binding.", "ui", new JObject
                {
                    ["Action"] = "Inspect",
                    ["Target"] = target
                }),
                Step("bind_button_event", "Bind a persistent Button.onClick listener.", "ui", new JObject
                {
                    ["Action"] = "SetButtonEvent",
                    ["Target"] = target,
                    ["EventTarget"] = NullIfEmpty(parameters.EventTarget),
                    ["EventComponent"] = NullIfEmpty(parameters.EventComponent),
                    ["EventMethod"] = parameters.EventMethod,
                    ["EventArgument"] = NullIfEmpty(parameters.EventArgument),
                    ["EventArgumentType"] = string.IsNullOrWhiteSpace(parameters.EventArgumentType) ? "Void" : parameters.EventArgumentType,
                    ["ClearExistingEvents"] = OptionBool(parameters, true, "clearExisting", "clearExistingEvents")
                }),
                Step("inspect_button_event", "Verify the persistent Button.onClick listener.", "unity_event", new JObject
                {
                    ["Action"] = "Inspect",
                    ["Target"] = target,
                    ["Component"] = "Button",
                    ["EventProperty"] = "onClick"
                })
            };

            return steps.ToArray();
        }

        static JObject[] BuildCreateScriptableConfigAndBindToScene(WorkflowRecipesParams parameters, RecipeBuild build)
        {
            var path = NormalizeAssetPath(parameters.ScriptableObjectPath ?? parameters.AssetPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                var baseName = Clean(parameters.Name, "RecipeConfig");
                path = $"Assets/UniBridgeRecipes/Configs/{baseName}.asset";
            }

            if (string.IsNullOrWhiteSpace(parameters.ScriptableObjectType))
            {
                build.Errors.Add("ScriptableObjectType is required for CreateScriptableConfigAndBindToScene.");
                return Array.Empty<JObject>();
            }

            var properties = parameters.Options?["properties"] as JObject ?? new JObject();
            var steps = new List<JObject>
            {
                Step("create_scriptable_config", "Create or update the ScriptableObject config asset.", "scriptable_object", new JObject
                {
                    ["Action"] = "CreateOrUpdate",
                    ["Path"] = path,
                    ["ScriptableObjectType"] = parameters.ScriptableObjectType,
                    ["Properties"] = properties.DeepClone()
                })
            };

            if (build.IncludePostCreateSteps)
            {
                steps.Add(Step("inspect_scriptable_config", "Inspect the ScriptableObject config asset.", "scriptable_object", new JObject
                {
                    ["Action"] = "Inspect",
                    ["Path"] = path,
                    ["IncludeSerializedProperties"] = true,
                    ["MaxSerializedProperties"] = 120
                }));
            }
            else
            {
                build.Warnings.Add("Post-create ScriptableObject inspect is omitted from dry-run/build batches because the asset may not exist until execution.");
            }

            var componentName = OptionString(parameters, "componentName", null);
            var propertyName = OptionString(parameters, "propertyName", null);
            if (!build.IncludePostCreateSteps)
            {
                build.Warnings.Add("Scene binding is omitted from dry-run/build batches because the config asset may not exist until execution.");
            }
            else if (!string.IsNullOrWhiteSpace(parameters.Target) &&
                !string.IsNullOrWhiteSpace(componentName) &&
                !string.IsNullOrWhiteSpace(propertyName))
            {
                steps.Add(Step("bind_config_to_scene", "Assign the config asset reference to a scene component property.", "game_object", new JObject
                {
                    ["action"] = "set_component_property",
                    ["target"] = parameters.Target,
                    ["component_name"] = componentName,
                    ["component_properties"] = new JObject
                    {
                        [componentName] = new JObject
                        {
                            [propertyName] = path
                        }
                    }
                }));
            }
            else
            {
                build.Warnings.Add("Scene binding skipped. Provide Target plus Options.componentName and Options.propertyName to assign the created asset to a component.");
            }

            return steps.ToArray();
        }

        static JObject[] BuildRunCoreSmokeTest(WorkflowRecipesParams parameters, RecipeBuild build)
        {
            var name = Clean(parameters.Name, $"UniBridge_CoreSmoke_{DateTime.Now:HHmmssfff}");
            var targetPath = "/" + name;
            var steps = new List<JObject>
            {
                Step("guide_overview", "Read the agent-facing UniBridge guide.", "tool_guide", new JObject
                {
                    ["Action"] = "Overview"
                }),
                Step("console_diagnostic", "Read console health before the scene smoke.", "console", new JObject
                {
                    ["Action"] = "DiagnosticSummary"
                }, optional: true),
                Step("create_probe_game_object", "Create a temporary scene GameObject with a simple component.", "game_object", new JObject
                {
                    ["Action"] = "Create",
                    ["Name"] = name,
                    ["Position"] = new JArray(0f, 0f, 0f),
                    ["ComponentsToAdd"] = new JArray("BoxCollider")
                })
            };

            if (build.IncludePostCreateSteps)
            {
                steps.Add(Step("inspect_probe_components", "Inspect components on the temporary GameObject.", "game_object", new JObject
                {
                    ["Action"] = "GetComponents",
                    ["Target"] = targetPath,
                    ["SearchMethod"] = "ByPath",
                    ["IncludeNonPublicSerialized"] = false
                }));
                steps.Add(Step("view_probe_object", "Read a compact Scene/Object view for the temporary GameObject.", "scene_object_view", new JObject
                {
                    ["Action"] = "View",
                    ["Target"] = targetPath,
                    ["SearchMethod"] = "ByPath",
                    ["Detail"] = "Detailed",
                    ["MaxDepth"] = 1
                }, optional: true));
                steps.Add(Step("delete_probe_game_object", "Delete the temporary GameObject so the smoke leaves no scene object behind.", "game_object", new JObject
                {
                    ["Action"] = "Delete",
                    ["Target"] = targetPath,
                    ["SearchMethod"] = "ByPath"
                }));
            }
            else
            {
                build.Warnings.Add("Post-create core smoke inspect/view/delete steps are omitted from dry-run/build batches because the probe GameObject does not exist until execution.");
            }

            return steps.ToArray();
        }

        static JObject[] BuildRunUiSmokeTest(WorkflowRecipesParams parameters, RecipeBuild build)
        {
            var canvasName = Clean(parameters.Name, $"UniBridge_UISmoke_{DateTime.Now:HHmmssfff}");
            var panelName = canvasName + "_Panel";
            var steps = new List<JObject>
            {
                Step("create_smoke_canvas", "Create a temporary Canvas for UI smoke validation.", "ui", new JObject
                {
                    ["Action"] = "CreateCanvas",
                    ["Name"] = canvasName,
                    ["RenderMode"] = "ScreenSpaceOverlay",
                    ["SortingOrder"] = 950
                })
            };

            if (build.IncludePostCreateSteps)
            {
                steps.Add(Step("create_smoke_panel", "Create a template panel under the temporary Canvas.", "ui", new JObject
                {
                    ["Action"] = "CreateTemplate",
                    ["TemplateType"] = "Panel",
                    ["Parent"] = canvasName,
                    ["Name"] = panelName,
                    ["Title"] = "UniBridge UI Smoke",
                    ["Subtitle"] = "Layout validation",
                    ["ItemTexts"] = new JArray("Template", "Layout", "Audit"),
                    ["ActionTexts"] = new JArray("OK"),
                    ["ValidateAfterCreate"] = true,
                    ["UseTextMeshPro"] = true
                }));
                steps.Add(Step("audit_smoke_panel", "Audit the created panel for layout issues.", "ui", new JObject
                {
                    ["Action"] = "Audit",
                    ["Target"] = panelName,
                    ["IncludeInactive"] = true,
                    ["MaxIssues"] = 80
                }));
                steps.Add(Step("delete_smoke_canvas", "Delete the temporary Canvas so the smoke leaves no UI behind.", "game_object", new JObject
                {
                    ["Action"] = "Delete",
                    ["Target"] = "/" + canvasName,
                    ["SearchMethod"] = "ByPath"
                }));
            }
            else
            {
                build.Warnings.Add("Post-create UI smoke template/audit/delete steps are omitted from dry-run/build batches because the Canvas does not exist until execution.");
            }

            return steps.ToArray();
        }

        static JObject[] BuildRunAssetSmokeTest(WorkflowRecipesParams parameters, RecipeBuild build)
        {
            var folder = NormalizeAssetPath(parameters.Folder);
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = AssetDatabase.IsValidFolder("Assets/Sprites") ? "Assets/Sprites" : "Assets";
            }

            var maxAssets = Math.Min(ClampMaxAssets(parameters.MaxAssets), 4);
            var assets = FindTextureAssets(folder, maxAssets);
            if (assets.Length == 0)
            {
                build.Errors.Add($"No Texture2D assets found under '{folder}'. Provide Folder or add a texture to run the asset smoke test.");
                return Array.Empty<JObject>();
            }

            var firstAsset = assets[0];
            var steps = new List<JObject>
            {
                Step("inspect_asset_importer", "Inspect importer settings for a representative texture asset.", "asset_importer", new JObject
                {
                    ["Action"] = "Inspect",
                    ["Path"] = firstAsset,
                    ["IncludeSerializedProperties"] = false,
                    ["MaxSerializedProperties"] = 40
                }),
                Step("capture_asset_grid", "Render a small contact sheet to verify asset preview/capture path.", "asset_capture", new JObject
                {
                    ["Action"] = "CaptureGrid",
                    ["Paths"] = new JArray(assets),
                    ["Types"] = new JArray("Sprite", "Texture2D"),
                    ["MaxResults"] = assets.Length,
                    ["IncludeLabels"] = true,
                    ["Tag"] = Clean(parameters.Name, "asset_smoke")
                }, optional: true)
            };

            return steps.ToArray();
        }

        static JObject Step(string id, string description, string tool, JObject parameters, bool optional = false)
        {
            return new JObject
            {
                ["id"] = id,
                ["tool"] = tool,
                ["description"] = description,
                ["optional"] = optional,
                ["parameters"] = RemoveNulls(parameters)
            };
        }

        static JObject RemoveNulls(JObject obj)
        {
            foreach (var property in obj.Properties().Where(property => property.Value.Type == JTokenType.Null).ToArray())
            {
                property.Remove();
            }

            return obj;
        }

        static RecipeDefinition ResolveRecipe(string recipe)
        {
            var key = NormalizeKey(recipe);
            return GetRecipeDefinitions().FirstOrDefault(definition =>
                NormalizeKey(definition.Name) == key ||
                definition.Aliases.Any(alias => NormalizeKey(alias) == key));
        }

        static object ToDefinitionObject(RecipeDefinition definition)
        {
            return new
            {
                name = definition.Name,
                aliases = definition.Aliases,
                when = definition.When,
                required = definition.Required,
                optional = definition.Optional,
                outputs = definition.Outputs,
                underlyingTools = definition.UnderlyingTools
            };
        }

        static RecipeDefinition[] GetRecipeDefinitions()
        {
            return new[]
            {
                new RecipeDefinition(
                    "CreateInventoryScreen",
                    new[] { "inventory", "inventory_screen", "create_inventory" },
                    "Create a complete list-style inventory UI template and audit it.",
                    Array.Empty<string>(),
                    new[] { "Name", "Parent", "Options.items", "Options.actions", "Options.title", "Options.subtitle" },
                    new[] { "Canvas/UI template", "UI audit report" },
                    new[] { "UniBridge_ManageUI", "UniBridge_BatchActions" }),
                new RecipeDefinition(
                    "ImportSpriteFolderAs2D",
                    new[] { "import_sprites", "sprite_folder", "sprites_2d" },
                    "Apply a TextureSprite2D importer preset to Texture2D assets in a folder and capture a contact sheet.",
                    new[] { "Folder" },
                    new[] { "MaxAssets", "Options.pixelsPerUnit", "Options.maxTextureSize", "Options.compressionQuality" },
                    new[] { "Updated importer settings", "Optional sprite contact sheet" },
                    new[] { "UniBridge_ManageAssetImporter", "UniBridge_CaptureAsset", "UniBridge_BatchActions" }),
                new RecipeDefinition(
                    "CreateSpriteMaterialAndPreview",
                    new[] { "sprite_material", "material_preview", "create_sprite_mat" },
                    "Create/update a sprite-friendly material from a sprite/texture and capture a preview.",
                    new[] { "SpritePath or AssetPath" },
                    new[] { "MaterialPath", "Name", "Options.preset", "Options.color" },
                    new[] { "Material asset", "Material preview PNG" },
                    new[] { "UniBridge_ManageMaterial", "UniBridge_CaptureAsset", "UniBridge_BatchActions" }),
                new RecipeDefinition(
                    "CreateHUDFromAssets",
                    new[] { "hud", "create_hud", "hud_from_assets" },
                    "Create a HUD template using supplied labels/assets and audit it.",
                    Array.Empty<string>(),
                    new[] { "Name", "Parent", "AssetPaths", "Options.items", "Options.actions", "Options.title" },
                    new[] { "Canvas/UI HUD", "UI audit report" },
                    new[] { "UniBridge_ManageUI", "UniBridge_BatchActions" }),
                new RecipeDefinition(
                    "SetupClickableUIButton",
                    new[] { "button_click", "clickable_button", "bind_button" },
                    "Inspect a Button, bind a persistent onClick listener, and verify it.",
                    new[] { "Target", "EventMethod" },
                    new[] { "EventTarget", "EventComponent", "EventArgument", "EventArgumentType", "Options.clearExisting" },
                    new[] { "Persistent Button.onClick listener", "UnityEvent inspect result" },
                    new[] { "UniBridge_ManageUI", "UniBridge_ManageUnityEvent", "UniBridge_BatchActions" }),
                new RecipeDefinition(
                    "CreateScriptableConfigAndBindToScene",
                    new[] { "scriptable_config", "config_asset", "bind_config" },
                    "Create/update a ScriptableObject config asset and optionally assign it to a scene component property.",
                    new[] { "ScriptableObjectType" },
                    new[] { "ScriptableObjectPath", "AssetPath", "Target", "Options.properties", "Options.componentName", "Options.propertyName" },
                    new[] { "ScriptableObject asset", "Optional scene component reference assignment" },
                    new[] { "UniBridge_ManageScriptableObject", "UniBridge_ManageGameObject", "UniBridge_BatchActions" }),
                new RecipeDefinition(
                    "RunCoreSmokeTest",
                    new[] { "core_smoke", "scene_smoke", "self_test_core" },
                    "Run a temporary scene-object smoke test and clean it up after execution.",
                    Array.Empty<string>(),
                    new[] { "Name" },
                    new[] { "Tool guide overview", "Console diagnostic summary", "Temporary GameObject create/inspect/delete" },
                    new[] { "UniBridge_ToolGuide", "UniBridge_ReadConsole", "UniBridge_ManageGameObject", "UniBridge_SceneObjectView", "UniBridge_BatchActions" }),
                new RecipeDefinition(
                    "RunUISmokeTest",
                    new[] { "ui_smoke", "self_test_ui", "ui_validation_smoke" },
                    "Create a temporary UI template, audit it, and clean it up after execution.",
                    Array.Empty<string>(),
                    new[] { "Name" },
                    new[] { "Temporary Canvas/template", "UI audit report", "Cleanup delete" },
                    new[] { "UniBridge_ManageUI", "UniBridge_ManageGameObject", "UniBridge_BatchActions" }),
                new RecipeDefinition(
                    "RunAssetSmokeTest",
                    new[] { "asset_smoke", "self_test_assets", "asset_preview_smoke" },
                    "Inspect a representative texture importer and render a small asset contact sheet.",
                    Array.Empty<string>(),
                    new[] { "Folder", "MaxAssets", "Name" },
                    new[] { "Importer inspect result", "Optional asset contact sheet" },
                    new[] { "UniBridge_ManageAssetImporter", "UniBridge_CaptureAsset", "UniBridge_BatchActions" })
            };
        }

        static string NormalizeKey(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }

        static string Clean(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        static JValue NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? JValue.CreateNull() : new JValue(value.Trim());
        }

        static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Trim().Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Assets/" + normalized.TrimStart('/');
            }

            return normalized;
        }

        static string[] FindTextureAssets(string folder, int maxAssets)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                return Array.Empty<string>();
            }

            return AssetDatabase.FindAssets("t:Texture2D", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path => IsTextureLikePath(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(maxAssets)
                .ToArray();
        }

        static bool IsTextureLikePath(string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".psd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase);
        }

        static int ClampMaxAssets(int? value)
        {
            return Math.Max(1, Math.Min(HardMaxAssets, value ?? DefaultMaxAssets));
        }

        static string OptionString(WorkflowRecipesParams parameters, string key, string fallback)
        {
            var token = parameters.Options?[key];
            return token == null || token.Type == JTokenType.Null || string.IsNullOrWhiteSpace(token.ToString())
                ? fallback
                : token.ToString().Trim();
        }

        static bool OptionBool(WorkflowRecipesParams parameters, bool fallback, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = parameters.Options?[key];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token.ToObject<bool>();
                }
            }

            return fallback;
        }

        static float OptionFloat(WorkflowRecipesParams parameters, float fallback, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = parameters.Options?[key];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token.ToObject<float>();
                }
            }

            return fallback;
        }

        static int? OptionInt(WorkflowRecipesParams parameters, int? fallback, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = parameters.Options?[key];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token.ToObject<int>();
                }
            }

            return fallback;
        }

        static string[] ReadStringArray(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj?[key] is JArray array)
                {
                    return array
                        .Select(token => token?.ToString())
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim())
                        .ToArray();
                }
            }

            return null;
        }

        static float[] ReadFloatArray(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (obj?[key] is JArray array)
                {
                    return array.Select(token => token.ToObject<float>()).ToArray();
                }
            }

            return null;
        }

        sealed record RecipeDefinition(
            string Name,
            string[] Aliases,
            string When,
            string[] Required,
            string[] Optional,
            string[] Outputs,
            string[] UnderlyingTools);

        sealed class RecipeBuild
        {
            public JObject Batch;
            public bool IncludePostCreateSteps;
            public List<string> Errors { get; } = new();
            public List<string> Warnings { get; } = new();
        }
    }
}
