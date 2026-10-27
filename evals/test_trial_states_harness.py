"""Specs for PR 2 of the eval harness rework, at the harness level.

`test_trial_verdict.py` fixes the rules. These tests run the real
`run-evals.sh` (`run_case`, `run_multi_turn_case`, `main`) with a fake CLI and
check what the operator and the stored results see. They use no model, no
network, no daemon, and no sleep. The fake CLI exits 124 at once when it plays
a cut-off. Failed CLI calls abort the suite with exit 2. Score tests use
exit-zero calls with incomplete evidence. Abort tests read preserved evidence
offline through the real collector and verdict helpers. One test uses a real
`timeout` kill (1 s). Each test deletes its
temp folders.

Contracts that these tests fix, besides the ones in `test_trial_verdict.py`:

  Trial folder   After each trial, `evals/runs/<run>/trials/<case>/<nn>/verdict.json`
                 holds `{"status", "reason", "error"}` (the output of
                 `trial_verdict.py trial-status`).
  results.db     `eval_results` has the columns `status` (pass, fail, or
                 inconclusive) and `reason` (NULL or a token for a pass and a
                 fail, the reason token for an inconclusive trial). For a failed
                 turn, `details` holds the error text. `eval_runs` has the column
                 `inconclusive_cases`. The `passed` column of an inconclusive
                 trial is not fixed. `passed_cases` counts only cases with the
                 status PASS.
  Case line      `[PASS|FAIL|INCONCLUSIVE] <case> <passes>/<conclusive> (...)`.
                 When a trial is inconclusive, the line holds
                 `<n> inconclusive: <reasons>` (design 2.4 example:
                 `4/4 (1 inconclusive: timeout)`).
  Summary        The text from the `Overall:` line on holds the number of
                 inconclusive cases as `<n> inconclusive`.
  Exit code      `main` exits 0 when every case passes, 1 when a case fails, 2 for
                 an infra halt (the eval container died), and 3 when no case
                 fails and a case is inconclusive. Order: 2, then 1, then 3.
  Cycle cases    A cycle assertion that reports `"status": "inconclusive"` (F11)
                 gives an inconclusive trial, with the reason of the report. A
                 report with `"status": "failed"` stays a fail.

The fake CLI plays a plan, one entry for each CLI call: `<cli>:<assertion>`.
  cli        ok (a finished turn), cut (exit 124 at once, no turn end), fail (an
             ERROR record, a turn end, exit 1), crash (a crash log, exit 134),
             incomplete (exit 0, no turn-end evidence), nolog (exit 0, no session log),
             slow (blocks on a signal, so `timeout` cuts it)
  assertion  pass (exit 0), fail (exit 1), or an exit code, such as 141
The assertion function reads the entry of the last CLI call. So a harness that
skips the assertion for an incomplete trial does not shift the plan.
"""

import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

EVALS = Path(__file__).resolve().parent
if str(EVALS) not in sys.path:
    sys.path.insert(0, str(EVALS))

import test_cycle_evals as cycle_tests  # noqa: E402  (needs the path set above)

FAKE_CLI = r"""#!/usr/bin/env bash
# Args: chat -p [--json] [--resume ID] PROMPT      or      --version
args=("$@")
if [[ "${args[0]}" == "--version" ]]; then echo "netclaw 0.0.0-fake"; exit 0; fi
prompt="${args[-1]}"
session=""
json=0
for ((i = 0; i < ${#args[@]}; i++)); do
    if [[ "${args[i]}" == "--resume" ]]; then session="${args[i+1]}"; fi
    if [[ "${args[i]}" == "--json" ]]; then json=1; fi
done
n=$(( $(cat "$FAKE_STATE/count" 2>/dev/null || echo 0) + 1 ))
echo "$n" > "$FAKE_STATE/count"
entry=$(printf '%s' "$FAKE_PLAN" | cut -d, -f"$n")
[[ -n "$entry" ]] || entry="ok:pass"
printf '%s' "$entry" > "$FAKE_STATE/last_entry"
mode="${entry%%:*}"
[[ -n "$session" ]] || session="signalr/fake-${FAKE_TAG}-$n"
log="$NETCLAW_HOME/logs/${session//\//-}.log"
stamp() { printf '[2026-10-15T10:00:%02d.0000000+00:00] ' "$1"; }
if [[ "$mode" != nolog ]]; then
    { stamp 0; echo "Headless session started: $session"
      stamp 1; echo "PROMPT: $prompt"
      stamp 2; echo "SESSION_JOINED turn_count=0 title=(none)"; } >> "$log"
fi
reply() {
    if [[ "$json" == 1 ]]; then
        if [[ -n "${FAKE_JSON_FILE:-}" ]]; then cat "$FAKE_JSON_FILE"
        else printf '{"sessionId":"%s","response":"reply"}\n' "$session"; fi
    else
        printf 'reply\n'
    fi
}
finish_turn() {
    local turn
    turn=$(( $(grep -c ' TURN_COMPLETED: ' "$log") + 1 ))
    { stamp 5; echo "ASSISTANT_FINAL: reply"
      stamp 6; echo "USAGE: in=1 out=1 total=2 cached=0 reasoning= context_window=1000 prompt_ms=1 predicted_tok_s="
      stamp 7; echo "TURN_COMPLETED: turn=$turn"
      stamp 8; echo "SESSION_ENDED"; } >> "$log"
}
case "$mode" in
    ok)     finish_turn; reply; exit 0 ;;
    nolog)  reply; exit 0 ;;
    cut)    exit 124 ;;
    incomplete) reply; exit 0 ;;
    slow)   exec python3 -c 'import signal; signal.pause()' ;;
    fail)   { stamp 4; echo "ERROR: ${FAKE_ERROR:-provider unavailable}"; } >> "$log"; finish_turn; exit 1 ;;
    crash)  printf 'No space left on device\n' > "$NETCLAW_HOME/logs/crash-20261015-131615.log"; exit 134 ;;
    *)      echo "unknown fake mode: $mode" >&2; exit 99 ;;
esac
"""

