"""Verify the long_command_background_job check with synthetic --json envelopes."""

import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

from long_command_evals import MIN_TIMEOUT_SECONDS, evaluate

COMMAND = "/home/netclaw/.netclaw/evals/slow_render"


def call(tool, **arguments):
    return {"toolName": tool, "callId": f"call-{tool}", "argumentsJson": json.dumps(arguments)}


def shell(command=COMMAND, **meta):
    return call("shell_execute", Command=command, _rationale="Run the render script.", **meta)


def envelope(*calls):
    return {"sessionId": "eval/long-command", "response": "render-complete", "toolCalls": list(calls)}


class LongCommandCheckTests(unittest.TestCase):
    def assert_passes(self, *calls):
        report = evaluate(envelope(*calls))
        self.assertTrue(report["passed"], report)
        self.assertIsNone(report["reason"])

    def assert_fails(self, reason, *calls):
        report = evaluate(envelope(*calls))
        self.assertFalse(report["passed"], report)
        self.assertEqual(reason, report["reason"])

    def test_background_job_on_the_first_call_passes(self):
        self.assert_passes(shell(_background=True), call("check_background_job", JobId="job-1"))

    def test_two_foreground_timeouts_then_a_background_job_pass(self):
        self.assert_passes(shell(), shell(), shell(_background=True))

    def test_a_raised_timeout_passes(self):
        self.assert_passes(shell(), shell(_timeout_seconds=300))

    def test_the_skill_page_after_two_foreground_runs_passes(self):
        page = call("skill_read_resource", SkillName="netclaw-operations",
                    Path="references/long-running-commands.md")
        self.assert_passes(shell(), shell(), page)

    def test_three_identical_foreground_calls_fail_even_with_a_later_background_job(self):
        self.assert_fails("identical_foreground_repeated", shell(), shell(), shell(), shell(_background=True))

    def test_the_live_pattern_of_four_identical_calls_fails(self):
        self.assert_fails("identical_foreground_repeated", shell(), shell(), shell(), shell())

    def test_a_changed_short_timeout_is_still_the_same_call(self):
        self.assert_fails("identical_foreground_repeated", shell(), shell(_timeout_seconds=90),
                          shell(_timeout_seconds="120"), shell(_background=True))

    def test_argument_key_spelling_variants_are_recognized(self):
        for meta in ({"Background": True}, {"background": True}, {"_Background": "true"},
                     {"TimeoutSeconds": 200}, {"timeout_seconds": "180"}, {"Timeout": 181}):
            with self.subTest(meta=meta):
                self.assert_passes(call("shell_execute", command=COMMAND, **meta))

    def test_a_timeout_equal_to_the_fixture_sleep_is_not_a_strategy(self):
        # The fixture sleeps 150 s and then prints. A 150 s timeout stops it before the print.
        self.assert_fails("no_long_command_strategy", shell(_timeout_seconds=150))
        self.assert_fails("no_long_command_strategy", shell(_timeout_seconds="179"))
        self.assert_passes(shell(_timeout_seconds=180))

    def test_the_minimum_timeout_is_greater_than_the_fixture_sleep(self):
        script = Path(__file__).with_name("run-evals.sh").read_text()
        fixture = script[script.index("slow_render_executable="):]
        sleep_seconds = int(re.search(r"'sleep (\d+)'", fixture).group(1))
        self.assertEqual(150, sleep_seconds)
        self.assertGreater(MIN_TIMEOUT_SECONDS, sleep_seconds)

    def test_string_booleans_are_recognized(self):
        self.assert_passes(shell(_background="true"))
        self.assert_passes(shell(_background=" TRUE "))
        self.assert_fails("no_long_command_strategy", shell(_background="false"))

    def test_unrelated_shell_calls_are_ignored(self):
        self.assert_passes(shell("echo hello"), shell("echo hello"), shell("echo hello"),
                           shell(_background=True))
        self.assert_fails("no_long_command_strategy", shell("sleep 200", _background=True))

    def test_an_invalid_envelope_fails(self):
        for output in (None, [], {"response": "x", "toolCalls": []},
                       {"sessionId": "s", "toolCalls": []},
                       {"sessionId": "s", "response": "x", "toolCalls": {}},
                       {"sessionId": "s", "response": "x", "toolCalls": [{"argumentsJson": "{}"}]},
                       {"sessionId": "s", "response": "x",
                        "toolCalls": [{"toolName": "shell_execute", "argumentsJson": "{"}]}):
            with self.subTest(output=output):
                report = evaluate(output)
                self.assertFalse(report["passed"])
                self.assertEqual("invalid_envelope", report["reason"])

    def test_no_strategy_fails(self):
        self.assert_fails("no_long_command_strategy")
        self.assert_fails("no_long_command_strategy", shell(), shell())
        wrong_page = call("skill_read_resource", SkillName="netclaw-operations", Path="references/other.md")
        self.assert_fails("no_long_command_strategy", shell(), wrong_page)


class LongCommandShellAssertionTests(unittest.TestCase):
    def run_assertion(self, output):
        with tempfile.TemporaryDirectory() as directory:
            stdout = Path(directory) / "stdout.txt"
            stdout.write_text(json.dumps(output))
            script = Path(__file__).with_name("run-evals.sh")
            completed = subprocess.run(
                ["bash", "-c", 'source "$1"; STDOUT_FILE="$2"; rc=0; assert_long_command_background_job || rc=$?; '
                 'printf "%s\\n%s\\n" "$EVAL_ASSERTION_REPORT" "${EVAL_ASSERTION_DETAILS:-}"; exit "$rc"',
                 "bash", str(script), str(stdout)], capture_output=True, text=True, timeout=30)
            lines = completed.stdout.splitlines()
            report = json.loads(Path(lines[0]).read_text())
            return completed.returncode, report, lines[1] if len(lines) > 1 else ""

    def test_the_bash_assertion_passes_a_background_run(self):
        code, report, details = self.run_assertion(envelope(shell(), shell(_background=True)))
        self.assertEqual(0, code)
        self.assertTrue(report["passed"])
        self.assertEqual("", details)

    def test_the_bash_assertion_fails_the_live_pattern_with_a_reason(self):
        code, report, details = self.run_assertion(envelope(shell(), shell(), shell(), shell()))
        self.assertEqual(1, code)
        self.assertFalse(report["passed"])
        self.assertEqual("identical_foreground_repeated", details)


if __name__ == "__main__":
    unittest.main()
