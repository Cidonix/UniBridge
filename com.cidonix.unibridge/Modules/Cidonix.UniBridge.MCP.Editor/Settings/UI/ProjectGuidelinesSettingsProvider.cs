using System;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Cidonix.UniBridge.MCP.Editor.Settings.UI
{
    internal sealed class ProjectGuidelinesSettingsProvider : SettingsProvider
    {
        ProjectGuidelinesSettingsProvider() : base("Project/UniBridge/Project Guidelines", SettingsScope.Project) { }

        [SettingsProvider]
        public static SettingsProvider CreateProvider() => new ProjectGuidelinesSettingsProvider();

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            var settings = ProjectGuidelinesSettings.instance;
            rootElement.style.fontSize = 12;
            rootElement.Add(new Label("Project-authored guidelines"));
            rootElement.Add(new HelpBox("Only explicitly configured sources are exposed through MCP. Source text is returned as data; the bridge never executes it or changes its permissions from it.", HelpBoxMessageType.Info));
            var status = new HelpBox("", HelpBoxMessageType.None);
            var asset = new ObjectField("Guidelines TextAsset")
            {
                objectType = typeof(TextAsset), allowSceneObjects = false, value = settings.GuidelinesAsset
            };
            asset.tooltip = "Assign an imported main TextAsset from project Assets/. MCP reads that source file; TextAsset subassets are not supported. The persistent GUID follows moves and renames.";
            asset.RegisterValueChangedCallback(change =>
            {
                try
                {
                    Undo.RecordObject(settings, "Configure UniBridge project guidelines");
                    settings.SetAsset(change.newValue as TextAsset);
                    settings.Persist();
                    status.text = "Guidelines configuration saved.";
                }
                catch (Exception error)
                {
                    asset.SetValueWithoutNotify(settings.GuidelinesAsset);
                    status.text = "Configuration could not be saved: " + error.Message;
                }
            });
            rootElement.Add(asset);
            var agents = new Toggle("Include root AGENTS.md") { value = settings.IncludeRootAgents };
            agents.RegisterValueChangedCallback(change => SaveToggle(settings, agents, change.newValue, true, status));
            rootElement.Add(agents);
            var context = new Toggle("Include project context document") { value = settings.IncludeProjectContext };
            context.tooltip = "Reads only <project folder name>_context.md in the project root. No arbitrary file paths are accepted.";
            context.RegisterValueChangedCallback(change => SaveToggle(settings, context, change.newValue, false, status));
            rootElement.Add(context);
            rootElement.Add(new Label("MCP: UniBridge_ProjectGuidelines Status / Read. Read budgets and source truncation are explicit."));
            rootElement.Add(status);
        }

        static void SaveToggle(ProjectGuidelinesSettings settings, Toggle field, bool value, bool agents, HelpBox status)
        {
            try
            {
                Undo.RecordObject(settings, "Configure UniBridge project guidelines");
                if (agents) settings.SetRootAgents(value); else settings.SetProjectContext(value);
                settings.Persist();
                status.text = "Guidelines configuration saved.";
            }
            catch (Exception error)
            {
                field.SetValueWithoutNotify(agents ? settings.IncludeRootAgents : settings.IncludeProjectContext);
                status.text = "Configuration could not be saved: " + error.Message;
            }
        }
    }
}
