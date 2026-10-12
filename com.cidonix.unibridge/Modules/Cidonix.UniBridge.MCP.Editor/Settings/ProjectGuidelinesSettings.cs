using System;
using System.Collections.Generic;
using System.IO;
using Cidonix.UniBridge.MCP.Editor.Helpers;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.MCP.Editor.Settings
{
    [FilePath(ProjectGuidelinesSettings.ConfigurationPath, FilePathAttribute.Location.ProjectFolder)]
    internal sealed class ProjectGuidelinesSettings : ScriptableSingleton<ProjectGuidelinesSettings>
    {
        internal const string ConfigurationPath = "ProjectSettings/UniBridgeProjectGuidelines.asset";
        [SerializeField] TextAsset m_GuidelinesAsset;
        [SerializeField, HideInInspector] string m_GuidelinesAssetGuid;
        [SerializeField] bool m_IncludeRootAgents;
        [SerializeField] bool m_IncludeProjectContext;

        internal TextAsset GuidelinesAsset => m_GuidelinesAsset;
        internal bool IncludeRootAgents => m_IncludeRootAgents;
        internal bool IncludeProjectContext => m_IncludeProjectContext;

        internal void SetAsset(TextAsset value)
        {
            if (value != null)
            {
                if (!AssetDatabase.IsMainAsset(value))
                    throw new ArgumentException("Choose an imported main TextAsset; subassets are not file-backed guidelines.");
                var path = AssetDatabase.GetAssetPath(value);
                if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Choose a project TextAsset from Assets/.");
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long _) || string.IsNullOrEmpty(guid))
                    throw new ArgumentException("The TextAsset must have a persistent asset GUID.");
                m_GuidelinesAssetGuid = guid;
            }
            else m_GuidelinesAssetGuid = null;
            m_GuidelinesAsset = value;
        }

        internal void SetRootAgents(bool value) => m_IncludeRootAgents = value;
        internal void SetProjectContext(bool value) => m_IncludeProjectContext = value;

        internal void Persist()
        {
            VersionControlUtility.EnsureAssetEditable(ConfigurationPath, checkout: true, throwOnBlocked: true);
            Save(true);
        }

        internal ProjectGuidelinesConfiguration Capture()
        {
            var root = ProjectPathResolver.ProjectRoot;
            var sources = new List<ProjectGuidelinesSource>();
            var issues = new List<string>();
            if (m_GuidelinesAsset != null || !string.IsNullOrEmpty(m_GuidelinesAssetGuid))
            {
                // A reused cached path must not rebind a still-alive object to a different asset GUID.
                var guid = m_GuidelinesAssetGuid;
                if (m_GuidelinesAsset != null)
                    guid = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m_GuidelinesAsset, out string objectGuid, out long _) ? objectGuid : null;
                var path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
                var accepted = !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                    (m_GuidelinesAsset == null || AssetDatabase.IsMainAsset(m_GuidelinesAsset));
                string absolute = null;
                if (accepted)
                {
                    // GUIDToAssetPath can retain a deleted asset's old path until the Editor cache expires.
                    // A resource URI requires a current typed asset, matching identity and real source file.
                    var current = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                    absolute = Path.GetFullPath(Path.Combine(root, path));
                    accepted = current != null && AssetDatabase.IsMainAsset(current) &&
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(current, out string currentGuid, out long _) &&
                        string.Equals(currentGuid, guid, StringComparison.OrdinalIgnoreCase) && File.Exists(absolute);
                }
                if (!accepted) issues.Add("The configured TextAsset GUID is missing or no longer resolves inside project Assets/.");
                sources.Add(new ProjectGuidelinesSource
                {
                    Kind = "text_asset", SourceId = "asset:" + guid, AssetGuid = guid,
                    DisplayName = m_GuidelinesAsset != null ? m_GuidelinesAsset.name : "Configured TextAsset",
                    RelativePath = accepted ? path : null, AbsolutePath = accepted ? absolute : null
                });
            }
            if (m_IncludeRootAgents) sources.Add(RootSource(root, "root_agents", "AGENTS.md"));
            if (m_IncludeProjectContext) sources.Add(RootSource(root, "project_context", new DirectoryInfo(root).Name + "_context.md"));
            return new ProjectGuidelinesConfiguration
            {
                ProjectRoot = root, ConfigurationPath = ConfigurationPath,
                Configured = sources.Count != 0, Sources = sources.ToArray(), Issues = issues.ToArray()
            };
        }

        internal object Orientation()
        {
            var configured = m_GuidelinesAsset != null || !string.IsNullOrEmpty(m_GuidelinesAssetGuid) || m_IncludeRootAgents || m_IncludeProjectContext;
            return new ProjectGuidelinesOrientation
            {
                Configured = configured, Status = configured ? "configured" : "unavailable",
                Tool = "UniBridge_ProjectGuidelines", ConfigurationPath = ConfigurationPath,
                Note = configured ? "Use Status then Read for explicitly configured project-authored guidance. Read declares source identity and truncation."
                    : "No authored guidelines are configured. Configure a typed TextAsset or opt in to the fixed root documents in Project Settings > UniBridge > Project Guidelines."
            };
        }

        static ProjectGuidelinesSource RootSource(string root, string kind, string filename) => new ProjectGuidelinesSource
        {
            Kind = kind, SourceId = kind + ":" + filename, DisplayName = filename,
            RelativePath = filename, AbsolutePath = Path.Combine(root, filename)
        };
    }

}
