using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;

namespace Cidonix.UniBridge.Relay;

static class EditorWaitRegression
{
    static readonly List<JsonObject> Results = new();
    static string? Filter;
    public static async Task<int> Main(string[] args)
    {
        Filter = args.Length > 2 ? args[2] : null;
        try { await RunAsync(); }
        catch (Exception ex) { Results.Add(new() { ["name"] = "suite_exception", ["passed"] = false, ["error"] = ex.ToString() }); }
        var failed = Results.Count(r => r["passed"]?.GetValue<bool>() != true);
        File.WriteAllText(args[0], new JsonObject
        {
            ["suite"] = "UniBridge Editor readiness outcome regression", ["createdUtc"] = DateTime.UtcNow.ToString("O"),
            ["passed"] = Results.Count - failed, ["failed"] = failed,
            ["productionSources"] = JsonNode.Parse(File.ReadAllText(args[1])),
            ["scope"] = "Verbatim production wait/checkpoint methods with fake Editor APIs; current production relay and owned fake named pipe; no real Editor or discovery data touched.",
            ["results"] = new JsonArray(Results.Select(r => r.DeepClone()).ToArray())
        }.ToJsonString(new() { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"Editor wait regression: {Results.Count - failed}/{Results.Count} passed; report: {args[0]}");
        return failed == 0 ? 0 : 1;
    }

    static async Task RunAsync()
    {
        foreach (var reason in new[] { "compiling", "updating", "playing" })
        {
            await CaseAsync("editor_after_reload_timeout_" + reason, async () =>
            {
                EditorWaitProduction.Reset();
                UnityEditor.EditorApplication.isCompiling = reason == "compiling";
                UnityEditor.EditorApplication.isUpdating = reason == "updating";
                UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode = reason == "playing";
                var result = await EditorWaitProduction.InvokeAsync("WaitForReadyAfterReload", Parameters());
                Assert(result["success"]?.Value<bool>() == false, "Editor timeout became outer success: " + result);
                Assert(result["code"]?.Value<string>()?.Contains("Timed out", StringComparison.OrdinalIgnoreCase) == true,
                    "original wait timeout code was replaced");
                Assert(EditorWaitProduction.DiagnosticsCalls == 0, "diagnostics ran after failed wait");
                return FromObject(new { diagnosticsCalls = EditorWaitProduction.DiagnosticsCalls, result });
            });
        }
        await CaseAsync("editor_timeout_preserved_when_diagnostics_would_throw", async () =>
        {
            EditorWaitProduction.Reset();
            UnityEditor.EditorApplication.isCompiling = true;
            EditorWaitProduction.ThrowDiagnostics = true;
            var result = await EditorWaitProduction.InvokeAsync("WaitForReadyAfterReload", Parameters());
            Assert(result["success"]?.Value<bool>() == false, "timeout did not return failure");
            Assert(EditorWaitProduction.DiagnosticsCalls == 0, "failed wait was masked by diagnostics exception");
            return FromObject(new { result });
        });
        await CaseAsync("editor_after_reload_ready_positive", async () =>
        {
            EditorWaitProduction.Reset();
            var result = await EditorWaitProduction.InvokeAsync("WaitForReadyAfterReload", Parameters());
            Assert(result["success"]?.Value<bool>() == true && result["data"]?["waitResult"]?["success"]?.Value<bool>() == true,
                "healthy Editor checkpoint lost success");
            Assert(EditorWaitProduction.DiagnosticsCalls == 1, "healthy checkpoint did not collect diagnostics");
            return FromObject(new { result });
        });
        await CaseAsync("editor_after_reload_eventual_ready_positive", async () =>
        {
            EditorWaitProduction.Reset();
            UnityEditor.EditorApplication.isCompiling = true;
            var release = Task.Run(async () => { await Task.Delay(55); UnityEditor.EditorApplication.isCompiling = false; });
            var parameters = Parameters(); parameters.TimeoutMs = 500;
            var result = await EditorWaitProduction.InvokeAsync("WaitForReadyAfterReload", parameters);
            await release;
            Assert(result["success"]?.Value<bool>() == true, "Editor becoming ready within the budget failed");
            return FromObject(new { result });
        });
        await CaseAsync("editor_require_not_playing_false_positive", async () =>
        {
            EditorWaitProduction.Reset();
            UnityEditor.EditorApplication.isPlaying = true;
            UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode = true;
            var parameters = Parameters(); parameters.RequireNotPlaying = false;
            var result = await EditorWaitProduction.InvokeAsync("WaitForReadyAfterReload", parameters);
            Assert(result["success"]?.Value<bool>() == true, "allowed ready Play Mode was rejected");
            return FromObject(new { result });
        });
        foreach (var waiting in new[] { true, false })
        {
            await CaseAsync("editor_reload_checkpoint_" + (waiting ? "timeout" : "no_wait_request"), async () =>
            {
                EditorWaitProduction.Reset();
                UnityEditor.EditorApplication.isCompiling = true;
                var parameters = Parameters(); parameters.WaitForCompletion = waiting;
                var result = await EditorWaitProduction.InvokeAsync("ReloadCheckpoint", parameters);
                Assert(result["success"]?.Value<bool>() == !waiting, "checkpoint masked wait result: " + result);
                Assert(UnityEditor.AssetDatabase.RefreshCount == 1, "checkpoint replayed its refresh mutation");
                if (waiting)
                    Assert(result["data"]?["waitResult"]?["success"]?.Value<bool>() == false && result["data"]?["completed"]?.Value<bool>() == false,
                        "failed checkpoint lost original wait and partial-change evidence");
                return FromObject(new { refreshCount = UnityEditor.AssetDatabase.RefreshCount, result });
            });
        }
        await CaseAsync("editor_reload_checkpoint_ready_positive", async () =>
        {
            EditorWaitProduction.Reset();
            var parameters = Parameters(); parameters.WaitForCompletion = true;
            var result = await EditorWaitProduction.InvokeAsync("ReloadCheckpoint", parameters);
            Assert(result["success"]?.Value<bool>() == true, "healthy checkpoint failed");
            Assert(UnityEditor.AssetDatabase.RefreshCount == 1, "healthy checkpoint refresh count mismatch");
            return FromObject(new { result });
        });
        foreach (var target in new[] { true, false })
        {
            foreach (var cancel in new[] { true, false })
            {
                await CaseAsync("editor_" + (target ? "play" : "stop") + "_wait_" + (cancel ? "reload_cancellation" : "timeout"), async () =>
                {
                    EditorWaitProduction.Reset();
                    UnityEditor.EditorApplication.isPlaying = !target;
                    UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode = !target;
                    PlayWaitProduction.CancelUpdate = cancel;
                    var result = await PlayWaitProduction.InvokeAsync(target);
                    Assert(result["success"]?.Value<bool>() == false, "unconfirmed play state became success: " + result);
                    return FromObject(new { result });
                });
            }
            await CaseAsync("editor_" + (target ? "play" : "stop") + "_wait_reached_positive", async () =>
            {
                EditorWaitProduction.Reset();
                UnityEditor.EditorApplication.isPlaying = target;
                UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode = target;
                PlayWaitProduction.CancelUpdate = false;
                var result = await PlayWaitProduction.InvokeAsync(target);
                Assert(result["success"]?.Value<bool>() == true, "confirmed play state lost success");
                return FromObject(new { result });
            });
        }

        foreach (var action in new[] { "WaitForReady", "WaitIdle", "WaitForReadyAfterReload" })
        {
            foreach (var reason in new[] { "compiling", "updating", "playing", "play_transition" })
            {
                await CaseAsync("legacy_" + action + "_busy_" + reason, () =>
                {
                    EditorWaitProduction.Reset();
                    UnityEditor.EditorApplication.isCompiling = reason == "compiling";
                    UnityEditor.EditorApplication.isUpdating = reason == "updating";
                    UnityEditor.EditorApplication.isPlaying = reason == "playing";
                    UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode = reason is "playing" or "play_transition";
                    var result = LegacyWaitProduction.Invoke(action, requireNotPlaying: true);
                    Assert(result["success"]?.Value<bool>() == false, "legacy busy readiness became success: " + result);
                    Assert(result["completed"]?.Value<bool>() == false && result["waitSupported"]?.Value<bool>() == false,
                        "legacy probe concealed incomplete or unsupported waiting");
                    Assert(result["timeoutConsumed"]?.Value<bool>() == false && result["waitedMs"]?.Value<int>() == 0,
                        "legacy probe claimed to wait or consume the timeout");
                    return Task.FromResult(FromObject(new { result }));
                });
            }
            foreach (var playing in new[] { true, false })
            {
                await CaseAsync("legacy_" + action + "_ready_" + (playing ? "allowed_play" : "edit"), () =>
                {
                    EditorWaitProduction.Reset();
                    UnityEditor.EditorApplication.isPlaying = playing;
                    UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode = playing;
                    var result = LegacyWaitProduction.Invoke(action, requireNotPlaying: !playing);
                    Assert(result["success"]?.Value<bool>() == true, "legacy ready probe failed");
                    return Task.FromResult(FromObject(new { result }));
                });
            }
        }
        await CaseAsync("legacy_get_state_remains_observation_when_busy", () =>
        {
            EditorWaitProduction.Reset();
            UnityEditor.EditorApplication.isCompiling = true;
            var result = LegacyWaitProduction.Invoke("GetState", requireNotPlaying: true);
            Assert(result["success"]?.Value<bool>() == true && result["ready"]?.Value<bool>() == false,
                "GetState should report a successful observation of a busy Editor");
            return Task.FromResult(FromObject(new { result }));
        });

        foreach (var kind in new[] { "compilation", "refresh", "play", "stop" })
        {
            foreach (var shape in new[] { "timeout", "tool_error", "nested_timeout", "not_ready", "missing_readiness", "malformed_success", "string_readiness", "contradictory_structured_content" })
            {
                await CaseAsync("relay_" + kind + "_" + shape + "_cannot_succeed", async () =>
                {
                    await using var fixture = await EditorWaitFaultUnity.StartAsync();
                    fixture.Respond = command =>
                    {
                        var action = Action(command);
                        if (action.StartsWith("WaitFor", StringComparison.Ordinal))
                        {
                            // A broad fallback would hide the primary error because
                            // the fallback deliberately reports a healthy Editor.
                            if (action == "WaitForReady") return Task.FromResult<JsonObject?>(HealthyWait(kind == "play"));
                            return Task.FromResult<JsonObject?>(WaitShape(shape, kind == "play"));
                        }
                        return Task.FromResult<JsonObject?>(HealthyState(kind == "play"));
                    };
                    var result = await fixture.RecoverAsync(kind, targetPlaying: kind == "play");
                    AssertFailure(result, "failed wait was reported as successful recovery");
                    var commands = fixture.Commands();
                    Assert(!commands.Any(c => Action(c) == "WaitForReady"), "failed primary wait triggered a compatibility fallback");
                    AssertNoMutation(commands);
                    return Evidence(result, commands);
                });
            }
            await CaseAsync("relay_" + kind + "_ready_positive", async () =>
            {
                await using var fixture = await EditorWaitFaultUnity.StartAsync();
                fixture.Respond = command => Task.FromResult<JsonObject?>(Action(command).StartsWith("WaitFor", StringComparison.Ordinal)
                    ? HealthyWait(kind == "play") : HealthyState(kind == "play"));
                var result = await fixture.RecoverAsync(kind, targetPlaying: kind == "play");
                Assert(result["status"]?.GetValue<string>() == "success" && result["result"]?["success"]?.GetValue<bool>() == true,
                    "confirmed healthy wait lost success: " + result);
                AssertNoMutation(fixture.Commands());
                return Evidence(result, fixture.Commands());
            });
            foreach (var wording in new[] { "unknown_action", "legacy_unsupported_action" })
            {
                await CaseAsync("relay_" + kind + "_" + wording + "_compatibility_fallback", async () =>
                {
                    await using var fixture = await EditorWaitFaultUnity.StartAsync();
                    fixture.Respond = command =>
                    {
                        var action = Action(command);
                        if (action.StartsWith("WaitFor", StringComparison.Ordinal) && action != "WaitForReady")
                        {
                            var message = wording == "unknown_action" ? "Unknown action: '" + action + "'."
                                : "Unsupported ManageEditor action '" + action + "'.";
                            return Task.FromResult<JsonObject?>(EditorWaitFaultUnity.Error(message));
                        }
                        return Task.FromResult<JsonObject?>(action == "WaitForReady" ? HealthyWait(kind == "play") : HealthyState(kind == "play"));
                    };
                    var result = await fixture.RecoverAsync(kind, targetPlaying: kind == "play");
                    Assert(result["status"]?.GetValue<string>() == "success", "supported compatibility fallback failed: " + result);
                    Assert(fixture.Commands().Count(c => Action(c) == "WaitForReady") == 1, "unknown action did not fallback exactly once");
                    AssertNoMutation(fixture.Commands());
                    return Evidence(result, fixture.Commands());
                });
            }
            await CaseAsync("relay_" + kind + "_fallback_timeout_cannot_succeed", async () =>
            {
                await using var fixture = await EditorWaitFaultUnity.StartAsync();
                fixture.Respond = command => Task.FromResult<JsonObject?>(Action(command) == "WaitForReady"
                    ? WaitShape("timeout", kind == "play") : EditorWaitFaultUnity.Error("Unknown action: '" + Action(command) + "'."));
                var result = await fixture.RecoverAsync(kind, targetPlaying: kind == "play");
                AssertFailure(result, "failed compatibility fallback became success");
                AssertNoMutation(fixture.Commands());
                return Evidence(result, fixture.Commands());
            });
        }
        foreach (var kind in new[] { "play", "stop" })
        {
            foreach (var stateShape in new[] { "wrong_target", "missing", "error", "still_transitioning" })
            {
                await CaseAsync("relay_" + kind + "_observed_state_" + stateShape, async () =>
                {
                    await using var fixture = await EditorWaitFaultUnity.StartAsync();
                    fixture.Respond = command =>
                    {
                        if (Action(command).StartsWith("WaitFor", StringComparison.Ordinal))
                            return Task.FromResult<JsonObject?>(HealthyWait(kind == "play"));
                        var response = stateShape switch
                        {
                            "wrong_target" => HealthyState(kind != "play"),
                            "missing" => EditorWaitFaultUnity.Success(new()),
                            "error" => EditorWaitFaultUnity.Error("play_state_inspection_failed"),
                            _ => HealthyState(false)
                        };
                        if (stateShape == "still_transitioning")
                            response["result"]!["data"]!["isPlayingOrWillChangePlaymode"] = true;
                        return Task.FromResult<JsonObject?>(response);
                    };
                    var result = await fixture.RecoverAsync(kind, targetPlaying: kind == "play");
                    AssertFailure(result, "unconfirmed observed Play Mode state became success");
                    AssertNoMutation(fixture.Commands());
                    return Evidence(result, fixture.Commands());
                });
            }
        }
        foreach (var kind in new[] { "compilation", "refresh", "play" })
        {
            await CaseAsync("relay_" + kind + "_real_shared_budget_expiration", async () =>
            {
                await using var fixture = await EditorWaitFaultUnity.StartAsync();
                fixture.Respond = async command =>
                {
                    if (Action(command).StartsWith("WaitFor", StringComparison.Ordinal))
                    {
                        await Task.Delay(600);
                        return HealthyWait(kind == "play");
                    }
                    return HealthyState(kind == "play");
                };
                var started = System.Diagnostics.Stopwatch.StartNew();
                var result = await fixture.RecoverAsync(kind, timeoutMs: 200, targetPlaying: kind == "play");
                var elapsedMs = started.Elapsed.TotalMilliseconds;
                AssertFailure(result, "response delivered after the shared budget became success");
                Assert(result["result"]?["code"]?.GetValue<string>() == "editor_wait_timeout", "budget expiration lost timeout category");
                Assert(elapsedMs >= 150 && elapsedMs < 500, "200ms recovery budget was not respected: " + elapsedMs);
                AssertNoMutation(fixture.Commands());
                var evidence = Evidence(result, fixture.Commands());
                evidence["actualResponseElapsedMs"] = elapsedMs;
                evidence["requestedBudgetMs"] = 200;
                evidence["lateFixtureResponseMs"] = 600;
                return evidence;
            });
        }
        foreach (var shape in new[] { "false_tool_success", "nested_timeout", "contradictory_structured_content", "nested_status_error", "outer_error" })
        {
            await CaseAsync("mcp_wire_" + shape + "_unambiguous_failure", async () =>
            {
                await using var fixture = await EditorWaitFaultUnity.StartAsync();
                fixture.Respond = command =>
                {
                    JsonObject response;
                    if (shape == "false_tool_success")
                    {
                        response = EditorWaitFaultUnity.Error("Timed out waiting for Unity editor readiness.");
                        response["status"] = "success";
                    }
                    else if (shape == "nested_status_error")
                    {
                        response = HealthyWait(false);
                        response["result"]!["data"]!["waitResult"] = new JsonObject { ["status"] = "error", ["error"] = "Timed out" };
                    }
                    else if (shape == "outer_error") response = EditorWaitFaultUnity.Error("Timed out");
                    else response = WaitShape(shape, false);
                    return Task.FromResult<JsonObject?>(response);
                };
                var wire = await fixture.CallMcpAsync();
                Assert(wire["result"]?["isError"]?.GetValue<bool>() == true, "MCP did not flag unsuccessful waiting as a tool error: " + wire);
                var body = JsonNode.Parse(wire["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
                Assert(body["success"]?.GetValue<bool>() != true && body["status"]?.GetValue<string>() != "success",
                    "MCP isError contradicted a successful top-level text payload: " + body);
                if (wire["result"]?["structuredContent"] is JsonObject structured)
                    Assert(structured["success"]?.GetValue<bool>() != true, "MCP structuredContent contradicted its error envelope");
                Assert(fixture.Commands().Count == 1, "normal MCP forwarding repeated the Editor command");
                return new() { ["wire"] = wire, ["body"] = body, ["commandCount"] = fixture.Commands().Count };
            });
        }
        await CaseAsync("mcp_wire_ready_positive", async () =>
        {
            await using var fixture = await EditorWaitFaultUnity.StartAsync();
            fixture.Respond = command => Task.FromResult<JsonObject?>(HealthyWait(false));
            var wire = await fixture.CallMcpAsync();
            var body = JsonNode.Parse(wire["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
            Assert(wire["result"]?["isError"]?.GetValue<bool>() != true && body["success"]?.GetValue<bool>() == true,
                "healthy wait lost MCP success");
            Assert(fixture.Commands().Count == 1, "healthy forwarding dispatched twice");
            return new() { ["wire"] = wire, ["body"] = body };
        });
        await CaseAsync("mcp_wire_legacy_success_without_success_boolean", async () =>
        {
            await using var fixture = await EditorWaitFaultUnity.StartAsync();
            fixture.Respond = command => Task.FromResult<JsonObject?>(new()
            {
                ["status"] = "success", ["result"] = new JsonObject { ["ready"] = true, ["message"] = "Legacy observation captured." }
            });
            var wire = await fixture.CallMcpAsync();
            var body = JsonNode.Parse(wire["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
            Assert(wire["result"]?["isError"]?.GetValue<bool>() != true && body["ready"]?.GetValue<bool>() == true,
                "ordinary legacy transport success was unnecessarily rejected");
            return new() { ["wire"] = wire, ["body"] = body };
        });
        await CaseAsync("mcp_wire_optional_batch_failed_step_preserves_outer_success", async () =>
        {
            await using var fixture = await EditorWaitFaultUnity.StartAsync();
            fixture.Respond = command => Task.FromResult<JsonObject?>(EditorWaitFaultUnity.Success(new()
            {
                ["steps"] = new JsonArray(new JsonObject { ["success"] = false, ["optional"] = true, ["message"] = "Optional step failed." }),
                ["completed"] = true
            }));
            var wire = await fixture.CallMcpAsync("UniBridge_BatchActions");
            var body = JsonNode.Parse(wire["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
            Assert(wire["result"]?["isError"]?.GetValue<bool>() != true && body["success"]?.GetValue<bool>() == true &&
                body["data"]?["steps"]?[0]?["success"]?.GetValue<bool>() == false,
                "generic envelope validation changed intentional optional batch semantics");
            Assert(fixture.ToolCommandCount("UniBridge_BatchActions") == 1, "batch forwarding replayed its operation");
            return new() { ["wire"] = wire, ["body"] = body };
        });
        await CaseAsync("relay_remaining_timeout_does_not_extend_exhausted_budget", () =>
        {
            var method = typeof(McpServer).GetMethod("RemainingTimeoutMs", BindingFlags.Static | BindingFlags.NonPublic)!;
            var remaining = (int)method.Invoke(null, new object[] { DateTime.UtcNow.AddMilliseconds(-2000), 1000 })!;
            Assert(remaining <= 0, "expired shared recovery timeout received a fresh budget: " + remaining);
            return Task.FromResult(new JsonObject { ["remainingMs"] = remaining });
        });
    }

    static EditorWaitProduction.ManageEditorParams Parameters() => new() { TimeoutMs = 100, PollIntervalMs = 25, RequireNotPlaying = true };
    static JsonObject FromObject(object value) => JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(value))!.AsObject();
    static string Action(JsonObject command) => command["params"]?["Action"]?.GetValue<string>() ?? "";
    static JsonObject Readiness(bool playing, bool ready = true) => new()
    {
        ["isReady"] = ready, ["isCompiling"] = !ready, ["isUpdating"] = false, ["isPlaying"] = playing,
        ["isPlayingOrWillChangePlaymode"] = playing, ["requireNotPlaying"] = !playing
    };
    static JsonObject HealthyWait(bool playing) => EditorWaitFaultUnity.Success(new()
    {
        ["readiness"] = Readiness(playing), ["waitedMs"] = 0,
        ["waitResult"] = new JsonObject { ["success"] = true, ["data"] = new JsonObject { ["readiness"] = Readiness(playing) } }
    });
    static JsonObject HealthyState(bool playing) => EditorWaitFaultUnity.Success(new()
    {
        ["isPlaying"] = playing, ["isPlayingOrWillChangePlaymode"] = playing, ["isPaused"] = false
    });
    static JsonObject WaitShape(string shape, bool playing)
    {
        var response = HealthyWait(playing);
        switch (shape)
        {
            case "timeout": return EditorWaitFaultUnity.Error("Timed out waiting for Unity editor readiness.");
            case "tool_error": return EditorWaitFaultUnity.Error("Editor readiness handler failed.");
            case "nested_timeout": response["result"]!["data"]!["waitResult"] = new JsonObject { ["success"] = false, ["code"] = "Timed out waiting for Unity editor readiness." }; break;
            case "not_ready": response["result"]!["data"]!["readiness"] = Readiness(playing, false); break;
            case "missing_readiness": return EditorWaitFaultUnity.Success(new());
            case "malformed_success": response["result"]!["success"] = "true"; break;
            case "string_readiness": response["result"]!["data"]!["readiness"]!["isReady"] = "true"; break;
            case "contradictory_structured_content": response["result"]!["structuredContent"] = new JsonObject { ["success"] = false, ["error"] = "Timed out" }; break;
        }
        return response;
    }
    static void AssertFailure(JsonObject result, string reason) => Assert(
        result["status"]?.GetValue<string>() == "error" && result["result"]?["success"]?.GetValue<bool>() == false,
        reason + ": " + result);
    static void AssertNoMutation(List<JsonObject> commands) => Assert(
        commands.All(c => Action(c).StartsWith("WaitFor", StringComparison.Ordinal) || Action(c) is "GetCompilationDiagnostics" or "GetPlayModeState"),
        "recovery replayed an Editor mutation: " + new JsonArray(commands.Select(c => (JsonNode?)c.DeepClone()).ToArray()));
    static JsonObject Evidence(JsonObject result, List<JsonObject> commands) => new()
    {
        ["response"] = result.DeepClone(), ["manageEditorActions"] = new JsonArray(commands.Select(c => (JsonNode?)JsonValue.Create(Action(c))).ToArray())
    };
    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static async Task CaseAsync(string name, Func<Task<JsonObject>> body)
    {
        if (Filter != null && !name.Contains(Filter, StringComparison.OrdinalIgnoreCase)) return;
        var started = DateTime.UtcNow;
        try
        {
            var evidence = await body();
            Results.Add(new() { ["name"] = name, ["passed"] = true, ["elapsedMs"] = (DateTime.UtcNow - started).TotalMilliseconds, ["evidence"] = evidence });
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            Results.Add(new() { ["name"] = name, ["passed"] = false, ["elapsedMs"] = (DateTime.UtcNow - started).TotalMilliseconds, ["error"] = ex.ToString() });
            var compactError = ex.Message.Replace("\r", " ").Replace("\n", " ");
            Console.WriteLine("FAIL " + name + ": " + compactError[..Math.Min(240, compactError.Length)]);
        }
    }
}
