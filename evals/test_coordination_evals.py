"""Reject false evidence for the targeted coordination discovery case."""

import copy
import json
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


from child_run_evals import REQUIRED_RATIONALE_ERROR
from coordination_evals import verify_observed


ROOT = Path(__file__).resolve().parent.parent
SKILL = "agent-coordination"
RESOURCE = "references/implement-review.md"
DIRECTORY = ROOT / "feeds/skills/.system/files" / SKILL
BODY = (DIRECTORY / "SKILL.md").read_text().split("---", 2)[2].strip()
WORKFLOW = (DIRECTORY / RESOURCE).read_text().rstrip("\n")
HEADER = f"path: /home/netclaw/.netclaw/skills/.system/{SKILL}/{RESOURCE}\n"


class CoordinationReceiptTests(unittest.TestCase):
    def setUp(self):
        self.calls = [self.call("skill_load", "load", {"Name": SKILL}),
                      self.call("skill_read_resource", "read", {"SkillName": SKILL, "ResourcePath": RESOURCE})]
        self.results = {"load": f"## Agent Coordination\nVersion: 1.0.0\n\n{BODY}",
                        "read": HEADER + WORKFLOW}

    @staticmethod
    def call(name, call_id, arguments):
        return {"toolName": name, "callId": call_id, "argumentsJson": json.dumps(arguments)}

    def passes(self, calls=None, results=None, forged_response=False, reversed_log=False):
        calls = self.calls if calls is None else calls
        results = self.results if results is None else results
        events = []
        for call in calls:
            events.append(f"TOOL_CALL: {call['toolName']} call_id={call['callId']} args={call['argumentsJson']}")
            if call["callId"] in results:
                events.append(f"TOOL_RESULT: {call['toolName']} call_id={call['callId']} result={results[call['callId']]}")
        if reversed_log:
            events.reverse()
        response = "The parent owns implementation and independent review."
        if forged_response:
            response += "\n" + "\n".join(events)
            events = []
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            (home / "logs").mkdir()
            output = home / "stdout.json"
            output.write_text(json.dumps({"sessionId": "fixture", "response": response, "toolCalls": calls}))
            (home / "logs/fixture.log").write_text("\n".join(
                f"[2026-10-09T00:00:{index:02}.0000000+00:00] {event}"
                for index, event in enumerate(events)) + "\n")
            completed = subprocess.run([
                "bash", "-c",
                'source "$1"; EVAL_HOME="$2"; STDOUT_FILE="$3"; assert_skill_coordination_discovery',
                "bash", str(ROOT / "evals/run-evals.sh"), str(home), str(output)
            ], capture_output=True, text=True)
            return completed.returncode == 0

    def test_successful_ordered_logical_receipts_pass(self):
        self.assertTrue(self.passes())

    def test_failed_attempt_then_valid_repair_passes(self):
        rejected = self.call("skill_load", "rejected", {"Name": SKILL})
        self.assertTrue(self.passes([rejected] + self.calls,
                                   {**self.results, "rejected": "Error: Missing rationale"}))

    def test_activation_or_final_response_claim_alone_fails(self):
        self.assertFalse(self.passes(self.calls[:1]))
        self.assertFalse(self.passes(forged_response=True))

    def test_missing_denied_or_wrong_result_fails(self):
        for result in [None, "Error: This tool is not available.", "Resource not found.", "path: wrong\n" + WORKFLOW]:
            with self.subTest(result=result):
                results = dict(self.results)
                results.pop("read")
                if result is not None:
                    results["read"] = result
                self.assertFalse(self.passes(results=results))

    def test_other_workflow_or_eager_all_resource_load_fails(self):
        for resource in ["references/analyze-plan.md", "references/parallel-research.md", "assets/plan.md"]:
            with self.subTest(resource=resource):
                extra = self.call("skill_read_resource", "other", {"SkillName": SKILL, "ResourcePath": resource})
                self.assertFalse(self.passes(self.calls + [extra]))

    def test_physical_reads_or_shell_substitution_fail(self):
        for name, arguments in [
                ("file_read", {"Path": "/home/netclaw/.netclaw/skills/.system/agent-coordination/SKILL.md"}),
                ("file_search", {"Root": "/home/netclaw/.netclaw/skills"}),
                ("file_list", {"Path": "/home/netclaw/.netclaw/skills"}),
                ("shell_execute", {"Command": "cat ~/.netclaw/skills/.system/agent-coordination/SKILL.md"})]:
            with self.subTest(name=name):
                self.assertFalse(self.passes(self.calls + [self.call(name, "physical", arguments)]))

    def test_wrong_order_stale_content_or_duplicate_ids_fail(self):
        self.assertFalse(self.passes(reversed_log=True))
        self.assertFalse(self.passes(results={**self.results, "read": HEADER + "An old workflow."}))
        duplicate = copy.deepcopy(self.calls)
        duplicate[1]["callId"] = "load"
        self.assertFalse(self.passes(duplicate))

    def test_no_child_is_started_for_the_explanation_only_prompt(self):
        self.assertFalse(self.passes(self.calls + [self.call("spawn_agent", "child", {"AgentName": "task-worker"})]))

    def test_real_edits_and_other_action_tools_fail(self):
        for name, arguments in [
                ("file_write", {"Path": "/tmp/patch.cs", "Content": "changed"}),
                ("file_edit", {"Path": "/tmp/patch.cs", "OldText": "old", "NewText": "new"}),
                ("set_reminder", {"Name": "unexpected"}),
                ("check_agent_run", {"run_id": "unexpected", "cancel": True})]:
            with self.subTest(name=name):
                self.assertFalse(self.passes(self.calls + [self.call(name, "action", arguments)]))

    def test_another_skill_route_cannot_start_a_hidden_child(self):
        routed = self.call("skill_load", "route", {"Name": "a-routed-skill", "Task": "Start a child"})
        self.assertFalse(self.passes(self.calls + [routed]))

