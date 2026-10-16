"""Tests for evals/results_db.py: the results.db schema, its migration, and its writes.

The OLD_SCHEMA text below is the schema that run-evals.sh created before result
states (init_db on dev at c8fce03e0). The tests build a results.db with that
schema and old rows, then run the new init. They use only the standard library.

Each test names the planted bug that it catches.
"""

import contextlib
import io
from pathlib import Path
import shutil
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import results_db  # noqa: E402

OLD_SCHEMA = """
CREATE TABLE eval_runs (
    run_id        TEXT PRIMARY KEY,
    started_at    TEXT NOT NULL,
    netclaw_ver   TEXT NOT NULL,
    model_id      TEXT,
    runs_per_case INTEGER NOT NULL,
    threshold     REAL NOT NULL,
    total_cases   INTEGER NOT NULL,
    passed_cases  INTEGER NOT NULL,
    overall_score REAL NOT NULL
);
CREATE TABLE eval_results (
    run_id      TEXT NOT NULL REFERENCES eval_runs(run_id),
    category    TEXT NOT NULL,
    case_name   TEXT NOT NULL,
    run_number  INTEGER NOT NULL,
    prompt_used TEXT NOT NULL,
    passed      INTEGER NOT NULL,
    details     TEXT,
    PRIMARY KEY (run_id, case_name, run_number)
);
CREATE TABLE eval_metrics (
    run_id            TEXT NOT NULL REFERENCES eval_runs(run_id),
    category          TEXT NOT NULL,
    case_name         TEXT NOT NULL,
    run_number        INTEGER NOT NULL,
    turn_number       INTEGER NOT NULL DEFAULT 1,
    input_tokens      INTEGER,
    output_tokens     INTEGER,
    cached_tokens     INTEGER,
    prompt_ms         REAL,
    predicted_tok_s   REAL,
    PRIMARY KEY (run_id, case_name, run_number, turn_number)
);
"""

# (run_number, passed, details) for the old rows of the case "old_case".
OLD_RESULT_ROWS = [(1, 1, "ok one"), (2, 0, "it's a fail"), (3, 1, None), (4, 0, "")]


class TempDirTestCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="results-db-test-"))
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.db_path = self.tmp / "results.db"

    def query(self, sql, params=()):
        database = sqlite3.connect(self.db_path)
        try:
            return database.execute(sql, params).fetchall()
        finally:
            database.close()

    def build_old_db(self):
        database = sqlite3.connect(self.db_path)
        try:
            database.executescript(OLD_SCHEMA)
            database.execute(
                "INSERT INTO eval_runs VALUES ('old-run', '2026-01-01T00:00:00Z', '0.1.0', 'm', 4, 0.8, 1, 0, 0.5)")
            database.executemany(
                "INSERT INTO eval_results VALUES ('old-run', 'cat', 'old_case', ?, 'prompt', ?, ?)",
                OLD_RESULT_ROWS)
            database.execute(
                "INSERT INTO eval_metrics (run_id, category, case_name, run_number, input_tokens) "
                "VALUES ('old-run', 'cat', 'old_case', 1, 123)")
            database.commit()
        finally:
            database.close()

    def snapshot(self):
        """Schema text and every row of every table. Equal snapshots mean no change."""
        database = sqlite3.connect(self.db_path)
        try:
            schema = database.execute(
                "SELECT name, sql FROM sqlite_master ORDER BY name").fetchall()
            tables = {}
            for table in ("eval_runs", "eval_results", "eval_metrics"):
                tables[table] = database.execute(f"SELECT * FROM {table} ORDER BY 1, 2, 3, 4").fetchall()
            return schema, tables
        finally:
            database.close()