DRIVER = r"""
source "$HARNESS_SCRIPT"
sleep() { :; }
store_metrics() { :; }
check_prerequisites() { :; }
build_local_image() { :; }
build_coordination_observer() { :; }
start_eval_daemon() { :; }
seed_eval_memories() { :; }
if [[ "${DRIVER_REAL_DAEMON_CHECK:-0}" != 1 ]]; then check_daemon_alive() { :; }; fi
docker() {
    case "$1" in
        inspect) cat "$FAKE_STATE/docker_running" 2>/dev/null || echo true ;;
        stop) echo false > "$FAKE_STATE/docker_running"; printf '%s\n' "${@: -1}" ;;
        wait) echo 0 ;;
        ps|container) : ;;
        image) echo sha256:fixture-image ;;
        exec|logs) : ;;
        *) return 1 ;;
    esac
}
fake_assertion() {
    local entry verdict
    entry=$(cat "$FAKE_STATE/last_entry")
    verdict="${entry#*:}"
    case "$verdict" in
        pass) return 0 ;;
        fail) return 1 ;;
        *) return "$verdict" ;;
    esac
}
assert_demo() { fake_assertion; }
assert_other() { fake_assertion; }
RUN_ID=run-under-test
EVAL_HOME="$DRIVER_HOME"
TMPDIR_EVAL="$DRIVER_TMP"
EVAL_PORT=1
EVAL_CONTAINER_NAME=eval-fake
EVAL_CONTAINER_ID=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
EVAL_MODEL_ID=fake-model
NETCLAW_VER=fake
STARTED_AT=2026-10-15T10:00:00+00:00
RESULTS_DIR="$DRIVER_TMP/results"
RESULTS_DB="$RESULTS_DIR/results.db"
CURRENT_CATEGORY=demo-category
CATEGORY_SKIPPED=false
if [[ "$DRIVER_KIND" != scenario ]]; then init_db; fi
case "$DRIVER_KIND" in
    text) run_case demo "demo case" "$@" ;;
    json) run_case --json demo "demo case" "$@" ;;
    multi) run_multi_turn_case demo "demo case" "$@" ;;
    cycle)
        source "$REPO_ROOT/evals/cycle_evals.sh"
        CYCLE_CASE=correction
        CYCLE_FIXTURE_PORT=1
        curl() { if [[ -n "${CURL_FAIL:-}" ]]; then return 22; fi; cat "$CYCLE_SNAPSHOT_FILE"; }
        stdout_json_session_actor_log_path() { printf '%s\n' "$CYCLE_ACTOR_LOG"; }
        stdout_json_headless_log_path() { printf '%s\n' "$CYCLE_HEADLESS_LOG"; }
        assert_demo() { assert_cycle_case; }
        run_case --json demo "cycle demo" "cycle prompt" ;;
    scenario)
        # Like the last line of run-evals.sh: main is the last command, so the
        # exit code of the script is the exit code of main, under set -e.
        run_all() { eval "$SCENARIO"; }
        main
        exit 0 ;;
esac
finalize_db
echo DRIVER_DONE
"""


