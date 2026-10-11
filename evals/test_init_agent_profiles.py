"""Reject missing or altered profile files in the native init oracle."""

from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest

from test_coordination_workflow_evals import shell_functions


ROOT = Path(__file__).resolve().parent.parent
ASSET = ROOT / "src/Netclaw.Cli/Resources/identity/task-worker.profile.md"
SPECIALIST = "---\nname: summarizer\ndescription: Operator smoke profile\nmodelRole: Compaction\n---\nPreserve operator profile.\n"


class InitProfileOracleTests(unittest.TestCase):
    def check_profiles(self, mutation, expected):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory) / "tape home with spaces"
            (home / "agents").mkdir(parents=True)
            worker = home / "agents/task-worker.md"
            specialist = home / "agents/summarizer.md"
            shutil.copyfile(ASSET, worker)
            specialist.write_text(SPECIALIST)
            mutation(worker, specialist)
            completed = subprocess.run([
                "bash", "-c", 'source "$1"; assert_init_agent_profiles "$2" "$3"',
                "bash", str(ROOT / "tests/smoke/assertions/_init-agent-profiles.sh"), str(home), str(ASSET)
            ], capture_output=True, text=True)
            self.assertEqual(expected, completed.returncode == 0, completed.stderr)

    def test_canonical_worker_and_preserved_specialist_pass(self):
        self.check_profiles(lambda worker, specialist: None, True)

    def test_absent_worker_fails(self):
        self.check_profiles(lambda worker, specialist: worker.unlink(), False)

    def test_changed_operator_profile_fails(self):
        self.check_profiles(lambda worker, specialist: specialist.write_text("Changed operator content.\n"), False)


class EvalProfileSeedTests(unittest.TestCase):
    def seed(self, home, case, assets=ROOT):
        (home / "data/agents").mkdir(parents=True)
        return subprocess.run(["bash", "-euc", shell_functions("seed_eval_agents") + "\nseed_eval_agents"],
                              env={**os.environ, "EVAL_HOME": str(home), "EVAL_ASSET_ROOT": str(assets), "FILTER_CASE": case},
                              capture_output=True, text=True)

    def assert_existing_profiles(self, home):
        for fixture in (ROOT / "evals/fixtures/agents").glob("*.md"):
            self.assertEqual(fixture.read_bytes(), (home / "data/agents" / fixture.name).read_bytes())
        self.assertEqual(ASSET.read_bytes(), (home / "data/agents/task-worker.md").read_bytes())

    def test_required_cases_seed_the_reviewer_and_preserve_all_existing_profiles(self):
        for case in ("coordination_implement_review", "coordination_conflicting_evidence"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                home = Path(directory) / "owned home with spaces"
                result = self.seed(home, case)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assert_existing_profiles(home)
                self.assertTrue((home / "data/agents/code-analyst.md").is_file())
                self.assertEqual((ROOT / "evals/fixtures/coordination-agents/code-analyst.md").read_bytes(),
                                 (home / "data/agents/code-analyst.md").read_bytes())

    def test_other_cases_keep_the_existing_catalog_without_the_reviewer(self):
        for case in ("", "coordination_trivial_task", "coordination_unavailable_profile", "productive_parent_child",
                     "coordination_stale_incomplete", "child_run_held_parent"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                home = Path(directory) / "owned home"
                result = self.seed(home, case)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assert_existing_profiles(home)
                self.assertFalse((home / "data/agents/code-analyst.md").exists())

    def test_missing_required_reviewer_fixture_fails_the_selected_case(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            assets = root / "assets"
            shutil.copytree(ROOT / "evals/fixtures/agents", assets / "evals/fixtures/agents")
            worker = assets / "src/Netclaw.Cli/Resources/identity/task-worker.profile.md"
            worker.parent.mkdir(parents=True)
            shutil.copyfile(ASSET, worker)
            result = self.seed(root / "home", "coordination_implement_review", assets)
            self.assertNotEqual(0, result.returncode)
            self.assert_existing_profiles(root / "home")
            self.assertFalse((root / "home/data/agents/code-analyst.md").exists())


if __name__ == "__main__":
    unittest.main()
