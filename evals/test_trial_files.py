"""Specs for PR 1 of the eval harness rework: per-trial evidence folders and seeded prompts.

Two kinds of test live here.

1. `DiscoverSessionTests` call `evals/trial_evidence.py discover-session` as a
   process. The interface is fixed by these tests:

       trial_evidence.py discover-session --logs-dir DIR --known FILE

   `FILE` lists the base names of the files in `DIR` before the CLI call, one
   name for each line. The command prints one JSON object on stdout:
   `{"status": "ok", "session_id": "...", "log": "<base name>"}` and exits 0.
   Other status values are `no_session` and `ambiguous_session`. They exit
   with a non-zero code. `ambiguous_session` also lists `candidates` (the
   base names of the new session logs).

2. `TrialFolderTests` and `VariantSeedTests` run the real `run-evals.sh` in a
   temp checkout, with a fake `netclaw` CLI. They call `run_case` and
   `run_multi_turn_case`, and they read the trial folder:

       evals/runs/<RUN_ID>/trials/<case>/<nn>/raw/...

   `nn` has two digits and counts from 01. The tests do not call a model,
   a network, Docker, or the daemon. The tests replace `sleep` with a no-op
   function in the driver shell so that the fixed `sleep 2` in `run_prompt`
   does not slow the run. The fake CLI never waits, except for the prompts
   that hold the word SLOW. `timeout` kills the fake CLI for those prompts.

Each test names the planted bug that it catches.
"""

import json
import os
from datetime import datetime
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

EVALS = Path(__file__).resolve().parent

SESSION_HEADER = "[2026-10-15T10:00:00.0000000+00:00] Headless session started: {}\n"


def write_session_log(logs, session_id, mtime=None, extra=""):
    path = logs / (session_id.replace("/", "-") + ".log")
    path.write_text(SESSION_HEADER.format(session_id) + extra)
    if mtime is not None:
        os.utime(path, (mtime, mtime))
    return path


class DiscoverSessionTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.logs = self.root / "logs"
        self.logs.mkdir()

    def known(self):
        return sorted(p.name for p in self.logs.iterdir())

    def discover(self, known):
        known_file = self.root / "known.txt"
        known_file.write_text("".join(name + "\n" for name in known))
        completed = subprocess.run(
            [sys.executable, str(EVALS / "trial_evidence.py"), "discover-session",
             "--logs-dir", str(self.logs), "--known", str(known_file)],
            capture_output=True, text=True)
        try:
            result = json.loads(completed.stdout)
        except ValueError:
            self.fail("discover-session printed no JSON object. "
                      f"exit={completed.returncode} stdout={completed.stdout!r} "
                      f"stderr={completed.stderr.strip()!r}")
        return completed.returncode, result

    def test_discover_session_picks_the_log_created_during_the_trial(self):
        # Planted bug: pick the log with the newest mtime. An older multi-turn
        # log is appended during the trial, so its mtime is the newest.
        write_session_log(self.logs, "signalr/old-multi-turn", mtime=1_000)
        (self.logs / "daemon-2026-10-15.log").write_text("daemon line\n")
        known = self.known()
        write_session_log(self.logs, "signalr/this-trial", mtime=2_000)
        with open(self.logs / "signalr-old-multi-turn.log", "a") as old:
            old.write("[2026-10-15T10:05:00.0000000+00:00] PROMPT: turn two\n")
        os.utime(self.logs / "signalr-old-multi-turn.log", (9_000, 9_000))

        code, result = self.discover(known)

        self.assertEqual(0, code)
        self.assertEqual("ok", result["status"])
        self.assertEqual("signalr/this-trial", result["session_id"])
        self.assertEqual("signalr-this-trial.log", result["log"])

    def test_discover_session_reports_none_when_no_log_is_new(self):
        # Planted bug: return the previous trial's session.
        write_session_log(self.logs, "signalr/previous-trial", mtime=5_000)
        known = self.known()

        code, result = self.discover(known)

        self.assertNotEqual(0, code)
        self.assertEqual("no_session", result["status"])
        self.assertFalse(result.get("session_id"))

    def test_discover_session_refuses_two_new_logs(self):
        # Planted bug: return the first new log.
        known = self.known()
        write_session_log(self.logs, "signalr/aaa-first")
        write_session_log(self.logs, "signalr/bbb-second")

        code, result = self.discover(known)

        self.assertNotEqual(0, code)
        self.assertEqual("ambiguous_session", result["status"])
        self.assertFalse(result.get("session_id"))
        self.assertEqual({"signalr-aaa-first.log", "signalr-bbb-second.log"},
                         set(result["candidates"]))

    def test_discover_session_ignores_new_files_that_are_not_cli_session_logs(self):
        # Planted bug: count every new *.log file as a session. A crash log, a
        # rotated daemon log, and headless-errors.log appear in the same folder.
        known = self.known()
        (self.logs / "crash-20261015-131615.log").write_text("No space left on device\n")
        (self.logs / "daemon-2026-10-16.log").write_text("daemon line\n")
        (self.logs / "headless-errors.log").write_text("[2026-10-15T10:00:01+00:00] FAILED: x\n")

        with self.subTest("a real session log is also new"):
            write_session_log(self.logs, "signalr/real")
            code, result = self.discover(known)
            self.assertEqual(0, code)
            self.assertEqual("signalr/real", result["session_id"])

        with self.subTest("only the other files are new"):
            (self.logs / "signalr-real.log").unlink()
            code, result = self.discover(known)
            self.assertNotEqual(0, code)
            self.assertEqual("no_session", result["status"])