class StatesCase(unittest.TestCase):
    """Builds a temp checkout with the real harness scripts and a fake CLI."""

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        copy = self.root / "checkout" / "evals"
        copy.mkdir(parents=True)
        for source in EVALS.iterdir():
            if source.is_file() and source.suffix in (".sh", ".py"):
                shutil.copy2(source, copy / source.name)
        self.script = copy / "run-evals.sh"
        self.home = self.root / "home"
        (self.home / "logs").mkdir(parents=True)
        # The daemon creates netclaw.db at start. Without it, the memory
        # snapshot of each trial fails and every trial is inconclusive.
        (self.home / "data").mkdir()
        sqlite3.connect(self.home / "data" / "netclaw.db").close()
        (self.home / "logs" / "daemon-2026-10-15.log").write_text(
            "09:00:00.000 [INF] Netclaw.Boot: before the trial session=signalr/other\n")
        self.tmp_eval = self.root / "tmp-eval"
        self.tmp_eval.mkdir()
        self.state = self.root / "state"
        self.state.mkdir()
        self.cli = self.root / "fake-netclaw"
        self.cli.write_text(FAKE_CLI)
        self.cli.chmod(0o755)
        self.invocations = 0

    def drive(self, kind, prompts, plan, runs=1, timeout="5", expect_done=True, **env):
        self.invocations += 1
        for name in ("count", "last_entry", "docker_running"):
            (self.state / name).unlink(missing_ok=True)  # each drive starts its plan from the top
        environment = {
            "FAKE_TAG": str(self.invocations),
            "PATH": os.environ["PATH"],
            "HOME": str(self.root),
            "TMPDIR": str(self.root),
            "PYTHONDONTWRITEBYTECODE": "1",
            "HARNESS_SCRIPT": str(self.script),
            "NETCLAW_BIN": str(self.cli),
            "NETCLAW_EVAL_RUNS": str(runs),
            "NETCLAW_EVAL_TIMEOUT": str(timeout),
            "NETCLAW_EVAL_SEED": "7",
            "DRIVER_KIND": kind,
            "DRIVER_HOME": str(self.home),
            "DRIVER_TMP": str(self.tmp_eval),
            "FAKE_STATE": str(self.state),
            "FAKE_PLAN": plan,
        }
        environment.update({key: str(value) for key, value in env.items()})
        completed = subprocess.run(["bash", "-c", DRIVER, "bash", *prompts],
                                   env=environment, capture_output=True, text=True)
        if expect_done:
            self.assertIn("DRIVER_DONE", completed.stdout,
                          "the harness stopped before the driver finished. "
                          f"exit={completed.returncode} stdout tail={completed.stdout[-400:]!r} "
                          f"stderr tail={completed.stderr[-600:]!r}")
        return completed

    def collect_aborted_trial(self, number, expected_codes):
        """Read preserved evidence after abort. This step does not dispatch a prompt."""
        raw = self.trial_dir(number) / "raw"
        exit_files = sorted(raw.rglob("cli-exit.json"))
        self.assertEqual(expected_codes, [json.loads(p.read_text())["exit_code"] for p in exit_files])
        if (raw.parent / "verdict.json").exists():
            return
        # The abort gate precedes score calculation. Reuse the real evidence and database readers offline.
        known = sorted(self.tmp_eval.glob("known_logs_*.txt"))[-1]
        script = r'''source "$HARNESS_SCRIPT"
EVAL_HOME="$DRIVER_HOME"
TRIAL_RAW="$POSTMORTEM_RAW"
TRIAL_KNOWN_LOGS_FILE="$POSTMORTEM_KNOWN"
RESULTS_DB="$POSTMORTEM_DB"
RUN_ID=run-under-test
CURRENT_CATEGORY=demo-category
finish_trial
record_trial demo "$POSTMORTEM_NUMBER" p fail
'''
        completed = subprocess.run(
            ["bash", "-c", script], capture_output=True, text=True, timeout=20,
            env={"PATH": os.environ["PATH"], "HOME": str(self.root), "TMPDIR": str(self.root),
                 "PYTHONDONTWRITEBYTECODE": "1", "HARNESS_SCRIPT": str(self.script),
                 "DRIVER_HOME": str(self.home), "POSTMORTEM_RAW": str(raw),
                 "POSTMORTEM_KNOWN": str(known), "POSTMORTEM_DB": str(self.results_db),
                 "POSTMORTEM_NUMBER": str(number)})
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual(expected_codes, [json.loads(p.read_text())["exit_code"] for p in exit_files])

    @property
    def results_db(self):
        return self.tmp_eval / "results" / "results.db"

    def trial_dir(self, number):
        return (self.root / "checkout" / "evals" / "runs" / "run-under-test"
                / "trials" / "demo" / f"{number:02d}")

    def verdict(self, number=1):
        path = self.trial_dir(number) / "verdict.json"
        if not path.exists():
            listing = sorted(str(p.relative_to(path.parent)) for p in path.parent.glob("*")) \
                if path.parent.exists() else "(folder missing)"
            self.fail(f"{path} was not written. Folder holds: {listing}")
        return json.loads(path.read_text())

    def result_rows(self):
        """Rows of eval_results as (run_number, status, reason, details)."""
        database = sqlite3.connect(self.results_db)
        self.addCleanup(database.close)
        columns = [row[1] for row in database.execute("PRAGMA table_info(eval_results)")]
        for needed in ("status", "reason"):
            if needed not in columns:
                self.fail(f"eval_results has no {needed} column. Columns: {columns}")
        return database.execute(
            "SELECT run_number, status, reason, details FROM eval_results ORDER BY case_name, run_number").fetchall()

    def run_row(self):
        database = sqlite3.connect(self.results_db)
        self.addCleanup(database.close)
        columns = [row[1] for row in database.execute("PRAGMA table_info(eval_runs)")]
        if "inconclusive_cases" not in columns:
            self.fail(f"eval_runs has no inconclusive_cases column. Columns: {columns}")
        row = database.execute(
            "SELECT total_cases, passed_cases, inconclusive_cases FROM eval_runs").fetchone()
        return {"total": row[0], "passed": row[1], "inconclusive": row[2]}

    CASE_LINE = re.compile(r"^\s*\[(PASS|FAIL|INCONCLUSIVE)\]\s+(\S+)\s+(\d+)/(\d+)\b(.*)$", re.MULTILINE)

    def case_lines(self, completed):
        return [m for m in self.CASE_LINE.finditer(completed.stdout)]


