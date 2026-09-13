using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
        internal const string AdapterVersion = "0.2.55";
        internal const string ProtocolVersion = "2.0";
        internal const string AdapterFamily = "UnityLegacy";
        internal const string MinimumUnityVersion = "5.3.2";
        private const int MaximumPipeInstances = 32;

        private sealed class PendingCommand
        {
            public string Type;
            public Dictionary<string, object> Parameters;
            public string RequestId;
            public string Response;
            public bool IsCompleted;
            public bool IsCancelled;
            public readonly object CompletionLock = new object();
        }

        // StreamReader and StreamWriter both close their base stream when disposed in
        // the .NET 2.0 profile used by Unity 5.6. Sharing a NamedPipeServerStream
        // directly between both wrappers therefore closes the same old Mono native
        // handle more than once. Keep ownership in HandleClient and close it exactly
        // once instead.
        private sealed class NonClosingStream : Stream
        {
            private readonly Stream inner;

            public NonClosingStream(Stream stream)
            {
                if (stream == null)
                    throw new ArgumentNullException("stream");
                inner = stream;
            }

            public override bool CanRead { get { return inner.CanRead; } }
            public override bool CanSeek { get { return inner.CanSeek; } }
            public override bool CanWrite { get { return inner.CanWrite; } }
            public override long Length { get { return inner.Length; } }
            public override long Position
            {
                get { return inner.Position; }
                set { inner.Position = value; }
            }

            public override void Flush() { inner.Flush(); }
            public override int Read(byte[] buffer, int offset, int count) { return inner.Read(buffer, offset, count); }
            public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }
            public override void SetLength(long value) { inner.SetLength(value); }
            public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); }

            protected override void Dispose(bool disposing)
            {
                // Deliberately do not dispose the shared named pipe.
                base.Dispose(disposing);
            }
        }

        private interface ILegacyPipeConnection
        {
            void WaitForConnection();
            string ReadLine();
            void WriteLine(string line);
            void CloseOnce();
        }

        private sealed class ManagedPipeConnection : ILegacyPipeConnection
        {
            private readonly NamedPipeServerStream pipe;
            private StreamReader reader;
            private StreamWriter writer;
            private int isClosed;

            public ManagedPipeConnection(string name)
            {
                pipe = new NamedPipeServerStream(
                    name,
                    PipeDirection.InOut,
                    MaximumPipeInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
            }

            public void WaitForConnection()
            {
                pipe.WaitForConnection();
                reader = new StreamReader(new NonClosingStream(pipe), Utf8WithoutBom);
                writer = new StreamWriter(new NonClosingStream(pipe), Utf8WithoutBom);
                writer.AutoFlush = true;
            }

            public string ReadLine()
            {
                return reader.ReadLine();
            }

            public void WriteLine(string line)
            {
                writer.WriteLine(line);
            }

            public void CloseOnce()
            {
                if (Interlocked.Exchange(ref isClosed, 1) != 0)
                    return;
                if (writer != null)
                {
                    try { writer.Dispose(); }
                    catch { }
                }
                if (reader != null)
                {
                    try { reader.Dispose(); }
                    catch { }
                }
                try { pipe.Close(); }
                catch { }
            }
        }

        // Unity 5.6 ships an old Mono System.IO.Pipes implementation. On current
        // Windows builds it can echo duplex writes and, more importantly, corrupt
        // the native heap while a disconnected NamedPipeServerStream is released.
        // Use the Win32 pipe API directly so neither Mono stream disposal nor its
        // finalizer owns the Windows handle. Newer/non-Windows legacy Editors keep
        // the managed fallback above.
        private sealed class NativeWindowsPipeConnection : ILegacyPipeConnection
        {
            private const int ErrorInvalidHandle = 6;
            private const int ErrorBrokenPipe = 109;
            private const int ErrorNoData = 232;
            private const int ErrorPipeNotConnected = 233;
            private const int ErrorOperationAborted = 995;
            private const int ErrorPipeConnected = 535;
            private const int ErrorPipeListening = 536;
            private const int MaximumLineBytes = 8 * 1024 * 1024;
            private const int ListenerPollMilliseconds = 25;
            private const int ClientPollMilliseconds = 10;
            private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

            private readonly List<byte> bufferedBytes = new List<byte>();
            private readonly object writeLock = new object();
            private readonly IntPtr handle;
            private int isClosed;

            public NativeWindowsPipeConnection(string name)
            {
                handle = NativeMethods.CreateNamedPipe(
                    "\\\\.\\pipe\\" + name,
                    NativeMethods.PipeAccessDuplex,
                    NativeMethods.PipeTypeByte | NativeMethods.PipeReadModeByte | NativeMethods.PipeNoWait,
                    MaximumPipeInstances,
                    64 * 1024,
                    64 * 1024,
                    0,
                    IntPtr.Zero);
                if (handle == InvalidHandleValue)
                    throw PipeIOException("CreateNamedPipe", Marshal.GetLastWin32Error());
            }

            public void WaitForConnection()
            {
                while (Interlocked.CompareExchange(ref isClosed, 0, 0) == 0)
                {
                    if (NativeMethods.ConnectNamedPipe(handle, IntPtr.Zero))
                    {
                        SetBlockingByteMode();
                        return;
                    }

                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorPipeConnected)
                    {
                        SetBlockingByteMode();
                        return;
                    }
                    if (error == ErrorPipeListening)
                    {
                        Thread.Sleep(ListenerPollMilliseconds);
                        continue;
                    }
                    if (IsClosedError(error))
                        throw new ObjectDisposedException("UniBridge native named pipe");
                    throw PipeIOException("ConnectNamedPipe", error);
                }
                throw new ObjectDisposedException("UniBridge native named pipe");
            }

            public string ReadLine()
            {
                string line;
                byte[] chunk = new byte[4096];
                while (true)
                {
                    if (TryTakeBufferedLine(out line))
                        return line;

                    int available;
                    bool peeked = NativeMethods.PeekNamedPipe(
                        handle,
                        IntPtr.Zero,
                        0,
                        IntPtr.Zero,
                        out available,
                        IntPtr.Zero);
                    if (!peeked)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (IsClosedError(error))
                            return null;
                        throw PipeIOException("PeekNamedPipe", error);
                    }
                    if (available <= 0)
                    {
                        Thread.Sleep(ClientPollMilliseconds);
                        continue;
                    }

                    int bytesRead;
                    bool succeeded = NativeMethods.ReadFile(
                        handle,
                        chunk,
                        Math.Min(chunk.Length, available),
                        out bytesRead,
                        IntPtr.Zero);
                    if (!succeeded)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (IsClosedError(error))
                            return null;
                        throw PipeIOException("ReadFile", error);
                    }
                    if (bytesRead <= 0)
                        return null;

                    for (int index = 0; index < bytesRead; index++)
                        bufferedBytes.Add(chunk[index]);
                    if (bufferedBytes.Count > MaximumLineBytes)
                        throw new IOException("UniBridge named-pipe line exceeded the 8 MiB safety limit.");
                }
            }

            public void WriteLine(string line)
            {
                byte[] bytes = Utf8WithoutBom.GetBytes((line ?? String.Empty) + "\n");
                lock (writeLock)
                {
                    int bytesWritten;
                    bool succeeded = NativeMethods.WriteFile(
                        handle,
                        bytes,
                        bytes.Length,
                        out bytesWritten,
                        IntPtr.Zero);
                    if (!succeeded)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (IsClosedError(error))
                            throw new ObjectDisposedException("UniBridge native named pipe");
                        throw PipeIOException("WriteFile", error);
                    }
                    if (bytesWritten != bytes.Length)
                        throw new IOException("WriteFile completed only " + bytesWritten + " of " + bytes.Length + " bytes.");
                }
            }

            public void CloseOnce()
            {
                if (Interlocked.Exchange(ref isClosed, 1) != 0)
                    return;
                try { NativeMethods.CancelIoEx(handle, IntPtr.Zero); }
                catch { }
                try { NativeMethods.DisconnectNamedPipe(handle); }
                catch { }
                try { NativeMethods.CloseHandle(handle); }
                catch { }
            }

            private bool TryTakeBufferedLine(out string line)
            {
                for (int index = 0; index < bufferedBytes.Count; index++)
                {
                    if (bufferedBytes[index] != (byte)'\n')
                        continue;

                    int contentLength = index;
                    if (contentLength > 0 && bufferedBytes[contentLength - 1] == (byte)'\r')
                        contentLength--;
                    byte[] bytes = bufferedBytes.ToArray();
                    line = Utf8WithoutBom.GetString(bytes, 0, contentLength);
                    bufferedBytes.RemoveRange(0, index + 1);
                    return true;
                }
                line = null;
                return false;
            }

            private void SetBlockingByteMode()
            {
                uint mode = NativeMethods.PipeReadModeByte | NativeMethods.PipeWait;
                if (!NativeMethods.SetNamedPipeHandleState(handle, ref mode, IntPtr.Zero, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (IsClosedError(error))
                        throw new ObjectDisposedException("UniBridge native named pipe");
                    throw PipeIOException("SetNamedPipeHandleState", error);
                }
            }

            private static bool IsClosedError(int error)
            {
                return error == ErrorInvalidHandle ||
                       error == ErrorBrokenPipe ||
                       error == ErrorNoData ||
                       error == ErrorPipeNotConnected ||
                       error == ErrorOperationAborted;
            }

            private static IOException PipeIOException(string operation, int error)
            {
                return new IOException(operation + " failed with Win32 error " + error + ".");
            }

            private static class NativeMethods
            {
                internal const uint PipeAccessDuplex = 0x00000003;
                internal const uint PipeTypeByte = 0x00000000;
                internal const uint PipeReadModeByte = 0x00000000;
                internal const uint PipeWait = 0x00000000;
                internal const uint PipeNoWait = 0x00000001;

                [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                internal static extern IntPtr CreateNamedPipe(
                    string lpName,
                    uint dwOpenMode,
                    uint dwPipeMode,
                    int nMaxInstances,
                    int nOutBufferSize,
                    int nInBufferSize,
                    int nDefaultTimeOut,
                    IntPtr lpSecurityAttributes);

                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                internal static extern bool ConnectNamedPipe(IntPtr hNamedPipe, IntPtr lpOverlapped);

                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                internal static extern bool DisconnectNamedPipe(IntPtr hNamedPipe);

                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                internal static extern bool ReadFile(
                    IntPtr hFile,
                    byte[] lpBuffer,
                    int nNumberOfBytesToRead,
                    out int lpNumberOfBytesRead,
                    IntPtr lpOverlapped);

                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                internal static extern bool PeekNamedPipe(
                    IntPtr hNamedPipe,
                    IntPtr lpBuffer,
                    int nBufferSize,
                    IntPtr lpBytesRead,
                    out int lpTotalBytesAvail,
                    IntPtr lpBytesLeftThisMessage);

                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                internal static extern bool SetNamedPipeHandleState(
                    IntPtr hNamedPipe,
                    ref uint lpMode,
                    IntPtr lpMaxCollectionCount,
                    IntPtr lpCollectDataTimeout);

                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                internal static extern bool WriteFile(
                    IntPtr hFile,
                    byte[] lpBuffer,
                    int nNumberOfBytesToWrite,
                    out int lpNumberOfBytesWritten,
                    IntPtr lpOverlapped);

                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                internal static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                internal static extern bool CloseHandle(IntPtr hObject);
            }
        }

        private sealed class PipeLease
        {
            private int isClosed;

            public PipeLease(ILegacyPipeConnection pipe)
            {
                if (pipe == null)
                    throw new ArgumentNullException("pipe");
                Pipe = pipe;
            }

            public readonly ILegacyPipeConnection Pipe;

            public void CloseOnce()
            {
                if (Interlocked.Exchange(ref isClosed, 1) != 0)
                    return;
                try { Pipe.CloseOnce(); }
                catch { }
            }
        }

        private static readonly object QueueLock = new object();
        private static readonly object PipeLock = new object();
        private static readonly object WarningLock = new object();
        private static readonly Queue<PendingCommand> CommandQueue = new Queue<PendingCommand>();
        private static readonly List<PipeLease> ActivePipes = new List<PipeLease>();
        private static readonly Queue<string> TransportWarnings = new Queue<string>();
        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(false);

        private static Thread serverThread;
        private static volatile bool running;
        private static string projectId;
        private static string projectName;
        private static string projectRoot;
        private static string projectAssetsPath;
        private static string editorUnityVersion;
        private static string compatibilityProfile;
        private static string projectHash;
        private static string pipeName;
        private static string discoveryPath;
        private static List<object> toolDescriptors;
        private static string toolsHash;

        static UniBridgeLegacyHost()
        {
            EditorApplication.update += OnEditorUpdate;
            Application.logMessageReceived += UniBridgeLegacyConsole.OnLogMessage;
            AppDomain.CurrentDomain.DomainUnload += OnDomainUnload;
            ScheduleStart();
        }

        [InitializeOnLoadMethod]
        private static void InitializeAfterAssemblyReload()
        {
            // Unity 5.6 can discard delayCall delegates while it rebuilds its
            // post-refresh Editor state. An explicit load method plus the first
            // ready Editor update makes bridge re-registration deterministic.
            ScheduleStart();
        }

        internal static string ProjectId { get { return projectId; } }
        internal static string ProjectName { get { return projectName; } }
        internal static string ProjectRoot { get { return projectRoot; } }
        internal static bool IsRunning { get { return running; } }
        internal static string DiscoveryPath { get { return discoveryPath; } }
        internal static string CompatibilityProfile
        {
            get
            {
                string version = editorUnityVersion;
                if (String.IsNullOrEmpty(version))
                    version = Application.unityVersion ?? String.Empty;
                if (version.StartsWith("5.3.", StringComparison.Ordinal))
                    return "Unity53Legacy";
                if (version.StartsWith("5.6.", StringComparison.Ordinal))
                    return "Unity56Legacy";
                if (version.StartsWith("2017.", StringComparison.Ordinal))
                    return "Unity2017Legacy";
                if (version.StartsWith("2018.", StringComparison.Ordinal))
                    return "Unity2018Legacy";
                return AdapterFamily;
            }
        }

        private static void ScheduleStart()
        {
            EditorApplication.update -= StartWhenEditorReady;
            EditorApplication.update += StartWhenEditorReady;
        }

        private static void StartWhenEditorReady()
        {
            if (running)
            {
                EditorApplication.update -= StartWhenEditorReady;
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            EditorApplication.update -= StartWhenEditorReady;
            Start();
        }

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
                "Profile: " + CompatibilityProfile + "\n" +
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
            CancelQueuedCommands("UniBridge Legacy is stopping.");
            PipeLease[] pipes;
            lock (PipeLock)
            {
                pipes = ActivePipes.ToArray();
                ActivePipes.Clear();
            }

            for (int index = 0; index < pipes.Length; index++)
            {
                try
                {
                    pipes[index].CloseOnce();
                }
                catch { }
            }

            Thread thread = serverThread;
            if (thread != null && thread != Thread.CurrentThread && thread.IsAlive)
            {
                try { thread.Join(500); }
                catch { }
            }
            serverThread = null;

            try
            {
                if (!String.IsNullOrEmpty(discoveryPath) && File.Exists(discoveryPath))
                    File.Delete(discoveryPath);
            }
            catch { }
        }

        private static void OnDomainUnload(object sender, EventArgs args)
        {
            Stop();
        }

        private static void InitializeProjectIdentity()
        {
            projectAssetsPath = Application.dataPath;
            editorUnityVersion = Application.unityVersion;
            compatibilityProfile = CompatibilityProfile;
            projectRoot = Directory.GetParent(projectAssetsPath).FullName;
            projectName = new DirectoryInfo(projectRoot).Name;
            string settingsDirectory = Path.Combine(Path.Combine(projectRoot, "ProjectSettings"), "UniBridge");
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
            if (String.IsNullOrEmpty(projectId) || !TryParseGuid(projectId, out parsedId))
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
            string userProfile = GetUserProfilePath();
            string discoveryDirectory = Path.Combine(
                Path.Combine(Path.Combine(userProfile, ".unibridge"), "mcp"),
                "connections");
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
            info["adapter"] = "unity-legacy";
            info["adapter_family"] = AdapterFamily;
            info["compatibility_profile"] = CompatibilityProfile;
            info["minimum_unity_version"] = MinimumUnityVersion;
            List<object> aliases = new List<object>();
            aliases.Add("unity2017-legacy");
            info["adapter_aliases"] = aliases;
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

        private static bool TryParseGuid(string value, out Guid parsed)
        {
            try
            {
                parsed = new Guid(value);
                return true;
            }
            catch
            {
                parsed = Guid.Empty;
                return false;
            }
        }

        private static string GetUserProfilePath()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!String.IsNullOrEmpty(profile))
                return profile;

            string drive = Environment.GetEnvironmentVariable("HOMEDRIVE");
            string path = Environment.GetEnvironmentVariable("HOMEPATH");
            if (!String.IsNullOrEmpty(drive) && !String.IsNullOrEmpty(path))
                return drive + path;

            string personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            if (!String.IsNullOrEmpty(personal))
            {
                DirectoryInfo parent = Directory.GetParent(personal);
                if (parent != null)
                    return parent.FullName;
            }

            throw new InvalidOperationException("Could not resolve the current user profile directory for UniBridge discovery.");
        }

        private static void ServerLoop()
        {
            while (running)
            {
                PipeLease listener = null;
                try
                {
                    listener = new PipeLease(CreatePipeConnection());
                    RegisterPipe(listener);
                    listener.Pipe.WaitForConnection();
                    if (!running)
                        return;

                    PipeLease clientPipe = listener;
                    listener = null;
                    Thread clientThread = new Thread(new ThreadStart(delegate { HandleClient(clientPipe); }));
                    clientThread.IsBackground = true;
                    clientThread.Name = "UniBridge Legacy MCP Client";
                    clientThread.Start();
                }
                catch (IOException exception)
                {
                    if (!running)
                        return;
                    EnqueueTransportWarning("Pipe listener recovered from I/O failure: " + exception.Message);
                    Thread.Sleep(1000);
                }
                catch (ObjectDisposedException exception)
                {
                    if (!running)
                        return;
                    EnqueueTransportWarning("Pipe listener recovered from disposal race: " + exception.Message);
                    Thread.Sleep(1000);
                }
                catch (Exception exception)
                {
                    if (running)
                    {
                        EnqueueTransportWarning("Pipe listener recovered from: " + exception.Message);
                        Thread.Sleep(1000);
                    }
                }
                finally
                {
                    if (listener != null)
                    {
                        UnregisterPipe(listener);
                        listener.CloseOnce();
                    }
                }
            }
        }

        private static void HandleClient(PipeLease pipeLease)
        {
            ILegacyPipeConnection pipe = pipeLease.Pipe;
            try
            {
                pipe.WriteLine(CreateHandshake());

                while (running)
                {
                    string line = pipe.ReadLine();
                    if (line == null)
                        break;
                    string response = HandleTransportCommand(line);
                    pipe.WriteLine(response);
                }
            }
            catch (IOException)
            {
                // Normal when a relay disconnects or is restarted.
            }
            catch (ObjectDisposedException)
            {
                // Normal during bridge shutdown.
            }
            catch (Exception exception)
            {
                if (running)
                    EnqueueTransportWarning("Client connection recovered from: " + exception.Message);
            }
            finally
            {
                UnregisterPipe(pipeLease);
                pipeLease.CloseOnce();
            }
        }

        private static ILegacyPipeConnection CreatePipeConnection()
        {
            PlatformID platform = Environment.OSVersion.Platform;
            if (platform == PlatformID.Win32NT ||
                platform == PlatformID.Win32Windows ||
                platform == PlatformID.Win32S ||
                platform == PlatformID.WinCE)
                return new NativeWindowsPipeConnection(pipeName);
            return new ManagedPipeConnection(pipeName);
        }

        private static void EnqueueTransportWarning(string message)
        {
            lock (WarningLock)
            {
                if (TransportWarnings.Count >= 16)
                    return;
                TransportWarnings.Enqueue(message);
            }
        }

        private static void DrainTransportWarnings()
        {
            int count = 0;
            while (count < 4)
            {
                string message = null;
                lock (WarningLock)
                {
                    if (TransportWarnings.Count > 0)
                        message = TransportWarnings.Dequeue();
                }
                if (message == null)
                    return;
                UnityEngine.Debug.LogWarning("[UniBridge Legacy] " + message);
                count++;
            }
        }

        private static void CancelQueuedCommands(string reason)
        {
            List<PendingCommand> cancelled = new List<PendingCommand>();
            lock (QueueLock)
            {
                while (CommandQueue.Count > 0)
                    cancelled.Add(CommandQueue.Dequeue());
            }
            for (int index = 0; index < cancelled.Count; index++)
                CompletePending(cancelled[index], ErrorResponse(reason, cancelled[index].RequestId));
        }

        private static void CompletePending(PendingCommand pending, string response)
        {
            lock (pending.CompletionLock)
            {
                if (pending.IsCompleted)
                    return;
                pending.Response = response;
                pending.IsCompleted = true;
                Monitor.PulseAll(pending.CompletionLock);
            }
        }

        private static void RegisterPipe(PipeLease pipe)
        {
            lock (PipeLock)
                ActivePipes.Add(pipe);
        }

        private static void UnregisterPipe(PipeLease pipe)
        {
            lock (PipeLock)
                ActivePipes.Remove(pipe);
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

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(130000);
                bool timedOut = false;
                lock (pending.CompletionLock)
                {
                    while (!pending.IsCompleted)
                    {
                        TimeSpan remaining = deadline - DateTime.UtcNow;
                        if (remaining.TotalMilliseconds <= 0)
                            break;
                        int waitMilliseconds = (int)Math.Min(remaining.TotalMilliseconds, Int32.MaxValue);
                        Monitor.Wait(pending.CompletionLock, waitMilliseconds);
                    }
                    if (!pending.IsCompleted)
                    {
                        pending.IsCancelled = true;
                        timedOut = true;
                    }
                }

                if (timedOut)
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
            DrainTransportWarnings();
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

                lock (pending.CompletionLock)
                {
                    if (pending.IsCancelled)
                    {
                        pending.Response = ErrorResponse(
                            "Command was cancelled before Unity main-thread execution.",
                            pending.RequestId);
                        pending.IsCompleted = true;
                        Monitor.PulseAll(pending.CompletionLock);
                        processed++;
                        continue;
                    }
                }

                string response;
                try
                {
                    object result = UniBridgeLegacyTools.Execute(pending.Type, pending.Parameters);
                    response = SuccessResponse(result, pending.RequestId);
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogError("[UniBridge Legacy] " + pending.Type + " failed: " + exception);
                    response = ErrorResponse(exception.Message, pending.RequestId);
                }
                CompletePending(pending, response);
                processed++;
            }
        }

        internal static Dictionary<string, object> BuildProjectContext()
        {
            Dictionary<string, object> context = new Dictionary<string, object>();
            context["id"] = projectId;
            context["name"] = projectName;
            context["root"] = projectRoot;
            context["assetsPath"] = projectAssetsPath;
            context["unityVersion"] = editorUnityVersion;
            context["adapter"] = compatibilityProfile;
            context["adapterFamily"] = AdapterFamily;
            context["adapterVersion"] = AdapterVersion;
            context["minimumUnityVersion"] = MinimumUnityVersion;
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
