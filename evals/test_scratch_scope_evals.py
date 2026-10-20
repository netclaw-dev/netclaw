"""Verify the oracle for managed scratch and reusable grant guidance."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest


class ScratchScopeOracleTests(unittest.TestCase):
    def accepts(self, response, tool_calls=None, skill_loaded=True, session_id="fixture"):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "stdout.json"
            output.write_text(json.dumps({
                "sessionId": session_id,
                "response": response,
                "toolCalls": tool_calls or [],
            }))
            script = Path(__file__).with_name("run-evals.sh")
            completed = subprocess.run([
                "bash", "-c",
                'source "$1"; STDOUT_FILE="$2"; '
                'daemon_log_skill_loaded_via_skill_tool() { [[ "$1" == netclaw-operations && "$fixture_skill_loaded" == yes ]]; }; '
                'fixture_skill_loaded="$3"; assert_approval_scratch_scope_guidance',
                "bash", str(script), str(output), "yes" if skill_loaded else "no",
            ], capture_output=True, text=True)
            return completed.returncode == 0

    def test_accepts_the_required_policy_choices(self):
        self.assertTrue(self.accepts("subcommands=separate scratch=managed cwd=preserve"))

    def test_rejects_each_incorrect_policy_choice(self):
        for response in [
            "subcommands=shared scratch=managed cwd=preserve",
            "subcommands=separate scratch=global cwd=preserve",
            "subcommands=separate scratch=managed cwd=change",
        ]:
            with self.subTest(response=response):
                self.assertFalse(self.accepts(response))

    def test_rejects_a_shell_call(self):
        self.assertFalse(self.accepts(
            "subcommands=separate scratch=managed cwd=preserve",
            [{"toolName": "shell_execute", "callId": "shell", "argumentsJson": "{}"}],
        ))

    def test_requires_the_skill_and_a_valid_envelope(self):
        response = "subcommands=separate scratch=managed cwd=preserve"
        self.assertFalse(self.accepts(response, skill_loaded=False))
        self.assertFalse(self.accepts(response, session_id=""))


if __name__ == "__main__":
    unittest.main()
