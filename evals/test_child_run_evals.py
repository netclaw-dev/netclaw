"""Controls for child attribution, durable evidence, and actual-file oracles."""

import copy
from contextlib import contextmanager
import json
import hashlib
import io
import subprocess
from contextlib import redirect_stdout
import os
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import tempfile
import threading
import time
import urllib.request
import unittest
from unittest.mock import patch

from child_run_evals import (CHILD_CONTRACT, REQUIRED_RATIONALE_ERROR, ChildFixture, acceptance, accepted_start_calls, actual_file, bind_request,
                             canonical_pairs, child_handler, committed_positions, consumed_deliveries, collect, legacy_observer_mode, read_cancellation_report, session_logs, validate_prompt_receipt, verified_final_response, verify_child_actions, verify_cli_acceptance, verify_trial, write_completed)

ACCEPTED = {"run_id": "run-neutral", "scope_id": "scope-neutral", "state": "Accepted", "control_tool": "check_agent_run"}
ROOT = "/home/netclaw/.netclaw/sessions/neutral/subagents/neutral"
PATHS = {"session_dir": ROOT, "temp_dir": ROOT + "/tmp", "artifact_dir": ROOT + "/artifacts", "log_path": ROOT + "/logs/session.log"}
STATUS = {**ACCEPTED, "state": "Running", "log_path": PATHS["log_path"], "artifact_directory": PATHS["artifact_dir"]}


def child():
    return {"messages": [{"role": "system", "content": CHILD_CONTRACT}, {"role": "user", "content":
            "Context:\n[session]\n" + "\n".join(key + ": " + value for key, value in PATHS.items()) + "\nTask:\nTrial neutral-nonce."}]}


def terminal(cancel=False):
    return {"run_id": ACCEPTED["run_id"], "scope_id": ACCEPTED["scope_id"], "source_operation": "spawn_agent",
            "state": "Cancelled" if cancel else "Completed", "outcome": "Failed" if cancel else "Completed",
            "reason": "cancelled_by_parent" if cancel else None, "log_path": PATHS["log_path"],
            "artifact_directory": PATHS["artifact_dir"],
            "output": "The child was cancelled. The last durable partial report is at " + PATHS["artifact_dir"] + "/cancelled-results.json.",
            "warning": None, "checkpoint": {"CompletedRound": 1, "Summary": "File written: partial-neutral-nonce.txt",
                "ConfirmedActivity": {"ProjectDirectory": None, "Worktree": None, "Branch": None, "Head": None,
                                      "ConfirmedChangedFiles": [PATHS["artifact_dir"] + "/partial-neutral-nonce.txt"],
                                      "ReadFiles": [], "ObservedChangedFiles": []}} if cancel else None}


def parent(value=None, identifier="delivery-neutral"):
    return {"messages": [{"role": "assistant", "tool_calls": [{"id": identifier, "function": {
                "name": "spawn_agent", "arguments": json.dumps({"run_id": ACCEPTED["run_id"], "source_operation": "spawn_agent"})}}]},
            {"role": "tool", "tool_call_id": identifier, "content": json.dumps(value or terminal())}]}



def child_after_write(cancel=False):
    request = child()
    filename = "/partial-neutral-nonce.txt" if cancel else "/complete-neutral-nonce.txt"
    content = ("PARTIAL-" if cancel else "COMPLETE-") + "neutral-nonce"
    request["messages"].extend([{"role": "assistant", "tool_calls": [{"id": "write-neutral", "function": {
        "name": "file_write", "arguments": json.dumps({"Path": PATHS["artifact_dir"] + filename, "Content": content})}}]},
        {"role": "tool", "tool_call_id": "write-neutral", "content": "File written"}])
    return request

def logs():
    return "\n".join(f"child_run_{name} owner=session-neutral runId=run-neutral journalSequence={number}" +
                     (" inputId=input-neutral callId=delivery-neutral" if name == "delivery_admitted" else "")
                     for name, number in [("accepted", 1), ("terminal_recorded", 3), ("result_prepared", 4), ("delivery_admitted", 5)])