# The driver runs inside one bash process. It sources the real harness, stubs
# the parts that need Docker or sqlite results, and calls the real run_case.
DRIVER = r"""
source "$HARNESS_SCRIPT"
sleep() { :; }
check_daemon_alive() { :; }
store_metrics() { :; }
store_result() { printf '%s|%s|%s|%s\n' "$1" "$2" "$4" "$5" >> "$DRIVER_RESULTS"; }
assert_demo() {
    if [[ -n "${ASSERT_STDERR:-}" ]]; then printf '%s\n' "$ASSERT_STDERR" >&2; fi
    return "${ASSERT_RC:-0}"
}
RUN_ID=run-under-test
EVAL_HOME="$DRIVER_HOME"
TMPDIR_EVAL="$DRIVER_TMP"
EVAL_PORT=1
CURRENT_CATEGORY=demo-category
CATEGORY_SKIPPED=false
EVAL_REMINDER_TARGET=target-77
case "$DRIVER_KIND" in
    text) run_case demo "demo case" "$@" ;;
    json) run_case --json demo "demo case" "$@" ;;
    multi) run_multi_turn_case demo "demo case" "$@" ;;
esac
echo DRIVER_DONE
"""

FAKE_CLI = r"""#!/usr/bin/env bash
# Args: chat -p [--json] [--resume ID] PROMPT
args=("$@")
prompt="${args[-1]}"
session="${FAKE_SESSION:-}"
for ((i = 0; i < ${#args[@]}; i++)); do
    if [[ "${args[i]}" == "--resume" ]]; then session="${args[i+1]}"; fi
done
printf '%s\n' "$prompt" >> "$FAKE_STATE/prompts.txt"
if [[ -n "$session" ]]; then
    log="$NETCLAW_HOME/logs/${session//\//-}.log"
    if [[ ! -f "$log" ]]; then
        printf '[2026-10-15T10:00:00.0000000+00:00] Headless session started: %s\n' "$session" >> "$log"
    fi
    printf '[2026-10-15T10:00:01.0000000+00:00] PROMPT: %s\n' "$prompt" >> "$log"
fi
if [[ -n "${FAKE_APPEND_LOG:-}" ]]; then
    printf '[2026-10-15T10:00:02.0000000+00:00] PROMPT: appended\n' >> "$NETCLAW_HOME/logs/$FAKE_APPEND_LOG"
fi
if [[ -n "${FAKE_DAEMON_LINES:-}" ]]; then
    printf '%s\n' "$FAKE_DAEMON_LINES" >> "$NETCLAW_HOME/logs/daemon-2026-10-15.log"
fi
printf '%s' "${FAKE_STDOUT-}"
printf '%s' "${FAKE_STDERR-}" >&2
if [[ "${FAKE_PRINT_PID:-}" == "1" ]]; then printf 'pid=%s\n' "$$"; fi
if [[ "$prompt" == *SLOW* ]]; then exec sleep 30; fi
if [[ "$prompt" == *FAIL* ]]; then exit 1; fi
exit 0
"""


