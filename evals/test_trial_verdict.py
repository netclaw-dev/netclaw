"""Specs for PR 2 of the eval harness rework: pass, fail, and inconclusive results.

These tests fix the interface of `evals/trial_verdict.py`. The module reads the
trial folder that PR 1 writes and gives each trial one of three states. It
gives each case one of the same three states, by the bounds rule in the design
(section 2.4, decision 7).

Trial states: `trial_status(trial_dir)`

    trial_dir  evals/runs/<run>/trials/<case>/<nn>   (it holds raw/)
    returns    {"status": "pass" | "fail" | "inconclusive",
                "reason": None for pass and fail, a token for inconclusive,
                "error": the text of the CLI ERROR record of a failed turn, or None}

    Inputs, all from PR 1:
      raw/cli-exit.json            one CLI call (a single-turn trial)
      raw/turn-NN/cli-exit.json    one CLI call for each turn (a multi-turn trial)
      raw/session.json             discover-session output, with other_new_files
      raw/cli-session.log          the CLI session log, when session.json is ok
      raw/assert.json              the assertion exit code (exit_code)

    The rules, in this order (design 2.4):
      1. infra       no session, ambiguous session, a CLI exit other than 0, 1,
                     or 124, a new crash-*.log in the trial, or evidence that
                     cannot be read. Reason: "infra:<detail>". Details that the
                     tests fix: no_session, ambiguous_session, cli_exit_<code>,
                     crash_log. Reason for evidence that cannot be read: any
                     text that starts with "infra".
      2. timeout     the CLI exit is 124, or a turn has no TURN_COMPLETED record
                     in the CLI log. Reason: "timeout".
      3. turn_failed the CLI exit is 1. Reason: "turn_failed". The error text is
                     kept in "error".
      4. assertion   the assertion exit is not 0 and not 1 (SIGPIPE 141, command
                     not found 127, a grep error 2). The check itself broke, so
                     it gave no verdict. Reason: "assertion_exit_<code>".
      5. pass        the assertion exit is 0. fail: the assertion exit is 1.

    A TURN_COMPLETED record starts a line: `[<time>] TURN_COMPLETED: turn=<n>`.
    The same words inside another record do not count.

Case states: `case_status(passes, fails, inconclusive, threshold)`

    returns {"status": "pass" | "fail" | "inconclusive",
             "score": passes / (passes + fails), or None when no trial is conclusive,
             "passes", "fails", "inconclusive", "conclusive"}
    Count each inconclusive trial once as a pass and once as a fail.
    Worst case >= threshold is pass. Best case < threshold is fail. Otherwise
    inconclusive. The denominator of both is the number of trials.

Command line (the same results as JSON on stdout, exit 0 for every status):

    trial_verdict.py trial-status --trial-dir DIR      also writes DIR/verdict.json
    trial_verdict.py case-status --threshold T --passes P --fails F --inconclusive I

The tests use no model, no network, no daemon, and no sleep. They delete their
temp folders.
"""

import importlib
import json
import re
from fractions import Fraction
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

EVALS = Path(__file__).resolve().parent
if str(EVALS) not in sys.path:
    sys.path.insert(0, str(EVALS))

D4 = EVALS / "fixtures" / "d4"


def record(text, second=0):
    return f"[2026-10-15T10:00:{second:02d}.0000000+00:00] {text}\n"


def cli_log(session_id="signalr/t1", turns=1, completed_turns=None, error=None, extra=()):
    """The CLI session log of a trial. One block for each CLI call, like `chat -p --resume`."""
    completed_turns = turns if completed_turns is None else completed_turns
    text = ""
    for turn in range(1, turns + 1):
        text += record(f"Headless session started: {session_id}")
        text += record(f"PROMPT: question {turn}", 1)
        text += record("SESSION_JOINED turn_count=0 title=(none)", 2)
        for line in extra:
            text += record(line, 3)
        if error is not None and turn == turns:
            text += record(f"ERROR: {error}", 4)
        if turn <= completed_turns:
            text += record("ASSISTANT_FINAL: an answer", 5)
            text += record("USAGE: in=1 out=1 total=2 cached=0 reasoning= context_window=1000 "
                           "prompt_ms=1 predicted_tok_s=", 6)
            text += record(f"TURN_COMPLETED: turn={turn}", 7)
            text += record("SESSION_ENDED", 8)
    return text


