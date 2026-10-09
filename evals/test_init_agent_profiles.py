"""Reject missing or altered profile files in the native init oracle."""

from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


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


if __name__ == "__main__":
    unittest.main()
