"""Behavioral controls for two actual isolated writer candidates."""

import copy
import json
import os
import shutil
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from child_run_evals import REQUIRED_RATIONALE_ERROR, actual_file, legacy_observer_mode
from coordination_two_writers_evals import (CASE, WRITERS, checkout_snapshot, commands, git, initialization_script,
                                           main, prepare, prompt, run_check, sha, snapshot, verify)
from test_child_run_evals import ACCEPTED, ROOT as CHILD_ROOT, child, parent, terminal
from test_coordination_workflow_evals import shell_functions

ROOT = Path(__file__).resolve().parents[1]


def pair(request, events, name, arguments, result, identifier, failure=None):
    request["messages"].extend([
        {"role": "assistant", "tool_calls": [{"id": identifier, "function": {"name": name, "arguments": json.dumps(arguments)}}]},
        {"role": "tool", "tool_call_id": identifier, "content": result}])
    if events is not None:
        for value in ({"Type": "tool_call", "CallId": identifier, "ToolName": name, "ArgumentsJson": json.dumps(arguments)},
                      {"Type": "tool_result", "CallId": identifier, "ToolName": name, "Result": result, "ToolFailureCode": failure}):
            n = len(events) + 1
            events.append({"sequence": n, "observed_ns": n, "output": {**value, "SessionId": "session-neutral"}})