class TrialVerdictFileTests(StatesCase):
    def test_each_trial_folder_holds_a_verdict_for_pass_fail_and_inconclusive(self):
        # Planted bug: the harness never calls trial_verdict, or calls it
        # before the assertion ran, so the verdict is missing or stale.
        self.drive("text", ["p"], "ok:pass,ok:fail,incomplete:pass", runs=3)

        self.assertEqual("pass", self.verdict(1)["status"])
        self.assertEqual("fail", self.verdict(2)["status"])
        self.assertEqual({"status": "inconclusive", "reason": "timeout", "error": None}, self.verdict(3))

    def test_cut_trial_aborts_and_keeps_timeout_evidence(self):
        completed = self.drive("text", ["p"], "cut:pass,ok:pass", runs=2, expect_done=False)

        self.assertEqual(2, completed.returncode, completed.stderr)
        self.assertEqual("1", (self.state / "count").read_text().strip())
        self.assertFalse(self.trial_dir(2).exists())
        self.collect_aborted_trial(1, [124])
        self.assertEqual({"status": "inconclusive", "reason": "timeout", "error": None}, self.verdict(1))

    def test_real_timeout_kill_keeps_its_original_status(self):
        # The fake CLI blocks on a signal. GNU timeout terminates that process.
        completed = self.drive("text", ["p"], "slow:pass", timeout=1, expect_done=False)

        self.assertEqual(2, completed.returncode, completed.stderr)
        self.collect_aborted_trial(1, [124])
        self.assertEqual({"status": "inconclusive", "reason": "timeout", "error": None}, self.verdict(1))

    def test_cli_crash_aborts_and_keeps_the_crash_evidence(self):
        completed = self.drive("text", ["p"], "crash:pass", expect_done=False)

        self.assertEqual(2, completed.returncode, completed.stderr)
        self.collect_aborted_trial(1, [134])
        verdict = self.verdict(1)
        self.assertEqual("inconclusive", verdict["status"])
        self.assertTrue(verdict["reason"].startswith("infra"), verdict)

    def test_trial_without_a_session_log_is_inconclusive_infra(self):
        # Planted bug: no session log, so the assertion decides.
        self.drive("text", ["p"], "nolog:pass")

        verdict = self.verdict(1)
        self.assertEqual("inconclusive", verdict["status"])
        self.assertIn("no_session", verdict["reason"])

    def test_assertion_pipe_failure_gives_an_inconclusive_trial(self):
        # Planted bug: any non-zero assertion exit counts as a fail. assert.json
        # (PR 1) holds the exit code, so the harness must pass it on.
        self.drive("text", ["p"], "ok:141")

        self.assertEqual({"status": "inconclusive", "reason": "assertion_exit_141", "error": None},
                         self.verdict(1))
        self.assertEqual(141, json.loads((self.trial_dir(1) / "raw" / "assert.json").read_text())["exit_code"])

    def test_failed_turn_is_inconclusive_and_the_error_text_is_stored_intact(self):
        # Planted bug: the stored text goes into SQL without escaping, so a
        # quote ends the statement; or the harness stores the failed turn as a
        # fail; or it drops the error text (decision 6).
        error = 'Provider returned 500: it\'s "down"); DROP TABLE eval_results; --\nsecond line'
        completed = self.drive("text", ["p"], "fail:pass", FAKE_ERROR=error, expect_done=False)

        self.assertEqual(2, completed.returncode, completed.stderr)
        self.collect_aborted_trial(1, [1])

        self.assertEqual({"status": "inconclusive", "reason": "turn_failed", "error": error}, self.verdict(1))
        ((number, status, reason, details),) = self.result_rows()
        self.assertEqual((1, "inconclusive", "turn_failed"), (number, status, reason))
        self.assertIn(error, details)

    def test_quotes_and_newlines_in_the_prompt_survive_the_database(self):
        # Planted bug: build SQL for the new columns with string formatting.
        prompt = "Don't say \"hi\"; DROP TABLE eval_results; --\nsecond line"
        self.drive("text", [prompt], "ok:pass")

        self.result_rows()  # fails with a clear message when the new columns are missing
        database = sqlite3.connect(self.results_db)
        self.addCleanup(database.close)
        ((stored, status),) = database.execute("SELECT prompt_used, status FROM eval_results").fetchall()
        self.assertEqual(prompt, stored)
        self.assertEqual("pass", status)

    def test_multi_turn_trial_aborts_at_the_first_cut_turn(self):
        completed = self.drive(
            "multi", ["turn one", "turn two"], "ok:pass,ok:pass,ok:pass,cut:pass,ok:pass",
            runs=3, expect_done=False)

        self.assertEqual(2, completed.returncode, completed.stderr)
        self.assertEqual("4", (self.state / "count").read_text().strip())
        self.assertFalse(self.trial_dir(3).exists())
        self.assertEqual("pass", self.verdict(1)["status"])
        self.collect_aborted_trial(2, [0, 124])
        self.assertEqual("timeout", self.verdict(2)["reason"])
        self.assertEqual("inconclusive", self.verdict(2)["status"])
        self.assertEqual(["pass", "inconclusive"], [row[1] for row in self.result_rows()])


