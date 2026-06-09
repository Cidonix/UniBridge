using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cidonix.UniBridge.Relay;

static class Program
{
    const string ProductName = "UniBridge Relay";
    const string ServerName = "unibridge-relay";
    public const string Version = "1.1.0-build.14";
    public const string ProtocolVersion = "1.0";

    static async Task<int> Main(string[] args)
    {
        var options = RelayOptions.Parse(args);

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.ShowVersion)
        {
            PrintVersion();
            return 0;
        }

        if (options.RelayMode)
        {
            Console.Error.WriteLine($"{ProductName} {Version}: --relay is not available in the MCP-only UniBridge relay.");
            return 2;
        }

        if (!options.McpMode)
        {
            Console.Error.WriteLine($"{ProductName} {Version}: specify --mcp.");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var logger = new Logger(options.Debug);
        var server = new McpServer(options, logger);

        try
        {
            await server.TryConnectUnityAsync(cts.Token).ConfigureAwait(false);
            await server.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error(ex.Message);
            if (options.Debug)
                logger.Error(ex.ToString());
            return 1;
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    static void PrintVersion()
    {
        Console.WriteLine(ProductName);
        Console.WriteLine($"Name: {ServerName}");
        Console.WriteLine($"Version: {Version}");
        Console.WriteLine($"Protocol Version: {ProtocolVersion}");
        Console.WriteLine("Mode: MCP-only");
    }

    static void PrintHelp()
    {
        PrintVersion();
        Console.WriteLine();
        Console.WriteLine("Usage:");
        var executable = Path.GetFileName(Environment.ProcessPath) ?? "unibridge_relay";
        Console.WriteLine($"  {executable} --mcp [--project-id <id>] [--instance-id <pid>] [--project-path <path>] [--workspace-root <path>] [--name <client>] [--debug]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --mcp                  Run as an MCP stdio server.");
        Console.WriteLine("  --project-id <id>      Connect to a specific UniBridge project ID.");
        Console.WriteLine("  --instance-id <pid>    Connect to a specific Unity Editor PID.");
        Console.WriteLine("  --project-path <path>  Connect to a specific Unity project or Assets path.");
        Console.WriteLine("  --workspace-root <path> Prefer or validate the Unity bridge matching the current Codex workspace root.");
        Console.WriteLine("  --name <client>        Client display name reported to Unity.");
        Console.WriteLine("  --debug                Write diagnostic logs to stderr.");
        Console.WriteLine("  --version              Print version information.");
        Console.WriteLine("  --help                 Print this help.");
    }
}

sealed class RelayOptions
{
    public bool McpMode { get; private set; }
    public bool RelayMode { get; private set; }
    public bool Debug { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public int? InstanceId { get; private set; }
    public string? ProjectId { get; private set; }
    public string? ProjectPath { get; private set; }
    public string? WorkspaceRoot { get; private set; }
    public string ClientName { get; private set; } = "UniBridge Relay";

    public string? ExpectedProjectRoot => !string.IsNullOrWhiteSpace(ProjectPath)
        ? ProjectPath
        : WorkspaceRoot;

    public static RelayOptions Parse(string[] args)
    {
        var options = new RelayOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var value = GetInlineValue(arg);
            var key = value == null ? arg : arg[..arg.IndexOf('=')];

            switch (key)
            {
                case "--mcp":
                    options.McpMode = true;
                    break;
                case "--relay":
                    options.RelayMode = true;
                    break;
                case "--debug":
                    options.Debug = true;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                case "--version":
                case "-v":
                    options.ShowVersion = true;
                    break;
                case "--instance-id":
                    value ??= ReadNext(args, ref i, key);
                    if (int.TryParse(value, out var pid))
                        options.InstanceId = pid;
                    break;
                case "--project-id":
                    options.ProjectId = NormalizeProjectId(value ?? ReadNext(args, ref i, key));
                    break;
                case "--project-path":
                    options.ProjectPath = value ?? ReadNext(args, ref i, key);
                    break;
                case "--workspace-root":
                    options.WorkspaceRoot = value ?? ReadNext(args, ref i, key);
                    break;
                case "--name":
                    options.ClientName = value ?? ReadNext(args, ref i, key);
                    break;
            }
        }

        options.InstanceId ??= ReadIntEnvironment("UNIBRIDGE_INSTANCE_ID")
            ?? ReadIntEnvironment("UNITY_INSTANCE_ID");
        options.ProjectId ??= NormalizeProjectId(Environment.GetEnvironmentVariable("UNIBRIDGE_PROJECT_ID")
            ?? Environment.GetEnvironmentVariable("UNITY_PROJECT_ID"));
        options.ProjectPath ??= Environment.GetEnvironmentVariable("UNIBRIDGE_PROJECT_PATH")
            ?? Environment.GetEnvironmentVariable("UNITY_PROJECT_PATH");
        options.WorkspaceRoot ??= Environment.GetEnvironmentVariable("UNIBRIDGE_WORKSPACE_ROOT")
            ?? Environment.GetEnvironmentVariable("CODEX_WORKSPACE_ROOT")
            ?? Environment.GetEnvironmentVariable("CODEX_PROJECT_ROOT")
            ?? Environment.GetEnvironmentVariable("CODEX_CWD")
            ?? Environment.GetEnvironmentVariable("WORKSPACE_ROOT");
        options.ClientName = Environment.GetEnvironmentVariable("UNIBRIDGE_CLIENT_NAME")
            ?? options.ClientName;

        return options;
    }

    static string? GetInlineValue(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq < 0 ? null : arg[(eq + 1)..];
    }

    static string ReadNext(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");

        index++;
        return args[index];
    }

    static int? ReadIntEnvironment(string name)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? value
            : null;
    }

    static string? NormalizeProjectId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var guid))
            return guid.ToString("N");

        var compact = trimmed.Replace("-", string.Empty);
        return compact.Length == 32 && compact.All(IsHex)
            ? compact.ToLowerInvariant()
            : trimmed;
    }

    static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

sealed class Logger(bool debug)
{
    public void Info(string message)
    {
        if (debug)
            Console.Error.WriteLine($"[INFO] {message}");
    }

    public void Warn(string message) => Console.Error.WriteLine($"[WARN] {message}");
    public void Error(string message) => Console.Error.WriteLine($"[ERROR] {message}");
}

sealed class McpServer(RelayOptions options, Logger logger) : IAsyncDisposable
{
    const string ExpectedProjectRootParameter = "__unibridge_expected_project_root";

    static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    readonly UnityConnection unity = new(options, logger);
    readonly SemaphoreSlim stdoutLock = new(1, 1);
    string? clientName;
    string? clientVersion;

