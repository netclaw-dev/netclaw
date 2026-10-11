"""Retain the actual failed observer journal before fixture teardown."""

from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

import background_process_fixture as runtime


class ProcessFailureRetentionControls(unittest.TestCase):
    def observe_failure(self, receipt, projector_exit=0, database=True):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            evidence = root / "output/child-runs"
            evidence.mkdir(parents=True)
            (evidence / "setup-base.json").write_text(json.dumps({
                "case": "child_run_approval_once", "nonce": "owned"}))
            home = root / "home"
            db = home / "data/netclaw.db"
            db.parent.mkdir(parents=True)
            order, retained = [], {}
            with closing(sqlite3.connect(db)) as live:
                live.execute("PRAGMA journal_mode=WAL")
                live.execute("CREATE TABLE evidence (value TEXT)")
                live.execute("INSERT INTO evidence VALUES ('committed')")
                live.commit()
                live.execute("INSERT INTO evidence VALUES ('uncommitted')")
                def observer(*args):
                    order.append("observer_failed")
                    folder = evidence / "observer"
                    folder.mkdir()
                    if receipt != "absent":
                        (folder / "observer-receipt.json").write_text(json.dumps(receipt))
                    raise AssertionError("The actual observer control failed.")
                def projector(argv, **kwargs):
                    order.append("journal_projected")
                    self.assertEqual([str(evidence / "journal-backup.sqlite"), "actual-owned-session",
                                      str(evidence / "journal.json")], argv[-3:])
                    with closing(sqlite3.connect(evidence / "journal-backup.sqlite")) as copy:
                        retained["rows"] = copy.execute("SELECT value FROM evidence").fetchall()
                    if projector_exit == 0:
                        (evidence / "journal.json").write_text("[]")
                    return SimpleNamespace(returncode=projector_exit, stdout=b"projector output", stderr=b"projector error")
                def control(port, action, **body):
                    order.append(action)
                    if action == "snapshot":
                        retained["backup_before_snapshot"] = (evidence / "journal-backup.sqlite").is_file()
                    return {"released": True}
                environment = {"RUNS": "1", "TMPDIR_EVAL": str(root / "output"),
                    "EVAL_HOME": str(home if database else root / "missing-home"),
                    "NETCLAW_EVAL_CASE": "child_run_approval_once", "EVAL_CONTAINER_NAME": "netclaw-eval-owned",
                    "NETCLAW_IMAGE": "owned-image"}
                with patch.dict(os.environ, environment), \
                        patch.object(runtime.subprocess, "check_output", return_value="owned-container\n"), \
                        patch.object(runtime, "invoke_observer", side_effect=observer), \
                        patch.object(runtime, "observer_command", return_value=["owned-projector"]), \
                        patch.object(runtime.subprocess, "run", side_effect=projector) as project, \
                        patch.object(runtime, "control", side_effect=control):
                    result = runtime.run(1)
                report = json.loads((evidence / "trial-receipt.json").read_text())
                outer = json.loads((root / "output/stdout_background-results.txt").read_text())
                self.assertEqual(1, result)
                self.assertFalse(report["passed"])
                self.assertEqual("AssertionError: The actual observer control failed.", report["error"])
                self.assertEqual(report["error"], outer["errors"][0])
                return report, outer, order, retained, project.call_count

    def test_actual_failed_session_retains_only_committed_wal_bytes_before_final_snapshot(self):
        report, outer, order, retained, count = self.observe_failure({"session_id": "actual-owned-session"})
        self.assertEqual("actual-owned-session", report.get("session_id"))
        self.assertNotIn("journal_retention_error", report)
        self.assertEqual([report["error"]], outer["errors"])
        self.assertEqual([("committed",)], retained["rows"])
        self.assertTrue(retained["backup_before_snapshot"])
        self.assertEqual(["child-setup", "observer_failed", "journal_projected", "snapshot"], order)
        self.assertEqual(1, count)

    def test_absent_or_invalid_actual_session_fails_without_a_guessed_capture(self):
        for receipt in ["absent", None, [], {}, {"session_id": None}, {"session_id": ""},
                        {"session_id": "   "}, {"session_id": 123}]:
            with self.subTest(receipt=receipt):
                report, outer, order, retained, count = self.observe_failure(receipt)
                self.assertIn("journal_retention_error", report)
                self.assertEqual(report["journal_retention_error"], outer["errors"][1])
                self.assertNotIn("session_id", report)
                self.assertFalse(retained["backup_before_snapshot"])
                self.assertEqual(0, count)

    def test_backup_fault_stays_explicit_beside_the_original_observer_failure(self):
        report, outer, order, retained, count = self.observe_failure({"session_id": "actual-owned-session"}, database=False)
        self.assertIn("journal_retention_error", report)
        self.assertIn("owned live journal", report["journal_retention_error"])
        self.assertEqual(report["journal_retention_error"], outer["errors"][1])
        self.assertFalse(retained["backup_before_snapshot"])
        self.assertEqual(0, count)

    def test_projector_fault_retains_the_backup_and_both_explicit_errors(self):
        report, outer, order, retained, count = self.observe_failure({"session_id": "actual-owned-session"}, projector_exit=7)
        self.assertIn("journal_retention_error", report)
        self.assertIn("canonical journal decoder failed", report["journal_retention_error"])
        self.assertEqual(report["journal_retention_error"], outer["errors"][1])
        self.assertTrue(retained["backup_before_snapshot"])
        self.assertEqual([("committed",)], retained["rows"])
        self.assertEqual(1, count)


