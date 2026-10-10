"""Adverse controls for real-file report copies and parent audit evidence."""

import copy
import json
import hashlib
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from child_run_evals import REQUIRED_RATIONALE_ERROR, actual_file, legacy_observer_mode
from coordination_stale_incomplete_evals import CASE, FIXTURE, main, prepare, prompt, report_gaps, sha, verify
from test_child_run_evals import ACCEPTED, child, parent, terminal
from test_coordination_workflow_evals import shell_functions

ROOT = Path(__file__).resolve().parents[1]


def evidence(root, valid=False):
    home = root / "home"
    setup = prepare(home, root / "evidence")
    if valid:
        content = b"Declared status: complete\n\n" + (FIXTURE / "artifacts/plan-complete.md").read_bytes()
        for row in setup["reports"].values():
            actual_file(home, row["input"]).write_bytes(content)
            setup["input_hashes"][row["input"]] = sha(content)
    receipt = {"session_id": "session-neutral", "accepted_runs": [ACCEPTED], "verified_deliveries": [],
               "last_reply": ""}
    p = {"messages": [{"role": "system", "content": "[available-subagents]\n## task-worker\nExecute scoped tasks"},
                      {"role": "user", "content": prompt(setup)}]}
    c = child()
    assignment = setup["nonce"] + "\n" + "\n".join(path for row in setup["reports"].values() for path in row.values())
    c["messages"][1]["content"] += "\n" + assignment
    events = []

    def pair(request, name, args, result, identifier, dto=False, failure=None):
        request["messages"].extend([
            {"role": "assistant", "tool_calls": [{"id": identifier, "function": {"name": name, "arguments": json.dumps(args)}}]},
            {"role": "tool", "tool_call_id": identifier, "content": result}])
        if dto:
            for value in ({"Type": "tool_call", "CallId": identifier, "ToolName": name, "ArgumentsJson": json.dumps(args)},
                          {"Type": "tool_result", "CallId": identifier, "ToolName": name, "Result": result, "ToolFailureCode": failure}):
                events.append({"sequence": len(events) + 1, "observed_ns": len(events) + 1,
                               "output": {**value, "SessionId": receipt["session_id"]}})
    pair(p, "spawn_agent", {"Agent": "task-worker", "Task": assignment}, json.dumps(ACCEPTED), "start-neutral", True)
    for key, row in setup["reports"].items():
        content = actual_file(home, row["input"]).read_bytes()
        actual_file(home, row["output"]).write_bytes(content)
        pair(c, "file_read", {"Path": row["input"]}, content.decode(), "input-" + key)
        pair(c, "file_write", {"Path": row["output"], "Content": content.decode()},
             f"Successfully wrote {len(content)} bytes to {row['output']}", "write-" + key)
    t = {**terminal(), "output": "Both report copies exist. This proves the copy task only."}
    receipt["verified_deliveries"] = [{"accepted": ACCEPTED, "terminal": t}]
    p["messages"].extend(parent(t)["messages"])
    for key, path in {"source": setup["source"], "findings": setup["findings"],
                      **{k: row["output"] for k, row in setup["reports"].items()}}.items():
        pair(p, "file_read", {"Path": path}, actual_file(home, path).read_bytes().decode(), "review-" + key, True)
    reports = {}
    for key, row in setup["reports"].items():
        gaps = report_gaps(actual_file(home, row["output"]).read_bytes().decode(), setup["revision"])
        reports[key] = {"path": row["output"], "declared_status": "complete", "accepted": not gaps, "gaps": gaps}
    receipt["last_reply"] = json.dumps({"nonce": setup["nonce"], "source_revision": setup["revision"], "reports": reports})
    return setup, receipt, events, [p, c], home


class StaleIncompleteControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.args = evidence(Path(self.temp.name))

    def reject(self, args=None):
        with self.assertRaises((AssertionError, ValueError)):
            verify(*(args or self.args))

    def test_actual_stale_and_placeholder_copies_require_both_precise_gaps(self):
        result = verify(*self.args)
        self.assertTrue(result["passed"])
        self.assertEqual("stale_revision", result["reports"]["revision"]["gaps"][0]["kind"])
        self.assertEqual("A1", result["reports"]["template"]["gaps"][0]["step"])
        self.assertFalse(result["reports"]["revision"]["accepted"])
        self.assertFalse(result["reports"]["template"]["accepted"])

    def test_current_complete_report_controls_require_acceptance(self):
        args = evidence(Path(self.temp.name) / "valid", valid=True)
        result = verify(*args)
        self.assertTrue(all(row["accepted"] and not row["gaps"] for row in result["reports"].values()))
        report = json.loads(args[1]["last_reply"])
        report["reports"]["revision"]["accepted"] = False
        args[1]["last_reply"] = json.dumps(report)
        self.reject(args)

    def test_numeric_acceptance_values_cannot_replace_actual_json_booleans(self):
        valid = evidence(Path(self.temp.name) / "boolean-valid", valid=True)
        for original, numeric in ((self.args, 0), (valid, 1)):
            self.assertTrue(verify(*original)["passed"])
            for key in original[0]["reports"]:
                args = copy.deepcopy(original)
                answer = json.loads(args[1]["last_reply"])
                answer["reports"][key]["accepted"] = numeric
                args[1]["last_reply"] = json.dumps(answer)
                with self.subTest(key=key, numeric=numeric):
                    self.reject(args)

    def test_completion_declaration_cannot_replace_audit(self):
        setup, receipt, _, _, _ = self.args
        report = json.loads(receipt["last_reply"])
        for row in report["reports"].values():
            row.update(accepted=True, gaps=[])
        receipt["last_reply"] = json.dumps(report)
        self.reject()

    def test_wrong_revision_field_or_missing_gap_fails(self):
        for key in ("revision", "template"):
            for mutation in ("field", "observed", "expected", "absent"):
                args = copy.deepcopy(self.args)
                report = json.loads(args[1]["last_reply"])
                row = report["reports"][key]
                if mutation == "absent":
                    row["gaps"] = []
                else:
                    row["gaps"][0][mutation] = "incorrect"
                args[1]["last_reply"] = json.dumps(report)
                with self.subTest(key=key, mutation=mutation):
                    self.reject(args)

    def test_each_actual_parent_full_read_is_required(self):
        for key in ("source", "findings", "revision", "template"):
            args = copy.deepcopy(self.args)
            for m in args[3][0]["messages"]:
                if m.get("tool_call_id") == "review-" + key:
                    m["content"] = "A summary replaces the full file."
            for e in args[2]:
                if e["output"].get("CallId") == "review-" + key and e["output"]["Type"] == "tool_result":
                    e["output"]["Result"] = "A summary replaces the full file."
            with self.subTest(key=key):
                self.reject(args)

    def test_reads_before_terminal_do_not_prove_post_terminal_review(self):
        args = copy.deepcopy(self.args)
        messages = args[3][0]["messages"]
        # Move the actual attributed terminal pair after all report reads.
        terminal_index = next(i for i, m in enumerate(messages) if m.get("tool_call_id") == "delivery-neutral")
        pair = messages[terminal_index - 1:terminal_index + 1]
        del messages[terminal_index - 1:terminal_index + 1]
        messages.extend(pair)
        self.reject(args)

    def test_child_full_read_and_actual_write_receipts_are_required(self):
        for identifier in ("input-revision", "write-revision", "input-template", "write-template"):
            args = copy.deepcopy(self.args)
            for m in args[3][1]["messages"]:
                if m.get("tool_call_id") == identifier:
                    m["content"] = "Fabricated success"
            with self.subTest(identifier=identifier):
                self.reject(args)

    def test_child_write_before_actual_input_receipt_fails(self):
        args = copy.deepcopy(self.args)
        messages = args[3][1]["messages"]
        messages[2:6] = messages[4:6] + messages[2:4]
        self.reject(args)

    def test_original_inputs_and_actual_output_bytes_must_stay_exact(self):
        setup, _, _, _, home = self.args
        paths = [*setup["input_hashes"], *[r["output"] for r in setup["reports"].values()]]
        for path in paths:
            actual = actual_file(home, path);original = actual.read_bytes()
            try:
                actual.write_bytes(original + b"changed\n")
                with self.subTest(path=path):
                    self.reject()
            finally:
                actual.write_bytes(original)

    def test_foreign_missing_failed_or_duplicate_terminal_fails(self):
        for mutation in ("foreign", "missing", "failed", "duplicate"):
            args = copy.deepcopy(self.args)
            if mutation == "missing":
                args[1]["verified_deliveries"] = []
            elif mutation == "duplicate":
                args[1]["verified_deliveries"] *= 2
            elif mutation == "foreign":
                args[1]["verified_deliveries"][0]["terminal"]["run_id"] = "foreign"
            else:
                t = args[1]["verified_deliveries"][0]["terminal"]
                t.update(state="Lost", outcome="Failed")
                args[3][0]["messages"][5]["content"] = json.dumps(t)
            with self.subTest(mutation=mutation):
                self.reject(args)

    def test_extra_child_wrong_profile_and_foreign_parent_fail(self):
        for mutation in ("extra", "profile", "parent"):
            args = copy.deepcopy(self.args)
            if mutation == "extra":
                args[1]["accepted_runs"] *= 2
            elif mutation == "parent":
                args[2][0]["output"]["SessionId"] = "foreign-owner"
            else:
                args[2][0]["output"]["ArgumentsJson"] = args[2][0]["output"]["ArgumentsJson"].replace("task-worker", "summary")
            with self.subTest(mutation=mutation):
                self.reject(args)

    def test_parent_and_child_cannot_repair_even_with_final_bytes_restored(self):
        for index in (0, 1):
            for tool in ("shell_execute", "file_edit", "file_write"):
                args = copy.deepcopy(self.args)
                path = args[0]["source"]
                args[3][index]["messages"].extend([
                    {"role": "assistant", "tool_calls": [{"id": "forbidden", "function": {
                        "name": tool, "arguments": json.dumps({"Path": path, "Content": "temporary", "Command": "true"})}}]},
                    {"role": "tool", "tool_call_id": "forbidden", "content": "Succeeded"}])
                with self.subTest(index=index, tool=tool):
                    self.reject(args)

    def test_cumulative_capture_and_case_insensitive_profile_remain_valid(self):
        args = copy.deepcopy(self.args)
        args[3].append(copy.deepcopy(args[3][0]))
        args[3].append(copy.deepcopy(args[3][1]))
        for event in args[2]:
            if event["output"].get("ArgumentsJson"):
                event["output"]["ArgumentsJson"] = event["output"]["ArgumentsJson"].replace('"task-worker"', '"TASK-WORKER"')
        for request in args[3]:
            for m in request["messages"]:
                for call in m.get("tool_calls", []):
                    call["function"]["arguments"] = call["function"]["arguments"].replace('"task-worker"', '"TASK-WORKER"')
        self.assertTrue(verify(*args)["passed"])

    def test_exact_unexecuted_rationale_attempt_keeps_one_accepted_child(self):
        args = copy.deepcopy(self.args)
        call_args = {"Agent": "task-worker", "Task": "The original copy task.", "_rationale": ""}
        pair = [
            {"role": "assistant", "tool_calls": [{"id": "rejected-start", "function": {
                "name": "spawn_agent", "arguments": json.dumps(call_args)}}]},
            {"role": "tool", "tool_call_id": "rejected-start", "content": REQUIRED_RATIONALE_ERROR}]
        args[3][0]["messages"][2:2] = pair
        dtos = [
            {"Type": "tool_call", "CallId": "rejected-start", "ToolName": "spawn_agent", "ArgumentsJson": json.dumps(call_args)},
            {"Type": "tool_result", "CallId": "rejected-start", "ToolName": "spawn_agent", "Result": REQUIRED_RATIONALE_ERROR,
             "ToolFailureCode": "invalid_rationale"}]
        args[2][:0] = [{"output": {**d, "SessionId": args[1]["session_id"]}} for d in dtos]
        for index, event in enumerate(args[2], 1):
            event.update(sequence=index, observed_ns=index)
        self.assertTrue(verify(*args)["passed"])
        args[2][1]["output"]["ToolFailureCode"] = "authorization_denied"
        self.reject(args)
        args[2][1]["output"]["ToolFailureCode"] = "invalid_rationale"
        args[3][0]["messages"][3]["content"] = "Different rejection"
        self.reject(args)

    def test_actual_parent_dto_and_provider_read_pairs_are_both_required(self):
        args = copy.deepcopy(self.args)
        args[2][:] = [event for event in args[2] if event["output"].get("CallId") != "review-template"]
        for index, event in enumerate(args[2], 1):
            event.update(sequence=index, observed_ns=index)
        self.reject(args)
        args = copy.deepcopy(self.args)
        for message in args[3][0]["messages"]:
            if message.get("tool_call_id") == "review-template":
                message["content"] = "Different actual bytes"
        self.reject(args)

    def test_verify_cli_failure_archives_wrong_actual_bytes_before_verdict(self):
        setup, receipt, events, requests, home = self.args
        root = Path(self.temp.name)
        observer, relay = root / "observer", root / "relay"
        observer.mkdir();relay.mkdir()
        data = {"Mode": "collect", "InitialPrompt": prompt(setup), "Nonce": "neutral-observer-nonce", "SessionId": receipt["session_id"]}
        receipt.update(status="observed", observer_mode="collect", case=CASE, prompt_ordinal=1, prompt_nonce=data["Nonce"],
                       initial_prompt_sha256=hashlib.sha256(data["InitialPrompt"].encode()).hexdigest())
        for name, value in (("observer-input.json", data), ("verified-receipt.json", receipt)):
            (observer / name).write_text(json.dumps(value))
        (observer / "session-output.jsonl").write_text("\n".join(json.dumps(e) for e in events))
        for n, request in enumerate(requests, 1):
            (relay / f"request-{n:04}.json").write_text(json.dumps(request))
        output = actual_file(home, setup["reports"]["revision"]["output"])
        wrong = b"Actual wrong report bytes.\n"
        output.write_bytes(wrong)
        argv = ["eval", "verify", "--eval-home", str(home), "--evidence", str(root / "evidence"),
                "--observer-directory", str(observer), "--relay-directory", str(relay)]
        with patch("sys.argv", argv), self.assertRaises(AssertionError):
            main()
        self.assertEqual(wrong, (observer / "revision.md").read_bytes())
        captured = json.loads((observer / "report-copy-artifacts.json").read_text())
        self.assertEqual({"state": "captured", "sha256": sha(wrong)}, captured["revision"])

    def add_declaration(self, args, actor, path, result):
        index = 0 if actor == "parent" else 1
        identifier = "declare-" + actor
        arguments = {"Path": path}
        args[3][index]["messages"][2:2] = [
            {"role": "assistant", "tool_calls": [{"id": identifier, "function": {
                "name": "set_working_directory", "arguments": json.dumps(arguments)}}]},
            {"role": "tool", "tool_call_id": identifier, "content": result}]
        if actor == "parent":
            dtos = [
                {"Type": "tool_call", "CallId": identifier, "ToolName": "set_working_directory", "ArgumentsJson": json.dumps(arguments)},
                {"Type": "tool_result", "CallId": identifier, "ToolName": "set_working_directory", "Result": result, "ToolFailureCode": None}]
            args[2][:0] = [{"output": {**d, "SessionId": args[1]["session_id"]}} for d in dtos]
            for n, event in enumerate(args[2], 1):
                event.update(sequence=n, observed_ns=n)

    def test_exact_named_workspace_declarations_preserve_both_report_verdicts(self):
        args = copy.deepcopy(self.args)
        for actor in ("parent", "child"):
            self.add_declaration(args, actor, args[0]["root"], args[0]["root"])
        self.assertTrue(verify(*args)["passed"])

    def test_wrong_root_failed_unpaired_and_foreign_declarations_fail(self):
        for actor in ("parent", "child"):
            for mutation in ("root", "failed", "unpaired", "foreign"):
                args = copy.deepcopy(self.args)
                root = args[0]["root"]
                path = root + "/source" if mutation == "root" else root
                self.add_declaration(args, actor, path, "Error: denied" if mutation == "failed" else path)
                if mutation == "unpaired":
                    messages = args[3][0 if actor == "parent" else 1]["messages"]
                    messages[:] = [m for m in messages if m.get("tool_call_id") != "declare-" + actor]
                if mutation == "foreign":
                    if actor == "parent":
                        for e in args[2][:2]:
                            e["output"]["CallId"] = "foreign-declaration"
                    else:
                        for m in args[3][1]["messages"]:
                            if m.get("tool_call_id") == "declare-child":
                                m["tool_call_id"] = "foreign-declaration"
                with self.subTest(actor=actor, mutation=mutation):
                    self.reject(args)

    def test_foreign_parent_read_call_and_result_ids_cannot_borrow_actual_receipts(self):
        args = copy.deepcopy(self.args)
        for event in args[2]:
            if event["output"].get("ToolName") == "file_read":
                event["output"]["CallId"] = "foreign-" + event["output"]["CallId"]
        self.reject(args)

    def test_completed_read_id_reuse_and_cumulative_history_remain_valid(self):
        args = copy.deepcopy(self.args)
        for event in args[2]:
            if event["output"].get("CallId") == "review-findings":
                event["output"]["CallId"] = "review-source"
        for message in args[3][0]["messages"]:
            for call in message.get("tool_calls", []):
                if call["id"] == "review-findings":
                    call["id"] = "review-source"
            if message.get("tool_call_id") == "review-findings":
                message["tool_call_id"] = "review-source"
        args[3].append(copy.deepcopy(args[3][0]))
        self.assertTrue(verify(*args)["passed"])

    def test_actual_selection_uses_existing_collect_only_when_explicit(self):
        self.assertEqual("collect", legacy_observer_mode(CASE, 1))
        with self.assertRaises(AssertionError):
            legacy_observer_mode(CASE, 2)
        source = shell_functions("child_result_consumer", "run_all")
        script = source + '''
print_category() { :; }; end_category() { :; }; run_multi_turn_case() { :; }
run_case() { [[ "${1:-}" != --json ]] || shift; [[ "$1" != coordination_stale_incomplete ]] || printf '%s\\n' "$1"; }
FILTER_CASE="$1"; FILTER_CATEGORY=""; run_all
'''
        for selected, expected in ((CASE, CASE + "\n"), ("", ""), ("coordination_analyze_plan", "")):
            run = subprocess.run(["bash", "-c", script, "control", selected], capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stderr)
            self.assertEqual(expected, run.stdout)
        run = subprocess.run(["bash", "-c", shell_functions("child_result_consumer") + '\nFILTER_CASE="$1"; child_result_consumer', "control", CASE])
        self.assertEqual(0, run.returncode)