    public async Task TryConnectUnityAsync(CancellationToken ct)
    {
        try
        {
            await unity.ConnectAsync(ct).ConfigureAwait(false);
            await unity.SendClientInfoAsync(options.ClientName, "1.0.0", options.ClientName, ct).ConfigureAwait(false);
            await SendToolsChangedNotificationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Warn($"Unity connection is not ready: {ex.Message}");
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        logger.Info("MCP stdio server started.");

        while (!ct.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line)?.AsObject();
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(null, -32700, $"Parse error: {ex.Message}", ct).ConfigureAwait(false);
                continue;
            }

            if (message == null)
            {
                await WriteErrorAsync(null, -32600, "Invalid JSON-RPC message.", ct).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleMessageAsync(message, ct), ct);
        }
    }

    async Task HandleMessageAsync(JsonObject message, CancellationToken ct)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(method))
        {
            if (id != null)
                await WriteErrorAsync(id, -32600, "Missing method.", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    await HandleInitializeAsync(id, message["params"] as JsonObject, ct).ConfigureAwait(false);
                    break;
                case "notifications/initialized":
                case "notifications/cancelled":
                    break;
                case "tools/list":
                    await HandleToolsListAsync(id, ct).ConfigureAwait(false);
                    break;
                case "tools/call":
                    await HandleToolsCallAsync(id, message["params"] as JsonObject, ct).ConfigureAwait(false);
                    break;
                case "ping":
                    if (id != null)
                        await WriteResultAsync(id, new JsonObject(), ct).ConfigureAwait(false);
                    break;
                case "shutdown":
                    if (id != null)
                        await WriteResultAsync(id, new JsonObject(), ct).ConfigureAwait(false);
                    break;
                case "exit":
                    Environment.ExitCode = 0;
                    break;
                default:
                    if (id != null)
                        await WriteErrorAsync(id, -32601, $"Method not found: {method}", ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (id != null)
                await WriteErrorAsync(id, -32603, ex.Message, ct).ConfigureAwait(false);
        }
    }

    async Task HandleInitializeAsync(JsonNode? id, JsonObject? parameters, CancellationToken ct)
    {
        var clientInfo = parameters?["clientInfo"] as JsonObject;
        clientName = clientInfo?["name"]?.GetValue<string>() ?? options.ClientName;
        clientVersion = clientInfo?["version"]?.GetValue<string>() ?? "unknown";

        if (unity.IsConnected)
            await unity.SendClientInfoAsync(clientName, clientVersion, options.ClientName, ct).ConfigureAwait(false);

        var protocol = parameters?["protocolVersion"]?.GetValue<string>() ?? "2025-03-26";
        var result = new JsonObject
        {
            ["protocolVersion"] = protocol,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = true },
                ["prompts"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = BuildServerName(),
                ["version"] = Program.Version
            }
        };

        await WriteResultAsync(id, result, ct).ConfigureAwait(false);
    }

    async Task HandleToolsListAsync(JsonNode? id, CancellationToken ct)
    {
        await EnsureUnityConnectedAsync(ct).ConfigureAwait(false);
        try
        {
            await unity.RefreshToolsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (UnityConnection.IsConnectionFailure(ex))
        {
            logger.Warn($"Unity connection lost while refreshing tools; reconnecting once: {ex.Message}");
            await ReconnectUnityAsync(ct).ConfigureAwait(false);
            await unity.RefreshToolsAsync(ct).ConfigureAwait(false);
        }

        var tools = new JsonArray { CreateServerInfoTool() };
        foreach (var tool in unity.Tools)
            tools.Add(tool.DeepClone());

        await WriteResultAsync(id, new JsonObject { ["tools"] = tools }, ct).ConfigureAwait(false);
    }

    async Task HandleToolsCallAsync(JsonNode? id, JsonObject? parameters, CancellationToken ct)
    {
        if (parameters == null)
        {
            await WriteErrorAsync(id, -32602, "tools/call params are required.", ct).ConfigureAwait(false);
            return;
        }

        var name = parameters["name"]?.GetValue<string>();
        var args = parameters["arguments"] as JsonObject ?? new JsonObject();

        if (string.IsNullOrWhiteSpace(name))
        {
            await WriteErrorAsync(id, -32602, "Tool name is required.", ct).ConfigureAwait(false);
            return;
        }

        if (name == "_server_info")
        {
            var info = await CreateServerInfoAsync(args, ct).ConfigureAwait(false);
            await WriteToolTextResultAsync(id, info, isError: false, ct).ConfigureAwait(false);
            return;
        }

        var response = await SendUnityCommandWithReconnectAsync(name, args, ct).ConfigureAwait(false);
        var status = response["status"]?.GetValue<string>();
        var success = string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);

        if (success)
        {
            var result = response["result"]?.DeepClone() ?? new JsonObject();
            await WriteToolTextResultAsync(id, result, isError: false, ct).ConfigureAwait(false);
        }
        else
        {
            await WriteToolTextResultAsync(id, response.DeepClone(), isError: true, ct).ConfigureAwait(false);
        }
    }

    async Task<JsonObject> SendUnityCommandWithReconnectAsync(string name, JsonObject args, CancellationToken ct)
    {
        await EnsureUnityConnectedAsync(ct).ConfigureAwait(false);
        var scopedArgs = AddExpectedProjectRoot(args);

        try
        {
            return await unity.SendCommandAsync(name, scopedArgs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (UnityConnection.IsConnectionFailure(ex))
        {
            if (IsScriptCompilationReloadRecoveryCandidate(name, scopedArgs))
            {
                logger.Warn($"Unity connection lost during script compilation workflow; attempting reload-safe recovery: {ex.Message}");
                return await RecoverScriptCompilationAfterReloadAsync(name, scopedArgs, ex, ct).ConfigureAwait(false);
            }

            if (TryGetPlayModeRecoveryArgs(name, scopedArgs, out var playModeArgs, out var targetPlaying))
            {
                logger.Warn($"Unity connection lost during play mode transition; attempting reload-safe recovery: {ex.Message}");
                return await RecoverPlayModeAfterReloadAsync(name, playModeArgs, ex, targetPlaying, ct).ConfigureAwait(false);
            }

            logger.Warn($"Unity connection lost while calling {name}; reconnecting once: {ex.Message}");
            await ReconnectUnityAsync(ct).ConfigureAwait(false);
            return await unity.SendCommandAsync(name, scopedArgs, ct).ConfigureAwait(false);
        }
    }

    static bool IsScriptCompilationReloadRecoveryCandidate(string name, JsonObject args)
    {
        if (string.Equals(name, "UniBridge_ManageEditor", StringComparison.Ordinal))
            return IsCompileAction(args);

        if (!string.Equals(name, "UniBridge_BatchActions", StringComparison.Ordinal))
            return false;

        var steps = args["Steps"] as JsonArray
                    ?? args["steps"] as JsonArray
                    ?? args["Actions"] as JsonArray
                    ?? args["actions"] as JsonArray;
        if (steps == null)
            return false;

        foreach (var node in steps)
        {
            if (node is not JsonObject step)
                continue;

            var tool = ReadString(step, "tool", "Tool", "toolName", "ToolName");
            if (!IsManageEditorToolAlias(tool))
                continue;

            var stepArgs = step["parameters"] as JsonObject
                           ?? step["Parameters"] as JsonObject
                           ?? step["params"] as JsonObject
                           ?? step["Params"] as JsonObject
                           ?? step["args"] as JsonObject
                           ?? step["Args"] as JsonObject
                           ?? step;

            if (IsCompileAction(stepArgs))
                return true;
        }

        return false;
    }

    static bool IsManageEditorToolAlias(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return false;

        return tool.Equals("UniBridge_ManageEditor", StringComparison.OrdinalIgnoreCase) ||
               tool.Equals("editor", StringComparison.OrdinalIgnoreCase) ||
               tool.Equals("manage_editor", StringComparison.OrdinalIgnoreCase) ||
               tool.Equals("project_operations", StringComparison.OrdinalIgnoreCase) ||
               tool.Equals("lifecycle", StringComparison.OrdinalIgnoreCase) ||
               tool.Equals("compile", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsCompileAction(JsonObject args)
    {
        var action = ReadString(args, "Action", "action");
        if (string.IsNullOrWhiteSpace(action))
            return false;

        return action.Equals("RequestScriptCompilation", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("RequestScriptCompilationNoWait", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("request_script_compilation", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("request_script_compilation_no_wait", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("compile", StringComparison.OrdinalIgnoreCase);
    }

    static bool TryGetPlayModeRecoveryArgs(string name, JsonObject args, out JsonObject playModeArgs, out bool targetPlaying)
    {
        playModeArgs = args;
        targetPlaying = false;

        if (string.Equals(name, "UniBridge_ManageEditor", StringComparison.Ordinal))
            return TryReadPlayModeTarget(args, out targetPlaying);

        if (!string.Equals(name, "UniBridge_BatchActions", StringComparison.Ordinal))
            return false;

        var steps = args["Steps"] as JsonArray
                    ?? args["steps"] as JsonArray
                    ?? args["Actions"] as JsonArray
                    ?? args["actions"] as JsonArray;
        if (steps == null)
            return false;

        foreach (var node in steps)
        {
            if (node is not JsonObject step)
                continue;

            var tool = ReadString(step, "tool", "Tool", "toolName", "ToolName");
            if (!IsManageEditorToolAlias(tool))
                continue;

            var stepArgs = step["parameters"] as JsonObject
                           ?? step["Parameters"] as JsonObject
                           ?? step["params"] as JsonObject
                           ?? step["Params"] as JsonObject
                           ?? step["args"] as JsonObject
                           ?? step["Args"] as JsonObject
                           ?? step;

            if (!TryReadPlayModeTarget(stepArgs, out targetPlaying))
                continue;

            playModeArgs = stepArgs;
            return true;
        }

        return false;
    }

    static bool TryReadPlayModeTarget(JsonObject args, out bool targetPlaying)
    {
        targetPlaying = false;
        var action = ReadString(args, "Action", "action");
        if (string.IsNullOrWhiteSpace(action))
            return false;

        var normalized = action.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
        if (normalized.Equals("Play", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("RequestPlayModeNoWait", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("RequestPlayNoWait", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("PlayNoWait", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("EnterPlayModeNoWait", StringComparison.OrdinalIgnoreCase))
        {
            targetPlaying = true;
            return true;
        }

        if (normalized.Equals("Stop", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ExitPlayMode", StringComparison.OrdinalIgnoreCase))
        {
            targetPlaying = false;
            return true;
        }

        return false;
    }

    async Task<JsonObject> RecoverScriptCompilationAfterReloadAsync(string originalTool, JsonObject originalArgs, Exception failure, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var timeoutMs = ReadInt(originalArgs, 120000, "TimeoutMs", "timeoutMs", "timeout_ms", "timeout");
        timeoutMs = Math.Clamp(timeoutMs, 5000, 300000);
        var pollIntervalMs = ReadInt(originalArgs, 500, "PollIntervalMs", "pollIntervalMs", "poll_interval_ms", "poll");
        pollIntervalMs = Math.Clamp(pollIntervalMs, 50, 5000);
        var requireNotPlaying = ReadBool(originalArgs, false, "RequireNotPlaying", "requireNotPlaying", "require_not_playing");

        await ReconnectUnityUntilAsync(timeoutMs, ct).ConfigureAwait(false);

        var remainingMs = RemainingTimeoutMs(started, timeoutMs);
        var waitResponse = await CallManageEditorWithFallbackAsync(
            primaryAction: "WaitForReadyAfterReload",
            fallbackAction: "WaitForReady",
            timeoutMs: remainingMs,
            pollIntervalMs: pollIntervalMs,
            requireNotPlaying: requireNotPlaying,
            ct: ct).ConfigureAwait(false);

        var diagnosticsResponse = await TryCallManageEditorAsync(new JsonObject
        {
            ["Action"] = "GetCompilationDiagnostics"
        }, ct).ConfigureAwait(false);

        var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        var data = new JsonObject
        {
            ["recoveredAfterReload"] = true,
            ["originalTool"] = originalTool,
            ["originalAction"] = ReadString(originalArgs, "Action", "action"),
            ["connectionFailure"] = failure.Message,
            ["elapsedMs"] = elapsedMs,
            ["timeoutMs"] = timeoutMs,
            ["waitResult"] = ExtractUnityResult(waitResponse),
            ["compilationDiagnostics"] = diagnosticsResponse != null ? ExtractUnityResult(diagnosticsResponse) : null,
            ["nextSuggestedCalls"] = new JsonArray(
                "UniBridge_ManageEditor Action=GetCompilationDiagnostics",
                "UniBridge_ReadConsole Action=DiagnosticSummary")
        };

        var result = new JsonObject
        {
            ["success"] = true,
            ["message"] = originalTool.Equals("UniBridge_BatchActions", StringComparison.Ordinal)
                ? "Unity reloaded during a script-compilation batch. Relay reconnected and confirmed the editor is ready."
                : "Script compilation completed after Unity reload. Relay reconnected and confirmed the editor is ready.",
            ["data"] = data
        };

        if (originalTool.Equals("UniBridge_BatchActions", StringComparison.Ordinal))
        {
            data["batchInterruptedByReload"] = true;
            data["batchResumeNote"] = "Steps before RequestScriptCompilation may have completed, but the Unity domain reload interrupted the in-process BatchActions result. Run post-compile verification calls after this recovered response.";
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["result"] = result
        };
    }

    async Task<JsonObject> RecoverPlayModeAfterReloadAsync(string originalTool, JsonObject playModeArgs, Exception failure, bool targetPlaying, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var timeoutMs = ReadInt(playModeArgs, 120000, "TimeoutMs", "timeoutMs", "timeout_ms", "timeout");
        timeoutMs = Math.Clamp(timeoutMs, 5000, 300000);
        var pollIntervalMs = ReadInt(playModeArgs, 500, "PollIntervalMs", "pollIntervalMs", "poll_interval_ms", "poll");
        pollIntervalMs = Math.Clamp(pollIntervalMs, 50, 5000);
        var requireNotPlaying = ReadBool(playModeArgs, !targetPlaying, "RequireNotPlaying", "requireNotPlaying", "require_not_playing");

        await ReconnectUnityUntilAsync(timeoutMs, ct).ConfigureAwait(false);

        var remainingMs = RemainingTimeoutMs(started, timeoutMs);
        var waitAction = targetPlaying ? "WaitForPlayMode" : "WaitForEditMode";
        var waitResponse = await CallManageEditorWithFallbackAsync(
            primaryAction: waitAction,
            fallbackAction: "WaitForReady",
            timeoutMs: remainingMs,
            pollIntervalMs: pollIntervalMs,
            requireNotPlaying: requireNotPlaying,
            ct: ct).ConfigureAwait(false);

        var playModeStateResponse = await TryCallManageEditorAsync(new JsonObject
        {
            ["Action"] = "GetPlayModeState"
        }, ct).ConfigureAwait(false);

        var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        var data = new JsonObject
        {
            ["recoveredAfterPlayModeReload"] = true,
            ["originalTool"] = originalTool,
            ["originalAction"] = ReadString(playModeArgs, "Action", "action"),
            ["targetPlaying"] = targetPlaying,
            ["connectionFailure"] = failure.Message,
            ["elapsedMs"] = elapsedMs,
            ["timeoutMs"] = timeoutMs,
            ["waitResult"] = ExtractUnityResult(waitResponse),
            ["playModeState"] = playModeStateResponse != null ? ExtractUnityResult(playModeStateResponse) : null,
            ["nextSuggestedCalls"] = targetPlaying
                ? new JsonArray(
                    "UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=false",
                    "UniBridge_ReadConsole Action=DiagnosticSummary")
                : new JsonArray(
                    "UniBridge_ManageEditor Action=WaitForReady RequireNotPlaying=true",
                    "UniBridge_ReadConsole Action=DiagnosticSummary")
        };

        var result = new JsonObject
        {
            ["success"] = true,
            ["message"] = originalTool.Equals("UniBridge_BatchActions", StringComparison.Ordinal)
                ? "Unity reloaded during a Play Mode batch. Relay reconnected and confirmed the requested play-mode state."
                : "Unity reloaded during Play Mode transition. Relay reconnected and confirmed the requested play-mode state.",
            ["data"] = data
        };

        if (originalTool.Equals("UniBridge_BatchActions", StringComparison.Ordinal))
        {
            data["batchInterruptedByReload"] = true;
            data["batchResumeNote"] = "Steps before the Play Mode transition may have completed, but Unity domain reload interrupted the in-process BatchActions result. Run the suggested post-reconnect verification calls before continuing.";
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["result"] = result
        };
    }

    async Task ReconnectUnityUntilAsync(int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Exception? last = null;

        while (DateTime.UtcNow <= deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ReconnectUnityAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                logger.Warn($"Unity reload reconnect attempt failed; waiting for bridge to republish: {ex.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        throw new IOException($"Unity reload recovery timed out after {timeoutMs}ms waiting for a fresh bridge connection.", last);
    }

    async Task<JsonObject> CallManageEditorWithFallbackAsync(
        string primaryAction,
        string fallbackAction,
        int timeoutMs,
        int pollIntervalMs,
        bool requireNotPlaying,
        CancellationToken ct)
    {
        var primaryArgs = BuildWaitManageEditorArgs(primaryAction, timeoutMs, pollIntervalMs, requireNotPlaying);
        var primary = await TryCallManageEditorAsync(primaryArgs, ct).ConfigureAwait(false);
        if (primary != null && IsUnityResponseSuccess(primary))
            return primary;

        var primaryText = primary?.ToJsonString(CompactJson) ?? "(no response)";
        logger.Warn($"ManageEditor {primaryAction} did not succeed during reload recovery; falling back to {fallbackAction}: {Truncate(primaryText, 240)}");

        var fallbackArgs = BuildWaitManageEditorArgs(fallbackAction, timeoutMs, pollIntervalMs, requireNotPlaying);
        return await unity.SendCommandAsync("UniBridge_ManageEditor", AddExpectedProjectRoot(fallbackArgs), ct).ConfigureAwait(false);
    }

    async Task<JsonObject?> TryCallManageEditorAsync(JsonObject args, CancellationToken ct)
    {
        try
        {
            return await unity.SendCommandAsync("UniBridge_ManageEditor", AddExpectedProjectRoot(args), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (UnityConnection.IsConnectionFailure(ex))
        {
            logger.Warn($"Unity connection dropped during reload recovery helper call; reconnecting once: {ex.Message}");
            await ReconnectUnityAsync(ct).ConfigureAwait(false);
            return await unity.SendCommandAsync("UniBridge_ManageEditor", AddExpectedProjectRoot(args), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Warn($"ManageEditor helper call failed during reload recovery: {ex.Message}");
            return null;
        }
    }

    static JsonObject BuildWaitManageEditorArgs(string action, int timeoutMs, int pollIntervalMs, bool requireNotPlaying) =>
        new()
        {
            ["Action"] = action,
            ["TimeoutMs"] = timeoutMs,
            ["PollIntervalMs"] = pollIntervalMs,
            ["RequireNotPlaying"] = requireNotPlaying
        };

    static bool IsUnityResponseSuccess(JsonObject response)
    {
        var status = response["status"]?.GetValue<string>();
        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            return false;

        if (response["result"] is JsonObject result &&
            result["success"] is JsonValue value &&
            value.TryGetValue<bool>(out var successValue))
        {
            return successValue;
        }

        return true;
    }

    static JsonNode? ExtractUnityResult(JsonObject response) =>
        response["result"]?.DeepClone() ?? response.DeepClone();

    static int RemainingTimeoutMs(DateTime startedUtc, int timeoutMs)
    {
        var elapsedMs = (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds;
        return Math.Max(1000, timeoutMs - elapsedMs);
    }

    static string? ReadString(JsonObject args, params string[] names)
    {
        foreach (var name in names)
        {
            if (args[name] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        }

        return null;
    }

    static int ReadInt(JsonObject args, int defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (args[name] == null)
                continue;

            if (args[name] is JsonValue value)
            {
                if (value.TryGetValue<int>(out var intValue))
                    return intValue;

                if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed))
                    return parsed;
            }
        }

        return defaultValue;
    }

    static bool ReadBool(JsonObject args, bool defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (args[name] == null)
                continue;

            if (args[name] is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var boolValue))
                    return boolValue;

                if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed))
                    return parsed;
            }
        }

        return defaultValue;
    }

    static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    JsonObject AddExpectedProjectRoot(JsonObject args)
    {
        var scoped = args.DeepClone().AsObject();
        var expectedRoot = options.ExpectedProjectRoot;
        if (!string.IsNullOrWhiteSpace(expectedRoot))
            scoped[ExpectedProjectRootParameter] = NormalizeProjectRoot(expectedRoot);
        return scoped;
    }

    async Task ReconnectUnityAsync(CancellationToken ct)
    {
        await unity.DisconnectAsync("Reconnecting to Unity.").ConfigureAwait(false);
        await EnsureUnityConnectedAsync(ct).ConfigureAwait(false);
    }

    async Task EnsureUnityConnectedAsync(CancellationToken ct)
    {
        if (unity.IsConnected)
            return;

        await unity.ConnectAsync(ct).ConfigureAwait(false);
        await unity.SendClientInfoAsync(
            clientName ?? options.ClientName,
            clientVersion ?? "1.0.0",
            options.ClientName,
            ct).ConfigureAwait(false);
        await SendToolsChangedNotificationAsync(ct).ConfigureAwait(false);
    }

    async Task SendToolsChangedNotificationAsync(CancellationToken ct)
    {
        await WriteMessageAsync(new JsonObject
        {
            ["method"] = "notifications/tools/list_changed",
            ["jsonrpc"] = "2.0"
        }, ct).ConfigureAwait(false);
    }

    async Task<JsonObject> CreateServerInfoAsync(JsonObject args, CancellationToken ct)
    {
        var action = args["action"]?.GetValue<string>() ?? "status";

        if (string.Equals(action, "reconnect", StringComparison.OrdinalIgnoreCase))
        {
            await ReconnectUnityAsync(ct).ConfigureAwait(false);
            var reconnected = CreateServerStatus();
            reconnected["reconnected"] = unity.IsConnected;
            return reconnected;
        }

        var status = CreateServerStatus();

        return action switch
        {
            "tools" => new JsonObject
            {
                ["toolCount"] = unity.Tools.Count,
                ["tools"] = new JsonArray(unity.Tools.Select(t => t.DeepClone()).ToArray())
            },
            "connections" => CreateConnectionsInfo(),
            "all" => new JsonObject
            {
                ["status"] = status,
                ["connections"] = CreateConnectionsInfo(),
                ["tools"] = new JsonObject
                {
                    ["toolCount"] = unity.Tools.Count,
                    ["tools"] = new JsonArray(unity.Tools.Select(t => t.DeepClone()).ToArray())
                }
            },
            _ => status
        };
    }

    JsonObject CreateServerStatus() => new()
    {
        ["relay"] = ProgramInfo(),
        ["unityConnected"] = unity.IsConnected,
        ["selectedConnection"] = unity.ConnectionPath,
        ["projectId"] = unity.ProjectId,
        ["projectName"] = unity.ProjectName,
        ["projectPath"] = unity.ProjectPath,
        ["projectRoot"] = unity.ProjectRoot,
        ["editorPid"] = unity.EditorPid,
        ["expectedProjectRoot"] = NormalizeProjectRoot(options.ExpectedProjectRoot),
        ["workspaceRoot"] = NormalizeProjectRoot(options.WorkspaceRoot),
        ["toolCount"] = unity.Tools.Count
    };

    JsonObject ProgramInfo() => new()
    {
        ["name"] = BuildServerName(),
        ["baseName"] = "unibridge-relay",
        ["version"] = Program.Version,
        ["mode"] = "mcp-only"
    };

    JsonObject CreateConnectionsInfo()
    {
        var available = Discovery.ListConnections(options, logger)
            .Select(ToJson)
            .ToArray();

        return new JsonObject
        {
            ["selectedConnection"] = unity.ConnectionPath,
            ["projectId"] = unity.ProjectId,
            ["projectName"] = unity.ProjectName,
            ["projectPath"] = unity.ProjectPath,
            ["projectRoot"] = unity.ProjectRoot,
            ["editorPid"] = unity.EditorPid,
            ["connected"] = unity.IsConnected,
            ["expectedProjectRoot"] = NormalizeProjectRoot(options.ExpectedProjectRoot),
            ["availableConnections"] = new JsonArray(available)
        };
    }

    static JsonObject ToJson(DiscoveryEntry entry) => new()
    {
        ["connectionType"] = entry.ConnectionType,
        ["connectionPath"] = entry.ConnectionPath,
        ["projectId"] = entry.ProjectId,
        ["projectName"] = entry.ProjectName,
        ["projectPath"] = entry.ProjectPath,
        ["projectRoot"] = entry.ProjectRoot,
        ["editorPid"] = entry.EditorPid,
        ["lastWriteUtc"] = entry.LastWriteUtc.ToString("O"),
        ["sourceFile"] = entry.SourceFile
    };

    string BuildServerName()
    {
        var projectName = unity.ProjectName;
        if (string.IsNullOrWhiteSpace(projectName))
            projectName = ProjectNameFromPath(options.ExpectedProjectRoot);

        var projectId = unity.ProjectId ?? options.ProjectId;
        return BuildProjectScopedName(projectName, projectId, options.ExpectedProjectRoot);
    }

    static string BuildProjectScopedName(string? projectName, string? projectId, string? fallbackRoot)
    {
        var slug = Slugify(projectName);
        var suffix = ShortProjectId(projectId);
        if (string.IsNullOrWhiteSpace(suffix))
            suffix = !string.IsNullOrWhiteSpace(fallbackRoot)
                ? StableHash(NormalizeProjectRoot(fallbackRoot) ?? fallbackRoot)
                : "project";

        return $"unibridge_{slug}_{suffix}";
    }

    static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unity_project";

        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = true;
        var previousWasLowerOrDigit = false;

        foreach (var c in value.Trim())
        {
            if (char.IsUpper(c) && previousWasLowerOrDigit && !lastWasSeparator)
            {
                builder.Append('_');
                lastWasSeparator = true;
            }

            if (c is >= 'A' and <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSeparator = false;
                previousWasLowerOrDigit = true;
            }
            else if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
                lastWasSeparator = false;
                previousWasLowerOrDigit = true;
            }
            else if (!lastWasSeparator)
            {
                builder.Append('_');
                lastWasSeparator = true;
                previousWasLowerOrDigit = false;
            }
            else
            {
                previousWasLowerOrDigit = false;
            }
        }

        var slug = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(slug))
            return "unity_project";

        const int maxSlugLength = 40;
        return slug.Length <= maxSlugLength
            ? slug
            : slug[..maxSlugLength].TrimEnd('_');
    }

    static string? ShortProjectId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var guid))
            return guid.ToString("N")[..8];

        var compact = trimmed.Replace("-", string.Empty);
        if (compact.Length >= 8 && compact.Take(8).All(IsHex))
            return compact[..8].ToLowerInvariant();

        return StableHash(trimmed);
    }

    static string? ProjectNameFromPath(string? path)
    {
        var root = NormalizeProjectRoot(path);
        if (string.IsNullOrWhiteSpace(root))
            return null;

        try
        {
            return Path.GetFileName(root);
        }
        catch
        {
            return null;
        }
    }

    static string? NormalizeProjectRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = NormalizePath(path);
        if (normalized.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^"/Assets".Length];

        return normalized.TrimEnd('/');
    }

    static string NormalizePath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch
        {
            // Keep non-local path values intact enough for display and comparison.
        }

        return trimmed.Replace('\\', '/').TrimEnd('/');
    }

    static string StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant();
    }

    static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    static JsonObject CreateServerInfoTool() => new()
    {
        ["name"] = "_server_info",
        ["description"] = "UniBridge Unity MCP discovery/status tool. Search aliases: UniBridge, Unity, ValidateScript, RefreshAssets, RequestScriptCompilationNoWait, WaitForReadyAfterReload, GetCompilationDiagnostics, ReadConsole, DiagnosticSummary, ClearConsole, PlayMode, WaitForPlayMode, WaitForEditMode, ValidateAdditiveSceneRegistration. Inspect relay state, Unity bridge connections, available tools, or force a Unity reconnect.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Information to retrieve, or 'reconnect' to drop the current Unity pipe and select a fresh one.",
                    ["enum"] = new JsonArray("connections", "tools", "status", "all", "reconnect"),
                    ["default"] = "status"
                }
            },
            ["required"] = new JsonArray()
        }
    };

    async Task WriteToolTextResultAsync(JsonNode? id, JsonNode payload, bool isError, CancellationToken ct)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = payload.ToJsonString(PrettyJson)
                }
            }
        };

        if (payload is JsonObject obj && obj["structuredContent"] != null)
            result["structuredContent"] = obj["structuredContent"]!.DeepClone();

        if (isError)
            result["isError"] = true;

        await WriteResultAsync(id, result, ct).ConfigureAwait(false);
    }

    async Task WriteResultAsync(JsonNode? id, JsonObject result, CancellationToken ct)
    {
        var response = new JsonObject
        {
            ["result"] = result,
            ["jsonrpc"] = "2.0"
        };

        if (id != null)
            response["id"] = id.DeepClone();

        await WriteMessageAsync(response, ct).ConfigureAwait(false);
    }

    async Task WriteErrorAsync(JsonNode? id, int code, string message, CancellationToken ct)
    {
        var response = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            },
            ["jsonrpc"] = "2.0"
        };

        if (id != null)
            response["id"] = id.DeepClone();

        await WriteMessageAsync(response, ct).ConfigureAwait(false);
    }

    async Task WriteMessageAsync(JsonObject message, CancellationToken ct)
    {
        var json = message.ToJsonString(CompactJson);
        await stdoutLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Console.Out.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await Console.Out.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            stdoutLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        stdoutLock.Dispose();
        await unity.DisposeAsync().ConfigureAwait(false);
    }
}