class CaseLineTests(StatesCase):
    def check_case(self, plan, runs, tag, score, inconclusive, reasons=(), statuses=None):
        completed = self.drive("text", ["p"], plan, runs=runs)
        lines = self.case_lines(completed)
        self.assertEqual(1, len(lines), completed.stdout)
        line = lines[0]
        self.assertEqual(tag, line.group(1), line.group(0))
        self.assertEqual(score, (int(line.group(3)), int(line.group(4))), line.group(0))
        if inconclusive:
            found = re.search(r"\b(\d+) inconclusive\b", line.group(5))
            self.assertIsNotNone(found, f"no inconclusive count on: {line.group(0)}")
            self.assertEqual(inconclusive, int(found.group(1)), line.group(0))
            for reason in reasons:
                self.assertIn(reason, line.group(5), line.group(0))
        if statuses is not None:
            self.assertEqual(statuses, [row[1] for row in self.result_rows()])
        return completed

    def test_four_passes_and_one_incomplete_trial_is_a_pass_scored_4_of_4(self):
        # Planted bug: a pass needs every trial (the old rule scores 4/5 = 0.80
        # and passes only by luck of the threshold), the score divides by all
        # trials (shows 4/5), or the inconclusive count is missing from the
        # line. The design example: `4/4 (1 inconclusive: timeout)`.
        completed = self.check_case(
            "ok:pass,ok:pass,ok:pass,ok:pass,incomplete:pass", 5, "PASS", (4, 4), 1, ["timeout"],
            ["pass", "pass", "pass", "pass", "inconclusive"])

        self.assertEqual({"total": 1, "passed": 1, "inconclusive": 0}, self.run_row())
        self.assertRegex(completed.stdout, r"\b1 inconclusive: timeout\b")

    def test_three_passes_one_fail_and_one_incomplete_trial_is_inconclusive(self):
        # Planted bug: count the incomplete trial as a pass (PASS), or as a fail
        # (FAIL, the old rule).
        self.check_case("ok:pass,ok:pass,ok:pass,ok:fail,incomplete:pass", 5, "INCONCLUSIVE", (3, 4), 1, ["timeout"],
                        ["pass", "pass", "pass", "fail", "inconclusive"])

        self.assertEqual({"total": 1, "passed": 0, "inconclusive": 1}, self.run_row())

    def test_a_case_that_cannot_reach_the_threshold_is_a_fail_even_with_an_incomplete_trial(self):
        # 1P 2F 2I at 0.8: even if both incomplete trials passed, 3/5 is below it.
        # Planted bug: any inconclusive trial makes the case inconclusive.
        self.check_case("ok:pass,ok:fail,ok:fail,incomplete:pass,incomplete:pass", 5, "FAIL", (1, 3), 2, ["timeout"],
                        ["pass", "fail", "fail", "inconclusive", "inconclusive"])

        self.assertEqual({"total": 1, "passed": 0, "inconclusive": 0}, self.run_row())

    def test_one_conclusive_pass_among_four_incomplete_trials_shows_1_of_1_and_inconclusive(self):
        # Planted bug: a lone pass makes the case a PASS, or the score is 1/5.
        self.check_case("incomplete:pass,incomplete:pass,ok:pass,incomplete:fail,incomplete:pass", 5, "INCONCLUSIVE", (1, 1), 4, ["timeout"])

        self.assertEqual({"total": 1, "passed": 0, "inconclusive": 1}, self.run_row())

    def test_a_case_with_no_conclusive_trial_does_not_divide_by_zero(self):
        # D4: 15 of 33 cases had only incomplete trials. Planted bug: awk divides by
        # the number of conclusive trials, which is zero.
        completed = self.check_case("incomplete:pass,incomplete:pass", 2, "INCONCLUSIVE", (0, 0), 2, ["timeout"])

        self.assertNotRegex(completed.stdout + completed.stderr, r"(?i)division by zero|\binf\b|\bnan\b")

    def test_reasons_of_mixed_inconclusive_trials_are_listed(self):
        # Planted bug: only the first reason is shown, or the reasons are lost.
        completed = self.drive("text", ["p"], "ok:pass,incomplete:pass,ok:141,ok:pass,ok:pass", runs=5)

        line = self.case_lines(completed)[0]
        self.assertEqual("INCONCLUSIVE", line.group(1), line.group(0))
        self.assertEqual((3, 3), (int(line.group(3)), int(line.group(4))), line.group(0))
        self.assertRegex(line.group(5), r"\b2 inconclusive\b")
        self.assertIn("timeout", line.group(5))
        self.assertIn("assertion_exit_141", line.group(5))

    def test_each_case_is_scored_from_its_own_trials(self):
        # Planted bug: the trial tally is not reset between cases, so the second
        # case inherits the passes of the first and shows 2/2 instead of 0/0.
        completed = self.drive(
            "scenario", ["unused"], "ok:pass,ok:pass,incomplete:pass,incomplete:pass", runs=2,
            SCENARIO='run_case demo "first" "p1"; run_case other "second" "p2"', expect_done=False)

        first, second = self.case_lines(completed)
        self.assertEqual(("PASS", "demo", 2, 2), (first.group(1), first.group(2), int(first.group(3)), int(first.group(4))))
        self.assertEqual(("INCONCLUSIVE", "other", 0, 0),
                         (second.group(1), second.group(2), int(second.group(3)), int(second.group(4))))
        self.assertRegex(second.group(5), r"\b2 inconclusive\b")

    def test_multi_turn_case_line_uses_the_same_rule(self):
        # Planted bug: run_multi_turn_case keeps its own copy of the old score.
        completed = self.drive(
            "multi", ["turn one", "turn two"],
            "ok:pass,ok:pass,ok:pass,ok:pass,ok:pass,ok:pass,ok:pass,ok:pass,incomplete:pass,incomplete:pass", runs=5)

        line = self.case_lines(completed)[0]
        self.assertEqual("PASS", line.group(1), line.group(0))
        self.assertEqual((4, 4), (int(line.group(3)), int(line.group(4))), line.group(0))
        self.assertRegex(line.group(5), r"\b1 inconclusive\b")
        self.assertEqual(["pass", "pass", "pass", "pass", "inconclusive"], [row[1] for row in self.result_rows()])


