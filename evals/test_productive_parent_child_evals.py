"""Behavioral controls for the sequential catalog proof, without a model."""

import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from child_run_evals import actual_file, legacy_observer_mode
from productive_parent_child_evals import CASE, archive_outputs, encode, prepare, prompt, records, verify
from test_coordination_workflow_evals import shell_functions
from test_child_run_evals import ACCEPTED, child, parent, terminal

ROOT = Path(__file__).resolve().parents[1]


def evidence(root):
    home = root / "home"
    setup = prepare(home, root / "truth")
    child_content = encode({"nonce": setup["nonce"], "records": records(setup, "child")})
    combined_content = encode({"nonce": setup["nonce"], "parent_records": records(setup, "parent"),
                              "child_records": records(setup, "child")})
    actual_file(home, setup["child_output"]).write_text(child_content)
    actual_file(home, setup["combined_output"]).write_text(combined_content)
    receipt = {"session_id": "session-neutral", "accepted_runs": [ACCEPTED],
               "delivery_observations": {"complete": True},
               "verified_deliveries": [{"accepted": ACCEPTED, "terminal": terminal()}],
               "last_reply": json.dumps({"nonce": setup["nonce"], "output": setup["combined_output"],
                                         "parent_records": 65, "child_records": 35})}
    events = []
    parent_request = {"messages": [{"role": "user", "content": prompt(setup)}]}
    child_request = child()

    def pair(request, name, args, result, identifier, dto=False):
        request["messages"].extend([
            {"role": "assistant", "tool_calls": [{"id": identifier, "function": {
                "name": name, "arguments": json.dumps(args)}}]},
            {"role": "tool", "tool_call_id": identifier, "content": result}])
        if dto:
            for out in ({"Type": "tool_call", "CallId": identifier, "ToolName": name,
                         "ArgumentsJson": json.dumps(args)},
                        {"Type": "tool_result", "CallId": identifier, "ToolName": name,
                         "Result": result, "ToolFailureCode": None}):
                n = len(events) + 1
                events.append({"sequence": n, "observed_ns": n, "output": {**out, "SessionId": "session-neutral"}})

    pair(parent_request, "spawn_agent", {"Agent": "task-worker", "Task": setup["nonce"] + " " +
         setup["chains"]["child"][0]["path"] + " " + setup["child_output"]}, json.dumps(ACCEPTED), "start", True)
    for owner, request in (("parent", parent_request), ("child", child_request)):
        for index, row in enumerate(setup["chains"][owner]):
            pair(request, "file_read", {"Path": row["path"]}, row["content"], f"{owner}-{index}", owner == "parent")
    pair(child_request, "file_write", {"Path": setup["child_output"], "Content": child_content},
         f"Successfully wrote {len(child_content.encode())} bytes to {setup['child_output']}", "child-write")
    parent_request["messages"].extend(parent(identifier="terminal")["messages"])
    pair(parent_request, "file_read", {"Path": setup["child_output"]}, child_content, "review", True)
    pair(parent_request, "file_write", {"Path": setup["combined_output"], "Content": combined_content},
         f"Successfully wrote {len(combined_content.encode())} bytes to {setup['combined_output']}", "combined", True)
    return setup, receipt, events, [parent_request, child_request], home


def resequence(events):
    for number, event in enumerate(events, 1):
        event.update(sequence=number, observed_ns=number)


def remove_pair(request, identifier):
    request["messages"] = [m for m in request["messages"]
        if m.get("tool_call_id") != identifier and not any(c["id"] == identifier for c in m.get("tool_calls", []))]


class ProductiveParentChildControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="productive-catalog-control-")
        self.addCleanup(self.temp.cleanup)
        self.args = evidence(Path(self.temp.name))

    def test_exact_sequential_catalogs_pass_with_actual_artifact_bytes(self):
        result = verify(*self.args)
        self.assertEqual((65, 35), (result["parent_feedback_rounds"], result["child_feedback_rounds"]))
        self.assertTrue(result["passed"])

    def test_missing_parent_or_child_round_cannot_use_the_other_actors_count(self):
        for index, identifier in ((0, "parent-64"), (1, "child-34")):
            with self.subTest(actor=index):
                args = copy.deepcopy(self.args)
                remove_pair(args[3][index], identifier)
                with self.assertRaisesRegex(AssertionError, "required successful catalog feedback"):
                    verify(*args)

    def test_parent_batch_guess_fails_even_with_exact_values_and_successes(self):
        setup, receipt, events, requests, home = copy.deepcopy(self.args)
        call1, result1, call2, result2 = events[2:6]
        events[2:6] = [call1, call2, result1, result2]
        resequence(events)
        with self.assertRaisesRegex(AssertionError, "ordered successful DTO"):
            verify(setup, receipt, events, requests, home)

    def test_child_batch_guess_fails_even_with_exact_values_and_successes(self):
        args = copy.deepcopy(self.args)
        messages = args[3][1]["messages"]
        messages[2]["tool_calls"].extend(messages[4]["tool_calls"])
        del messages[4]
        with self.assertRaisesRegex(AssertionError, "preceding actual result feedback"):
            verify(*args)

    def test_wrong_actual_values_or_order_fail_independent_of_final_counts(self):
        setup, receipt, events, requests, home = self.args
        path = actual_file(home, setup["combined_output"])
        original = path.read_bytes()
        for fault in ("value", "order"):
            value = json.loads(original)
            if fault == "value":
                value["parent_records"][0]["value"] = "fabricated"
            else:
                value["child_records"].reverse()
            path.write_text(encode(value))
            with self.subTest(fault=fault), self.assertRaisesRegex(AssertionError, "wrong values or order"):
                verify(*self.args)
        path.write_bytes(original)
        self.assertTrue(verify(*self.args)["passed"])

    def test_fabricated_provider_read_or_write_receipt_fails(self):
        for identifier in ("child-0", "child-write", "parent-0"):
            args = copy.deepcopy(self.args)
            for request in args[3]:
                for message in request["messages"]:
                    if message.get("tool_call_id") == identifier:
                        message["content"] = "Success: fabricated"
            with self.subTest(identifier=identifier), self.assertRaises(AssertionError):
                verify(*args)

    def test_provider_value_and_parent_dto_must_match(self):
        args = copy.deepcopy(self.args)
        args[2][3]["output"]["Result"] = "fabricated"
        with self.assertRaisesRegex(AssertionError, "ordered successful DTO"):
            verify(*args)

    def test_parent_cannot_skip_full_child_artifact_read(self):
        args = copy.deepcopy(self.args)
        remove_pair(args[3][0], "review")
        args[2][:] = [e for e in args[2] if e["output"].get("CallId") != "review"]
        resequence(args[2])
        with self.assertRaisesRegex(AssertionError, "never fully reads"):
            verify(*args)

    def test_child_artifact_review_before_terminal_is_not_enough(self):
        args = copy.deepcopy(self.args)
        messages = args[3][0]["messages"]
        terminal_messages = messages[-6:-4]
        del messages[-6:-4]
        messages.extend(terminal_messages)
        with self.assertRaisesRegex(AssertionError, "never fully reads"):
            verify(*args)

    def test_foreign_child_context_and_wrong_profile_fail(self):
        args = copy.deepcopy(self.args)
        args[3][1]["messages"][1]["content"] = args[3][1]["messages"][1]["content"].replace("/neutral/", "/foreign/")
        with self.assertRaisesRegex(AssertionError, "attributed provider"):
            verify(*args)
        args = copy.deepcopy(self.args)
        args[2][0]["output"]["ArgumentsJson"] = args[2][0]["output"]["ArgumentsJson"].replace("task-worker", "other-profile")
        with self.assertRaisesRegex(AssertionError, "task-worker"):
            verify(*args)

    def test_no_shell_directory_discovery_or_outside_writes(self):
        for owner in (0, 1):
            for name, args in (("shell_execute", {"Command": "cat *"}), ("file_list", {"Path": "/"}),
                               ("file_write", {"Path": "/outside", "Content": "x"})):
                values = copy.deepcopy(self.args)
                values[3][owner]["messages"].append({"role": "assistant", "tool_calls": [{"id": "forbidden", "function": {
                    "name": name, "arguments": json.dumps(args)}}]})
                with self.subTest(owner=owner, name=name), self.assertRaises(AssertionError):
                    verify(*values)

    def test_completed_call_id_reuse_and_repeated_cumulative_capture_pass(self):
        args = copy.deepcopy(self.args)
        for request in args[3]:
            for message in request["messages"]:
                for call in message.get("tool_calls", []):
                    if call["function"]["name"] == "file_read":
                        call["id"] = "reused-read"
                if message.get("tool_call_id", "").removeprefix("parent-").isdigit() or message.get("tool_call_id", "").removeprefix("child-").isdigit() or message.get("tool_call_id") == "review":
                    message["tool_call_id"] = "reused-read"
        for event in args[2]:
            if event["output"].get("ToolName") == "file_read":
                event["output"]["CallId"] = "reused-read"
        args[3].extend(copy.deepcopy(args[3]))
        self.assertTrue(verify(*args)["passed"])

    def test_fabricated_start_acceptance_and_changed_source_fail(self):
        args = copy.deepcopy(self.args)
        args[3][0]["messages"][2]["content"] = json.dumps({**ACCEPTED, "run_id": "foreign-run"})
        with self.assertRaisesRegex(AssertionError, "paired provider acceptance"):
            verify(*args)
        setup, _, _, _, home = self.args
        path = actual_file(home, setup["chains"]["child"][0]["path"])
        path.write_text("changed source")
        with self.assertRaisesRegex(AssertionError, "source record changed"):
            verify(*self.args)

    def test_same_completed_child_read_occurrence_cannot_repeat_inside_one_history(self):
        args = copy.deepcopy(self.args)
        messages = args[3][1]["messages"]
        messages[4:4] = copy.deepcopy(messages[2:4])
        with self.assertRaisesRegex(AssertionError, "record repeats within one provider history"):
            verify(*args)

    def test_same_child_write_occurrence_cannot_repeat_inside_one_history(self):
        args = copy.deepcopy(self.args)
        messages = args[3][1]["messages"]
        messages.extend(copy.deepcopy(messages[-2:]))
        with self.assertRaisesRegex(AssertionError, "output write repeats within one provider history"):
            verify(*args)

    def test_source_newline_bytes_cannot_change(self):
        setup, _, _, _, home = self.args
        path = actual_file(home, setup["chains"]["child"][0]["path"])
        path.write_bytes(path.read_bytes().replace(b"\n", b"\r\n"))
        with self.assertRaisesRegex(AssertionError, "source record changed"):
            verify(*self.args)

    def test_provider_comparison_omits_only_runtime_hints(self):
        args = copy.deepcopy(self.args)
        for event in args[2]:
            dto = event["output"]
            if dto["Type"] == "tool_call":
                arguments = json.loads(dto["ArgumentsJson"])
                arguments.update(_timeout_seconds=30, _background=False)
                dto["ArgumentsJson"] = json.dumps(arguments)
        self.assertTrue(verify(*args)["passed"])
        dto = args[2][0]["output"]
        arguments = json.loads(dto["ArgumentsJson"])
        arguments["_rationale"] = "changed rationale"
        dto["ArgumentsJson"] = json.dumps(arguments)
        with self.assertRaisesRegex(AssertionError, "paired provider acceptance"):
            verify(*args)

    def test_unresolved_call_id_overlap_fails(self):
        args = copy.deepcopy(self.args)
        args[3][1]["messages"].insert(3, copy.deepcopy(args[3][1]["messages"][2]))
        with self.assertRaisesRegex(AssertionError, "unresolved provider call"):
            verify(*args)

    def test_setup_exposes_only_entry_paths_without_expected_output_or_truth(self):
        setup, _, _, _, home = self.args
        text = prompt(setup)
        paths = [row["path"] for owner in ("parent", "child") for row in setup["chains"][owner]]
        self.assertEqual(100, len(set(paths)))
        for owner in ("parent", "child"):
            self.assertIn(setup["chains"][owner][0]["path"], text)
            for row in setup["chains"][owner][1:]:
                self.assertNotIn(row["path"], text)
                self.assertNotIn(row["content"], text)
        self.assertFalse(list(Path(home).rglob("setup.json")))
        fresh = prepare(home, Path(self.temp.name) / "truth2")
        self.assertNotEqual(setup["nonce"], fresh["nonce"])
        self.assertFalse(actual_file(home, fresh["combined_output"]).exists())

    def test_verify_cli_archives_actual_wrong_output_before_failure(self):
        setup, receipt, events, requests, home = self.args
        root = Path(self.temp.name)
        observer, relay = root / "observer", root / "relay"
        observer.mkdir()
        relay.mkdir()
        data = {"Mode": "collect", "Nonce": "observer-neutral", "InitialPrompt": prompt(setup),
                "SessionId": receipt["session_id"]}
        receipt.update(status="observed", observer_mode="collect", prompt_nonce=data["Nonce"],
                       initial_prompt_sha256=hashlib.sha256(data["InitialPrompt"].encode()).hexdigest(),
                       case=CASE, prompt_ordinal=1)
        (observer / "observer-input.json").write_text(json.dumps(data))
        (observer / "verified-receipt.json").write_text(json.dumps(receipt))
        (observer / "session-output.jsonl").write_text("\n".join(json.dumps(e) for e in events))
        for index, request in enumerate(requests, 1):
            (relay / f"request-{index:04}.json").write_text(json.dumps(request))
        wrong = b'{"actual":"wrong catalog"}\n'
        actual_file(home, setup["combined_output"]).write_bytes(wrong)
        result = subprocess.run([sys.executable, "-B", str(ROOT / "evals/productive_parent_child_evals.py"), "verify",
                                 "--eval-home", str(home), "--evidence", str(root / "truth"),
                                 "--observer-directory", str(observer), "--relay-directory", str(relay)],
                                capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertIn("combined catalog has wrong values or order", result.stderr)
        self.assertEqual(wrong, (observer / "combined-catalog.json").read_bytes())
        capture = json.loads((observer / "productive-output-capture.json").read_text())
        self.assertEqual(hashlib.sha256(wrong).hexdigest(), capture["combined_output"]["sha256"])
        self.assertEqual("captured", capture["child_output"]["state"])

    def test_output_capture_records_absence_and_rejects_links_without_outside_read(self):
        setup, _, _, _, home = self.args
        root = Path(self.temp.name)
        child_path = actual_file(home, setup["child_output"])
        child_path.unlink()
        archive = root / "capture"
        archive.mkdir()
        result = archive_outputs(setup, home, archive)
        self.assertEqual("missing", result["child_output"]["state"])
        self.assertFalse((archive / "child-catalog.json").exists())
        marker = root / "outside"
        marker.write_bytes(b"outside marker")
        child_path.symlink_to(marker)
        linked = root / "linked"
        linked.mkdir()
        with self.assertRaisesRegex(AssertionError, "link escapes the eval-owned home"):
            archive_outputs(setup, home, linked)
        self.assertEqual(b"outside marker", marker.read_bytes())
        self.assertEqual([], list(linked.iterdir()))

    def test_selection_uses_existing_collect_and_excludes_default(self):
        self.assertEqual("collect", legacy_observer_mode(CASE, 1))
        for ordinal in (0, 2, True):
            with self.subTest(ordinal=ordinal), self.assertRaises(AssertionError):
                legacy_observer_mode(CASE, ordinal)
        functions = shell_functions("child_result_consumer", "run_all")
        script = functions + '''
print_category() { :; }
end_category() { :; }
run_multi_turn_case() { :; }
run_case() {
    [[ "${1:-}" != --json ]] || shift
    [[ "$1" != productive_parent_child ]] || printf '%s\\n' "$1"
}
FILTER_CASE="$1"; FILTER_CATEGORY=""; run_all
'''
        for case, expected in ((CASE, CASE + "\n"), ("", ""), ("coordination_analyze_plan", "")):
            result = subprocess.run(["bash", "-c", script, "control", case], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(expected, result.stdout)
        result = subprocess.run(["bash", "-c", shell_functions("child_result_consumer") +
                                 '\nFILTER_CASE=productive_parent_child; child_result_consumer'], capture_output=True)
        self.assertEqual(0, result.returncode)


if __name__ == "__main__":
    unittest.main()