sealed class UnityConnection(RelayOptions options, Logger logger) : IAsyncDisposable
{
    const int MaxMessageBytes = 64 * 1024 * 1024;
    const int TargetedDiscoveryRetryMs = 15000;
    const int UntargetedDiscoveryRetryMs = 3000;
    const int DiscoveryRetryDelayMs = 250;
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> pending = new();
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly SemaphoreSlim connectionLock = new(1, 1);
    readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    Stream? connectionStream;
    NamedPipeClientStream? namedPipe;
    Socket? unixSocket;
    StreamReader? reader;
    StreamWriter? writer;
    Task? readerTask;
    readonly string requestPrefix = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
    int nextRequestId;
    string? toolsHash;

    public bool IsConnected => IsTransportOpen();
    public List<JsonObject> Tools { get; } = new();
    public string? ConnectionPath { get; private set; }
    public string? ProjectId { get; private set; }
    public string? ProjectName { get; private set; }
    public string? ProjectPath { get; private set; }
    public string? ProjectRoot { get; private set; }
    public int? EditorPid { get; private set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await connectionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected)
                return;

            if (connectionStream != null || reader != null || writer != null || readerTask != null)
                await CloseConnectionCoreAsync("Resetting stale Unity connection.", waitForReader: false).ConfigureAwait(false);