def write_trial(root, *, exit_codes=(0,), log=None, session="ok", assert_rc=0,
                other_new_files=(), exit_json=None):
    """Write the PR 1 trial folder. `assert_rc=None` writes no assert.json.

    `exit_codes` has one entry for a single-turn trial and more for a
    multi-turn trial. `log=None` writes a log that finished every turn.
    """
    raw = Path(root) / "raw"
    raw.mkdir(parents=True)
    log_text = log if log is not None else cli_log(turns=len(exit_codes))
    session_id = re.match(r"\[[^\]]+\] Headless session started: (\S+)", log_text).group(1)
    multi = len(exit_codes) > 1
    for index, code in enumerate(exit_codes, start=1):
        folder = raw / f"turn-{index:02d}" if multi else raw
        folder.mkdir(exist_ok=True)
        content = exit_json if exit_json is not None else json.dumps(
            {"exit_code": code, "started_at": "2026-10-15T10:00:00.000+00:00",
             "ended_at": "2026-10-15T10:00:01.000+00:00", "deadline_s": 300})
        if content != "MISSING":
            (folder / "cli-exit.json").write_text(content)
    if session == "ok":
        session_json = {"status": "ok", "session_id": session_id,
                        "log": session_id.replace("/", "-") + ".log",
                        "other_new_files": list(other_new_files)}
    elif session == "no_session":
        session_json = {"status": "no_session", "session_id": None, "log": None,
                        "other_new_files": list(other_new_files)}
    else:
        session_json = {"status": "ambiguous_session", "session_id": None, "log": None,
                        "candidates": ["signalr-a.log", "signalr-b.log"],
                        "other_new_files": list(other_new_files)}
    (raw / "session.json").write_text(session_json if isinstance(session_json, str) else json.dumps(session_json))
    if session == "ok":
        (raw / "cli-session.log").write_text(log_text)
    if assert_rc is not None:
        (raw / "assert.json").write_text(json.dumps(
            {"exit_code": assert_rc, "daemon_log": "daemon-2026-10-15.log",
             "window_lines_before": 1, "window_lines_after": 2, "extra_daemon_logs": []}))
    return Path(root)


def two_turn_log(error_of_turn_1, error_of_turn_2, ended=(True, True)):
    """A CLI log of two turns. `ended[i]` False leaves out the TURN_COMPLETED of turn i + 1."""
    text = ""
    for turn, error in ((1, error_of_turn_1), (2, error_of_turn_2)):
        text += record("Headless session started: signalr/t1")
        text += record(f"PROMPT: question {turn}", 1)
        text += record("SESSION_JOINED turn_count=0 title=(none)", 2)
        if error is not None:
            text += record(f"ERROR: {error}", 4)
        if ended[turn - 1]:
            text += record("ASSISTANT_FINAL: an answer", 5)
            text += record(f"TURN_COMPLETED: turn={turn}", 7)
            text += record("SESSION_ENDED", 8)
    return text