class ChildAttributionControls(unittest.TestCase):
    def test_collect_retains_rejected_attempt_before_two_accepted_children(self):
        sibling = {**ACCEPTED, "run_id": "run-sibling", "scope_id": "scope-sibling"}
        rejected = {"id": "rejected", "name": "spawn_agent", "success": False,
                    "failure_code": "invalid_rationale", "result": REQUIRED_RATIONALE_ERROR}
        starts = [rejected] + [{"id": identifier, "name": "spawn_agent", "success": True,
                               "failure_code": None, "result": json.dumps(value)}
                              for identifier, value in [("start-neutral", ACCEPTED), ("start-sibling", sibling)]]
        sibling_body = {**terminal(), "run_id": sibling["run_id"], "scope_id": sibling["scope_id"]}
        sibling_request = parent(sibling_body, "delivery-sibling")
        sibling_request["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
            {"run_id": sibling["run_id"], "source_operation": "spawn_agent"})
        receipt = {"session_id": "session-neutral", "accepted_runs": [ACCEPTED, sibling],
                   "calls": starts, "delivery_observations": {"complete": True}}
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {"TMPDIR_EVAL": directory, "EVAL_HOME": directory}):
            with patch("child_run_evals.invoke_observer", return_value=(receipt, "neutral observer output")), \
                 patch("child_run_evals.evidence_requests", return_value=[parent(), sibling_request]), \
                 patch("child_run_evals.session_logs", return_value=logs() + "\n" + logs().replace("run-neutral", "run-sibling").replace("delivery-neutral", "delivery-sibling")), \
                 redirect_stdout(io.StringIO()):
                collect(1, "neutral task", "session-neutral", "json", directory, "subagent_specialization_precedence", 1)
            saved = json.loads((Path(directory) / "verified-receipt.json").read_text())
            self.assertEqual([ACCEPTED, sibling], [row["accepted"] for row in saved["verified_deliveries"]])
            self.assertEqual(rejected, saved["calls"][0])

    def test_start_classifier_rejects_untrusted_failure_shapes(self):
        rejected = {"name": "spawn_agent", "success": False, "failure_code": "invalid_rationale",
                    "result": REQUIRED_RATIONALE_ERROR}
        self.assertEqual([], accepted_start_calls([rejected]))
        for changed in [{**rejected, "success": 0}, {**rejected, "success": True},
                        {**rejected, "failure_code": None}, {**rejected, "failure_code": "unknown_agent"},
                        {**rejected, "result": REQUIRED_RATIONALE_ERROR + " suffix"},
                        {**rejected, "result": json.dumps(ACCEPTED)}]:
            with self.subTest(changed=changed), self.assertRaises((AssertionError, ValueError)):
                accepted_start_calls([changed])

    def test_acceptance_requires_canonical_fields_and_unique_keys(self):
        self.assertEqual(ACCEPTED, acceptance(json.dumps(ACCEPTED)))
        for value in [{**ACCEPTED, "scope_id": " "}, {**ACCEPTED, "state": "Completed"},
                      {**ACCEPTED, "control_tool": "check_background_job"}]:
            with self.subTest(value=value), self.assertRaises(AssertionError):
                acceptance(json.dumps(value))
        with self.assertRaises(AssertionError):
            acceptance('{"run_id":"first",' + json.dumps(ACCEPTED)[1:])

    def test_cancellation_wire_uses_reason_value_and_enum_names_for_state_and_outcome(self):
        source = Path(__file__).resolve().parents[1] / "src/Netclaw.Tools.Abstractions/SubAgentRunMetadata.cs"
        match = re.search(r'CancelledByParent\s*=\s*new\("([^"\n]+)"\)', source.read_text())
        self.assertIsNotNone(match, "The canonical outcome-reason definition changed; review its wire value.")
        wire = json.loads('{"state":"Cancelled","outcome":"Failed","reason":"cancelled_by_parent"}')
        self.assertEqual(match.group(1), wire["reason"])
        self.assertEqual(wire, {name: terminal(True)[name] for name in wire})

    def test_binding_requires_actual_child_contract_nonce_and_returned_paths(self):
        self.assertEqual(PATHS, bind_request(child(), ACCEPTED, STATUS, "neutral-nonce"))
        mutations = [lambda r: r["messages"][0].update(content="ordinary parent prompt"),
                     lambda r: r["messages"][0].update(content="Summarize memory"),
                     lambda r: r["messages"][1].update(content=r["messages"][1]["content"].replace(ROOT, ROOT + "-foreign")),
                     lambda r: r["messages"][1].update(content=r["messages"][1]["content"].replace("neutral-nonce", "foreign"))]
        for mutate in mutations:
            request = child()
            mutate(request)
            with self.subTest(request=request), self.assertRaises(AssertionError):
                bind_request(request, ACCEPTED, STATUS, "neutral-nonce")
        for key in ["run_id", "scope_id", "artifact_directory", "log_path"]:
            with self.subTest(key=key), self.assertRaises(AssertionError):
                bind_request(child(), ACCEPTED, {**STATUS, key: "foreign"}, "neutral-nonce")

    def test_terminal_pair_needs_actual_parent_history_and_one_stable_call(self):
        self.assertEqual(("delivery-neutral", terminal()), canonical_pairs([parent(), parent()], ACCEPTED, "start-neutral", "spawn_agent", []))
        for requests in [[], [child()], [parent(), parent(identifier="other")],
                         [parent({**terminal(), "scope_id": "foreign"})],
                         [parent({**terminal(), "source_operation": "shell_execute"})],
                         [parent(), parent({**terminal(), "outcome": "Failed"})]]:
            with self.subTest(requests=requests), self.assertRaises(AssertionError):
                canonical_pairs(requests, ACCEPTED, "start-neutral", "spawn_agent", [])

    def test_terminal_pair_requires_fresh_id_consistent_argument_source_and_assistant_role(self):
        for mutate in [lambda r: r["messages"][0].update(role="user"),
                       lambda r: r["messages"][0]["tool_calls"][0]["function"].update(arguments=json.dumps({"run_id": ACCEPTED["run_id"], "source_operation": "shell_execute"}))]:
            request = parent()
            mutate(request)
            with self.subTest(request=request), self.assertRaises(AssertionError):
                canonical_pairs([request], ACCEPTED, "start-neutral", "spawn_agent", [])
        with self.assertRaises(AssertionError):
            canonical_pairs([parent(identifier="start-neutral")], ACCEPTED, "start-neutral", "spawn_agent", [])
        duplicated = parent()
        duplicated["messages"].extend(copy.deepcopy(duplicated["messages"]))
        with self.assertRaises(AssertionError):
            canonical_pairs([duplicated], ACCEPTED, "start-neutral", "spawn_agent", [])

    def test_consumption_accepts_first_response_and_compatible_siblings_in_one_response(self):
        sibling = {**ACCEPTED, "run_id": "run-sibling", "scope_id": "scope-sibling"}
        sibling_body = {**terminal(), "run_id": sibling["run_id"], "scope_id": sibling["scope_id"]}
        sibling_request = parent(sibling_body, "delivery-sibling")
        sibling_request["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
            {"run_id": sibling["run_id"], "source_operation": "spawn_agent"})
        request = parent()
        request["messages"].extend(sibling_request["messages"])
        records = [{"request": request, "request_id": 1, "admitted_ns": 2,
                    "response_first_payload_ns": 3, "response_payload_written": True}]
        expected = [{"accepted": ACCEPTED, "call_id": "start-neutral", "source_operation": "spawn_agent"}]
        self.assertTrue(consumed_deliveries(records, expected, 4, [])["complete"])
        self.assertFalse(consumed_deliveries(records, [], 4, [])["complete"])
        repeated = records + [{**records[0], "request_id": 2, "admitted_ns": 5, "response_first_payload_ns": 6}]
        self.assertEqual(1, consumed_deliveries(repeated, expected, 7, [])["deliveries"][0]["request_id"])
        expected.append({"accepted": sibling, "call_id": "start-sibling", "source_operation": "spawn_agent"})
        result = consumed_deliveries(records, expected, 4, [])
        self.assertTrue(result["complete"])
        self.assertEqual(2, len(result["deliveries"]))
        self.assertEqual({1}, {row["request_id"] for row in result["deliveries"]})
        for rows, boundary in [([], 4), ([{**records[0], "response_first_payload_ns": 0}], 4),
                               ([{**records[0], "response_payload_written": False}], 4), (records, 2)]:
            with self.subTest(rows=rows, boundary=boundary):
                self.assertFalse(consumed_deliveries(rows, expected, boundary, [])["complete"])

    def test_final_response_requires_the_exact_current_prompt_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            old = root / "observer-0001"
            current = root / "observer-0002"
            old.mkdir(); current.mkdir()
            data = {"Nonce": "prompt-four", "Mode": "turn", "InitialPrompt": "Report the actual final changed files.",
                    "SessionId": "session-neutral"}
            receipt = {"status": "observed", "prompt_nonce": data["Nonce"], "observer_mode": data["Mode"],
                       "initial_prompt_sha256": hashlib.sha256(data["InitialPrompt"].encode()).hexdigest(),
                       "session_id": "session-neutral", "last_reply": "Actual final response", "all_replies": ["REQUIRED-MARKER initial acknowledgement", "Actual final response"]}
            (old / "verified-receipt.json").write_text(json.dumps({**receipt, "prompt_nonce": "prompt-three", "last_reply": "STALE REQUIRED-MARKER"}))
            (current / "observer-input.json").write_text(json.dumps(data))
            with self.assertRaises(FileNotFoundError):
                verified_final_response(current)
            (current / "verified-receipt.json").write_text((old / "verified-receipt.json").read_text())
            with self.assertRaises(AssertionError):
                verified_final_response(current)
            (current / "verified-receipt.json").write_text(json.dumps(receipt))
            self.assertEqual("Actual final response", verified_final_response(current))
            self.assertNotIn("REQUIRED-MARKER", verified_final_response(current))
            for fields in [{"status": "incomplete"}, {"observer_mode": "collect"}, {"initial_prompt_sha256": "foreign"},
                           {"session_id": "foreign"}, {"session_id": ""}, {"last_reply": ""}, {"last_reply": None}]:
                with self.subTest(fields=fields), self.assertRaises(AssertionError):
                    validate_prompt_receipt({**receipt, **fields}, data)

    def test_legacy_handoff_selects_only_its_child_prompt_for_terminal_collection(self):
        self.assertEqual(["turn", "turn", "collect", "turn"],
                         [legacy_observer_mode("coding_context_worktree_handoff", number) for number in range(1, 5)])
        self.assertEqual("collect", legacy_observer_mode("subagent_specialization_precedence", 1))
        for case, number in [("coding_context_worktree_handoff", 0), ("coding_context_worktree_handoff", 5),
                             ("subagent_specialization_precedence", 2), ("foreign", 1)]:
            with self.subTest(case=case, number=number), self.assertRaises(AssertionError):
                legacy_observer_mode(case, number)
        # These unit fixtures represent observer receipts, not actual CLI output.
        for number in [1, 2, 4]:
            with self.subTest(number=number), tempfile.TemporaryDirectory() as directory:
                receipt = {"accepted_runs": [], "last_reply": f"Neutral parent reply {number}"}
                with patch("child_run_evals.invoke_observer", return_value=(receipt, "neutral observer text")) as observer:
                    with redirect_stdout(io.StringIO()):
                        collect(1, "neutral task", "session-neutral", "text", directory, "coding_context_worktree_handoff", number)
                    self.assertEqual("turn", observer.call_args.args[3])
                saved = json.loads((Path(directory) / "verified-receipt.json").read_text())
                self.assertEqual(number, saved["prompt_ordinal"])
                self.assertEqual([], saved["verified_deliveries"])

    def test_discovery_uses_one_turn_without_a_child_wait(self):
        self.assertEqual("turn", legacy_observer_mode("skill_coordination_discovery", 1))
        for ordinal in (0, 2, True, 1.0):
            with self.subTest(ordinal=ordinal), self.assertRaises(AssertionError):
                legacy_observer_mode("skill_coordination_discovery", ordinal)
        with tempfile.TemporaryDirectory() as directory:
            receipt = {"accepted_runs": [], "last_reply": "Actual parent reply"}
            with patch("child_run_evals.invoke_observer", return_value=(receipt, "observer reply")) as observer:
                with redirect_stdout(io.StringIO()):
                    collect(1, "Explain the process", "", "json", directory, "skill_coordination_discovery", 1)
                self.assertEqual("turn", observer.call_args.args[3])
            saved = json.loads((Path(directory) / "verified-receipt.json").read_text())
            self.assertEqual([], saved["verified_deliveries"])
            self.assertEqual("skill_coordination_discovery", saved["case"])

    def shell_functions(self, names):
        source = (Path(__file__).resolve().parents[1] / "evals/run-evals.sh").read_text()
        return "\n".join(re.search(r"^" + re.escape(name) + r"\(\) \{\n.*?^\}", source,
                                    re.MULTILINE | re.DOTALL).group(0) for name in names)

    def test_failed_current_observer_cannot_reuse_an_earlier_final_reply(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            previous = root / "child-runs/observer-previous"
            previous.mkdir(parents=True)
            (previous / "verified-receipt.json").write_text(json.dumps({"last_reply": "STALE-ANSWER"}))
            functions = self.shell_functions(["child_result_consumer", "observe_child_result", "run_prompt_resume",
                                              "stdout_response_contains", "stdout_response_not_contains"])
            script = functions + r"""
set -euo pipefail
case_name=coding_context_worktree_handoff
resolve_daemon_log() { DAEMON_LOG=/missing-neutral-log; }
CHILD_LAST_EVIDENCE="$TMPDIR_EVAL/child-runs/observer-previous"
previous="$CHILD_LAST_EVIDENCE"
if run_prompt_resume session-neutral 'Final neutral prompt.' text 4; then exit 41; fi
[[ "$CHILD_LAST_EVIDENCE" != "$previous" ]]
[[ -f "$CHILD_LAST_EVIDENCE/observer-input.json" ]]
[[ ! -f "$CHILD_LAST_EVIDENCE/verified-receipt.json" ]]
if stdout_response_contains STALE-ANSWER; then exit 42; fi
if stdout_response_not_contains arbitrary; then exit 43; fi
printf '%s' "$CHILD_LAST_EVIDENCE"
"""
            env = {**os.environ, "REPO_ROOT": str(Path(__file__).resolve().parents[1]), "TMPDIR_EVAL": directory,
                   "NETCLAW_BIN": "/bin/true", "CHILD_FIXTURE_PORT": "1", "EVAL_PORT": "1", "PROMPT_TIMEOUT": "1",
                   "NETCLAW_CHILD_OBSERVER": str(root / "missing-observer.dll"), "FILTER_CASE": "coding_context_worktree_handoff"}
            result = subprocess.run(["bash", "-c", script], env=env, text=True, capture_output=True, timeout=10)
            self.assertEqual(0, result.returncode, result.stderr)
            data = json.loads((Path(result.stdout) / "observer-input.json").read_text())
            self.assertEqual("turn", data["Mode"])
            self.assertEqual("Final neutral prompt.", data["InitialPrompt"])

    def test_failed_fourth_prompt_blocks_the_legacy_case_assertion(self):
        with tempfile.TemporaryDirectory() as directory:
            script = self.shell_functions(["run_multi_turn_case"]) + r"""
set -euo pipefail
CATEGORY_SKIPPED=false
FILTER_CASE=coding_context_worktree_handoff
RUNS=1
THRESHOLD=1
CATEGORY_CASES=0; TOTAL_CASES=0; FAILED_CASES=0; CATEGORY_PASSED=0; PASSED_CASES=0
check_daemon_alive() { :; }
setup_coding_context_worktree_handoff() { :; }
# The control supplies return codes only. It supplies no CLI output.
run_prompt_resume() { LAST_TURN_USAGE_LINE=""; [[ "$4" -lt 4 ]]; }
store_metrics() { :; }
store_result() { printf '%s' "$4" > "$CONTROL_ROOT/stored-pass"; printf '%s' "$5" > "$CONTROL_ROOT/stored-detail"; }
assert_coding_context_worktree_handoff() { printf called > "$CONTROL_ROOT/assertion-called"; return 0; }
run_multi_turn_case coding_context_worktree_handoff neutral one two three four
[[ ! -f "$CONTROL_ROOT/assertion-called" ]]
[[ "$(cat "$CONTROL_ROOT/stored-pass")" == 0 ]]
[[ "$(cat "$CONTROL_ROOT/stored-detail")" == observer_failed ]]
[[ "$FAILED_CASES" == 1 ]]
"""
            result = subprocess.run(["bash", "-c", script], env={**os.environ, "CONTROL_ROOT": directory},
                                    text=True, capture_output=True, timeout=10)
            self.assertEqual(0, result.returncode, result.stderr)

    def test_consistent_terminal_source_still_must_equal_the_actual_start(self):
        wrong = parent()
        function = wrong["messages"][0]["tool_calls"][0]["function"]
        function["name"] = "shell_execute"
        function["arguments"] = json.dumps({"run_id": ACCEPTED["run_id"], "source_operation": "shell_execute"})
        wrong["messages"][1]["content"] = json.dumps({**terminal(), "source_operation": "shell_execute"})
        with self.assertRaises(AssertionError):
            canonical_pairs([wrong], ACCEPTED, "start-neutral", "spawn_agent", [])
        self.assertEqual("delivery-neutral", canonical_pairs([wrong], ACCEPTED, "start-neutral", "shell_execute", [])[0])
        record = {"request": wrong, "request_id": 1, "admitted_ns": 1, "response_first_payload_ns": 2, "response_payload_written": True}
        with self.assertRaises(AssertionError):
            consumed_deliveries([record], [{"accepted": ACCEPTED, "call_id": "start-neutral", "source_operation": "spawn_agent"}], 3, [])

    def test_post_commit_positions_reject_absence_duplicate_reorder_and_foreign_call(self):
        self.assertEqual(5, committed_positions(logs(), "session-neutral", "run-neutral", "delivery-neutral")["delivery_admitted"])
        for value in [logs().replace("child_run_result_prepared", "unobserved"), logs() + "\n" + logs(),
                      logs().replace("journalSequence=4", "journalSequence=2"),
                      logs().replace("callId=delivery-neutral", "callId=foreign")]:
            with self.subTest(log=value), self.assertRaises(AssertionError):
                committed_positions(value, "session-neutral", "run-neutral", "delivery-neutral")

    def test_parent_log_reader_uses_the_owned_partition_and_excludes_child_logs(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            parent = home / "data/sessions/opaque-parent/logs/session.log"
            child = home / "data/sessions/opaque-parent/subagents/neutral-child/logs/session.log"
            daemon = home / "logs/daemon.log"
            foreign = home / "data/sessions/opaque-foreign/logs/session.log"
            for path, text in [(parent, logs()), (child, logs()), (daemon, logs()),
                               (foreign, logs().replace("session-neutral", "session-foreign"))]:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(text)
            log = session_logs(home, "session-neutral", ["run-neutral"])
            self.assertEqual(logs(), log)
            self.assertEqual(5, committed_positions(log, "session-neutral", "run-neutral", "delivery-neutral")["delivery_admitted"])

    def test_daemon_diagnostics_alone_cannot_supply_parent_commit_proof(self):
        with tempfile.TemporaryDirectory() as directory:
            daemon = Path(directory) / "logs/daemon.log"
            daemon.parent.mkdir()
            daemon.write_text(logs())
            with self.assertRaises(AssertionError):
                session_logs(directory, "session-neutral", ["run-neutral"])

    def test_parent_log_reader_rejects_foreign_owner_run_and_missing_acceptance(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "data/sessions/opaque-parent/logs/session.log"
            path.parent.mkdir(parents=True)
            for text in [logs().replace("session-neutral", "session-foreign"),
                         logs().replace("run-neutral", "run-foreign"),
                         logs().replace("child_run_accepted", "unobserved")]:
                with self.subTest(log=text):
                    path.write_text(text)
                    with self.assertRaises(AssertionError):
                        session_logs(directory, "session-neutral", ["run-neutral"])

    def test_parent_log_reader_rejects_duplicate_partitions_and_preserves_duplicate_record_rejection(self):
        with tempfile.TemporaryDirectory() as directory:
            first = Path(directory) / "data/sessions/opaque-first/logs/session.log"
            second = Path(directory) / "data/sessions/opaque-second/logs/session.log"
            for path in [first, second]:
                path.parent.mkdir(parents=True)
                path.write_text(logs())
            with self.assertRaises(AssertionError):
                session_logs(directory, "session-neutral", ["run-neutral"])
            second.unlink()
            first.write_text(logs() + "\n" + logs())
            with self.assertRaises(AssertionError):
                committed_positions(session_logs(directory, "session-neutral", ["run-neutral"]),
                                    "session-neutral", "run-neutral", "delivery-neutral")

    def test_parent_log_reader_retains_all_runs_in_one_parent_partition(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "data/sessions/opaque-parent/logs/session.log"
            path.parent.mkdir(parents=True)
            path.write_text(logs() + "\n" + logs().replace("run-neutral", "run-sibling"))
            log = session_logs(directory, "session-neutral", ["run-neutral", "run-sibling"])
            for run_id in ["run-neutral", "run-sibling"]:
                self.assertEqual(5, committed_positions(log, "session-neutral", run_id, "delivery-neutral")["delivery_admitted"])

    @contextmanager
    def actual_held_response(self, directory, partial=False, stream=False):
        body = {**(child_after_write(True) if partial else child()), "stream": stream}
        payload = b'data: {"actual":"held-neutral"}\n\ndata: [DONE]\n\n' if stream else b'{"actual":"held-neutral"}'
        upstream_requests = []
        first_client_byte = threading.Event()
        received = []
        errors = []
        class Upstream(BaseHTTPRequestHandler):
            def log_message(self, *_):
                return
            def do_POST(self):
                upstream_requests.append(self.rfile.read(int(self.headers["Content-Length"])))
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream" if stream else "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
        with ThreadingHTTPServer(("127.0.0.1", 0), Upstream) as upstream:
            fixture = ChildFixture(f"http://127.0.0.1:{upstream.server_port}/v1", "neutral", "", Path(directory) / "relay")
            fixture.control("child-setup", {"case": "child_run_partial_cancel" if partial else "child_run_held_parent", "nonce": "neutral-nonce"})
            with ThreadingHTTPServer(("127.0.0.1", 0), child_handler(fixture)) as relay:
                servers = [upstream, relay]
                threads = [threading.Thread(target=server.serve_forever) for server in servers]
                for thread in threads:
                    thread.start()
                def call():
                    try:
                        request = urllib.request.Request(f"http://127.0.0.1:{relay.server_port}/v1/chat/completions",
                                                         data=json.dumps(body).encode())
                        with urllib.request.urlopen(request, timeout=5) as response:
                            first = response.read(1)
                            first_client_byte.set()
                            received.append(first + response.read())
                    except Exception as error:
                        errors.append(error)
                worker = threading.Thread(target=call)
                worker.start()
                try:
                    fixture.control("child-wait", {})
                    self.assertEqual([json.dumps(body).encode()], upstream_requests)
                    self.assertFalse(first_client_byte.is_set())
                    self.assertTrue(worker.is_alive())
                    row = fixture.snapshot()["requests"][0]
                    self.assertGreater(row["upstream_first_payload_ns"], row["admitted_ns"])
                    self.assertEqual(0, row["response_first_payload_ns"])
                    self.assertFalse(row["response_payload_written"])
                    self.assertTrue((fixture.evidence / "response-0001.wire").read_bytes().endswith(payload))
                    yield fixture
                finally:
                    fixture.control("child-abort", {})
                    worker.join(5)
                    for server in servers:
                        server.shutdown()
                    for thread in threads:
                        thread.join(2)
                self.assertFalse(worker.is_alive())
                self.assertEqual([], errors)
                self.assertEqual([payload], received)
                self.assertTrue(first_client_byte.is_set())
                fixture.control("child-drained", {})
                self.assertTrue(fixture.snapshot()["requests"][0]["response_payload_written"])

    def test_barrier_holds_actual_upstream_payload_then_requires_binding_before_release(self):
        for stream in [False, True]:
            with self.subTest(stream=stream), tempfile.TemporaryDirectory() as directory:
                with self.actual_held_response(directory, stream=stream) as fixture:
                    with self.assertRaises(AssertionError):
                        fixture.control("child-release", {})
                    snapshot = fixture.control("child-bind", {"accepted": ACCEPTED, "status": STATUS})
                    self.assertEqual(1, snapshot["binding"]["request_id"])
                    self.assertGreater(snapshot["binding"]["upstream_first_payload_ns"], snapshot["binding"]["arrived_ns"])
                    with self.assertRaises(AssertionError):
                        fixture.control("child-bind", {"accepted": ACCEPTED, "status": STATUS})
                    fixture.control("child-release", {})

    def test_partial_binding_checks_actual_file_before_parent_cancellation(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {"EVAL_HOME": directory}):
            path = actual_file(directory, PATHS["artifact_dir"] + "/partial-neutral-nonce.txt")
            path.parent.mkdir(parents=True)
            path.write_text("wrong bytes")
            with self.actual_held_response(directory, partial=True) as fixture:
                with self.assertRaises(AssertionError):
                    fixture.control("child-bind", {"accepted": ACCEPTED, "status": STATUS})
                self.assertIsNone(fixture.binding)
                path.write_text("PARTIAL-neutral-nonce")
                self.assertIsNotNone(fixture.control("child-bind", {"accepted": ACCEPTED, "status": STATUS})["binding"])
                fixture.control("child-release", {})

    def test_parent_sidecar_and_foreign_requests_do_not_consume_child_barrier(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = ChildFixture("http://127.0.0.1:1/v1", "neutral", "", Path(directory) / "relay")
            fixture.control("child-setup", {"case": "child_run_held_parent", "nonce": "neutral-nonce"})
            requests = [parent(), {"messages": [{"role": "system", "content": "memory distillation"},
                                               {"role": "user", "content": "neutral-nonce"}]}, child()]
            requests[-1]["messages"][1]["content"] = requests[-1]["messages"][1]["content"].replace("neutral-nonce", "foreign")
            for request in requests:
                self.assertIsNone(fixture.completion(request))
            self.assertIsNone(fixture.candidate)
            self.assertEqual(3, len(list((Path(directory) / "relay").glob("request-*.json"))))

    def test_actual_forward_preserves_stream_and_json_bytes_and_captures_admission(self):
        received = []
        class Upstream(BaseHTTPRequestHandler):
            def log_message(self, *_):
                return
            def do_POST(self):
                body = self.rfile.read(int(self.headers["Content-Length"]))
                received.append(body)
                parsed = json.loads(body)
                stream = parsed["stream"]
                if parsed.get("fail"):
                    self.send_error(503, "Neutral upstream failure")
                    return
                reply = b'data: {"actual":"neutral"}\n\ndata: [DONE]\n\n' if stream else b'{"actual":"neutral"}'
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream" if stream else "application/json")
                self.send_header("Content-Length", str(len(reply)))
                self.end_headers()
                self.wfile.write(reply)
        with tempfile.TemporaryDirectory() as directory, ThreadingHTTPServer(("127.0.0.1", 0), Upstream) as upstream:
            fixture = ChildFixture(f"http://127.0.0.1:{upstream.server_port}/v1", "neutral", "", Path(directory) / "relay")
            with ThreadingHTTPServer(("127.0.0.1", 0), child_handler(fixture)) as relay:
                threads = [threading.Thread(target=server.serve_forever) for server in [upstream, relay]]
                for thread in threads:
                    thread.start()
                try:
                    for stream in [False, True]:
                        body = {**parent(), "stream": stream}
                        request = urllib.request.Request(f"http://127.0.0.1:{relay.server_port}/v1/chat/completions",
                                                         data=json.dumps(body).encode())
                        with urllib.request.urlopen(request, timeout=2) as response:
                            actual = response.read()
                        expected = b'data: {"actual":"neutral"}\n\ndata: [DONE]\n\n' if stream else b'{"actual":"neutral"}'
                        self.assertEqual(expected, actual)
                        self.assertEqual(json.dumps(body).encode(), received[-1])
                        index = len(received)
                        self.assertEqual(body, json.loads((fixture.evidence / f"request-{index:04}.json").read_text()))
                        wire = (fixture.evidence / f"response-{index:04}.wire").read_bytes()
                        self.assertTrue(wire.endswith(expected))
                        snapshot = fixture.control("snapshot", {})
                        row = snapshot["requests"][-1]
                        self.assertTrue(row["response_payload_written"])
                        self.assertGreater(row["response_first_payload_ns"], row["admitted_ns"])
                        consumed = fixture.control("child-consumed", {"observed_calls": [], "expected": [
                            {"accepted": ACCEPTED, "call_id": "start-neutral", "source_operation": "spawn_agent"}], "parent_boundary_ns": time.monotonic_ns()})
                        self.assertTrue(consumed["complete"])
                        self.assertEqual("delivery-neutral", consumed["deliveries"][0]["call_id"])
                    failed = {**parent(), "stream": False, "fail": True}
                    request = urllib.request.Request(f"http://127.0.0.1:{relay.server_port}/v1/chat/completions",
                                                     data=json.dumps(failed).encode())
                    with self.assertRaises(urllib.error.HTTPError) as error:
                        urllib.request.urlopen(request, timeout=2)
                    self.assertEqual(502, error.exception.code)
                    error.exception.close()
                    row = fixture.records[-1]
                    self.assertEqual(0, row["upstream_first_payload_ns"])
                    self.assertEqual(0, row["response_first_payload_ns"])
                    self.assertFalse(row["response_payload_written"])
                    self.assertFalse(consumed_deliveries([row], [{"accepted": ACCEPTED, "call_id": "start-neutral", "source_operation": "spawn_agent"}],
                                                        time.monotonic_ns(), [])["complete"])
                finally:
                    upstream.shutdown()
                    relay.shutdown()
                    for thread in threads:
                        thread.join(2)

    def test_partial_hold_needs_real_paired_write_result(self):
        request = child()
        request["messages"] += [{"role": "assistant", "tool_calls": [{"id": "write", "function": {
            "name": "file_write", "arguments": json.dumps({"Path": PATHS["artifact_dir"] + "/partial-neutral-nonce.txt"})}}]}]
        self.assertIsNone(write_completed(request, "partial-neutral-nonce.txt"))
        request["messages"].append({"role": "tool", "tool_call_id": "foreign", "content": "Wrote file"})
        self.assertIsNone(write_completed(request, "partial-neutral-nonce.txt"))
        request["messages"].append({"role": "tool", "tool_call_id": "write", "content": "Wrote file"})
        self.assertEqual("write", write_completed(request, "partial-neutral-nonce.txt")["call_id"])

    def test_child_action_oracle_rejects_extra_shell_and_wrong_write(self):
        verify_child_actions([child_after_write()], PATHS, "neutral-nonce", False)
        repeated = child_after_write()
        repeated["messages"].extend(copy.deepcopy(repeated["messages"][-2:]))
        with self.assertRaises(AssertionError):
            verify_child_actions([repeated], PATHS, "neutral-nonce", False)
        for mutate in [lambda r: r["messages"][-2]["tool_calls"][0]["function"].update(name="shell_execute"),
                       lambda r: r["messages"][-2]["tool_calls"][0]["function"].update(arguments=json.dumps({"Path": "/tmp/foreign", "Content": "wrong"}))]:
            request = child_after_write()
            mutate(request)
            with self.subTest(request=request), self.assertRaises(AssertionError):
                verify_child_actions([request], PATHS, "neutral-nonce", False)

    def test_actual_cli_acceptance_requires_same_model_object_and_committed_start(self):
        stdout = "[tool:result] spawn_agent → " + json.dumps(ACCEPTED) + "\nCLI-ACCEPTED-neutral-nonce\n"
        request = {"messages": [{"role": "assistant", "tool_calls": [{"id": "start-neutral", "function": {
            "name": "spawn_agent", "arguments": json.dumps({"Agent": "child-run-worker"})}}]},
            {"role": "tool", "tool_call_id": "start-neutral", "content": json.dumps(ACCEPTED)}]}
        self.assertTrue(verify_cli_acceptance(stdout, [request], logs(), "session-neutral", "neutral-nonce")["passed"])
        for output, requests, log in [(stdout, [], logs()), (stdout, [request], ""),
                                      (stdout.replace("Accepted", "Completed"), [request], logs()),
                                      (stdout.replace("CLI-ACCEPTED-neutral-nonce", "no reply"), [request], logs()),
                                      (stdout, [parent()], logs())]:
            with self.subTest(output=output, requests=requests), self.assertRaises(AssertionError):
                verify_cli_acceptance(output, requests, log, "session-neutral", "neutral-nonce")

    def test_actual_file_rejects_foreign_paths_and_link_escapes(self):
        with tempfile.TemporaryDirectory() as home, tempfile.TemporaryDirectory() as foreign:
            root = Path(home) / "data"
            root.mkdir()
            (root / "escape").symlink_to(foreign)
            for path in ["/tmp/outside", "/home/netclaw/.netclaw/../outside", "/home/netclaw/.netclaw/escape/file"]:
                with self.subTest(path=path), self.assertRaises(AssertionError):
                    actual_file(home, path)


class CancellationReportReadControls(unittest.TestCase):
    def test_owner_read_preserves_one_exact_payload_and_uses_no_shell(self):
        with tempfile.TemporaryDirectory() as home, tempfile.TemporaryDirectory() as evidence:
            path = PATHS["artifact_dir"] + "/cancelled-results.json"
            target = actual_file(home, path)
            target.parent.mkdir(parents=True)
            target.write_bytes(b"host bytes must not supply the proof")
            body = b'{"summary":"actual owner bytes"}\r\n'
            with patch.dict(os.environ, {"EVAL_CONTAINER_NAME": "owned-neutral-container", "TMPDIR_EVAL": evidence}), \
                 patch("child_run_evals.subprocess.run", return_value=subprocess.CompletedProcess([], 0, body, b"")) as execute, \
                 patch.object(Path, "read_bytes", side_effect=PermissionError("The host cannot read the report")):
                self.assertEqual(body, read_cancellation_report(home, path, PATHS["artifact_dir"]))
            execute.assert_called_once_with(["docker", "exec", "--user", "netclaw", "owned-neutral-container", "cat", "--", path],
                                            capture_output=True, check=True, timeout=30)
            self.assertEqual(body, (Path(evidence) / "child-runs/actual-cancelled-results.json").read_bytes())

    def test_wrong_foreign_link_and_nonregular_paths_fail_before_container_read(self):
        with tempfile.TemporaryDirectory() as home, patch("child_run_evals.subprocess.run") as execute:
            path = PATHS["artifact_dir"] + "/cancelled-results.json"
            target = actual_file(home, path)
            target.parent.mkdir(parents=True)
            target.write_bytes(b"{}")
            for bad in [PATHS["artifact_dir"] + "/other.json", "/tmp/cancelled-results.json",
                        PATHS["artifact_dir"] + "/../cancelled-results.json", path.replace("/artifacts/", "/artifacts//")]:
                with self.subTest(path=bad), self.assertRaises(AssertionError):
                    read_cancellation_report(home, bad, PATHS["artifact_dir"])
            other = target.with_name("other.json")
            other.write_bytes(b"{}")
            target.unlink()
            target.symlink_to(other)
            with self.assertRaises(AssertionError):
                read_cancellation_report(home, path, PATHS["artifact_dir"])
            target.unlink()
            target.mkdir()
            with self.assertRaises(AssertionError):
                read_cancellation_report(home, path, PATHS["artifact_dir"])
            target.rmdir()
            other.unlink()
            target.parent.rmdir()
            relocated = target.parent.with_name("real-artifacts")
            relocated.mkdir()
            (relocated / target.name).write_bytes(b"{}")
            target.parent.symlink_to(relocated, target_is_directory=True)
            with self.assertRaises(AssertionError):
                read_cancellation_report(home, path, PATHS["artifact_dir"])
            execute.assert_not_called()

    def test_failed_owner_read_and_timeout_are_fatal_without_capture(self):
        with tempfile.TemporaryDirectory() as home, tempfile.TemporaryDirectory() as evidence:
            path = PATHS["artifact_dir"] + "/cancelled-results.json"
            target = actual_file(home, path)
            target.parent.mkdir(parents=True)
            target.write_bytes(b"{}")
            with patch.dict(os.environ, {"EVAL_CONTAINER_NAME": "owned-neutral-container", "TMPDIR_EVAL": evidence}):
                for failure in [subprocess.CalledProcessError(1, ["docker"], stderr=b"Permission denied"),
                                subprocess.TimeoutExpired(["docker"], 30)]:
                    with self.subTest(failure=type(failure).__name__), \
                         patch("child_run_evals.subprocess.run", side_effect=failure), self.assertRaises(type(failure)):
                        read_cancellation_report(home, path, PATHS["artifact_dir"])
            self.assertFalse((Path(evidence) / "child-runs/actual-cancelled-results.json").exists())


class TrialOracleControls(unittest.TestCase):
    def setUp(self):
        evidence = tempfile.TemporaryDirectory()
        self.addCleanup(evidence.cleanup)
        environment = patch.dict(os.environ, {"TMPDIR_EVAL": evidence.name, "EVAL_CONTAINER_NAME": "owned-neutral-container"})
        environment.start()
        self.addCleanup(environment.stop)
        self.report_home = None
        transport = patch("child_run_evals.subprocess.run", side_effect=self.read_report)
        self.transport = transport.start()
        self.addCleanup(transport.stop)

    def read_report(self, command, **kwargs):
        return subprocess.CompletedProcess(command, 0, actual_file(self.report_home, command[-1]).read_bytes(), b"")

    def test_repaired_initial_start_preserves_complete_held_and_cancel_flow(self):
        for cancel in [False, True]:
            with self.subTest(cancel=cancel), tempfile.TemporaryDirectory() as home:
                receipt, snapshot = self.sample(home, cancel)
                rejected = {"id": "rejected-start", "name": "spawn_agent", "arguments": {"Agent": "child-run-worker"},
                            "result": REQUIRED_RATIONALE_ERROR, "success": False,
                            "failure_code": "invalid_rationale", "turn": 1}
                receipt["calls"].insert(0, rejected)
                self.assertTrue(self.verify(receipt, [child_after_write(cancel), parent(terminal(cancel))], snapshot,
                                             logs(), home, "neutral-nonce", cancel)["passed"])
                self.assertEqual(False, receipt["calls"][0]["success"])
                for change in [lambda r: r["calls"].pop(1),
                               lambda r: r["calls"].append(copy.deepcopy(r["calls"][1])),
                               lambda r: r["calls"][1].update(turn=2),
                               lambda r: r["calls"][0].update(failure_code="unknown_agent"),
                               lambda r: r["calls"][0].update(result=REQUIRED_RATIONALE_ERROR + " suffix"),
                               lambda r: r["calls"][0].update(success=0)]:
                    changed = copy.deepcopy(receipt)
                    change(changed)
                    with self.assertRaises((AssertionError, ValueError)):
                        self.verify(changed, [child_after_write(cancel), parent(terminal(cancel))], snapshot,
                                     logs(), home, "neutral-nonce", cancel)

    def test_later_rejected_terminal_shaped_start_preserves_held_and_cancel_flow(self):
        for cancel in [False, True]:
            with self.subTest(cancel=cancel), tempfile.TemporaryDirectory() as home:
                receipt, snapshot = self.sample(home, cancel)
                rejected = {"id": "rejected-terminal", "name": "spawn_agent", "arguments": {
                    "run_id": ACCEPTED["run_id"], "source_operation": "spawn_agent"},
                    "result": REQUIRED_RATIONALE_ERROR, "success": False, "failure_code": "invalid_rationale",
                    "occurrence": 5, "turn": 2, "observed_ns": 28}
                receipt["calls"].append(rejected)
                self.provider_rows.append(copy.deepcopy(rejected))
                requests = [child_after_write(cancel), parent(terminal(cancel))]
                self.assertTrue(self.verify(receipt, requests, snapshot, logs(), home, "neutral-nonce", cancel)["passed"])
                self.assertEqual(rejected, receipt["calls"][-1])
                for field, value in [("failure_code", None), ("result", REQUIRED_RATIONALE_ERROR + " changed"), ("success", 0)]:
                    changed = copy.deepcopy(receipt)
                    changed["calls"][-1][field] = value
                    with self.subTest(field=field), self.assertRaises((AssertionError, ValueError)):
                        self.verify(changed, requests, snapshot, logs(), home, "neutral-nonce", cancel)

    def provider_requests(self, requests, rows=None):
        requests = copy.deepcopy(requests)
        messages = []
        for row in sorted(self.provider_rows if rows is None else rows, key=lambda value: value["observed_ns"]):
            messages.extend([
                {"role": "assistant", "tool_calls": [{"id": row["id"], "function": {
                    "name": row["name"], "arguments": json.dumps(row["arguments"])}}]},
                {"role": "tool", "tool_call_id": row["id"], "content": row["result"]}])
        requests[1]["messages"] = messages + requests[1]["messages"]
        return requests

    def verify(self, receipt, requests, *arguments):
        return verify_trial(receipt, self.provider_requests(requests), *arguments)

    def sample(self, home, cancel=False):
        self.report_home = home
        for canonical in [PATHS["log_path"], PATHS["artifact_dir"] + ("/partial-neutral-nonce.txt" if cancel else "/complete-neutral-nonce.txt")]:
            path = actual_file(home, canonical)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("actual neutral child log line" if canonical == PATHS["log_path"] else
                            ("PARTIAL-" if cancel else "COMPLETE-") + "neutral-nonce")
        calls = [{"id": "start-neutral", "name": "spawn_agent", "arguments": {"Agent": "child-run-worker"},
                  "result": json.dumps(ACCEPTED), "success": True, "turn": 1},
                 {"id": "load-neutral", "name": "load_tool", "arguments": {"Name": "check_agent_run"}, "success": True, "turn": 2, "observed_ns": 12, "result": "check_agent_run"},
                 {"id": "status-neutral", "name": "check_agent_run", "arguments": {"RunId": ACCEPTED["run_id"], "Cancel": cancel}, "success": True, "turn": 2, "observed_ns": 25, "result": json.dumps({**STATUS, "cancellation_requested": False, "dispatch_closed": False, "terminal": None})},
                 {"id": "log-neutral", "name": "file_read", "arguments": {"Path": PATHS["log_path"]}, "success": True, "turn": 2, "observed_ns": 20, "result": "actual neutral child log line"},
                 {"name": "file_read", "arguments": {"Path": PATHS["artifact_dir"] + ("/partial-neutral-nonce.txt" if cancel else "/complete-neutral-nonce.txt")},
                  "success": True, "turn": 2 if cancel else 3, "observed_ns": 35 if cancel else 80,
                  "result": ("PARTIAL-" if cancel else "COMPLETE-") + "neutral-nonce"}]
        receipt = {"accepted_run": ACCEPTED, "status": "observed", "completed_turns": 3, "user_inputs": 2,
                   "first_turn_ns": 10, "second_turn_ns": 40, "release_ns": 50, "session_id": "session-neutral",
                   "all_replies": ["Accepted the child.", "PARENT-PROBE-neutral-nonce: actual neutral child log line"],
                   "calls": calls, "last_reply": ("PARTIAL-" if cancel else "COMPLETE-") + "neutral-nonce"}
        snapshot = {"binding": {"accepted": ACCEPTED, "paths": PATHS, "request_id": 1, "arrived_ns": 5, "upstream_first_payload_ns": 6, "bound_ns": 30},
                    "released": True, "release_ns": 49, "requests": [{"held": True, "child": True, "request_id": 1, "admitted_ns": 5, "upstream_first_payload_ns": 6, "response_first_payload_ns": 0, "response_payload_written": False},
                        {"held": False, "child": False, "request_id": 2, "admitted_ns": 32 if cancel else 60,
                         "response_first_payload_ns": 33 if cancel else 70, "response_payload_written": True}]}
        receipt["delivery_observations"] = {"complete": True, "deliveries": [{"accepted": ACCEPTED,
            "call_id": "delivery-neutral", "terminal": terminal(cancel), "request_id": 2,
            "request_admitted_ns": 32 if cancel else 60, "response_first_payload_ns": 33 if cancel else 70,
            "parent_boundary_ns": 40 if cancel else 90}]}
        if cancel:
            checkpoint = terminal(True)["checkpoint"]
            report = {"run_id": ACCEPTED["run_id"], "state": "Cancelled", "summary": checkpoint["Summary"],
                      "confirmed_activity": checkpoint["ConfirmedActivity"],
                      "external_effects": "Recorded receipts describe known local results. They do not prove external effects stopped."}
            actual_file(home, PATHS["artifact_dir"] + "/cancelled-results.json").write_text(json.dumps(report))
            calls[2].update(observed_ns=25, result=json.dumps({**STATUS, "state": "Cancelling",
                "cancellation_requested": True, "dispatch_closed": False, "terminal": None}))
            calls.extend([
                {"name": "skill_load", "arguments": {"Name": "agent-coordination"}, "success": True,
                 "turn": 1, "observed_ns": 7,
                 "result": "A cancellation acceptance does not prove dispatch closure or terminal completion."},
                {"name": "file_read", "arguments": {"Path": PATHS["artifact_dir"] + "/cancelled-results.json"},
                 "success": True, "turn": 2, "observed_ns": 34, "result": json.dumps(report)},
                {"name": "check_agent_run", "arguments": {"RunId": ACCEPTED["run_id"], "Cancel": False},
                 "success": True, "turn": 2, "observed_ns": 36,
                 "result": json.dumps({**STATUS, "state": "Cancelled", "cancellation_requested": True,
                                        "dispatch_closed": True, "terminal": json.dumps(terminal(True))})}])
        self.provider_rows = copy.deepcopy(calls[1:4])
        return receipt, snapshot

    def test_valid_held_flow_requires_runtime_provider_and_actual_artifact_evidence(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home)
            self.assertTrue(self.verify(receipt, [child_after_write(), parent()], snapshot, logs(), home, "neutral-nonce", False)["passed"])
            for mutate in [lambda r: r.update(user_inputs=3), lambda r: r.update(completed_turns=1),
                           lambda r: r.update(first_turn_ns=0), lambda r: r.update(last_reply="imagined artifact"),
                           lambda r: r["calls"].pop(), lambda r: r["calls"][3].update(success=False),
                           lambda r: r["calls"][2]["arguments"].update(RunId="foreign"), lambda r: r["calls"][3].update(turn=3),
                           lambda r: r["calls"][4].update(observed_ns=60), lambda r: r["calls"][4].update(observed_ns=100)]:
                value = copy.deepcopy(receipt)
                mutate(value)
                with self.subTest(receipt=value), self.assertRaises(AssertionError):
                    self.verify(value, [child_after_write(), parent()], snapshot, logs(), home, "neutral-nonce", False)
            for mutate in [lambda row: row["binding"].update(upstream_first_payload_ns=0),
                           lambda row: row.update(release_ns=39),
                           lambda row: row["binding"].update(request_id=2),
                           lambda row: row["requests"][0].update(upstream_first_payload_ns=0)]:
                invalid = copy.deepcopy(snapshot)
                mutate(invalid)
                with self.subTest(snapshot=invalid), self.assertRaises(AssertionError):
                    self.verify(receipt, [child_after_write(), parent()], invalid, logs(), home, "neutral-nonce", False)
            actual_file(home, PATHS["artifact_dir"] + "/complete-neutral-nonce.txt").write_text("incorrect")
            with self.assertRaises(AssertionError):
                self.verify(receipt, [child_after_write(), parent()], snapshot, logs(), home, "neutral-nonce", False)

    def test_initial_turn_status_and_load_support_a_fresh_probe_log_and_reply(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home)
            receipt["calls"][1].update(turn=1, observed_ns=7)
            receipt["calls"][2].update(turn=1, observed_ns=8)
            requests = self.provider_requests([child_after_write(), parent()], receipt["calls"][1:4])
            verdict = verify_trial(receipt, requests, snapshot, logs(), home, "neutral-nonce", False)
            self.assertEqual(1, verdict["held_probe"]["status_turn"])
            receipt["calls"][2].update(turn=2, observed_ns=25)
            requests = self.provider_requests([child_after_write(), parent()], receipt["calls"][1:4])
            self.assertEqual(2, verify_trial(receipt, requests, snapshot, logs(), home, "neutral-nonce", False)["held_probe"]["status_turn"])

    def test_probe_rejects_status_outside_the_same_held_interval(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home)
            for timestamp in [0, 5, 6, 40, 50]:
                changed = copy.deepcopy(receipt)
                changed["calls"][2]["observed_ns"] = timestamp
                with self.subTest(timestamp=timestamp), self.assertRaises(AssertionError):
                    self.verify(changed, [child_after_write(), parent()], snapshot, logs(), home, "neutral-nonce", False)
            changed = copy.deepcopy(snapshot)
            changed["requests"][0]["held"] = False
            with self.assertRaises(AssertionError):
                self.verify(receipt, [child_after_write(), parent()], changed, logs(), home, "neutral-nonce", False)

    def test_probe_rejects_foreign_paths_owner_state_and_noncanonical_booleans(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home)
            status = json.loads(receipt["calls"][2]["result"])
            for field, value in [("run_id", "foreign"), ("scope_id", "foreign"), ("log_path", ROOT + "/other.log"),
                                 ("artifact_directory", ROOT + "/other"), ("state", "Completed"),
                                 ("state", "Cancelling"), ("cancellation_requested", 0), ("dispatch_closed", 0),
                                 ("cancellation_requested", True), ("dispatch_closed", True), ("terminal", "{}")]:
                changed = copy.deepcopy(receipt)
                changed["calls"][2]["result"] = json.dumps({**status, field: value})
                requests = self.provider_requests([child_after_write(), parent()], changed["calls"][1:4])
                with self.subTest(field=field, value=value), self.assertRaises(AssertionError):
                    verify_trial(changed, requests, snapshot, logs(), home, "neutral-nonce", False)

    def test_probe_requires_a_successful_attributed_load_result_before_status_call(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home)
            requests = self.provider_requests([child_after_write(), parent()])
            self.assertTrue(verify_trial(receipt, requests, snapshot, logs(), home, "neutral-nonce", False)["passed"])
            for mutate in [lambda r: r["calls"][1].update(success=False),
                           lambda r: r["calls"][1].update(failure_code="invalid_rationale"),
                           lambda r: r["calls"][1].update(result="other_tool"),
                           lambda r: r["calls"][1].update(observed_ns=25),
                           lambda r: r["calls"][1].update(id="foreign"),
                           lambda r: r["calls"][2].update(id="foreign")]:
                changed = copy.deepcopy(receipt)
                mutate(changed)
                with self.assertRaises(AssertionError):
                    verify_trial(changed, requests, snapshot, logs(), home, "neutral-nonce", False)
            changed = copy.deepcopy(requests)
            messages = changed[1]["messages"]
            load_result = messages.pop(1)
            messages.insert(4, load_result)
            with self.assertRaises(AssertionError):
                verify_trial(receipt, changed, snapshot, logs(), home, "neutral-nonce", False)
            for index in [0, 1, 4, 5]:
                changed = copy.deepcopy(requests)
                changed[1]["messages"].pop(index)
                with self.subTest(missing=index), self.assertRaises(AssertionError):
                    verify_trial(receipt, changed, snapshot, logs(), home, "neutral-nonce", False)

    def test_probe_requires_a_fresh_attributed_log_read_and_marker_before_release(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home)
            for mutate in [lambda r: r["calls"][3].update(observed_ns=10),
                           lambda r: r["calls"][3].update(observed_ns=40),
                           lambda r: r["calls"][3].update(observed_ns=50),
                           lambda r: r["calls"][3].update(id="foreign"),
                           lambda r: r["calls"][3].update(result="imagined child log"),
                           lambda r: r.update(all_replies=["Accepted", "No probe marker"]),
                           lambda r: r.update(all_replies=["Accepted", "No marker", "PARENT-PROBE-neutral-nonce"])]:
                changed = copy.deepcopy(receipt)
                mutate(changed)
                with self.assertRaises(AssertionError):
                    self.verify(changed, [child_after_write(), parent()], snapshot, logs(), home, "neutral-nonce", False)
            requests = self.provider_requests([child_after_write(), parent()])
            changed = copy.deepcopy(requests)
            changed[0]["messages"].extend(changed[1]["messages"][2:4])
            del changed[1]["messages"][2:4]
            with self.assertRaises(AssertionError):
                verify_trial(receipt, changed, snapshot, logs(), home, "neutral-nonce", False)

    def test_probe_provider_arguments_omit_only_execution_hints(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home)
            for row in receipt["calls"][1:4]:
                row["arguments"].update(_background=False, _timeout_seconds=30)
            self.assertTrue(self.verify(receipt, [child_after_write(), parent()], snapshot, logs(), home, "neutral-nonce", False)["passed"])
            receipt["calls"][2]["arguments"]["_rationale"] = "Changed intent."
            with self.assertRaises(AssertionError):
                self.verify(receipt, [child_after_write(), parent()], snapshot, logs(), home, "neutral-nonce", False)

    def test_cancel_stays_in_probe_turn_after_the_actual_log_read(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home, True)
            for mutate in [lambda r: r["calls"][2].update(turn=1, observed_ns=8),
                           lambda r: r["calls"][2].update(observed_ns=19),
                           lambda r: r["calls"][3].update(observed_ns=26)]:
                changed = copy.deepcopy(receipt)
                mutate(changed)
                requests = self.provider_requests([child_after_write(True), parent(terminal(True))], changed["calls"][1:4])
                with self.assertRaises(AssertionError):
                    verify_trial(changed, requests, snapshot, logs(), home, "neutral-nonce", True)

    def test_full_trial_requires_exact_unique_terminal_argument_keys(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home)
            def check(arguments):
                request = parent()
                request["messages"][0]["tool_calls"][0]["function"]["arguments"] = arguments
                return self.verify(receipt, [child_after_write(), request], snapshot, logs(), home, "neutral-nonce", False)
            self.assertTrue(check('  { "source_operation" : "spawn_agent",\n "run_id" : "run-neutral" }  ')["passed"])
            for arguments in [
                '{"run_id":"run-neutral","source_operation":"spawn_agent","unexpected_authority":"foreign"}',
                '{"run_id":"foreign","run_id":"run-neutral","source_operation":"spawn_agent"}',
                '{"run_id":"run-neutral","run_id":"foreign","source_operation":"spawn_agent"}',
                '{"run_id":"run-neutral","run_id":"run-neutral","source_operation":"spawn_agent"}',
                '{"run_id":"run-neutral","source_operation":"shell_execute","source_operation":"spawn_agent"}',
                '{"run_id":"run-neutral","source_operation":"spawn_agent","source_operation":"spawn_agent"}',
                '{"run_id":"run-neutral","source_operation":"spawn_agent","extra":{"scope":"foreign","scope":"owner"}}',
                '{"run_id":"run-neutral","source_operation":{"name":"foreign","name":"spawn_agent"}}',
            ]:
                with self.subTest(arguments=arguments), self.assertRaises(AssertionError):
                    check(arguments)

    def test_full_trial_rejects_duplicate_terminal_body_keys_at_each_object_scope(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home)
            for body in [
                '{"run_id":"foreign",' + json.dumps(terminal())[1:],
                json.dumps(terminal())[:-1] + ',"extra":{"scope":"foreign","scope":"owner"}}',
            ]:
                request = parent()
                request["messages"][1]["content"] = body
                observed = copy.deepcopy(receipt)
                observed["delivery_observations"]["deliveries"][0]["terminal"] = json.loads(body)
                with self.subTest(body=body), self.assertRaises(AssertionError):
                    self.verify(observed, [child_after_write(), request], snapshot, logs(), home, "neutral-nonce", False)

    def test_cancel_consumption_can_complete_inside_second_probe_before_release(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home, True)
            receipt["completed_turns"] = 2
            receipt["last_reply"] += " PARENT-PROBE-neutral-nonce"
            self.assertTrue(self.verify(receipt, [child_after_write(True), parent(terminal(True))], snapshot,
                                         logs(), home, "neutral-nonce", True)["passed"])

    def test_cancel_reads_report_once_and_rejects_malformed_owner_bytes(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home, True)
            path = actual_file(home, PATHS["artifact_dir"] + "/cancelled-results.json")
            body = path.read_bytes()
            self.assertTrue(self.verify(receipt, [child_after_write(True), parent(terminal(True))], snapshot,
                                         logs(), home, "neutral-nonce", True)["passed"])
            self.transport.assert_called_once()
            capture = Path(os.environ["TMPDIR_EVAL"]) / "child-runs/actual-cancelled-results.json"
            self.assertEqual(body, capture.read_bytes())
            for malformed in [b"\xff", b"not JSON", b"[]", b'{"run_id":"foreign","run_id":"run-neutral"}']:
                self.transport.return_value = subprocess.CompletedProcess([], 0, malformed, b"")
                self.transport.side_effect = None
                with self.subTest(body=malformed), self.assertRaises((AssertionError, ValueError)):
                    self.verify(receipt, [child_after_write(True), parent(terminal(True))], snapshot,
                                 logs(), home, "neutral-nonce", True)
                self.assertEqual(malformed, capture.read_bytes())
            self.transport.return_value = subprocess.CompletedProcess([], 0, body + b"\r\n", b"")
            with self.assertRaisesRegex(AssertionError, "full actual partial-report read"):
                self.verify(receipt, [child_after_write(True), parent(terminal(True))], snapshot,
                             logs(), home, "neutral-nonce", True)

    def test_cancel_requires_guidance_actual_report_read_and_explicit_closure(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home, True)
            mutations = [lambda r: r["calls"].pop(5),
                         lambda r: r["calls"][5].update(success=False),
                         lambda r: r["calls"].pop(6),
                         lambda r: r["calls"][6]["arguments"].update(Limit=1),
                         lambda r: r["calls"][6].update(result="A partial report summary."),
                         lambda r: r["calls"][6].update(observed_ns=31),
                         lambda r: r["calls"].pop(7),
                         lambda r: r["calls"][7].update(result=json.dumps({**STATUS,
                             "cancellation_requested": True, "dispatch_closed": False, "terminal": None})),
                         lambda r: r["calls"][7].update(result=json.dumps({**STATUS,
                             "scope_id": "foreign", "state": "Cancelled", "cancellation_requested": True,
                             "dispatch_closed": True, "terminal": json.dumps(terminal(True))})),
                         lambda r: r["calls"][7].update(result=json.dumps({**STATUS,
                             "state": "Cancelled", "cancellation_requested": True,
                             "dispatch_closed": 1, "terminal": json.dumps(terminal(True))})),
                         lambda r: r["calls"][7].update(result=json.dumps({**STATUS,
                             "state": "Cancelled", "cancellation_requested": True,
                             "dispatch_closed": True, "terminal": json.dumps({**terminal(True), "scope_id": "foreign"})})),
                         lambda r: r["calls"][2].update(result=json.dumps({**STATUS,
                             "state": "Cancelling", "cancellation_requested": 1,
                             "dispatch_closed": False, "terminal": None})),
                         lambda r: r["calls"][2].update(result=json.dumps({**STATUS,
                             "state": "Cancelled", "cancellation_requested": True,
                             "dispatch_closed": False, "terminal": json.dumps(terminal(True))}))]
            for index, mutate in enumerate(mutations):
                changed = copy.deepcopy(receipt)
                mutate(changed)
                with self.subTest(fault=index), self.assertRaises(AssertionError):
                    self.verify(changed, [child_after_write(True), parent(terminal(True))], snapshot,
                                 logs(), home, "neutral-nonce", True)

    def test_cancel_status_requires_exact_terminal_json_string(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home, True)
            self.assertTrue(self.verify(receipt, [child_after_write(True), parent(terminal(True))], snapshot,
                                         logs(), home, "neutral-nonce", True)["passed"])
            valid_status = json.loads(receipt["calls"][7]["result"])
            self.assertIsInstance(valid_status["terminal"], str)
            changed = copy.deepcopy(receipt)
            del valid_status["terminal"]
            changed["calls"][7]["result"] = json.dumps(valid_status)
            with self.assertRaisesRegex(AssertionError, "canonical JSON-string representation"):
                self.verify(changed, [child_after_write(True), parent(terminal(True))], snapshot,
                             logs(), home, "neutral-nonce", True)
            valid_status = json.loads(receipt["calls"][7]["result"])
            for bad in [None, terminal(True), "not JSON", "[]",
                        '{"run_id":"foreign",' + json.dumps(terminal(True))[1:],
                        json.dumps(terminal(True))[:-1] + ',"extra":{"scope":"foreign","scope":"owner"}}',
                        json.dumps({**terminal(True), "run_id": "foreign"}),
                        json.dumps({**terminal(True), "scope_id": "foreign"}),
                        json.dumps({**terminal(True), "reason": "other"})]:
                changed = copy.deepcopy(receipt)
                changed["calls"][7]["result"] = json.dumps({**valid_status, "terminal": bad})
                with self.subTest(terminal=bad), self.assertRaises((AssertionError, ValueError)):
                    self.verify(changed, [child_after_write(True), parent(terminal(True))], snapshot,
                                 logs(), home, "neutral-nonce", True)

    def test_partial_requires_exact_checkpoint_and_actual_run_local_report(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home, True)
            original = terminal(True)
            def check(body):
                value = copy.deepcopy(receipt)
                value["delivery_observations"]["deliveries"][0]["terminal"] = body
                return self.verify(value, [child_after_write(True), parent(body)], snapshot, logs(), home, "neutral-nonce", True)
            self.assertTrue(check(original)["passed"])
            for mutate in [lambda b: b.update(checkpoint=None),
                           lambda b: b["checkpoint"].update(CompletedRound=0),
                           lambda b: b["checkpoint"].update(Summary=""),
                           lambda b: b["checkpoint"]["ConfirmedActivity"].update(ConfirmedChangedFiles=[]),
                           lambda b: b.update(output=PATHS["artifact_dir"] + "/partial-neutral-nonce.txt"),
                           lambda b: b.update(warning="The framework report failed. No report path is confirmed.")]:
                body = copy.deepcopy(original)
                mutate(body)
                with self.subTest(body=body), self.assertRaises(AssertionError):
                    check(body)
            report_path = actual_file(home, PATHS["artifact_dir"] + "/cancelled-results.json")
            report = json.loads(report_path.read_text())
            for field, value in [("run_id", "foreign"), ("state", "Completed"), ("summary", "altered"),
                                 ("confirmed_activity", {"ConfirmedChangedFiles": []}), ("external_effects", "remote provider stopped")]:
                report_path.write_text(json.dumps({**report, field: value}))
                with self.subTest(field=field), self.assertRaises(AssertionError):
                    check(original)
            report_path.unlink()
            with self.assertRaises(FileNotFoundError):
                check(original)

    def test_cancel_requires_partial_evidence_and_rejects_later_child_admission(self):
        with tempfile.TemporaryDirectory() as home:
            receipt, snapshot = self.sample(home, True)
            self.assertTrue(self.verify(receipt, [child_after_write(True), parent(terminal(True))], snapshot, logs(), home, "neutral-nonce", True)["passed"])
            snapshot["requests"].append({"held": False, "child": True, "request_id": 3})
            with self.assertRaises(AssertionError):
                self.verify(receipt, [child_after_write(True), parent(terminal(True))], snapshot, logs(), home, "neutral-nonce", True)
            snapshot["requests"].pop()
            for fields in [{"reason": "Timeout"}, {"reason": "CancelledByParent"},
                           {"state": "cancelled"}, {"outcome": "failed"}]:
                with self.subTest(fields=fields), self.assertRaises(AssertionError):
                    self.verify(receipt, [child_after_write(True), parent({**terminal(True), **fields})], snapshot, logs(), home, "neutral-nonce", True)


if __name__ == "__main__":
    unittest.main()
