"""Exercise timeout failure and cleanup through the eval shell runner."""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


RUNNER = Path(__file__).with_name("run-evals.sh")


class RunEvalsTimeoutTests(unittest.TestCase):
    def prepare(self, root):
        root = Path(root)
        tools = root / "tools"
        tools.mkdir()
        repo = root / "repo"
        (repo / "evals/runs").mkdir(parents=True)
        assets = root / "assets"
        home = root / "home"
        (home / "logs").mkdir(parents=True)
        (home / "logs/daemon-2026-10-14.log").write_text("fixture daemon log\n")
        captures = root / "captures"
        captures.mkdir()
        logs = root / "logs"
        logs.mkdir()

        (tools / "timeout").write_text("""#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$TIMEOUT_LOG"
shift
"$@"
command_status=$?
exit "${FAKE_TIMEOUT_STATUS:-$command_status}"
""")
        (tools / "docker").write_text("""#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$DOCKER_LOG"
if [[ "$1" == image && "$2" == inspect ]]; then
    printf '%s\\n' 'sha256:fixture-image'
    exit 0
fi
if [[ "$1" == exec ]]; then exit 1; fi
exit 0
""")
        (tools / "stdbuf").write_text("""#!/usr/bin/env bash
shift 2
exec "$@"
""")
        (tools / "dotnet").write_text("""#!/usr/bin/env bash
printf '%s\\n' "observer $*" >> "$CLI_LOG"
printf '%s' "$FAKE_PROMPT_STDOUT"
printf '%s' "$FAKE_PROMPT_STDERR" >&2
""")
        cli = root / "netclaw"
        cli.write_text("""#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$CLI_LOG"
printf '%s' "$FAKE_PROMPT_STDOUT"
printf '%s' "$FAKE_PROMPT_STDERR" >&2
""")
        for path in [tools / "timeout", tools / "docker", tools / "stdbuf", tools / "dotnet", cli]:
            path.chmod(0o755)

        return {
            "root": root, "tools": tools, "repo": repo, "assets": assets, "home": home,
            "captures": captures, "logs": logs, "cli": cli,
        }

    def invoke_prompt(self, directory, kind, status=124, run_count=3, run_id="timeout-proof"):
        paths = self.prepare(directory)
        marker = paths["root"] / "runner-events.txt"
        prompt_stdout = json.dumps({"sessionId": "eval/timeout", "response": "partial success"})
        script = r'''source "$1"
PATH="$TOOLS:$PATH"
REPO_ROOT="$REPO"
EVAL_ASSET_ROOT="$ASSETS"
EVAL_CONTAINER_NAME=netclaw-eval-owned
NETCLAW_IMAGE=fixture-image
NETCLAW_BIN="$CLI"
EVAL_HOME="$HOME_DIR"
TMPDIR_EVAL="$CAPTURES"
RUN_ID="$TEST_RUN_ID"
STARTED_AT=now
RESULTS_DB=
DAEMON_LOG="$HOME_DIR/logs/daemon.log"
RUNS="$RUN_COUNT"
THRESHOLD=1
PROMPT_TIMEOUT=1
MAX_PROMPT_TIMEOUT=1
FILTER_CASE=
FILTER_CATEGORY=
CATEGORY_SKIPPED=false
CATEGORY_CASES=0
CATEGORY_PASSED=0
TOTAL_CASES=0
PASSED_CASES=0
NETCLAW_EVAL_CASE=
TIMEOUT_LOG="$LOG_DIR/timeout.log"
DOCKER_LOG="$LOG_DIR/docker.log"
CLI_LOG="$LOG_DIR/cli.log"
FAKE_PROMPT_STDOUT="$PROMPT_STDOUT"
FAKE_PROMPT_STDERR='forced timeout diagnostic'
FAKE_TIMEOUT_STATUS="$PROMPT_STATUS"
export TIMEOUT_LOG DOCKER_LOG CLI_LOG FAKE_PROMPT_STDOUT FAKE_PROMPT_STDERR FAKE_TIMEOUT_STATUS
trap cleanup_eval_env EXIT
check_daemon_alive() { :; }
setup_timeout_case() { printf 'setup:%s\n' "$1" >> "$EVENTS"; }
assert_timeout_case() { printf 'assert\n' >> "$EVENTS"; return 0; }
assert_resume_case() { printf 'assert-resume\n' >> "$EVENTS"; return 0; }
assert_observer_case() { printf 'assert-observer\n' >> "$EVENTS"; return 0; }
store_result() { printf 'score\n' >> "$EVENTS"; }
store_metrics() { printf 'metrics\n' >> "$EVENTS"; }
case "$KIND" in
  prompt) run_case --json timeout_case 'timeout control' 'first prompt' 'second prompt';;
  resume) run_multi_turn_case resume_case 'timeout control' 'first prompt' 'second prompt';;
  observer) run_case --json --coordination-observer observer_case 'timeout control' 'first prompt' 'second prompt';;
  warmup) seed_eval_memories; printf 'later-case\\n' >> "$EVENTS";;
esac
printf 'suite-continued\n' >> "$EVENTS"
'''
        env = {
            "TOOLS": str(paths["tools"]), "REPO": str(paths["repo"]),
            "ASSETS": str(paths["assets"]), "CLI": str(paths["cli"]),
            "HOME_DIR": str(paths["home"]), "CAPTURES": str(paths["captures"]),
            "LOG_DIR": str(paths["logs"]), "EVENTS": str(marker), "KIND": kind,
            "PROMPT_STATUS": str(status), "PROMPT_STDOUT": prompt_stdout,
            "RUN_COUNT": str(run_count), "TEST_RUN_ID": run_id,
        }
        if kind == "warmup":
            fixtures = paths["assets"] / "evals/fixtures"
            fixtures.mkdir(parents=True)
            (fixtures / "eval-memories.json").write_text(json.dumps({"seedDocuments": []}))
            (paths["repo"] / "evals/seed-memories.py").write_text("pass\n")

        completed = subprocess.run(
            ["bash", "-c", script, "timeout-test", str(RUNNER)],
            env={**os.environ, **env}, capture_output=True, text=True, timeout=10,
        )
        return paths, marker, completed

    def assert_timeout_aborts_and_archives(self, kind, status):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        paths, marker, completed = self.invoke_prompt(directory.name, kind, status)
        self.assertEqual(2, completed.returncode, completed.stderr)
        events = marker.read_text() if marker.exists() else ""
        self.assertNotIn("assert", events)
        self.assertNotIn("score", events)
        self.assertNotIn("suite-continued", events)
        archive = paths["repo"] / "evals/runs/timeout-proof/stdout"
        status_files = list(archive.glob("prompt_status_*.txt"))
        self.assertEqual(1, len(status_files))
        self.assertEqual(str(status), status_files[0].read_text().strip())
        stdout_files = list(archive.glob("stdout_*.txt"))
        stderr_files = list(archive.glob("stderr_*.txt"))
        self.assertTrue(stdout_files)
        self.assertTrue(stderr_files)
        self.assertTrue(any("partial success" in path.read_text() for path in stdout_files))
        self.assertTrue(any("forced timeout diagnostic" in path.read_text() for path in stderr_files))
        docker_calls = (paths["logs"] / "docker.log").read_text().splitlines()
        self.assertIn("stop netclaw-eval-owned", docker_calls)
        self.assertFalse(any(call.startswith("stop ") and call != "stop netclaw-eval-owned"
                             for call in docker_calls))
        timeout_calls = (paths["logs"] / "timeout.log").read_text().splitlines()
        self.assertEqual(1, len(timeout_calls))
        return paths, events, timeout_calls, completed

    def test_timeout_aborts_before_scoring_or_a_later_trial(self):
        _, events, timeout_calls, _ = self.assert_timeout_aborts_and_archives("prompt", 124)
        self.assertEqual("setup:1", events.strip())
        self.assertEqual(1, len(timeout_calls))

    def test_other_nonzero_prompt_status_is_retained_and_stops_the_suite(self):
        _, _, _, completed = self.assert_timeout_aborts_and_archives("prompt", 7)
        self.assertIn("exited with status 7", completed.stderr)

    def test_resume_and_observer_timeouts_stop_before_the_next_prompt(self):
        for kind in ("resume", "observer"):
            with self.subTest(kind=kind):
                self.assert_timeout_aborts_and_archives(kind, 124)

    def test_memory_warmup_timeout_uses_the_same_failure_and_cleanup_path(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        paths, marker, completed = self.invoke_prompt(directory.name, "warmup", 124)
        self.assertEqual(2, completed.returncode, completed.stderr)
        self.assertFalse(marker.exists())
        archive = paths["repo"] / "evals/runs/timeout-proof/stdout"
        status_files = list(archive.glob("prompt_status_*.txt"))
        self.assertEqual(1, len(status_files))
        self.assertEqual("124", status_files[0].read_text().strip())
        self.assertTrue(list(archive.glob("stdout_*_warmup.txt")))
        self.assertTrue(list(archive.glob("stderr_*_warmup.txt")))
        docker_calls = (paths["logs"] / "docker.log").read_text().splitlines()
        self.assertEqual(1, len([call for call in docker_calls if call.startswith("stop ")]))
        self.assertIn("stop netclaw-eval-owned", docker_calls)

    def test_successful_multi_turn_case_keeps_both_prompts_and_scores(self):
        with tempfile.TemporaryDirectory() as directory:
            # Arrange and act through the same shell caller as the failure cases.
            paths, marker, completed = self.invoke_prompt(
                directory, "resume", status=0, run_count=1, run_id="success-proof"
            )

            # Assert both prompts complete, score, and retain successful status.
            self.assertEqual(0, completed.returncode, completed.stderr)
            self.assertIn("[PASS]", completed.stdout)
            self.assertIn("score", marker.read_text())
            self.assertEqual(2, len((paths["logs"] / "cli.log").read_text().splitlines()))
            status_files = list((paths["repo"] / "evals/runs/success-proof/stdout").glob("prompt_status_*.txt"))
            self.assertEqual(["0", "0"], sorted(path.read_text().strip() for path in status_files))


if __name__ == "__main__":
    unittest.main()