class HarnessCase(unittest.TestCase):
    """Builds a temp checkout with the real harness scripts and a fake CLI."""

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        # REPO_ROOT derives from the script location, so a copy keeps every
        # trial folder inside the temp dir and out of the real checkout.
        copy = self.root / "checkout" / "evals"
        copy.mkdir(parents=True)
        for source in EVALS.iterdir():
            if source.is_file() and source.suffix in (".sh", ".py"):
                shutil.copy2(source, copy / source.name)
        self.script = copy / "run-evals.sh"
        self.home = self.root / "home"
        (self.home / "logs").mkdir(parents=True)
        (self.home / "logs" / "daemon-2026-10-15.log").write_text(
            "09:00:00.000 [INF] Netclaw.Boot: before the trial session=signalr/other\n")
        self.tmp_eval = self.root / "tmp-eval"
        self.tmp_eval.mkdir()
        self.state = self.root / "state"
        self.state.mkdir()
        self.cli = self.root / "fake-netclaw"
        self.cli.write_text(FAKE_CLI)
        self.cli.chmod(0o755)
        self.results = self.root / "results.txt"

    def run_harness(self, kind, prompts, runs=1, timeout="5", seed=None, **env):
        environment = {
            "PATH": os.environ["PATH"],
            "HOME": str(self.root),
            "HARNESS_SCRIPT": str(self.script),
            "NETCLAW_BIN": str(self.cli),
            "NETCLAW_EVAL_RUNS": str(runs),
            "NETCLAW_EVAL_TIMEOUT": str(timeout),
            "DRIVER_KIND": kind,
            "DRIVER_HOME": str(self.home),
            "DRIVER_TMP": str(self.tmp_eval),
            "DRIVER_RESULTS": str(self.results),
            "FAKE_STATE": str(self.state),
        }
        if seed is not None:
            environment["NETCLAW_EVAL_SEED"] = str(seed)
        environment.update({key: str(value) for key, value in env.items()})
        completed = subprocess.run(["bash", "-c", DRIVER, "bash", *prompts],
                                   env=environment, capture_output=True, text=True)
        self.assertIn(
            "DRIVER_DONE", completed.stdout,
            "the harness stopped before run_case returned. "
            f"exit={completed.returncode} stderr tail={completed.stderr[-400:]!r}")
        return completed

    def trial_dir(self, number=1, case="demo"):
        return (self.root / "checkout" / "evals" / "runs" / "run-under-test"
                / "trials" / case / f"{number:02d}")

    def raw(self, name, number=1, turn=None):
        base = self.trial_dir(number) / "raw"
        if turn is not None:
            base = base / f"turn-{turn:02d}"
        return base / name

    def read_text(self, path):
        if not path.exists():
            listing = sorted(str(p) for p in path.parent.glob("**/*")) \
                if path.parent.exists() else "(folder missing)"
            self.fail(f"{path} was not written. Folder holds: {listing}")
        return path.read_text()

    def read_json(self, path):
        return json.loads(self.read_text(path))


