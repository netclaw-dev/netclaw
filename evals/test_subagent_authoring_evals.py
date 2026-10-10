"""Exercise author-guide evidence, contract answers, and the selected harness path."""

import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from child_run_evals import REQUIRED_RATIONALE_ERROR, collect, legacy_observer_mode
from subagent_authoring_evals import CASE, PROMPT, verify
from test_coordination_evals import observed_complete, observed_fixture, observed_pair

ROOT = Path(__file__).resolve().parents[1]
SKILLS = ROOT / "feeds/skills/.system/files"
BODY = (SKILLS / "subagent-authoring/SKILL.md").read_text().split("---", 2)[2].strip()
GUIDE = "## Subagent Authoring\nVersion: 1.5.0\n\n" + BODY + "\n"


def current_answer():
    return {
        "example": {"name": "local-reviewer", "description": "Review the assigned source.",
                    "prompt": "Read the assigned source and report the evidence."},
        "static_tool_call_limit": None,
        "remaining_limits": ["cancellation", "inactivity_timeout", "authorization", "operation_deadline"],
        "start_result_fields": ["run_id", "scope_id", "state", "control_tool"],
        "start_state": "Accepted", "start_control_tool": "check_agent_run", "start_is_task_success": False,
        "terminal_uses_start_call_id": False, "start_completion_cancels_child": False,
        "later_parent_input_cancels_child": False, "parent_control_loader": "load_tool",
        "control_authority": "owning_session_and_original_eligible_requester", "child_peer_control": False,
        "cancel_admission_is_dispatch_closure": False, "cancel_admission_is_terminal": False,
        "partial_evidence_retained": True, "finalization_seconds": 5, "finalization_can_call_model": False,
        "finalization_can_use_task_tools": False, "finalization_can_edit_project": False,
        "finalization_can_approve_tools": False, "unresolved_owner_restart_state": "Lost",
        "owner_restart_resumes_child": False, "cancelled_or_lost_approval_prompt_state": "expired",
        "late_approval_grants_or_retries": False, "structured_findings_review_owner": "parent",
        "recurrent_stop_requires_final_model_call": False,
    }


def author_fixture(operations=None, answer=None):
    fixture = observed_fixture()
    receipt, _, requests, data = fixture
    data["InitialPrompt"] = PROMPT
    requests[0]["messages"][0]["content"] = PROMPT
    receipt.update(case=CASE, initial_prompt_sha256=hashlib.sha256(PROMPT.encode()).hexdigest(),
                   last_reply=json.dumps(current_answer() if answer is None else answer))
    for name, args, result, identifier, failure in (operations if operations is not None else [
            ("skill_load", {"Name": "subagent-authoring", "_rationale": "Read the author guide."}, GUIDE, "guide", None)]):
        observed_pair(fixture, name, args, result, identifier, failure)
    observed_complete(fixture)
    return fixture


