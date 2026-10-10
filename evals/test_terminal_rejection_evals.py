"""Controls for attributed rejected model calls that resemble terminal inputs."""

import copy
import json
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

from child_run_evals import (ChildFixture, REQUIRED_RATIONALE_ERROR, canonical_pairs, child_handler,
                             committed_positions, consumed_deliveries, require_observed_rejections, terminal_pairs)
from test_child_run_evals import ACCEPTED, logs, parent, terminal
from coordination_artifact_evals import occurrences


def rejected(identifier="rejected-terminal", occurrence=1):
    request = parent(identifier=identifier)
    request["messages"][1]["content"] = REQUIRED_RATIONALE_ERROR
    row = {"id": identifier, "name": "spawn_agent", "arguments": {
        "run_id": ACCEPTED["run_id"], "source_operation": "spawn_agent"},
        "result": REQUIRED_RATIONALE_ERROR, "success": False, "failure_code": "invalid_rationale",
        "occurrence": occurrence, "turn": 2, "observed_ns": 10}
    return request, row


def joined(*requests):
    return {"messages": [copy.deepcopy(message) for request in requests for message in request["messages"]]}


class TerminalRejectionControls(unittest.TestCase):
    def test_rejected_model_call_proves_no_terminal_and_requires_typed_evidence(self):
        request, row = rejected()
        self.assertEqual({}, terminal_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", [row]))
        with self.assertRaises(AssertionError):
            canonical_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", [row])
        with self.assertRaises(json.JSONDecodeError):
            terminal_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", [])
        self.assertEqual(REQUIRED_RATIONALE_ERROR, request["messages"][1]["content"])
        self.assertIs(row["success"], False)

    def test_real_terminal_after_rejection_survives_cumulative_history(self):
        request, row = rejected()
        complete = joined(request, parent())
        self.assertEqual(("delivery-neutral", terminal()), canonical_pairs(
            [request, complete, complete], ACCEPTED, "start-neutral", "spawn_agent", [row]))

    def test_completed_model_id_reuse_matches_the_full_pair(self):
        request, row = rejected()
        prior = parent(identifier=row["id"])
        prior["messages"][0]["tool_calls"][0]["function"] = {"name": "file_read", "arguments": '{"Path":"/neutral"}'}
        prior["messages"][1]["content"] = "A prior unrelated read."
        prior_row = {**row, "name": "file_read", "arguments": {"Path": "/neutral"}, "result": "A prior unrelated read.",
                     "success": True, "failure_code": None, "occurrence": 1}
        row["occurrence"] = 2
        self.assertEqual("delivery-neutral", canonical_pairs(
            [joined(prior, request, parent())], ACCEPTED, "start-neutral", "spawn_agent", [prior_row, row])[0])
        repeated = joined(request, request, parent())
        with self.assertRaises(AssertionError):
            canonical_pairs([repeated], ACCEPTED, "start-neutral", "spawn_agent", [row])
        self.assertEqual("delivery-neutral", canonical_pairs(
            [repeated], ACCEPTED, "start-neutral", "spawn_agent", [row, {**row, "occurrence": 3}])[0])

    def test_only_canonical_execution_hints_can_differ(self):
        request, row = rejected()
        row["arguments"].update(_timeout_seconds=20, _background=False)
        self.assertEqual({}, terminal_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", [row]))
        row["arguments"]["extra"] = "untrusted"
        with self.assertRaises(AssertionError):
            terminal_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", [row])

    def test_wrong_pair_code_text_and_nonboolean_success_fail(self):
        request, row = rejected()
        mutations = [lambda r: r.update(id="foreign"), lambda r: r.update(name="file_read"),
                     lambda r: r["arguments"].update(run_id="foreign"),
                     lambda r: r.update(result=REQUIRED_RATIONALE_ERROR + " changed"),
                     lambda r: r.update(failure_code=None), lambda r: r.update(failure_code="unknown_agent"),
                     lambda r: r.update(success=0), lambda r: r.update(success=True),
                     lambda r: r.update(occurrence=0), lambda r: r.update(occurrence=True)]
        for mutate in mutations:
            bad = copy.deepcopy(row)
            mutate(bad)
            with self.subTest(metadata=bad), self.assertRaises((AssertionError, json.JSONDecodeError)):
                terminal_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", [bad])
        altered = copy.deepcopy(request)
        altered["messages"][1]["content"] += " changed"
        with self.assertRaises(AssertionError):
            terminal_pairs([altered], ACCEPTED, "start-neutral", "spawn_agent", [row])

    def test_duplicate_occurrence_and_conflicting_exact_metadata_fail(self):
        request, row = rejected()
        for metadata in ([row, copy.deepcopy(row)], [row, {**row, "occurrence": 2, "failure_code": None}]):
            with self.subTest(metadata=metadata), self.assertRaises(AssertionError):
                terminal_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", metadata)

    def test_observed_model_terminal_json_cannot_be_a_framework_delivery(self):
        request = parent()
        row = {"id": "delivery-neutral", "name": "spawn_agent", "arguments": {
            "run_id": ACCEPTED["run_id"], "source_operation": "spawn_agent"}, "result": json.dumps(terminal()),
            "success": True, "failure_code": None, "occurrence": 1}
        with self.assertRaises(AssertionError):
            canonical_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", [row])
        identifier, _ = canonical_pairs([parent(identifier="forged")], ACCEPTED, "start-neutral", "spawn_agent", [])
        with self.assertRaises(AssertionError):
            committed_positions(logs(), "session-neutral", ACCEPTED["run_id"], identifier)

    def test_rejection_does_not_weaken_canonical_or_duplicate_terminal_gates(self):
        request, row = rejected()
        malformed = [parent({**terminal(), "scope_id": "foreign"}), parent({**terminal(), "source_operation": "file_read"})]
        for result in ("malformed prose", "{}", '{"run_id":"run-neutral","run_id":"run-neutral"}'):
            bad = parent()
            bad["messages"][1]["content"] = result
            malformed.append(bad)
        duplicate = joined(parent(), parent())
        for bad in [*malformed, duplicate]:
            with self.subTest(request=bad), self.assertRaises((AssertionError, json.JSONDecodeError)):
                canonical_pairs([joined(request, bad)], ACCEPTED, "start-neutral", "spawn_agent", [row])

    def test_dto_rejection_attribution_precedes_provider_classification(self):
        request, row = rejected()
        events = [{"sequence": 1, "observed_ns": 10, "output": {"Type": "tool_call", "SessionId": "session-neutral",
                  "CallId": row["id"], "ToolName": row["name"], "ArgumentsJson": json.dumps(row["arguments"])}},
                  {"sequence": 2, "observed_ns": 11, "output": {"Type": "tool_result", "SessionId": "session-neutral",
                  "CallId": row["id"], "ToolName": row["name"], "Result": row["result"], "ToolFailureCode": row["failure_code"]}}]
        dto_calls, _ = occurrences(events, "session-neutral")
        require_observed_rejections([row], dto_calls)
        self.assertEqual({}, terminal_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", [row]))
        for field, value in [("ToolFailureCode", None), ("ToolFailureCode", "unknown_agent"),
                             ("Result", REQUIRED_RATIONALE_ERROR + " changed"), ("SessionId", "foreign"),
                             ("CallId", "foreign"), ("ToolName", "file_read")]:
            changed = copy.deepcopy(events)
            changed[1]["output"][field] = value
            with self.subTest(field=field, value=value), self.assertRaises((AssertionError, ValueError)):
                calls, _ = occurrences(changed, "session-neutral")
                require_observed_rejections([row], calls)
        for value in [0, True, 2]:
            with self.subTest(occurrence=value), self.assertRaises(AssertionError):
                require_observed_rejections([{**row, "occurrence": value}], dto_calls)

    def test_fixture_http_control_uses_the_exact_observed_rejection(self):
        request, row = rejected()
        expected = [{"accepted": ACCEPTED, "call_id": "start-neutral", "source_operation": "spawn_agent"}]
        with tempfile.TemporaryDirectory() as directory:
            fixture = ChildFixture("http://127.0.0.1:1", "unused", "", Path(directory) / "relay")
            fixture.records = [{"request": joined(request, parent()), "request_id": 1, "admitted_ns": 2,
                                "response_first_payload_ns": 3, "response_payload_written": True, "forward_complete": True}]
            server = ThreadingHTTPServer(("127.0.0.1", 0), child_handler(fixture))
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            try:
                def post(metadata):
                    data = {"expected": expected, "parent_boundary_ns": 4, "observed_calls": metadata}
                    call = urllib.request.Request(f"http://127.0.0.1:{server.server_port}/control/child-consumed",
                        data=json.dumps(data).encode(), headers={"Content-Type": "application/json"})
                    with urllib.request.urlopen(call, timeout=2) as response:
                        return json.load(response)
                result = post([row])
                self.assertTrue(result["complete"])
                self.assertEqual(["delivery-neutral"], [d["call_id"] for d in result["deliveries"]])
                with self.assertRaises(urllib.error.HTTPError) as error:
                    post([{**row, "success": 0}])
                self.assertEqual(400, error.exception.code)
                error.exception.close()
                fixture.records[0]["request"] = request
                self.assertFalse(post([row])["complete"])
            finally:
                server.shutdown()
                server.server_close()
                thread.join(2)
                self.assertFalse(thread.is_alive())


if __name__ == "__main__":
    unittest.main()
