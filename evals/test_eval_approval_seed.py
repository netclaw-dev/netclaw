"""Controls for the selected coding cases' existing shell grant format."""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from test_coordination_workflow_evals import shell_functions

ROOT = Path(__file__).resolve().parents[1]
ASSET = ROOT / "evals/fixtures/config/tool-approvals.json"
CODING_CASES = ("coordination_implement_review", "coordination_two_writers")


class EvalApprovalSeedTests(unittest.TestCase):
    def seed(self, home, case, override=None):
        (home / "data/config").mkdir(parents=True)
        environment = {**os.environ, "EVAL_HOME": str(home), "EVAL_ASSET_ROOT": str(ROOT), "FILTER_CASE": case}
        environment.pop("NETCLAW_EVAL_APPROVALS_FILE", None)
        if override is not None:
            environment["NETCLAW_EVAL_APPROVALS_FILE"] = str(override)
        return subprocess.run(["bash", "-euc", shell_functions("seed_eval_approvals") + "\nseed_eval_approvals"],
                              env=environment, capture_output=True, text=True)

    def test_selected_cases_add_only_the_two_workspace_scoped_verbs(self):
        original = ASSET.read_bytes()
        for case in CODING_CASES:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                home = Path(directory) / "owned home with spaces"
                result = self.seed(home, case)
                self.assertEqual(0, result.returncode, result.stderr)
                policy = json.loads((home / "data/config/tool-approvals.json").read_bytes())
                entries = policy["audiences"]["personal"]["shell_execute"]
                self.assertEqual([
                    {"verb": "git add", "directory": "/home/netclaw/.netclaw/workspaces"},
                    {"verb": "git commit", "directory": "/home/netclaw/.netclaw/workspaces"},
                ], entries[-2:])
                del entries[-2:]
                self.assertEqual(json.loads(original), policy)
        self.assertEqual(original, ASSET.read_bytes())

    def test_other_cases_preserve_the_exact_default_fixture_bytes(self):
        for case in ("", "coordination_conflicting_evidence", "coordination_trivial_task",
                     "coordination_unavailable_profile", "productive_parent_child", "child_run_held_parent"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                home = Path(directory) / "owned home"
                result = self.seed(home, case)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(ASSET.read_bytes(), (home / "data/config/tool-approvals.json").read_bytes())

    def test_explicit_overrides_preserve_bytes_without_added_grants(self):
        original = b'{ "version": 2, "audiences": { "personal": { "shell_execute": [] } } }\r\n'
        for case in (*CODING_CASES, "coordination_conflicting_evidence"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                override = root / "operator policy with spaces.json"
                override.write_bytes(original)
                home = root / "owned home"
                result = self.seed(home, case, override)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(original, (home / "data/config/tool-approvals.json").read_bytes())
                self.assertEqual(original, override.read_bytes())

    def test_missing_explicit_override_fails_without_default_grants(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            home = root / "owned home"
            result = self.seed(home, CODING_CASES[0], root / "absent.json")
            self.assertNotEqual(0, result.returncode)
            self.assertFalse((home / "data/config/tool-approvals.json").exists())


if __name__ == "__main__":
    unittest.main()