class AuthorGuideControls(unittest.TestCase):
    def passes(self, fixture):
        return verify(*fixture[:3], SKILLS, fixture[3])["passed"]

    def rejects(self, fixture, message=None):
        with self.assertRaisesRegex((AssertionError, ValueError), message or "."):
            self.passes(fixture)

    def test_full_current_guide_and_current_contract_pass(self):
        self.assertTrue(self.passes(author_fixture()))

    def test_correct_answer_does_not_replace_required_full_current_guide(self):
        self.assertTrue(self.passes(author_fixture()))
        for result in (GUIDE[:100], GUIDE.replace("Version: 1.5.0", "Version: 1.4.0"),
                       GUIDE.replace("No static tool-call", "A static tool-call")):
            with self.subTest(result=result[:80]):
                self.rejects(author_fixture([("skill_load", {"Name": "subagent-authoring"}, result, "load", None)]),
                             "full current canonical body")
        self.rejects(author_fixture([]), "full canonical guide")
        self.rejects(author_fixture([("skill_load", {"Name": "another-guide"}, GUIDE, "other", None)]),
                     "full canonical guide")

    def test_stale_budget_start_terminal_and_cancel_claims_reject(self):
        for key, value in (("static_tool_call_limit", 30), ("static_tool_call_limit", 60),
                           ("start_state", "Completed"), ("start_is_task_success", True),
                           ("terminal_uses_start_call_id", True), ("cancel_admission_is_terminal", True),
                           ("cancel_admission_is_dispatch_closure", True), ("parent_control_loader", "spawn_agent")):
            answer = current_answer(); answer[key] = value
            with self.subTest(key=key, value=value):
                self.rejects(author_fixture(answer=answer))

    def test_grace_authority_restart_and_partial_claims_reject(self):
        for key, value in (("finalization_can_call_model", True), ("finalization_can_use_task_tools", True),
                           ("finalization_can_edit_project", True), ("finalization_can_approve_tools", True),
                           ("finalization_seconds", 0), ("control_authority", "any_same_session_actor"),
                           ("child_peer_control", True), ("owner_restart_resumes_child", True),
                           ("unresolved_owner_restart_state", "Running"), ("late_approval_grants_or_retries", True),
                           ("cancelled_or_lost_approval_prompt_state", "pending"), ("partial_evidence_retained", False),
                           ("structured_findings_review_owner", "automatic"),
                           ("recurrent_stop_requires_final_model_call", True),
                           ("start_completion_cancels_child", True), ("later_parent_input_cancels_child", True)):
            answer = current_answer(); answer[key] = value
            with self.subTest(key=key):
                self.rejects(author_fixture(answer=answer))

    def test_disclosed_structure_rejects_numeric_flags_missing_keys_and_extra_text(self):
        for key, value in (("start_is_task_success", 0), ("partial_evidence_retained", 1),
                           ("finalization_seconds", True), ("finalization_seconds", 5.0),
                           ("remaining_limits", ["cancellation"]), ("start_result_fields", ["run_id"]),
                           ("example", {"name": "", "description": "A worker.", "prompt": "Review."})):
            answer = current_answer(); answer[key] = value
            with self.subTest(key=key):
                self.rejects(author_fixture(answer=answer))
        for text in ("```json\n" + json.dumps(current_answer()) + "\n```", json.dumps(current_answer()) + " extra",
                     json.dumps(current_answer())[:-1] + ', "start_state": "Accepted"}'):
            fixture = author_fixture(); self.set_reply(fixture, text)
            with self.subTest(text=text[:25]), self.assertRaises((AssertionError, ValueError)):
                self.passes(fixture)
        answer = current_answer(); del answer["start_state"]
        self.rejects(author_fixture(answer=answer))
        answer = current_answer(); answer["extra"] = True
        self.rejects(author_fixture(answer=answer))

    def set_reply(self, fixture, text):
        fixture[0]["last_reply"] = text
        fixture[1][-2]["output"]["Text"] = text

    def test_rejected_load_needs_exact_typed_evidence_and_later_success(self):
        operations = [("skill_load", {"Name": "subagent-authoring"}, REQUIRED_RATIONALE_ERROR, "repair", "invalid_rationale"),
                      ("skill_load", {"Name": "subagent-authoring"}, GUIDE, "guide", None)]
        self.assertTrue(self.passes(author_fixture(operations)))
        self.rejects(author_fixture(operations[:1]), "full canonical guide")
        for code, text in ((None, REQUIRED_RATIONALE_ERROR), ("other", REQUIRED_RATIONALE_ERROR),
                           ("invalid_rationale", REQUIRED_RATIONALE_ERROR + "!")):
            changed = copy.deepcopy(operations); changed[0] = (*changed[0][:2], text, "repair", code)
            with self.subTest(code=code):
                self.rejects(author_fixture(changed))
        fixture = author_fixture(operations); fixture[0]["calls"][0]["success"] = 0
        self.rejects(fixture)

    def test_extra_inline_guide_does_not_earn_or_remove_author_credit(self):
        operations = [("skill_load", {"Name": "netclaw-projects"}, "## Netclaw Projects\nUseful extra guidance.", "extra", None)]
        self.rejects(author_fixture(operations), "full canonical guide")
        operations.append(("skill_load", {"Name": "subagent-authoring"}, GUIDE, "guide", None))
        self.assertTrue(self.passes(author_fixture(operations)))

    def test_actual_optional_resource_requires_exact_current_bytes(self):
        path = "references/child-runs.md"
        content = (SKILLS / "netclaw-operations" / path).read_bytes().decode()
        operations = [("skill_load", {"Name": "subagent-authoring"}, GUIDE, "guide", None),
                      ("skill_read_resource", {"SkillName": "netclaw-operations", "ResourcePath": path},
                       "path: /owned/skills/netclaw-operations/" + path + "\n" + content, "resource", None)]
        self.assertTrue(self.passes(author_fixture(operations)))
        for result in (operations[-1][2][:-1], operations[-1][2].replace("/netclaw-operations/", "/foreign/", 1)):
            changed = copy.deepcopy(operations); changed[-1] = (*changed[-1][:2], result, "resource", None)
            self.rejects(author_fixture(changed), "canonical bytes")

    def test_physical_and_action_attempts_fail_even_with_exact_nonexecution(self):
        for name, args in (("file_read", {"Path": "/private/SKILL.md"}), ("file_list", {"Path": "/private"}),
                           ("file_search", {"Root": "/private"}), ("shell_execute", {"Command": "true"}),
                           ("file_write", {"Path": "/private/note", "Content": "change"}),
                           ("spawn_agent", {"Agent": "task-worker"})):
            operations = [(name, args, REQUIRED_RATIONALE_ERROR, "forbidden", "invalid_rationale"),
                          ("skill_load", {"Name": "subagent-authoring"}, GUIDE, "guide", None)]
            with self.subTest(name=name):
                self.rejects(author_fixture(operations), "physical tool or an action tool")

    def test_routed_acceptance_and_actual_child_context_fail(self):
        operations = [("skill_load", {"Name": "routed-guide", "Task": "Execute a child"},
                       json.dumps({"run_id": "child", "scope_id": "scope", "state": "Accepted",
                                   "control_tool": "check_agent_run"}), "routed", None),
                      ("skill_load", {"Name": "subagent-authoring"}, GUIDE, "guide", None)]
        self.rejects(author_fixture(operations), "inline guide")
        from test_child_run_evals import child
        fixture = author_fixture(); fixture[2].append(child())
        self.rejects(fixture, "child provider")
        operations[0] = ("skill_load", {"Name": "another-inline-guide", "Task": "Read its context only"},
                         "## Another Inline Guide\nA Task argument does not create a child.", "inline", None)
        self.assertTrue(self.passes(author_fixture(operations)))

    def test_all_receipt_and_provider_identity_gates_stay_strict(self):
        self.assertTrue(self.passes(author_fixture()))
        for field, value in (("id", "foreign"), ("name", "other"), ("arguments", {"Name": "other"}),
                             ("result", "foreign"), ("success", 1), ("failure_code", "other"),
                             ("occurrence", 0), ("observed_ns", 999), ("turn", 2)):
            fixture = author_fixture(); fixture[0]["calls"][0][field] = value
            with self.subTest(field=field):
                self.rejects(fixture)
        fixture = author_fixture(); fixture[1][0]["output"]["SessionId"] = "foreign"
        self.rejects(fixture)
        fixture = author_fixture(); fixture[2][0]["messages"][-1]["content"] = "foreign"
        self.rejects(fixture)

    def test_duplicate_occurrences_fail_but_cumulative_captures_pass(self):
        fixture = author_fixture(); fixture[2].append(copy.deepcopy(fixture[2][0]))
        self.assertTrue(self.passes(fixture))
        fixture[2][0]["messages"].extend(copy.deepcopy(fixture[2][0]["messages"][-2:]))
        self.rejects(fixture, "distinct actual")
        fixture = author_fixture([("skill_load", {"Name": "subagent-authoring"}, GUIDE, "guide", None),
                                  ("skill_load", {"Name": "subagent-authoring"}, GUIDE, "guide", None)])
        self.assertTrue(self.passes(fixture))

    def test_nonce_prompt_and_final_boundary_claims_require_actual_evidence(self):
        for key, value in (("prompt_nonce", "foreign"), ("initial_prompt_sha256", "foreign"),
                           ("case", "foreign"), ("completed_turns", 0), ("user_inputs", 2),
                           ("accepted_runs", [{"run_id": "unexpected"}]), ("verified_deliveries", [{}])):
            fixture = author_fixture(); fixture[0][key] = value
            with self.subTest(key=key):
                self.rejects(fixture)
        fixture = author_fixture(); fixture[1][-1]["output"]["TurnOutcome"] = "failed"
        self.rejects(fixture)
        fixture = author_fixture(); fixture[0]["last_reply"] = "a forged answer"
        self.rejects(fixture)
        fixture = author_fixture(); fixture[1].pop()
        self.rejects(fixture)
        fixture = author_fixture(); fixture[1][-1]["output"]["Type"] = "file"
        self.rejects(fixture)

    def test_guide_result_must_precede_the_actual_visible_answer(self):
        self.assertTrue(self.passes(author_fixture()))
        fixture = author_fixture()
        events = fixture[1]
        events.insert(0, events.pop(-2))
        for sequence, event in enumerate(events, 1):
            event.update(sequence=sequence, observed_ns=sequence)
        fixture[0]["calls"][0]["observed_ns"] = 2
        self.rejects(fixture, "full canonical guide")

    def spilled(self, gap=False, changed=False, wrong_owner=False, partial=False):
        marker = ("\n\n[output truncated to 100 chars of " + str(len(GUIDE)) + "; continue with tool_output_read "
                  "using CallId='guide' and a bounded Start/Limit window instead of re-running]")
        operations = [("skill_load", {"Name": "subagent-authoring"}, GUIDE[:100] + marker, "guide", None)]
        chunks = [(0, GUIDE[:4000]), (4001 if gap else 4000, GUIDE[4000:])]
        if partial:
            chunks.pop()
        for index, (start, text) in enumerate(chunks):
            if changed and index == 1:
                text = "!" + text[1:]
            end = start + len(text)
            complete = end == len(GUIDE)
            result = text + f"\n[range start={start} end={end}; next_start={'none' if complete else end}; complete={str(complete).lower()}]"
            operations.append(("tool_output_read", {"CallId": "foreign" if wrong_owner else "guide", "Start": start},
                               result, "continuation-" + str(index), None))
        return author_fixture(operations)

    def test_full_continuations_pass_and_partial_foreign_or_changed_reads_fail(self):
        self.assertTrue(self.passes(self.spilled()))
        for options in ({"gap": True}, {"changed": True}, {"wrong_owner": True}, {"partial": True}):
            with self.subTest(options=options):
                self.rejects(self.spilled(**options))