class VerdictCase(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.counter = 0

    def module(self):
        if not (EVALS / "trial_verdict.py").is_file():
            self.fail("evals/trial_verdict.py does not exist")
        return importlib.import_module("trial_verdict")

    def trial(self, **kwargs):
        self.counter += 1
        return write_trial(self.root / f"trial-{self.counter}", **kwargs)

    def status(self, **kwargs):
        return self.module().trial_status(self.trial(**kwargs))

    def assert_inconclusive(self, result, reason):
        self.assertEqual("inconclusive", result["status"], result)
        self.assertEqual(reason, result["reason"], result)

    def assert_infra(self, result, detail):
        self.assertEqual("inconclusive", result["status"], result)
        self.assertTrue(str(result["reason"]).startswith("infra"), result)
        self.assertIn(detail, str(result["reason"]), result)


class FinishedTurnTests(VerdictCase):
    def test_finished_turn_with_a_passing_assertion_is_a_pass(self):
        # Planted bug: a pass needs more than the assertion (for example the
        # reason is set), or a finished turn is never a pass.
        result = self.status(exit_codes=(0,), assert_rc=0)

        self.assertEqual({"status": "pass", "reason": None, "error": None}, result)

    def test_finished_turn_with_a_failing_assertion_is_a_fail(self):
        # Planted bug: map an assertion fail to inconclusive, or drop the
        # difference between exit 0 and exit 1.
        result = self.status(exit_codes=(0,), assert_rc=1)

        self.assertEqual({"status": "fail", "reason": None, "error": None}, result)

    def test_error_record_in_a_finished_turn_does_not_hide_the_assertion_result(self):
        # Planted bug: any ERROR record in the CLI log makes the trial
        # inconclusive. A tool error is normal in a turn that finished with exit 0.
        for assert_rc, expected in ((0, "pass"), (1, "fail")):
            with self.subTest(assert_rc=assert_rc):
                result = self.status(exit_codes=(0,), assert_rc=assert_rc,
                                     log=cli_log(error="tool denied"))
                self.assertEqual(expected, result["status"], result)

    def test_real_d4_log_of_a_finished_turn_counts_as_finished(self):
        # D4 row 1. This is the CLI log format of the product, not a made-up one.
        # Planted bug: look for a record name that the product does not write.
        result = self.status(exit_codes=(0,), assert_rc=0,
                             log=(D4 / "cli-session-row-001-completed.txt").read_text())

        self.assertEqual("pass", result["status"], result)


class TimeoutTests(VerdictCase):
    def test_cut_trial_is_inconclusive_even_when_the_assertion_passed(self):
        # Planted bug: the assertion result wins over exit 124. This is the D4
        # false pass: 16 cut trials scored as passes.
        result = self.status(exit_codes=(124,), assert_rc=0,
                             log=cli_log(completed_turns=0))

        self.assert_inconclusive(result, "timeout")

    def test_cut_trial_is_inconclusive_even_when_the_assertion_failed(self):
        # Planted bug: a cut trial with a failing assertion is a fail. D4 had 21
        # such rows. The late model answer was never read.
        result = self.status(exit_codes=(124,), assert_rc=1,
                             log=cli_log(completed_turns=0))

        self.assert_inconclusive(result, "timeout")

    def test_cut_trial_needs_no_assertion_result(self):
        # Planted bug: a missing assert.json is an error even for a cut trial.
        # The harness may skip the assertion when the turn did not finish.
        result = self.status(exit_codes=(124,), assert_rc=None,
                             log=cli_log(completed_turns=0))

        self.assert_inconclusive(result, "timeout")

    def test_exit_124_outranks_a_log_that_looks_finished(self):
        # Planted bug: trust the log over the exit code. `timeout` cut the CLI,
        # so the trial ran past its deadline, whatever the log says.
        result = self.status(exit_codes=(124,), assert_rc=0, log=cli_log())

        self.assert_inconclusive(result, "timeout")

    def test_log_without_turn_completed_is_inconclusive_although_the_exit_was_0(self):
        # Planted bug: use the exit code only.
        result = self.status(exit_codes=(0,), assert_rc=0, log=cli_log(completed_turns=0))

        self.assert_inconclusive(result, "timeout")

    def test_turn_completed_must_start_a_record(self):
        # Planted bug: search the whole log for the words TURN_COMPLETED. A
        # model reply, a tool result, or a continuation line can hold them.
        log = cli_log(completed_turns=0, extra=[
            "ASSISTANT_DELTA: the log ends with [2026-10-15T10:00:07.0000000+00:00] TURN_COMPLETED: turn=1",
            "TOOL_RESULT: shell_execute call_id=c1 result=first line\nTURN_COMPLETED: turn=1\nlast line",
        ])

        result = self.status(exit_codes=(0,), assert_rc=0, log=log)

        self.assert_inconclusive(result, "timeout")

    def test_real_d4_log_of_a_cut_trial_is_inconclusive(self):
        # D4 row 75: the harness scored this trial a pass, from a skill load of
        # the trial before it. The CLI log has three records and no turn end.
        # Planted bug: a pass wins over a cut.
        log = (D4 / "cli-session-row-075-cut.txt").read_text()
        self.assertNotIn("TURN_COMPLETED", log)

        result = self.status(exit_codes=(124,), assert_rc=0, log=log)

        self.assert_inconclusive(result, "timeout")


class CliFailureWithoutTurnEndTests(VerdictCase):
    """CLI exit 1 has two causes. A failed turn still writes TURN_COMPLETED
    (TurnOutcome.Failed). A channel exception after the session log exists
    (for example a failed send after a dropped daemon connection) writes no
    TURN_COMPLETED. The second cause is not a cut by the deadline."""

    def test_exit_1_without_a_turn_end_is_a_cli_failure_not_a_timeout(self):
        # Planted bug: `finished_turns < len(exit_codes)` wins for exit 1, so a
        # broken hub send shows as `timeout`. The operator then blames the load
        # of the endpoint, and the PR 3 breaker halts the run for a product fault.
        for assert_rc in (0, 1):
            with self.subTest(assert_rc=assert_rc):
                result = self.status(exit_codes=(1,), assert_rc=assert_rc,
                                     log=cli_log(completed_turns=0))

                self.assert_inconclusive(result, "infra:cli_failed")

    def test_exit_1_without_a_turn_end_in_any_turn_of_a_multi_turn_trial(self):
        # Planted bug: the check reads only the first turn, or only the last one.
        for codes, ended in (((1, 0), (False, True)), ((0, 1), (True, False))):
            with self.subTest(exit_codes=codes):
                result = self.status(exit_codes=codes, assert_rc=0,
                                     log=two_turn_log(None, None, ended))

                self.assert_inconclusive(result, "infra:cli_failed")

    def test_exit_124_without_a_turn_end_stays_a_timeout(self):
        # Guard. Planted bug: the new rule takes over every cut trial.
        result = self.status(exit_codes=(124,), assert_rc=1, log=cli_log(completed_turns=0))

        self.assert_inconclusive(result, "timeout")

    def test_exit_0_without_a_turn_end_stays_a_timeout(self):
        # Guard. The deadline rule for a missing turn end stays for exit 0.
        result = self.status(exit_codes=(0,), assert_rc=0, log=cli_log(completed_turns=0))

        self.assert_inconclusive(result, "timeout")

    def test_exit_124_in_one_turn_outranks_exit_1_in_another(self):
        # Planted bug: the cli_failed rule runs before the exit 124 rule. A cut
        # turn is a timeout, whatever the other turn did.
        result = self.status(exit_codes=(124, 1), assert_rc=0, log=cli_log(turns=2, completed_turns=0))

        self.assert_inconclusive(result, "timeout")


class FailedTurnTests(VerdictCase):
    ERROR = 'Provider returned 500: it\'s "down"); DROP TABLE eval_results; --\nsecond line of the error'

    def test_exit_1_after_a_finished_turn_is_turn_failed_and_keeps_the_error(self):
        # Planted bug: store it as a fail, or let the assertion decide, or drop
        # the error text. The CLI exits 1 on TurnOutcome.Failed and still writes
        # TURN_COMPLETED. A failed turn says nothing about the product behavior
        # under test (decision 6).
        for assert_rc in (0, 1):
            with self.subTest(assert_rc=assert_rc):
                result = self.status(exit_codes=(1,), assert_rc=assert_rc, log=cli_log(error=self.ERROR))

                self.assert_inconclusive(result, "turn_failed")
                self.assertEqual(self.ERROR, result["error"])

    def test_exit_1_without_an_error_record_is_turn_failed_with_no_error_text(self):
        # Planted bug: invent an error text, or treat the missing text as a
        # reason to call the trial a fail.
        result = self.status(exit_codes=(1,), assert_rc=0, log=cli_log())

        self.assert_inconclusive(result, "turn_failed")
        self.assertIsNone(result["error"])

    def test_pass_and_fail_carry_no_error_text(self):
        # Planted bug: leak the ERROR text of a finished turn into the verdict.
        for assert_rc in (0, 1):
            with self.subTest(assert_rc=assert_rc):
                result = self.status(exit_codes=(0,), assert_rc=assert_rc, log=cli_log(error="tool denied"))
                self.assertIsNone(result["error"])


class FailedTurnErrorOfTheRightTurnTests(VerdictCase):
    def test_error_text_is_the_error_of_the_failed_turn_not_of_a_later_turn(self):
        # P4. Planted bug: take the last ERROR record of the whole log. Turn 1
        # fails with a provider error (exit 1). Turn 2 finishes (exit 0) and
        # logs a normal tool error. The stored error must be the provider error.
        log = two_turn_log("provider unavailable", "tool denied: file_read")

        result = self.status(exit_codes=(1, 0), assert_rc=0, log=log)

        self.assert_inconclusive(result, "turn_failed")
        self.assertEqual("provider unavailable", result["error"])

    def test_error_text_is_the_error_of_the_failed_turn_not_of_an_earlier_turn(self):
        # P4, mirror. Planted bug: take the first ERROR record of the whole log.
        log = two_turn_log("tool denied: file_read", "provider unavailable")

        result = self.status(exit_codes=(0, 1), assert_rc=0, log=log)

        self.assert_inconclusive(result, "turn_failed")
        self.assertEqual("provider unavailable", result["error"])

    def test_failed_turn_without_an_error_record_does_not_borrow_the_error_of_another_turn(self):
        # P4. Planted bug: fall back to any ERROR record in the log.
        log = two_turn_log(None, "tool denied: file_read")

        result = self.status(exit_codes=(1, 0), assert_rc=0, log=log)

        self.assert_inconclusive(result, "turn_failed")
        self.assertIsNone(result["error"])


class InfraTests(VerdictCase):
    def test_cli_crash_134_is_inconclusive_infra(self):
        # Planted bug: any non-zero CLI exit other than 124 is a fail, or the
        # assertion decides. 134 is an aborted CLI (D4: disk full).
        result = self.status(exit_codes=(134,), assert_rc=0, log=cli_log(completed_turns=0))

        self.assert_infra(result, "cli_exit_134")

    def test_every_cli_exit_other_than_0_1_and_124_is_infra(self):
        # Planted bug: treat only 134 as a crash.
        for code in (2, 126, 127, 137, 139):
            with self.subTest(exit_code=code):
                result = self.status(exit_codes=(code,), assert_rc=0, log=cli_log(completed_turns=0))
                self.assert_infra(result, f"cli_exit_{code}")

    def test_no_session_log_is_infra(self):
        # Planted bug: no session log means nothing to check, so the trial
        # passes or fails by the assertion alone.
        for assert_rc in (0, 1):
            with self.subTest(assert_rc=assert_rc):
                result = self.status(exit_codes=(0,), session="no_session", assert_rc=assert_rc)
                self.assert_infra(result, "no_session")

    def test_two_new_session_logs_are_infra(self):
        # Planted bug: take the first candidate.
        result = self.status(exit_codes=(0,), session="ambiguous_session", assert_rc=0)

        self.assert_infra(result, "ambiguous_session")

    def test_new_crash_log_in_the_trial_is_infra_even_when_the_turn_finished(self):
        # Planted bug: ignore crash logs. A crash in the window means the
        # evidence of this trial is not trusted.
        result = self.status(exit_codes=(0,), assert_rc=0,
                             other_new_files=["crash-20261015-131615.log"])

        self.assert_infra(result, "crash_log")

    def test_other_new_log_files_are_not_infra(self):
        # Planted bug: any other_new_files entry makes the trial infra. A new
        # daemon file after midnight UTC is normal and PR 1 handles it.
        result = self.status(exit_codes=(0,), assert_rc=0, other_new_files=["daemon-2026-10-16.log"])

        self.assertEqual("pass", result["status"], result)

    def test_infra_outranks_timeout(self):
        # D4 row 59: the CLI was cut at 300 s and then crashed on a full disk
        # (crash-20261015-131615.log). The design lists infra first. The reason
        # must name the crash, not the cut. Planted bug: check the timeout first.
        self.assertIn("No space left on device", (D4 / "crash-20261015-131615.txt").read_text())

        result = self.status(exit_codes=(124,), assert_rc=0, log=cli_log(completed_turns=0),
                             other_new_files=["crash-20261015-131615.log"])

        self.assert_infra(result, "crash_log")

    def test_no_session_outranks_timeout(self):
        # Planted bug: a cut CLI with no session log is reported as a timeout.
        result = self.status(exit_codes=(124,), session="no_session", assert_rc=None)

        self.assert_infra(result, "no_session")

    def test_evidence_that_cannot_be_read_is_infra(self):
        # Planted bug: a missing or broken file is read as exit 0, a pass, a
        # fail, or raises. Design 2.4 rule 1: "an evidence parse error".
        broken = (
            ("cli-exit.json is missing", dict(exit_json="MISSING")),
            ("cli-exit.json is not JSON", dict(exit_json="{not json")),
            ("exit_code is text", dict(exit_json='{"exit_code": "0"}')),
            ("exit_code is missing", dict(exit_json="{}")),
        )
        for name, kwargs in broken:
            with self.subTest(name):
                result = self.status(assert_rc=0, **kwargs)
                self.assertEqual("inconclusive", result["status"], result)
                self.assertTrue(str(result["reason"]).startswith("infra"), result)

        with self.subTest("a finished turn has no assert.json"):
            result = self.status(exit_codes=(0,), assert_rc=None)
            self.assertEqual("inconclusive", result["status"], result)
            self.assertTrue(str(result["reason"]).startswith("infra"), result)

        with self.subTest("assert.json is not JSON"):
            trial = self.trial(exit_codes=(0,), assert_rc=0)
            (trial / "raw" / "assert.json").write_text("[[[")
            result = self.module().trial_status(trial)
            self.assertEqual("inconclusive", result["status"], result)
            self.assertTrue(str(result["reason"]).startswith("infra"), result)

        with self.subTest("session.json is not JSON"):
            trial = self.trial(exit_codes=(0,), assert_rc=0)
            (trial / "raw" / "session.json").write_text("")
            result = self.module().trial_status(trial)
            self.assertEqual("inconclusive", result["status"], result)
            self.assertTrue(str(result["reason"]).startswith("infra"), result)


class AssertionExitTests(VerdictCase):
    def test_assertion_pipe_failure_141_is_inconclusive_not_a_fail(self):
        # Planted bug: every non-zero assertion exit is a fail. Under
        # `set -o pipefail`, `producer | grep -q` returns 141 when grep exits
        # early (design 1.4). The check broke, so the trial has no verdict.
        result = self.status(exit_codes=(0,), assert_rc=141)

        self.assert_inconclusive(result, "assertion_exit_141")

    def test_other_assertion_exit_codes_are_inconclusive_too(self):
        # Planted bug: treat only 141 as a broken check. 127 is a missing
        # command, 2 is a grep or shell error.
        for code in (2, 126, 127):
            with self.subTest(assert_rc=code):
                result = self.status(exit_codes=(0,), assert_rc=code)
                self.assert_inconclusive(result, f"assertion_exit_{code}")

    def test_timeout_outranks_a_broken_assertion(self):
        # Planted bug: report the assertion before the cut.
        result = self.status(exit_codes=(124,), assert_rc=141, log=cli_log(completed_turns=0))

        self.assert_inconclusive(result, "timeout")


class MultiTurnTests(VerdictCase):
    def test_a_cut_turn_makes_the_trial_inconclusive_in_any_position(self):
        # Planted bug: look only at the last turn, or only at the first. Each
        # position is tried twice: once with a log that has no turn end for
        # the cut turn, and once with a log that looks finished, so that the
        # exit code is the only signal.
        for codes, completed in (((0, 124), 1), ((124, 0), 0), ((0, 0, 124), 2)):
            for log_shows_cut in (True, False):
                with self.subTest(exit_codes=codes, log_shows_cut=log_shows_cut):
                    log = cli_log(turns=len(codes), completed_turns=completed if log_shows_cut else len(codes))
                    result = self.status(exit_codes=codes, assert_rc=0, log=log)
                    self.assert_inconclusive(result, "timeout")

    def test_every_turn_needs_its_own_turn_completed(self):
        # Planted bug: one TURN_COMPLETED anywhere in the log is enough. Both
        # CLI calls exit 0 here, but the second turn has no turn end.
        result = self.status(exit_codes=(0, 0), assert_rc=0, log=cli_log(turns=2, completed_turns=1))

        self.assert_inconclusive(result, "timeout")

    def test_all_turns_finished_takes_the_assertion_result(self):
        # Planted bug: a multi-turn trial is never conclusive.
        for assert_rc, expected in ((0, "pass"), (1, "fail")):
            with self.subTest(assert_rc=assert_rc):
                result = self.status(exit_codes=(0, 0), assert_rc=assert_rc, log=cli_log(turns=2))
                self.assertEqual(expected, result["status"], result)

    def test_a_failed_turn_in_a_multi_turn_trial_is_turn_failed(self):
        # Planted bug: only the last turn can fail.
        result = self.status(exit_codes=(1, 0), assert_rc=0, log=cli_log(turns=2))

        self.assert_inconclusive(result, "turn_failed")

    def test_a_crash_in_a_later_turn_is_infra(self):
        # Planted bug: read only turn-01/cli-exit.json.
        result = self.status(exit_codes=(0, 134), assert_rc=0, log=cli_log(turns=2, completed_turns=1))

        self.assert_infra(result, "cli_exit_134")


class CaseStatusTests(VerdictCase):
    def case(self, passes, fails, inconclusive, threshold=0.8):
        return self.module().case_status(passes, fails, inconclusive, threshold)

    def test_design_examples_at_threshold_0_8(self):
        # Design 2.4. Planted bug, by row:
        #   4P 1I  count inconclusive as a fail (the old rule gives FAIL), or use `>` for the pass bound
        #   3P 2I  count inconclusive as a pass (gives PASS)
        #   3P 1I 1F  count inconclusive as a pass (gives PASS), or use `<=` for the fail bound
        #   2F 3P  ignore the fail bound
        #   1P 4I  score over all trials (1/5), or call a lone pass a PASS
        #   0P 5I  divide by zero, or return a score
        table = (
            ((4, 0, 1), "pass", 4 / 4),
            ((3, 0, 2), "inconclusive", 3 / 3),
            ((3, 1, 1), "inconclusive", 3 / 4),
            ((3, 2, 0), "fail", 3 / 5),
            ((1, 0, 4), "inconclusive", 1 / 1),
            ((0, 0, 5), "inconclusive", None),
            ((5, 0, 0), "pass", 1.0),
            ((0, 5, 0), "fail", 0.0),
            ((2, 3, 0), "fail", 2 / 5),
            ((1, 2, 2), "fail", 1 / 3),
        )
        for (passes, fails, inconclusive), status, score in table:
            with self.subTest(trials=f"{passes}P {fails}F {inconclusive}I"):
                result = self.case(passes, fails, inconclusive)
                self.assertEqual(status, result["status"])
                if score is None:
                    self.assertIsNone(result["score"])
                else:
                    self.assertAlmostEqual(score, result["score"], places=9)
                self.assertEqual(passes + fails, result["conclusive"])
                self.assertEqual(inconclusive, result["inconclusive"])
                self.assertEqual(passes, result["passes"])
                self.assertEqual(fails, result["fails"])

    @staticmethod
    def combinations(max_trials=6):
        for total in range(1, max_trials + 1):
            for passes in range(total + 1):
                for fails in range(total - passes + 1):
                    yield passes, fails, total - passes - fails

    THRESHOLDS = ("0.5", "0.6", "0.75", "0.8", "1")

    @staticmethod
    def bounds_model(passes, fails, inconclusive, threshold):
        """A separate model: exact fractions, no floats."""
        total = passes + fails + inconclusive
        limit = Fraction(threshold)
        if Fraction(passes, total) >= limit:
            return "pass"
        if Fraction(passes + inconclusive, total) < limit:
            return "fail"
        return "inconclusive"

    def test_status_matches_an_independent_model_for_every_small_mix(self):
        # Planted bug: any change of the two bounds, such as `>` for `>=`,
        # a denominator of conclusive trials, or inconclusive counted as a pass
        # or a fail. Every mix of up to six trials, five thresholds.
        for threshold in self.THRESHOLDS:
            for passes, fails, inconclusive in self.combinations():
                expected = self.bounds_model(passes, fails, inconclusive, threshold)
                result = self.case(passes, fails, inconclusive, float(threshold))
                self.assertEqual(
                    expected, result["status"],
                    f"{passes}P {fails}F {inconclusive}I at threshold {threshold}")

    def test_score_counts_only_conclusive_trials(self):
        # Planted bug: divide by all trials, so an inconclusive trial lowers the score.
        for passes, fails, inconclusive in self.combinations():
            result = self.case(passes, fails, inconclusive)
            conclusive = passes + fails
            if conclusive == 0:
                self.assertIsNone(result["score"], (passes, fails, inconclusive))
            else:
                self.assertAlmostEqual(passes / conclusive, result["score"], places=9)
            self.assertEqual(conclusive, result["conclusive"])
            self.assertEqual(inconclusive, result["inconclusive"])

    def test_an_inconclusive_trial_can_only_help_when_it_becomes_a_pass(self):
        # Properties that do not copy the rule. Order: fail < inconclusive < pass.
        # Turning a fail into an inconclusive trial, or an inconclusive trial
        # into a pass, never lowers the status. Planted bug: a bound with the
        # wrong sign, or an inconclusive trial counted on the wrong side.
        rank = {"fail": 0, "inconclusive": 1, "pass": 2}
        for threshold in self.THRESHOLDS:
            limit = float(threshold)
            for passes, fails, inconclusive in self.combinations():
                here = rank[self.case(passes, fails, inconclusive, limit)["status"]]
                if fails:
                    better = rank[self.case(passes, fails - 1, inconclusive + 1, limit)["status"]]
                    self.assertLessEqual(here, better, (passes, fails, inconclusive, threshold, "F->I"))
                if inconclusive:
                    better = rank[self.case(passes + 1, fails, inconclusive - 1, limit)["status"]]
                    self.assertLessEqual(here, better, (passes, fails, inconclusive, threshold, "I->P"))
                if passes:
                    worse = rank[self.case(passes - 1, fails, inconclusive + 1, limit)["status"]]
                    self.assertGreaterEqual(here, worse, (passes, fails, inconclusive, threshold, "P->I"))

    def test_without_inconclusive_trials_the_old_rule_still_holds(self):
        # The old rule: score = passes / runs, pass when score >= threshold.
        # Planted bug: a case with no inconclusive trial can be INCONCLUSIVE.
        for threshold in self.THRESHOLDS:
            limit = float(threshold)
            for passes, fails, inconclusive in self.combinations():
                if inconclusive:
                    continue
                expected = "pass" if Fraction(passes, passes + fails) >= Fraction(threshold) else "fail"
                self.assertEqual(expected, self.case(passes, fails, 0, limit)["status"],
                                 (passes, fails, threshold))

    def test_a_case_with_only_inconclusive_trials_is_never_pass_or_fail(self):
        # D4: 19 of 33 cases had a cut trial, and 15 had only cut trials.
        # Planted bug: no conclusive trial gives a score of 0 or 1, and a verdict.
        for threshold in self.THRESHOLDS:
            for count in range(1, 8):
                with self.subTest(threshold=threshold, trials=count):
                    result = self.case(0, 0, count, float(threshold))
                    self.assertEqual("inconclusive", result["status"])
                    self.assertIsNone(result["score"])


class VerdictCommandTests(VerdictCase):
    SCRIPT = EVALS / "trial_verdict.py"

    def run_command(self, *args):
        if not self.SCRIPT.is_file():
            self.fail("evals/trial_verdict.py does not exist")
        return subprocess.run([sys.executable, str(self.SCRIPT), *map(str, args)],
                              capture_output=True, text=True)

    def test_trial_status_command_prints_the_verdict_and_stores_it_in_the_trial_folder(self):
        # Planted bug: print only, so the trial folder has no verdict.json; or
        # exit non-zero for an inconclusive trial, which stops a `set -e` run.
        trial = self.trial(exit_codes=(124,), assert_rc=0, log=cli_log(completed_turns=0))

        completed = self.run_command("trial-status", "--trial-dir", trial)

        self.assertEqual(0, completed.returncode, completed.stderr)
        printed = json.loads(completed.stdout)
        self.assertEqual({"status": "inconclusive", "reason": "timeout", "error": None}, printed)
        self.assertEqual(printed, json.loads((trial / "verdict.json").read_text()))

    def test_trial_status_command_exits_0_for_pass_fail_and_inconclusive(self):
        # Planted bug: the exit code carries the status.
        for rc, expected in ((0, "pass"), (1, "fail"), (141, "inconclusive")):
            with self.subTest(assert_rc=rc):
                trial = self.trial(exit_codes=(0,), assert_rc=rc)
                completed = self.run_command("trial-status", "--trial-dir", trial)
                self.assertEqual(0, completed.returncode, completed.stderr)
                self.assertEqual(expected, json.loads(completed.stdout)["status"])

    def test_case_status_command_prints_the_case_state(self):
        # Planted bug: the command and the function use different rules.
        for (passes, fails, inconclusive), expected in (((4, 0, 1), "pass"), ((3, 1, 1), "inconclusive"),
                                                         ((2, 3, 0), "fail"), ((0, 0, 3), "inconclusive")):
            with self.subTest(trials=(passes, fails, inconclusive)):
                completed = self.run_command(
                    "case-status", "--threshold", "0.8", "--passes", passes, "--fails", fails,
                    "--inconclusive", inconclusive)
                self.assertEqual(0, completed.returncode, completed.stderr)
                result = json.loads(completed.stdout)
                self.assertEqual(expected, result["status"])
                self.assertEqual(passes + fails, result["conclusive"])

    def test_case_status_command_refuses_a_threshold_outside_0_to_1(self):
        # Planted bug: accept any number. A threshold of 80 would make every
        # case INCONCLUSIVE or FAIL without a message (no silent fallbacks).
        for threshold in ("1.5", "-0.1", "abc"):
            with self.subTest(threshold=threshold):
                completed = self.run_command(
                    "case-status", "--threshold", threshold, "--passes", 1, "--fails", 0, "--inconclusive", 0)
                self.assertNotEqual(0, completed.returncode)
                self.assertTrue(completed.stderr.strip(), "the refusal needs a message on stderr")


class D4RegressionTests(VerdictCase):
    """Replay the D4 run (161 trials) under the new rules.

    PR 1 records the CLI exit code. D4 ran before PR 1, so the cut flag in
    fixtures/d4/trials.json stands for exit 124. The harness verdict stands for
    the assertion exit (0 for a pass, 1 for a fail).
    """

    @classmethod
    def trials(cls):
        return json.loads((D4 / "trials.json").read_text())["trials"]

    def replay(self, row):
        """Build the trial folder of one D4 row and return its trial status."""
        log = ((D4 / "cli-session-row-075-cut.txt") if row["cut"]
               else (D4 / "cli-session-row-001-completed.txt")).read_text()
        trial = self.trial(exit_codes=(124 if row["cut"] else 0,), log=log,
                           assert_rc=0 if row["harness_passed"] else 1)
        return self.module().trial_status(trial)

    def test_fixture_matches_the_judge_report(self):
        # Guards the fixture itself, from eval-judgement.md: 161 trials, 121
        # harness passes, 40 harness fails, and 78 cut trials.
        rows = self.trials()
        self.assertEqual(161, len(rows))
        self.assertEqual(121, sum(r["harness_passed"] for r in rows))
        self.assertEqual(78, sum(r["cut"] for r in rows))
        self.assertEqual(4, sum(r["judged"] == "W" for r in rows))
        self.assertEqual(52, sum(r["judged"] == "L" for r in rows))

    def test_every_cut_trial_is_inconclusive_and_none_is_a_pass_or_a_fail(self):
        # Planted bug: a passing assertion wins over exit 124, or a failing one
        # does. D4: 78 of 161 trials ran into the deadline.
        for row in self.trials():
            if not row["cut"]:
                continue
            with self.subTest(row=row["row"], case=row["case"]):
                result = self.replay(row)
                self.assertEqual("inconclusive", result["status"], result)
                self.assertEqual("timeout", result["reason"])

    def test_the_16_cut_false_passes_of_the_judge_leave_the_scores(self):
        # The judge found 19 false passes. 16 were cut trials: rows 21, 53, 54,
        # 68, and the 12 rows that passed on a line leaked from the trial before
        # (75, 77, 80, 82, 85, 89, 90, 104, 105, 107, 108, 109). PR 2 alone
        # removes them. The other 3 (rows 6, 8, 10, identity_version) were not
        # cut. A weak check passed them. PR 7 fixes those.
        # Planted bug: a cut trial keeps the harness verdict.
        rows = self.trials()
        false_passes = [r for r in rows if r["harness_passed"] and r["judged"] != "C"]
        self.assertEqual(19, len(false_passes))
        self.assertEqual(
            [21, 53, 54, 68, 75, 77, 80, 82, 85, 89, 90, 104, 105, 107, 108, 109],
            [r["row"] for r in false_passes if r["cut"]])

        still_passing = [r["row"] for r in false_passes if self.replay(r)["status"] == "pass"]

        self.assertEqual([6, 8, 10], still_passing)

    def test_no_trial_that_the_judge_called_load_is_a_pass_or_a_fail(self):
        # Judge code L: no valid answer in time. Planted bug: any L trial keeps
        # a conclusive state.
        for row in self.trials():
            if row["judged"] != "L":
                continue
            with self.subTest(row=row["row"], case=row["case"]):
                self.assertEqual("inconclusive", self.replay(row)["status"])

    def test_case_states_of_the_d4_run(self):
        # Hand-checked against the judge's table. Threshold 0.8, as in D4.
        # Each value: passes, fails, inconclusive, status.
        # skill_activation_subagent_authoring has only cut trials, so it is
        # INCONCLUSIVE with no score. The design and the judge agree: "cannot
        # be judged for D4". Planted bug: inconclusive counted as a fail makes
        # it FAIL, and counted as a pass makes it PASS.
        expected = {
            "identity_name": (5, 0, 0, "pass"),
            "identity_version": (5, 0, 0, "pass"),
            "identity_repo": (5, 0, 0, "pass"),
            "identity_session": (5, 0, 0, "pass"),
            "identity_file_routing": (3, 1, 1, "inconclusive"),
            "skill_scheduling_knowledge": (5, 0, 0, "pass"),
            "skill_mattermost_current_session": (5, 0, 0, "pass"),
            "skill_cron_tz_timezone": (5, 0, 0, "pass"),
            "skill_progressive_disclosure": (4, 0, 1, "pass"),
            "skill_device_pairing_procedure": (5, 0, 0, "pass"),
            "skill_memory_knowledge": (2, 1, 2, "inconclusive"),
            "skill_operations_diagnostics": (0, 0, 5, "inconclusive"),
            "skill_citation_search": (0, 0, 5, "inconclusive"),
            "skill_web_content_knowledge": (0, 0, 5, "inconclusive"),
            "skill_activation_scheduling": (0, 0, 5, "inconclusive"),
            "skill_activation_memory": (0, 0, 5, "inconclusive"),
            "skill_activation_search": (0, 0, 5, "inconclusive"),
            "skill_activation_soft_scheduling": (0, 0, 5, "inconclusive"),
            "skill_activation_soft_memory": (0, 0, 5, "inconclusive"),
            "skill_activation_run_reminder": (0, 0, 5, "inconclusive"),
            "skill_activation_subagent_authoring": (0, 0, 5, "inconclusive"),
            "skill_activation_user_coding": (0, 0, 5, "inconclusive"),
            "skill_activation_user_serialization": (0, 0, 5, "inconclusive"),
            "skill_server_feed_logical_access": (0, 0, 5, "inconclusive"),
            "mcp_prompt_skill_activation": (0, 0, 5, "inconclusive"),
            "mcp_prompt_skill_unrelated": (5, 0, 0, "pass"),
            "skill_explicit_physical_inspection": (5, 0, 0, "pass"),
            "skill_no_activation_unrelated": (5, 0, 0, "pass"),
            "skill_no_activation_general_code": (5, 0, 0, "pass"),
            "memory_recall_active": (2, 0, 3, "inconclusive"),
            "memory_identity_preference_routing": (5, 0, 0, "pass"),
            "memory_explicit_store": (5, 0, 0, "pass"),
            "memory_recall_filters": (0, 0, 1, "inconclusive"),
        }
        counts = {}
        for row in self.trials():
            counts.setdefault(row["case"], {"pass": 0, "fail": 0, "inconclusive": 0})
            counts[row["case"]][self.replay(row)["status"]] += 1

        module = self.module()
        actual = {}
        for name, tally in counts.items():
            case = module.case_status(tally["pass"], tally["fail"], tally["inconclusive"], 0.8)
            actual[name] = (tally["pass"], tally["fail"], tally["inconclusive"], case["status"])

        self.assertEqual(expected, actual)

    def test_d4_scores_count_only_conclusive_trials(self):
        # skill_progressive_disclosure: the D4 harness scored it 4/5. Row 45
        # was cut, and the harness counted it as a fail. The new score is 4/4,
        # and it shows one inconclusive trial. Planted bug: score over all trials.
        module = self.module()
        trials = [self.replay(r) for r in self.trials() if r["case"] == "skill_progressive_disclosure"]
        tally = {state: sum(t["status"] == state for t in trials) for state in ("pass", "fail", "inconclusive")}

        case = module.case_status(tally["pass"], tally["fail"], tally["inconclusive"], 0.8)

        self.assertEqual({"pass": 4, "fail": 0, "inconclusive": 1}, tally)
        self.assertAlmostEqual(1.0, case["score"])
        self.assertEqual(4, case["conclusive"])


if __name__ == "__main__":
    unittest.main()
