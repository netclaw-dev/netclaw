"""Exercise authorization-corpus cache reuse through real Git worktrees."""

from __future__ import annotations

import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "authorization-corpus/run.py"
SPEC = importlib.util.spec_from_file_location("authorization_corpus", SCRIPT)
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


class CompletedProbe:
    returncode = 0

    def wait(self, timeout):
        return 0


class CorpusCacheTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repository = self.root / "repository"
        self.repository.mkdir()
        self.out = self.root / "results"
        self.out.mkdir()
        self.corpus = self.out / "corpus.json"
        self.corpus.write_text('["ls"]', encoding="utf-8")
        self.git("init", "-q", "-b", "main")
        self.write("src/Netclaw.Actors.Tests/Tools/Fixture.cs", "class Fixture {}\n")
        self.write("Directory.Packages.props", "base\n")
        self.base = self.commit()
        self.builds = 0
        self.real_run = subprocess.run
        self.real_popen = subprocess.Popen

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.repository, check=True,
                              capture_output=True).stdout.decode().strip()

    def write(self, name, content):
        path = self.repository / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def commit(self, empty=False):
        self.git("add", "-A")
        args = ["-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "commit", "-q", "-m", "Cache fixture"]
        if empty:
            args.append("--allow-empty")
        self.git(*args)
        return self.git("rev-parse", "HEAD")

    def build(self, command, **kwargs):
        if command[:2] == ["dotnet", "build"]:
            self.builds += 1
            return subprocess.CompletedProcess(command, 0)
        return self.real_run(command, **kwargs)

    def probe(self, command, **kwargs):
        if command[:2] != ["dotnet", "test"]:
            return self.real_popen(command, **kwargs)
        worktree = Path(kwargs["cwd"])
        package = (worktree / "Directory.Packages.props").read_text().strip()
        Path(kwargs["env"]["NETCLAW_CORPUS_OUT"]).write_text(
            f"package={package}; build={self.builds}\n", encoding="utf-8")
        kwargs["stdout"].write("Total:     1\n")
        return CompletedProbe()

    def run_revision(self, revision, fresh=False):
        with patch.object(runner.subprocess, "run", side_effect=self.build), \
                patch.object(runner.subprocess, "Popen", side_effect=self.probe), \
                contextlib.redirect_stdout(io.StringIO()):
            return runner.run_revision(
                self.repository, self.out, revision, "authorizer", self.corpus,
                "bash52-none-i-approval", 1, False, {}, "test", fresh)

    def test_package_only_revision_cannot_reuse_the_baseline(self):
        baseline = self.run_revision(self.base)
        self.write("Directory.Packages.props", "candidate\n")
        candidate = self.commit()
        self.assertEqual(self.git("rev-parse", f"{self.base}:src"),
                         self.git("rev-parse", f"{candidate}:src"))

        result = self.run_revision(candidate)

        self.assertEqual("package=candidate; build=2\n", result.read_text())
        self.assertEqual("package=base; build=1\n", baseline.read_text())
        self.assertNotEqual(baseline, result)

    def test_other_committed_root_inputs_cannot_reuse_the_baseline(self):
        baseline = self.run_revision(self.base)
        for index, name in enumerate(("Directory.Build.props", "Directory.Build.targets",
                                      "NuGet.Config", "global.json", "custom/import.props"), 2):
            with self.subTest(name=name):
                self.write(name, "candidate\n")
                candidate = self.commit()
                self.assertEqual(self.git("rev-parse", f"{self.base}:src"),
                                 self.git("rev-parse", f"{candidate}:src"))

                result = self.run_revision(candidate)

                self.assertEqual(f"package=base; build={index}\n", result.read_text())
                self.assertEqual("package=base; build=1\n", baseline.read_text())

    def test_identical_committed_tree_reuses_its_completed_result(self):
        baseline = self.run_revision(self.base)
        same_tree = self.commit(empty=True)

        result = self.run_revision(same_tree)

        self.assertEqual("package=base; build=1\n", result.read_text())
        self.assertEqual(baseline, result)

    def test_fresh_head_does_not_replace_the_cached_baseline(self):
        baseline = self.run_revision(self.base)

        fresh = self.run_revision(self.base, fresh=True)
        reused = self.run_revision(self.base)

        self.assertEqual("package=base; build=2\n", fresh.read_text())
        self.assertEqual("package=base; build=1\n", baseline.read_text())
        self.assertNotEqual(baseline, fresh)
        self.assertEqual(baseline, reused)

    def test_legacy_source_cache_remains_visible_but_cannot_be_reused(self):
        source = self.git("rev-parse", f"{self.base}:src")
        state = runner.hashlib.sha256(b"bash52-none-i-approval").hexdigest()[:8]
        legacy = self.out / f"src-{source[:12]}-authorizer-{runner.probe_digest('authorizer', self.corpus)}-{state}.tsv"
        legacy.write_text("legacy result\n", encoding="utf-8")

        result = self.run_revision(self.base)

        self.assertEqual("package=base; build=1\n", result.read_text())
        self.assertEqual("legacy result\n", legacy.read_text())
        self.assertNotEqual(legacy, result)

    def test_explicit_prune_removes_only_other_current_and_legacy_outputs(self):
        baseline = self.run_revision(self.base)
        other = self.out / "tree-other.tsv"
        legacy = self.out / "src-legacy.tsv"
        unrelated = self.out / "evidence.tsv"
        for path in (other, legacy, unrelated):
            path.write_text("preserve unless a cache output\n", encoding="utf-8")

        with contextlib.redirect_stdout(io.StringIO()):
            runner.clean_up(self.out, baseline, baseline, keep=False, prune_cache=True)

        self.assertEqual("package=base; build=1\n", baseline.read_text())
        self.assertFalse(other.exists())
        self.assertFalse(legacy.exists())
        self.assertEqual("preserve unless a cache output\n", unrelated.read_text())


if __name__ == "__main__":
    unittest.main()
