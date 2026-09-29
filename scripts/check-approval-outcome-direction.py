#!/usr/bin/env python3
"""Check the direction of shell approval outcome changes.

The check compares the Result column of two versions of the shell approval
review snapshot:

    src/Netclaw.Actors.Tests/Tools/
      ShellApprovalDispositionMatrixTests.Shell_approval_cases_match_review_table.verified.md

It keys each row by (section, ID). A section is the text of the nearest
Markdown heading above the table.

Outcome direction rule (plan decision D1):

    Allowed          -> anything else   always fails (regression)
    Denied           -> anything else   fails unless an intended change has approvedBy
    RequiresApproval -> Allowed         fails unless an intended change names a negative control
    RequiresApproval -> Denied          fails unless an intended change has approvedBy
    case removed                        always fails
    case added                          passes (reported)

Intended-changes file (JSON):

    {
      "changes": [
        {
          "section": "Fresh Personal approval matrix",
          "id": "safe-verb-external-prompts",
          "from": "RequiresApproval",
          "to": "Allowed",
          "reason": "Why the change is safe.",
          "negativeControl": "safe-verb-external-path-prompts",
          "approvedBy": "owner name"
        }
      ]
    }

- "section", "id", "from", "to", and "reason" are required.
- "negativeControl" is required for RequiresApproval -> Allowed. It names a case
  ID in the same section. That case must exist in the baseline and in the
  candidate snapshot, and it must not be Allowed in either one.
- "approvedBy" is required for Denied -> other and RequiresApproval -> Denied.
- Each entry that is new since the baseline must match an actual transition.
  A new entry that does not match fails the check (stale entry).
- An entry that is also in the baseline version of the file is history. The
  check reports it and ignores it. It does not justify a transition.

Exit status: 0 when the check passes, 1 on a rule violation, 2 on bad input.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field

SNAPSHOT_PATH = (
    "src/Netclaw.Actors.Tests/Tools/"
    "ShellApprovalDispositionMatrixTests.Shell_approval_cases_match_review_table.verified.md"
)
INTENDED_CHANGES_PATH = "src/Netclaw.Actors.Tests/Tools/approval-outcome-intended-changes.json"
DEFAULT_BASE_REF = "origin/dev"

ALLOWED = "Allowed"
REQUIRES_APPROVAL = "RequiresApproval"
DENIED = "Denied"
OUTCOMES = (ALLOWED, REQUIRES_APPROVAL, DENIED)

# Only an unescaped pipe separates cells. The review table renderer writes a
# pipe inside a cell as "\|", and cell separators are " | ".
CELL_SEPARATOR = re.compile(r"(?<!\\)\|")
HEADING = re.compile(r"^#{1,6}\s+(?P<title>.+?)\s*#*\s*$")
DIVIDER_CELL = re.compile(r"^:?-{3,}:?$")


class InputError(Exception):
    """The inputs cannot be read or parsed."""


@dataclass(frozen=True)
class Row:
    section: str
    case_id: str
    result: str


@dataclass
class Transition:
    section: str
    case_id: str
    before: str | None
    after: str | None
    status: str = "FAIL"
    note: str = ""


@dataclass
class Report:
    transitions: list[Transition] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return not self.errors and all(t.status != "FAIL" for t in self.transitions)


def split_cells(line: str) -> list[str]:
    stripped = line.strip()
    if not stripped.startswith("|") or not stripped.endswith("|") or stripped.endswith("\\|"):
        raise InputError(f"table row does not start and end with '|': {line!r}")
    return [cell.strip() for cell in CELL_SEPARATOR.split(stripped)[1:-1]]


def parse_snapshot(text: str, label: str) -> dict[tuple[str, str], Row]:
    """Parse every Markdown table that has ID and Result columns."""
    text = text.lstrip("\ufeff")
    rows: dict[tuple[str, str], Row] = {}
    section = ""
    header: list[str] | None = None
    expect_divider = False
    for number, raw in enumerate(text.splitlines(), start=1):
        line = raw.rstrip("\r")
        heading = HEADING.match(line)
        if heading:
            section = heading.group("title")
            header = None
            continue
        if not line.strip().startswith("|"):
            header = None
            expect_divider = False
            continue
        try:
            cells = split_cells(line)
        except InputError as error:
            raise InputError(f"{label}:{number}: {error}") from error
        if header is None:
            duplicates = sorted({name for name in cells if cells.count(name) > 1})
            if duplicates:
                # A second Result or ID column could hide a regression.
                raise InputError(
                    f"{label}:{number}: table header repeats column(s) {duplicates}")
            header = cells
            expect_divider = True
            continue
        if expect_divider:
            if not all(DIVIDER_CELL.match(cell) for cell in cells):
                raise InputError(f"{label}:{number}: table header has no divider row")
            expect_divider = False
            if "ID" not in header or "Result" not in header:
                raise InputError(
                    f"{label}:{number}: table in section {section!r} has no ID or Result column")
            continue
        if len(cells) != len(header):
            raise InputError(
                f"{label}:{number}: row has {len(cells)} cells, header has {len(header)}")
        case_id = cells[header.index("ID")]
        result = cells[header.index("Result")]
        if result not in OUTCOMES:
            raise InputError(f"{label}:{number}: unknown Result {result!r} for {case_id!r}")
        key = (section, case_id)
        if key in rows:
            raise InputError(f"{label}:{number}: duplicate case {case_id!r} in section {section!r}")
        rows[key] = Row(section, case_id, result)
    if not rows:
        raise InputError(f"{label}: no approval case rows found")
    return rows


def parse_intended_changes(text: str | None, label: str) -> list[dict]:
    if text is None:
        return []
    try:
        document = json.loads(text)
    except json.JSONDecodeError as error:
        raise InputError(f"{label}: invalid JSON: {error}") from error
    if not isinstance(document, dict) or not isinstance(document.get("changes"), list):
        raise InputError(f"{label}: expected an object with a 'changes' array")
    allowed_keys = {"section", "id", "from", "to", "reason", "negativeControl", "approvedBy"}
    entries = []
    for index, entry in enumerate(document["changes"]):
        where = f"{label}: changes[{index}]"
        if not isinstance(entry, dict):
            raise InputError(f"{where}: entry is not an object")
        unknown = set(entry) - allowed_keys
        if unknown:
            raise InputError(f"{where}: unknown keys {sorted(unknown)}")
        for key in ("section", "id", "from", "to", "reason"):
            if not isinstance(entry.get(key), str) or not entry[key].strip():
                raise InputError(f"{where}: '{key}' must be a non-empty string")
        for key in ("from", "to"):
            if entry[key] not in OUTCOMES:
                raise InputError(f"{where}: '{key}' must be one of {', '.join(OUTCOMES)}")
        for key in ("negativeControl", "approvedBy"):
            if key in entry and (not isinstance(entry[key], str) or not entry[key].strip()):
                raise InputError(f"{where}: '{key}' must be a non-empty string when present")
        entries.append(entry)
    return entries


def entry_key(entry: dict) -> tuple[str, str, str, str]:
    return (entry["section"], entry["id"], entry["from"], entry["to"])


def check(
    baseline: dict[tuple[str, str], Row],
    candidate: dict[tuple[str, str], Row],
    intended: list[dict],
    baseline_intended: list[dict],
) -> Report:
    report = Report()
    carried = {entry_key(entry) for entry in baseline_intended}
    active: dict[tuple[str, str], dict] = {}
    for entry in intended:
        if entry_key(entry) in carried:
            report.notes.append(
                f"history: {entry['section']} / {entry['id']} "
                f"{entry['from']} -> {entry['to']} is in the baseline file; ignored")
            continue
        case_key = (entry["section"], entry["id"])
        if case_key in active:
            report.errors.append(f"intended change for {entry['id']!r} appears more than once")
            continue
        active[case_key] = entry

    used: set[tuple[str, str]] = set()
    for key in sorted(set(baseline) | set(candidate)):
        before = baseline[key].result if key in baseline else None
        after = candidate[key].result if key in candidate else None
        if before == after:
            continue
        transition = Transition(key[0], key[1], before, after)
        report.transitions.append(transition)
        entry = active.get(key)
        if entry is not None:
            used.add(key)
        evaluate(transition, entry, baseline, candidate)

    for key, entry in active.items():
        if key in used:
            continue
        report.errors.append(
            f"stale intended change: {entry['section']} / {entry['id']} "
            f"{entry['from']} -> {entry['to']} matches no transition")
    return report


def evaluate(transition: Transition, entry: dict | None, baseline: dict, candidate: dict) -> None:
    before, after = transition.before, transition.after
    if after is None:
        transition.note = "case removed; a case must not disappear"
        return
    if before is None:
        transition.status = "ok"
        transition.note = "case added"
        return
    if entry is not None and (entry["from"] != before or entry["to"] != after):
        transition.note = (
            f"intended change says {entry['from']} -> {entry['to']}, "
            f"actual is {before} -> {after}")
        return
    if before == ALLOWED:
        transition.note = "Allowed must stay Allowed"
        return
    if before == DENIED:
        if entry is None or "approvedBy" not in entry:
            transition.note = "Denied -> other needs an intended change with approvedBy"
            return
        transition.status = "ok"
        transition.note = f"approved by {entry['approvedBy']}"
        return
    if after == ALLOWED:
        if entry is None:
            transition.note = "RequiresApproval -> Allowed needs an intended change"
            return
        control_id = entry.get("negativeControl")
        if control_id is None:
            transition.note = "intended change has no negativeControl"
            return
        # The control must be an existing case that prompts or denies before
        # and after the change. A control that the same PR adds proves nothing.
        control_key = (transition.section, control_id)
        control_before = baseline.get(control_key)
        control_after = candidate.get(control_key)
        if control_before is None or control_after is None:
            transition.note = (
                f"negative control {control_id!r} must exist in the baseline and the candidate")
            return
        if control_id == transition.case_id or ALLOWED in (control_before.result, control_after.result):
            transition.note = (
                f"negative control {control_id!r} is Allowed in the baseline or the candidate; "
                "it must prompt or deny in both")
            return
        transition.status = "ok"
        transition.note = f"negative control {control_id} is {control_after.result}"
        return
    # RequiresApproval -> Denied
    if entry is None or "approvedBy" not in entry:
        transition.note = "RequiresApproval -> Denied needs an intended change with approvedBy"
        return
    transition.status = "ok"
    transition.note = f"approved by {entry['approvedBy']}"


def summarize(rows: dict[tuple[str, str], Row]) -> str:
    counts = {outcome: 0 for outcome in OUTCOMES}
    for row in rows.values():
        counts[row.result] += 1
    parts = ", ".join(f"{counts[outcome]} {outcome}" for outcome in OUTCOMES)
    return f"{len(rows)} rows ({parts})"


def render(report: Report, baseline_label: str, baseline: dict, candidate: dict) -> str:
    lines = [
        f"Baseline:  {baseline_label}: {summarize(baseline)}",
        f"Candidate: {summarize(candidate)}",
    ]
    if report.transitions:
        headers = ("Status", "Section", "ID", "From", "To", "Note")
        table = [
            (t.status, t.section, t.case_id, t.before or "(none)", t.after or "(none)", t.note)
            for t in report.transitions
        ]
        widths = [max(len(str(value)) for value in column) for column in zip(headers, *table)]
        lines.append("")
        lines.append("  ".join(h.ljust(w) for h, w in zip(headers, widths)).rstrip())
        lines.append("  ".join("-" * w for w in widths))
        for row in table:
            lines.append("  ".join(str(v).ljust(w) for v, w in zip(row, widths)).rstrip())
    else:
        lines.append("Transitions: 0")
    for note in report.notes:
        lines.append(f"note: {note}")
    for error in report.errors:
        lines.append(f"error: {error}")
    failed = sum(1 for t in report.transitions if t.status == "FAIL") + len(report.errors)
    lines.append("")
    lines.append("RESULT: PASS" if report.ok else f"RESULT: FAIL ({failed} violation(s))")
    return "\n".join(lines)


def git(repo: str, *args: str) -> str:
    completed = subprocess.run(
        ["git", "-C", repo, *args], capture_output=True, text=True, encoding="utf-8")
    if completed.returncode != 0:
        raise InputError(f"git {' '.join(args)} failed: {completed.stderr.strip()}")
    return completed.stdout


def git_show_optional(repo: str, rev: str, path: str) -> str | None:
    completed = subprocess.run(
        ["git", "-C", repo, "cat-file", "-e", f"{rev}:{path}"], capture_output=True)
    if completed.returncode != 0:
        return None
    return git(repo, "show", f"{rev}:{path}")


def read_file(path: str) -> str:
    try:
        with open(path, encoding="utf-8") as handle:
            return handle.read()
    except OSError as error:
        raise InputError(f"cannot read {path}: {error}") from error


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fail when a shell approval outcome moves in a direction that D1 forbids.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__)
    parser.add_argument("--repo", default=".", help="repository root (default: current directory)")
    parser.add_argument("--base-ref", default=DEFAULT_BASE_REF,
                        help="ref for the merge base (default: origin/dev)")
    parser.add_argument("--base-rev",
                        help="exact baseline revision; skips the merge-base step")
    parser.add_argument("--baseline", help="baseline snapshot file; skips git")
    parser.add_argument("--baseline-intended",
                        help="baseline intended-changes file (only with --baseline)")
    parser.add_argument("--candidate", help=f"candidate snapshot (default: {SNAPSHOT_PATH})")
    parser.add_argument("--intended",
                        help=f"intended-changes file (default: {INTENDED_CHANGES_PATH})")
    args = parser.parse_args(argv)

    repo = os.path.abspath(args.repo)
    try:
        if args.baseline:
            baseline_label = args.baseline
            baseline_text = read_file(args.baseline)
            baseline_intended_text = (
                read_file(args.baseline_intended) if args.baseline_intended else None)
        else:
            rev = args.base_rev or git(repo, "merge-base", "HEAD", args.base_ref).strip()
            baseline_label = f"{rev[:12]}:{SNAPSHOT_PATH.rsplit('/', 1)[-1]}"
            baseline_text = git_show_optional(repo, rev, SNAPSHOT_PATH)
            if baseline_text is None:
                raise InputError(f"baseline revision {rev} has no {SNAPSHOT_PATH}")
            baseline_intended_text = git_show_optional(repo, rev, INTENDED_CHANGES_PATH)

        candidate_path = args.candidate or os.path.join(repo, SNAPSHOT_PATH)
        intended_path = args.intended or os.path.join(repo, INTENDED_CHANGES_PATH)
        baseline = parse_snapshot(baseline_text, "baseline")
        candidate = parse_snapshot(read_file(candidate_path), candidate_path)
        intended = parse_intended_changes(read_file(intended_path), intended_path)
        baseline_intended = parse_intended_changes(baseline_intended_text, "baseline intended changes")
    except InputError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    report = check(baseline, candidate, intended, baseline_intended)
    print(render(report, baseline_label, baseline, candidate))
    return 0 if report.ok else 1


if __name__ == "__main__":
    sys.exit(main())