class MigrationTests(TempDirTestCase):
    def test_migration_keeps_every_old_row_and_leaves_status_null(self):
        # Bugs caught: the migration drops or truncates eval_results; it rewrites passed;
        # it invents a status for an old row.
        self.build_old_db()

        results_db.init_db(self.db_path)

        rows = self.query(
            "SELECT run_number, passed, details, status, reason FROM eval_results "
            "WHERE run_id = 'old-run' ORDER BY run_number")
        self.assertEqual(
            [(n, passed, details, None, None) for n, passed, details in OLD_RESULT_ROWS], rows)

    def test_migration_keeps_the_other_old_tables(self):
        # Bug caught: the migration drops eval_runs or eval_metrics, or loses their rows.
        self.build_old_db()

        results_db.init_db(self.db_path)

        self.assertEqual(
            [("old-run", 4, 0.5, 0)],
            self.query("SELECT run_id, runs_per_case, overall_score, inconclusive_cases FROM eval_runs"))
        self.assertEqual(
            [("old-run", "old_case", 123)],
            self.query("SELECT run_id, case_name, input_tokens FROM eval_metrics"))

    def test_migration_adds_the_new_columns(self):
        # Bug caught: a new column is missing, or only a fresh database gets it.
        self.build_old_db()

        results_db.init_db(self.db_path)

        database = sqlite3.connect(self.db_path)
        try:
            results_columns = results_db.column_names(database, "eval_results")
            runs_columns = results_db.column_names(database, "eval_runs")
        finally:
            database.close()
        self.assertIn("status", results_columns)
        self.assertIn("reason", results_columns)
        self.assertIn("inconclusive_cases", runs_columns)

    def test_old_runs_get_zero_inconclusive_cases_on_both_paths(self):
        # Bug caught: a wrong default for inconclusive_cases. The migration path (ALTER) and
        # the fresh path (CREATE) each declare it. An old run, or a writer that does not
        # know the column, must read 0.
        self.build_old_db()
        results_db.init_db(self.db_path)
        fresh_path = self.tmp / "fresh.db"
        results_db.init_db(fresh_path)
        database = sqlite3.connect(fresh_path)
        try:
            database.execute(
                "INSERT INTO eval_runs (run_id, started_at, netclaw_ver, runs_per_case, threshold, "
                "total_cases, passed_cases, overall_score) VALUES ('r', 't', 'v', 1, 0.8, 1, 1, 1.0)")
            database.commit()
            fresh = database.execute("SELECT inconclusive_cases FROM eval_runs").fetchall()
        finally:
            database.close()

        self.assertEqual([(0,)], self.query("SELECT inconclusive_cases FROM eval_runs"))
        self.assertEqual([(0,)], fresh)

    def test_migration_leaves_no_legacy_table_behind(self):
        # Bug caught: the migration forgets to drop the renamed copy of the old table.
        self.build_old_db()

        results_db.init_db(self.db_path)

        tables = {name for (name,) in self.query("SELECT name FROM sqlite_master WHERE type = 'table'")}
        self.assertEqual({"eval_runs", "eval_results", "eval_metrics"}, tables)

    def test_migrated_table_accepts_an_inconclusive_row(self):
        # Bug caught: the migration keeps passed NOT NULL, so an inconclusive trial cannot be stored.
        self.build_old_db()
        results_db.init_db(self.db_path)

        results_db.store_result(self.db_path, "old-run", "cat", "new_case", 1, "p",
                                "inconclusive", "timeout", "")

        self.assertEqual(
            [(None, "inconclusive", "timeout")],
            self.query("SELECT passed, status, reason FROM eval_results WHERE case_name = 'new_case'"))

    def test_second_init_changes_nothing(self):
        # Bugs caught: no idempotency guard. A second rebuild copies only the legacy columns,
        # so it wipes status and reason from the new rows. A second ALTER fails on a duplicate column.
        self.build_old_db()
        results_db.init_db(self.db_path)
        for number, status, reason in ((1, "pass", ""), (2, "fail", ""), (3, "inconclusive", "timeout")):
            results_db.store_result(self.db_path, "new-run", "cat", "new_case", number, "p", status, reason, "d")
        before = self.snapshot()

        results_db.init_db(self.db_path)

        self.assertEqual(before, self.snapshot())

    def test_init_on_a_fresh_database_is_idempotent(self):
        # Bug caught: a fresh database differs from a migrated one, or a re-init changes it.
        results_db.init_db(self.db_path)
        results_db.store_result(self.db_path, "r", "cat", "c", 1, "p", "inconclusive", "timeout", "")
        before = self.snapshot()

        results_db.init_db(self.db_path)

        self.assertEqual(before, self.snapshot())

    def test_fresh_and_migrated_databases_have_the_same_results_columns(self):
        # Bug caught: the migration builds a table that differs from the one a fresh run builds.
        self.build_old_db()
        results_db.init_db(self.db_path)
        fresh_path = self.tmp / "fresh.db"
        results_db.init_db(fresh_path)

        def shape(path):
            database = sqlite3.connect(path)
            try:
                return database.execute("PRAGMA table_info(eval_results)").fetchall()
            finally:
                database.close()

        self.assertEqual(shape(fresh_path), shape(self.db_path))

    def test_average_of_passed_treats_inconclusive_rows_as_absent(self):
        # Bug caught: an inconclusive row is stored with passed=0, so an old AVG(passed) query
        # counts it as a fail. Old rows: 2 pass, 2 fail. New rows: 1 pass, 2 inconclusive.
        self.build_old_db()
        results_db.init_db(self.db_path)
        results_db.store_result(self.db_path, "new-run", "cat", "c", 1, "p", "pass", "", "")
        results_db.store_result(self.db_path, "new-run", "cat", "c", 2, "p", "inconclusive", "timeout", "")
        results_db.store_result(self.db_path, "new-run", "cat", "c", 3, "p", "inconclusive", "turn_failed", "")

        (average,) = self.query("SELECT AVG(passed) FROM eval_results")[0]
        (counted,) = self.query("SELECT COUNT(passed) FROM eval_results")[0]
        (inconclusive_passed,) = self.query(
            "SELECT COUNT(passed) FROM eval_results WHERE status = 'inconclusive'")[0]

        self.assertEqual(5, counted)
        self.assertEqual(0, inconclusive_passed)
        self.assertAlmostEqual(3 / 5, average)


