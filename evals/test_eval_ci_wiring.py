"""Verify that CI and the README run every eval test file.

F12 in the eval rework design: the CI job named four test files, and the README
pattern `test_*evals.py` missed `test_session_log_eval.py`. A new test file
then never ran. These tests read the workflow and the README as text.
"""

import fnmatch
from pathlib import Path
import re
import unittest

EVALS = Path(__file__).resolve().parent
REPO = EVALS.parent
WORKFLOW = REPO / ".github" / "workflows" / "pr_validation.yml"
README = EVALS / "README.md"

DISCOVER = re.compile(r"unittest\s+discover\s+-s\s+evals\s+-p\s+(?:'([^']+)'|\"([^\"]+)\"|(\S+))")


def test_files():
    return sorted(path.name for path in EVALS.glob("test_*.py"))


def patterns(text):
    return [next(group for group in match.groups() if group) for match in DISCOVER.finditer(text)]


def uncovered(names, found_patterns):
    return [name for name in names if not any(fnmatch.fnmatch(name, p) for p in found_patterns)]


def eval_oracles_job():
    text = WORKFLOW.read_text()
    start = text.index("  eval-oracles:")
    following = re.search(r"^  [a-z][a-z0-9-]*:\s*$", text[start + 1:], re.MULTILINE)
    return text[start:start + 1 + following.start()] if following else text[start:]


class EvalTestDiscoveryTests(unittest.TestCase):
    def test_ci_job_runs_every_eval_test_file(self):
        # Planted bug: the job names files one by one, so a file that nobody
        # adds to the list never runs (test_session_log_eval.py today).
        found = patterns(eval_oracles_job())
        self.assertTrue(found, "the Eval Oracle Tests job has no unittest discover command")
        self.assertEqual([], uncovered(test_files(), found),
                         "these test files do not run in the Eval Oracle Tests job")

    def test_readme_command_runs_every_eval_test_file(self):
        # Planted bug: the README pattern test_*evals.py skips test_session_log_eval.py.
        section = README.read_text().split("Run the assertion tests without a model:", 1)[1][:600]
        found = patterns(section)
        self.assertTrue(found, "the README has no unittest discover command in that section")
        self.assertEqual([], uncovered(test_files(), found),
                         "these test files do not run with the README command")


if __name__ == "__main__":
    unittest.main()
