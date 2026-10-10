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
                                                initialization_script, main, prepare, project_declarations, prompt, run_check, verify)
from test_child_run_evals import child, parent, terminal, ROOT as CHILD_ROOT
from test_coordination_workflow_evals import observer_calls, shell_functions

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
    receipt["calls"] = observer_calls(events)
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
        args[1]["calls"] = observer_calls(events)
        self.assertTrue(verify(*args)["passed"])
        for failure, result in (("invalid_rationale", "different text"), ("authorization_denied", REQUIRED_RATIONALE_ERROR), (None, REQUIRED_RATIONALE_ERROR)):
            changed = copy.deepcopy(args)
            changed[2][1]["output"].update(ToolFailureCode=failure, Result=result)
            changed[3][0]["messages"][3]["content"] = result
            with self.subTest(failure=failure, result=result):
                self.reject(changed)
        args[3][0]["messages"][3]["content"] = "forged provider result"
        self.reject(args)

    def test_terminal_shaped_rejection_needs_its_actual_dto_metadata(self):
        args = copy.deepcopy(self.args)
        _, receipt, events, requests, _, _ = args
        arguments = {"run_id": receipt["accepted_runs"][1]["run_id"], "source_operation": "spawn_agent"}
        outputs = [{"Type": "tool_call", "CallId": "rejected-terminal", "ToolName": "spawn_agent", "ArgumentsJson": json.dumps(arguments)},
                   {"Type": "tool_result", "CallId": "rejected-terminal", "ToolName": "spawn_agent", "Result": REQUIRED_RATIONALE_ERROR,
                    "ToolFailureCode": "invalid_rationale"}]
        for output in outputs:
            ordinal = len(events) + 1
            events.append({"sequence": ordinal, "observed_ns": ordinal, "output": {**output, "SessionId": receipt["session_id"]}})
        requests[0]["messages"].extend([
            {"role": "assistant", "tool_calls": [{"id": "rejected-terminal", "function": {
                "name": "spawn_agent", "arguments": json.dumps(arguments)}}]},
            {"role": "tool", "tool_call_id": "rejected-terminal", "content": REQUIRED_RATIONALE_ERROR}])
        receipt["calls"] = observer_calls(events)
        self.assertTrue(verify(*args)["passed"])
        self.assertIs(receipt["calls"][-1]["success"], False)
        for field, value in [("failure_code", None), ("success", 0), ("occurrence", 1)]:
            changed = copy.deepcopy(args)
            changed[1]["calls"][-1][field] = value
            with self.subTest(field=field):
                self.reject(changed)
        changed = copy.deepcopy(args)
        changed[2][-1]["output"]["ToolFailureCode"] = None
        self.reject(changed)

    def append_parent_pair(self, args, identifier, name, arguments, result, failure=None):
        receipt, events, requests = args[1:4]
        requests[0]["messages"].extend([
            {"role": "assistant", "tool_calls": [{"id": identifier, "function": {"name": name, "arguments": json.dumps(arguments)}}]},
            {"role": "tool", "tool_call_id": identifier, "content": result}])
        for output in [{"Type": "tool_call", "CallId": identifier, "ToolName": name, "ArgumentsJson": json.dumps(arguments)},
                       {"Type": "tool_result", "CallId": identifier, "ToolName": name, "Result": result, "ToolFailureCode": failure}]:
            ordinal = len(events) + 1
            events.append({"sequence": ordinal, "observed_ns": ordinal, "output": {**output, "SessionId": receipt["session_id"]}})
        receipt["calls"] = observer_calls(events)

    def repaired_parent(self):
        args = copy.deepcopy(self.args)
        self.append_parent_pair(args, "rejected-load", "skill_load", {"Name": "agent-coordination"},
                                REQUIRED_RATIONALE_ERROR, "invalid_rationale")
        self.append_parent_pair(args, "rejected-declaration", "set_working_directory", {"Path": args[0]["root"]},
                                REQUIRED_RATIONALE_ERROR, "invalid_rationale")
        return args

    def test_exact_parent_metadata_rejections_allow_correction_without_declaration_credit(self):
        args = self.repaired_parent()
        self.assertTrue(verify(*args)["passed"])
        roots = {args[0]["worker"], args[0]["operator"]}
        self.assertEqual(set(), project_declarations([args[3][0]], roots, args[1]["calls"]))
        self.append_parent_pair(args, "actual-declaration", "set_working_directory", {"Path": args[0]["worker"]}, args[0]["worker"])
        self.assertTrue(verify(*args)["passed"])
        self.assertEqual({("actual-declaration", json.dumps({"Path": args[0]["worker"]}, sort_keys=True), args[0]["worker"])},
                         project_declarations([args[3][0]], roots, args[1]["calls"]))
        self.assertEqual([False, False, True], [row["success"] for row in args[1]["calls"][-3:]])

    def test_parent_rejection_needs_typed_dto_provider_and_exact_occurrence_evidence(self):
        args = self.repaired_parent()
        for index in [-2, -1]:
            for field, value in [("failure_code", None), ("failure_code", "unknown_agent"), ("success", 0),
                                 ("occurrence", 1), ("id", "foreign"), ("result", REQUIRED_RATIONALE_ERROR + " changed")]:
                changed = copy.deepcopy(args)
                changed[1]["calls"][index][field] = value
                with self.subTest(index=index, field=field):
                    self.reject(changed)
        for field, value in [("ToolFailureCode", None), ("ToolFailureCode", "unknown_agent"),
                             ("Result", REQUIRED_RATIONALE_ERROR + " changed"), ("SessionId", "foreign")]:
            changed = copy.deepcopy(args)
            changed[2][-1]["output"][field] = value
            with self.subTest(field=field):
                self.reject(changed)
        changed = copy.deepcopy(args)
        changed[3][0]["messages"][-1]["content"] = "Different provider bytes."
        self.reject(changed)

    def test_canonical_parent_feedback_without_typed_code_is_not_success(self):
        args = self.repaired_parent()
        for name in ["skill_load", "set_working_directory"]:
            for failure in [None, "unknown_agent"]:
                changed = copy.deepcopy(args)
                result = next(e["output"] for e in changed[2] if e["output"]["Type"] == "tool_result"
                              and e["output"]["ToolName"] == name and e["output"]["Result"] == REQUIRED_RATIONALE_ERROR)
                result["ToolFailureCode"] = failure
                changed[1]["calls"] = observer_calls(changed[2])
                with self.subTest(name=name, failure=failure):
                    self.reject(changed)
        self.append_parent_pair(args, "actual-load", "skill_load", {"Name": "agent-coordination"},
                                "The coordination workflow describes an implementation and an independent review.")
        self.assertTrue(verify(*args)["passed"])
        self.assertIs(args[1]["calls"][-1]["success"], True)

    def test_rejected_parent_calls_need_distinct_occurrences_only_within_one_history(self):
        args = self.repaired_parent()
        args[3].append(copy.deepcopy(args[3][0]))
        self.assertTrue(verify(*args)["passed"])
        args[3].pop()
        duplicate_pair = copy.deepcopy(args[3][0]["messages"][-2:])
        args[3][0]["messages"].extend(duplicate_pair)
        self.reject(args)
        args[3][0]["messages"] = args[3][0]["messages"][:-2]
        self.append_parent_pair(args, "rejected-declaration", "set_working_directory", {"Path": args[0]["root"]},
                                REQUIRED_RATIONALE_ERROR, "invalid_rationale")
        self.assertTrue(verify(*args)["passed"])

    def test_executed_base_declaration_and_rejected_required_reads_stay_strict(self):
        args = self.repaired_parent()
        self.append_parent_pair(args, "actual-base-declaration", "set_working_directory", {"Path": args[0]["root"]}, args[0]["root"])
        self.reject(args)
        args = self.repaired_parent()
        self.append_parent_pair(args, "failed-read", "file_read", {"Path": args[0]["worker_report"]},
                                REQUIRED_RATIONALE_ERROR, "invalid_rationale")
        self.reject(args)
        args = self.repaired_parent()
        self.append_parent_pair(args, "failed-shell", "shell_execute", {"Command": commands(args[0])["check"]},
                                REQUIRED_RATIONALE_ERROR, "invalid_rationale")
        self.reject(args)

    def test_actual_combined_parent_command_shapes_stay_outside_the_case_contract(self):
        args = self.repaired_parent()
        setup = args[0]
        worker, operator, base = setup["worker"], setup["operator"], setup["base_commit"]
        combined = [f"git -C {worker} rev-parse HEAD && git -C {worker} status --porcelain=v1",
                    f"git -C {operator} status --porcelain=v1 && git -C {operator} rev-parse HEAD && git -C {operator} branch --show-current",
                    f"git -C {worker} rev-parse HEAD && git -C {worker} diff {base} HEAD -- source/catalog.py && git -C {worker} status --porcelain=v1"]
        for command in combined:
            changed = copy.deepcopy(args)
            self.append_parent_pair(changed, "forbidden-combined", "shell_execute", {"Command": command}, "Exit code: 0\n")
            with self.subTest(command=command):
                self.reject(changed)

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

    def add_declaration(self, args, actor, path, result):
        index = {"parent": 0, "worker": 1, "review": 2}[actor]
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

    def test_named_parent_and_child_project_declarations_preserve_the_code_contract(self):
        for parent_root in (self.args[0]["operator"], self.args[0]["worker"]):
            args = copy.deepcopy(self.args)
            self.add_declaration(args, "parent", parent_root, parent_root)
            for actor in ("worker", "review"):
                self.add_declaration(args, actor, args[0]["worker"], args[0]["worker"])
            with self.subTest(parent_root=parent_root):
                self.assertTrue(verify(*args)["passed"])

    def test_wrong_actor_root_failed_or_unpaired_project_declarations_fail(self):
        for actor in ("parent", "worker", "review"):
            for mutation in ("root", "failed", "unpaired"):
                args = copy.deepcopy(self.args)
                root = args[0]["worker"]
                path = args[0]["root"] if actor == "parent" else args[0]["operator"]
                self.add_declaration(args, actor, path if mutation == "root" else root,
                                     "Error: denied" if mutation == "failed" else (path if mutation == "root" else root))
                if mutation == "unpaired":
                    request = args[3][{"parent": 0, "worker": 1, "review": 2}[actor]]
                    request["messages"][:] = [m for m in request["messages"] if m.get("tool_call_id") != "declare-" + actor]
                with self.subTest(actor=actor, mutation=mutation):
                    self.reject(args)

    def test_parent_declaration_dto_and_provider_identity_must_match(self):
        for mutation in ("id", "path", "result", "failure", "missing"):
            args = copy.deepcopy(self.args)
            root = args[0]["operator"]
            self.add_declaration(args, "parent", root, root)
            if mutation == "missing":
                del args[2][:2]
                for index, event in enumerate(args[2], 1):
                    event.update(sequence=index, observed_ns=index)
            elif mutation == "id":
                for event in args[2][:2]:
                    event["output"]["CallId"] = "foreign-declaration"
            elif mutation == "path":
                args[2][0]["output"]["ArgumentsJson"] = json.dumps({"Path": args[0]["worker"]})
            elif mutation == "failure":
                args[2][1]["output"]["ToolFailureCode"] = "authorization_denied"
            else:
                args[2][1]["output"]["Result"] = args[0]["worker"]
            with self.subTest(mutation=mutation):
                self.reject(args)

    def test_foreign_parent_read_call_and_result_ids_cannot_borrow_real_provider_pairs(self):
        args = copy.deepcopy(self.args)
        for event in args[2]:
            if event["output"].get("ToolName") == "file_read":
                event["output"]["CallId"] = "foreign-" + event["output"]["CallId"]
        self.reject(args)

    def test_completed_parent_read_id_reuse_and_cumulative_capture_remain_valid(self):
        args = copy.deepcopy(self.args)
        identifiers = [e["output"]["CallId"] for e in args[2] if e["output"].get("Type") == "tool_call"
                       and e["output"].get("ToolName") == "file_read"]
        first, second = identifiers[:2]
        for event in args[2]:
            if event["output"].get("CallId") == second:
                event["output"]["CallId"] = first
        for message in args[3][0]["messages"]:
            for call in message.get("tool_calls", []):
                if call["id"] == second:
                    call["id"] = first
            if message.get("tool_call_id") == second:
                message["tool_call_id"] = first
        args[3].append(copy.deepcopy(args[3][0]))
        self.assertTrue(verify(*args)["passed"])

    def test_actual_prepared_coding_prompt_reaches_run_prompt_without_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fake_bin = root / "bin"; fake_bin.mkdir()
            docker = fake_bin / "docker"
            docker.write_text('''#!/usr/bin/env python3
import os,subprocess,sys
assert sys.argv[1:]==['exec','-i','--user','netclaw','owned-control','bash','-s'],sys.argv
script=sys.stdin.read().replace('/home/netclaw/.netclaw/',os.environ['EVAL_HOME']+'/data/')
raise SystemExit(subprocess.run(['bash','-s'],input=script,text=True).returncode)
''')
            docker.chmod(0o755)
            script = shell_functions("setup_coordination_implement_review", "run_case", "run_all") + r'''
print_category() { :; }; end_category() { :; }; run_multi_turn_case() { :; }
check_daemon_alive() { :; }
pick_variant() { printf '%s' "$1"; }
run_prompt() { printf '%s' "$1" > "$CAPTURE"; printf '%s' "$2" > "$CAPTURE.format"; }
assert_coordination_implement_review() { return 0; }
store_result() { :; }; store_metrics() { :; }
CATEGORY_SKIPPED=false; CATEGORY_CASES=0; TOTAL_CASES=0
CATEGORY_PASSED=0; PASSED_CASES=0; FAILED_CASES=0
RUNS=1; THRESHOLD=1; FILTER_CATEGORY=""
run_all
'''
            env = {**os.environ, "FILTER_CASE": CASE, "REPO_ROOT": str(ROOT), "EVAL_HOME": str(root / "home"),
                   "TMPDIR_EVAL": str(root / "temporary"), "CAPTURE": str(root / "actual-prompt"),
                   "PATH": str(fake_bin) + os.pathsep + os.environ["PATH"], "EVAL_CONTAINER_NAME": "owned-control",
                   "PYTHONDONTWRITEBYTECODE": "1"}
            result = subprocess.run(["bash", "-e", "-c", script], env=env, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            setup = json.loads((root / "temporary/child-runs/implement-review-case/setup.json").read_text())
            expected = prompt(setup).encode()
            actual = (root / "actual-prompt").read_bytes()
            self.assertEqual(expected, actual)
            self.assertIn(commands(setup)["commit"].encode(), actual)
            self.assertEqual(b"json", (root / "actual-prompt.format").read_bytes())

    def test_single_prompt_replacements_preserve_literal_ampersands_and_newlines(self):
        variables = {"MANAGED_WORKTREE_BRANCH": "MANAGED_WORKTREE_BRANCH", "CYCLE_PROMPT": "CYCLE_PROMPT",
                     "EVAL_REMINDER_TARGET": "EVAL_REMINDER_TARGET", "COORDINATION_PROMPT": "COORDINATION_PROMPT",
                     "PRODUCTIVE_PROMPT": "PRODUCTIVE_PROMPT", "IMPLEMENT_REVIEW_PROMPT": "IMPLEMENT_REVIEW_PROMPT",
                     "REPORT_REVIEW_PROMPT": "REPORT_REVIEW_PROMPT", "CONFLICT_REVIEW_PROMPT": "CONFLICT_REVIEW_PROMPT"}
        script = shell_functions("run_case") + r'''
check_daemon_alive() { :; }; pick_variant() { printf '%s' "$1"; }
run_prompt() { printf '%s' "$1" > "$CAPTURE"; }
assert_literal_control() { return 0; }; store_result() { :; }; store_metrics() { :; }
CATEGORY_SKIPPED=false; FILTER_CASE=literal_control; RUNS=1; THRESHOLD=1
CATEGORY_CASES=0; TOTAL_CASES=0; CATEGORY_PASSED=0; PASSED_CASES=0; FAILED_CASES=0
run_case literal_control 'literal value' "$TEMPLATE"
'''
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for token, variable in variables.items():
                for value in ("ordinary value", 'alpha & beta && gamma\n/neutral/a&b "quoted" $literal `unexecuted`\n'):
                    with self.subTest(token=token, value=value):
                        env = {**os.environ, **{name: "" for name in variables.values()}, variable: value,
                               "CAPTURE": str(root / "prompt"), "TEMPLATE": "before {{" + token + "}} after"}
                        result = subprocess.run(["bash", "-e", "-c", script], env=env, capture_output=True, text=True)
                        self.assertEqual(0, result.returncode, result.stderr)
                        self.assertEqual(("before " + value + " after").encode(), (root / "prompt").read_bytes())

    def test_multi_turn_path_replacements_preserve_literal_values(self):
        variables = {"FIRST_WORKTREE": "CODING_CONTEXT_FIRST_WORKTREE", "SECOND_WORKTREE": "CODING_CONTEXT_SECOND_WORKTREE",
                     "TARGET_BRANCH": "CODING_CONTEXT_TARGET_BRANCH", "TARGET_FILE": "CODING_CONTEXT_TARGET_FILE",
                     "DIRECT_ATTACHMENT_SOURCE": "DIRECT_ATTACHMENT_SOURCE_PATH"}
        script = shell_functions("run_multi_turn_case") + r'''
check_daemon_alive() { :; }
run_prompt_resume() { printf '%s' "$2" > "$CAPTURE.$4"; printf '%s' "$3" > "$CAPTURE.format"; }
assert_literal_control() { return 0; }; store_result() { :; }; store_metrics() { :; }
CATEGORY_SKIPPED=false; FILTER_CASE=literal_control; RUNS=1; THRESHOLD=1
CATEGORY_CASES=0; TOTAL_CASES=0; CATEGORY_PASSED=0; PASSED_CASES=0; FAILED_CASES=0
run_multi_turn_case --json literal_control 'literal paths' "$TEMPLATE" "second $TEMPLATE"
'''
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for token, variable in variables.items():
                for value in ("/neutral/path", "/neutral/a&b\nchild&&literal"):
                    with self.subTest(token=token, value=value):
                        env = {**os.environ, **{name: "" for name in variables.values()}, variable: value,
                               "CAPTURE": str(root / "prompt"), "TEMPLATE": "before {{" + token + "}} after"}
                        result = subprocess.run(["bash", "-e", "-c", script], env=env, capture_output=True, text=True)
                        self.assertEqual(0, result.returncode, result.stderr)
                        expected = "before " + value + " after"
                        self.assertEqual(expected.encode(), (root / "prompt.1").read_bytes())
                        self.assertEqual(("second " + expected).encode(), (root / "prompt.2").read_bytes())
                        self.assertEqual(b"json", (root / "prompt.format").read_bytes())

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
