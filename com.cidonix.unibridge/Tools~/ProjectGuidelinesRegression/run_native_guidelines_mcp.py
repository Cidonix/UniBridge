#!/usr/bin/env python3
import argparse
import ctypes
import importlib.util
import json
import os
from pathlib import Path
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--relay", type=Path, required=True)
    parser.add_argument("--smoke-module", type=Path, default=Path(__file__).resolve().parent.parent / "McpSmokeRegression/run_mcp_smoke_regression.py")
    parser.add_argument("--validator-path", type=Path, help="Optional installed jsonschema dependency directory compatible with this Python.")
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if os.name == "nt" and not ctypes.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("Administrator token required for relay launch.")
    if args.report.exists():
        raise FileExistsError("Reports are immutable; choose a fresh report name.")
    args.project = args.project.resolve(strict=True)
    args.relay = args.relay.resolve(strict=True)
    if args.validator_path:
        sys.path.insert(0, str(args.validator_path.resolve(strict=True)))
    from jsonschema import Draft202012Validator
    spec = importlib.util.spec_from_file_location("guidelines_smoke_client", args.smoke_module)
    smoke = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(smoke)
    cases = []
    report = {"passed": False, "cases": cases, "calls": [], "project": str(args.project), "relay": str(args.relay), "administrator": os.name == "nt", "editorLaunched": False}
    fixture_file = args.project / "Library/AgentValidation/ProjectGuidelinesNative/fixture.json"
    prepared = False
    class ModernClient(smoke.McpClient):
        def initialize(self):
            negotiated = self.send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                   "clientInfo": {"name": "UniBridge Project Guidelines Regression", "version": "1.0"}})
            assert negotiated.get("result", {}).get("protocolVersion") == "2025-11-25", "Modern MCP negotiation failed"
            self.send("notifications/initialized", {}, expect_response=False)

    with ModernClient(args.relay, args.project, 120) as client:
        validator = None
        def call(name, arguments=None, negative=False):
            result = client.call_tool(name, arguments or {}, allow_error=negative)
            if name == "UniBridge_ProjectGuidelines":
                report["calls"].append({"arguments": arguments or {}, "reply": result})
                business = dict(result["data"])
                mirror = business.pop("structuredContent", None)
                structured = result["raw"].get("structuredContent")
                errors = list(validator.iter_errors(structured))
                assert structured == business and (mirror is None or mirror == structured), "Text/structured payload mismatch"
                assert not errors, "Registered output schema rejected payload: " + "; ".join(error.message for error in errors[:4])
            if negative:
                assert result["raw"].get("isError") is True, "Expected actual MCP isError"
            return result["data"]

        def menu(action, phase):
            previous = fixture_file.stat().st_mtime_ns if fixture_file.exists() else None
            call("UniBridge_ManageMenuItem", {"Action": "Execute", "MenuPath": "Tools/UniBridge/Tests/Project Guidelines/" + action})
            until = time.monotonic() + 30
            while time.monotonic() < until:
                if fixture_file.exists() and fixture_file.stat().st_mtime_ns != previous:
                    value = json.loads(fixture_file.read_text(encoding="utf-8-sig"))
                    if value["phase"] == phase:
                        return value
                time.sleep(.1)
            raise RuntimeError("Fixture did not reach fresh phase " + phase)

        def case(name, verify):
            verify()
            cases.append({"name": name, "passed": True})

        def data(action="Read", **kwargs):
            result = call("UniBridge_ProjectGuidelines", {"Action": action, **kwargs})
            assert result["success"] is True
            return result["data"]

        try:
            ping = call("UniBridge_Discover", {"Action": "Ping"})
            actual_project = Path(ping["data"]["unity"]["projectPath"]).resolve()
            assert actual_project == args.project, "MCP connected to a different project"
            report["projectIdentityVerified"] = True
            info = next(tool for tool in client.list_tools() if tool["name"] == "UniBridge_ProjectGuidelines")
            schema = info["outputSchema"]
            Draft202012Validator.check_schema(schema)
            validator = Draft202012Validator(schema)
            assert info["annotations"].get("uniBridgeExecution", {}).get("readOnly") is True, "Read-only tool policy not advertised"
            report["toolInfo"] = info
            prepared = True
            fixture = menu("Prepare", "prepared_disabled")
            case("native_unconfigured_status", lambda: expect(data("Status"), configured=False, status="unavailable"))
            case("native_unconfigured_read_error", lambda: expect(call("UniBridge_ProjectGuidelines", {"Action": "Read"}, True), code="GUIDELINES_NOT_CONFIGURED"))
            fixture = menu("Configure Asset", "asset_configured")
            def verify_full():
                result = data()
                source = result["sources"][0]
                expect(source, assetGuid=fixture["assetGuid"], text=fixture["body"], sha256=fixture["assetSha256"], sha256Scope="full", truncated=False)
                expect(result, contentRole="authored_project_guidance", complete=True)
            case("native_typed_text_asset_full_read", verify_full)
            def verify_status():
                source = data("Status")["sources"][0]
                expect(source, text=None, bytesRead=0, sha256=None, assetGuid=fixture["assetGuid"])
            case("native_status_metadata_only", verify_status)
            def verify_char():
                result = data(MaxCharsPerSource=5)
                expect(result["sources"][0], text=fixture["body"][:5], sha256=fixture["assetSha256"], sha256Scope="full", truncated=True)
                expect(result, complete=False, status="partial")
            case("native_character_cap", verify_char)
            def verify_bytes():
                source = data(MaxFileBytes=12)["sources"][0]
                expect(source, sha256Scope="captured_prefix", bytesHashed=12, bytesRead=12, truncated=True)
                assert source["sha256"] != fixture["assetSha256"]
            case("native_byte_cap_prefix_identity", verify_bytes)
            case("native_invalid_budget_is_error", lambda: expect(call("UniBridge_ProjectGuidelines", {"Action": "Read", "MaxTotalChars": 0}, True), code="INVALID_GUIDELINES_BUDGET"))
            fixture = menu("Move Asset", "asset_moved")
            def verify_moved():
                source = data()["sources"][0]
                expect(source, assetGuid=fixture["assetGuid"], relativePath=fixture["assetPath"], text=fixture["body"], sha256=fixture["assetSha256"])
            case("native_guid_identity_after_move", verify_moved)
            fixture = menu("Enable Context Document", "context_enabled")
            def verify_context():
                result = call("UniBridge_ProjectGuidelines", {"Action": "Read", "MaxCharsPerSource": 64, "MaxFileBytes": 256}, True if not (args.project / (args.project.name + "_context.md")).exists() else False)
                rows = result["data"]["sources"]
                assert any(row["kind"] == "project_context" for row in rows)
                source = next(row for row in rows if row["kind"] == "project_context")
                expect(source, relativePath=args.project.name + "_context.md")
                assert source["returnedChars"] <= 64 and source["bytesRead"] <= 256
            case("native_explicit_context_document_bounded", verify_context)
            fixture = menu("Delete Asset", "asset_deleted")
            def verify_deleted():
                status = call("UniBridge_ProjectGuidelines", {"Action": "Status"}, True)
                expect(status, code="GUIDELINES_READ_FAILED")
                metadata = status["data"]["sources"][0]
                expect(metadata, assetGuid=fixture["assetGuid"], uri=None, relativePath=None, exists=False)
                result = call("UniBridge_ProjectGuidelines", {"Action": "Read"}, True)
                expect(result, code="GUIDELINES_READ_FAILED")
                expect(result["data"]["sources"][0], assetGuid=fixture["assetGuid"], uri=None, relativePath=None)
            case("native_missing_configured_guid_truthful", verify_deleted)
        except Exception as error:
            report["error"] = type(error).__name__ + ": " + str(error)[:700]
        finally:
            if prepared:
                try:
                    fixture = menu("Finish", "cleanup_verified")
                    report["cleanupVerified"] = fixture["phase"] == "cleanup_verified"
                    report["savedConfigurationPreserved"] = fixture["configurationNotSavedByFixture"]
                except Exception as error:
                    report["cleanupError"] = type(error).__name__ + ": " + str(error)[:700]
            report["passed"] = len(cases) == 10 and "error" not in report and report.get("cleanupVerified") is True and report.get("savedConfigurationPreserved") is True
            args.report.parent.mkdir(parents=True, exist_ok=True)
            with args.report.open("x", encoding="utf-8") as stream:
                json.dump(report, stream, ensure_ascii=False, indent=2)
    print(json.dumps({"passed": report["passed"], "cases": len(cases), "report": str(args.report)}))
    return 0 if report["passed"] else 1


def expect(value, **pairs):
    for key, expected in pairs.items():
        assert value.get(key) == expected, f"{key}: expected {expected!r}, got {value.get(key)!r}"


if __name__ == "__main__":
    raise SystemExit(main())
