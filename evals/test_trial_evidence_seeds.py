"""Tests for two parts of PR 1 that test_trial_files.py does not cover.

1. The actor log statuses. `trial_evidence.py actor-log` reads the session
   catalog (`netclaw.db`, table `sessions`) and copies one log. Each way that
   this can fail must leave a status in `actor-log.json`, print a WARN line on
   stderr, and let the run go on. A catalog path outside the netclaw home must
   never be copied.

2. The seed. `trial_seed` comes from the run seed, the case, and the trial
   number. A run filtered with NETCLAW_EVAL_CASE must replay the variants of
   the full run. A run seed that is not a non-negative integer stops the run
   with exit 2.

The tests run the real `run-evals.sh` with a fake CLI (see test_trial_files.py).
They use no model, no network, and no daemon. Each test names the planted bug
that it catches.
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

import test_trial_files as trial_files

EVALS = Path(__file__).resolve().parent
TRIAL_EVIDENCE = EVALS / "trial_evidence.py"

CONTAINER_HOME = "/home/netclaw/.netclaw/"

# Copy of the DDL in src/Netclaw.Daemon/Gateway/SessionCatalogService.cs
# (SessionsCreateTableDdl). The harness reads only persistence_id and log_path.
SESSIONS_DDL = """
CREATE TABLE IF NOT EXISTS sessions (
    persistence_id    TEXT NOT NULL PRIMARY KEY,
    channel           TEXT NOT NULL,
    created_at        INTEGER NOT NULL,
    last_activity     INTEGER NOT NULL,
    status            TEXT NOT NULL DEFAULT 'active',
    turn_count        INTEGER NOT NULL DEFAULT 0,
    title             TEXT,
    description       TEXT,
    last_input_tokens INTEGER,
    log_path          TEXT,
    metadata          TEXT
)
"""


def write_catalog(home, rows):
    """Create home/data/netclaw.db. `rows` holds (session id, log path) pairs."""
    path = home / "data" / "netclaw.db"
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        path.unlink()
    database = sqlite3.connect(path)
    try:
        database.execute(SESSIONS_DDL)
        database.executemany(
            "INSERT INTO sessions (persistence_id, channel, created_at, last_activity, log_path) "
            "VALUES (?, 'signalr', 1, 1, ?)",
            [(f"session-{session_id}", log_path) for session_id, log_path in rows])
        database.commit()
    finally:
        database.close()
    return path


class ActorLogTests(trial_files.HarnessCase):
    SESSION = "signalr/this-trial"

    def make_actor_log(self, directory="dirA", text="actor log of THIS session\n"):
        """Create a session folder under home/data. Return the log and its catalog path."""
        log = self.home / "data" / "sessions" / directory / "logs" / "session.log"
        log.parent.mkdir(parents=True)
        log.write_text(text)
        return log, f"{CONTAINER_HOME}sessions/{directory}/logs/session.log"

    def run_trial(self):
        """Run one trial in a clean run folder. Return (process, actor-log.json)."""
        shutil.rmtree(self.root / "checkout" / "evals" / "runs", ignore_errors=True)
        self.results.write_text("")
        # The CLI log of an earlier run in this test would not be new.
        (self.home / "logs" / "signalr-this-trial.log").unlink(missing_ok=True)
        completed = self.run_harness("text", ["p"], FAKE_SESSION=self.SESSION)
        return completed, self.read_json(self.raw("actor-log.json"))

    def assert_finding(self, completed, record, status_prefix):
        """The status is recorded, stderr shows it, no log is kept, and the run went on."""
        self.assertTrue(record["status"].startswith(status_prefix),
                        f"status was {record['status']!r}, wanted {status_prefix!r}")
        self.assertEqual(self.SESSION, record["session_id"])
        self.assertFalse(self.raw("session.log").exists())
        self.assertIn(f"WARN: no actor log for {self.SESSION} ({record['status']})", completed.stderr)
        self.assertIn("demo|1|", self.results.read_text(), "the run must store a result")
        self.assertTrue(self.raw("daemon-session.log").exists(),
                        "the harness must go on to the daemon evidence")

    def test_no_catalog_db_is_recorded_and_the_run_goes_on(self):
        # Planted bug: open the missing file anyway. sqlite3 creates an empty
        # database, the query fails, and the status is catalog_unreadable.
        completed, record = self.run_trial()

        self.assert_finding(completed, record, "no_catalog_db")
        self.assertFalse((self.home / "data" / "netclaw.db").exists(),
                         "the harness must not create the catalog")

    def test_session_without_a_catalog_row_copies_no_other_session_log(self):
        # Planted bug: fall back to another row, or to the newest session.log,
        # when the session has no row. The harness then keeps the log of a
        # different session as the evidence of this trial.
        other_log, other_path = self.make_actor_log("dirB", "actor log of ANOTHER session\n")
        write_catalog(self.home, [("signalr/another", other_path)])

        completed, record = self.run_trial()

        self.assert_finding(completed, record, "no_catalog_row")
        self.assertIsNone(record["catalog_log_path"])
        self.assertTrue(other_log.exists())

    def test_unreadable_catalog_is_recorded_with_the_error(self):
        # Planted bug: drop the `except sqlite3.Error`. The helper crashes, and
        # the harness stops without a record. A second bug: report such a
        # catalog as no_catalog_row, which hides the broken file.
        cases = {
            "file is not a database": lambda path: path.write_bytes(b"this is not sqlite" * 50),
            "database has no sessions table": lambda path: sqlite3.connect(path).execute(
                "CREATE TABLE other (x INTEGER)").connection.commit(),
        }
        for name, break_catalog in cases.items():
            with self.subTest(name):
                catalog = self.home / "data" / "netclaw.db"
                catalog.parent.mkdir(parents=True, exist_ok=True)
                if catalog.exists():
                    catalog.unlink()
                break_catalog(catalog)

                completed, record = self.run_trial()

                self.assert_finding(completed, record, "catalog_unreadable: ")
                self.assertGreater(len(record["status"]), len("catalog_unreadable: "),
                                   "the status must carry the error text")

    def test_catalog_row_without_a_path_is_recorded(self):
        # Planted bug: test only `row is None`. A NULL path then crashes the
        # prefix check, and an empty path is judged as outside the home.
        for name, log_path in (("NULL path", None), ("empty path", "")):
            with self.subTest(name):
                write_catalog(self.home, [(self.SESSION, log_path)])

                completed, record = self.run_trial()

                self.assert_finding(completed, record, "catalog_row_without_path")

    def test_catalog_path_outside_home_is_never_copied(self):
        # Planted bug: use a catalog path that exists on the host as it is, for
        # example `log = Path(container_path)` when that file exists. The file
        # outside the home is then copied into the trial folder.
        secret = self.root / "outside" / "secret.log"
        secret.parent.mkdir()
        secret.write_text("SECRET data outside the netclaw home\n")
        for name, log_path in (
            ("absolute host path", str(secret)),
            ("sibling of the container home", "/home/netclaw/.netclaw-other/secret.log"),
            ("relative path", "outside/secret.log"),
        ):
            with self.subTest(name):
                write_catalog(self.home, [(self.SESSION, log_path)])

                completed, record = self.run_trial()

                self.assert_finding(completed, record, "catalog_path_outside_home")
                self.assertEqual(log_path, record["catalog_log_path"])
                for copied in self.trial_dir().rglob("*"):
                    if copied.is_file():
                        self.assertNotIn("SECRET", copied.read_text(errors="replace"),
                                         f"{copied} holds data from outside the home")
                self.assertTrue(secret.exists())

    def test_catalog_path_inside_home_with_no_file_is_recorded(self):
        # Planted bug: drop the `is_file` check. copyfile raises, or the status
        # says ok while session.log is missing.
        write_catalog(self.home, [(self.SESSION, CONTAINER_HOME + "sessions/gone/logs/session.log")])

        completed, record = self.run_trial()

        self.assert_finding(completed, record, "catalog_log_missing")

    def test_found_actor_log_is_copied_with_sub_agent_logs_only(self):
        # Planted bug: copy the whole session folder, or print the WARN line for
        # a good lookup. A session folder can hold private workspace files.
        log, container_path = self.make_actor_log()
        envelope = log.parent.parent
        (envelope / "workspace").mkdir()
        (envelope / "workspace" / "notes.txt").write_text("PRIVATE workspace data\n")
        sub_logs = envelope / "subagents" / "run-1" / "logs"
        sub_logs.mkdir(parents=True)
        (sub_logs / "agent.log").write_text("sub-agent log\n")
        (sub_logs / "scratch.txt").write_text("PRIVATE sub-agent scratch\n")
        write_catalog(self.home, [(self.SESSION, container_path)])

        completed, record = self.run_trial()

        self.assertEqual("ok", record["status"])
        self.assertEqual(["subagents/run-1/agent.log"], record["subagent_logs"])
        self.assertEqual("actor log of THIS session\n", self.read_text(self.raw("session.log")))
        self.assertEqual("sub-agent log\n", self.read_text(self.raw("subagents/run-1/agent.log")))
        for copied in self.trial_dir().rglob("*"):
            if copied.is_file():
                self.assertNotIn("PRIVATE", copied.read_text(errors="replace"),
                                 f"{copied} holds a file that is not a log")
        self.assertNotIn("WARN: no actor log", completed.stderr)


def derive_seed(seed, case, trial, **env):
    environment = {**os.environ, **{key: str(value) for key, value in env.items()}}
    completed = subprocess.run(
        [sys.executable, str(TRIAL_EVIDENCE), "derive-seed",
         "--seed", str(seed), "--case", case, "--trial", str(trial)],
        capture_output=True, text=True, env=environment)
    assert completed.returncode == 0, completed.stderr
    return int(completed.stdout)


class DeriveSeedTests(unittest.TestCase):
    def test_trial_seed_matches_the_documented_value(self):
        # Planted bug: use another hash, little-endian bytes, 8 bytes in place
        # of 4, or a different material layout. Old run seeds then replay other
        # variants. These values were computed once from SHA-256 over
        # "<seed>\0<case>\0<trial>", first 4 bytes, big-endian.
        for args, expected in (
            ((7, "demo", 1), 357770588),
            ((7, "demo", 2), 65173577),
            ((0, "demo", 1), 4172380411),
            ((42, "skill_activation_basic", 3), 35001267),
        ):
            with self.subTest(args=args):
                self.assertEqual(expected, derive_seed(*args))

    def test_every_input_changes_the_trial_seed(self):
        # Planted bug: leave out the case or the trial. Every case, or every
        # trial, then gets the same variant mix.
        base = derive_seed(7, "demo", 1)
        self.assertNotEqual(base, derive_seed(8, "demo", 1))
        self.assertNotEqual(base, derive_seed(7, "other", 1))
        self.assertNotEqual(base, derive_seed(7, "demo", 2))

    def test_material_fields_do_not_run_together(self):
        # Planted bug: join the fields with no separator. Seed 1 with case "2a",
        # and seed 12 with case "a", then hash the same text "12a1".
        self.assertNotEqual(derive_seed(1, "2a", 1), derive_seed(12, "a", 1))
        self.assertNotEqual(derive_seed(1, "a1", 1), derive_seed(1, "a", 11))

    def test_trial_seed_fits_in_32_bits(self):
        # Planted bug: keep the whole digest. RANDOM takes a 32-bit seed.
        for trial in range(1, 40):
            self.assertLess(derive_seed(123456789, "demo", trial), 2 ** 32)

    def test_same_run_seed_gives_the_same_trial_seeds_in_every_process(self):
        # Planted bug: derive the value with Python hash(). It changes with
        # PYTHONHASHSEED, so two processes disagree, and a replay or a
        # filtered run picks other variants.
        runs = [[derive_seed(7, case, trial, PYTHONHASHSEED=salt) for case in ("demo", "other")
                 for trial in (1, 2, 3)] for salt in (1, 2, 3)]
        self.assertEqual([runs[0]] * 3, runs)


class RunSeedValidationTests(unittest.TestCase):
    def source_harness(self, seed):
        """Source the real harness with NETCLAW_EVAL_SEED=seed. Return the process."""
        with tempfile.TemporaryDirectory() as temp:
            script = Path(temp) / "checkout" / "evals"
            script.mkdir(parents=True)
            for source in EVALS.iterdir():
                if source.is_file() and source.suffix in (".sh", ".py"):
                    shutil.copy2(source, script / source.name)
            environment = {
                "PATH": os.environ["PATH"],
                "HOME": temp,
                "NETCLAW_EVAL_SEED": seed,
                "HARNESS_SCRIPT": str(script / "run-evals.sh"),
            }
            return subprocess.run(["bash", "-c", 'source "$HARNESS_SCRIPT"; echo SOURCED'],
                                  env=environment, capture_output=True, text=True)

    def test_run_seed_that_is_not_a_non_negative_integer_stops_the_run_with_exit_2(self):
        # Planted bug: drop the check. `--argjson seed` or `RANDOM=` then fails
        # later in a trial, or a typo like "7x" gives an unexplained mix.
        for seed in ("abc", "-5", "1.5", "7x", "0x10", "1e3", " 7", "7 8"):
            with self.subTest(seed=seed):
                completed = self.source_harness(seed)

                self.assertEqual(2, completed.returncode, completed.stderr[-300:])
                self.assertNotIn("SOURCED", completed.stdout)
                self.assertIn("NETCLAW_EVAL_SEED", completed.stderr)

    def test_valid_run_seed_is_accepted_including_zero(self):
        # Planted bug: reject 0, or reject a seed with leading zeros, or reject
        # every seed. The check must stop only a seed that is not an integer.
        for seed in ("0", "7", "007", "4294967295", "99999999999"):
            with self.subTest(seed=seed):
                completed = self.source_harness(seed)

                self.assertEqual(0, completed.returncode, completed.stderr[-300:])
                self.assertIn("SOURCED", completed.stdout)


# A full run: another case runs first, then the case under test.
FULL_RUN_DRIVER = trial_files.DRIVER.replace(
    'case "$DRIVER_KIND" in',
    'assert_other() { return 0; }\nrun_case other "other case" "$@"\ncase "$DRIVER_KIND" in', 1)
assert FULL_RUN_DRIVER != trial_files.DRIVER


class FilteredRunTests(trial_files.HarnessCase):
    VARIANTS = [f"variant-{i:02d}" for i in range(20)]
    RUNS = 4

    def run_case_variants(self, case_filter=None, seed=7):
        """Run in a fresh process. Return {case: [(variant_index, trial_seed)]} by trial."""
        shutil.rmtree(self.root / "checkout" / "evals" / "runs", ignore_errors=True)
        env = {"NETCLAW_EVAL_CASE": case_filter} if case_filter else {}
        with mock.patch.object(trial_files, "DRIVER", FULL_RUN_DRIVER):
            self.run_harness("text", self.VARIANTS, runs=self.RUNS, seed=seed,
                             FAKE_SESSION="", **env)
        result = {}
        for case in ("other", "demo"):
            records = []
            for number in range(1, self.RUNS + 1):
                path = (self.root / "checkout" / "evals" / "runs" / "run-under-test" / "trials"
                        / case / f"{number:02d}" / "raw" / "variant.json")
                if path.exists():
                    record = self.read_json(path)
                    records.append((record["variant_index"], record["trial_seed"]))
            if records:
                result[case] = records
        return result

    def test_filtered_run_picks_the_same_variants_as_the_full_run(self):
        # Planted bug: seed RANDOM once for the run, or count trials across
        # cases. The case that runs second then depends on the case that ran
        # first, and NETCLAW_EVAL_CASE=demo replays another mix.
        full = self.run_case_variants()
        filtered = self.run_case_variants(case_filter="demo")

        self.assertEqual({"other", "demo"}, set(full), "the full run must run both cases")
        self.assertEqual({"demo"}, set(filtered), "the filter must skip the other case")
        self.assertEqual(self.RUNS, len(full["demo"]))
        self.assertEqual(full["demo"], filtered["demo"])

    def test_trial_seed_in_variant_file_is_the_derived_seed(self):
        # Planted bug: record the run seed as trial_seed, or the trial number.
        # A reader could not replay a trial from variant.json.
        recorded = self.run_case_variants()["demo"]

        expected = [derive_seed(7, "demo", number) for number in range(1, self.RUNS + 1)]
        self.assertEqual(expected, [trial_seed for _, trial_seed in recorded])

    def test_cases_in_one_run_get_different_variant_mixes(self):
        # Planted bug: pass the trial number as the case, or leave the case out
        # of the call. Every case then repeats the same variant sequence.
        recorded = self.run_case_variants()

        self.assertNotEqual([index for index, _ in recorded["other"]],
                            [index for index, _ in recorded["demo"]])


if __name__ == "__main__":
    unittest.main()