def evidence(root, line_ending="\n"):
    home = root / "home"
    setup = prepare(home, root / "evidence", lambda runtime: subprocess.run(
        ["bash", "-s"], input=initialization_script(str(actual_file(home, runtime))), capture_output=True, text=True))
    receipts, outputs, baseline, reports = {}, {}, {}, {}
    for writer, row in setup["writers"].items():
        source = actual_file(home, row["source"])
        baseline[writer] = source.read_bytes().decode()
        content = baseline[writer]
        if writer == "writer-a":
            content = content.replace("        self.records = candidate\n        self._validate_unique(candidate)",
                                      "        self._validate_unique(candidate)\n        self.records = candidate")
        else:
            content = 'def select(records, identifier):\n    return next((record for record in records if record["id"] == identifier), None)\n'
        source.write_bytes(content.replace("\n", line_ending).encode())
        git(home, setup, writer, "add", row["relative_source"])
        outputs[writer] = git(home, setup, writer, "commit", "-m", "Fix " + writer).decode()
        checked = run_check(home, setup, writer)
        assert checked.returncode == 0, checked.stderr
        receipts[writer] = json.loads(checked.stdout)
        reports[writer] = json.dumps({**receipts[writer], "changed_files": [row["relative_source"]]}, sort_keys=True) + "\n"
        actual_file(home, row["report"]).write_text(reports[writer])
    receipt = {"session_id": "session-neutral", "accepted_runs": [], "verified_deliveries": [],
               "delivery_observations": {"complete": True}, "last_reply": ""}
    p = {"messages": [{"role": "system", "content": "[available-subagents]\n## task-worker\nExecute scoped code tasks"},
                      {"role": "user", "content": prompt(setup)}]}
    events, children, final, terminals = [], [], [], []
    for writer, row in setup["writers"].items():
        accepted = {**ACCEPTED, "run_id": "run-" + writer, "scope_id": "scope-" + writer}
        receipt["accepted_runs"].append(accepted)
        assignment = "\n".join([setup["nonce"], row["root"], row["source"], row["report"], setup["base_commit"], commands(setup, writer)["check"]])
        pair(p, events, "spawn_agent", {"Agent": "task-worker", "Task": assignment}, json.dumps(accepted), "start-" + writer)
        c = child(); runtime = CHILD_ROOT + "-" + writer
        c["messages"][1]["content"] = c["messages"][1]["content"].replace(CHILD_ROOT, runtime) + "\n" + assignment
        source = actual_file(home, row["source"]).read_bytes().decode()
        pair(c, None, "file_read", {"Path": row["source"]}, baseline[writer], "baseline-" + writer)
        pair(c, None, "file_write", {"Path": row["source"], "Content": source}, f"Successfully wrote {len(source.encode())} bytes to {row['source']}", "source-" + writer)
        pair(c, None, "shell_execute", {"Command": commands(setup, writer)["commit"]}, "Exit code: 0\n" + outputs[writer], "commit-" + writer)
        pair(c, None, "shell_execute", {"Command": commands(setup, writer)["check"]}, "Exit code: 0\n" + json.dumps(receipts[writer], sort_keys=True) + "\n", "check-" + writer)
        pair(c, None, "file_write", {"Path": row["report"], "Content": reports[writer]}, f"Successfully wrote {len(reports[writer].encode())} bytes to {row['report']}", "report-" + writer)
        children.append(c)
        value = {**terminal(), "run_id": accepted["run_id"], "scope_id": accepted["scope_id"],
                 "log_path": runtime + "/logs/session.log", "artifact_directory": runtime + "/artifacts"}
        receipt["verified_deliveries"].append({"accepted": accepted, "terminal": value})
        synthetic = parent(value, identifier="delivery-" + writer)["messages"]
        synthetic[0]["tool_calls"][0]["function"]["arguments"] = json.dumps({"run_id": accepted["run_id"], "source_operation": "spawn_agent"})
        terminals.extend(synthetic)
        final.append({"writer": writer, "run_id": accepted["run_id"], "worktree": row["root"], "report": row["report"],
                      "candidate_commit": receipts[writer]["candidate_commit"], "source_sha256": receipts[writer]["source_sha256"], "checks": receipts[writer]["checks"]})
    p["messages"].extend(terminals)
    for writer, row in setup["writers"].items():
        for kind, path, content in (("source", row["source"], actual_file(home, row["source"]).read_bytes().decode()),
                                    ("report", row["report"], reports[writer])):
            pair(p, events, "file_read", {"Path": path}, content, "read-" + kind + "-" + writer)
        for key, result in (("revision", receipts[writer]["candidate_commit"] + "\n"),
                            ("diff", git(home, setup, writer, "diff", setup["base_commit"], "HEAD", "--", row["relative_source"]).decode()),
                            ("check", json.dumps(receipts[writer], sort_keys=True) + "\n")):
            pair(p, events, "shell_execute", {"Command": commands(setup, writer)[key]}, "Exit code: 0\n" + result, "parent-" + key + "-" + writer)
    receipt["last_reply"] = json.dumps({"nonce": setup["nonce"], "candidates": final, "operator_preserved": True,
                                        "unresolved_findings": [], "integration": "not_performed"})
    return setup, receipt, events, [p, *children], home, lambda writer: run_check(home, setup, writer)


class TwoWriterControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.args = evidence(Path(self.temp.name))

    def reject(self):
        with self.assertRaises((AssertionError, ValueError, KeyError, subprocess.CalledProcessError)):
            verify(*self.args)

    def test_two_actual_candidates_pass_and_both_faulty_baselines_fail(self):
        result = verify(*self.args)
        self.assertTrue(result["passed"])
        self.assertEqual(2, len({r["candidate_commit"] for r in result["candidates"]}))
        self.assertEqual(self.args[0]["operator_before"], checkout_snapshot(self.args[4], self.args[0]))
        for writer, defect in (("writer-a", "A rejected refresh must preserve the prior records."),
                                ("writer-b", "Selection must match the exact requested identifier.")):
            self.assertIn(defect, (Path(self.temp.name) / ("evidence/" + writer + "-baseline.stderr")).read_text())

    def test_exact_crlf_candidate_bytes_pass(self):
        self.assertTrue(verify(*evidence(Path(self.temp.name) / "crlf", "\r\n"))["passed"])

    def test_cumulative_captures_and_completed_call_id_reuse_pass(self):
        setup, receipt, events, requests, *_ = self.args
        p = requests[0]
        reads = [m for m in p["messages"] if m.get("tool_call_id", "").startswith("read-source")]
        first, second = [m["tool_call_id"] for m in reads]
        for e in events:
            if e["output"]["CallId"] == second: e["output"]["CallId"] = first
        for m in p["messages"]:
            if m.get("tool_call_id") == second: m["tool_call_id"] = first
            for c in m.get("tool_calls", []):
                if c["id"] == second: c["id"] = first
        for m in requests[1]["messages"]:
            if m.get("tool_call_id"):
                m["tool_call_id"] = "completed-child-id"
            for c in m.get("tool_calls", []):
                c["id"] = "completed-child-id"
        requests.append(copy.deepcopy(p))
        requests.append(copy.deepcopy(requests[1]))
        self.assertTrue(verify(*self.args)["passed"])

    def test_shared_original_context_does_not_merge_distinct_writer_reports(self):
        p = self.args[3][0]
        context = "\n".join(row["root"] + "\n" + row["report"] for row in self.args[0]["writers"].values())
        for m in p["messages"]:
            for c in m.get("tool_calls", []):
                if c["id"].startswith("start-"):
                    args = json.loads(c["function"]["arguments"]); args["Context"] = context
                    c["function"]["arguments"] = json.dumps(args)
                    for e in self.args[2]:
                        if e["output"]["Type"] == "tool_call" and e["output"]["CallId"] == c["id"]:
                            e["output"]["ArgumentsJson"] = json.dumps(args)
        self.assertTrue(verify(*self.args)["passed"])

    def test_wrong_or_missing_actual_profile_rejects(self):
        for name in ("code-analyst", "unknown"):
            with self.subTest(name=name):
                args = copy.deepcopy(self.args[:4])
                p = args[3][0]
                for m in p["messages"]:
                    for c in m.get("tool_calls", []):
                        if c["id"] == "start-writer-a":
                            a = json.loads(c["function"]["arguments"]); a["Agent"] = name; c["function"]["arguments"] = json.dumps(a)
                for e in args[2]:
                    if e["output"]["Type"] == "tool_call" and e["output"]["CallId"] == "start-writer-a":
                        a = json.loads(e["output"]["ArgumentsJson"]); a["Agent"] = name; e["output"]["ArgumentsJson"] = json.dumps(a)
                with self.assertRaises(AssertionError): verify(*args, *self.args[4:])

    def test_mixed_case_profile_and_hint_only_dto_arguments_pass(self):
        for m in self.args[3][0]["messages"]:
            for c in m.get("tool_calls", []):
                if c["id"].startswith("start-"):
                    args = json.loads(c["function"]["arguments"]); args["Agent"] = "TASK-WORKER";c["function"]["arguments"] = json.dumps(args)
                    for e in self.args[2]:
                        if e["output"]["Type"] == "tool_call" and e["output"]["CallId"] == c["id"]:
                            e["output"]["ArgumentsJson"] = json.dumps({**args, "_timeout_seconds": 30, "_background": False})
        self.assertTrue(verify(*self.args)["passed"])

    def test_exact_unexecuted_rationale_attempt_does_not_count_as_writer(self):
        pair(self.args[3][0], self.args[2], "spawn_agent", {"Agent": "task-worker", "Task": "not executed"},
             REQUIRED_RATIONALE_ERROR, "invalid-rationale", "invalid_rationale")
        self.assertTrue(verify(*self.args)["passed"])
        self.args[2][-1]["output"]["ToolFailureCode"] = "execution_failed"
        self.reject()

    def test_one_writer_or_unconsumed_terminal_rejects(self):
        for value in (False, 1, 1.0, "true"):
            with self.subTest(complete=value):
                self.args[1]["delivery_observations"]["complete"] = value
                self.reject()
        self.args[1]["delivery_observations"]["complete"] = True
        self.args[1]["accepted_runs"].pop()
        self.reject()

    def test_foreign_canonical_acceptance_fields_reject(self):
        original = copy.deepcopy(self.args[1]["accepted_runs"])
        for key, value in (("scope_id", "foreign"), ("state", "Running"), ("control_tool", "foreign")):
            with self.subTest(key=key):
                self.args[1]["accepted_runs"] = copy.deepcopy(original)
                self.args[1]["accepted_runs"][0] = {**original[0], key: value}
                self.reject()
        self.args[1]["accepted_runs"] = list(reversed(original))
        self.assertTrue(verify(*self.args)["passed"])

    def test_foreign_parent_dto_ids_reject(self):
        for e in self.args[2]:
            if e["output"]["ToolName"] == "file_read": e["output"]["CallId"] = "foreign-" + e["output"]["CallId"]
        self.reject()

    def test_missing_or_false_child_edit_commit_check_report_receipts_reject(self):
        original = copy.deepcopy(self.args[3][1])
        for identifier in ("baseline-writer-a", "source-writer-a", "commit-writer-a", "check-writer-a", "report-writer-a"):
            with self.subTest(identifier=identifier):
                self.args[3][1] = copy.deepcopy(original)
                for m in self.args[3][1]["messages"]:
                    if m.get("tool_call_id") == identifier: m["content"] = "fabricated success"
                self.reject()
        self.args[3][1] = original

    def test_writer_cannot_change_other_candidate_operator_or_checker(self):
        setup = self.args[0]
        for path in (setup["writers"]["writer-b"]["source"], setup["operator"] + "/operator.txt", setup["checker"]):
            with self.subTest(path=path):
                c = copy.deepcopy(self.args[3][1]);self.args[3][1] = c
                pair(c, None, "file_write", {"Path": path, "Content": "restored later"}, f"Successfully wrote 14 bytes to {path}", "forbidden-write")
                self.reject()
                c["messages"] = c["messages"][:-2]

    def test_visible_post_commit_source_restore_rejects(self):
        row = self.args[0]["writers"]["writer-a"]
        content = actual_file(self.args[4], row["source"]).read_text()
        pair(self.args[3][1], None, "file_write", {"Path": row["source"], "Content": content},
             f"Successfully wrote {len(content.encode())} bytes to {row['source']}", "post-commit-write")
        self.reject()

    def test_writer_calls_must_follow_successful_prior_feedback(self):
        original = copy.deepcopy(self.args[3][1])
        for identifier in ("source-writer-a", "commit-writer-a", "check-writer-a", "report-writer-a"):
            with self.subTest(identifier=identifier):
                self.args[3][1] = copy.deepcopy(original)
                messages = self.args[3][1]["messages"]
                index = next(n for n, m in enumerate(messages) if any(c["id"] == identifier for c in m.get("tool_calls", [])))
                call = messages.pop(index)
                messages.insert(index - 1, call)
                self.reject()
        self.args[3][1] = original
        self.assertTrue(verify(*self.args)["passed"])

    def test_early_source_edit_before_retained_baseline_rejects(self):
        original = copy.deepcopy(self.args[3][1])
        messages = original["messages"]
        index = next(n for n, m in enumerate(messages)
                     if any(c["id"] == "source-writer-a" for c in m.get("tool_calls", [])))
        early = copy.deepcopy(messages[index:index + 2])
        early[0]["tool_calls"][0]["id"] = "early-source-write"
        early[1]["tool_call_id"] = "early-source-write"
        baseline = next(n for n, m in enumerate(messages)
                        if any(c["id"] == "baseline-writer-a" for c in m.get("tool_calls", [])))
        for placement in ("first", "later"):
            with self.subTest(placement=placement):
                changed = copy.deepcopy(original)
                changed["messages"][baseline:baseline] = early
                self.args[3][1] = changed if placement == "first" else original
                if placement == "later":
                    self.args[3].append(changed)
                try:
                    with self.assertRaisesRegex(AssertionError, "before successful full baseline"):
                        verify(*self.args)
                finally:
                    if placement == "later":
                        self.args[3].pop()
        self.args[3][1] = original
        self.assertTrue(verify(*self.args)["passed"])

    def test_compacted_child_suffix_preserves_complete_prior_feedback_proof(self):
        suffix = copy.deepcopy(self.args[3][1])
        baseline = next(n for n, m in enumerate(suffix["messages"])
                        if m.get("tool_call_id") == "baseline-writer-a")
        suffix["messages"] = suffix["messages"][:2] + suffix["messages"][baseline + 1:]
        self.args[3].append(suffix)
        self.assertTrue(verify(*self.args)["passed"])

    def test_common_directory_redirect_rejects_before_any_git_process(self):
        setup, _, _, _, home, _ = self.args
        common = actual_file(home, setup["operator"]) / ".git"
        alternate = Path(self.temp.name) / "alternate-common"
        shutil.copytree(common, alternate)
        commondir = common / "worktrees" / "writer-a" / "commondir"
        original = commondir.read_bytes()
        pointer = actual_file(home, setup["writers"]["writer-a"]["root"]) / ".git"
        registration = commondir.parent / "gitdir"
        retained = (pointer.read_bytes(), registration.read_bytes())
        try:
            commondir.write_text(str(alternate) + "\n")
            self.assertEqual(retained, (pointer.read_bytes(), registration.read_bytes()))
            with patch("coordination_two_writers_evals.subprocess.check_output") as process:
                with self.assertRaisesRegex(AssertionError, "original common Git directory"):
                    git(home, setup, "writer-a", "rev-parse", "HEAD")
                process.assert_not_called()
            with self.assertRaisesRegex(AssertionError, "original common Git directory"):
                verify(*self.args)
            setup["writers"]["writer-a"]["common_directory_sha256"] = sha(commondir.read_bytes())
            with self.assertRaisesRegex(AssertionError, "original common Git directory"):
                verify(*self.args)
        finally:
            commondir.write_bytes(original)
            setup["writers"]["writer-a"]["common_directory_sha256"] = sha(original)
        self.assertTrue(verify(*self.args)["passed"])

    def test_each_operator_state_remains_exact(self):
        setup, _, _, _, home, _ = self.args
        operator = actual_file(home, setup["operator"])
        for path in (operator / "operator.txt", operator / "UNTRACKED.txt", operator / ".git/index", operator / ".git/HEAD"):
            with self.subTest(path=path):
                original = path.read_bytes()
                try:
                    path.write_bytes(original + b"changed")
                    self.reject()
                finally:
                    path.write_bytes(original)
        self.assertTrue(verify(*self.args)["passed"])

    def test_shared_worktree_or_registration_substitution_rejects(self):
        setup, _, _, _, home, _ = self.args
        path = actual_file(home, setup["writers"]["writer-b"]["root"]) / ".git"
        original = path.read_bytes()
        try:
            path.write_bytes((actual_file(home, setup["writers"]["writer-a"]["root"]) / ".git").read_bytes())
            self.reject()
        finally:
            path.write_bytes(original)

    def test_parent_must_read_both_full_sources_and_reports_after_both_terminals(self):
        original = copy.deepcopy(self.args[3][0])
        for identifier in ("read-source-writer-a", "read-report-writer-b"):
            with self.subTest(identifier=identifier):
                self.args[3][0] = copy.deepcopy(original)
                for m in self.args[3][0]["messages"]:
                    if m.get("tool_call_id") == identifier:m["content"] = "partial"
                self.reject()
        self.args[3][0] = original
        messages = self.args[3][0]["messages"]
        at = next(n for n,m in enumerate(messages) if any(c["id"] == "read-report-writer-a" for c in m.get("tool_calls", [])))
        before = next(n for n,m in enumerate(messages) if any(c["id"] == "delivery-writer-a" for c in m.get("tool_calls", [])))
        moved = messages[at:at+2];del messages[at:at+2];messages[before:before] = moved
        self.reject()

    def test_parent_must_inspect_exact_candidate_revision_diff_and_check(self):
        original = copy.deepcopy(self.args[3][0])
        for identifier in ("parent-revision-writer-a", "parent-diff-writer-b", "parent-check-writer-a"):
            with self.subTest(identifier=identifier):
                self.args[3][0] = copy.deepcopy(original)
                for m in self.args[3][0]["messages"]:
                    if m.get("tool_call_id") == identifier:m["content"] = "Exit code: 0\nstale\n"
                self.reject()
        self.args[3][0] = original

    def test_named_root_declarations_require_exact_success_and_pair(self):
        row = self.args[0]["writers"]["writer-a"]
        pair(self.args[3][0], self.args[2], "set_working_directory", {"Path": row["root"]}, row["root"], "parent-project")
        pair(self.args[3][1], None, "set_working_directory", {"Path": row["root"]}, row["root"], "child-project")
        self.assertTrue(verify(*self.args)["passed"])
        self.args[2][-1]["output"]["Result"] = "foreign"
        self.reject()
        self.args[2][-1]["output"]["Result"] = row["root"]
        self.args[3][1]["messages"][-1]["content"] = "foreign"
        self.reject()

    def test_foreign_directory_action_or_parent_write_rejects(self):
        for actor, name, args, result in ((0, "file_write", {"Path": self.args[0]["writers"]["writer-a"]["source"], "Content": "x"}, "written"),
                                          (1, "file_list", {"Path": self.args[0]["operator"]}, "metadata"),
                                          (2, "file_search", {"Root": "/foreign", "Pattern": "x"}, "result")):
            with self.subTest(actor=actor,name=name):
                c = self.args[3][actor]
                pair(c, None, name, args, result, "foreign-action")
                self.reject();c["messages"] = c["messages"][:-2]

    def test_final_reply_reconciles_both_candidates_without_integration(self):
        before = self.args[1]["last_reply"]
        for key, value in (("integration", "merged"), ("unresolved_findings", ["unresolved"]), ("operator_preserved", 1)):
            with self.subTest(key=key):
                reply = json.loads(before);reply[key]=value;self.args[1]["last_reply"] = json.dumps(reply);self.reject()
        self.args[1]["last_reply"] = before

    def test_actual_report_and_final_checks_require_boolean_values(self):
        setup, receipt, _, _, home, _ = self.args
        reply = receipt["last_reply"]
        for value in (1, 1.0):
            with self.subTest(value=value, boundary="final"):
                answer = json.loads(reply)
                answer["candidates"][0]["checks"]["duplicate_retains_prior"] = value
                receipt["last_reply"] = json.dumps(answer)
                self.reject()
        receipt["last_reply"] = reply
        path = actual_file(home, setup["writers"]["writer-a"]["report"])
        content = path.read_bytes()
        report = json.loads(content); report["checks"]["duplicate_retains_prior"] = 1
        path.write_text(json.dumps(report))
        with self.assertRaisesRegex(AssertionError, "report is stale or names false checks"):
            verify(*self.args)
        path.write_bytes(content)
        original = self.args[-1]
        def numeric_actual_check(writer):
            result = original(writer)
            output = json.loads(result.stdout)
            output["checks"][next(iter(output["checks"]))] = 1
            return subprocess.CompletedProcess(result.args, 0, json.dumps(output), result.stderr)
        with self.assertRaisesRegex(AssertionError, "actual check describes another candidate"):
            verify(*self.args[:5], numeric_actual_check)
        self.assertTrue(verify(*self.args)["passed"])
        checkpoint = terminal(True)["checkpoint"]
        receipt["verified_deliveries"][0]["terminal"]["checkpoint"] = copy.deepcopy(checkpoint)
        for message in self.args[3][0]["messages"]:
            if message.get("tool_call_id") == "delivery-writer-a":
                body = json.loads(message["content"]); body["checkpoint"] = checkpoint
                message["content"] = json.dumps(body)
        self.assertTrue(verify(*self.args)["passed"])
        receipt["verified_deliveries"][0]["terminal"]["checkpoint"]["CompletedRound"] = True
        self.reject()

    def test_mutations_during_oracle_check_cannot_change_operator_checker_or_candidate(self):
        setup, _, _, _, home, original = self.args
        for target in (actual_file(home, setup["operator"] + "/UNTRACKED.txt"), actual_file(home, setup["checker"])):
            with self.subTest(target=target):
                content = target.read_bytes()
                def changed(writer):
                    result = original(writer);target.write_bytes(content+b"changed");return result
                try:
                    with self.assertRaises(AssertionError):verify(*self.args[:5],changed)
                finally:
                    target.write_bytes(content)
        def extra_commit(writer):
            result = original(writer)
            git(home,setup,writer,"commit","--allow-empty","-m","Unexpected candidate")
            return result
        with self.assertRaises(AssertionError):verify(*self.args[:5],extra_commit)

    def test_timeout_is_an_eval_failure(self):
        def timed_out(writer):raise subprocess.TimeoutExpired(["checker", writer], 60)
        with self.assertRaises(subprocess.TimeoutExpired):verify(*self.args[:5],timed_out)

    def test_verify_cli_preserves_actual_outputs_before_failure_and_bounds_checks(self):
        setup, receipt, events, requests, home, _ = self.args
        root = Path(self.temp.name)
        observer, relay = root / "observer", root / "relay"
        observer.mkdir(); relay.mkdir()
        data = {"Mode": "collect", "InitialPrompt": prompt(setup), "Nonce": "neutral-prompt-nonce", "SessionId": receipt["session_id"]}
        receipt.update(status="observed", observer_mode="collect", case=CASE, prompt_ordinal=1, prompt_nonce=data["Nonce"],
                       initial_prompt_sha256=sha(data["InitialPrompt"].encode()))
        for name, value in (("observer-input.json", data), ("verified-receipt.json", receipt)):
            (observer / name).write_text(json.dumps(value))
        (observer / "session-output.jsonl").write_text("\n".join(json.dumps(e) for e in events))
        for n, request in enumerate(requests, 1):
            (relay / f"request-{n:04}.json").write_text(json.dumps(request))
        path = actual_file(home, setup["writers"]["writer-b"]["report"])
        wrong = b'{"actual":"unverified report"}\n'; path.write_bytes(wrong)
        original = subprocess.run
        captured = []
        def timed_out(argv, **kwargs):
            if argv[0] == "docker":
                captured.append((argv, kwargs))
                raise subprocess.TimeoutExpired(argv, kwargs["timeout"])
            return original(argv, **kwargs)
        argv = ["eval", "verify", "--eval-home", str(home), "--evidence", str(root / "evidence"),
                "--observer-directory", str(observer), "--relay-directory", str(relay), "--container", "owned-control"]
        for ordinal in (True, 1.0):
            with self.subTest(ordinal=ordinal):
                receipt["prompt_ordinal"] = ordinal
                (observer / "verified-receipt.json").write_text(json.dumps(receipt))
                with patch("sys.argv", argv):
                    with self.assertRaisesRegex(AssertionError, "prompt ordinal is invalid"):
                        main()
        receipt["prompt_ordinal"] = 1
        (observer / "verified-receipt.json").write_text(json.dumps(receipt))
        with patch("sys.argv", argv), patch.dict(os.environ, {"PROMPT_TIMEOUT": "300"}), patch("subprocess.run", side_effect=timed_out):
            with self.assertRaises(subprocess.TimeoutExpired):
                main()
        self.assertEqual(["docker", "exec", "--user", "netclaw", "owned-control", "python3", "-B", setup["checker"],
                          setup["writers"]["writer-a"]["root"], setup["nonce"], "writer-a"], captured[0][0])
        self.assertEqual(300, captured[0][1]["timeout"])
        self.assertEqual(wrong, (observer / "writer-b-writer-b.json").read_bytes())
        summary = json.loads((observer / "two-writer-artifacts.json").read_text())
        self.assertEqual({"state": "captured", "sha256": sha(wrong)}, summary["writer-b-writer-b.json"])
        self.assertEqual(4, len(summary))
        path.unlink()
        with patch("sys.argv", argv), patch.dict(os.environ, {"PROMPT_TIMEOUT": "300"}), patch("subprocess.run", side_effect=timed_out):
            with self.assertRaises(subprocess.TimeoutExpired):
                main()
        summary = json.loads((observer / "two-writer-artifacts.json").read_text())
        self.assertEqual({"state": "missing"}, summary["writer-b-writer-b.json"])

    def test_explicit_selection_and_actual_prompt_dispatch(self):
        self.assertEqual("collect", legacy_observer_mode(CASE,1))
        with self.assertRaises(AssertionError):legacy_observer_mode(CASE,2)
        script = shell_functions("setup_coordination_two_writers", "run_case", "run_all", "child_result_consumer") + r'''
print_category() { :; }; end_category() { :; }; run_multi_turn_case() { :; }
check_daemon_alive() { :; }
pick_variant() { printf '%s' "$1"; }
run_prompt() { printf '%s' "$1" > "$CAPTURE"; printf '%s' "$2" > "$CAPTURE.format"; }
assert_coordination_two_writers() { return 0; }
store_result() { :; }; store_metrics() { :; }
CATEGORY_SKIPPED=false; CATEGORY_CASES=0; TOTAL_CASES=0
CATEGORY_PASSED=0; PASSED_CASES=0; FAILED_CASES=0
RUNS=1; THRESHOLD=1; FILTER_CATEGORY=""
run_all
'''
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            fake_bin=root/"bin";fake_bin.mkdir()
            docker=fake_bin/"docker"
            docker.write_text('''#!/usr/bin/env python3
import os,subprocess,sys
assert sys.argv[1:]==['exec','-i','--user','netclaw','owned-control','bash','-s'],sys.argv
script=sys.stdin.read().replace('/home/netclaw/.netclaw/',os.environ['EVAL_HOME']+'/data/')
raise SystemExit(subprocess.run(['bash','-s'],input=script,text=True).returncode)
''')
            docker.chmod(0o755)
            env={**os.environ,"REPO_ROOT":str(ROOT),"EVAL_HOME":str(root/"home"),"TMPDIR_EVAL":str(root/"temporary"),
                 "CAPTURE":str(root/"prompt"),"PATH":str(fake_bin)+os.pathsep+os.environ["PATH"],
                 "EVAL_CONTAINER_NAME":"owned-control","PYTHONDONTWRITEBYTECODE":"1","FILTER_CASE":CASE}
            result=subprocess.run(["bash","-e","-c",script],env=env,capture_output=True,text=True)
            self.assertEqual(0,result.returncode,result.stderr)
            setup=json.loads((root/"temporary/child-runs/two-writers-case/setup.json").read_text())
            self.assertEqual(prompt(setup).encode(),(root/"prompt").read_bytes())
            self.assertEqual(b"json",(root/"prompt.format").read_bytes())
            for writer in WRITERS:
                self.assertIn(commands(setup,writer)["commit"].encode(),(root/"prompt").read_bytes())
            self.assertEqual(0,subprocess.run(["bash","-c",shell_functions("child_result_consumer")+'\nFILTER_CASE="$1"; child_result_consumer',"control",CASE]).returncode)
        selection=shell_functions("run_all")+'''
print_category(){ :; };end_category(){ :; };run_multi_turn_case(){ :; }
run_case(){ [[ "${1:-}" != --json ]] || shift; [[ "$1" != coordination_two_writers ]] || printf '%s\\n' "$1"; }
FILTER_CASE="$1";run_all
'''
        for selected,expected in ((CASE,CASE+"\n"),("",""),("coordination_implement_review","")):
            result=subprocess.run(["bash","-c",selection,"control",selected],capture_output=True,text=True)
            self.assertEqual(0,result.returncode,result.stderr);self.assertEqual(expected,result.stdout)


if __name__ == "__main__":
    unittest.main()