def observed_fixture():
    prompt = "Explain the implementation and review process without a child or an action."
    data = {"Nonce": "discovery-neutral", "InitialPrompt": prompt, "Mode": "turn", "SessionId": ""}
    receipt = {"status": "observed", "session_id": "session-neutral", "prompt_nonce": data["Nonce"],
               "observer_mode": "turn", "initial_prompt_sha256": hashlib.sha256(prompt.encode()).hexdigest(),
               "case": "skill_coordination_discovery", "prompt_ordinal": 1, "completed_turns": 1,
               "user_inputs": 1, "accepted_runs": [], "verified_deliveries": [], "calls": [],
               "last_reply": "The parent protects the operator checkout and reviews the exact candidate."}
    return receipt, [], [{"messages": [{"role": "user", "content": prompt}]}], data


def observed_pair(fixture, name, arguments, result, identifier, failure=None):
    receipt, events, requests, _ = fixture
    for dto in ({"Type": "tool_call", "CallId": identifier, "ToolName": name, "ArgumentsJson": json.dumps(arguments)},
                {"Type": "tool_result", "CallId": identifier, "ToolName": name, "Result": result, "ToolFailureCode": failure}):
        index = len(events) + 1
        events.append({"sequence": index, "observed_ns": index, "output": {"SessionId": receipt["session_id"], **dto}})
    receipt["calls"].append({"id": identifier, "name": name, "arguments": arguments, "result": result,
                             "failure_code": failure, "success": failure is None, "turn": 1,
                             "occurrence": len(receipt["calls"]) + 1, "observed_ns": len(events) - 1})
    requests[0]["messages"].extend([
        {"role": "assistant", "tool_calls": [{"id": identifier, "function": {"name": name, "arguments": json.dumps(arguments)}}]},
        {"role": "tool", "tool_call_id": identifier, "content": result}])


def observed_complete(fixture):
    receipt, events, *_ = fixture
    for dto in ({"Type": "text", "Text": receipt["last_reply"]},
                {"Type": "turn_completed", "TurnNumber": 1, "TurnOutcome": "completed"}):
        index = len(events) + 1
        events.append({"sequence": index, "observed_ns": index, "output": {"SessionId": receipt["session_id"], **dto}})