class ExitCodeTests(StatesCase):
    def run_main(self, scenario, plan, **env):
        return self.drive("scenario", ["unused"], plan, expect_done=False, SCENARIO=scenario, **env)

    TWO_CASES = 'run_case demo "first" "p1"; run_case other "second" "p2"'

    def test_exit_3_when_no_case_fails_and_a_case_is_inconclusive(self):
        # Planted bug: the exit code stays 0 (an inconclusive case is not a
        # fail, so nothing is wrong), or becomes 1 (CI cannot tell it apart).
        for scenario, plan in (('run_case demo "only" "p"', "incomplete:pass"),
                               (self.TWO_CASES, "ok:pass,incomplete:pass"),
                               (self.TWO_CASES, "incomplete:fail,incomplete:pass")):
            with self.subTest(plan=plan):
                completed = self.run_main(scenario, plan)
                self.assertEqual(3, completed.returncode, completed.stdout[-500:] + completed.stderr[-500:])

    def test_exit_1_when_a_case_fails_even_if_another_case_is_inconclusive(self):
        # Planted bug: inconclusive outranks fail, so a real regression hides
        # behind an incomplete trial.
        for plan in ("ok:fail,incomplete:pass", "incomplete:pass,ok:fail"):
            with self.subTest(plan=plan):
                completed = self.run_main(self.TWO_CASES, plan)
                self.assertEqual(1, completed.returncode, completed.stdout[-500:] + completed.stderr[-500:])

    def test_exit_0_and_1_do_not_change(self):
        # Guard. Passes on the current code. Planted bug: exit 3 for any run,
        # or exit 1 for a clean run.
        for plan, expected in (("ok:pass,ok:pass", 0), ("ok:pass,ok:fail", 1), ("ok:fail,ok:fail", 1)):
            with self.subTest(plan=plan):
                completed = self.run_main(self.TWO_CASES, plan)
                self.assertEqual(expected, completed.returncode, completed.stdout[-500:] + completed.stderr[-500:])

    def test_exit_2_when_the_eval_container_dies_whatever_the_cases_before_it_said(self):
        # Guard for "as today". The container dies after the first case.
        # Planted bug: the infra halt turns into exit 3 or 1 because a case
        # before it was inconclusive or failed.
        scenario = ('run_case demo "first" "p1"; echo false > "$FAKE_STATE/docker_running"; '
                    'run_case other "second" "p2"')
        for plan in ("incomplete:pass,ok:pass", "ok:fail,ok:pass", "ok:pass,ok:pass"):
            with self.subTest(plan=plan):
                completed = self.run_main(scenario, plan, DRIVER_REAL_DAEMON_CHECK=1)
                self.assertEqual(2, completed.returncode, completed.stdout[-500:] + completed.stderr[-500:])

    def test_summary_shows_the_number_of_inconclusive_cases(self):
        # Planted bug: the summary counts an inconclusive case as passed, or
        # says nothing about it.
        completed = self.run_main(self.TWO_CASES, "ok:pass,incomplete:pass")

        summary = completed.stdout[completed.stdout.index("Overall:"):]
        self.assertRegex(summary, r"\b1/2 cases passed\b")
        self.assertRegex(summary, r"\b1 inconclusive\b")

    def test_overall_line_of_a_clean_run_has_no_inconclusive_cases(self):
        # Planted bug: the count is wrong when nothing is inconclusive.
        completed = self.run_main(self.TWO_CASES, "ok:pass,ok:pass")

        summary = completed.stdout[completed.stdout.index("Overall:"):]
        self.assertRegex(summary, r"\b2/2 cases passed\b")
        self.assertNotRegex(summary, r"\b[1-9]\d* inconclusive\b")


