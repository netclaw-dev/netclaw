"""Tests for scripts/authorization-metrics.py.

Run: python3 -m unittest discover -s scripts/tests -p 'test_*.py' -v
"""

from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
import tempfile
import unittest

SCRIPT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "authorization-metrics.py")
_spec = importlib.util.spec_from_file_location("authorization_metrics", SCRIPT)
metrics = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = metrics
_spec.loader.exec_module(metrics)


class MetricsTests(unittest.TestCase):
    def setUp(self):
        self._dir = tempfile.TemporaryDirectory()
        self.root = self._dir.name
        self.git("init", "-q", "-b", "main")

    def tearDown(self):
        self._dir.cleanup()

    def git(self, *args):
        subprocess.run(["git", "-C", self.root, *args], check=True, capture_output=True)

    def write(self, path, text):
        full = os.path.join(self.root, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8") as handle:
            handle.write(text)

    def commit(self):
        self.git("add", "-A")
        self.git("-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "c")

    def group(self, results, prefix):
        return next(r for r in results if r.group.name.startswith(prefix))

    def test_counts_listed_files_globs_and_skips_tests(self):
        self.write("src/Netclaw.Actors/Tools/PathAccessPolicy.cs",
                   "namespace N;\npublic sealed class PathAccessPolicy\n{\n}\ninternal record struct Scope(int X);\n")
        self.write("src/Netclaw.Security/Authorization/Filesystem/Link.cs", "internal enum LinkKind { A }\n")
        self.write("src/Netclaw.Actors/Authorization/Unsorted/Thing.cs", "file class Helper {}\n")
        self.write("src/Netclaw.Actors.Tests/Authorization/Filesystem/LinkTests.cs", "public class LinkTests {}\n")
        self.commit()

        results = metrics.measure(metrics.Source(self.root, "HEAD")).groups

        filesystem = self.group(results, "7.")
        self.assertEqual(
            ["src/Netclaw.Actors/Tools/PathAccessPolicy.cs",
             "src/Netclaw.Security/Authorization/Filesystem/Link.cs"],
            [item.path for item in filesystem.files])
        self.assertEqual(6, filesystem.loc)
        self.assertEqual(3, filesystem.types)
        self.assertEqual(
            ["src/Netclaw.Actors/Authorization/Unsorted/Thing.cs"],
            [item.path for item in self.group(results, "11.").files])
        all_paths = [item.path for result in results for item in result.files]
        self.assertNotIn("src/Netclaw.Actors.Tests/Authorization/Filesystem/LinkTests.cs", all_paths)
        self.assertEqual(len(all_paths), len(set(all_paths)))
        self.assertIn("src/Netclaw.Security/ToolPathPolicy.cs", self.group(results, "6.").missing)

    def test_rev_mode_reads_the_revision_not_the_working_tree(self):
        self.write("src/Netclaw.Actors/Tools/ShellProcessLaunch.cs", "a\nb\n")
        self.commit()
        self.write("src/Netclaw.Actors/Tools/ShellProcessLaunch.cs", "a\nb\nc\nd\n")

        at_head = self.group(metrics.measure(metrics.Source(self.root, "HEAD")).groups, "10.")
        working = self.group(metrics.measure(metrics.Source(self.root, None)).groups, "10.")

        self.assertEqual(2, at_head.loc)
        self.assertEqual(4, working.loc)

    def test_relocation_to_an_unlisted_path_stays_in_the_whole_tree_total(self):
        self.write("src/Netclaw.Actors/Tools/ShellProcessLaunch.cs", "public class ShellProcessLaunch\n{\n}\n")
        self.write("src/Netclaw.Actors.Tests/LaunchTests.cs", "public class LaunchTests\n{\n}\n")
        self.commit()
        os.makedirs(os.path.join(self.root, "src/Netclaw.Actors/Misc"))
        self.git("mv", "src/Netclaw.Actors/Tools/ShellProcessLaunch.cs", "src/Netclaw.Actors/Misc/Launch.cs")
        self.commit()

        base = metrics.measure(metrics.Source(self.root, "HEAD~1"))
        head = metrics.measure(metrics.Source(self.root, "HEAD"))

        self.assertEqual(3, base.grouped_loc)
        self.assertEqual(0, head.grouped_loc)
        self.assertEqual(3, base.production_loc)
        self.assertEqual(3, head.production_loc)
        self.assertEqual(1, head.production_types)
        report = metrics.render_compare(base, head)
        self.assertIn("All production .cs under src/", report)
        self.assertIn("WARNING:", report)
        self.assertIn("src/Netclaw.Actors/Tools/ShellProcessLaunch.cs  [10. Launch-time re-authorization]", report)


if __name__ == "__main__":
    unittest.main()
