using System;
using Newtonsoft.Json;

namespace Cidonix.UniBridge.MCP.Editor.Helpers
{
    internal sealed class ProjectGuidelinesSource
    {
        public string Kind;
        public string SourceId;
        public string AssetGuid;
        public string DisplayName;
        public string RelativePath;
        public string AbsolutePath;
    }

    internal sealed class ProjectGuidelinesConfiguration
    {
        public string ProjectRoot;
        public string ConfigurationPath;
        public bool Configured;
        public ProjectGuidelinesSource[] Sources = Array.Empty<ProjectGuidelinesSource>();
        public string[] Issues = Array.Empty<string>();
    }

    internal sealed class ProjectGuidelinesData
    {
        [JsonProperty("schema")] public string Schema = "unibridge.project-guidelines.v1";
        [JsonProperty("action")] public string Action;
        [JsonProperty("status")] public string Status;
        [JsonProperty("configured")] public bool Configured;
        [JsonProperty("complete")] public bool Complete;
        [JsonProperty("truncated")] public bool Truncated;
        [JsonProperty("configurationPath")] public string ConfigurationPath;
        [JsonProperty("contentRole")] public string ContentRole = "authored_project_guidance";
        [JsonProperty("interpretation")] public string Interpretation = "Source text is returned as project-authored data. The bridge does not execute it, change its own permissions, or treat it as trusted bridge instructions.";
        [JsonProperty("maxCharsPerSource")] public int MaxCharsPerSource;
        [JsonProperty("maxTotalChars")] public int MaxTotalChars;
        [JsonProperty("maxFileBytes")] public int MaxFileBytes;
        [JsonProperty("timeoutMs")] public int TimeoutMs;
        [JsonProperty("returnedChars")] public int ReturnedChars;
        [JsonProperty("bytesRead")] public long BytesRead;
        [JsonProperty("elapsedMs")] public long ElapsedMs;
        [JsonProperty("sources")] public ProjectGuidelinesSourceResult[] Sources = Array.Empty<ProjectGuidelinesSourceResult>();
        [JsonProperty("issues")] public string[] Issues = Array.Empty<string>();
    }

    internal sealed class ProjectGuidelinesSourceResult
    {
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("sourceId")] public string SourceId;
        [JsonProperty("assetGuid")] public string AssetGuid;
        [JsonProperty("displayName")] public string DisplayName;
        [JsonProperty("relativePath")] public string RelativePath;
        [JsonProperty("uri")] public string Uri;
        [JsonProperty("status")] public string Status;
        [JsonProperty("detail")] public string Detail;
        [JsonProperty("exists")] public bool Exists;
        [JsonProperty("lengthBytes")] public long? LengthBytes;
        [JsonProperty("lastModifiedUtc")] public string LastModifiedUtc;
        [JsonProperty("sha256")] public string Sha256;
        [JsonProperty("sha256Scope")] public string Sha256Scope = "none";
        [JsonProperty("bytesHashed")] public long BytesHashed;
        [JsonProperty("bytesRead")] public long BytesRead;
        [JsonProperty("encoding")] public string Encoding;
        [JsonProperty("text")] public string Text;
        [JsonProperty("returnedChars")] public int ReturnedChars;
        [JsonProperty("truncated")] public bool Truncated;
    }

    internal sealed class ProjectGuidelinesOrientation
    {
        [JsonProperty("configured")] public bool Configured;
        [JsonProperty("status")] public string Status;
        [JsonProperty("tool")] public string Tool;
        [JsonProperty("configurationPath")] public string ConfigurationPath;
        [JsonProperty("note")] public string Note;
    }
}
