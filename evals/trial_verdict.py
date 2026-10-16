#!/usr/bin/env python3
"""Pass, fail, or inconclusive: the verdict of one trial and of one case.

A trial folder (PR 1) holds the evidence. This module reads it and gives the
trial one of three states. It never raises on bad evidence: evidence that it
cannot read gives an inconclusive trial with an explicit reason.

Trial states, in this order (design 2.4):

  1. infra       No session log, two session logs, a new crash-*.log, a CLI
                 exit other than 0, 1, or 124, or evidence that cannot be
                 read. Reason: "infra:<detail>".
  2. timeout     A CLI exit of 124, or a turn with no TURN_COMPLETED record in
                 the CLI session log. Reason: "timeout".
  3. turn_failed A CLI exit of 1. The CLI writes TURN_COMPLETED for a failed
                 turn too. The trial says nothing about the product behavior
                 under test. Reason: "turn_failed", and "error" holds the
                 text of the ERROR record.
  4. report      The assertion wrote a report with the status "inconclusive"
                 (cycle cases). Reason: the reason of the report.
  5. assertion   An assertion exit other than 0 and 1 (141 from a pipe,
                 127 for a missing command). The check broke, so it gave no
                 verdict. Reason: "assertion_exit_<code>".
  6. pass        The assertion exit is 0.  fail: the assertion exit is 1.

headless-errors.log is not evidence here. A normal cut (CANCELLED record)
writes to it, so a rule on that file would turn every timeout into infra. The
infra signal for a crash is the crash-*.log file that PR 1 lists in
session.json.

Case states: the bounds rule. Count each inconclusive trial once as a pass
and once as a fail. The worst case at or above the threshold is a pass. The
best case below the threshold is a fail. Anything else is inconclusive. Both
bounds divide by all trials. The score divides by conclusive trials only.

Commands (the result is JSON on stdout):

  trial-status --trial-dir DIR    Also writes DIR/verdict.json. Exit 0 for
                                  every state.
  case-status --threshold T --passes P --fails F --inconclusive I
                                  Exit 2 with a message on stderr for a bad
                                  threshold or a bad count.
"""

import argparse
from fractions import Fraction
import json
from pathlib import Path
import re
import sys

PASS, FAIL, INCONCLUSIVE = "pass", "fail", "inconclusive"

CLI_EXIT_OK, CLI_EXIT_FAILED_TURN, CLI_EXIT_TIMEOUT = 0, 1, 124
ASSERT_EXIT_PASS, ASSERT_EXIT_FAIL = 0, 1

# A record starts a line with a bracketed timestamp. A continuation line of a
# multi-line record does not. The same words inside a model reply or a tool
# result must not count, so every pattern anchors at the line start.
RECORD_START = re.compile(r"^\[[^\]\n]+\] ", re.MULTILINE)
SESSION_HEADER = re.compile(r"^\[[^\]\n]+\] Headless session started: ", re.MULTILINE)
TURN_COMPLETED = re.compile(r"^\[[^\]\n]+\] TURN_COMPLETED: turn=\d+", re.MULTILINE)
ERROR_RECORD = re.compile(r"^\[[^\]\n]+\] ERROR: ", re.MULTILINE)


class EvidenceError(Exception):
    """The evidence of a trial is missing or malformed."""


def verdict(status, reason=None, error=None):
    return {"status": status, "reason": reason, "error": error}


def infra(detail):
    return verdict(INCONCLUSIVE, f"infra:{detail}")


def read_text(path):
    try:
        return path.read_text(errors="replace")
    except OSError as error:
        raise EvidenceError(f"{path.name} cannot be read: {error.strerror or error}") from error


def read_json(path):
    try:
        document = json.loads(read_text(path))
    except ValueError as error:
        raise EvidenceError(f"{path.name} is not JSON: {error}") from error
    if not isinstance(document, dict):
        raise EvidenceError(f"{path.name} is not a JSON object")
    return document


def read_exit_code(path):
    code = read_json(path).get("exit_code")
    # bool is an int in Python. "true" is not an exit code.
    if not isinstance(code, int) or isinstance(code, bool):
        raise EvidenceError(f"{path.name} has no integer exit_code")
    return code


def cli_exit_codes(raw):
    """One exit code for each CLI call: turn-NN/cli-exit.json, or cli-exit.json."""
    turn_files = sorted(raw.glob("turn-[0-9][0-9]/cli-exit.json"))
    if turn_files:
        return [read_exit_code(path) for path in turn_files]
    return [read_exit_code(raw / "cli-exit.json")]


def log_blocks(log):
    """Split a CLI session log into one block for each CLI call."""
    starts = [match.start() for match in SESSION_HEADER.finditer(log)]
    return [log[begin:end] for begin, end in zip(starts, starts[1:] + [len(log)])]