class TrialFolderTests(HarnessCase):
    def test_run_prompt_records_exit_124_on_timeout(self):
        # Planted bug: keep `|| true`, so the exit file says 0 (or the run
        # aborts under `set -e`) when the CLI is cut off.
        self.run_harness("text", ["SLOW prompt"], timeout=1, FAKE_SESSION="signalr/cut")

        record = self.read_json(self.raw("cli-exit.json"))
        self.assertEqual(124, record["exit_code"])
        self.assertEqual(1, record["deadline_s"])
        started = datetime.fromisoformat(record["started_at"])
        ended = datetime.fromisoformat(record["ended_at"])
        self.assertIsNotNone(started.tzinfo, "started_at needs a UTC offset")
        self.assertIsNotNone(ended.tzinfo, "ended_at needs a UTC offset")
        self.assertGreaterEqual(ended, started)
        self.assertIn("demo|1|", self.results.read_text(),
                      "the run must go on and store a result after a cut-off trial")

    def test_run_prompt_records_the_real_exit_code_of_a_failed_or_clean_turn(self):
        # Planted bug: write a constant exit code, or record only exit 124.
        for prompt, expected in (("FAIL prompt", 1), ("clean prompt", 0)):
            with self.subTest(prompt=prompt):
                shutil.rmtree(self.root / "checkout" / "evals" / "runs", ignore_errors=True)
                self.run_harness("text", [prompt], FAKE_SESSION="signalr/exit")
                self.assertEqual(expected, self.read_json(self.raw("cli-exit.json"))["exit_code"])

    def test_raw_folder_holds_the_cli_output_of_a_text_trial(self):
        # Planted bug: capture the CLI output in the shared temp dir only, or
        # name a text capture as JSON.
        self.run_harness("text", ["say hello"], FAKE_SESSION="signalr/out",
                         FAKE_STDOUT="reply text\n", FAKE_STDERR="warn text\n")

        self.assertEqual("reply text\n", self.read_text(self.raw("cli-stdout.txt")))
        self.assertEqual("warn text\n", self.read_text(self.raw("cli-stderr.txt")))
        self.assertFalse(self.raw("cli-stdout.json").exists())

    def test_raw_folder_names_the_cli_output_of_a_json_trial_as_json(self):
        # Planted bug: always write cli-stdout.txt.
        self.run_harness("json", ["say hello"], FAKE_SESSION="signalr/json",
                         FAKE_STDOUT='{"sessionId":"signalr/json","response":"hi"}\n')

        self.assertEqual('{"sessionId":"signalr/json","response":"hi"}\n',
                         self.read_text(self.raw("cli-stdout.json")))
        self.assertFalse(self.raw("cli-stdout.txt").exists())

    def test_prompt_file_holds_the_rendered_prompt_the_cli_received(self):
        # Planted bug: save the template with its {{...}} placeholder, not the
        # text that the CLI got. The trial then cannot replay.
        self.run_harness("text", ["Remind {{EVAL_REMINDER_TARGET}} now"], FAKE_SESSION="signalr/p")

        sent = (self.state / "prompts.txt").read_text().rstrip("\n")
        self.assertEqual("Remind target-77 now", sent)
        self.assertEqual(sent, self.read_text(self.raw("prompt.txt")).rstrip("\n"))

    def test_each_trial_gets_its_own_numbered_folder(self):
        # Planted bug: every trial writes to the same folder and overwrites
        # the one before.
        self.run_harness("text", ["same prompt"], runs=3, FAKE_SESSION="", FAKE_PRINT_PID="1")

        outputs = [self.read_text(self.raw("cli-stdout.txt", number=n)) for n in (1, 2, 3)]
        self.assertEqual(3, len(set(outputs)), f"trial folders share output: {outputs}")

    def test_assertion_stderr_goes_to_the_trial_folder(self):
        # Planted bug: keep `2>/dev/null` on the assertion call. The cause of a
        # false fail (D4 rows 55 and 147) then stays unknown.
        for rc, expected in ((1, "0"), (0, "1")):
            with self.subTest(assertion_rc=rc):
                shutil.rmtree(self.root / "checkout" / "evals" / "runs", ignore_errors=True)
                self.results.write_text("")
                self.run_harness("text", ["p"], FAKE_SESSION="signalr/a",
                                 ASSERT_RC=rc, ASSERT_STDERR="expected 'x' in reply, found none")
                self.assertIn("expected 'x' in reply, found none",
                              self.read_text(self.raw("assert-stderr.txt")))
                self.assertEqual(expected, self.results.read_text().split("|")[2],
                                 "the verdict must stay what the assertion returned")

    def test_cli_session_log_is_the_log_created_during_the_trial(self):
        # Planted bug: take the snapshot of known logs after the CLI call
        # (nothing is new) or pick the newest mtime. An older multi-turn log
        # gains a line during the trial and has the newest mtime.
        write_session_log(self.home / "logs", "signalr/old-multi", mtime=1_000)
        self.run_harness("text", ["p"], FAKE_SESSION="signalr/this-trial",
                         FAKE_APPEND_LOG="signalr-old-multi.log")

        copied = self.read_text(self.raw("cli-session.log"))
        self.assertIn("Headless session started: signalr/this-trial", copied)
        self.assertNotIn("old-multi", copied)
        session = self.read_json(self.raw("session.json"))
        self.assertEqual("ok", session["status"])
        self.assertEqual("signalr/this-trial", session["session_id"])

    def test_trial_without_a_new_cli_log_records_no_session_and_goes_on(self):
        # Planted bug: abort the run, or write nothing, when the CLI left no log.
        self.run_harness("text", ["p"], FAKE_SESSION="")

        self.assertEqual("no_session", self.read_json(self.raw("session.json"))["status"])
        self.assertFalse(self.raw("cli-session.log").exists())
        self.assertIn("demo|1|", self.results.read_text())

    def test_actor_log_is_the_one_in_the_session_catalog(self):
        # Planted bug: copy the newest session.log, or all of them. The
        # catalog (netclaw.db, table sessions) maps one session to one log.
        sessions = self.home / "data" / "sessions"
        for directory, text in (("dirA", "actor log of THIS session\n"), ("dirB", "actor log of ANOTHER session\n")):
            (sessions / directory / "logs").mkdir(parents=True)
            (sessions / directory / "logs" / "session.log").write_text(text)
        os.utime(sessions / "dirB" / "logs" / "session.log", (9_000_000_000, 9_000_000_000))
        database = sqlite3.connect(self.home / "data" / "netclaw.db")
        database.execute("CREATE TABLE sessions (persistence_id TEXT, log_path TEXT)")
        database.executemany("INSERT INTO sessions VALUES (?, ?)", [
            ("session-signalr/this-trial", "/home/netclaw/.netclaw/sessions/dirA/logs/session.log"),
            ("session-signalr/another", "/home/netclaw/.netclaw/sessions/dirB/logs/session.log")])
        database.commit()
        database.close()

        self.run_harness("text", ["p"], FAKE_SESSION="signalr/this-trial")

        self.assertEqual("actor log of THIS session\n", self.read_text(self.raw("session.log")))

    def test_daemon_session_log_keeps_only_exact_session_token_lines(self):
        # Planted bug: match the session id as a substring (signalr/this-trial
        # also matches signalr/this-trial-2), or keep lines with no session.
        lines = [
            "10:00:01.000 [INF] Netclaw.Memory: memory_retrieval_final session=signalr/this-trial mode=lexical",
            "10:00:02.000 [INF] Netclaw.Memory: memory_retrieval_plan session=signalr/this-trial",
            "10:00:03.000 [INF] Netclaw.Tools: Tool executed sessionId=signalr/this-trial tool=skill_load",
            "10:00:04.000 [INF] Netclaw.Memory: memory_retrieval_final session=signalr/this-trial-2 mode=lexical",
            "10:00:05.000 [INF] Netclaw.Memory: memory_retrieval_final session=signalr/other mode=lexical",
            "10:00:06.000 [INF] Netclaw.Tools: turn_skill_loaded skill=netclaw-operations method=skill_load",
        ]
        self.run_harness("text", ["p"], FAKE_SESSION="signalr/this-trial",
                         FAKE_DAEMON_LINES="\n".join(lines))

        kept = self.read_text(self.raw("daemon-session.log")).splitlines()
        self.assertEqual(lines[:3], kept)

    def test_daemon_window_log_holds_only_lines_written_during_the_trial(self):
        # Planted bug: copy the whole daemon log, or take the line baseline
        # after the CLI call (the window is then empty).
        lines = ["10:00:01.000 [INF] A: first session=signalr/other",
                 "10:00:02.000 [INF] B: second no token"]
        self.run_harness("text", ["p"], FAKE_SESSION="signalr/this-trial",
                         FAKE_DAEMON_LINES="\n".join(lines))

        window = self.read_text(self.raw("daemon-window.log")).splitlines()
        self.assertEqual(lines, window)

    def test_run_prompt_resume_records_the_exit_code_of_every_turn(self):
        # Planted bug: keep `|| true` in run_prompt_resume, or write one exit
        # file for the whole multi-turn trial. Multi-turn trials keep one
        # sub-folder for each turn: raw/turn-01/, raw/turn-02/.
        self.run_harness("multi", ["turn one", "SLOW turn two"], timeout=1)

        self.assertEqual(0, self.read_json(self.raw("cli-exit.json", turn=1))["exit_code"])
        self.assertEqual(124, self.read_json(self.raw("cli-exit.json", turn=2))["exit_code"])
        self.assertEqual("turn one", self.read_text(self.raw("prompt.txt", turn=1)).rstrip("\n"))
        self.assertEqual("SLOW turn two", self.read_text(self.raw("prompt.txt", turn=2)).rstrip("\n"))


