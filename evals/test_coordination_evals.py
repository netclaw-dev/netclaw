"""Test the coordination eval oracles with positive and negative controls."""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


RUNNER = Path(__file__).with_name("run-evals.sh")


class CoordinationAssertionTests(unittest.TestCase):
    def passes(self, assertion, response, tool_calls, child_log=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "stdout.json"
            output.write_text(json.dumps({
                "sessionId": "eval/coordination",
                "response": response,
                "toolCalls": tool_calls,
            }))
            marker = root / "marker"
            marker.touch()
            os.utime(marker, ns=(1, 1))
            eval_home = root / "home"
            if child_log is not None:
                log = eval_home / "data/sessions/parent/subagents/child/logs/session.log"
                log.parent.mkdir(parents=True)
                log.write_text(child_log)

            completed = subprocess.run([
                "bash", "-c",
                'source "$1"; EVAL_HOME="$2"; STDOUT_FILE="$3"; '
                'COORDINATION_LOG_MARKER="$4"; "$5"',
                "bash", str(RUNNER), str(eval_home), str(output), str(marker), assertion,
            ], capture_output=True, text=True)
            return completed.returncode == 0

    @staticmethod
    def call(name, task="inspect the assigned scope"):
        return {
            "toolName": name,
            "callId": f"call-{name}",
            "argumentsJson": json.dumps({"Task": task}),
        }

    def test_retained_child_inspection_requires_child_read_and_correct_evidence(self):
        calls = [
            self.call("skill_load"),
            self.call("spawn_agent", "Inspect the retained call result"),
        ]
        self.assertTrue(self.passes(
            "assert_coordination_retained_child_inspection",
            "Record 160 holds cobalt-heron-4471.",
            calls,
            "tool_output_read CallId=call-skill_load result=cobalt-heron-4471",
        ))
        self.assertFalse(self.passes(
            "assert_coordination_retained_child_inspection",
            "Record 160 holds cobalt-heron-4471.",
            calls,
            "file_read path=/skills/eval-spill-probe/SKILL.md",
        ))

    def test_independent_synthesis_requires_two_distinct_tasks_and_the_right_result(self):
        calls = [
            self.call("spawn_agent", "Investigate East: 8 failed and 6 recovered."),
            self.call("spawn_agent", "Investigate West: 11 failed and 7 recovered."),
        ]
        self.assertTrue(self.passes(
            "assert_coordination_independent_synthesis",
            "There are 6 unresolved requests. West has more.",
            calls,
        ))
        self.assertFalse(self.passes(
            "assert_coordination_independent_synthesis",
            "There are 4 unresolved requests. West has more.",
            calls[:1],
        ))

    def test_artifact_review_requires_the_defect_and_clean_control(self):
        calls = [self.call("spawn_agent", "Review both artifacts independently.")]
        self.assertTrue(self.passes(
            "assert_coordination_artifact_review",
            "Artifact A fails for an empty set. Artifact B is correct and has no defect.",
            calls,
            "SubAgent reviewer completed the artifact review",
        ))
        self.assertFalse(self.passes(
            "assert_coordination_artifact_review",
            "Artifact A is correct. Artifact B is correct and has no defect.",
            calls,
            "SubAgent reviewer completed the artifact review",
        ))


if __name__ == "__main__":
    unittest.main()
