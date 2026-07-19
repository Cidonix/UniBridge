using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Cidonix.UniBridge.Legacy
{
    [InitializeOnLoad]
    internal static class UniBridgeLegacyHost
    {
        internal const string AdapterVersion = "0.2.50";
        internal const string ProtocolVersion = "2.0";

        private sealed class PendingCommand
        {
            public string Type;
            public Dictionary<string, object> Parameters;
            public string RequestId;
            public string Response;
            public readonly ManualResetEvent Completed = new ManualResetEvent(false);
        }

        private static readonly object QueueLock = new object();
        private static readonly Queue<PendingCommand> CommandQueue = new Queue<PendingCommand>();
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);

        private static Thread serverThread;
        private static NamedPipeServerStream activePipe;
        private static volatile bool running;
        private static string projectId;
        private static string projectName;
        private static string projectRoot;
        private static string projectHash;
        private static string pipeName;
        private static string discoveryPath;
        private static List<object> toolDescriptors;
        private static string toolsHash;

        static UniBridgeLegacyHost()
        {
            EditorApplication.update += OnEditorUpdate;
            Application.logMessageReceived += UniBridgeLegacyConsole.OnLogMessage;
            EditorApplication.delayCall += Start;
        }

        internal static string ProjectId { get { return projectId; } }
        internal static string ProjectName { get { return projectName; } }
        internal static string ProjectRoot { get { return projectRoot; } }
        internal static bool IsRunning { get { return running; } }
        internal static string DiscoveryPath { get { return discoveryPath; } }

        [MenuItem("Tools/UniBridge Legacy/Restart MCP Bridge")]
        private static void RestartFromMenu()
        {
            Stop();
            Start();
        }

        [MenuItem("Tools/UniBridge Legacy/Show Status")]
        private static void ShowStatus()
        {
            EditorUtility.DisplayDialog(
                "UniBridge Legacy",
                "Version: " + AdapterVersion + "\n" +
                "Project: " + projectName + "\n" +
                "Project ID: " + projectId + "\n" +
                "Bridge: " + (running ? "Running" : "Stopped") + "\n" +
                "Pipe: " + pipeName + "\n" +
                "Discovery: " + discoveryPath,
                "OK");
        }

        internal static void Start()
        {
            if (running)
                return;

            try
            {
                InitializeProjectIdentity();
                toolDescriptors = UniBridgeLegacyTools.BuildDescriptors();
                toolsHash = ComputeHash(UniBridgeLegacyJson.Serialize(toolDescriptors));
                int processId = Process.GetCurrentProcess().Id;
                pipeName = "unity-mcp-" + projectHash + "-" + processId;
                WriteDiscoveryFile(processId);

                running = true;
                serverThread = new Thread(ServerLoop);
                serverThread.IsBackground = true;
                serverThread.Name = "UniBridge Legacy MCP";
                serverThread.Start();
                UnityEngine.Debug.Log("[UniBridge Legacy] MCP bridge started for " + projectName +
                                      " (" + projectId.Substring(0, 8) + ") using protocol " + ProtocolVersion + ".");
            }
            catch (Exception exception)
            {
                running = false;
                UnityEngine.Debug.LogError("[UniBridge Legacy] Failed to start MCP bridge: " + exception);
            }
        }

        internal static void Stop()
        {
            running = false;
            try
            {
                if (activePipe != null)
                    activePipe.Close();
            }
            catch { }
            activePipe = null;

            try
            {
                if (!String.IsNullOrEmpty(discoveryPath) && File.Exists(discoveryPath))
                    File.Delete(discoveryPath);
            }
            catch { }
        }

        private static void InitializeProjectIdentity()
        {
            projectRoot = Directory.GetParent(Application.dataPath).FullName;
            projectName = new DirectoryInfo(projectRoot).Name;
            string settingsDirectory = Path.Combine(projectRoot, "ProjectSettings", "UniBridge");
            string settingsPath = Path.Combine(settingsDirectory, "project.json");

            if (File.Exists(settingsPath))
            {
                try
                {
                    Dictionary<string, object> identity = UniBridgeLegacyJson.Deserialize(File.ReadAllText(settingsPath)) as Dictionary<string, object>;
                    projectId = UniBridgeLegacyValue.GetString(identity, "project_id", null);
                    string storedName = UniBridgeLegacyValue.GetString(identity, "project_name", null);
                    if (!String.IsNullOrEmpty(storedName))
                        projectName = storedName;
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogWarning("[UniBridge Legacy] Could not read project identity: " + exception.Message);
                }
            }

            Guid parsedId;
            if (String.IsNullOrEmpty(projectId) || !Guid.TryParse(projectId, out parsedId))
            {
                projectId = Guid.NewGuid().ToString("N");
                if (!Directory.Exists(settingsDirectory))
                    Directory.CreateDirectory(settingsDirectory);

                Dictionary<string, object> identity = new Dictionary<string, object>();
                identity["schema_version"] = 1;
                identity["project_id"] = projectId;
                identity["project_name"] = projectName;
                identity["created_date"] = DateTime.UtcNow.ToString("O");
                identity["updated_date"] = DateTime.UtcNow.ToString("O");
                File.WriteAllText(settingsPath, UniBridgeLegacyJson.Serialize(identity), Utf8WithoutBom);
            }
            else
            {
                projectId = parsedId.ToString("N");
            }

            projectHash = ComputeHash(Application.dataPath).Substring(0, 8);
        }

        private static void WriteDiscoveryFile(int processId)
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string discoveryDirectory = Path.Combine(userProfile, ".unibridge", "mcp", "connections");
            if (!Directory.Exists(discoveryDirectory))
                Directory.CreateDirectory(discoveryDirectory);

            CleanStaleDiscoveryFiles(discoveryDirectory);
            discoveryPath = Path.Combine(discoveryDirectory, "bridge-" + projectHash + "-" + processId + ".json");

            Dictionary<string, object> info = new Dictionary<string, object>();
            info["connection_type"] = "named_pipe";
            info["connection_path"] = "\\\\.\\pipe\\" + pipeName;
            info["created_date"] = DateTime.UtcNow.ToString("O");
            info["project_path"] = Application.dataPath;
            info["project_id"] = projectId;
            info["project_name"] = projectName;
            info["project_root"] = projectRoot;
            info["protocol_version"] = ProtocolVersion;
            info["editor_pid"] = processId;
            info["adapter"] = "unity2017-legacy";
            info["adapter_version"] = AdapterVersion;
            File.WriteAllText(discoveryPath, UniBridgeLegacyJson.Serialize(info), Utf8WithoutBom);
        }

        private static void CleanStaleDiscoveryFiles(string directory)
        {
            string[] files = Directory.GetFiles(directory, "bridge-" + projectHash + "-*.json");
            for (int index = 0; index < files.Length; index++)
            {
                try
                {
                    Dictionary<string, object> info = UniBridgeLegacyJson.Deserialize(File.ReadAllText(files[index])) as Dictionary<string, object>;
                    int processId = UniBridgeLegacyValue.GetInt(info, "editor_pid", 0);
                    if (processId <= 0 || !IsProcessAlive(processId))
                        File.Delete(files[index]);
                }
                catch { }
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void ServerLoop()
        {
            while (running)
            {
                try
                {
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        4,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None))
                    {
                        activePipe = pipe;
                        pipe.WaitForConnection();
                        if (!running)
                            return;

                        using (StreamReader reader = new StreamReader(pipe, Utf8WithoutBom))
                        using (StreamWriter writer = new StreamWriter(pipe, Utf8WithoutBom))
                        {
                            writer.AutoFlush = true;
                            writer.WriteLine(CreateHandshake());

                            while (running && pipe.IsConnected)
                            {
                                string line = reader.ReadLine();
                                if (line == null)
                                    break;
                                string response = HandleTransportCommand(line);
                                writer.WriteLine(response);
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    if (!running)
                        return;
                }
                catch (ObjectDisposedException)
                {
                    if (!running)
                        return;
                }
                catch (Exception exception)
                {
                    if (running)
                    {
                        UnityEngine.Debug.LogWarning("[UniBridge Legacy] Pipe listener recovered from: " + exception.Message);
                        Thread.Sleep(250);
                    }
                }
                finally
                {
                    activePipe = null;
                }
            }
        }

        private static string CreateHandshake()
        {
            Dictionary<string, object> handshake = new Dictionary<string, object>();
            handshake["type"] = "handshake";
            handshake["protocol"] = "unity-mcp";
            handshake["version"] = ProtocolVersion;
            handshake["toolsHash"] = toolsHash;
            handshake["tools"] = toolDescriptors;
            return UniBridgeLegacyJson.Serialize(handshake);
        }

        private static string HandleTransportCommand(string line)
        {
            string requestId = null;
            try
            {
                Dictionary<string, object> command = UniBridgeLegacyJson.Deserialize(line) as Dictionary<string, object>;
                if (command == null)
                    return ErrorResponse("Invalid JSON command object.", null);

                string type = UniBridgeLegacyValue.GetString(command, "type", null);
                requestId = UniBridgeLegacyValue.GetString(command, "requestId", null);
                Dictionary<string, object> parameters = UniBridgeLegacyValue.GetObject(command, "params");
                if (String.IsNullOrEmpty(type))
                    return ErrorResponse("Command type cannot be empty.", requestId);

                if (String.Equals(type, "ping", StringComparison.OrdinalIgnoreCase))
                    return SuccessResponse(UniBridgeLegacyValue.Object("message", "pong"), requestId);

                if (String.Equals(type, "set_client_info", StringComparison.OrdinalIgnoreCase))
                    return SuccessResponse(UniBridgeLegacyValue.Object("message", "Client info received"), requestId);

                if (String.Equals(type, "get_available_tools", StringComparison.OrdinalIgnoreCase))
                {
                    string requestedHash = UniBridgeLegacyValue.GetString(parameters, "hash", null);
                    Dictionary<string, object> result = new Dictionary<string, object>();
                    result["hash"] = toolsHash;
                    if (String.Equals(requestedHash, toolsHash, StringComparison.Ordinal))
                        result["unchanged"] = true;
                    else
                        result["tools"] = toolDescriptors;
                    return SuccessResponse(result, requestId);
                }

                PendingCommand pending = new PendingCommand();
                pending.Type = type;
                pending.Parameters = parameters ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                pending.RequestId = requestId;
                lock (QueueLock)
                    CommandQueue.Enqueue(pending);

                if (!pending.Completed.WaitOne(130000))
                    return ErrorResponse("Timed out waiting for the Unity Editor main thread.", requestId);
                return pending.Response;
            }
            catch (Exception exception)
            {
                return ErrorResponse(exception.Message, requestId);
            }
        }

        private static void OnEditorUpdate()
        {
            int processed = 0;
            while (processed < 16)
            {
                PendingCommand pending = null;
                lock (QueueLock)
                {
                    if (CommandQueue.Count > 0)
                        pending = CommandQueue.Dequeue();
                }
                if (pending == null)
                    break;

                try
                {
                    object result = UniBridgeLegacyTools.Execute(pending.Type, pending.Parameters);
                    pending.Response = SuccessResponse(result, pending.RequestId);
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogError("[UniBridge Legacy] " + pending.Type + " failed: " + exception);
                    pending.Response = ErrorResponse(exception.Message, pending.RequestId);
                }
                finally
                {
                    pending.Completed.Set();
                }
                processed++;
            }
        }

        internal static Dictionary<string, object> BuildProjectContext()
        {
            Dictionary<string, object> context = new Dictionary<string, object>();
            context["id"] = projectId;
            context["name"] = projectName;
            context["root"] = projectRoot;
            context["assetsPath"] = Application.dataPath;
            context["unityVersion"] = Application.unityVersion;
            context["adapter"] = "Unity2017Legacy";
            context["adapterVersion"] = AdapterVersion;
            return context;
        }

        private static string SuccessResponse(object result, string requestId)
        {
            Dictionary<string, object> response = new Dictionary<string, object>();
            response["status"] = "success";
            response["result"] = result;
            if (!String.IsNullOrEmpty(requestId))
                response["requestId"] = requestId;
            return UniBridgeLegacyJson.Serialize(response);
        }

        private static string ErrorResponse(string error, string requestId)
        {
            Dictionary<string, object> response = new Dictionary<string, object>();
            response["status"] = "error";
            response["error"] = error;
            response["projectContext"] = BuildProjectContext();
            if (!String.IsNullOrEmpty(requestId))
                response["requestId"] = requestId;
            return UniBridgeLegacyJson.Serialize(response);
        }

        internal static string ComputeHash(string value)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? String.Empty));
                StringBuilder builder = new StringBuilder(bytes.Length * 2);
                for (int index = 0; index < bytes.Length; index++)
                    builder.Append(bytes[index].ToString("x2"));
                return builder.ToString();
            }
        }
    }

    internal static class UniBridgeLegacyValue
    {
        public static Dictionary<string, object> Object(string key, object value)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result[key] = value;
            return result;
        }

        public static object Get(Dictionary<string, object> source, string key)
        {
            if (source == null)
                return null;
            object value;
            return source.TryGetValue(key, out value) ? value : null;
        }

        public static string GetString(Dictionary<string, object> source, string key, string fallback)
        {
            object value = Get(source, key);
            return value == null ? fallback : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool GetBool(Dictionary<string, object> source, string key, bool fallback)
        {
            object value = Get(source, key);
            if (value == null) return fallback;
            if (value is bool) return (bool)value;
            bool parsed;
            return Boolean.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }

        public static int GetInt(Dictionary<string, object> source, string key, int fallback)
        {
            object value = Get(source, key);
            if (value == null) return fallback;
            try { return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static float GetFloat(Dictionary<string, object> source, string key, float fallback)
        {
            object value = Get(source, key);
            if (value == null) return fallback;
            try { return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> source, string key)
        {
            return Get(source, key) as Dictionary<string, object>;
        }

        public static List<object> GetArray(Dictionary<string, object> source, string key)
        {
            return Get(source, key) as List<object>;
        }
    }
}