            var entry = await FindConnectionWithRetryAsync(ct).ConfigureAwait(false);

            connectionStream = await OpenConnectionStreamAsync(entry, ct).ConfigureAwait(false);

            reader = new StreamReader(connectionStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            writer = new StreamWriter(connectionStream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            var handshakeLine = await ReadLineWithTimeoutAsync(10000, ct).ConfigureAwait(false);
            var handshake = JsonNode.Parse(handshakeLine)?.AsObject()
                ?? throw new InvalidOperationException("Unity handshake was not a JSON object.");

            var protocol = handshake["protocol"]?.GetValue<string>();
            var version = handshake["version"]?.GetValue<string>();
            if (!string.Equals(protocol, "unity-mcp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unexpected Unity protocol: {protocol ?? "(missing)"}");

            ConnectionPath = entry.ConnectionPath;
            ProjectId = entry.ProjectId;
            ProjectName = entry.ProjectName;
            ProjectPath = entry.ProjectPath;
            ProjectRoot = entry.ProjectRoot;
            EditorPid = entry.EditorPid;

            toolsHash = handshake["toolsHash"]?.GetValue<string>();
            Tools.Clear();
            if (handshake["tools"] is JsonArray tools)
            {
                foreach (var node in tools)
                {
                    if (node is JsonObject tool)
                        Tools.Add(tool.DeepClone().AsObject());
                }
            }

            logger.Info($"Unity MCP handshake received: protocol={protocol} version={version} tools={Tools.Count}");
            readerTask = Task.Run(() => ReadLoopAsync(ct), ct);
        }
        catch
        {
            await CloseConnectionCoreAsync("Unity connection attempt failed.", waitForReader: false).ConfigureAwait(false);
            throw;
        }
        finally
        {
            connectionLock.Release();
        }
    }

    async Task<DiscoveryEntry> FindConnectionWithRetryAsync(CancellationToken ct)
    {
        var timeoutMs = HasExplicitTarget(options)
            ? TargetedDiscoveryRetryMs
            : UntargetedDiscoveryRetryMs;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var loggedWait = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var entry = Discovery.FindConnection(options, logger);
            if (entry != null)
                return entry;

            if (DateTime.UtcNow >= deadline)
            {
                var available = Discovery.ListConnections(options, logger);
                throw new InvalidOperationException(available.Count > 0
                    ? Discovery.BuildNoMatchingConnectionMessage(options, available)
                    : "Unity not detected. No matching UniBridge discovery file was found.");
            }

            if (!loggedWait)
            {
                loggedWait = true;
                logger.Info("Unity discovery file is temporarily unavailable; waiting for the editor bridge to republish it.");
            }

            await Task.Delay(DiscoveryRetryDelayMs, ct).ConfigureAwait(false);
        }
    }

