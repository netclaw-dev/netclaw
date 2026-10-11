"""Verify the assertion for the continuation line of an oversized skill result."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest

STEER = ("\n\n[output truncated to 12000 chars of 19495; continue with tool_output_read "
         "using CallId='load1' and a bounded Start/Limit window instead of re-running]")
NO_STEER = "\n\n[output truncated to 12000 chars of 19495]"


class SpillSteerAssertionTests(unittest.TestCase):
    def passes(self, calls, response, load_result):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory) / "home"
            (home / "logs").mkdir(parents=True)
            output = Path(directory) / "stdout.json"
            output.write_text(json.dumps({"sessionId": "signalr/abc", "response": response, "toolCalls": [
                {"toolName": name, "callId": call_id, "argumentsJson": json.dumps(arguments)}
                for name, call_id, arguments in calls]}))
            (home / "logs" / "signalr-abc.log").write_text(
                "[2026-10-08T16:30:03.0000000+00:00] Headless session started: signalr/abc\n"
                "[2026-10-08T16:30:03.5000000+00:00] TOOL_RESULT: skill_load call_id=load0 "
                "result=Error: Required meta argument '_rationale' must be a non-empty string.\n"
                f"[2026-10-08T16:30:04.0000000+00:00] TOOL_RESULT: skill_load call_id=load1 result=## Probe{load_result}\n"
                "[2026-10-08T16:30:05.0000000+00:00] USAGE: in=1 out=1\n")
            script = Path(__file__).with_name("run-evals.sh")
            completed = subprocess.run([
                "bash", "-c",
                'source "$1"; set +e; EVAL_HOME="$2"; STDOUT_FILE="$3"; assert_complex_skill_spill_steer_single_turn',
                "bash", str(script), str(home), str(output)], capture_output=True, text=True)
            return completed.returncode == 0

    LOAD = ("skill_load", "load1", {"Name": "eval-spill-probe"})
    READ = ("tool_output_read", "read1", {"CallId": "load1", "Start": 6000, "Limit": 8000})

    def test_a_run_that_follows_the_continuation_line_passes(self):
        self.assertTrue(self.passes([self.LOAD, self.READ], "The phrase is cobalt-heron-4471.", STEER))

    def test_a_repeated_load_after_a_validation_error_passes(self):
        rejected = ("skill_load", "load0", {"Name": "eval-spill-probe"})
        self.assertTrue(self.passes(
            [rejected, self.LOAD, self.READ], "The phrase is cobalt-heron-4471.", STEER))

    def test_a_read_through_another_call_id_fails(self):
        other = ("tool_output_read", "read1", {"CallId": "load0", "Start": 6000})
        self.assertFalse(self.passes([self.LOAD, other], "The phrase is cobalt-heron-4471.", STEER))

    def test_a_result_with_no_continuation_line_fails(self):
        self.assertFalse(self.passes([self.LOAD, self.READ], "The phrase is cobalt-heron-4471.", NO_STEER))

    def test_a_run_that_does_not_read_more_fails(self):
        self.assertFalse(self.passes([self.LOAD], "The phrase is cobalt-heron-4471.", STEER))

    def test_a_read_of_the_physical_skill_file_fails(self):
        physical = ("file_read", "f1", {"Path": "/home/netclaw/.netclaw/skills/eval-spill-probe/SKILL.md"})
        self.assertFalse(self.passes(
            [self.LOAD, self.READ, physical], "The phrase is cobalt-heron-4471.", STEER))

    def test_a_search_of_the_skills_folder_fails(self):
        search = ("shell_execute", "s1", {"Command": 'grep -r "verification phrase" ~/.netclaw/skills'})
        self.assertFalse(self.passes(
            [self.LOAD, self.READ, search], "The phrase is cobalt-heron-4471.", STEER))

    def test_a_response_with_no_phrase_fails(self):
        self.assertFalse(self.passes([self.LOAD, self.READ], "I did not find a phrase.", STEER))

    def test_a_run_with_no_tool_call_fails(self):
        self.assertFalse(self.passes([], "The phrase is cobalt-heron-4471.", STEER))


if __name__ == "__main__":
    unittest.main()