class VariantSeedTests(HarnessCase):
    VARIANTS = [f"variant-{i:02d}" for i in range(20)]

    def indexes(self, seed=None, runs=4):
        """Run one case in a fresh process. Return variant indexes and seeds by trial."""
        shutil.rmtree(self.root / "checkout" / "evals" / "runs", ignore_errors=True)
        self.run_harness("text", self.VARIANTS, runs=runs, seed=seed, FAKE_SESSION="")
        records = [self.read_json(self.raw("variant.json", number=n)) for n in range(1, runs + 1)]
        for number, record in enumerate(records, start=1):
            prompt = self.read_text(self.raw("prompt.txt", number=number)).rstrip("\n")
            self.assertEqual(self.VARIANTS[record["variant_index"]], prompt,
                             f"trial {number}: variant_index does not name the prompt that ran")
        return [r["variant_index"] for r in records], [r["seed"] for r in records]

    def test_variant_choice_repeats_with_the_same_seed(self):
        # Planted bug: seed RANDOM after the first pick_variant, or pick in a
        # `$(...)` subshell. Bash reseeds RANDOM in each subshell, so the
        # seed has no effect there. Several processes make a lucky match of the
        # first pick unlikely: (1/20)^3.
        runs = [self.indexes(seed=7)[0] for _ in range(4)]
        self.assertEqual([runs[0]] * 4, runs)

        other_seed, _ = self.indexes(seed=8)
        self.assertNotEqual(runs[0], other_seed, "a different seed must give a different mix")

    def test_unseeded_run_records_a_seed_that_replays_the_variants(self):
        # Planted bug: draw a seed and not record it, or record a seed that was
        # not the one used. Then two builds cannot get like-for-like prompts.
        picked, seeds = self.indexes(seed=None)
        self.assertEqual(1, len(set(seeds)), "one run must use one seed")
        self.assertIsInstance(seeds[0], int)

        replayed, _ = self.indexes(seed=seeds[0])
        self.assertEqual(picked, replayed)

    def test_seeded_run_records_the_seed_it_was_given(self):
        # Planted bug: record a fresh random seed and not NETCLAW_EVAL_SEED.
        _, seeds = self.indexes(seed=42, runs=2)
        self.assertEqual([42, 42], seeds)


if __name__ == "__main__":
    unittest.main()