class ProcessCorruptSqliteControls(unittest.TestCase):
    def run_corrupt_backup(self, observer_fails):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            evidence = root / "output/child-runs"
            evidence.mkdir(parents=True)
            (evidence / "setup-base.json").write_text(json.dumps({
                "case": "child_run_approval_once", "nonce": "owned"}))
            home = root / "home"
            database = home / "data/netclaw.db"
            database.parent.mkdir(parents=True)
            database.write_bytes(b"This is a corrupt owned SQLite file.\n")
            def observer(*args):
                folder = evidence / "observer"
                folder.mkdir()
                receipt = {"session_id": "actual-owned-session"}
                (folder / "observer-receipt.json").write_text(json.dumps(receipt))
                if observer_fails:
                    raise AssertionError("The actual observer control failed.")
                return receipt, None
            environment = {"RUNS": "1", "TMPDIR_EVAL": str(root / "output"),
                "EVAL_HOME": str(home), "NETCLAW_EVAL_CASE": "child_run_approval_once",
                "EVAL_CONTAINER_NAME": "netclaw-eval-owned", "NETCLAW_IMAGE": "owned-image"}
            with patch.dict(os.environ, environment), \
                    patch.object(runtime.subprocess, "check_output", return_value="owned-id\n"), \
                    patch.object(runtime, "invoke_observer", side_effect=observer), \
                    patch.object(runtime, "control", return_value={"released": True}), \
                    patch.object(runtime.subprocess, "run") as projector:
                try:
                    result = runtime.run(1)
                except sqlite3.Error as error:
                    self.fail("The real SQLite fault escaped the report boundary: " + str(error))
            self.assertEqual(1, result)
            self.assertEqual(0, projector.call_count)
            report = json.loads((evidence / "trial-receipt.json").read_bytes())
            outer = json.loads((root / "output/stdout_background-results.txt").read_bytes())
            self.assertFalse(report["passed"])
            self.assertFalse(outer["passed"])
            return report, outer

    def test_corrupt_sqlite_keeps_the_observer_failure_primary_and_retention_fault_explicit(self):
        report, outer = self.run_corrupt_backup(True)
        self.assertEqual("AssertionError: The actual observer control failed.", report["error"])
        self.assertEqual("DatabaseError: file is not a database", report["journal_retention_error"])
        self.assertEqual([report["error"], report["journal_retention_error"]], outer["errors"])
        self.assertEqual("actual-owned-session", report["session_id"])

    def test_corrupt_sqlite_after_a_successful_observer_stays_an_explicit_primary_failure(self):
        report, outer = self.run_corrupt_backup(False)
        self.assertEqual("DatabaseError: file is not a database", report["error"])
        self.assertNotIn("journal_retention_error", report)
        self.assertEqual([report["error"]], outer["errors"])
        self.assertEqual("actual-owned-session", report["session_id"])
