"""Tests for the review fixes of PR 2513 (trial evidence and seeded prompts).

Each group names the review finding that it pins. Tests marked "guard" pass on
the first PR head. They exist to kill a planted bug (M1, M2, or M5 in the
review). All other tests fail on that head until the fix lands.

Contracts that these tests fix:

  S1  The variant index is `trial_seed % variant_count`. It does not use the
      bash RANDOM generator, whose output changed in bash 5.1.
  S2  A trial that crosses midnight UTC reads the daemon lines of both log
      files. `daemon-session.log` and `daemon-window.log` list the old file
      first, then the new file.
  S3  `raw/assert.json` holds `exit_code` (the status of the assertion),
      `daemon_log` (the base name of the daemon file at the window start),
      `window_lines_before` (lines in that file when the trial window began),
      and `window_lines_after` (lines in that file when the assertion ended).
      `daemon-window.log` is copied after the assertion. A multi-turn trial
      starts its window before turn 1.
  S4  A `NETCLAW_EVAL_TIMEOUT` that is not a whole number of seconds stops the
      run at startup with exit 2 and a message that names the variable. The
      README and the script header define the value as seconds, and the design
      records `deadline_s` as a number. So `5m` is rejected, and it is not
      converted. A missing python3 is reported by the prerequisite check.
  S5  A copied actor log or sub-agent log must resolve under `data/sessions`
      (and a sub-agent log under the session folder). A link into `data/keys`
      or `data/config` is not copied.
  R1  A run without any `daemon-*.log` in `$EVAL_HOME/logs` stops with exit 2
      and a message on stderr that names that folder. There is no
      `no_daemon_log` status.
  S5b A sub-agent log is copied only when its link target is a `*.log` file
      under its own session folder. The name of the link does not count.
  M3  The harness opens `netclaw.db` read-only.
  T1  Multi-turn evidence (M1), a crash of discover-session (M2), the CLI crash
      path, and a daemon file that must not change before the window copy (M5).

The tests run the real run-evals.sh with a fake CLI. They use no model, no
network, no daemon, and no sleep. Each test removes its temp folders.
"""

import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

import test_trial_evidence_seeds as seeds
import test_trial_files as trial_files
import trial_evidence

EVALS = Path(__file__).resolve().parent

DAEMON_15 = "daemon-2026-10-15.log"
DAEMON_16 = "daemon-2026-10-16.log"
BEFORE_LINE = "09:00:00.000 [INF] Netclaw.Boot: before the trial session=signalr/other"

# The fake CLI of test_trial_files, with three additions:
#   FAKE_DAEMON_WRITES    lines "file|text" that are appended to files in logs/
#   FAKE_TOKEN_LINE_FILE  a daemon line that carries this call's own session id
#   FAKE_FUTURE_MTIME     a daemon file that gets a newer mtime than all others
#   a prompt with CRASH   writes a crash log and exits 134, like an aborted CLI
CLI_ADDITIONS = r"""
if [[ -n "${FAKE_DAEMON_WRITES:-}" ]]; then
    while IFS='|' read -r file text; do
        if [[ -n "$file" ]]; then printf '%s\n' "$text" >> "$NETCLAW_HOME/logs/$file"; fi
    done <<< "$FAKE_DAEMON_WRITES"
fi
if [[ -n "${FAKE_TOKEN_LINE_FILE:-}" ]]; then
    printf '10:00:03.000 [INF] Netclaw.Memory: memory_retrieval_final session=%s note=%s\n' \
        "$session" "${prompt// /_}" >> "$NETCLAW_HOME/logs/$FAKE_TOKEN_LINE_FILE"
fi
if [[ -n "${FAKE_FUTURE_MTIME:-}" ]]; then
    touch -d 'now + 1 hour' "$NETCLAW_HOME/logs/$FAKE_FUTURE_MTIME"
fi
if [[ "$prompt" == *CRASH* ]]; then
    printf 'No space left on device\n' > "$NETCLAW_HOME/logs/crash-20261015-131615.log"
    exit 134
fi
"""
FAKE_CLI = trial_files.FAKE_CLI.replace(
    "printf '%s' \"${FAKE_STDOUT-}\"", CLI_ADDITIONS + "printf '%s' \"${FAKE_STDOUT-}\"", 1)
