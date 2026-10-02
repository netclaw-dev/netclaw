"""Fake-failure tests for scripts/check-approval-outcome-direction.py.

Run: python3 -m unittest discover -s scripts/tests -p 'test_*.py' -v
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest

SCRIPT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "check-approval-outcome-direction.py")
_spec = importlib.util.spec_from_file_location("check_direction", SCRIPT)
direction = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = direction
_spec.loader.exec_module(direction)

SECTION = "Fresh Personal approval matrix"
HEADER = (
    "| ID | Host | Command | Result | Reason |\n"
    "| --- | --- | --- | --- | --- |\n")


def snapshot(rows: dict[str, str], section: str = SECTION) -> str:
    body = "".join(
        f"| {case_id} | Bash | git status \\| head -1 | {result} | reason |\n"
        for case_id, result in rows.items())
    return f"\ufeff# {section}\n\nSome text.\n\n{HEADER}{body}"


BASE_ROWS = {
    "safe-allows": "Allowed",
    "push-prompts": "RequiresApproval",
    "external-prompts": "RequiresApproval",
    "hard-deny-blocks": "Denied",
}


def change(**overrides) -> dict:
    entry = {
        "section": SECTION,
        "id": "push-prompts",
        "from": "RequiresApproval",
        "to": "Allowed",
        "reason": "Reviewed fatigue reduction.",
        "negativeControl": "external-prompts",
    }
    entry.update(overrides)
    return entry


class DirectionCheckTests(unittest.TestCase):
    def run_check(self, candidate_rows, changes=(), baseline_rows=None,
                  baseline_changes=None, candidate_text=None):
        with tempfile.TemporaryDirectory() as root:
            def write(name, text):
                path = os.path.join(root, name)
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(text)
                return path

            args = [
                "--baseline", write("base.md", snapshot(baseline_rows or BASE_ROWS)),
                "--candidate", write("cand.md", candidate_text or snapshot(candidate_rows)),
                "--intended", write("intended.json", json.dumps({"changes": list(changes)})),
            ]
            if baseline_changes is not None:
                args += ["--baseline-intended",
                         write("base-intended.json", json.dumps({"changes": baseline_changes}))]
            output = io.StringIO()
            with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                code = direction.main(args)
            return code, output.getvalue()

    def test_pass_on_no_change(self):
        code, output = self.run_check(BASE_ROWS)
        self.assertEqual(0, code, output)
        self.assertIn("Transitions: 0", output)

    def test_fail_allowed_to_requires_approval(self):
        code, output = self.run_check({**BASE_ROWS, "safe-allows": "RequiresApproval"})
        self.assertEqual(1, code, output)
        self.assertIn("Allowed must stay Allowed", output)

    def test_fail_allowed_change_listed_without_approval(self):
        entry = change(id="safe-allows", **{"from": "Allowed", "to": "Denied"}, negativeControl="hard-deny-blocks")
        code, output = self.run_check({**BASE_ROWS, "safe-allows": "Denied"}, [entry])
        self.assertEqual(1, code, output)
        self.assertIn("Allowed must stay Allowed", output)

    def test_fail_allowed_change_with_approval_but_no_negative_control(self):
        entry = change(id="safe-allows", **{"from": "Allowed", "to": "Denied"}, approvedBy="owner")
        entry.pop("negativeControl", None)
        code, output = self.run_check({**BASE_ROWS, "safe-allows": "Denied"}, [entry])
        self.assertEqual(1, code, output)
        self.assertIn("intended change has no negativeControl", output)

    def test_pass_allowed_change_with_approval_and_negative_control(self):
        entry = change(
            id="safe-allows",
            **{"from": "Allowed", "to": "RequiresApproval"},
            approvedBy="owner",
            negativeControl="hard-deny-blocks")
        code, output = self.run_check({**BASE_ROWS, "safe-allows": "RequiresApproval"}, [entry])
        self.assertEqual(0, code, output)
        self.assertIn("approved by owner", output)

    def test_pass_requires_approval_to_correction(self):
        code, output = self.run_check({**BASE_ROWS, "push-prompts": "RequiresAgentCorrection"})
        self.assertEqual(0, code, output)
        self.assertIn("the call does not run", output)

    def test_fail_unlisted_correction_to_allowed(self):
        baseline = {**BASE_ROWS, "push-prompts": "RequiresAgentCorrection"}
        code, output = self.run_check({**BASE_ROWS, "push-prompts": "Allowed"}, baseline_rows=baseline)
        self.assertEqual(1, code, output)
        self.assertIn("RequiresAgentCorrection -> Allowed needs an intended change", output)

    def test_fail_denied_to_allowed(self):
        code, output = self.run_check({**BASE_ROWS, "hard-deny-blocks": "Allowed"})
        self.assertEqual(1, code, output)
        self.assertIn("Denied -> other needs an intended change with approvedBy", output)

    def test_pass_denied_change_with_owner_approval(self):
        entry = change(id="hard-deny-blocks", **{"from": "Denied", "to": "RequiresApproval"},
                       approvedBy="owner")
        code, output = self.run_check({**BASE_ROWS, "hard-deny-blocks": "RequiresApproval"}, [entry])
        self.assertEqual(0, code, output)

    def test_fail_removed_case(self):
        rows = dict(BASE_ROWS)
        del rows["push-prompts"]
        code, output = self.run_check(rows)
        self.assertEqual(1, code, output)
        self.assertIn("case removed", output)

    def test_pass_added_case(self):
        code, output = self.run_check({**BASE_ROWS, "new-case-prompts": "RequiresApproval"})
        self.assertEqual(0, code, output)
        self.assertIn("case added", output)

    def test_pass_listed_requires_approval_to_allowed_with_negative_control(self):
        code, output = self.run_check({**BASE_ROWS, "push-prompts": "Allowed"}, [change()])
        self.assertEqual(0, code, output)
        self.assertIn("negative control external-prompts is RequiresApproval", output)

    def test_fail_unlisted_requires_approval_to_allowed(self):
        code, output = self.run_check({**BASE_ROWS, "push-prompts": "Allowed"})
        self.assertEqual(1, code, output)
        self.assertIn("RequiresApproval -> Allowed needs an intended change", output)

    def test_fail_missing_negative_control(self):
        code, output = self.run_check(
            {**BASE_ROWS, "push-prompts": "Allowed"}, [change(negativeControl="no-such-case")])
        self.assertEqual(1, code, output)
        self.assertIn("must exist in the baseline and the candidate", output)

    def test_fail_negative_control_added_by_the_same_change(self):
        rows = {**BASE_ROWS, "push-prompts": "Allowed", "new-control-prompts": "RequiresApproval"}
        code, output = self.run_check(rows, [change(negativeControl="new-control-prompts")])
        self.assertEqual(1, code, output)
        self.assertIn("must exist in the baseline and the candidate", output)

    def test_fail_negative_control_allowed_in_baseline(self):
        baseline = {**BASE_ROWS, "control-was-allowed": "Allowed"}
        rows = {**BASE_ROWS, "push-prompts": "Allowed", "control-was-allowed": "RequiresApproval"}
        code, output = self.run_check(
            rows, [change(negativeControl="control-was-allowed")], baseline_rows=baseline)
        self.assertEqual(1, code, output)
        self.assertIn("it must prompt or deny in both", output)

    def test_fail_allowed_negative_control(self):
        code, output = self.run_check(
            {**BASE_ROWS, "push-prompts": "Allowed"}, [change(negativeControl="safe-allows")])
        self.assertEqual(1, code, output)
        self.assertIn("it must prompt or deny", output)

    def test_fail_negative_control_absent_from_entry(self):
        entry = change()
        del entry["negativeControl"]
        code, output = self.run_check({**BASE_ROWS, "push-prompts": "Allowed"}, [entry])
        self.assertEqual(1, code, output)
        self.assertIn("no negativeControl", output)

    def test_fail_requires_approval_to_denied_without_approval(self):
        entry = change(**{"to": "Denied"})
        del entry["negativeControl"]
        code, output = self.run_check({**BASE_ROWS, "push-prompts": "Denied"}, [entry])
        self.assertEqual(1, code, output)
        self.assertIn("approvedBy", output)

    def test_fail_stale_entry(self):
        code, output = self.run_check(BASE_ROWS, [change()])
        self.assertEqual(1, code, output)
        self.assertIn("stale intended change", output)

    def test_fail_entry_with_wrong_direction(self):
        entry = change(**{"to": "Denied"}, approvedBy="owner")
        code, output = self.run_check({**BASE_ROWS, "push-prompts": "Allowed"}, [entry])
        self.assertEqual(1, code, output)
        self.assertIn("actual is RequiresApproval -> Allowed", output)

    def test_entry_from_baseline_file_is_history_and_justifies_nothing(self):
        code, output = self.run_check(
            {**BASE_ROWS, "push-prompts": "Allowed"}, [change()], baseline_changes=[change()])
        self.assertEqual(1, code, output)
        self.assertIn("history:", output)
        self.assertIn("RequiresApproval -> Allowed needs an intended change", output)

    def test_history_entry_is_not_stale(self):
        code, output = self.run_check(BASE_ROWS, [change()], baseline_changes=[change()])
        self.assertEqual(0, code, output)

    def test_bad_input_duplicate_case_id(self):
        text = snapshot(BASE_ROWS) + "| safe-allows | Bash | ls | Allowed | reason |\n"
        code, output = self.run_check(BASE_ROWS, candidate_text=text)
        self.assertEqual(2, code, output)
        self.assertIn("duplicate case", output)

    def test_bad_input_duplicate_result_column(self):
        # The second Result column holds the regression; the check must not read the first one only.
        text = (
            "# " + SECTION + "\n\n"
            "| ID | Result | Result |\n| --- | --- | --- |\n"
            "| safe-allows | Allowed | RequiresApproval |\n")
        code, output = self.run_check(BASE_ROWS, candidate_text=text)
        self.assertEqual(2, code, output)
        self.assertIn("repeats column(s) ['Result']", output)

    def test_bad_input_unknown_result(self):
        code, output = self.run_check({**BASE_ROWS, "safe-allows": "Maybe"})
        self.assertEqual(2, code, output)

    def test_rows_are_keyed_by_section(self):
        text = snapshot(BASE_ROWS) + "\n" + snapshot(
            {"safe-allows": "RequiresApproval"}, "Other").lstrip("\ufeff")
        code, output = self.run_check(BASE_ROWS, candidate_text=text)
        self.assertEqual(0, code, output)
        self.assertIn("case added", output)

    def test_escaped_pipe_stays_in_one_cell(self):
        rows = direction.parse_snapshot(snapshot(BASE_ROWS), "test")
        self.assertEqual("Allowed", rows[(SECTION, "safe-allows")].result)


class GitModeTests(unittest.TestCase):
    """Exercise the default mode: baseline from the merge base with a base ref."""

    def git(self, root, *args):
        subprocess.run(["git", "-C", root, *args], check=True, capture_output=True)

    def commit_files(self, root, rows, changes, message):
        tools = os.path.join(root, os.path.dirname(direction.SNAPSHOT_PATH))
        os.makedirs(tools, exist_ok=True)
        with open(os.path.join(root, direction.SNAPSHOT_PATH), "w", encoding="utf-8") as handle:
            handle.write(snapshot(rows))
        with open(os.path.join(root, direction.INTENDED_CHANGES_PATH), "w", encoding="utf-8") as handle:
            handle.write(json.dumps({"changes": changes}))
        self.git(root, "add", "-A")
        self.git(root, "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                 "commit", "-q", "-m", message)

    def test_merge_base_mode(self):
        with tempfile.TemporaryDirectory() as root:
            self.git(root, "init", "-q", "-b", "main")
            self.commit_files(root, BASE_ROWS, [], "base")
            self.git(root, "branch", "base")
            self.git(root, "checkout", "-q", "-b", "work")
            self.commit_files(root, {**BASE_ROWS, "safe-allows": "RequiresApproval"}, [], "regress")
            output = io.StringIO()
            with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                code = direction.main(["--repo", root, "--base-ref", "base"])
            self.assertEqual(1, code, output.getvalue())
            self.assertIn("Allowed must stay Allowed", output.getvalue())


if __name__ == "__main__":
    unittest.main()