class AuthorHarnessControls(unittest.TestCase):
    def test_selected_mode_and_collector_keep_one_turn_without_child(self):
        self.assertEqual("turn", legacy_observer_mode(CASE, 1))
        with self.assertRaises(AssertionError):
            legacy_observer_mode(CASE, 2)
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "evidence"
            output.mkdir()
            fixture = author_fixture()
            with patch("child_run_evals.invoke_observer", return_value=(fixture[0], "actual-output")) as invoke, \
                    patch("builtins.print"):
                collect(1234, PROMPT, "", "json", output, CASE, 1)
            self.assertEqual("turn", invoke.call_args.args[3])
            self.assertEqual(PROMPT, invoke.call_args.args[1])
            receipt = json.loads((output / "verified-receipt.json").read_bytes())
            self.assertEqual([], receipt["verified_deliveries"])
            self.assertEqual(CASE, receipt["case"])

    def test_real_selected_run_all_case_prompt_path_preserves_exact_fixed_bytes(self):
        script = r'''
source "$1"
TMPDIR_EVAL="$CONTROL_TEMP"
FILTER_CASE=skill_activation_subagent_authoring
print_category() { :; }; end_category() { :; }; run_multi_turn_case() { :; }
check_daemon_alive() { :; }; resolve_daemon_log() { DAEMON_LOG="$TMPDIR_EVAL/absent.log"; }
observe_child_result() { printf '%s' "$1" > "$CAPTURE"; printf '%s' "$3" > "$CAPTURE.format"; }
assert_skill_activation_subagent_authoring() { return 0; }; store_result() { :; }; store_metrics() { :; }
CATEGORY_SKIPPED=false; CATEGORY_CASES=0; TOTAL_CASES=0
CATEGORY_PASSED=0; PASSED_CASES=0; FAILED_CASES=0
RUNS=1; THRESHOLD=1; FILTER_CATEGORY=""; CHILD_FIXTURE_PORT=1234; PROMPT_TIMEOUT=60
run_all
'''
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            env = {**os.environ, "FILTER_CASE": CASE, "REPO_ROOT": str(ROOT), "CONTROL_TEMP": directory,
                   "CAPTURE": str(root / "actual-prompt"), "PYTHONDONTWRITEBYTECODE": "1"}
            result = subprocess.run(["bash", "-e", "-u", "-c", script, "control", str(ROOT / "evals/run-evals.sh")], env=env, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(PROMPT.encode(), (root / "actual-prompt").read_bytes())
            self.assertEqual(b"json", (root / "actual-prompt.format").read_bytes())

    def test_real_assertion_path_retains_valid_and_invalid_contract_verdicts(self):
        script = r'''
source "$1"
FILTER_CASE=skill_activation_subagent_authoring
TMPDIR_EVAL="$2"
CHILD_LAST_EVIDENCE="$2/child-runs/observer"
assert_skill_activation_subagent_authoring
'''
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            evidence, relay = root / "child-runs/observer", root / "child-runs/relay"
            evidence.mkdir(parents=True); relay.mkdir()
            fixture = author_fixture()
            for expected in (True, False):
                if not expected:
                    answer = current_answer(); answer["static_tool_call_limit"] = 30
                    self_reply = json.dumps(answer)
                    fixture[0]["last_reply"] = self_reply
                    fixture[1][-2]["output"]["Text"] = self_reply
                (evidence / "verified-receipt.json").write_text(json.dumps(fixture[0]))
                (evidence / "observer-input.json").write_text(json.dumps(fixture[3]))
                (evidence / "session-output.jsonl").write_text("".join(json.dumps(event) + "\n" for event in fixture[1]))
                (relay / "request-0001.json").write_text(json.dumps(fixture[2][0]))
                result = subprocess.run(["bash", "-e", "-u", "-c", script, "control",
                                         str(ROOT / "evals/run-evals.sh"), directory], capture_output=True, text=True,
                                        env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
                with self.subTest(expected=expected):
                    self.assertEqual(0 if expected else 1, result.returncode, result.stderr)
                    if expected:
                        self.assertIs(json.loads((evidence / "author-guide-verdict.json").read_text())["passed"], True)
                    else:
                        self.assertEqual(b"", (evidence / "author-guide-verdict.json").read_bytes())
                        self.assertIn("static tool-call budget", (evidence / "author-guide-assertion.stderr").read_text())

    def test_default_suite_keeps_three_variants_and_legacy_assertion(self):
        script = r'''
source "$1"
print_category() { :; }; end_category() { :; }; run_multi_turn_case() { :; }
run_case() {
    [[ "${1:-}" != --json ]] || shift
    [[ "$1" == skill_activation_subagent_authoring ]] || return 0
    shift 2
    printf '%s\0' "$@" > "$CAPTURE"
}
daemon_log_skill_loaded_via_skill_tool() { [[ "$1" == subagent-authoring ]]; }
stdout_no_skill_file_read_called() { return "$READ_STATUS"; }
FILTER_CASE=""; FILTER_CATEGORY=""; READ_STATUS=0; PROMPT_TIMEOUT=60
run_all
assert_skill_activation_subagent_authoring
READ_STATUS=1
if assert_skill_activation_subagent_authoring; then exit 90; fi
case_name=skill_activation_subagent_authoring
if child_result_consumer; then exit 91; fi
'''
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            env = {**os.environ, "CAPTURE": str(root / "variants")}
            result = subprocess.run(["bash", "-e", "-u", "-c", script, "control", str(ROOT / "evals/run-evals.sh")], env=env, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual([b"How do I create a custom subagent in Netclaw?",
                              b"Walk me through authoring a new file-based subagent.",
                              b"What goes in a Netclaw agent definition file?", b""],
                             (root / "variants").read_bytes().split(b"\0"))


if __name__ == "__main__":
    unittest.main()
