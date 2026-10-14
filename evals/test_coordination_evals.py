"""Test coordination eval assertions with valid and invalid outcomes."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest


RUNNER = Path(__file__).with_name("run-evals.sh")
SESSION_KEY = "eval_coordination"


class CoordinationAssertionTests(unittest.TestCase):
    def evaluate(self, assertion, response, tool_calls=(), parent_log="", child_logs=None, coordination=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "stdout.json"
            output.write_text(json.dumps({
                "sessionId": "eval/coordination",
                "response": json.dumps(response),
                "toolCalls": list(tool_calls),
                "coordination": coordination,
            }))
            eval_home = root / "home"
            session_root = eval_home / "data/sessions" / SESSION_KEY
            parent_path = session_root / "logs/session.log"
            parent_path.parent.mkdir(parents=True)
            parent_path.write_text(parent_log)
            for (session_key, child_id), contents in (child_logs or {}).items():
                path = eval_home / "data/sessions" / session_key / "subagents" / child_id / "logs/session.log"
                path.parent.mkdir(parents=True)
                path.write_text(contents)

            command = r'''source "$1"
EVAL_HOME="$2"
STDOUT_FILE="$3"
coordination_session_actor_log() { printf '%s\n' "$EVAL_HOME/data/sessions/eval_coordination/logs/session.log"; }
"$4"'''
            completed = subprocess.run([
                "bash", "-c", command, "bash", str(RUNNER), str(eval_home), str(output), assertion,
            ], capture_output=True, text=True)
            return completed.returncode == 0

    @staticmethod
    def call(name, task):
        return {
            "toolName": name,
            "callId": f"call-{name}-{task[:3]}",
            "argumentsJson": json.dumps({"Task": task}),
        }

    def test_retained_output_accepts_direct_read_without_delegation(self):
        calls = [
            {
                "toolName": "file_read",
                "callId": "source-call",
                "argumentsJson": json.dumps({
                    "Path": "/home/netclaw/.netclaw/workspaces/coordination-retained-inspection/records.txt",
                    "StartLine": 0,
                }),
            },
            {
                "toolName": "tool_output_read",
                "callId": "receipt-read-call",
                "argumentsJson": json.dumps({"CallId": "source-call", "Start": 15000, "Limit": 10000}),
            },
        ]
        self.assertTrue(self.evaluate(
            "assert_coordination_retained_child_inspection",
            {"record": 400, "verification": "cobalt-heron-4471"},
            calls,
            parent_log="Tool result: tool_output_read (call=receipt-read-call) → Record 400 verification phrase: cobalt-heron-4471.\n",
        ))

    def test_retained_output_rejects_missing_read_and_unrelated_child_log(self):
        self.assertFalse(self.evaluate(
            "assert_coordination_retained_child_inspection",
            {"record": 400, "verification": "cobalt-heron-4471"},
            child_logs={("other_eval", "child-1"): "Tool call: tool_output_read (call=read-call)\n"},
        ))

    def test_retained_output_rejects_a_receipt_for_another_source_or_an_error(self):
        source = {
            "toolName": "file_read",
            "callId": "source-call",
            "argumentsJson": json.dumps({
                "Path": "/home/netclaw/.netclaw/workspaces/coordination-retained-inspection/records.txt",
                "StartLine": 0,
            }),
        }
        wrong_receipt = {
            "toolName": "tool_output_read",
            "callId": "receipt-read-call",
            "argumentsJson": json.dumps({"CallId": "other-source", "Start": 15000}),
        }
        response = {"record": 400, "verification": "cobalt-heron-4471"}
        self.assertFalse(self.evaluate(
            "assert_coordination_retained_child_inspection",
            response,
            [source, wrong_receipt],
            parent_log="Tool result: tool_output_read (call=receipt-read-call) → Record 400 verification phrase: cobalt-heron-4471.\n",
        ))
        wrong_receipt["argumentsJson"] = json.dumps({"CallId": "source-call", "Start": 15000})
        self.assertFalse(self.evaluate(
            "assert_coordination_retained_child_inspection",
            response,
            [source, wrong_receipt],
            parent_log="Tool result: tool_output_read (call=receipt-read-call) → Error: CallId is not retained.\n",
        ))

    def test_independent_synthesis_requires_exact_counts_and_two_terminal_children(self):
        tasks = [
            self.call("spawn_agent", "Investigate East requests."),
            self.call("spawn_agent", "Investigate West requests."),
        ]
        terminal_log = "SubAgent completed: task-worker (success=True)\n" * 2
        coordination = {
            "initialResponse": "Two investigations are active.",
            "turnResponses": [
                "Two investigations are active.",
                json.dumps({"eastUnresolved": 2, "westUnresolved": 4, "totalUnresolved": 6, "highestRegion": "west"}),
            ],
            "activeChildrenAtFirstCompletion": 2,
            "completedChildren": 2,
            "turnCompletions": 2,
            "userMessagesSent": 1,
            "automaticContinuation": True,
        }
        self.assertTrue(self.evaluate(
            "assert_coordination_independent_synthesis",
            {"eastUnresolved": 2, "westUnresolved": 4, "totalUnresolved": 6, "highestRegion": "west"},
            tasks,
            terminal_log,
            coordination=coordination,
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_independent_synthesis",
            {"eastUnresolved": 2, "westUnresolved": 14, "totalUnresolved": 16, "highestRegion": "west"},
            tasks,
            terminal_log,
            coordination=coordination,
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_independent_synthesis",
            {"eastUnresolved": 2, "westUnresolved": 4, "totalUnresolved": 6, "highestRegion": "west"},
            tasks,
            "SubAgent completed: task-worker (success=True)\n",
            coordination=coordination,
        ))
        coordination["turnResponses"] = ["Two investigations are active."]
        self.assertFalse(self.evaluate(
            "assert_coordination_independent_synthesis",
            {"eastUnresolved": 2, "westUnresolved": 4, "totalUnresolved": 6, "highestRegion": "west"},
            tasks,
            terminal_log,
            coordination=coordination,
        ))

    def test_artifact_review_binds_defect_and_clean_verdict_to_each_artifact(self):
        task = self.call("spawn_agent", "Review artifact-a.cs and artifact-b.cs.")
        valid = {
            "artifactA": {"verdict": "defect", "observedForTwoPending": True, "expectedForTwoPending": False},
            "artifactB": {"verdict": "clean", "observedForZeroPending": True, "expectedForZeroPending": True},
        }
        self.assertTrue(self.evaluate(
            "assert_coordination_artifact_review", valid, [task],
            "SubAgent completed: task-worker (success=True)\n",
        ))
        swapped = json.loads(json.dumps(valid))
        swapped["artifactA"]["verdict"] = "clean"
        self.assertFalse(self.evaluate(
            "assert_coordination_artifact_review", swapped, [task],
            "SubAgent completed: task-worker (success=True)\n",
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_artifact_review", valid, [task], "",
        ))


if __name__ == "__main__":
    unittest.main()