class CategoryLabelTests(StatesCase):
    """The label at the end of a category (README: GREEN, INCONCLUSIVE, YELLOW, RED)."""

    def category_line(self, scenario, plan):
        completed = self.drive("scenario", ["unused"], plan, expect_done=False,
                               SCENARIO=f'print_category "Mixed"; {scenario}; end_category')
        found = re.search(r"^\s*Category: (\d+)/(\d+) passed \((\w+)\)$", completed.stdout, re.MULTILINE)
        self.assertIsNotNone(found, completed.stdout[-600:] + completed.stderr[-400:])
        return (int(found.group(1)), int(found.group(2)), found.group(3))

    TWO_CASES = 'run_case demo "first" "p1"; run_case other "second" "p2"'

    def test_a_fail_next_to_an_inconclusive_case_is_not_labeled_inconclusive(self):
        # P1. Planted bug: the label is INCONCLUSIVE when any case is
        # inconclusive, not only when no case failed. The category line then
        # hides a real FAIL.
        for plan in ("ok:fail,incomplete:pass", "incomplete:pass,ok:fail"):
            with self.subTest(plan=plan):
                self.assertEqual((0, 2, "RED"), self.category_line(self.TWO_CASES, plan))

    def test_a_category_without_a_fail_and_with_an_inconclusive_case_is_labeled_inconclusive(self):
        # P1 guard. Planted bug: the inconclusive case gets a colour that means "bad".
        self.assertEqual((1, 2, "INCONCLUSIVE"), self.category_line(self.TWO_CASES, "ok:pass,incomplete:pass"))

    def test_a_category_of_passing_cases_is_green(self):
        # P1 guard. Planted bug: the label looks at inconclusive trials before it looks at the pass count.
        self.assertEqual((2, 2, "GREEN"), self.category_line(self.TWO_CASES, "ok:pass,ok:pass"))

    def test_a_category_with_a_fail_is_yellow_or_red_by_the_pass_share(self):
        # P1 guard. 1 of 2 cases passed (0.50) is RED. The share for YELLOW is 0.80 or more.
        self.assertEqual((1, 2, "RED"), self.category_line(self.TWO_CASES, "ok:pass,ok:fail"))


class MultiTurnFailDetailsTests(StatesCase):
    def test_details_of_a_failed_multi_turn_assertion_are_stored(self):
        # P3. Planted bug: run_multi_turn_case passes the word `fail` to
        # record_trial and drops EVAL_ASSERTION_DETAILS. The coding-context
        # case then loses `spawn_agent_not_called` and five other details.
        scenario = ('assert_demo() { EVAL_ASSERTION_DETAILS="spawn_agent_not_called"; return 1; }; '
                    'run_multi_turn_case demo "demo case" "turn one" "turn two"')
        self.drive("scenario", ["unused"], "ok:pass,ok:pass", expect_done=False, SCENARIO=scenario)

        ((number, status, reason, details),) = self.result_rows()
        self.assertEqual((1, "fail", None), (number, status, reason))
        self.assertEqual("spawn_agent_not_called", details)

    def test_a_failed_multi_turn_assertion_without_details_stores_a_fail_word(self):
        # P3 guard. Planted bug: the stored details are empty, so the row says nothing about the fail.
        scenario = ('assert_demo() { return 1; }; '
                    'run_multi_turn_case demo "demo case" "turn one" "turn two"')
        self.drive("scenario", ["unused"], "ok:pass,ok:pass", expect_done=False, SCENARIO=scenario)

        ((_, status, _, details),) = self.result_rows()
        self.assertEqual("fail", status)
        self.assertTrue(details, "a fail row needs some details text")


