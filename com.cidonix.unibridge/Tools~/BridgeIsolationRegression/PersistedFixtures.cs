using System;
using System.IO;
using Newtonsoft.Json;
using Cidonix.UniBridge.MCP.Editor.Helpers;

// Only storage and test-entry adapters below are substitutes. Model declarations and
// the actual SaveState/SaveWriteToken/SaveSnapshot bodies are extracted unchanged.
namespace Golden
{
    static partial class SessionProduction
    {
        const int DefaultMaxFiles = 12000, DefaultMaxSemanticObjects = 30000;
        const long DefaultMaxSingleCaptureBytes = 2L * 1024 * 1024, DefaultMaxTotalCaptureBytes = 150L * 1024 * 1024;
        public static string Storage;
        static string ProjectRoot => Storage;
        static string GetSessionDir(string id) => Path.Combine(Storage, "sessions", id);
        static string GetSessionFile(string id) => Path.Combine(GetSessionDir(id), "session.json");
        static string GetWriteTokenPath(string session, string token) => Path.Combine(GetSessionDir(session), "write-tokens", token + ".json");
        static void EnsureNoReparsePoints(string path, string root)
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Owned persisted fixture path escape");
        }
        public static object ParseState(string json) => McpJson.DeserializeObject<SessionState>(json);
        public static object ParseToken(string json) => McpJson.DeserializeObject<WriteTrackingToken>(json);
        public static object ParsePreview(string json) => McpJson.DeserializeObject<RevertPreview>(json);
        public static object ParseSemantic(string json) => McpJson.DeserializeObject<SceneSemanticCollection>(json);
        public static string PersistState(object state) { SaveState((SessionState)state); return GetSessionFile(((SessionState)state).SessionId); }
        public static string PersistToken(object token) { SaveWriteToken((WriteTrackingToken)token); return GetWriteTokenPath(((WriteTrackingToken)token).SessionId, ((WriteTrackingToken)token).TokenId); }
    }
    static partial class SnapshotProduction
    {
        public static string Storage;
        static string SnapshotDirectory => Path.Combine(Storage, "snapshots");
        static string GetSnapshotPath(string id) => Path.Combine(SnapshotDirectory, id + ".json");
        public static object ParseModel(string json) => McpJson.DeserializeObject<EditorSnapshotData>(json, JsonSettings);
        public static JsonSerializerSettings Settings => JsonSettings;
        public static string Persist(object model) => SaveSnapshot((EditorSnapshotData)model);
    }
    static partial class DiscoveryProduction
    {
        public static object ParseModel(string json) => McpJson.DeserializeObject<ConnectionInfo>(json);
    }
}
