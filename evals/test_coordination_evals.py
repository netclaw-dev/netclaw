"""Reject false evidence for the targeted coordination discovery case."""

import copy
import json
from pathlib import Path
import subprocess
import tempfile
import unittest


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


if __name__ == "__main__":
    unittest.main()