assert FAKE_CLI != trial_files.FAKE_CLI

# The fake assertion can write a daemon line while it runs, as a late event would.
DRIVER = trial_files.DRIVER.replace(
    "assert_demo() {\n",
    "assert_demo() {\n"
    "    if [[ -n \"${ASSERT_DAEMON_LINE:-}\" ]]; then\n"
    "        printf '%s\\n' \"$ASSERT_DAEMON_LINE\" >> \"$EVAL_HOME/logs/daemon-2026-10-15.log\"\n"
    "    fi\n", 1)
assert DRIVER != trial_files.DRIVER


class ReviewHarnessCase(trial_files.HarnessCase):
    def setUp(self):
        # The base setUp writes FAKE_CLI to disk, so patch before it runs.
        for name, value in (("FAKE_CLI", FAKE_CLI), ("DRIVER", DRIVER)):
            patcher = mock.patch.object(trial_files, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        super().setUp()

    def reset_runs(self):
        shutil.rmtree(self.root / "checkout" / "evals" / "runs", ignore_errors=True)

    def lines(self, name, number=1):
        return self.read_text(self.raw(name, number=number)).splitlines()

    def launch(self, kind, prompts, **env):
        """Run the driver and return the process. Unlike run_harness, it expects no DRIVER_DONE."""
        environment = {
            "PATH": os.environ["PATH"],
            "HOME": str(self.root),
            "HARNESS_SCRIPT": str(self.script),
            "NETCLAW_BIN": str(self.cli),
            "NETCLAW_EVAL_RUNS": "1",
            "NETCLAW_EVAL_TIMEOUT": "5",
            "DRIVER_KIND": kind,
            "DRIVER_HOME": str(self.home),
            "DRIVER_TMP": str(self.tmp_eval),
            "DRIVER_RESULTS": str(self.results),
            "FAKE_STATE": str(self.state),
        }
        environment.update({key: str(value) for key, value in env.items()})
        return subprocess.run(["bash", "-c", DRIVER, "bash", *prompts],
                              env=environment, capture_output=True, text=True)


class VariantIndexTests(ReviewHarnessCase):
    VARIANTS = [f"variant-{i:02d}" for i in range(20)]

    # Trial seeds are SHA-256 values, see DeriveSeedTests. The indexes are
    # trial_seed % count, computed once by hand. They hold on every bash version.
    TABLE = (
        (7, 20, [357770588, 65173577, 2882733712], [8, 17, 12]),
        (0, 20, [4172380411, 3027419932, 25254256], [11, 12, 16]),
        (123, 20, [4080040921, 3585062645, 182414050], [1, 5, 10]),
        (123, 7, [4080040921, 3585062645, 182414050], [5, 3, 0]),
        (7, 5, [357770588, 65173577, 2882733712], [3, 2, 2]),
    )

    def test_variant_index_is_the_trial_seed_modulo_the_variant_count(self):
        # S1. Planted bug: take the index from bash RANDOM. Bash 5.1 changed the
        # generator, so the same seed picks another variant on another host.
        for seed, count, trial_seeds, indexes in self.TABLE:
            with self.subTest(seed=seed, variants=count):
                self.reset_runs()
                variants = self.VARIANTS[:count]
                self.run_harness("text", variants, runs=3, seed=seed, FAKE_SESSION="")

                records = [self.read_json(self.raw("variant.json", number=n)) for n in (1, 2, 3)]
                self.assertEqual(trial_seeds, [r["trial_seed"] for r in records])
                self.assertEqual(indexes, [r["variant_index"] for r in records])
                self.assertEqual([count] * 3, [r["variant_count"] for r in records])
                prompts = [self.read_text(self.raw("prompt.txt", number=n)).rstrip("\n")
                           for n in (1, 2, 3)]
                self.assertEqual([variants[i] for i in indexes], prompts)


class MidnightRotationTests(ReviewHarnessCase):
    SESSION = "signalr/this-trial"
    LATE = f"23:59:58.000 [INF] Netclaw.Memory: memory_retrieval_plan session={SESSION}"
    NEUTRAL = "00:00:01.000 [INF] Netclaw.Boot: a line with no session token"
    EARLY = f"00:00:02.000 [INF] Netclaw.Memory: memory_retrieval_final session={SESSION} mode=lexical"

    def run_trial(self, writes):
        return self.run_harness("text", ["p"], FAKE_SESSION=self.SESSION,
                                FAKE_DAEMON_WRITES="\n".join(writes))

    def test_trial_that_crosses_midnight_keeps_the_lines_of_both_daemon_files(self):
        # S2. Planted bug: read only the file that existed before the CLI call.
        # The lines of the new day are lost, and the evidence looks like a
        # trial with no events.
        done = self.run_trial([f"{DAEMON_15}|{self.LATE}", f"{DAEMON_16}|{self.NEUTRAL}",
                               f"{DAEMON_16}|{self.EARLY}"])

        self.assertEqual([self.LATE, self.EARLY], self.lines("daemon-session.log"), done.stderr)
        self.assertEqual([self.LATE, self.NEUTRAL, self.EARLY], self.lines("daemon-window.log"))

    def test_trial_whose_lines_are_all_in_the_new_daemon_file_is_not_empty(self):
        # S2. Planted bug: same as above. This is the reviewer's probe: the
        # day rolls before the first line of the trial, so the old file has
        # no new line and the new file holds all of them.
        done = self.run_trial([f"{DAEMON_16}|{self.NEUTRAL}", f"{DAEMON_16}|{self.EARLY}"])

        self.assertEqual([self.EARLY], self.lines("daemon-session.log"), done.stderr)
        self.assertEqual([self.NEUTRAL, self.EARLY], self.lines("daemon-window.log"))

    def test_trial_keeps_new_tails_from_older_and_current_files_in_date_order_without_old_prefixes(self):
        # Each file contributes only the lines after its baseline offset.
        # The future mtime must not make the older file replace the current file.
        old = self.home / "logs" / "daemon-2026-10-14.log"
        old.write_text("08:00:00.000 [INF] Old.Day: one\n08:00:01.000 [INF] Old.Day: two\n"
                       "08:00:02.000 [INF] Old.Day: three\n")
        os.utime(old, (1_000, 1_000))
        stray = "00:00:05.000 [INF] Old.Day: stray write to an older file"

        self.run_harness("text", ["p"], FAKE_SESSION=self.SESSION,
                         FAKE_DAEMON_WRITES=f"{DAEMON_15}|{self.LATE}\ndaemon-2026-10-14.log|{stray}",
                         FAKE_FUTURE_MTIME="daemon-2026-10-14.log")

        self.assertEqual([self.LATE], self.lines("daemon-session.log"))
        self.assertEqual([stray, self.LATE], self.lines("daemon-window.log"))


class AssertionEvidenceTests(ReviewHarnessCase):
    SESSION = "signalr/this-trial"
    CLI_LINE = f"10:00:01.000 [INF] Netclaw.Memory: memory_retrieval_final session={SESSION}"
    LATE_LINE = "10:00:09.000 [WRN] Netclaw.Memory: memory_recall_degraded session=signalr/cut-session"

    def test_assert_json_records_the_exit_code_of_the_assertion(self):
        # S3. Planted bug: keep `if $assert_fn` and drop the status. A SIGPIPE
        # (141) and a missed match (1) then look the same in the trial folder.
        for rc in (0, 1, 141):
            with self.subTest(assertion_rc=rc):
                self.reset_runs()
                self.results.write_text("")
                self.run_harness("text", ["p"], FAKE_SESSION=self.SESSION, ASSERT_RC=rc)

                # Since PR 2 the stored trial status also depends on the CLI exit
                # and the turn end, which this fake CLI does not write. The
                # assertion result is what assert.json holds.
                self.assertEqual(rc, self.read_json(self.raw("assert.json"))["exit_code"])

    def test_daemon_window_is_copied_after_the_assertion_and_assert_json_gives_its_bounds(self):
        # S3. Planted bug: copy the window before the assertion runs. A line that
        # arrives in between decides the verdict (row 147) and is not in the
        # evidence.
        self.run_harness("text", ["p"], FAKE_SESSION=self.SESSION, ASSERT_RC=141,
                         FAKE_DAEMON_WRITES=f"{DAEMON_15}|{self.CLI_LINE}",
                         ASSERT_DAEMON_LINE=self.LATE_LINE)

        self.assertEqual([self.CLI_LINE, self.LATE_LINE], self.lines("daemon-window.log"))
        record = self.read_json(self.raw("assert.json"))
        self.assertEqual(DAEMON_15, record["daemon_log"])
        self.assertEqual(1, record["window_lines_before"])
        self.assertEqual(3, record["window_lines_after"])

    def test_multi_turn_window_starts_at_the_first_turn(self):
        # S3. Planted bug: use the line count that run_prompt_resume takes for
        # each turn. The window then starts at the last turn, and the lines of
        # turn 1 are missing.
        self.run_harness("multi", ["turn one", "turn two"], ASSERT_RC=141,
                         FAKE_TOKEN_LINE_FILE=DAEMON_15, ASSERT_DAEMON_LINE=self.LATE_LINE)

        window = self.lines("daemon-window.log")
        self.assertEqual(3, len(window), window)
        self.assertIn("note=turn_one", window[0])
        self.assertIn("note=turn_two", window[1])
        self.assertEqual(self.LATE_LINE, window[2])
        record = self.read_json(self.raw("assert.json"))
        self.assertEqual(141, record["exit_code"])
        self.assertEqual(1, record["window_lines_before"])
        self.assertEqual(4, record["window_lines_after"])


class MultiTurnEvidenceTests(ReviewHarnessCase):
    def test_multi_turn_trial_keeps_the_session_evidence_of_every_turn(self):
        # M1 guard. Planted bug: take the snapshot of known logs in each turn.
        # The log of turn 1 is then known at turn 2, and every multi-turn trial
        # records no_session with no CLI log, no actor log, and no daemon lines.
        self.run_harness("multi", ["turn one", "turn two"], FAKE_TOKEN_LINE_FILE=DAEMON_15)

        session = self.read_json(self.raw("session.json"))
        self.assertEqual("ok", session["status"])
        self.assertTrue(session["session_id"].startswith("eval/demo-run1-"), session)
        cli_log = self.read_text(self.raw("cli-session.log"))
        self.assertIn(f"Headless session started: {session['session_id']}", cli_log)
        self.assertIn("PROMPT: turn one", cli_log)
        self.assertIn("PROMPT: turn two", cli_log)
        kept = self.lines("daemon-session.log")
        self.assertEqual(2, len(kept), kept)
        self.assertTrue(all(f"session={session['session_id']} " in line for line in kept))

    def test_cli_crash_is_recorded_with_its_exit_code_and_crash_log(self):
        # The abort must retain the crash log reference and the original exit code.
        self.run_harness("text", ["CRASH now"], runs=2,
                         expect_abort=True, FAKE_SESSION="signalr/crash")

        self.assertEqual(134, self.read_json(self.raw("cli-exit.json"))["exit_code"])
        session = self.read_json(self.raw("session.json"))
        self.assertEqual("ok", session["status"])
        self.assertEqual(["crash-20261015-131615.log"], session["other_new_files"])
        self.assertEqual("No space left on device\n",
                         (self.home / "logs/crash-20261015-131615.log").read_text())
        self.assertEqual(["CRASH now"], (self.state / "prompts.txt").read_text().splitlines())
        self.assertFalse(self.trial_dir(2).exists())
        self.assertFalse(self.results.exists() and self.results.read_text(),
                         "The abort must precede score calculation")


class DiscoverCrashTests(ReviewHarnessCase):
    def test_crash_of_discover_session_stops_the_run_with_exit_2(self):
        # M2 guard. Planted bug: treat any discover-session failure as a finding
        # (WARN, then return). The run goes on with an empty session.json and
        # a trial with no evidence. The helper is replaced by a script that
        # raises for discover-session only, so the test does not depend on file modes or on the user.
        real = self.root / "checkout" / "evals" / "trial_evidence.py"
        kept = real.with_name("trial_evidence_real.py")
        real.rename(kept)
        real.write_text(
            "import runpy, sys\n"
            "if sys.argv[1] == 'discover-session':\n"
            "    raise RuntimeError('boom')\n"
            f"runpy.run_path({str(kept)!r}, run_name='__main__')\n")

        done = self.launch("text", ["p"], FAKE_SESSION="signalr/crash")

        self.assertEqual(2, done.returncode, done.stderr[-400:])
        self.assertNotIn("DRIVER_DONE", done.stdout)
        self.assertIn("ERROR", done.stderr)
        self.assertIn("discover-session", done.stderr)
        self.assertFalse(self.results.exists() and self.results.read_text(),
                         "no result may be stored for a trial without evidence")


class StartupCheckTests(unittest.TestCase):
    """Run run-evals.sh as a process, with fake docker, and a CLI that leaves a mark."""

    TOOLS = ("env", "dirname", "basename", "cat", "date", "mktemp", "rm", "mkdir", "ls", "head",
             "tail", "tr", "sed", "awk", "grep", "find", "sort", "wc", "cp", "mv", "uname", "id",
             "sleep", "timeout", "stdbuf", "jq", "curl", "sqlite3", "cut", "tee", "chmod", "touch",
             "readlink", "realpath", "uuidgen", "xargs", "sh", "true", "false")

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        evals = self.root / "checkout" / "evals"
        evals.mkdir(parents=True)
        for source in EVALS.iterdir():
            if source.is_file() and source.suffix in (".sh", ".py"):
                shutil.copy2(source, evals / source.name)
        template = self.root / "checkout" / "src" / "Netclaw.Cli" / "Resources" / "identity"
        template.mkdir(parents=True)
        (template / "SOUL.template.md").write_text("soul\n")
        (self.root / "tmp").mkdir()
        self.marks = self.root / "marks.txt"
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.fake("docker", f'echo "docker $*" >> "{self.marks}"; exit 1')
        self.fake("netclaw", f'echo "cli $*" >> "{self.marks}"; exit 0')

    def fake(self, name, body):
        path = self.bin / name
        path.write_text(f"#!/bin/sh\n{body}\n")
        path.chmod(0o755)

    def run_script(self, with_system_path, **env):
        if with_system_path:
            path = f"{self.bin}{os.pathsep}{os.environ['PATH']}"
        else:
            # Only these tools resolve. python3 does not.
            tools = self.root / "tools"
            tools.mkdir(exist_ok=True)
            for name in self.TOOLS:
                found = shutil.which(name)
                if found and not (tools / name).exists():
                    (tools / name).symlink_to(found)
            path = f"{self.bin}{os.pathsep}{tools}"
            self.assertIsNone(shutil.which("python3", path=path))
        environment = {
            "PATH": path,
            "HOME": str(self.root),
            "TMPDIR": str(self.root / "tmp"),
            "NETCLAW_BIN": str(self.bin / "netclaw"),
            "NETCLAW_EVAL_NO_BUILD": "1",
            "NETCLAW_EVAL_PROVIDER_TYPE": "ollama",
            "NETCLAW_EVAL_PROVIDER_ENDPOINT": "http://127.0.0.1:1",
            "NETCLAW_EVAL_MODEL_ID": "fake-model",
            "NETCLAW_EVAL_RUNS": "1",
            "NETCLAW_EVAL_SEED": "1",
        }
        environment.update(env)
        return subprocess.run(
            [shutil.which("bash"), str(self.root / "checkout" / "evals" / "run-evals.sh")],
            env=environment, capture_output=True, text=True, stdin=subprocess.DEVNULL, timeout=120)

    def marks_text(self):
        return self.marks.read_text() if self.marks.exists() else ""

    def test_timeout_that_is_not_whole_seconds_stops_the_run_before_any_call(self):
        # S4. Planted bug: pass the value to `timeout` and to `jq --argjson`
        # without a check. `timeout` accepts 5m, but jq does not, so the run
        # fails in the first trial, after the image, the container, and the
        # memory seed. Choice: the README and the script header define the
        # value as seconds and the design records `deadline_s` as a number, so
        # a unit suffix is rejected and not converted.
        for value in ("5m", "1h", "abc", "-5"):
            with self.subTest(timeout=value):
                self.marks.unlink(missing_ok=True)

                done = self.run_script(True, NETCLAW_EVAL_TIMEOUT=value)

                self.assertNotEqual(0, done.returncode)
                self.assertEqual(2, done.returncode, done.stderr[-400:])
                self.assertIn("NETCLAW_EVAL_TIMEOUT", done.stderr)
                self.assertIn(value, done.stderr)
                self.assertNotIn("cli ", self.marks_text())
                self.assertNotIn("docker run", self.marks_text())

    def test_timeout_in_whole_seconds_passes_the_startup_check(self):
        # S4 control. Planted bug: reject every value. The run then stops at
        # startup for a good value. The run still ends early here, because the
        # fake docker fails, but the message must not name the timeout.
        for value in ("60", "240"):
            with self.subTest(timeout=value):
                done = self.run_script(True, NETCLAW_EVAL_TIMEOUT=value)

                self.assertNotIn("NETCLAW_EVAL_TIMEOUT", done.stderr)

    def test_threshold_outside_0_to_1_stops_the_run_at_startup(self):
        # Planted bug: delete the startup check of NETCLAW_EVAL_THRESHOLD. A
        # value such as 80 then passes startup. The run builds the image,
        # starts the container, runs the first case, and only then stops with
        # exit 2 from case-status. The check must stop the run before any call.
        for value in ("80", "1.5", "-0.1", "abc", "0.8x"):
            with self.subTest(threshold=value):
                self.marks.unlink(missing_ok=True)

                done = self.run_script(True, NETCLAW_EVAL_THRESHOLD=value)

                self.assertEqual(2, done.returncode, done.stderr[-400:])
                self.assertIn("NETCLAW_EVAL_THRESHOLD", done.stderr)
                self.assertEqual("", self.marks_text())

    def test_threshold_above_0_to_1_passes_the_startup_check(self):
        # Control. Planted bug: reject every value, or reject 1 or a
        # decimal. The run still ends early here, because the fake docker
        # fails, but the message must not name the threshold.
        for value in ("0.01", "0.8", "0.75", "1", "1.0"):
            with self.subTest(threshold=value):
                done = self.run_script(True, NETCLAW_EVAL_THRESHOLD=value)

                self.assertNotIn("NETCLAW_EVAL_THRESHOLD", done.stderr)

    def test_zero_threshold_stops_the_run_at_startup(self):
        # Owner decision: the threshold is above 0 and at most 1. Planted bug:
        # accept 0. A case with only inconclusive trials then passes with a
        # score of n/a, and an inconclusive result becomes a pass. The check
        # must stop the run before any call and must name the variable.
        for value in ("0", "0.0"):
            with self.subTest(threshold=value):
                self.marks.unlink(missing_ok=True)

                done = self.run_script(True, NETCLAW_EVAL_THRESHOLD=value)

                self.assertEqual(2, done.returncode, done.stderr[-400:])
                self.assertIn("NETCLAW_EVAL_THRESHOLD", done.stderr)
                self.assertEqual("", self.marks_text())

    def test_python3_that_is_new_enough_passes_the_prerequisite_check(self):
        # S4 guard. Planted bug: invert the version test. Then every real run
        # stops at startup because python3 is "too old". The python3 of the
        # test host is 3.9 or later, so the message must not appear.
        done = self.run_script(True, NETCLAW_EVAL_TIMEOUT="60")

        self.assertNotIn("'python3' 3.9", done.stderr)

    def test_missing_python3_is_reported_by_the_prerequisite_check(self):
        # S4. Planted bug: leave python3 out of check_prerequisites. Each
        # trial runs trial_evidence.py, so the run then fails in the first
        # trial with a bare "command not found".
        done = self.run_script(False, NETCLAW_EVAL_TIMEOUT="60")

        self.assertNotEqual(0, done.returncode)
        self.assertIn("python3", done.stderr)
        self.assertNotIn("cli ", self.marks_text())
        self.assertNotIn("docker run", self.marks_text())


class CopyBoundaryTests(ReviewHarnessCase):
    SESSION = "signalr/this-trial"
    SECRET = "SECRET data-protection key material"

    def write_catalog(self, log_path):
        seeds.write_catalog(self.home, [(self.SESSION, log_path)])

    def make_session_log(self):
        log = self.home / "data" / "sessions" / "dirA" / "logs" / "session.log"
        log.parent.mkdir(parents=True)
        log.write_text("actor log of THIS session\n")
        return log

    def make_secrets(self):
        key = self.home / "data" / "keys" / "key-1.xml"
        key.parent.mkdir(parents=True)
        key.write_text(self.SECRET + "\n")
        config_log = self.home / "data" / "config" / "logs" / "agent.log"
        config_log.parent.mkdir(parents=True)
        config_log.write_text(self.SECRET + " (config)\n")
        return key, config_log

    def assert_no_secret_in_runs(self):
        runs = self.root / "checkout" / "evals" / "runs"
        for path in runs.rglob("*"):
            if path.is_file():
                self.assertNotIn("SECRET", path.read_text(errors="replace"),
                                 f"{path} holds data from outside the session folders")

    def test_sub_agent_log_that_links_into_keys_or_config_is_not_copied(self):
        # S5. Planted bug: check sub-agent links against the whole data root.
        # `data/keys` and `data/config` are inside it, so a model that can write
        # in the sessions folder copies key files into evals/runs.
        key, config_log = self.make_secrets()
        log = self.make_session_log()
        subagents = log.parent.parent / "subagents"
        (subagents / "key-link" / "logs").mkdir(parents=True)
        (subagents / "key-link" / "logs" / "agent.log").symlink_to(key)
        (subagents / "folder-link").symlink_to(config_log.parent.parent)
        (subagents / "good" / "logs").mkdir(parents=True)
        (subagents / "good" / "logs" / "agent.log").write_text("sub-agent log\n")
        self.write_catalog("/home/netclaw/.netclaw/sessions/dirA/logs/session.log")

        self.run_harness("text", ["p"], FAKE_SESSION=self.SESSION)

        record = self.read_json(self.raw("actor-log.json"))
        self.assertEqual(["subagents/good/agent.log"], record["subagent_logs"])
        self.assertEqual("sub-agent log\n", self.read_text(self.raw("subagents/good/agent.log")))
        self.assert_no_secret_in_runs()

    def test_sub_agent_log_name_that_links_to_a_file_that_is_not_a_log_is_not_copied(self):
        # S5b. Planted bug: test the name of the link and not its target. The
        # link is called b.log, but it points at a private file in the tmp
        # folder of the same session. The session folder holds more than
        # logs, so the target must be a *.log file too.
        log = self.make_session_log()
        envelope = log.parent.parent
        (envelope / "tmp" / "parent").mkdir(parents=True)
        (envelope / "tmp" / "parent" / "notes.txt").write_text("SECRET tmp file\n")
        subagents = envelope / "subagents"
        (subagents / "good" / "logs").mkdir(parents=True)
        (subagents / "good" / "logs" / "a.log").write_text("sub-agent log\n")
        (subagents / "notes-link" / "logs").mkdir(parents=True)
        (subagents / "notes-link" / "logs" / "b.log").symlink_to(envelope / "tmp" / "parent" / "notes.txt")
        # Control: a link to a log file in the same session folder is fine.
        (subagents / "log-link" / "logs").mkdir(parents=True)
        (subagents / "log-link" / "logs" / "c.log").symlink_to(subagents / "good" / "logs" / "a.log")
        self.write_catalog("/home/netclaw/.netclaw/sessions/dirA/logs/session.log")

        done = self.run_harness("text", ["p"], FAKE_SESSION=self.SESSION)

        record = self.read_json(self.raw("actor-log.json"))
        self.assertEqual("ok", record["status"])
        self.assertEqual(["subagents/good/a.log", "subagents/log-link/c.log"], record["subagent_logs"])
        self.assertEqual(["subagents/notes-link/logs/b.log"], record["refused_subagent_logs"])
        self.assertFalse(self.raw("subagents/notes-link/b.log").exists())
        self.assertTrue(any("notes-link" in line for line in done.stderr.splitlines() if "WARN" in line),
                        done.stderr)
        self.assert_no_secret_in_runs()

    def test_actor_log_path_in_the_data_root_but_outside_the_sessions_is_not_copied(self):
        # S5. Planted bug: accept any catalog path under the data root. A catalog
        # row that names data/keys/key-1.xml (or a link to it) then copies the
        # key as session.log. The status must not be ok.
        key, _ = self.make_secrets()
        sessions = self.home / "data" / "sessions"
        (sessions / "linked" / "logs").mkdir(parents=True)
        (sessions / "linked" / "logs" / "session.log").symlink_to(key)
        for name, log_path in (
            ("path in keys", "/home/netclaw/.netclaw/keys/key-1.xml"),
            ("path in config", "/home/netclaw/.netclaw/config/logs/agent.log"),
            ("link in sessions to keys", "/home/netclaw/.netclaw/sessions/linked/logs/session.log"),
        ):
            with self.subTest(name):
                self.reset_runs()
                (self.home / "logs" / "signalr-this-trial.log").unlink(missing_ok=True)
                self.write_catalog(log_path)

                done = self.run_harness("text", ["p"], FAKE_SESSION=self.SESSION)

                self.assertNotEqual("ok", self.read_json(self.raw("actor-log.json"))["status"])
                self.assertTrue(any(line.lstrip().startswith("WARN") for line in done.stderr.splitlines()),
                                done.stderr)
                self.assertFalse(self.raw("session.log").exists())
                self.assert_no_secret_in_runs()


class MissingDaemonLogTests(ReviewHarnessCase):
    def test_run_without_a_daemon_log_stops_with_exit_2_and_names_the_logs_folder(self):
        # R1. Planted bug: let `ls` fail inside the command substitution under
        # `set -e` and `pipefail`, with its message sent to /dev/null. The run
        # then stops with exit 2 and an empty stderr. A missing daemon log
        # means that the daemon never started, so the run must not go on and
        # must say why.
        for log in (self.home / "logs").glob("daemon-*.log"):
            log.unlink()

        done = self.launch("text", ["p"], FAKE_SESSION="signalr/x")

        self.assertEqual(2, done.returncode, done.stderr[-400:])
        self.assertNotIn("DRIVER_DONE", done.stdout)
        self.assertIn("ERROR", done.stderr)
        self.assertIn("daemon log", done.stderr)
        self.assertIn(str(self.home / "logs"), done.stderr)
        self.assertFalse(self.results.exists() and self.results.read_text(),
                         "no result may be stored for a run without a daemon")


class CatalogReadOnlyTests(unittest.TestCase):
    def test_catalog_is_opened_read_only(self):
        # M3. Planted bug: open the catalog read-write. The daemon holds the
        # file open, and the harness must never write to it. The probe tries a
        # write on the connection that the harness opens, then lets the lookup
        # go on.
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        home = Path(temp.name)
        catalog = seeds.write_catalog(home, [("signalr/x", "/home/netclaw/.netclaw/sessions/a/logs/session.log")])
        before = catalog.read_bytes()
        real_connect = sqlite3.connect
        write_results = []

        def probing_connect(*args, **kwargs):
            connection = real_connect(*args, **kwargs)
            try:
                connection.execute("CREATE TABLE write_probe (x)")
                write_results.append("the write worked")
            except sqlite3.OperationalError as error:
                write_results.append(str(error))
            return connection

        with mock.patch.object(trial_evidence.sqlite3, "connect", probing_connect):
            status, path = trial_evidence.catalog_log_path(catalog, "signalr/x")

        self.assertEqual("ok", status)
        self.assertEqual("/home/netclaw/.netclaw/sessions/a/logs/session.log", path)
        self.assertEqual(1, len(write_results))
        self.assertIn("readonly", write_results[0])
        self.assertEqual(before, catalog.read_bytes())


if __name__ == "__main__":
    unittest.main()
