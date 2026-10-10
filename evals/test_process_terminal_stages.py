"""Keep original terminals distinct from prepared delivery data."""

import importlib.util
import os
from pathlib import Path
import unittest

import test_background_process_evals as controls


candidate = os.environ.get("BACKGROUND_PROCESS_STAGE_CANDIDATE")
if candidate:
    spec = importlib.util.spec_from_file_location("independent_terminal_stage_oracle", Path(candidate))
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
else:
    oracle = controls.oracle


def stage(record, kind):
    data = next(row["data"] for row in record["journal"]
        if row["event_type"] == "ChildRunEvent" and row["data"]["kind"] == kind)
    data["terminal"] = controls.copy.deepcopy(data["terminal"])
    return data["terminal"]


class TerminalStageControls(unittest.TestCase):
    def rejects(self, record):
        with self.assertRaises((AssertionError, ValueError)):
            oracle.verify_process_trial(**record)

    def test_completed_original_paths_can_be_absent_or_canonical(self):
        for log_absent, artifact_absent in [(False, False), (True, True), (True, False), (False, True)]:
            with self.subTest(log_absent=log_absent, artifact_absent=artifact_absent):
                record = controls.valid_routed()
                original = stage(record, "TerminalRecorded")
                if log_absent:
                    original["log_path"] = None
                if artifact_absent:
                    original["artifact_directory"] = None
                self.assertTrue(oracle.verify_process_trial(**record)["passed"])

    def test_completed_original_foreign_paths_reject(self):
        for field in ["log_path", "artifact_directory"]:
            record = controls.valid_routed()
            stage(record, "TerminalRecorded")[field] = "/another-owner/foreign"
            self.rejects(record)

    def test_absent_paths_do_not_hide_changed_original_terminal_facts(self):
        for field, value in [("run_id", "foreign"), ("scope_id", "foreign"), ("source_operation", "spawn_agent"),
                ("state", "Failed"), ("outcome", "Failed"), ("reason", "forged"), ("output", "forged output"),
                ("working_context", {"ConfirmedChangedFiles": ["/foreign"]}), ("findings", ["invented"]),
                ("warning", "forged"), ("checkpoint", {"CompletedRound": 999})]:
            with self.subTest(field=field):
                record = controls.valid_routed()
                original = stage(record, "TerminalRecorded")
                original["log_path"] = original["artifact_directory"] = None
                original[field] = value
                self.rejects(record)

    def test_prepared_terminal_requires_exact_paths_and_facts(self):
        for field, value in [("log_path", None), ("artifact_directory", None), ("output", "changed"),
                ("state", "Failed"), ("warning", "changed"), ("working_context", {})]:
            with self.subTest(field=field):
                record = controls.valid_routed()
                stage(record, "ResultPrepared")[field] = value
                self.rejects(record)

    def test_cancelled_and_lost_original_terminals_keep_exact_paths(self):
        for name, make in [("cancel", lambda: controls.valid_approval(True)), ("lost", controls.valid_recovery)]:
            with self.subTest(case=name):
                self.assertTrue(oracle.verify_process_trial(**make())["passed"])
            for field in ["log_path", "artifact_directory"]:
                for value in [None, "/foreign"]:
                    with self.subTest(case=name, field=field, value=value):
                        record = make()
                        stage(record, "TerminalRecorded")[field] = value
                        self.rejects(record)

    def test_cancelled_and_lost_facts_cannot_change_between_stages(self):
        for name, make in [("cancel", lambda: controls.valid_approval(True)), ("lost", controls.valid_recovery)]:
            for kind in ["TerminalRecorded", "ResultPrepared"]:
                for field, value in [("output", "claimed success"), ("outcome", "Completed"),
                        ("warning", "uncertainty removed"), ("checkpoint", {"CompletedRound": 999})]:
                    with self.subTest(case=name, kind=kind, field=field):
                        record = make()
                        stage(record, kind)[field] = value
                        self.rejects(record)


if __name__ == "__main__":
    unittest.main()
