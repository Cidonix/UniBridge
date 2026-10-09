using System.Text.Json;
using System.Text.Json.Nodes;
namespace Cidonix.UniBridge.Relay;
public static class RelayOutputContracts
{
    public static string Negotiate(string? requested) => requested is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25"
        ? requested : "2025-11-25";

    public static bool SupportsStructuredOutput(string protocol) => protocol is "2025-06-18" or "2025-11-25";

    public static JsonObject ToolResult(JsonNode? payload, bool supportsStructuredOutput, bool isError)
    {
        var body = payload is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject { ["value"] = payload?.DeepClone() };
        // Authored object content belongs to the tool. Invalid content must not be
        // forwarded as an MCP structured object or silently discard the original.
        if (body.ContainsKey("structuredContent") && body["structuredContent"] is not JsonObject)
        {
            var original = body;
            body = new JsonObject {
                ["status"] = "error", ["code"] = "RESULT_PROJECTION_FAILED",
                ["error"] = "Result delivery failed after a tool response was received.",
                ["executionEvidence"] = new JsonObject { ["handlerCompleted"] = true, ["retryOriginal"] = false,
                    ["operationOutcome"] = "serialized_result_retained" },
                ["originalResponse"] = original.DeepClone() };
            if (original["projectContext"] is JsonObject context) body["projectContext"] = context.DeepClone();
            if (original["_meta"] != null) body["_meta"] = original["_meta"]!.DeepClone();
            isError = true;
        }
        isError |= HasFailure(body);
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject {
            ["type"] = "text", ["text"] = body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) }) };
        if (supportsStructuredOutput)
        {
            result["structuredContent"] = body["structuredContent"] is JsonObject authored
                ? authored.DeepClone() : body.DeepClone();
        }
        if (body["_meta"] is JsonObject metadata) result["_meta"] = metadata.DeepClone();
        if (isError) result["isError"] = true;
        return result;
    }

    static bool HasFailure(JsonObject body)
    {
        foreach (var key in new[] { "success", "isError" })
            if (body[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && (key == "success" ? !flag : flag)) return true;
        if (body["status"] is JsonValue status && status.TryGetValue<string>(out var text) && text is "error" or "failed") return true;
        return body["structuredContent"] is JsonObject structured && HasFailure(structured);
    }

    public static JsonObject ServerInfo() => JsonNode.Parse("""
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "$defs": {
    "status": {
      "type": "object",
      "properties": {
        "relay": {
          "type": "object",
          "properties": {
            "name": {
              "type": "string"
            },
            "baseName": {
              "type": "string"
            },
            "version": {
              "type": "string"
            },
            "mode": {
              "type": "string"
            }
          },
          "required": [
            "name",
            "baseName",
            "version",
            "mode"
          ]
        },
        "unityConnected": {
          "type": "boolean"
        },
        "selectedConnection": {
          "type": [
            "string",
            "null"
          ]
        },
        "projectId": {
          "type": [
            "string",
            "null"
          ]
        },
        "projectName": {
          "type": [
            "string",
            "null"
          ]
        },
        "projectPath": {
          "type": [
            "string",
            "null"
          ]
        },
        "projectRoot": {
          "type": [
            "string",
            "null"
          ]
        },
        "editorPid": {
          "type": [
            "integer",
            "null"
          ]
        },
        "expectedProjectRoot": {
          "type": [
            "string",
            "null"
          ]
        },
        "workspaceRoot": {
          "type": [
            "string",
            "null"
          ]
        },
        "toolCount": {
          "type": "integer",
          "minimum": 0
        },
        "reconnected": {
          "type": "boolean"
        }
      },
      "required": [
        "relay",
        "unityConnected",
        "selectedConnection",
        "projectId",
        "projectName",
        "projectPath",
        "projectRoot",
        "editorPid",
        "expectedProjectRoot",
        "workspaceRoot",
        "toolCount"
      ]
    },
    "tools": {
      "type": "object",
      "properties": {
        "toolCount": {
          "type": "integer",
          "minimum": 0
        },
        "tools": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": {
                "type": "string"
              },
              "inputSchema": {
                "type": "object"
              }
            },
            "required": [
              "name",
              "inputSchema"
            ]
          }
        }
      },
      "required": [
        "toolCount",
        "tools"
      ]
    },
    "connections": {
      "type": "object",
      "properties": {
        "selectedConnection": {
          "type": [
            "string",
            "null"
          ]
        },
        "projectId": {
          "type": [
            "string",
            "null"
          ]
        },
        "projectName": {
          "type": [
            "string",
            "null"
          ]
        },
        "projectPath": {
          "type": [
            "string",
            "null"
          ]
        },
        "projectRoot": {
          "type": [
            "string",
            "null"
          ]
        },
        "editorPid": {
          "type": [
            "integer",
            "null"
          ]
        },
        "connected": {
          "type": "boolean"
        },
        "expectedProjectRoot": {
          "type": [
            "string",
            "null"
          ]
        },
        "availableConnections": {
          "type": "array",
          "items": {
            "type": "object"
          }
        }
      },
      "required": [
        "selectedConnection",
        "projectId",
        "projectName",
        "projectPath",
        "projectRoot",
        "editorPid",
        "connected",
        "expectedProjectRoot",
        "availableConnections"
      ]
    }
  },
  "anyOf": [
    {
      "$ref": "#/$defs/status"
    },
    {
      "$ref": "#/$defs/tools"
    },
    {
      "$ref": "#/$defs/connections"
    },
    {
      "properties": {
        "status": {
          "$ref": "#/$defs/status"
        },
        "connections": {
          "$ref": "#/$defs/connections"
        },
        "tools": {
          "$ref": "#/$defs/tools"
        }
      },
      "required": [
        "status",
        "connections",
        "tools"
      ]
    },
    {
      "properties": {
        "status": {
          "enum": [
            "error",
            "failed"
          ]
        },
        "error": {
          "type": "string"
        }
      },
      "required": [
        "status",
        "error"
      ]
    }
  ]
}
""")!.AsObject();
    public static JsonObject CommandStatus() => JsonNode.Parse("""
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "properties": {
    "state": {
      "enum": [
        "in_flight",
        "completed",
        "outcome_unknown",
        "request_conflict"
      ]
    },
    "reason": {
      "type": [
        "string",
        "null"
      ]
    },
    "originalRequestId": {
      "type": "string"
    },
    "response": {
      "type": [
        "object",
        "null"
      ]
    },
    "details": {
      "type": "object"
    }
  },
  "anyOf": [
    {
      "properties": {
        "state": {
          "const": "completed"
        },
        "response": {
          "type": "object"
        }
      },
      "required": [
        "state",
        "response"
      ]
    },
    {
      "properties": {
        "state": {
          "const": "in_flight"
        }
      },
      "required": [
        "state"
      ]
    },
    {
      "properties": {
        "state": {
          "enum": [
            "outcome_unknown",
            "request_conflict"
          ]
        },
        "reason": {
          "type": "string"
        }
      },
      "required": [
        "state",
        "reason"
      ]
    },
    {
      "properties": {
        "status": {
          "enum": [
            "error",
            "failed"
          ]
        },
        "error": {
          "type": "string"
        }
      },
      "required": [
        "status",
        "error"
      ]
    }
  ]
}
""")!.AsObject();
}
