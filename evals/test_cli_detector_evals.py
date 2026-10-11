"""Verify the detector for a `netclaw` CLI call in a shell_execute command."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest


class NetclawCliDetectorTests(unittest.TestCase):
    def detects(self, command):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "stdout.json"
            output.write_text(json.dumps({"sessionId": "fixture", "response": "", "toolCalls": [{
                "toolName": "shell_execute", "callId": "shell",
                "argumentsJson": json.dumps({"Command": command, "_rationale": "fixture"})}]}))
            script = Path(__file__).with_name("run-evals.sh")
            completed = subprocess.run([
                "bash", "-c", 'source "$1"; STDOUT_FILE="$2"; stdout_json_shell_ran_netclaw_cli',
                "bash", str(script), str(output)], capture_output=True, text=True)
            return completed.returncode == 0

    def test_a_netclaw_command_is_detected(self):
        for command in [
                "netclaw reminder list",
                "netclaw",
                "cd /x && netclaw status",
                "echo hi | netclaw doctor",
                "x=$(netclaw reminder list)",
                "sudo netclaw status",
                "/usr/local/bin/netclaw reminder list",
                "~/.netclaw/bin/netclaw reminder list",
                "NO_COLOR=1 netclaw reminder list",
                "env NO_COLOR=1 netclaw reminder list",
                "cd /tmp\nnetclaw reminder list"]:
            with self.subTest(command=command):
                self.assertTrue(self.detects(command))

    def test_a_path_or_an_operand_is_not_a_netclaw_command(self):
        for command in [
                "ls ~/.netclaw/logs",
                "cat /home/netclaw/.netclaw/config/netclaw.json",
                "ls /home/netclaw",
                "ls /home/netclaw -la",
                "grep netclaw file",
                "grep netclawd file",
                "which netclaw",
                "echo done\nls ~/.netclaw"]:
            with self.subTest(command=command):
                self.assertFalse(self.detects(command))


if __name__ == "__main__":
    unittest.main()