    static bool HasExplicitTarget(RelayOptions options) =>
        options.InstanceId.HasValue ||
        !string.IsNullOrWhiteSpace(options.ProjectId) ||
        !string.IsNullOrWhiteSpace(options.ProjectPath) ||
        !string.IsNullOrWhiteSpace(options.WorkspaceRoot);

    bool IsTransportOpen()
    {
        if (connectionStream == null || reader == null || writer == null)
            return false;

        if (!connectionStream.CanRead || !connectionStream.CanWrite)
            return false;

        return readerTask == null || !readerTask.IsCompleted;
    }

    public async Task DisconnectAsync(string reason)
    {
        await connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseConnectionCoreAsync(reason, waitForReader: true).ConfigureAwait(false);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    async Task CloseConnectionCoreAsync(string reason, bool waitForReader)
    {
        var oldWriter = writer;
        var oldReader = reader;
        var oldStream = connectionStream;
        var oldPipe = namedPipe;
        var oldSocket = unixSocket;
        var oldReaderTask = readerTask;

        writer = null;
        reader = null;
        connectionStream = null;
        namedPipe = null;
        unixSocket = null;
        readerTask = null;
        toolsHash = null;

        ConnectionPath = null;
        ProjectId = null;
        ProjectName = null;
        ProjectPath = null;
        ProjectRoot = null;
        EditorPid = null;
        Tools.Clear();

        var exception = new IOException(reason);
        foreach (var item in pending)
        {
            if (pending.TryRemove(item.Key, out var tcs))
                tcs.TrySetException(exception);
        }

        DisposeQuietly(oldWriter);
        DisposeQuietly(oldReader);
        DisposeQuietly(oldStream);
        DisposeQuietly(oldPipe);
        DisposeQuietly(oldSocket);

        if (waitForReader &&
            oldReaderTask != null &&
            !oldReaderTask.IsCompleted &&
            Task.CurrentId != oldReaderTask.Id)
        {
            await Task.WhenAny(oldReaderTask, Task.Delay(250)).ConfigureAwait(false);
        }
    }

    static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // Best effort only.
        }
    }

    public static bool IsConnectionFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is IOException or ObjectDisposedException or SocketException)
                return true;

            if (current is InvalidOperationException &&
                (current.Message.Contains("Unity connection is not established", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("Unity pipe", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    async Task<Stream> OpenConnectionStreamAsync(DiscoveryEntry entry, CancellationToken ct)
    {
        var connectionType = NormalizeConnectionType(entry);
        switch (connectionType)
        {
            case "named_pipe":
                return await OpenNamedPipeAsync(entry.ConnectionPath, ct).ConfigureAwait(false);
            case "unix_socket":
                return await OpenUnixSocketAsync(entry.ConnectionPath, ct).ConfigureAwait(false);
            default:
                throw new NotSupportedException($"Unsupported connection type: {entry.ConnectionType ?? "(missing)"}");
        }
    }

    string NormalizeConnectionType(DiscoveryEntry entry)
    {
        var type = entry.ConnectionType?.Trim();
        if (string.IsNullOrWhiteSpace(type))
            type = InferConnectionType(entry.ConnectionPath);

        if (string.Equals(type, "named_pipe", StringComparison.OrdinalIgnoreCase) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            LooksLikeUnixSocketPath(entry.ConnectionPath))
        {
            logger.Warn("Discovery entry reports named_pipe on a Unix platform; treating the absolute path as unix_socket for compatibility.");
            return "unix_socket";
        }

        return type.ToLowerInvariant();
    }

    static string InferConnectionType(string connectionPath)
    {
        if (connectionPath.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
            return "named_pipe";

        if (LooksLikeUnixSocketPath(connectionPath))
            return "unix_socket";

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "named_pipe"
            : "unix_socket";
    }

    static bool LooksLikeUnixSocketPath(string connectionPath) =>
        connectionPath.StartsWith("/", StringComparison.Ordinal) ||
        connectionPath.StartsWith("~/", StringComparison.Ordinal);

    async Task<Stream> OpenNamedPipeAsync(string connectionPath, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            logger.Warn("Opening a named pipe on a non-Windows platform. Unix socket discovery is preferred for UniBridge.");

        var pipeName = NormalizePipeName(connectionPath);
        logger.Info($"Connecting to named pipe: {connectionPath}");

        namedPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await namedPipe.ConnectAsync(5000, ct).ConfigureAwait(false);
        return namedPipe;
    }

    async Task<Stream> OpenUnixSocketAsync(string connectionPath, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Unix socket connections are not supported on Windows.");

        var socketPath = ExpandHome(connectionPath);
        logger.Info($"Connecting to Unix socket: {socketPath}");

        unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await unixSocket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
            return new NetworkStream(unixSocket, ownsSocket: true);
        }
        catch
        {
            unixSocket.Dispose();
            unixSocket = null;
            throw;
        }
    }

    static string ExpandHome(string path)
    {
        if (!path.StartsWith("~/", StringComparison.Ordinal))
            return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home)
            ? path
            : Path.Combine(home, path[2..]);
    }

    public async Task SendClientInfoAsync(string name, string version, string title, CancellationToken ct)
    {
        if (!IsConnected)
            return;

        await SendCommandAsync("set_client_info", new JsonObject
        {
            ["name"] = name,
            ["version"] = version,
            ["title"] = title
        }, ct).ConfigureAwait(false);
    }

    public async Task RefreshToolsAsync(CancellationToken ct)
    {
        if (!IsConnected)
            return;

        var response = await SendCommandAsync("get_available_tools", new JsonObject
        {
            ["hash"] = toolsHash
        }, ct).ConfigureAwait(false);

        if (!string.Equals(response["status"]?.GetValue<string>(), "success", StringComparison.OrdinalIgnoreCase))
            return;

        var result = response["result"] as JsonObject;
        if (result == null || result["unchanged"]?.GetValue<bool>() == true)
            return;

        toolsHash = result["hash"]?.GetValue<string>() ?? toolsHash;
        if (result["tools"] is not JsonArray tools)
            return;

        Tools.Clear();
        foreach (var node in tools)
        {
            if (node is JsonObject tool)
                Tools.Add(tool.DeepClone().AsObject());
        }

        logger.Info($"Refreshed Unity tools: {Tools.Count}");
    }

    public async Task<JsonObject> SendCommandAsync(string type, JsonObject parameters, CancellationToken ct)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Unity connection is not established.");

        var requestId = $"{requestPrefix}-{Interlocked.Increment(ref nextRequestId)}";
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[requestId] = tcs;

        var request = new JsonObject
        {
            ["type"] = type,
            ["params"] = parameters.DeepClone(),
            ["requestId"] = requestId
        };

        try
        {
            await WriteRawAsync(request, ct).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await using var registration = linked.Token.Register(() =>
            {
                if (pending.TryRemove(requestId, out var pendingTcs))
                    pendingTcs.TrySetCanceled(linked.Token);
            });

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            pending.TryRemove(requestId, out _);
            await DisconnectAsync($"Unity connection lost: {ex.Message}").ConfigureAwait(false);
            throw new IOException($"Unity connection lost: {ex.Message}", ex);
        }
        catch
        {
            pending.TryRemove(requestId, out _);
            throw;
        }
    }

    async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                string line;
                try
                {
                    line = await ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line)?.AsObject();
                }
                catch (JsonException)
                {
                    logger.Warn($"Ignoring invalid Unity JSON: {Truncate(line, 160)}");
                    continue;
                }

                if (message == null)
                    continue;

                var type = message["type"]?.GetValue<string>();
                if (type is "command_in_progress" or "approval_pending")
                    continue;

                var requestId = message["requestId"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(requestId) && pending.TryRemove(requestId, out var tcs))
                {
                    message.Remove("requestId");
                    tcs.TrySetResult(message);
                    continue;
                }

                logger.Info($"Unity event: {Truncate(line, 200)}");
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    await DisconnectAsync("Unity connection closed.").ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Relay shutdown already owns cleanup.
                }
            }
        }
    }

    async Task WriteRawAsync(JsonObject message, CancellationToken ct)
    {
        if (writer == null)
            throw new InvalidOperationException("Unity pipe writer is not available.");

        var json = message.ToJsonString(jsonOptions);
        var bytes = Encoding.UTF8.GetByteCount(json) + 1;
        if (bytes > MaxMessageBytes)
            throw new InvalidOperationException($"Unity message too large: {bytes} bytes.");

        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    async Task<string> ReadLineWithTimeoutAsync(int timeoutMs, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return await ReadLineAsync(linked.Token).ConfigureAwait(false);
    }

    async Task<string> ReadLineAsync(CancellationToken ct)
    {
        if (reader == null)
            throw new InvalidOperationException("Unity pipe reader is not available.");

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        return line ?? throw new IOException("Unity pipe closed.");
    }

    static string NormalizePipeName(string connectionPath)
    {
        const string prefix = @"\\.\pipe\";
        if (connectionPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return connectionPath[prefix.Length..];
        return connectionPath;
    }

    static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync("Relay is shutting down.").ConfigureAwait(false);
        writeLock.Dispose();
        connectionLock.Dispose();
    }
}

