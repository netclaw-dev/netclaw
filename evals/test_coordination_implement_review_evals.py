"""Behavioral controls for an isolated code candidate and independent review."""

import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from child_run_evals import REQUIRED_RATIONALE_ERROR, actual_file, legacy_observer_mode
from coordination_implement_review_evals import (CASE, FIXTURE, checkout_snapshot, commands, git,
                                                initialization_script, main, prepare, prompt, run_check, verify)
from test_child_run_evals import child, parent, terminal, ROOT as CHILD_ROOT
from test_coordination_workflow_evals import shell_functions

ROOT = Path(__file__).resolve().parents[1]


def evidence(root, line_ending="\n"):
    home = root / "home"
    setup = prepare(home, root / "evidence", lambda runtime: subprocess.run(
        ["bash", "-s"], input=initialization_script(str(actual_file(home, runtime))), capture_output=True, text=True))
    source = actual_file(home, setup["worker"] + "/source/catalog.py")
    content = source.read_text().replace("        self.records = candidate\n        self._validate_unique(candidate)",
                                         "        self._validate_unique(candidate)\n        self.records = candidate")
    content = content.replace("\n", line_ending)
    source.write_bytes(content.encode())
    git(home, setup, "worker", "add", "source/catalog.py")
    commit_output = git(home, setup, "worker", "commit", "-m", "Fix catalog publication").decode()
    checked = run_check(home, setup)
    assert checked.returncode == 0, checked.stderr
    check = json.loads(checked.stdout)
    reports = {"worker": {**check, "changed_files": ["source/catalog.py"]}, "review": {**check, "findings": []}}
    report_text = {s: json.dumps(r, sort_keys=True) + "\n" for s, r in reports.items()}
    for stage, text in report_text.items():
        actual_file(home, setup[stage + "_report"]).write_text(text)
    receipt = {"session_id": "session-neutral", "accepted_runs": [], "verified_deliveries": [],
               "delivery_observations": {"complete": True}, "last_reply": json.dumps({
                   "candidate_commit": check["candidate_commit"], "source_sha256": check["source_sha256"],
                   "worker_report": setup["worker_report"], "review_report": setup["review_report"],
                   "unresolved_findings": [], "integration": "not_performed"})}
    parent_request = {"messages": [{"role": "system", "content":
        "[available-subagents]\n## task-worker\nExecute scoped code tasks\n## code-analyst\nRead and review source"},
        {"role": "user", "content": prompt(setup)}]}
    events, children = [], []

    def pair(request, name, args, result, identifier, dto=False):
        request["messages"].extend([
            {"role": "assistant", "tool_calls": [{"id": identifier, "function": {"name": name, "arguments": json.dumps(args)}}]},
            {"role": "tool", "tool_call_id": identifier, "content": result}])
        if dto:
            for output in ({"Type": "tool_call", "CallId": identifier, "ToolName": name, "ArgumentsJson": json.dumps(args)},
                           {"Type": "tool_result", "CallId": identifier, "ToolName": name, "Result": result, "ToolFailureCode": None}):
                n = len(events) + 1
                events.append({"sequence": n, "observed_ns": n, "output": {**output, "SessionId": "session-neutral"}})

    def parent_reads(suffix):
        pair(parent_request, "file_read", {"Path": setup["worker"] + "/source/catalog.py"}, content, "source-" + suffix, True)
        for key, result in (("revision", check["candidate_commit"] + "\n"),
                            ("diff", git(home, setup, "worker", "diff", setup["base_commit"], "HEAD", "--", "source/catalog.py").decode())):
            pair(parent_request, "shell_execute", {"Command": commands(setup)[key]}, "Exit code: 0\n" + result, key + "-" + suffix, True)

    for stage, agent in (("worker", "task-worker"), ("review", "code-analyst")):
        accepted = {"run_id": "run-" + stage, "scope_id": "scope-" + stage, "state": "Accepted", "control_tool": "check_agent_run"}
        receipt["accepted_runs"].append(accepted)
        assignment = " ".join([setup["nonce"], setup["worker"], setup[stage + "_report"], setup["base_commit"],
                               check["candidate_commit"], check["source_sha256"], commands(setup)["check"]])
        pair(parent_request, "spawn_agent", {"Agent": agent, "Task": assignment}, json.dumps(accepted), "start-" + stage, True)
        child_request = child()
        runtime_root = CHILD_ROOT + "-" + stage
        child_request["messages"][1]["content"] = child_request["messages"][1]["content"].replace(CHILD_ROOT, runtime_root) + "\n" + assignment
        if stage == "worker":
            path = setup["worker"] + "/source/catalog.py"
            pair(child_request, "file_write", {"Path": path, "Content": content}, f"Successfully wrote {len(content.encode())} bytes to {path}", "source-write")
            pair(child_request, "shell_execute", {"Command": commands(setup)["commit"]}, "Exit code: 0\n" + commit_output, "commit-worker")
        else:
            pair(child_request, "file_read", {"Path": setup["worker"] + "/source/catalog.py"}, content, "review-source")
        pair(child_request, "shell_execute", {"Command": commands(setup)["check"]}, "Exit code: 0\n" + checked.stdout, "check-" + stage)
        report_path = setup[stage + "_report"]
        pair(child_request, "file_write", {"Path": report_path, "Content": report_text[stage]},
             f"Successfully wrote {len(report_text[stage].encode())} bytes to {report_path}", "report-" + stage)
        children.append(child_request)
        result = {**terminal(), "run_id": accepted["run_id"], "scope_id": accepted["scope_id"],
                  "log_path": runtime_root + "/logs/session.log", "artifact_directory": runtime_root + "/artifacts"}
        receipt["verified_deliveries"].append({"accepted": accepted, "terminal": result})
        parent_request["messages"].extend(parent(result, identifier="delivery-" + stage)["messages"])
        # The canonical terminal request contains the accepted run in its synthetic arguments.
        parent_request["messages"][-2]["tool_calls"][0]["function"]["arguments"] = json.dumps({"run_id": accepted["run_id"], "source_operation": "spawn_agent"})
        pair(parent_request, "file_read", {"Path": report_path}, report_text[stage], "read-" + stage, True)
        parent_reads(stage)
    return setup, receipt, events, [parent_request, *children], home, lambda: run_check(home, setup)


class ImplementReviewControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.args = evidence(Path(self.temp.name))

    def reject(self, args=None):
        with self.assertRaises((AssertionError, ValueError, KeyError)):
            verify(*(args or self.args))

    def test_actual_baseline_fails_and_committed_candidate_passes(self):
        result = verify(*self.args)
        self.assertTrue(result["passed"])
        self.assertTrue(result["operator_preserved"])
        self.assertEqual(2, len(result["accepted_runs"]))
        self.assertIn("A rejected refresh must preserve the prior records.",
                      (Path(self.temp.name) / "evidence/baseline-check.stderr").read_text())
        self.assertEqual(self.args[0]["operator_before"], checkout_snapshot(self.args[4], self.args[0]))

    def test_crlf_candidate_source_retains_exact_read_bytes(self):
        args = evidence(Path(self.temp.name) / "crlf", "\r\n")
        self.assertTrue(verify(*args)["passed"])

    def test_verify_cli_uses_actual_container_and_existing_eval_deadline(self):
        setup, receipt, events, requests, home, _ = self.args
        root = Path(self.temp.name)
        observer, relay = root / "observer", root / "relay"
        observer.mkdir();relay.mkdir()
        data = {"Mode": "collect", "InitialPrompt": prompt(setup), "Nonce": "neutral-prompt-nonce", "SessionId": receipt["session_id"]}
        import hashlib
        receipt.update(status="observed", observer_mode="collect", case=CASE, prompt_ordinal=1, prompt_nonce=data["Nonce"],
                       initial_prompt_sha256=hashlib.sha256(data["InitialPrompt"].encode()).hexdigest())
        for name, value in (("observer-input.json", data), ("verified-receipt.json", receipt)):
            (observer / name).write_text(json.dumps(value))
        (observer / "session-output.jsonl").write_text("\n".join(json.dumps(e) for e in events))
        for n, request in enumerate(requests, 1):
            (relay / f"request-{n:04}.json").write_text(json.dumps(request))
        original = subprocess.run
        captured = []
        def timed_out(argv, **kwargs):
            if argv[0] == "docker":
                captured.append((argv, kwargs))
                raise subprocess.TimeoutExpired(argv, kwargs["timeout"])
            return original(argv, **kwargs)
        argv = ["eval", "verify", "--eval-home", str(home), "--evidence", str(root / "evidence"),
                "--observer-directory", str(observer), "--relay-directory", str(relay), "--container", "owned-neutral-container"]
        with patch("sys.argv", argv), patch.dict(os.environ, {"PROMPT_TIMEOUT": "180"}), patch("subprocess.run", side_effect=timed_out):
            with self.assertRaises(subprocess.TimeoutExpired):
                main()
        self.assertEqual(1, len(captured))
        self.assertEqual(["docker", "exec", "--user", "netclaw", "owned-neutral-container", "python3", "-B",
                          setup["checker"], setup["worker"], setup["nonce"]], captured[0][0])
        self.assertEqual(180, captured[0][1]["timeout"])
        self.assertEqual(actual_file(home, setup["review_report"]).read_bytes(), (observer / "review.json").read_bytes())

    def test_every_operator_file_index_head_branch_and_untracked_marker_is_preserved(self):
        setup, _, _, _, home, _ = self.args
        operator = actual_file(home, setup["operator"])
        for name in ("operator.txt", "UNTRACKED.txt", "source/catalog.py", ".git/index", ".git/HEAD", ".git/config"):
            path = operator / name
            original = path.read_bytes()
            try:
                path.write_bytes(original + b"changed\n")
                self.reject()
            finally:
                path.write_bytes(original)
        (operator / "extra-operator-file").write_text("extra")
        self.reject()

    def test_changed_checker_and_extra_candidate_file_fail(self):
        setup, _, _, _, home, _ = self.args
        checker = actual_file(home, setup["checker"])
        original = checker.read_bytes()
        checker.write_bytes(original + b"\n# altered\n")
        self.reject()
        checker.write_bytes(original)
        path = actual_file(home, setup["worker"] + "/unrelated")
        path.write_text("not permitted")
        self.reject()

    def test_candidate_bad_semantics_fail_despite_repaired_reports(self):
        setup, _, _, _, home, _ = self.args
        source = actual_file(home, setup["worker"] + "/source/catalog.py")
        source.write_text(source.read_text().replace("return self.records", "return self.records[::-1]"))
        git(home, setup, "worker", "add", "source/catalog.py")
        git(home, setup, "worker", "commit", "--amend", "--no-edit")
        self.reject()

    def test_wrong_profile_missing_child_and_reused_run_fail(self):
        for mutation in ("profile", "missing", "same-run", "foreign-terminal"):
            args = copy.deepcopy(self.args)
            setup, receipt, events, requests, _, _ = args
            if mutation == "profile":
                for event in events:
                    dto = event["output"]
                    if dto.get("CallId") == "start-review" and dto["Type"] == "tool_call":
                        value = json.loads(dto["ArgumentsJson"]);value["Agent"] = "task-worker";dto["ArgumentsJson"] = json.dumps(value)
                for message in requests[0]["messages"]:
                    for call in message.get("tool_calls", []):
                        if call["id"] == "start-review":
                            value = json.loads(call["function"]["arguments"]);value["Agent"] = "task-worker";call["function"]["arguments"] = json.dumps(value)
            elif mutation == "missing":
                requests.pop()
            elif mutation == "same-run":
                receipt["accepted_runs"][1] = receipt["accepted_runs"][0]
            else:
                receipt["verified_deliveries"][1]["terminal"]["run_id"] = "foreign"
            with self.subTest(mutation=mutation):
                self.reject(args)

    def test_stale_report_revision_or_hash_and_hidden_finding_fail(self):
        setup, _, _, _, home, _ = self.args
        path = actual_file(home, setup["review_report"])
        original = path.read_bytes()
        report = json.loads(original)
        for key, value in (("candidate_commit", setup["base_commit"]), ("source_sha256", "0" * 64),
                           ("findings", [{"path": "source/catalog.py", "line": 1, "detail": "unresolved", "severity": "review"}])):
            path.write_text(json.dumps({**report, key: value}))
            with self.subTest(key=key):
                self.reject()
        path.write_bytes(original)

    def test_unresolved_findings_can_remain_when_exactly_reported(self):
        args = copy.deepcopy(self.args)
        setup, receipt, events, requests, home, _ = args
        path = actual_file(home, setup["review_report"])
        findings = [{"path": "source/catalog.py", "line": 1, "detail": "General review quality remains unproved.", "severity": "limit"}]
        original = path.read_text();report = json.loads(original);report["findings"] = findings
        updated = json.dumps(report, sort_keys=True) + "\n";path.write_text(updated)
        for event in events:
            if event["output"].get("Result") == original:
                event["output"]["Result"] = updated
        for request in requests:
            for message in request["messages"]:
                if message.get("content") == original:
                    message["content"] = updated
                for call in message.get("tool_calls", []):
                    function = call["function"];value = json.loads(function["arguments"])
                    if value.get("Content") == original:
                        value["Content"] = updated;function["arguments"] = json.dumps(value)
            for message in request["messages"]:
                if message.get("tool_call_id") == "report-review":
                    message["content"] = f"Successfully wrote {len(updated.encode())} bytes to {setup['review_report']}"
        reply = json.loads(receipt["last_reply"]);reply["unresolved_findings"] = findings;receipt["last_reply"] = json.dumps(reply)
        self.assertTrue(verify(*args)["passed"])

    def test_absent_full_parent_report_source_revision_or_diff_read_fails(self):
        for identifier in ("read-worker", "read-review", "source-worker", "source-review", "revision-worker", "revision-review", "diff-worker", "diff-review"):
            args = copy.deepcopy(self.args)
            for event in args[2]:
                if event["output"].get("CallId") == identifier and event["output"]["Type"] == "tool_result":
                    event["output"]["Result"] = "summary only"
            for message in args[3][0]["messages"]:
                if message.get("tool_call_id") == identifier:
                    message["content"] = "summary only"
            with self.subTest(identifier=identifier):
                self.reject(args)

    def test_fabricated_or_failed_child_checks_commit_write_and_edit_receipts_fail(self):
        for identifier in ("check-worker", "check-review", "commit-worker", "report-worker", "report-review", "source-write"):
            args = copy.deepcopy(self.args)
            for request in args[3][1:]:
                for message in request["messages"]:
                    if message.get("tool_call_id") == identifier:
                        message["content"] = "Exit code: 1\nfailed"
            with self.subTest(identifier=identifier):
                self.reject(args)

    def test_reviewer_source_write_and_parent_reset_cannot_hide_behind_final_bytes(self):
        for stage, name, value in ((2, "file_write", {"Path": self.args[0]["worker"] + "/source/catalog.py", "Content": "same bytes"}),
                                   (0, "shell_execute", {"Command": "git reset --hard"})):
            args = copy.deepcopy(self.args)
            args[3][stage]["messages"].append({"role": "assistant", "tool_calls": [{"id": "outside", "function": {
                "name": name, "arguments": json.dumps(value)}}]})
            self.reject(args)

    def test_visible_worker_source_edit_after_commit_fails_even_if_restored(self):
        args = copy.deepcopy(self.args)
        request = args[3][1]
        request["messages"].extend(copy.deepcopy(request["messages"][2:4]))
        self.reject(args)

    def test_actual_oracle_check_cannot_change_operator_or_checker_before_success(self):
        setup, _, _, _, home, original_check = self.args
        for path in (actual_file(home, setup["operator"]) / "UNTRACKED.txt", actual_file(home, setup["checker"])):
            original = path.read_bytes()
            def changed_check():
                result = original_check()
                path.write_bytes(original + b"changed by candidate check\n")
                return result
            try:
                self.reject((*self.args[:5], changed_check))
            finally:
                path.write_bytes(original)

    def test_actual_oracle_check_cannot_replace_candidate_head_after_valid_output(self):
        setup, _, _, _, home, original_check = self.args
        def changed_check():
            result = original_check()
            git(home, setup, "worker", "commit", "--allow-empty", "-m", "Unreviewed empty candidate commit")
            return result
        original_head = git(home, setup, "worker", "rev-parse", "HEAD")
        with self.assertRaisesRegex(AssertionError, "exact committed revision"):
            verify(*self.args[:5], changed_check)
        self.assertNotEqual(original_head, git(home, setup, "worker", "rev-parse", "HEAD"))

    def test_exact_unexecuted_rationale_attempt_retains_pair_and_all_other_failures_fail(self):
        args = copy.deepcopy(self.args)
        _, _, events, requests, _, _ = args
        arguments = {"Agent": "task-worker", "Task": "unexecuted metadata attempt"}
        outputs = [{"Type": "tool_call", "CallId": "rejected-start", "ToolName": "spawn_agent", "ArgumentsJson": json.dumps(arguments)},
                   {"Type": "tool_result", "CallId": "rejected-start", "ToolName": "spawn_agent", "Result": REQUIRED_RATIONALE_ERROR, "ToolFailureCode": "invalid_rationale"}]
        events[:0] = [{"output": {**out, "SessionId": "session-neutral"}} for out in outputs]
        for n, event in enumerate(events, 1):
            event.update(sequence=n, observed_ns=n)
        requests[0]["messages"][2:2] = [
            {"role": "assistant", "tool_calls": [{"id": "rejected-start", "function": {"name": "spawn_agent", "arguments": json.dumps(arguments)}}]},
            {"role": "tool", "tool_call_id": "rejected-start", "content": REQUIRED_RATIONALE_ERROR}]
        self.assertTrue(verify(*args)["passed"])
        for failure, result in (("invalid_rationale", "different text"), ("authorization_denied", REQUIRED_RATIONALE_ERROR), (None, REQUIRED_RATIONALE_ERROR)):
            changed = copy.deepcopy(args)
            changed[2][1]["output"].update(ToolFailureCode=failure, Result=result)
            changed[3][0]["messages"][3]["content"] = result
            with self.subTest(failure=failure, result=result):
                self.reject(changed)
        args[3][0]["messages"][3]["content"] = "forged provider result"
        self.reject(args)

    def test_terminal_must_precede_report_review(self):
        args = copy.deepcopy(self.args)
        messages = args[3][0]["messages"]
        terminal_pair = [m for m in messages if m.get("tool_call_id") == "delivery-worker" or any(c["id"] == "delivery-worker" for c in m.get("tool_calls", []))]
        for message in terminal_pair:
            messages.remove(message)
        messages.extend(terminal_pair)
        self.reject(args)

    def test_runtime_hints_and_completed_call_id_reuse_remain_valid(self):
        args = copy.deepcopy(self.args)
        for event in args[2]:
            dto = event["output"]
            if dto["Type"] == "tool_call":
                value = json.loads(dto["ArgumentsJson"]);value.update(_timeout_seconds=30, _background=False);dto["ArgumentsJson"] = json.dumps(value)
            if dto.get("CallId") == "start-review":
                dto["CallId"] = "start-worker"
        for message in args[3][0]["messages"]:
            for call in message.get("tool_calls", []):
                if call["id"] == "start-review":
                    call["id"] = "start-worker"
            if message.get("tool_call_id") == "start-review":
                message["tool_call_id"] = "start-worker"
        self.assertTrue(verify(*args)["passed"])

    def test_actual_case_selection_uses_collect_and_excludes_default(self):
        self.assertEqual("collect", legacy_observer_mode(CASE, 1))
        functions = shell_functions("child_result_consumer", "run_all")
        script = functions + '''
print_category() { :; }; end_category() { :; }; run_multi_turn_case() { :; }
run_case() { [[ "${1:-}" != --json ]] || shift; [[ "$1" != coordination_implement_review ]] || printf '%s\\n' "$1"; }
FILTER_CASE="$1"; FILTER_CATEGORY=""; run_all
'''
        for case, expected in ((CASE, CASE + "\n"), ("", ""), ("coordination_analyze_plan", "")):
            result = subprocess.run(["bash", "-c", script, "control", case], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr);self.assertEqual(expected, result.stdout)
        result = subprocess.run(["bash", "-c", shell_functions("child_result_consumer") + '\nFILTER_CASE=coordination_implement_review; child_result_consumer'])
        self.assertEqual(0, result.returncode)


if __name__ == "__main__":
    unittest.main()
