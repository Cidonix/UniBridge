using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Cidonix.UniBridge.Relay;

static class CommandReplayRegression
{
    static readonly List<JsonObject> Results = new();
    static string? Filter;
    static bool IncludeTimeout;

    public static async Task<int> Main(string[] args)
    {
        var report = Path.GetFullPath(args[0]);
        Filter = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        IncludeTimeout = args.Contains("--include-timeout");
        try
        {
            await RunCasesAsync();
        }
        catch (Exception ex)
        {
            Results.Add(new JsonObject { ["name"] = "suite_exception", ["passed"] = false, ["error"] = ex.ToString() });
        }
        var failed = Results.Count(r => r["passed"]?.GetValue<bool>() != true);
        var output = new JsonObject
        {
            ["suite"] = "UniBridge command replay regression", ["createdUtc"] = DateTime.UtcNow.ToString("O"),
            ["passed"] = Results.Count - failed, ["failed"] = failed,
            ["scope"] = "Current relay source and fake Unity named pipe; real Editor and discovery directories untouched.",
            ["results"] = new JsonArray(Results.Select(r => r.DeepClone()).ToArray())
        };
        File.WriteAllText(report, output.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Command replay regression: {Results.Count - failed}/{Results.Count} passed; report: {report}");
        return failed == 0 ? 0 : 1;
    }

    static async Task RunCasesAsync()
    {
        await CaseAsync("transport_success", async () =>
        {
            await using var test = await FaultUnity.StartAsync();
            var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create", ["Name"] = "test" });
            Assert(response["status"]?.GetValue<string>() == "success", "normal command failed");
            Assert(test.Commands("UniBridge_ManageGameObject").Count == 1, "normal command dispatched more than once");
            return new() { ["dispatchCount"] = 1 };
        });

        foreach (var tool in new[] { "UniBridge_ManageGameObject", "UniBridge_ExecuteCode", "Unknown_Write_Tool" })
        {
            await CaseAsync("legacy_lost_response_no_replay_" + tool, async () =>
            {
                await using var test = await FaultUnity.StartAsync();
                test.Respond = (command, ordinal) =>
                {
                    Interlocked.Increment(ref test.SideEffects);
                    return Task.FromResult<JsonObject?>(ordinal == 1 ? null : FaultUnity.Success(new()));
                };
                var response = await test.CallAsync(tool, new() { ["Action"] = "Create", ["Name"] = "non_idempotent" });
                Assert(IsUnknown(response), "lost legacy write did not expose outcome_unknown: " + response);
                Assert(test.Commands(tool).Count == 1 && test.SideEffects == 1, "lost legacy write executed twice");
                return new() { ["dispatchCount"] = test.Commands(tool).Count, ["sideEffects"] = test.SideEffects, ["response"] = response.DeepClone() };
            });
        }

        await CaseAsync("modern_completed_result_recovered_by_query", async () =>
        {
            await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
            JsonObject? first = null;
            test.Respond = (command, ordinal) =>
            {
                if (command["type"]?.GetValue<string>() == "recover_command")
                {
                    Assert(command["params"]?["originalRequestId"]?.GetValue<string>() == first?["requestId"]?.GetValue<string>(), "query did not preserve original ID");
                    Assert(command["params"]?["originalType"]?.GetValue<string>() == "UniBridge_ManageGameObject", "query original tool mismatch");
                    Assert(command["params"]?["originalParams"]?["Name"]?.GetValue<string>() == "one_created_object", "query original arguments mismatch");
                    Assert(command["params"]?["expectedSessionId"]?.GetValue<string>() == "epoch-a", "query editor epoch mismatch");
                    Assert(command["requestId"]?.GetValue<string>() != first?["requestId"]?.GetValue<string>(), "query must use its own transport ID");
                    return Task.FromResult<JsonObject?>(Recovery("completed", first!, FaultUnity.Success(new() { ["createdObjectId"] = 1234 })));
                }
                first = command.DeepClone().AsObject();
                Interlocked.Increment(ref test.SideEffects);
                return Task.FromResult<JsonObject?>(null);
            };
            var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create", ["Name"] = "one_created_object" });
            Assert(response["status"]?.GetValue<string>() == "success", "completed result was not recovered");
            Assert(response["result"]?["data"]?["createdObjectId"]?.GetValue<int>() == 1234, "recovered response changed original result");
            Assert(test.Commands("UniBridge_ManageGameObject").Count == 1 && test.SideEffects == 1, "recover query replayed mutation");
            Assert(test.Commands("recover_command").Count == 1, "completed recovery did not use one query");
            return new() { ["sideEffects"] = test.SideEffects, ["originalRequestId"] = first?["requestId"]?.DeepClone(), ["response"] = response.DeepClone() };
        });

        foreach (var state in new[] { "outcome_unknown", "request_conflict" })
        {
            await CaseAsync("modern_" + state + "_never_replays", async () =>
            {
                await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
                JsonObject? first = null;
                test.Respond = (command, ordinal) =>
                {
                    if (command["type"]?.GetValue<string>() == "recover_command")
                        return Task.FromResult<JsonObject?>(Recovery(state, first!));
                    first = command.DeepClone().AsObject();
                    Interlocked.Increment(ref test.SideEffects);
                    return Task.FromResult<JsonObject?>(null);
                };
                var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create" });
                Assert(response["status"]?.GetValue<string>() == "error", state + " incorrectly became success");
                Assert(response.ToJsonString().Contains(state, StringComparison.Ordinal), "recovery state missing in error");
                Assert(test.Commands("UniBridge_ManageGameObject").Count == 1 && test.SideEffects == 1, "uncertain mutation replayed");
                return new() { ["sideEffects"] = test.SideEffects, ["response"] = response.DeepClone() };
            });
        }

        await CaseAsync("modern_in_flight_polled_without_replay", async () =>
        {
            await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
            JsonObject? first = null;
            test.Respond = (command, ordinal) =>
            {
                if (command["type"]?.GetValue<string>() == "recover_command")
                    return Task.FromResult<JsonObject?>(Recovery(ordinal == 1 ? "in_flight" : "completed", first!,
                        ordinal == 1 ? null : FaultUnity.Success(new() { ["once"] = true })));
                first = command.DeepClone().AsObject();
                Interlocked.Increment(ref test.SideEffects);
                return Task.FromResult<JsonObject?>(null);
            };
            var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create" });
            Assert(response["status"]?.GetValue<string>() == "success", "in-flight operation did not complete through query");
            Assert(test.Commands("recover_command").Count >= 2, "in-flight status was not polled");
            Assert(test.Commands("UniBridge_ManageGameObject").Count == 1 && test.SideEffects == 1, "in-flight mutation replayed");
            return new() { ["queryCount"] = test.Commands("recover_command").Count, ["sideEffects"] = test.SideEffects };
        });

        await CaseAsync("modern_recovered_error_keeps_original_error_envelope", async () =>
        {
            await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
            JsonObject? first = null;
            test.Respond = (command, ordinal) =>
            {
                if (command["type"]?.GetValue<string>() == "recover_command")
                    return Task.FromResult<JsonObject?>(Recovery("completed", first!, new() { ["status"] = "error", ["code"] = "original_failure", ["error"] = "original handler failed after partial change" }));
                first = command.DeepClone().AsObject();
                Interlocked.Increment(ref test.SideEffects);
                return Task.FromResult<JsonObject?>(null);
            };
            var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create" });
            Assert(response["status"]?.GetValue<string>() == "error" && response["code"]?.GetValue<string>() == "original_failure", "original terminal error changed during recovery");
            Assert(test.SideEffects == 1 && test.Commands("UniBridge_ManageGameObject").Count == 1, "original failed handler was replayed");
            return new() { ["sideEffects"] = 1, ["response"] = response.DeepClone() };
        });

        await CaseAsync("lost_recovery_query_never_replays_original_write", async () =>
        {
            await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
            test.Respond = (command, ordinal) =>
            {
                if (command["type"]?.GetValue<string>() != "recover_command") Interlocked.Increment(ref test.SideEffects);
                return Task.FromResult<JsonObject?>(null);
            };
            var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create" });
            Assert(IsUnknown(response), "failed recovery query hid uncertain original outcome");
            Assert(test.SideEffects == 1 && test.Commands("UniBridge_ManageGameObject").Count == 1, "query failure replayed original write");
            return new() { ["sideEffects"] = 1, ["queryCount"] = test.Commands("recover_command").Count, ["response"] = response.DeepClone() };
        });

        await CaseAsync("manual_command_status_only_queries_original_operation", async () =>
        {
            await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
            JsonObject? first = null;
            test.Respond = (command, ordinal) =>
            {
                if (command["type"]?.GetValue<string>() == "recover_command")
                    return Task.FromResult<JsonObject?>(Recovery(ordinal == 1 ? "outcome_unknown" : "completed", first!,
                        ordinal == 1 ? null : FaultUnity.Success(new() { ["finishedLater"] = true })));
                first = command.DeepClone().AsObject();
                Interlocked.Increment(ref test.SideEffects);
                return Task.FromResult<JsonObject?>(null);
            };
            var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create" });
            Assert(IsUnknown(response), "fixture did not expose original unknown outcome");
            var suggested = response["nextSuggestedCall"]?["arguments"] as JsonObject;
            Assert(suggested != null, "unknown outcome did not provide query-only next call");
            var status = await test.CommandStatusAsync(suggested!);
            Assert(status["state"]?.GetValue<string>() == "completed", "manual status lookup lost completed response");
            Assert(test.SideEffects == 1 && test.Commands("UniBridge_ManageGameObject").Count == 1, "manual status lookup replayed original write");
            return new() { ["sideEffects"] = 1, ["queryCount"] = test.Commands("recover_command").Count, ["manualStatus"] = status.DeepClone() };
        });

        foreach (var policy in new[] { "ReadOnly", "Observer" })
        {
            await CaseAsync("declared_" + policy + "_retry_reuses_original_id", async () =>
            {
                await using var test = await FaultUnity.StartAsync(DeclaredToolHandshake("UniBridge_Discover", policy));
                test.Respond = (command, ordinal) => Task.FromResult<JsonObject?>(ordinal == 1 ? null : FaultUnity.Success(new() { ["readValue"] = 7 }));
                var response = await test.CallAsync("UniBridge_Discover", new() { ["Action"] = "Ping" });
                var commands = test.Commands("UniBridge_Discover");
                Assert(response["status"]?.GetValue<string>() == "success", "declared read retry failed");
                Assert(commands.Count == 2, "read did not retry exactly once");
                Assert(commands[0]["requestId"]?.GetValue<string>() == commands[1]["requestId"]?.GetValue<string>(), "read retry allocated a new logical ID");
                return new() { ["dispatchCount"] = commands.Count, ["requestId"] = commands[0]["requestId"]?.DeepClone() };
            });
        }

        foreach (var scenario in new[]
        {
            (Name: "missing_certificate_read_only_hint", Tool: "UniBridge_Discover", Action: "Ping", Certificate: (bool?)null),
            (Name: "false_certificate_read_only_hint", Tool: "UniBridge_Discover", Action: "Ping", Certificate: (bool?)false),
            (Name: "mixed_read_console_clear", Tool: "UniBridge_ReadConsole", Action: "Clear", Certificate: (bool?)false),
            (Name: "mixed_editor_events_mark_session", Tool: "UniBridge_EditorEvents", Action: "MarkSession", Certificate: (bool?)false)
        })
        {
            await CaseAsync("uncertified_" + scenario.Name + "_never_replays", async () =>
            {
                var handshake = DeclaredToolHandshake(scenario.Tool, "ReadOnly", scenario.Certificate);
                handshake["tools"]![0]!["annotations"]!["readOnlyHint"] = true;
                await using var test = await FaultUnity.StartAsync(handshake);
                test.Respond = (command, ordinal) =>
                {
                    Interlocked.Increment(ref test.SideEffects);
                    return Task.FromResult<JsonObject?>(ordinal == 1 ? null : FaultUnity.Success(new()));
                };
                var response = await test.CallAsync(scenario.Tool, new() { ["Action"] = scenario.Action });
                Assert(IsUnknown(response), "uncertified policy/hint granted replay permission");
                Assert(test.SideEffects == 1 && test.Commands(scenario.Tool).Count == 1, "uncertified or mixed tool replayed");
                return new() { ["sideEffects"] = 1, ["replayCertificate"] = scenario.Certificate, ["action"] = scenario.Action, ["response"] = response.DeepClone() };
            });
        }

        await CaseAsync("undeclared_read_is_conservatively_not_replayed", async () =>
        {
            await using var test = await FaultUnity.StartAsync();
            test.Respond = (command, ordinal) => Task.FromResult<JsonObject?>(null);
            var response = await test.CallAsync("UniBridge_ReadConsole", new() { ["Action"] = "Get" });
            Assert(IsUnknown(response), "undeclared read was silently retried without policy evidence");
            Assert(test.Commands("UniBridge_ReadConsole").Count == 1, "undeclared read was replayed");
            return new() { ["dispatchCount"] = 1, ["response"] = response.DeepClone() };
        });

        await CaseAsync("mutable_tool_read_like_action_is_not_replayed", async () =>
        {
            await using var test = await FaultUnity.StartAsync(DeclaredToolHandshake("UniBridge_ManageGameObject", "Write"));
            test.Respond = (command, ordinal) => Task.FromResult<JsonObject?>(null);
            var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Inspect" });
            Assert(IsUnknown(response), "read-like action on mutable tool acquired unsafe replay permission");
            Assert(test.Commands("UniBridge_ManageGameObject").Count == 1, "mutable tool replayed");
            return new() { ["dispatchCount"] = 1, ["response"] = response.DeepClone() };
        });

        await CaseAsync("reload_epoch_change_never_replays_original_mutation", async () =>
        {
            await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
            JsonObject? first = null;
            test.Respond = (command, ordinal) =>
            {
                if (command["type"]?.GetValue<string>() == "recover_command")
                    return Task.FromResult<JsonObject?>(Recovery("outcome_unknown", first!));
                first = command.DeepClone().AsObject();
                Interlocked.Increment(ref test.SideEffects);
                test.HandshakeExtension = ModernHandshake("epoch-b");
                return Task.FromResult<JsonObject?>(null);
            };
            var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create" });
            Assert(IsUnknown(response), "changed editor epoch incorrectly reported known success");
            Assert(test.SideEffects == 1 && test.Commands("UniBridge_ManageGameObject").Count == 1, "epoch change replayed mutation");
            return new() { ["sideEffects"] = test.SideEffects, ["response"] = response.DeepClone() };
        });

        await CaseAsync("declared_read_editor_epoch_change_is_blocked", async () =>
        {
            var handshake = ModernHandshake("epoch-a");
            handshake["tools"] = DeclaredToolHandshake("UniBridge_Discover", "ReadOnly")["tools"]!.DeepClone();
            await using var test = await FaultUnity.StartAsync(handshake);
            test.Respond = (command, ordinal) =>
            {
                if (command["type"]?.GetValue<string>() == "UniBridge_Discover")
                {
                    var replacement = ModernHandshake("epoch-b");
                    replacement["tools"] = DeclaredToolHandshake("UniBridge_Discover", "ReadOnly")["tools"]!.DeepClone();
                    test.HandshakeExtension = replacement;
                    return Task.FromResult<JsonObject?>(ordinal == 1 ? null : FaultUnity.Success(new()));
                }
                return Task.FromResult<JsonObject?>(FaultUnity.Success(new()));
            };
            var response = await test.CallAsync("UniBridge_Discover", new() { ["Action"] = "Ping" });
            Assert(IsUnknown(response), "read retried against another editor epoch");
            Assert(test.Commands("UniBridge_Discover").Count == 1, "read scope changed during automatic retry");
            return new() { ["dispatchCount"] = 1, ["response"] = response.DeepClone() };
        });

        foreach (var policy in new[] { "ReadOnly", "Write" })
        {
            await CaseAsync("editor_pid_change_blocks_" + policy + "_retry", async () =>
            {
                await using var test = await FaultUnity.StartAsync(DeclaredToolHandshake("UniBridge_ManageGameObject", policy));
                test.Respond = (command, ordinal) =>
                {
                    Interlocked.Increment(ref test.SideEffects);
                    test.ChangeEditorPid(Environment.ProcessId);
                    return Task.FromResult<JsonObject?>(ordinal == 1 ? null : FaultUnity.Success(new()));
                };
                var response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Inspect" });
                Assert(IsUnknown(response), "changed Editor PID did not block retry");
                Assert(test.Commands("UniBridge_ManageGameObject").Count == 1 && test.SideEffects == 1, "command reran after Editor PID changed");
                return new() { ["dispatchCount"] = 1, ["response"] = response.DeepClone() };
            });
        }

        await CaseAsync("simultaneous_lost_mutations_recover_independently_without_replay", async () =>
        {
            await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
            var committed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstById = new ConcurrentDictionary<string, JsonObject>();
            test.Respond = async (command, ordinal) =>
            {
                var type = command["type"]?.GetValue<string>();
                if (type == "recover_command")
                {
                    var id = command["params"]?["originalRequestId"]!.GetValue<string>()!;
                    Assert(firstById.TryGetValue(id, out var original), "concurrent recovery used another operation identity");
                    return Recovery("completed", original!, FaultUnity.Success(new() { ["createdName"] = original!["params"]?["Name"]!.DeepClone() }));
                }
                var originalId = command["requestId"]!.GetValue<string>();
                Assert(firstById.TryAdd(originalId, command.DeepClone().AsObject()), "original mutation request replayed");
                if (Interlocked.Increment(ref test.SideEffects) == 2) committed.TrySetResult(true);
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return null;
            };
            var calls = new[]
            {
                test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create", ["Name"] = "first" }),
                test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create", ["Name"] = "second" })
            };
            var responses = await Task.WhenAll(calls);
            Assert(responses.All(r => r["status"]?.GetValue<string>() == "success"), "concurrent recovery did not return both terminal results: " + string.Join(" | ", responses.Select(r => r.ToJsonString())));
            var names = responses.Select(r => r["result"]?["data"]?["createdName"]?.GetValue<string>()).Order().ToArray();
            Assert(names.SequenceEqual(new[] { "first", "second" }), "concurrent operation results crossed");
            Assert(test.SideEffects == 2 && test.Commands("UniBridge_ManageGameObject").Count == 2, "concurrent mutations were replayed");
            return new() { ["sideEffects"] = test.SideEffects, ["distinctOperations"] = firstById.Count, ["queryCount"] = test.Commands("recover_command").Count };
        });

        await CaseAsync("lost_batch_compilation_never_replays_or_reports_complete", async () =>
        {
            await using var test = await FaultUnity.StartAsync();
            test.Respond = (command, ordinal) =>
            {
                if (command["type"]?.GetValue<string>() == "UniBridge_BatchActions")
                {
                    Interlocked.Increment(ref test.SideEffects);
                    return Task.FromResult<JsonObject?>(null);
                }
                return Task.FromResult<JsonObject?>(FaultUnity.Success(new() { ["ready"] = true, ["isPlaying"] = false }));
            };
            var response = await test.CallAsync("UniBridge_BatchActions", new()
            {
                ["Steps"] = new JsonArray(
                    new JsonObject { ["Tool"] = "UniBridge_ManageGameObject", ["Parameters"] = new JsonObject { ["Action"] = "Create" } },
                    new JsonObject { ["Tool"] = "UniBridge_ManageEditor", ["Parameters"] = new JsonObject { ["Action"] = "RequestScriptCompilation", ["TimeoutMs"] = 5000 } },
                    new JsonObject { ["Tool"] = "UniBridge_ManageGameObject", ["Parameters"] = new JsonObject { ["Action"] = "Create" } })
            });
            Assert(response["status"]?.GetValue<string>() == "error", "interrupted batch incorrectly reported all steps completed");
            Assert(test.Commands("UniBridge_BatchActions").Count == 1 && test.SideEffects == 1, "interrupted batch replayed");
            return new() { ["batchDispatchCount"] = 1, ["response"] = response.DeepClone() };
        });

        await CaseAsync("cancellation_after_side_effect_never_replays", async () =>
        {
            await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
            using var cancel = new CancellationTokenSource();
            test.Respond = (command, ordinal) =>
            {
                Interlocked.Increment(ref test.SideEffects);
                cancel.Cancel();
                return Task.FromResult<JsonObject?>(null);
            };
            try { await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create" }, cancel.Token); }
            catch (OperationCanceledException) { }
            Assert(test.SideEffects == 1 && test.Commands("UniBridge_ManageGameObject").Count == 1, "cancelled mutation replayed");
            return new() { ["sideEffects"] = test.SideEffects, ["dispatchCount"] = 1 };
        });
        if (IncludeTimeout)
        {
            await CaseAsync("real_response_timeout_never_replays_mutation", async () =>
            {
                await using var test = await FaultUnity.StartAsync(ModernHandshake("epoch-a"));
                var release = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
                test.Respond = (command, ordinal) =>
                {
                    Interlocked.Increment(ref test.SideEffects);
                    return release.Task;
                };
                JsonObject response;
                var started = DateTime.UtcNow;
                try { response = await test.CallAsync("UniBridge_ManageGameObject", new() { ["Action"] = "Create" }, wait: TimeSpan.FromSeconds(130)); }
                finally { release.TrySetResult(null); }
                Assert(IsUnknown(response) && response["reason"]?.GetValue<string>() == "response_timeout", "real timeout did not expose uncertain outcome");
                Assert(test.SideEffects == 1 && test.Commands("UniBridge_ManageGameObject").Count == 1, "timeout replayed mutation");
                return new() { ["sideEffects"] = 1, ["elapsedSeconds"] = (DateTime.UtcNow - started).TotalSeconds, ["response"] = response.DeepClone() };
            });
        }
#if JOURNAL_TESTS
        await JournalRegression.RunAsync();
#endif
    }

    static bool IsUnknown(JsonObject response) => response["status"]?.GetValue<string>() == "error" && response.ToJsonString().Contains("outcome_unknown", StringComparison.Ordinal);

    static JsonObject ModernHandshake(string session) => new()
    {
        ["commandRecovery"] = new JsonObject { ["version"] = 1, ["mode"] = "query-only", ["sessionId"] = session, ["retentionSeconds"] = 600 }
    };

    static JsonObject DeclaredToolHandshake(string tool, string policy, bool? replaySafe = true) => new()
    {
        ["tools"] = new JsonArray(new JsonObject { ["name"] = tool, ["annotations"] = new JsonObject { ["uniBridgeExecution"] = new JsonObject { ["policy"] = policy, ["replaySafe"] = replaySafe } } })
    };

    static JsonObject Recovery(string state, JsonObject first, JsonObject? response = null) => new()
    {
        ["status"] = "success", ["result"] = new JsonObject
        {
            ["state"] = state, ["originalRequestId"] = first["requestId"]?.DeepClone(),
            ["response"] = response?.DeepClone(), ["reason"] = "deterministic fault fixture"
        }
    };

    internal static async Task CaseAsync(string name, Func<Task<JsonObject>> body)
    {
        if (Filter != null && !name.Contains(Filter, StringComparison.OrdinalIgnoreCase)) return;
        var started = DateTime.UtcNow;
        try
        {
            var evidence = await body();
            Results.Add(new() { ["name"] = name, ["passed"] = true, ["elapsedMs"] = (DateTime.UtcNow - started).TotalMilliseconds, ["evidence"] = evidence });
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            Results.Add(new() { ["name"] = name, ["passed"] = false, ["elapsedMs"] = (DateTime.UtcNow - started).TotalMilliseconds, ["error"] = ex.ToString() });
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    internal static void Assert(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }

    sealed class FaultUnity : IAsyncDisposable
    {
        readonly string root;
        readonly string pipeName;
        readonly JsonObject discoveryEntry;
        readonly string? oldStatusDirectory;
        readonly CancellationTokenSource lifetime = new();
        readonly ConcurrentBag<NamedPipeServerStream> streams = new();
        readonly ConcurrentBag<Task> handlers = new();
        readonly List<JsonObject> received = new();
        readonly TaskCompletionSource<bool> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly McpServer relay;
        Task? listener;
        public Func<JsonObject, int, Task<JsonObject?>>? Respond { get; set; }
        public JsonObject? HandshakeExtension { get; set; }
        public int SideEffects;

        FaultUnity()
        {
            root = Path.Combine(Path.GetTempPath(), "unibridge-command-replay-fixture-" + Guid.NewGuid().ToString("N"));
            pipeName = "unibridge-command-replay-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(root);
            oldStatusDirectory = Environment.GetEnvironmentVariable("UNIBRIDGE_MCP_STATUS_DIR");
            Environment.SetEnvironmentVariable("UNIBRIDGE_MCP_STATUS_DIR", root);
            var projectId = Guid.NewGuid().ToString("N");
            discoveryEntry = new JsonObject
            {
                ["connection_type"] = "named_pipe", ["connection_path"] = @"\\.\pipe\" + pipeName,
                ["project_id"] = projectId, ["project_name"] = "CommandReplayFixture", ["project_root"] = root,
                ["project_path"] = Path.Combine(root, "Assets")
            };
            File.WriteAllText(Path.Combine(root, "bridge-test.json"), discoveryEntry.ToJsonString());
            relay = new McpServer(RelayOptions.Parse(new[] { "--mcp", "--project-id", projectId, "--project-path", root }), new Logger(false));
        }

        public static async Task<FaultUnity> StartAsync(JsonObject? handshakeExtension = null)
        {
            var fixture = new FaultUnity { HandshakeExtension = handshakeExtension };
            fixture.listener = fixture.ListenAsync();
            await fixture.listening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return fixture;
        }

        public async Task<JsonObject> CallAsync(string tool, JsonObject arguments, CancellationToken ct = default, TimeSpan? wait = null)
        {
            var method = typeof(McpServer).GetMethod("SendUnityCommandWithReconnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Task<JsonObject> task;
            try { task = (Task<JsonObject>)method.Invoke(relay, new object[] { tool, arguments, ct })!; }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            return await task.WaitAsync(wait ?? TimeSpan.FromSeconds(20));
        }

        public async Task<JsonObject> CommandStatusAsync(JsonObject arguments)
        {
            var method = typeof(McpServer).GetMethod("GetCommandStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task<JsonObject>)method.Invoke(relay, new object[] { arguments, CancellationToken.None })!;
            return await task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        public void ChangeEditorPid(int pid)
        {
            discoveryEntry["editor_pid"] = pid;
            File.WriteAllText(Path.Combine(root, "bridge-test.json"), discoveryEntry.ToJsonString());
        }

        public List<JsonObject> Commands(string tool)
        {
            lock (received) return received.Where(r => r["type"]?.GetValue<string>() == tool).Select(r => r.DeepClone().AsObject()).ToList();
        }

        async Task ListenAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    streams.Add(pipe);
                    listening.TrySetResult(true);
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    handlers.Add(HandleAsync(pipe));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
        }

        async Task HandleAsync(NamedPipeServerStream pipe)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            var responseWrites = new SemaphoreSlim(1, 1);
            var pendingResponses = new List<Task>();
            var handshake = new JsonObject { ["protocol"] = "unity-mcp", ["version"] = "1.0", ["toolsHash"] = "test", ["tools"] = new JsonArray() };
            if (HandshakeExtension != null)
                foreach (var entry in HandshakeExtension) handshake[entry.Key] = entry.Value?.DeepClone();
            try
            {
                await writer.WriteLineAsync(handshake.ToJsonString());
                while (!lifetime.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(lifetime.Token);
                    if (line == null) break;
                    var command = JsonNode.Parse(line)!.AsObject();
                    int ordinal;
                    lock (received)
                    {
                        received.Add(command.DeepClone().AsObject());
                        ordinal = received.Count(r => r["type"]?.GetValue<string>() == command["type"]?.GetValue<string>());
                    }
                    pendingResponses.Add(RespondAsync(command, ordinal, pipe, writer, responseWrites));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
            finally
            {
                await Task.WhenAll(pendingResponses);
                responseWrites.Dispose();
                try { writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
                try { reader.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
                pipe.Dispose();
            }
        }

        async Task RespondAsync(JsonObject command, int ordinal, NamedPipeServerStream pipe, StreamWriter writer, SemaphoreSlim responseWrites)
        {
            try
            {
                var type = command["type"]?.GetValue<string>();
                JsonObject? response;
                if (type == "set_client_info" || type == "get_available_tools")
                    response = Success(new() { ["unchanged"] = true });
                else if (Respond != null)
                    response = await Respond(command, ordinal);
                else
                    response = Success(new() { ["dispatchCount"] = ordinal });
                if (response == null) { pipe.Dispose(); return; }
                response["requestId"] = command["requestId"]?.DeepClone();
                await responseWrites.WaitAsync(lifetime.Token);
                try { await writer.WriteLineAsync(response.ToJsonString()); }
                finally { responseWrites.Release(); }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
        }

        public static JsonObject Success(JsonObject data) => new() { ["status"] = "success", ["result"] = new JsonObject { ["success"] = true, ["data"] = data } };

        public async ValueTask DisposeAsync()
        {
            await relay.DisposeAsync();
            lifetime.Cancel();
            foreach (var stream in streams) stream.Dispose();
            if (listener != null) await listener;
            await Task.WhenAll(handlers);
            lifetime.Dispose();
            Environment.SetEnvironmentVariable("UNIBRIDGE_MCP_STATUS_DIR", oldStatusDirectory);
            // Every file under this unique fixture directory was created by this
            // harness. Validate its absolute ownership before recursive cleanup.
            var resolved = Path.GetFullPath(root);
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("unibridge-command-replay-fixture-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing fixture cleanup outside the task-owned temporary directory.");
            Directory.Delete(resolved, true);
        }
    }
}