static class Discovery
{
    public static DiscoveryEntry? FindConnection(RelayOptions options, Logger logger)
    {
        var liveCandidates = GetLiveConnections(options, logger);
        var matchingCandidates = liveCandidates
            .Where(e => Matches(options, e))
            .OrderByDescending(e => WorkspaceRank(options, e))
            .ThenByDescending(e => e.LastWriteUtc)
            .ToList();

        if (matchingCandidates.Count == 0)
            return null;

        if (!options.InstanceId.HasValue && matchingCandidates.Count > 1)
            throw new InvalidOperationException(BuildAmbiguousConnectionMessage(options, matchingCandidates));

        return matchingCandidates.FirstOrDefault();
    }

    public static IReadOnlyList<DiscoveryEntry> ListConnections(RelayOptions options, Logger logger) =>
        GetLiveConnections(options, logger)
            .OrderByDescending(e => WorkspaceRank(options, e))
            .ThenByDescending(e => e.LastWriteUtc)
            .ToList();

    public static string BuildNoMatchingConnectionMessage(RelayOptions options, IReadOnlyList<DiscoveryEntry> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Unity not detected for the requested UniBridge scope.");
        AppendRequestedScope(sb, options);
        AppendCandidates(sb, candidates);
        return sb.ToString().TrimEnd();
    }