class ResultsDatabaseFailureTests(StatesCase):
    """N3. A failed results_db.py call is a crashed evidence step: exit 2.
    Exit 1 means that a case failed, and Python's exit code would say so."""

    def run_main(self, scenario):
        return self.drive("scenario", ["unused"], "ok:pass", expect_done=False, SCENARIO=scenario)

    def test_failed_store_ends_the_run_with_exit_2(self):
        # Planted bug: `store` runs under `set -e` without a guard, so the
        # IntegrityError of a duplicate row ends the run with exit 1. The row
        # for (run, demo, 1) exists before the first trial stores its own.
        scenario = ('python3 "$RESULTS_DB_PY" store --db "$RESULTS_DB" --run-id "$RUN_ID" --category c '
                    '--case demo --run-number 1 --prompt x --status pass; '
                    'run_case demo "only" "p"')

        completed = self.run_main(scenario)

        self.assertEqual(2, completed.returncode, completed.stdout[-400:] + completed.stderr[-600:])
        self.assertNotIn("Overall:", completed.stdout)

    def test_failed_init_ends_the_run_with_exit_2(self):
        # Planted bug: `init` runs under `set -e` without a guard, so the
        # error of a results.db that is not a database ends the run with exit 1.
        results = self.tmp_eval / "results"
        results.mkdir()
        (results / "results.db").write_text("this is not a database\n" * 50)

        completed = self.run_main('run_case demo "only" "p"')

        self.assertEqual(2, completed.returncode, completed.stdout[-400:] + completed.stderr[-600:])
        self.assertNotIn("Overall:", completed.stdout)


class CycleStatusTests(StatesCase):
    """F11: `run_case` stored a cycle report with `"status": "inconclusive"` as a fail."""

    def cycle(self, documents=None, curl_fail=False, snapshot=None):
        documents = documents or cycle_tests.CycleCommandTests().documents("correction")
        files = {}
        for key in ("snapshot", "output", "actor", "headless"):
            path = self.root / f"cycle-{key}.txt"
            path.write_text(snapshot if key == "snapshot" and snapshot is not None else documents[key])
            files[key] = path
        env = {"CYCLE_SNAPSHOT_FILE": files["snapshot"], "CYCLE_ACTOR_LOG": files["actor"],
               "CYCLE_HEADLESS_LOG": files["headless"], "FAKE_JSON_FILE": files["output"]}
        if curl_fail:
            env["CURL_FAIL"] = "1"
        completed = self.drive("cycle", ["unused"], "ok:fail", **env)
        return completed

    def test_report_written_by_the_assertion_helper_gives_an_inconclusive_trial(self):
        # The fixture does not answer (curl fails), so the helper writes an
        # inconclusive report and returns 1. Planted bug: the trial is a fail,
        # which is what run_case stored before PR 2.
        self.cycle(curl_fail=True)

        verdict = self.verdict(1)
        self.assertEqual("inconclusive", verdict["status"], verdict)
        self.assertIn("snapshot_unavailable", verdict["reason"])
        self.assertEqual("inconclusive", self.result_rows()[0][1])

    def test_inconclusive_report_of_the_check_command_gives_an_inconclusive_trial(self):
        # The snapshot names no known case, so `cycle_evals.py check` itself
        # reports "inconclusive" (reason invalid_snapshot) and exits 1.
        # Planted bug: only reports that cycle_inconclusive writes are mapped.
        self.cycle(snapshot='{"case": "nope"}')

        verdict = self.verdict(1)
        self.assertEqual("inconclusive", verdict["status"], verdict)
        self.assertIn("invalid_snapshot", verdict["reason"])

    def test_failed_report_stays_a_fail(self):
        # Planted bug: every cycle assertion that returns 1 becomes
        # inconclusive. The failed report: the headless log has no primary
        # receipts, so `cycle_evals.py check` reports "failed".
        documents = cycle_tests.CycleCommandTests().documents("correction")
        broken = dict(documents, headless=cycle_tests.PrimaryReceiptTests.record("USAGE: context_window=65536"))

        self.cycle(documents=broken)

        self.assertEqual({"status": "fail", "reason": None, "error": None}, self.verdict(1))

    def test_passed_report_stays_a_pass(self):
        # Planted bug: the cycle path never gives a pass (control).
        self.cycle()

        self.assertEqual({"status": "pass", "reason": None, "error": None}, self.verdict(1))


if __name__ == "__main__":
    unittest.main()
