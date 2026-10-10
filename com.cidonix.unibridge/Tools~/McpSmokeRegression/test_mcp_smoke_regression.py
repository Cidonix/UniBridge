"""Mock-only contracts for the MCP smoke runner; no Unity or relay launches."""
import argparse
import importlib.util
import io
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('smoke_contract_target', Path(__file__).with_name('run_mcp_smoke_regression.py'))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class RunnerContractTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="unibridge-smoke-contract-")
        self.addCleanup(temporary.cleanup)
        self.scratch = Path(temporary.name)

    def client(self):
        return smoke.McpClient('relay', 'project', 1)

    def suite(self):
        args = SimpleNamespace(project_path=str(self.scratch), relay_path='relay', report_path=str(self.scratch / 'unused-report.json'), reload_timeout_seconds=180)
        return smoke.SmokeSuite(args)

    def test_unexpected_tool_error_still_rejected(self):
        client = self.client()
        client.send = lambda *args, **kwargs: {'result': {'isError': True, 'content': [{'type': 'text', 'text': '{"success":false}'}]}}
        with self.assertRaises(RuntimeError):
            client.call_tool('example')

    def test_expected_error_remains_available_for_assertion(self):
        client = self.client()
        client.send = lambda *args, **kwargs: {'result': {'isError': True, 'content': [{'type': 'text', 'text': '{"success":false,"message":"stale_file"}'}]}}
        result = client.call_tool('example', allow_error=True)
        self.assertEqual(result['data'], {'success': False, 'message': 'stale_file'})
        self.assertIs(result['raw']['isError'], True)

    def test_expected_error_flag_does_not_forge_failure(self):
        client = self.client()
        client.send = lambda *args, **kwargs: {'result': {'content': [{'type': 'text', 'text': '{"success":true}'}]}}
        self.assertIs(client.call_tool('example', allow_error=True)['data']['success'], True)

    def test_integer_and_string_response_ids_are_distinct(self):
        client = self.client()
        client.stdout_queue.put(json.dumps({'id': '7', 'result': 'string'}))
        client.stdout_queue.put(json.dumps({'id': 7, 'result': 'integer'}))
        self.assertEqual(client.wait_for_response(7, 1)['result'], 'integer')
        self.assertEqual(client.wait_for_response('7', 1)['result'], 'string')

    def test_out_of_order_response_preserved(self):
        client = self.client()
        client.stdout_queue.put(json.dumps({'id': 2, 'result': 'second'}))
        client.stdout_queue.put(json.dumps({'id': 1, 'result': 'first'}))
        self.assertEqual(client.wait_for_response(1, 1)['result'], 'first')
        self.assertEqual(client.wait_for_response(2, 1)['result'], 'second')

    def test_protocol_error_rejected_unless_expected(self):
        response = {'id': None, 'error': {'code': -32600, 'message': 'invalid'}}
        with self.assertRaises(RuntimeError):
            smoke.McpClient.check_response(response)
        self.assertEqual(smoke.McpClient.check_response(response, allow_error=True), response)

    def test_explicit_cancel_wire_shape(self):
        client = self.client()
        stream = io.StringIO()
        client.process = SimpleNamespace(stdin=stream)
        client.cancel('request-7')
        message = json.loads(stream.getvalue())
        self.assertEqual(message['method'], 'notifications/cancelled')
        self.assertEqual(message['params']['requestId'], 'request-7')
        self.assertNotIn('id', message)

    def test_cancel_suppressed_response_assertion_rejects_response(self):
        client = self.client()
        client.stdout_queue.put(json.dumps({'id': 7, 'result': {}}))
        with self.assertRaises(AssertionError):
            client.assert_no_response(7, .01)

    def test_started_admission_requires_new_running_operation(self):
        suite = self.suite()
        suite.scheduler_snapshot = lambda observer: {'activeOperations': [{'tool': 'probe', 'operationId': 8, 'startedUtc': 'now', 'outcome': 'running'}]}
        result = suite.await_operation_admission(None, 'probe', {7})
        self.assertEqual(result['operationId'], 8)

    def test_queued_admission_refuses_started_write(self):
        suite = self.suite()
        suite.scheduler_snapshot = lambda observer: {'activeOperations': [{'tool': 'write', 'operationId': 8, 'startedUtc': 'now', 'outcome': 'running'}]}
        with self.assertRaises(AssertionError):
            suite.await_operation_admission(None, 'write', set(), queued=True)

    def test_exact_canceled_record_releases_without_recovery(self):
        suite = self.suite()
        suite.scheduler_snapshot = lambda observer: {'activeOperations': [], 'activeReaders': 0, 'activeExclusive': None, 'pending': {'readers': 0, 'exclusive': 0}, 'recentOperations': [{'operationId': 8, 'outcome': 'canceled', 'forcedRelease': None}]}
        result, _ = suite.await_canceled_operation(None, 8)
        self.assertEqual(result['operationId'], 8)

    def test_success_history_cannot_be_counted_as_canceled(self):
        suite = self.suite()
        suite.scheduler_snapshot = lambda observer: {'activeOperations': [], 'recentOperations': [{'operationId': 8, 'outcome': 'success'}]}
        with self.assertRaises(AssertionError):
            suite.await_canceled_operation(None, 8)

    def test_forced_recovery_cannot_be_counted_as_clean_cancel(self):
        suite = self.suite()
        suite.scheduler_snapshot = lambda observer: {'activeOperations': [], 'recentOperations': [{'operationId': 8, 'outcome': 'canceled', 'forcedRelease': {'reason': 'manual reap'}}]}
        with self.assertRaises(AssertionError):
            suite.await_canceled_operation(None, 8)

    def test_pending_write_after_cancel_is_failure(self):
        suite = self.suite()
        suite.scheduler_snapshot = lambda observer: {'activeOperations': [], 'activeReaders': 0, 'activeExclusive': None, 'pending': {'readers': 0, 'exclusive': 1}, 'recentOperations': [{'operationId': 8, 'outcome': 'canceled', 'forcedRelease': None}]}
        with self.assertRaises(AssertionError):
            suite.await_canceled_operation(None, 8)

    def test_health_requires_positive_flags(self):
        with self.assertRaises(AssertionError):
            smoke.assert_compilation_healthy({'errors': 0}, require_evidence=True)

    def test_health_rejects_build_console_inspection_failure(self):
        health = {'errors': 0, 'compileHealth': {'healthy': True}, 'buildSystemHealth': {'hasCriticalIssues': False, 'criticalIssueCount': 0, 'warning': 'console could not be inspected'}}
        with self.assertRaises(AssertionError):
            smoke.assert_compilation_healthy(health, require_evidence=True)

    def test_health_rejects_unverified_assembly_freshness(self):
        health = {'errors': 0, 'compileHealth': {'healthy': True}, 'buildSystemHealth': {'hasCriticalIssues': False, 'criticalIssueCount': 0}, 'assemblyFreshness': {'available': False}}
        with self.assertRaises(AssertionError):
            smoke.assert_compilation_healthy(health, require_evidence=True)

    def lifecycle_fixture(self, recovered):
        suite = self.suite()
        calls = []
        unknown = {'code': 'outcome_unknown', 'operationId': 'original', 'nextSuggestedCall': {'tool': 'UniBridge_CommandStatus', 'arguments': {'OperationId': 'original'}}}
        def tool(name, args=None, **kwargs):
            calls.append(name)
            if name == 'UniBridge_ManageEditor':
                raise smoke.ToolCallError(name, {'raw': {'isError': True}, 'data': unknown})
            return {'data': recovered}
        suite.tool = tool
        suite.step_wait_ready_after_reload = lambda: {'ready': True, 'compileHealth': {'healthy': True}}
        return suite, calls

    def test_ready_alone_cannot_make_unknown_write_successful(self):
        suite, calls = self.lifecycle_fixture({'state': 'outcome_unknown'})
        with self.assertRaises(AssertionError):
            suite.lifecycle_command_once({'Action': 'RefreshAssets'})
        self.assertEqual(calls, ['UniBridge_ManageEditor', 'UniBridge_CommandStatus'])

    def test_original_completed_success_recovered_without_write_replay(self):
        suite, calls = self.lifecycle_fixture({'state': 'completed', 'response': {'status': 'success', 'result': {'success': True, 'data': {'ready': True}}}})
        result = suite.lifecycle_command_once({'Action': 'RefreshAssets'})
        self.assertTrue(result['originalOutcomeRecovered'])
        self.assertFalse(result['automaticReplay'])
        self.assertEqual(calls, ['UniBridge_ManageEditor', 'UniBridge_CommandStatus'])

    def test_completed_failure_not_counted_as_boundary_success(self):
        suite, calls = self.lifecycle_fixture({'state': 'completed', 'response': {'status': 'success', 'result': {'success': False}}})
        with self.assertRaises(AssertionError):
            suite.lifecycle_command_once({'Action': 'RefreshAssets'})
        self.assertEqual(calls.count('UniBridge_ManageEditor'), 1)

    def test_owned_script_cleanup_waits_before_single_delete(self):
        suite = self.suite()
        calls = []
        def tool(name, args=None, **kwargs):
            calls.append((name, (args or {}).get('Action')))
            return {'data': {'ready': True}} if name == 'UniBridge_ManageEditor' else {'envelope': {'success': True}, 'data': {}}
        suite.tool = tool
        suite.cleanup_owned_script('Assets/Owned.cs')
        self.assertEqual(calls, [('UniBridge_ManageEditor', 'WaitForReady'), ('UniBridge_DeleteScript', None)])

    def test_owned_cleanup_does_not_delete_when_readiness_false(self):
        suite = self.suite()
        calls = []
        def tool(name, args=None, **kwargs):
            calls.append(name)
            return {'data': {'ready': False}}
        suite.tool = tool
        with self.assertRaises(AssertionError):
            suite.cleanup_owned_script('Assets/Owned.cs')
        self.assertEqual(calls, ['UniBridge_ManageEditor'])

    def test_cleanup_failure_preserves_original_failure(self):
        suite = self.suite()
        def tool(name, args=None, **kwargs):
            if name == 'UniBridge_ManageEditor':
                return {'data': {'ready': True}}
            raise RuntimeError('cleanup lost connection')
        suite.tool = tool
        try:
            raise AssertionError('original assertion failed')
        except AssertionError as original:
            suite.cleanup_owned_script('Assets/Owned.cs')
            self.assertEqual(str(original), 'original assertion failed')
            self.assertIn('cleanup lost connection', original.smoke_cleanup_errors[0])

    def test_cleanup_failure_without_original_is_not_hidden(self):
        suite = self.suite()
        suite.tool = lambda *args, **kwargs: {'data': {'ready': True}, 'envelope': {'success': False}}
        with self.assertRaises(AssertionError):
            suite.cleanup_owned_script('Assets/Owned.cs')

    def test_refresh_request_and_verified_state_do_not_claim_inline_completion(self):
        suite = self.suite()
        requests = []
        suite.lifecycle_command_once = lambda args: requests.append(args) or {'forced': True}
        suite.step_wait_ready_after_reload = lambda: {'ready': True, 'compileHealth': {'healthy': True}, 'freshConsoleTotals': {'errorCount': 0}}
        result = suite.step_refresh_assets()
        self.assertEqual(len(requests), 1)
        self.assertIs(requests[0]['WaitForCompletion'], False)
        self.assertIs(result['refreshRequestAdmitted'], True)
        self.assertIs(result['verifiedFinalState'], True)
        self.assertIs(result['originalCompletionReported'], False)
        self.assertIs(result['automaticReplay'], False)

    def test_ready_state_cannot_substitute_for_refresh_admission(self):
        suite = self.suite()
        suite.lifecycle_command_once = lambda args: {'ready': True}
        suite.step_wait_ready_after_reload = lambda: {'ready': True}
        with self.assertRaises(AssertionError):
            suite.step_refresh_assets()

    def test_queued_compile_does_not_claim_completion(self):
        suite = self.suite()
        requests = []
        suite.lifecycle_command_once = lambda args: requests.append(args) or {'status': 'queued', 'requestId': 'compile-1', 'reconnectRequired': True}
        result = suite.step_request_compile()
        self.assertEqual(len(requests), 1)
        self.assertIs(result['queued'], True)
        self.assertIs(result['originalCompletionReported'], False)

    def test_compile_without_queue_identity_is_failure(self):
        suite = self.suite()
        suite.lifecycle_command_once = lambda args: {'status': 'success'}
        with self.assertRaises(AssertionError):
            suite.step_request_compile()

    def test_mutation_cannot_opt_into_reload_replay(self):
        suite = self.suite()
        with self.assertRaises(ValueError):
            suite.tool('UniBridge_ScriptApplyEdits', {'Preview': False}, retry_after_reload=True)
        with self.assertRaises(ValueError):
            suite.tool('UniBridge_ManageEditor', {'Action': 'RefreshAssets'}, retry_after_reload=True)

    def test_successful_script_write_waits_before_next_operation(self):
        suite = self.suite()
        waits = []
        suite.step_wait_ready_after_reload = lambda: waits.append('ready')
        suite.wait_after_successful_script_edit({'envelope': {'success': True}, 'data': {'scheduledRefresh': True}})
        self.assertEqual(waits, ['ready'])

    def test_unknown_script_write_not_made_successful_by_ready(self):
        suite = self.suite()
        waits = []
        suite.step_wait_ready_after_reload = lambda: waits.append('ready')
        with self.assertRaises(AssertionError):
            suite.wait_after_successful_script_edit({'data': {'code': 'outcome_unknown'}})
        self.assertEqual(waits, [])

    def test_pure_read_retries_only_after_actual_readiness(self):
        suite = self.suite()
        events = []
        class Client:
            def __enter__(self):
                return self
            def __exit__(self, *args):
                pass
            def call_tool(self, name, args, **kwargs):
                events.append(name)
                if events.count(name) == 1:
                    raise RuntimeError('Unity not detected. No matching UniBridge discovery file was found.')
                return {'data': {'success': True, 'data': {'text': 'verified read'}}}
        suite.make_client = lambda *args: Client()
        suite.wait_for_script_cleanup_ready = lambda: events.append('ready')
        with patch.object(smoke.time, 'sleep', return_value=None):
            result = suite.tool('UniBridge_ReadResource', {'Uri': 'Assets/Owned.cs'})
        self.assertEqual(result['data']['text'], 'verified read')
        self.assertEqual(events, ['UniBridge_ReadResource', 'ready', 'UniBridge_ReadResource'])

    def readiness_fixture(self, always_stale=False, compiler_errors=0):
        suite = self.suite()
        calls = []
        diagnostics_count = [0]
        def tool(name, args=None, **kwargs):
            action = (args or {}).get('Action')
            calls.append((name, action))
            if action == 'WaitForReadyAfterReload':
                return {'data': {'readiness': {'isReady': True}}}
            if action == 'GetCompilationDiagnostics':
                diagnostics_count[0] += 1
                stale = always_stale or diagnostics_count[0] == 1
                return {'data': {
                    'errors': compiler_errors, 'compileHealth': {'healthy': not stale},
                    'buildSystemHealth': {'hasCriticalIssues': False, 'criticalIssueCount': 0},
                    'assemblyFreshness': {'available': True, 'staleLikely': stale, 'staleAssemblyCount': int(stale), 'missingOutputAssemblyCount': 0},
                }}
            if action == 'DiagnosticSummary':
                return {'data': {'totals': {'errorCount': 0, 'exceptionCount': 0, 'assertCount': 0}}}
            raise AssertionError(f'Unexpected call during read-only readiness: {name} {args}')
        suite.tool = tool
        return suite, calls, diagnostics_count

    def test_queued_compile_ready_gap_waits_until_actual_fresh_assemblies(self):
        suite, calls, count = self.readiness_fixture()
        with patch.object(smoke.time, 'sleep', return_value=None):
            result = suite.step_wait_ready_after_reload()
        self.assertEqual(result['readinessPolls'], 2)
        self.assertEqual(result['pendingAssemblyPolls'], 1)
        self.assertEqual(count[0], 2)
        self.assertIs(result['assemblyFreshnessVerified'], True)
        self.assertTrue(all(action in ('WaitForReadyAfterReload', 'GetCompilationDiagnostics', 'DiagnosticSummary') for _, action in calls))

    def test_genuine_compiler_error_is_not_retried_as_queued_gap(self):
        suite, calls, count = self.readiness_fixture(compiler_errors=1)
        with self.assertRaisesRegex(AssertionError, 'Compilation has errors'):
            suite.step_wait_ready_after_reload()
        self.assertEqual(count[0], 1)

    def test_persistent_stale_assembly_fails_bounded_with_last_evidence(self):
        suite, calls, count = self.readiness_fixture(always_stale=True)
        suite.args.reload_timeout_seconds = .06
        clock = [0]
        def monotonic():
            clock[0] += .005
            return clock[0]
        with patch.object(smoke.time, 'monotonic', side_effect=monotonic), patch.object(smoke.time, 'sleep', return_value=None):
            with self.assertRaisesRegex(AssertionError, "Last evidence=.*staleAssemblyCount.*1"):
                suite.step_wait_ready_after_reload()
        self.assertGreater(count[0], 0)
        self.assertTrue(all(action in ('WaitForReadyAfterReload', 'GetCompilationDiagnostics', 'DiagnosticSummary') for _, action in calls))

    def test_none_write_with_stale_assemblies_uses_only_bridge_readiness(self):
        suite = self.suite()
        calls = []
        def tool(name, args=None, **kwargs):
            calls.append((name, (args or {}).get('Action')))
            return {'data': {'readiness': {'isReady': True}, 'assemblyFreshness': {'staleAssemblyCount': 1}}}
        suite.tool = tool
        suite.step_wait_ready_after_reload = lambda: self.fail('No-import writes do not request assembly compilation.')
        suite.wait_after_successful_script_edit({'envelope': {'success': True}, 'data': {'scheduledRefresh': False}}, refresh_mode='none')
        self.assertEqual(calls, [('UniBridge_ManageEditor', 'WaitForReady')])

    def test_immediate_write_requires_fresh_compilation_even_without_scheduled_refresh(self):
        suite = self.suite()
        checks = []
        suite.step_wait_ready_after_reload = lambda: checks.append('fresh compilation')
        suite.wait_for_script_cleanup_ready = lambda: self.fail('Immediate writes require the full health gate.')
        suite.wait_after_successful_script_edit({'envelope': {'success': True}, 'data': {'scheduledRefresh': False}}, refresh_mode='immediate')
        self.assertEqual(checks, ['fresh compilation'])

    def test_missing_refresh_evidence_cannot_be_treated_as_none_write(self):
        suite = self.suite()
        suite.wait_for_script_cleanup_ready = lambda: self.fail('Refresh evidence is required.')
        with self.assertRaisesRegex(AssertionError, 'omitted explicit refresh evidence'):
            suite.wait_after_successful_script_edit({'envelope': {'success': True}})


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, help="Optional fresh JSON report path; existing reports are never overwritten.")
    args = parser.parse_args(argv)
    if args.report and args.report.exists():
        parser.error(f"Report already exists: {args.report}. Choose a fresh path.")
    started = smoke.utc_now()
    tests = unittest.defaultTestLoader.loadTestsFromTestCase(RunnerContractTests)
    result = unittest.TextTestRunner(verbosity=2).run(tests)
    if args.report:
        report = {
            "suite": "UniBridge MCP Smoke Runner Contracts",
            "status": "passed" if result.wasSuccessful() else "failed",
            "startedUtc": smoke.iso(started), "finishedUtc": smoke.iso(smoke.utc_now()),
            "tests": result.testsRun, "passed": result.testsRun - len(result.errors) - len(result.failures),
            "failures": len(result.failures), "errors": len(result.errors),
            "source": str(spec.origin), "liveUnity": False, "launchedProcesses": 0,
            "failureDetails": [{"test": test.id(), "traceback": traceback} for test, traceback in result.failures + result.errors],
        }
        args.report.parent.mkdir(parents=True, exist_ok=True)
        with args.report.open("x", encoding="utf-8") as output:
            json.dump(report, output, indent=2)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