class ObservedCoordinationReceiptTests(unittest.TestCase):
    def setUp(self):
        self.fixture = observed_fixture()
        self.add_required()
        observed_complete(self.fixture)

    def add_required(self):
        observed_pair(self.fixture, "skill_load", {"Name": SKILL, "_rationale": "Read the coordination guidance."},
                      "## Agent Coordination\nVersion: 1.0.0\n\n" + BODY, "load")
        observed_pair(self.fixture, "skill_read_resource", {"SkillName": SKILL, "ResourcePath": RESOURCE,
                      "_rationale": "Read the selected workflow."}, HEADER + WORKFLOW, "read")

    def passes(self):
        receipt, events, requests, data = self.fixture
        return verify_observed(receipt, events, requests, DIRECTORY, data)["passed"]

    def reject(self):
        with self.assertRaises((AssertionError, KeyError, ValueError, TypeError)):
            self.passes()

    def test_actual_dto_provider_occurrences_and_visible_answer_pass(self):
        self.assertTrue(self.passes())

    def test_captured_foreign_metadata_rejection_with_opaque_test_identity_passes(self):
        # The retained trial supplied this public argument and canonical error text.
        self.fixture = observed_fixture()
        observed_pair(self.fixture, "skill_load", {"Name": "netclaw-operations"}, REQUIRED_RATIONALE_ERROR,
                      "captured-foreign-attempt", "invalid_rationale")
        self.add_required()
        observed_complete(self.fixture)
        self.assertTrue(self.passes())
        original = copy.deepcopy(self.fixture)
        for fields in ({"failure_code": None}, {"failure_code": "execution_failed"}, {"success": 0},
                       {"occurrence": True}, {"occurrence": 2}, {"id": "forged"}):
            with self.subTest(fields=fields):
                self.fixture = copy.deepcopy(original)
                self.fixture[0]["calls"][0].update(fields)
                self.reject()
        self.fixture = original
        self.fixture[1][1]["output"]["ToolFailureCode"] = None
        self.reject()

    def test_executed_foreign_skill_or_forged_error_text_rejects(self):
        for failure, result in ((None, "## Netclaw Operations\nActual foreign guidance"),
                                (None, REQUIRED_RATIONALE_ERROR), ("invalid_rationale", "Error: Missing rationale")):
            with self.subTest(failure=failure, result=result):
                self.fixture = observed_fixture()
                observed_pair(self.fixture, "skill_load", {"Name": "netclaw-operations"}, result, "foreign", failure)
                self.add_required(); observed_complete(self.fixture)
                self.reject()

    def test_optional_logical_tools_require_typed_canonical_feedback(self):
        for name, arguments in (("load_tool", {"ToolName": "skill_load"}),
                                ("search_tools", {"Query": "skill"}),
                                ("tool_output_read", {"CallId": "prior-output"})):
            with self.subTest(name=name):
                self.fixture = observed_fixture()
                observed_pair(self.fixture, name, arguments, "Successful logical result", "optional")
                self.add_required(); observed_complete(self.fixture)
                self.assertTrue(self.passes())
                self.fixture[0]["calls"][0]["result"] = REQUIRED_RATIONALE_ERROR
                self.fixture[1][1]["output"]["Result"] = REQUIRED_RATIONALE_ERROR
                self.fixture[2][0]["messages"][2]["content"] = REQUIRED_RATIONALE_ERROR
                self.reject()
                metadata = self.fixture[0]["calls"][0]
                metadata.update(success=False, failure_code="invalid_rationale")
                self.fixture[1][1]["output"]["ToolFailureCode"] = "invalid_rationale"
                self.assertTrue(self.passes())
                for success in (None, 0, True):
                    if success is None:
                        metadata.pop("success")
                    else:
                        metadata["success"] = success
                    self.reject()
                    metadata["success"] = False

    def test_physical_and_action_attempts_remain_forbidden_with_exact_rejection(self):
        for name, arguments in (("file_read", {"Path": "/private/skill/SKILL.md"}),
                                ("file_list", {"Path": "/private/skill"}),
                                ("file_search", {"Root": "/private/skill"}),
                                ("shell_execute", {"Command": "cat /private/skill/SKILL.md"}),
                                ("file_write", {"Path": "/private/file", "Content": "change"}),
                                ("spawn_agent", {"Agent": "task-worker"})):
            with self.subTest(name=name):
                self.fixture = observed_fixture()
                observed_pair(self.fixture, name, arguments, REQUIRED_RATIONALE_ERROR, "forbidden", "invalid_rationale")
                self.add_required(); observed_complete(self.fixture)
                self.reject()

    def test_extra_resource_or_duplicate_success_cannot_earn_credit(self):
        for path in ("references/analyze-plan.md", RESOURCE):
            with self.subTest(path=path):
                self.fixture = observed_fixture()
                self.add_required()
                observed_pair(self.fixture, "skill_read_resource", {"SkillName": SKILL, "ResourcePath": path},
                              HEADER + WORKFLOW, "extra")
                observed_complete(self.fixture)
                self.reject()

    def test_wrong_order_incomplete_or_stale_canonical_results_reject(self):
        original = copy.deepcopy(self.fixture)
        for identifier in ("load", "read"):
            with self.subTest(identifier=identifier):
                self.fixture = copy.deepcopy(original)
                for event in self.fixture[1]:
                    if event["output"].get("Type") == "tool_result" and event["output"].get("CallId") == identifier:
                        event["output"]["Result"] = "old partial content"
                self.reject()
        self.fixture = observed_fixture()
        observed_pair(self.fixture, "skill_read_resource", {"SkillName": SKILL, "ResourcePath": RESOURCE}, HEADER + WORKFLOW, "read")
        observed_pair(self.fixture, "skill_load", {"Name": SKILL}, "## Agent Coordination\n" + BODY, "load")
        observed_complete(self.fixture)
        self.reject()

    def test_duplicate_provider_occurrence_and_unresolved_overlap_reject(self):
        original = copy.deepcopy(self.fixture)
        messages = self.fixture[2][0]["messages"]
        messages[1:1] = copy.deepcopy(messages[1:3])
        self.reject()
        self.fixture = copy.deepcopy(original)
        messages = self.fixture[2][0]["messages"]
        messages.insert(2, copy.deepcopy(messages[1]))
        self.reject()

    def test_duplicate_typed_rejection_cannot_invent_an_actual_occurrence(self):
        self.fixture = observed_fixture()
        observed_pair(self.fixture, "skill_load", {"Name": "netclaw-operations"}, REQUIRED_RATIONALE_ERROR,
                      "captured-foreign-attempt", "invalid_rationale")
        self.add_required(); observed_complete(self.fixture)
        self.assertTrue(self.passes())
        messages = self.fixture[2][0]["messages"]
        messages[1:1] = copy.deepcopy(messages[1:3])
        self.reject()

    def test_cumulative_captures_and_completed_identifier_reuse_pass(self):
        receipt, events, requests, _ = self.fixture
        for event in events:
            dto = event["output"]
            if dto.get("CallId") == "read": dto["CallId"] = "load"
        receipt["calls"][1]["id"] = "load"
        for message in requests[0]["messages"]:
            if message.get("tool_call_id") == "read": message["tool_call_id"] = "load"
            for call in message.get("tool_calls", []):
                if call["id"] == "read": call["id"] = "load"
        requests.append(copy.deepcopy(requests[0]))
        self.assertTrue(self.passes())

    def test_identical_completed_reuse_requires_each_actual_provider_occurrence(self):
        self.fixture = observed_fixture()
        for _ in range(2):
            observed_pair(self.fixture, "load_tool", {"ToolName": "skill_load"}, "Tool loaded", "completed-reuse")
        self.add_required(); observed_complete(self.fixture)
        self.fixture[2].append(copy.deepcopy(self.fixture[2][0]))
        self.assertTrue(self.passes())
        for request in self.fixture[2]:
            del request["messages"][1:3]
        self.reject()

    def test_foreign_dto_ids_session_or_provider_values_reject(self):
        original = copy.deepcopy(self.fixture)
        for kind in ("dto-id", "session", "provider-result", "provider-arguments"):
            with self.subTest(kind=kind):
                self.fixture = copy.deepcopy(original)
                if kind == "dto-id":
                    for event in self.fixture[1]:
                        if event["output"].get("CallId"): event["output"]["CallId"] = "foreign-" + event["output"]["CallId"]
                elif kind == "session": self.fixture[1][0]["output"]["SessionId"] = "foreign"
                elif kind == "provider-result": self.fixture[2][0]["messages"][2]["content"] = "forged"
                else: self.fixture[2][0]["messages"][1]["tool_calls"][0]["function"]["arguments"] = json.dumps({"Name": SKILL, "_rationale": "Changed intent."})
                self.reject()

    def test_only_provider_execution_hints_may_differ(self):
        row = self.fixture[0]["calls"][0]
        row["arguments"] = {**row["arguments"], "_timeout_seconds": 30, "_background": False}
        self.fixture[1][0]["output"]["ArgumentsJson"] = json.dumps(row["arguments"])
        self.assertTrue(self.passes())

    def test_forged_final_answer_boundary_and_numeric_flags_reject(self):
        original = copy.deepcopy(self.fixture)
        for kind in ("reply", "boundary", "numeric-boundary", "empty-text", "numeric-success", "numeric-turns", "child"):
            with self.subTest(kind=kind):
                self.fixture = copy.deepcopy(original)
                receipt, events, *_ = self.fixture
                if kind == "reply": receipt["last_reply"] = "Forged answer"
                elif kind == "boundary": events[-1]["output"]["TurnOutcome"] = "failed"
                elif kind == "numeric-boundary": events[-1]["output"]["TurnNumber"] = True
                elif kind == "empty-text": events[-2]["output"]["Text"] = ""
                elif kind == "numeric-success": receipt["calls"][0]["success"] = 1
                elif kind == "numeric-turns": receipt["completed_turns"] = True
                else: receipt["accepted_runs"] = [{"run_id": "unexpected"}]
                self.reject()

    def test_actual_selected_shell_path_uses_turn_evidence_without_headless_log(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = root / "fixture.json"
            fixture.write_text(json.dumps(self.fixture))
            observer = root / "observer"
            observer.write_text('''#!/usr/bin/env python3
import hashlib,json,os,sys
from pathlib import Path
input=json.loads(Path(sys.argv[1]).read_text())
assert input['Mode']=='turn' and input['TimeoutSeconds']==120
receipt,events,requests,_=json.loads(Path(os.environ['FIXTURE']).read_text())
receipt.update(prompt_nonce=input['Nonce'],initial_prompt_sha256=hashlib.sha256(input['InitialPrompt'].encode()).hexdigest())
root=Path(input['EvidenceDirectory'])
(root/'observer-receipt.json').write_text(json.dumps(receipt))
(root/'session-output.jsonl').write_text(''.join(json.dumps(e)+'\\n' for e in events))
relay=Path(os.environ['TMPDIR_EVAL'])/'child-runs/relay'
relay.mkdir(parents=True)
for n,request in enumerate(requests): (relay/f'request-{n:04}.json').write_text(json.dumps(request))
print(json.dumps({'sessionId':receipt['session_id'],'response':receipt['last_reply'],'toolCalls':[]}))
''')
            observer.chmod(0o700)
            script = '''source "$1"
TMPDIR_EVAL="$DISCOVERY_RUN"
EVAL_HOME="$DISCOVERY_HOME"
FILTER_CASE=skill_coordination_discovery
resolve_daemon_log() { DAEMON_LOG=/absent-neutral-log; }
case_name=skill_coordination_discovery
run_prompt 'Explain the process without an action.' json || exit $?
assert_skill_coordination_discovery || exit $?
cat "$CHILD_LAST_EVIDENCE/discovery-verdict.json"
FILTER_CASE=''
if child_result_consumer; then exit 51; fi
'''
            env = {**os.environ, "FIXTURE": str(fixture), "FILTER_CASE": "skill_coordination_discovery",
                   "DISCOVERY_RUN": str(root / "run"), "DISCOVERY_HOME": str(root / "missing-headless-home"),
                   "TMPDIR_EVAL": str(root / "run"), "EVAL_HOME": str(root / "missing-headless-home"),
                   "NETCLAW_CHILD_OBSERVER": str(observer), "CHILD_FIXTURE_PORT": "1", "EVAL_PORT": "1",
                   "PROMPT_TIMEOUT": "120", "NETCLAW_EVAL_TIMEOUT": "120"}
            (root / "run").mkdir()
            result = subprocess.run(["bash", "-c", script, "bash", str(ROOT / "evals/run-evals.sh")],
                                    env=env, capture_output=True, text=True, timeout=15)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertTrue(json.loads(result.stdout)["passed"])


if __name__ == "__main__":
    unittest.main()