def last_error_text(log):
    """The text of the last ERROR record, with its continuation lines, or None."""
    found = None
    for match in ERROR_RECORD.finditer(log):
        following = RECORD_START.search(log, match.end())
        end = following.start() if following else len(log)
        found = log[match.end():end].rstrip("\r\n")
    return found


def failed_turn_error(log, exit_codes):
    """The error of the first failed turn. A tool error of another turn is not it."""
    blocks = log_blocks(log)
    if len(blocks) == len(exit_codes):
        failed = exit_codes.index(CLI_EXIT_FAILED_TURN)
        return last_error_text(blocks[failed])
    return last_error_text(log)


def judge_assertion(raw):
    """Rules 4 to 6. The turn finished, so the assertion result is needed."""
    code = read_exit_code(raw / "assert.json")

    report_path = raw / "assert-report.json"
    if report_path.exists():
        report = read_json(report_path)
        if report.get("status") == INCONCLUSIVE:
            return verdict(INCONCLUSIVE, str(report.get("reason") or "assert_report_inconclusive"))

    if code == ASSERT_EXIT_PASS:
        return verdict(PASS)
    if code == ASSERT_EXIT_FAIL:
        return verdict(FAIL)
    return verdict(INCONCLUSIVE, f"assertion_exit_{code}")


def judge_trial(raw):
    session = read_json(raw / "session.json")
    session_status = session.get("status")
    if session_status != "ok":
        return infra(session_status if isinstance(session_status, str) and session_status else "session_status_unreadable")
    other_new_files = session.get("other_new_files", [])
    if not isinstance(other_new_files, list):
        raise EvidenceError("session.json other_new_files is not a list")
    if any(isinstance(name, str) and re.fullmatch(r"crash-.*\.log", name) for name in other_new_files):
        return infra("crash_log")

    exit_codes = cli_exit_codes(raw)
    for code in exit_codes:
        if code not in (CLI_EXIT_OK, CLI_EXIT_FAILED_TURN, CLI_EXIT_TIMEOUT):
            return infra(f"cli_exit_{code}")

    log = read_text(raw / "cli-session.log")
    finished_turns = len(TURN_COMPLETED.findall(log))
    if CLI_EXIT_TIMEOUT in exit_codes or finished_turns < len(exit_codes):
        return verdict(INCONCLUSIVE, "timeout")
    if CLI_EXIT_FAILED_TURN in exit_codes:
        return verdict(INCONCLUSIVE, "turn_failed", failed_turn_error(log, exit_codes))

    return judge_assertion(raw)


def trial_status(trial_dir):
    """Judge the trial folder. The result is verdict(...). It does not raise on bad evidence."""
    try:
        return judge_trial(Path(trial_dir) / "raw")
    except EvidenceError as error:
        return infra(f"evidence_unreadable: {error}")


def case_status(passes, fails, inconclusive, threshold):
    """Give a case its state from the counts of its trials.

    The threshold is compared as an exact decimal. The old harness rounded the
    score to two places first, so 0.795 passed a threshold of 0.80.
    """
    for name, count in (("passes", passes), ("fails", fails), ("inconclusive", inconclusive)):
        if not isinstance(count, int) or isinstance(count, bool) or count < 0:
            raise ValueError(f"{name} must be a whole number of 0 or more, got: {count!r}")
    try:
        limit = Fraction(str(threshold).strip())
    except ValueError as error:
        raise ValueError(f"threshold must be a number from 0 to 1, got: {threshold!r}") from error
    if not 0 <= limit <= 1:
        raise ValueError(f"threshold must be a number from 0 to 1, got: {threshold!r}")
    total = passes + fails + inconclusive
    if total == 0:
        raise ValueError("a case needs at least one trial")

    if Fraction(passes, total) >= limit:
        status = PASS
    elif Fraction(passes + inconclusive, total) < limit:
        status = FAIL
    else:
        status = INCONCLUSIVE
    conclusive = passes + fails
    return {"status": status, "score": passes / conclusive if conclusive else None,
            "passes": passes, "fails": fails, "inconclusive": inconclusive, "conclusive": conclusive}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)

    trial = commands.add_parser("trial-status", help="judge a trial folder and write verdict.json")
    trial.add_argument("--trial-dir", type=Path, required=True)

    case = commands.add_parser("case-status", help="judge a case from the counts of its trials")
    case.add_argument("--threshold", required=True)
    case.add_argument("--passes", type=int, required=True)
    case.add_argument("--fails", type=int, required=True)
    case.add_argument("--inconclusive", type=int, required=True)

    args = parser.parse_args(argv)

    if args.command == "trial-status":
        if not args.trial_dir.is_dir():
            print(f"ERROR: trial folder not found: {args.trial_dir}", file=sys.stderr)
            return 2
        result = trial_status(args.trial_dir)
        (args.trial_dir / "verdict.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result))
        return 0

    try:
        result = case_status(args.passes, args.fails, args.inconclusive, args.threshold)
    except ValueError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