class CheckConstraintTests(TempDirTestCase):
    def setUp(self):
        super().setUp()
        results_db.init_db(self.db_path)

    def insert(self, status, passed, case="c"):
        database = sqlite3.connect(self.db_path)
        try:
            with database:
                database.execute(
                    "INSERT INTO eval_results (run_id, category, case_name, run_number, prompt_used, passed, status) "
                    "VALUES ('r', 'cat', ?, 1, 'p', ?, ?)", (case, passed, status))
        finally:
            database.close()

    def test_check_rejects_inconsistent_rows(self):
        # Bug caught: the CHECK constraint is missing or too loose.
        inconsistent = [
            ("pass", 0),
            ("fail", 1),
            ("inconclusive", 1), ("inconclusive", 0),
            ("bogus", 1),
        ]
        # Not listed: ("pass", None) and ("fail", None). The CHECK expression gives NULL for
        # them, and SQLite accepts a NULL result. store_result cannot write them.
        for status, passed in inconsistent:
            with self.subTest(status=status, passed=passed):
                with self.assertRaises(sqlite3.IntegrityError):
                    self.insert(status, passed, case=f"{status}-{passed}")
        self.assertEqual([(0,)], self.query("SELECT COUNT(*) FROM eval_results"))

    def test_check_accepts_consistent_rows_and_old_style_rows(self):
        # Bug caught: the CHECK constraint is too strict and blocks a valid row,
        # or blocks an old-style row that has no status.
        self.insert("pass", 1, case="a")
        self.insert("fail", 0, case="b")
        self.insert("inconclusive", None, case="c")
        self.insert(None, 1, case="d")
        self.insert(None, 0, case="e")
        self.assertEqual([(5,)], self.query("SELECT COUNT(*) FROM eval_results"))

    def test_check_survives_the_migration(self):
        # Bug caught: the migrated table is built without the CHECK constraint.
        other = self.tmp / "migrated.db"
        self.db_path = other
        self.build_old_db()
        results_db.init_db(other)

        with self.assertRaises(sqlite3.IntegrityError):
            self.insert("pass", 0)

    def test_store_result_rejects_an_unknown_status_before_it_writes(self):
        # Bug caught: store_result accepts a status that maps to no passed value.
        with self.assertRaises(ValueError):
            results_db.store_result(self.db_path, "r", "cat", "c", 1, "p", "maybe", "", "")
        self.assertEqual([(0,)], self.query("SELECT COUNT(*) FROM eval_results"))


class StoreResultTests(TempDirTestCase):
    def setUp(self):
        super().setUp()
        results_db.init_db(self.db_path)

    def test_status_maps_to_passed_and_reason(self):
        # Bugs caught: passed=0 stored for inconclusive; the reason of a pass or fail kept;
        # an empty reason stored as an empty string instead of NULL.
        results_db.store_result(self.db_path, "r", "cat", "c", 1, "p", "pass", "", "")
        results_db.store_result(self.db_path, "r", "cat", "c", 2, "p", "fail", "", "")
        results_db.store_result(self.db_path, "r", "cat", "c", 3, "p", "inconclusive", "timeout", "")

        self.assertEqual(
            [(1, 1, "pass", None), (2, 0, "fail", None), (3, None, "inconclusive", "timeout")],
            self.query("SELECT run_number, passed, status, reason FROM eval_results ORDER BY run_number"))

    def test_quotes_and_newlines_are_stored_as_given(self):
        # Bug caught: string formatting or manual escaping in the INSERT. A quote then
        # breaks the statement or changes the stored text.
        case = "it's \"quoted\"; DROP TABLE eval_results;--"
        prompt = "line one\nline 'two'\n\"three\""
        details = "error: can't parse '%s' {0}"
        reason = "infra:it's broken"

        results_db.store_result(self.db_path, "run'1", "cat'egory", case, 1, prompt,
                                "inconclusive", reason, details)

        self.assertEqual(
            [("run'1", "cat'egory", case, prompt, details, reason)],
            self.query("SELECT run_id, category, case_name, prompt_used, details, reason FROM eval_results"))

    def test_store_command_stores_a_quoted_case_name(self):
        # Bug caught: the command line path loses or alters a quote that the function path keeps.
        case = "o'brien \"case\""
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = results_db.main([
                "store", "--db", str(self.db_path), "--run-id", "r", "--category", "cat",
                "--case", case, "--run-number", "2", "--prompt=-starts with a dash",
                "--status", "fail", "--details", "it's bad"])

        self.assertEqual(0, code)
        self.assertEqual(
            [(case, 2, "-starts with a dash", 0, "fail", "it's bad")],
            self.query("SELECT case_name, run_number, prompt_used, passed, status, details FROM eval_results"))

    def test_init_command_migrates_an_old_database(self):
        # Bug caught: the init command does not reach the migration.
        old = self.tmp / "old.db"
        self.db_path = old
        self.build_old_db()

        self.assertEqual(0, results_db.main(["init", "--db", str(old)]))

        self.assertEqual(
            [(1, None), (0, None), (1, None), (0, None)],
            self.query("SELECT passed, status FROM eval_results ORDER BY run_number"))


if __name__ == "__main__":
    unittest.main()