    static List<DiscoveryEntry> GetLiveConnections(RelayOptions options, Logger logger)
    {
        var entries = GetDiscoveryFiles(options)
            .Select(ReadEntry)
            .Where(e => e != null)
            .Select(e => e!)
            .OrderByDescending(e => e.LastWriteUtc)
            .ToList();

        var liveEntries = new List<DiscoveryEntry>();
        foreach (var entry in entries)
        {
            if (entry.EditorPid.HasValue && !IsProcessAlive(entry.EditorPid.Value))
            {
                logger.Info($"Skipping stale Unity discovery file for dead PID {entry.EditorPid.Value}: {entry.SourceFile}");
                TryDeleteStaleDiscoveryFile(entry.SourceFile, logger);
                continue;
            }

            liveEntries.Add(entry);
        }

        return liveEntries
            .GroupBy(GetConnectionIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.LastWriteUtc).First())
            .OrderByDescending(entry => entry.LastWriteUtc)
            .ToList();
    }

    static void TryDeleteStaleDiscoveryFile(string? path, Logger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName) ||
                !fileName.StartsWith("bridge-", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("bridge-status-", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!File.Exists(path))
                return;

            File.Delete(path);
            logger.Info($"Deleted stale Unity discovery file: {path}");
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to delete stale Unity discovery file '{path}': {ex.Message}");
        }
    }

    static string GetConnectionIdentity(DiscoveryEntry entry)
    {
        if (entry.EditorPid.HasValue)
            return $"pid:{entry.EditorPid.Value}";

        if (!string.IsNullOrWhiteSpace(entry.ConnectionPath))
            return $"path:{entry.ConnectionPath}";

        return $"file:{entry.SourceFile}";
    }

    static IEnumerable<string> GetDiscoveryFiles(RelayOptions options)
    {
        var dirs = new List<string>();

        AddEnvDir(dirs, "UNIBRIDGE_MCP_STATUS_DIR");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".unibridge", "mcp", "connections"));

        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "bridge-*.json"))
            {
                if (Path.GetFileName(file).StartsWith("bridge-status-", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return file;
            }
        }
    }

    static void AddEnvDir(List<string> dirs, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            dirs.Add(value);
    }

    static DiscoveryEntry? ReadEntry(string file)
    {
        try
        {
            var json = File.ReadAllText(file);
            var entry = JsonSerializer.Deserialize<DiscoveryEntry>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (entry == null || string.IsNullOrWhiteSpace(entry.ConnectionPath))
                return null;

            entry.SourceFile = file;
            entry.LastWriteUtc = File.GetLastWriteTimeUtc(file);
            entry.ProjectRoot = NormalizeProjectRoot(entry.ProjectRoot ?? entry.ProjectPath);
            return entry;
        }
        catch
        {
            return null;
        }
    }

    static bool Matches(RelayOptions options, DiscoveryEntry entry)
    {
        if (options.InstanceId.HasValue && entry.EditorPid != options.InstanceId)
            return false;

        if (!string.IsNullOrWhiteSpace(options.ProjectId) && !ProjectIdMatches(options.ProjectId, entry.ProjectId))
            return false;

        var requestedPath = !string.IsNullOrWhiteSpace(options.ProjectPath)
            ? options.ProjectPath
            : options.WorkspaceRoot;
        if (!string.IsNullOrWhiteSpace(requestedPath) && !ProjectMatches(requestedPath, entry))
            return false;

        return true;
    }

    static bool ProjectIdMatches(string requested, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return false;

        return string.Equals(NormalizeProjectId(requested), NormalizeProjectId(actual), StringComparison.OrdinalIgnoreCase);
    }

    static bool ProjectMatches(string requested, DiscoveryEntry entry) =>
        ProjectMatches(requested, entry.ProjectRoot) ||
        ProjectMatches(requested, entry.ProjectPath);

    static bool ProjectMatches(string requested, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return false;

        var req = NormalizeProjectRoot(requested);
        var act = NormalizeProjectRoot(actual);
        if (string.Equals(req, act, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    static int WorkspaceRank(RelayOptions options, DiscoveryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(options.ProjectPath) && ProjectMatches(options.ProjectPath, entry))
            return 3;

        if (!string.IsNullOrWhiteSpace(options.WorkspaceRoot) && ProjectMatches(options.WorkspaceRoot, entry))
            return 2;

        if (!string.IsNullOrWhiteSpace(options.ProjectId) && ProjectIdMatches(options.ProjectId, entry.ProjectId))
            return 1;

        return 0;
    }

    static string? NormalizeProjectRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var normalized = NormalizePath(path);
        if (normalized.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^"/Assets".Length];
        return normalized.TrimEnd('/');
    }

    static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }
        catch
        {
            return path.TrimEnd('\\', '/').Replace('\\', '/');
        }
    }

    static string NormalizeProjectId(string value)
    {
        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var guid))
            return guid.ToString("N");

        var compact = trimmed.Replace("-", string.Empty);
        return compact.Length == 32 && compact.All(IsHex)
            ? compact.ToLowerInvariant()
            : trimmed;
    }

    static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    static string BuildAmbiguousConnectionMessage(RelayOptions options, IReadOnlyList<DiscoveryEntry> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Multiple live Unity UniBridge connections match this relay scope.");

        AppendRequestedScope(sb, options);
        sb.AppendLine("Use --project-path or --instance-id to select one explicitly. Refusing to attach silently.");
        AppendCandidates(sb, candidates);

        return sb.ToString().TrimEnd();
    }

    static void AppendRequestedScope(StringBuilder sb, RelayOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ProjectPath))
            sb.AppendLine($"Requested project path: {NormalizeProjectRoot(options.ProjectPath)}");

        if (!string.IsNullOrWhiteSpace(options.WorkspaceRoot))
            sb.AppendLine($"Workspace root: {NormalizeProjectRoot(options.WorkspaceRoot)}");

        if (!string.IsNullOrWhiteSpace(options.ProjectId))
            sb.AppendLine($"Requested project ID: {options.ProjectId}");

        if (options.InstanceId.HasValue)
            sb.AppendLine($"Requested Unity PID: {options.InstanceId.Value}");
    }

    static void AppendCandidates(StringBuilder sb, IReadOnlyList<DiscoveryEntry> candidates)
    {
        sb.AppendLine("Available UniBridge bridges:");

        foreach (var entry in candidates)
        {
            sb.Append("  - ");
            sb.Append(entry.ProjectName ?? "(unnamed)");
            sb.Append(" id=");
            sb.Append(ShortId(entry.ProjectId));
            sb.Append(" pid=");
            sb.Append(entry.EditorPid?.ToString() ?? "unknown");
            sb.Append(" root=");
            sb.Append(entry.ProjectRoot ?? "(missing)");
            sb.Append(" assets=");
            sb.Append(entry.ProjectPath ?? "(missing)");
            sb.Append(" file=");
            sb.Append(entry.SourceFile ?? "(unknown)");
            sb.AppendLine();
        }
    }

    static string ShortId(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return "unknown";

        var normalized = NormalizeProjectId(projectId);
        return normalized.Length <= 8 ? normalized : normalized[..8];
    }

    static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

sealed class DiscoveryEntry
{
    [JsonPropertyName("connection_type")]
    public string? ConnectionType { get; set; }

    [JsonPropertyName("connection_path")]
    public string ConnectionPath { get; set; } = "";

    [JsonPropertyName("created_date")]
    public string? CreatedDate { get; set; }

    [JsonPropertyName("project_path")]
    public string? ProjectPath { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("project_name")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("project_root")]
    public string? ProjectRoot { get; set; }

    [JsonPropertyName("protocol_version")]
    public string? ProtocolVersion { get; set; }

    [JsonPropertyName("editor_pid")]
    public int? EditorPid { get; set; }

    [JsonIgnore]
    public string SourceFile { get; set; } = "";

    [JsonIgnore]
    public DateTime LastWriteUtc { get; set; }
}
