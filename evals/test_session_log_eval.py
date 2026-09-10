"""Verify the session-log eval with synthetic tool results."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest


class SessionLogEvalTests(unittest.TestCase):
    def check_trace(self, results, expected, shell=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            logs = root / "logs"
            logs.mkdir()
            calls = [{"toolName": "spawn_agent", "callId": "spawn", "argumentsJson": "{}"}]
            for index, result in enumerate(results):
                calls.append({"toolName": "file_read", "callId": f"read-{index}", "argumentsJson": json.dumps({
                    "Path": "/home/netclaw/.netclaw/sessions/fixture/subagents/child/logs/session.log"})})
            if shell:
                calls.append({"toolName": "shell_execute", "callId": "shell", "argumentsJson": "{}"})
            output = root / "stdout.json"
            output.write_text(json.dumps({"sessionId": "fixture", "response": "a log line", "toolCalls": calls}))
            (logs / "fixture.log").write_text("\n".join(
                f"TOOL_RESULT: file_read call_id=read-{index} result={result}"
                for index, result in enumerate(results) if result is not None))
            script = Path(__file__).with_name("run-evals.sh")
            completed = subprocess.run([
                "bash", "-c", 'source "$1"; EVAL_HOME="$2"; STDOUT_FILE="$3"; assert_parent_child_log_handoff',
                "bash", str(script), str(root), str(output)], capture_output=True, text=True)
            self.assertEqual(expected, completed.returncode == 0, completed.stderr)

    def test_success_after_a_corrected_call_satisfies_the_handoff(self):
        self.check_trace(["Error: Missing rationale", "a log line"], True)

    def test_a_denied_or_unexecuted_read_is_insufficient(self):
        for results in [["Error: Access denied"], [None], []]:
            with self.subTest(results=results):
                self.check_trace(results, False)

    def test_shell_use_still_fails(self):
        self.check_trace(["a log line"], False, shell=True)


if __name__ == "__main__":
    unittest.main()
