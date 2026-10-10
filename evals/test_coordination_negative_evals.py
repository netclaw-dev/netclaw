"""Exercise real file effects, tool receipts, and parent-only case selection."""

import copy
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest

from child_run_evals import actual_file
from coordination_negative_evals import prepare, verify


ROOT = Path(__file__).resolve().parents[1]
TRIVIAL = "coordination_trivial_task"
UNAVAILABLE = "coordination_unavailable_profile"


def shell_functions(*names):
    source = (ROOT / "evals/run-evals.sh").read_text()
    return "\n".join(re.search(r"^" + re.escape(name) + r"\(\) \{\n.*?^\}", source,
                              re.MULTILINE | re.DOTALL).group(0) for name in names)


class NegativeCoordinationControls(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.home = self.root / "home"
        profiles = self.home / "data/agents"
        profiles.mkdir(parents=True)
        (profiles / "task-worker.md").write_text("A neutral available profile.\n")

    def evidence(self, case=TRIVIAL, name="file_edit", rejection=False):
        evidence = self.root / "setup"
        prompt = prepare(case, self.home, evidence)
        setup = json.loads((evidence / "setup.json").read_text())
        index = "[system: [available-subagents — use spawn_agent to delegate]\n\n## task-worker\nA neutral worker.\n]"
        calls = []
        if case == TRIVIAL:
            actual_file(self.home, setup["path"]).write_text(setup["expected"])
            args = {"Path": setup["path"], "OldString": "raedy", "NewString": "ready"}
            result = f"Successfully edited {setup['path']}: replaced 1 occurrence(s)"
            if name == "file_write":
                args = {"path": setup["path"], "content": setup["expected"]}
                result = f"Successfully wrote {len(setup['expected'].encode())} bytes to {setup['path']}"
            elif name == "file_edit_content":
                name = "file_edit"
                args = {"Path": setup["path"], "Content": setup["expected"]}
                result = f"Successfully wrote {len(setup['expected'].encode())} bytes to {setup['path']}"
            calls.append((name, "correct", args, result))
            response = "The typo is corrected."
        else:
            response = json.dumps({"profile": setup["profile"], "status": "unavailable", "delegated": False})
            if rejection:
                calls.append(("spawn_agent", "rejected", {"Agent": setup["profile"], "Task": "Review the note."},
                              f"Error: Unknown agent '{setup['profile']}'. Available agents: task-worker"))
        envelope = {"sessionId": "neutral-parent", "response": response, "toolCalls": []}
        request = {"messages": [{"role": "user", "content": index}, {"role": "user", "content": prompt}]}
        events = []
        for tool, call_id, args, result in calls:
            raw = json.dumps(args)
            envelope["toolCalls"].append({"toolName": tool, "callId": call_id, "argumentsJson": raw})
            events.extend([f"TOOL_CALL: {tool} call_id={call_id} args={raw}",
                           f"TOOL_RESULT: {tool} call_id={call_id} result={result}"])
            request["messages"].extend([
                {"role": "assistant", "tool_calls": [{"id": call_id, "function": {"name": tool, "arguments": raw}}]},
                {"role": "tool", "tool_call_id": call_id, "content": result}])
        log = "\n".join(f"[2026-10-10T00:00:{index:02}.0000000+00:00] {event}"
                        for index, event in enumerate(events)) + "\n"
        if not calls:
            envelope.pop("toolCalls")
        return setup, envelope, log, [request]

    def check(self, evidence):
        setup, envelope, log, requests = evidence
        return verify(setup["case"], self.home, setup, envelope, log, requests)

    def test_actual_targeted_edit_passes(self):
        self.assertTrue(self.check(self.evidence())["passed"])

    def test_exact_full_write_passes(self):
        self.assertTrue(self.check(self.evidence(name="file_write"))["passed"])

    def test_exact_file_edit_content_passes(self):
        self.assertTrue(self.check(self.evidence(name="file_edit_content"))["passed"])

    def test_provider_history_omits_execution_hints_but_preserves_rationale(self):
        setup, envelope, log, requests = self.evidence()
        old = envelope["toolCalls"][0]["argumentsJson"]
        args = json.loads(old)
        args.update(_rationale="Correct the single typo.")
        requests[0]["messages"][-2]["tool_calls"][0]["function"]["arguments"] = json.dumps(args)
        args.update(_timeout_seconds=120, _background=False)
        raw = json.dumps(args)
        envelope["toolCalls"][0]["argumentsJson"] = raw
        log = log.replace(old, raw)
        evidence = (setup, envelope, log, requests)
        self.assertTrue(self.check(evidence)["passed"])
        changed = copy.deepcopy(evidence)
        raw = changed[3][0]["messages"][-2]["tool_calls"][0]["function"]["arguments"]
        changed[3][0]["messages"][-2]["tool_calls"][0]["function"]["arguments"] = raw.replace("Correct the single typo.", "Another rationale.")
        with self.assertRaises(AssertionError):
            self.check(changed)

    def test_unavailable_profile_report_without_spawn_passes(self):
        self.assertTrue(self.check(self.evidence(UNAVAILABLE))["passed"])

    def test_canonical_null_tool_calls_also_pass_without_spawn(self):
        evidence = self.evidence(UNAVAILABLE)
        evidence[1]["toolCalls"] = None
        self.assertTrue(self.check(evidence)["passed"])

    def test_completed_call_id_reuse_preserves_failed_attempt_and_actual_correction(self):
        setup, envelope, log, requests = self.evidence()
        args = {"Path": setup["path"], "OldString": "a nonexistent typo", "NewString": "ready"}
        raw = json.dumps(args)
        result = "Error: OldString not found."
        envelope["toolCalls"].insert(0, {"toolName": "file_edit", "callId": "correct", "argumentsJson": raw})
        log = (f"[2026-10-10T00:00:00+00:00] TOOL_CALL: file_edit call_id=correct args={raw}\n"
               f"[2026-10-10T00:00:01+00:00] TOOL_RESULT: file_edit call_id=correct result={result}\n") + log
        requests[0]["messages"][2:2] = [
            {"role": "assistant", "tool_calls": [{"id": "correct", "function": {"name": "file_edit", "arguments": raw}}]},
            {"role": "tool", "tool_call_id": "correct", "content": result}]
        evidence = (setup, envelope, log, requests)
        self.assertTrue(self.check(evidence)["passed"])
        changed = copy.deepcopy(evidence)
        changed[3][0]["messages"].pop(3)
        with self.assertRaises(AssertionError):
            self.check(changed)

    def test_actual_unknown_profile_rejection_and_report_pass(self):
        self.assertTrue(self.check(self.evidence(UNAVAILABLE, rejection=True))["passed"])

    def test_final_claim_cannot_replace_edit_or_actual_receipts(self):
        evidence = self.evidence()
        setup, envelope, log, requests = evidence
        mutations = [
            (setup, envelope, "", requests),
            (setup, {**envelope, "toolCalls": []}, log, requests),
            (setup, envelope, log.replace("Successfully edited", "Edit claimed"), requests),
            (setup, envelope, log.replace("call_id=correct result=", "call_id=foreign result="), requests),
            (setup, envelope, log, []),
        ]
        for index, mutated in enumerate(mutations):
            with self.subTest(fault=index), self.assertRaises(AssertionError):
                self.check(mutated)
        actual_file(self.home, setup["path"]).write_text(setup["original"])
        with self.assertRaises(AssertionError):
            self.check(evidence)

    def test_provider_receipt_and_exact_edit_arguments_cannot_be_forged(self):
        evidence = self.evidence()
        changed = copy.deepcopy(evidence)
        changed[3][0]["messages"][-1]["content"] = "The parent claims success."
        with self.assertRaises(AssertionError):
            self.check(changed)
        for replacement in ("wrong.txt", "preserve.txt"):
            changed = copy.deepcopy(evidence)
            old = changed[0]["path"]
            new = changed[0]["root"] + "/" + replacement
            changed[1]["toolCalls"][0]["argumentsJson"] = changed[1]["toolCalls"][0]["argumentsJson"].replace(old, new)
            changed = (*changed[:2], changed[2].replace(old, new), changed[3])
            changed[3][0]["messages"][-2]["tool_calls"][0]["function"]["arguments"] = changed[1]["toolCalls"][0]["argumentsJson"]
            changed[3][0]["messages"][-1]["content"] = changed[3][0]["messages"][-1]["content"].replace(old, new)
            with self.subTest(path=replacement), self.assertRaises(AssertionError):
                self.check(changed)

    def test_wrong_bytes_neighbor_change_or_new_profile_fail(self):
        evidence = self.evidence()
        setup = evidence[0]
        target = actual_file(self.home, setup["path"])
        target.write_text(setup["expected"] + "Unexpected byte.\n")
        with self.assertRaises(AssertionError):
            self.check(evidence)
        target.write_text(setup["expected"])
        sentinel = actual_file(self.home, setup["root"] + "/preserve.txt")
        sentinel.write_text("Unexpected edit.")
        with self.assertRaises(AssertionError):
            self.check(evidence)
        sentinel.write_text(setup["sentinel"])
        (self.home / "data/agents/invented.md").write_text("Invented profile.")
        with self.assertRaises(AssertionError):
            self.check(evidence)

    def test_profile_update_and_hidden_file_fail(self):
        evidence = self.evidence(UNAVAILABLE)
        profile = self.home / "data/agents/task-worker.md"
        profile.write_text("Altered profile.")
        with self.assertRaises(AssertionError):
            self.check(evidence)
        profile.write_text("A neutral available profile.\n")
        actual_file(self.home, evidence[0]["root"] + "/unexpected.txt").write_text("Unexpected file.")
        with self.assertRaises(AssertionError):
            self.check(evidence)

    def test_neighbor_newline_byte_change_fails(self):
        evidence = self.evidence()
        setup = evidence[0]
        sentinel = actual_file(self.home, setup["root"] + "/preserve.txt")
        sentinel.write_bytes(setup["sentinel"].replace("\n", "\r\n").encode())
        with self.assertRaisesRegex(AssertionError, "neighboring note changed"):
            self.check(evidence)

    def test_numeric_zero_cannot_claim_no_delegation(self):
        evidence = self.evidence(UNAVAILABLE)
        evidence[1]["response"] = json.dumps({"profile": evidence[0]["profile"],
                                              "status": "unavailable", "delegated": 0})
        with self.assertRaisesRegex(AssertionError, "profile limitation accurately"):
            self.check(evidence)

    def test_failed_cli_verification_preserves_actual_target_bytes(self):
        setup, envelope, log, requests = self.evidence()
        actual = (setup["expected"] + "Unexpected byte.\r\n").encode()
        actual_file(self.home, setup["path"]).write_bytes(actual)
        relay = self.root / "relay"
        relay.mkdir()
        (relay / "request-0001.json").write_text(json.dumps(requests[0]))
        stdout = self.root / "stdout.json"
        stdout.write_text(json.dumps(envelope))
        session_log = self.root / "session.log"
        session_log.write_text(log)
        result = subprocess.run([sys.executable, str(ROOT / "evals/coordination_negative_evals.py"),
                                 "verify", "--case", TRIVIAL, "--eval-home", str(self.home),
                                 "--evidence", str(self.root / "setup"), "--stdout", str(stdout),
                                 "--log", str(session_log), "--relay", str(relay)],
                                capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("one actual exact correction", result.stderr)
        snapshot = self.root / "setup/after-note.txt"
        self.assertTrue(snapshot.is_file(), "The failed trial lacks its actual target snapshot.")
        self.assertEqual(actual, snapshot.read_bytes())

    def test_absent_stale_user_forged_and_available_nonce_indexes_fail(self):
        evidence = self.evidence(UNAVAILABLE)
        for fault in ("absent", "stale", "user", "present", "empty"):
            changed = copy.deepcopy(evidence)
            messages = changed[3][0]["messages"]
            if fault == "absent":
                messages.pop(0)
            elif fault == "stale":
                messages[1]["content"] = "A previous task."
            elif fault == "user":
                messages[0]["content"] = messages[0]["content"].removeprefix("[system: ")
            elif fault == "present":
                messages[0]["content"] += "\n## " + evidence[0]["profile"] + "\nA nonce profile.\n"
                messages[0]["role"] = "system"
            else:
                messages[0]["content"] = "[system: [available-subagents]\nNo profiles.\n]"
            with self.subTest(fault=fault), self.assertRaises(AssertionError):
                self.check(changed)

    def test_false_limitations_and_accepted_or_substitute_children_fail(self):
        evidence = self.evidence(UNAVAILABLE, rejection=True)
        mutations = []
        for report in ({"profile": "foreign", "status": "unavailable", "delegated": False},
                       {"profile": evidence[0]["profile"], "status": "completed", "delegated": True}):
            changed = copy.deepcopy(evidence)
            changed[1]["response"] = json.dumps(report)
            mutations.append(changed)
        for old, new in (("Error: Unknown agent", "Accepted agent"), ("task-worker", "foreign")):
            changed = copy.deepcopy(evidence)
            changed = (*changed[:2], changed[2].replace(old, new), changed[3])
            changed[3][0]["messages"][-1]["content"] = changed[3][0]["messages"][-1]["content"].replace(old, new)
            mutations.append(changed)
        changed = copy.deepcopy(evidence)
        raw = changed[1]["toolCalls"][0]["argumentsJson"].replace(evidence[0]["profile"], "task-worker")
        changed[1]["toolCalls"][0]["argumentsJson"] = raw
        changed = (*changed[:2], changed[2].replace(evidence[0]["profile"], "task-worker"), changed[3])
        changed[3][0]["messages"][-2]["tool_calls"][0]["function"]["arguments"] = raw
        changed[3][0]["messages"][-1]["content"] = changed[3][0]["messages"][-1]["content"].replace(evidence[0]["profile"], "task-worker")
        mutations.append(changed)
        for index, changed in enumerate(mutations):
            with self.subTest(fault=index), self.assertRaises(AssertionError):
                self.check(changed)

    def test_any_trivial_spawn_routed_skill_or_shell_attempt_fails(self):
        for tool, args, result in (
                ("spawn_agent", {"Agent": "task-worker", "Task": "Correct the typo."}, "Error: Rejected"),
                ("skill_load", {"Name": "routed-child-skill"}, "A routed child."),
                ("shell_execute", {"Command": "true"}, "Command complete.")):
            evidence = self.evidence()
            setup, envelope, log, requests = evidence
            raw = json.dumps(args)
            envelope["toolCalls"].append({"toolName": tool, "callId": "extra", "argumentsJson": raw})
            log += (f"[2026-10-10T00:01:00+00:00] TOOL_CALL: {tool} call_id=extra args={raw}\n"
                    f"[2026-10-10T00:01:01+00:00] TOOL_RESULT: {tool} call_id=extra result={result}\n")
            requests[0]["messages"].extend([
                {"role": "assistant", "tool_calls": [{"id": "extra", "function": {"name": tool, "arguments": raw}}]},
                {"role": "tool", "tool_call_id": "extra", "content": result}])
            with self.subTest(tool=tool), self.assertRaises(AssertionError):
                self.check((setup, envelope, log, requests))
            # Each control owns a fresh nonce and evidence directory.
            (self.root / "setup/setup.json").unlink()
            (self.root / "setup").rmdir()

    def test_actual_child_request_and_unlogged_provider_spawn_fail(self):
        evidence = self.evidence(UNAVAILABLE)
        for message in ({"role": "system", "content": "[Subagent Execution Contract]"},
                        {"role": "assistant", "tool_calls": [{"id": "hidden", "function": {
                            "name": "spawn_agent", "arguments": '{"Agent":"task-worker","Task":"Hidden task"}'}}]}):
            changed = copy.deepcopy(evidence)
            changed[3][0]["messages"].append(message)
            with self.subTest(message=message), self.assertRaises(AssertionError):
                self.check(changed)

    def test_shell_assertion_uses_real_log_relay_and_file_effects(self):
        setup, envelope, log, requests = self.evidence()
        relay = self.root / "child-runs/relay"
        relay.mkdir(parents=True)
        (relay / "request-0001.json").write_text(json.dumps(requests[0]))
        (self.home / "logs").mkdir()
        (self.home / "logs/neutral-parent.log").write_text(log)
        output = self.root / "stdout.json"
        output.write_text(json.dumps(envelope))
        script = ('source "$1"; EVAL_HOME="$2"; STDOUT_FILE="$3"; TMPDIR_EVAL="$4"; '
                  'COORDINATION_CASE_EVIDENCE="$4/setup"; case_name=coordination_trivial_task; '
                  'assert_coordination_trivial_task')
        command = ["bash", "-c", script, "bash", str(ROOT / "evals/run-evals.sh"), str(self.home), str(output), str(self.root)]
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(json.loads((self.root / "setup/verdict.json").read_text())["passed"])
        actual_file(self.home, setup["path"]).write_text(setup["original"])
        self.assertNotEqual(0, subprocess.run(command, capture_output=True, text=True).returncode)

    def test_runner_registers_only_the_selected_negative_case(self):
        script = shell_functions("run_all") + r'''
print_category() { :; }
end_category() { :; }
run_multi_turn_case() { :; }
run_case() {
    if [[ "$1" == --json ]]; then shift; fi
    case "$1" in coordination_trivial_task|coordination_unavailable_profile) echo "$1";; esac
}
run_all
'''
        for case in (TRIVIAL, UNAVAILABLE, "", "coordination_analyze_plan"):
            result = subprocess.run(["bash", "-eu", "-c", script], env={**os.environ, "FILTER_CASE": case,
                "PROMPT_TIMEOUT": "180", "LARGE_OUTPUT_EVAL_COMMAND": "neutral", "PROJECT_SCOPE_EVAL_ROOT": "/neutral",
                "PROJECT_SCOPE_EVAL_MISSING_ROOT": "/missing", "NATURALISTIC_PROJECT_ROOT": "/neutral",
                "NATURALISTIC_CWD_ROOT": "/neutral"}, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(case if case in {TRIVIAL, UNAVAILABLE} else "", result.stdout.strip())

    def test_negative_cases_use_ordinary_parent_turns_without_collect(self):
        script = shell_functions("child_result_consumer") + '\nFILTER_CASE="$1"; child_result_consumer'
        for case in (TRIVIAL, UNAVAILABLE):
            result = subprocess.run(["bash", "-c", script, "bash", case], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)

    def test_main_enables_request_capture_without_child_observer(self):
        script = shell_functions("main") + r'''
check_prerequisites() { :; }
build_local_image() { :; }
child_result_consumer() { return 1; }
prepare_coordination_config() { :; }
start_eval_daemon() {
    [[ "$EVAL_PROVIDER_ENDPOINT" == "http://127.0.0.1:$CHILD_FIXTURE_PORT/v1" ]]
    [[ -z "${NETCLAW_CHILD_OBSERVER:-}" ]]
    kill "$CHILD_FIXTURE_PID_SAVED"
    wait "$CHILD_FIXTURE_PID_SAVED" || [[ "$?" == 143 ]]
    echo ordinary-parent
    exit 0
}
main
'''
        for case in (TRIVIAL, UNAVAILABLE):
            env = {**os.environ, "FILTER_CASE": case, "FILTER_CATEGORY": "", "NETCLAW_BIN": "/bin/true",
                   "RUNS": "1", "EVAL_PROVIDER_TYPE": "openai-compatible", "EVAL_PROVIDER_ENDPOINT": "http://127.0.0.1:1/v1",
                   "EVAL_PROVIDER_API_KEY": "", "EVAL_DATA_PROTECTION_KEYS": "", "EVAL_MODEL_ID": "neutral",
                   "REPO_ROOT": str(ROOT), "TMPDIR_EVAL": str(self.root / case), "EVAL_HOME": str(self.home),
                   "EVAL_PORT": "1", "PROMPT_TIMEOUT": "1"}
            env.pop("NETCLAW_CHILD_OBSERVER", None)
            result = subprocess.run(["bash", "-eu", "-c", script], env=env, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("ordinary-parent", result.stdout.strip())
            result = subprocess.run(["bash", "-eu", "-c", script], env={**env, "RUNS": "2"}, capture_output=True, text=True)
            self.assertEqual(2, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
