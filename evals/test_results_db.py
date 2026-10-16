"""Tests for evals/results_db.py: the results.db schema and its writes.

Each run makes a new results.db, so there is no migration to test. The tests
use only the standard library.

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


class FreshSchemaTests(TempDirTestCase):
    def test_new_run_has_zero_inconclusive_cases_by_default(self):
        # Bug caught: a wrong default for inconclusive_cases. A writer that does not
        # know the column must read 0.
        results_db.init_db(self.db_path)
        database = sqlite3.connect(self.db_path)
        try:
            database.execute(
                "INSERT INTO eval_runs (run_id, started_at, netclaw_ver, runs_per_case, threshold, "
                "total_cases, passed_cases, overall_score) VALUES ('r', 't', 'v', 1, 0.8, 1, 1, 1.0)")
            database.commit()
        finally:
            database.close()

        self.assertEqual([(0,)], self.query("SELECT inconclusive_cases FROM eval_runs"))

    def test_init_on_a_fresh_database_is_idempotent(self):
        # Bug caught: a re-init changes the schema or the rows.
        results_db.init_db(self.db_path)
        results_db.store_result(self.db_path, "r", "cat", "c", 1, "p", "inconclusive", "timeout", "")
        before = self.snapshot()

        results_db.init_db(self.db_path)

        self.assertEqual(before, self.snapshot())

    def test_average_of_passed_treats_inconclusive_rows_as_absent(self):
        # Bug caught: an inconclusive row is stored with passed=0, so an AVG(passed) query
        # counts it as a fail. Rows: 1 pass, 1 fail, 2 inconclusive.
        results_db.init_db(self.db_path)
        results_db.store_result(self.db_path, "r", "cat", "c", 1, "p", "pass", "", "")
        results_db.store_result(self.db_path, "r", "cat", "c", 2, "p", "fail", "", "")
        results_db.store_result(self.db_path, "r", "cat", "c", 3, "p", "inconclusive", "timeout", "")
        results_db.store_result(self.db_path, "r", "cat", "c", 4, "p", "inconclusive", "turn_failed", "")

        (average,) = self.query("SELECT AVG(passed) FROM eval_results")[0]
        (counted,) = self.query("SELECT COUNT(passed) FROM eval_results")[0]
        (inconclusive_passed,) = self.query(
            "SELECT COUNT(passed) FROM eval_results WHERE status = 'inconclusive'")[0]

        self.assertEqual(2, counted)
        self.assertEqual(0, inconclusive_passed)
        self.assertAlmostEqual(1 / 2, average)


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
        for status, passed in inconsistent:
            with self.subTest(status=status, passed=passed):
                with self.assertRaises(sqlite3.IntegrityError):
                    self.insert(status, passed, case=f"{status}-{passed}")
        self.assertEqual([(0,)], self.query("SELECT COUNT(*) FROM eval_results"))

    def test_check_rejects_a_pass_or_a_fail_without_a_passed_value(self):
        # Bug caught: the CHECK expression is `status = 'pass' AND passed = 1`. It gives NULL
        # when passed is NULL, and SQLite accepts a NULL result. A writer could then store
        # ('pass', NULL), and AVG(passed) would skip a real pass while status says pass.
        for status in ("pass", "fail"):
            with self.subTest(status=status):
                with self.assertRaises(sqlite3.IntegrityError):
                    self.insert(status, None, case=f"{status}-null")
        self.assertEqual([(0,)], self.query("SELECT COUNT(*) FROM eval_results"))

    def test_check_accepts_consistent_rows(self):
        # Bug caught: the CHECK constraint is too strict and blocks a valid row.
        self.insert("pass", 1, case="a")
        self.insert("fail", 0, case="b")
        self.insert("inconclusive", None, case="c")
        self.assertEqual([(3,)], self.query("SELECT COUNT(*) FROM eval_results"))

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


if __name__ == "__main__":
    unittest.main()
