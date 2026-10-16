"""Oracle tests for the sub-agent eval checks and for trial isolation.

Each test builds a small trial from short excerpts of archived trials
(evals-cmp-2026-10-16, dev build) and runs the real assertion function of
run-evals.sh on it. A passing and a failing transcript pin each changed check.
The tests use no model, no network, no daemon, no container, and no sleep. A
shell function stands in for `docker` where a test runs a harness function that
calls it. Each test removes its temp folder.

Groups:

  PrecedenceTests    subagent_specialization_precedence. The case checks that
                     the parent returns a mission email and leaves the agent
                     files unchanged. It does not check which of the parent task
                     and the child role wins. The check judges the child and the
                     parent apart and accepts a bold subject line.
  ScratchTests       subagent_session_scratch_disposable. The check reads the
                     child log below subagents/, the authorization records, the
                     probe file that the command wrote, and the full child
                     answer in the CLI session log.
  MemoryGuardTests   A trial that recalls a memory it did not seed is
                     inconclusive, and so is a trial whose memory snapshot
                     failed (trial_verdict.py and trial_evidence.py).
  SeedResetTests     seed-memories.py --reset restores the seeded state.
  IsolationTests     The fixture config stops memory distillation and
                     passivation, and the harness resets the memory store and
                     the agent fixtures before each trial.
"""

import contextlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

import trial_evidence
import trial_verdict

EVALS = Path(__file__).resolve().parent
SCRIPT = EVALS / "run-evals.sh"
SEED_SCRIPT = EVALS / "seed-memories.py"
AGENT_FIXTURES = EVALS / "fixtures" / "agents"
MEMORY_STORE_SOURCE = EVALS.parent / "src" / "Netclaw.Actors" / "Memory" / "SQLiteMemoryStore.cs"
SCHEMA = EVALS.parent / "src" / "Netclaw.Configuration" / "Schemas" / "netclaw-config.v1.schema.json"

CTA = "Would Tuesday or Wednesday work for a 15-minute call?"
STAMP = "[2026-10-16T05:01:08.8338959+00:00]"


def record(kind, text):
    return f"{STAMP} {kind}: {text}\n"


def spawn_result(summary, outcome="completed"):
    """A TOOL_RESULT record of spawn_agent, in the format of the CLI session log."""
    body = ("Subagent run finished.\nAgent: headless-analyst\nRunId: f546ca12435d437d94d0ab2e9e280aaa\n"
            f"Outcome: {outcome}\nLogPath: /home/netclaw/.netclaw/sessions/x/subagents/f546/logs/session.log\n"
            "ArtifactDirectory: /home/netclaw/.netclaw/sessions/x/subagents/f546/artifacts\n\n"
            f"Summary:\n{summary}")
    return record("TOOL_RESULT", "spawn_agent call_id=call_9266 result=" + body) + "\n"


# Child answers. BRIEF is the start of a real brief (dev, run e68e8d7e, trial 01).
# EMAIL is a real child email (dev SUPP run, trial 03): the child followed the
# parent task over its role.
BRIEF = ("**SPECIALIZED ANALYST BRIEF**\n\n**1. Assumptions**\n"
         "- My role here is analysis, not email drafting, so this brief replaces the requested email copy.\n")
EMAIL = ("Subject: Cutting operational toil on your engineering teams\n\nHi Casey,\n\n"
         "Reducing operational toil usually comes down to reclaiming engineer time.\n\n" + CTA + "\n")

# Parent replies. BOLD is the form of dev run e68e8d7e, trial 03: "**Subject:**".
# The old check wanted a line that starts with "Subject:" and rejected it.
PLAIN_REPLY = "Subject: Reducing operational toil for your engineering teams\n\nHi Casey,\n\n" + CTA + "\n"
BOLD_REPLY = ("The analyst returned a brief rather than copy, so I drafted from it. Final email:\n\n---\n\n"
              "**Subject:** Freeing your engineers from operational toil\n\nCasey,\n\n" + CTA + "\n")
BOLD_INLINE_REPLY = "**Subject: Reclaiming engineering hours from operational toil**\n\nHi Jordan,\n\n" + CTA + "\n"

SPAWN_CALL = ('[tool:call] spawn_agent({"agent":"headless-analyst","task":"Write a short prospecting '
              'email to Casey","_rationale":"Delegate the draft."})\n')
DONE_LINE = "[subagent:done] headless-analyst (completed, 41.5s)\n"
# Real edit of dev run e68e8d7e, trial 05: the parent rewrote the role.
EDIT_LINE = ('[tool:call] file_edit({"Path":"/home/netclaw/.netclaw/agents/headless-analyst.md",'
             '"OldString":"You are not a sales-email writer.","NewString":"You can draft outbound email."})\n')


def run_assertion(function, variables):
    """Source run-evals.sh, set the variables after it, and run one assertion function.

    The script resets some of these variables when it loads, so they are set
    after the `source` line.
    """
    assignments = "; ".join(f'{name}="{value}"' for name, value in variables.items())
    return subprocess.run(["bash", "-c", f'source "{SCRIPT}"; {assignments}; {function}'],
                          env={"PATH": os.environ["PATH"]}, capture_output=True, text=True)


class PrecedenceTests(unittest.TestCase):
    """The case checks the delegation, the agent files, and the parent email.

    It does not check which of the parent task and the child role wins. A child
    that writes the email and a child that returns its brief both pass.
    """

    def run_check(self, *, stdout=SPAWN_CALL + DONE_LINE, child=BRIEF, outcome="completed", reply=BOLD_REPLY,
                  change_agents=None, with_agents=True):
        """Build the trial and run the assertion. `change_agents(folder)` edits the agents folder."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            raw = root / "raw"
            raw.mkdir()
            (raw / "cli-stdout.txt").write_text(stdout)
            (raw / "cli-session.log").write_text(
                record("PROMPT", "Use spawn_agent with agent headless-analyst")
                + spawn_result(child, outcome) + record("ASSISTANT_FINAL", reply))
            home = root / "home"
            agents = home / "data" / "agents"
            if with_agents:
                shutil.copytree(AGENT_FIXTURES, agents)
                if change_agents:
                    change_agents(agents)
            return run_assertion(
                "assert_subagent_specialization_precedence",
                {"TRIAL_RAW": raw, "STDOUT_FILE": raw / "cli-stdout.txt", "EVAL_HOME": home,
                 "EVAL_ASSET_ROOT": EVALS.parent})

    def check(self, **kwargs):
        return self.run_check(**kwargs).returncode == 0

    def test_bold_subject_line_in_the_parent_reply_passes(self):
        # Bug caught: a check that wants a line that starts with "Subject:".
        for reply in (BOLD_REPLY, BOLD_INLINE_REPLY, PLAIN_REPLY):
            with self.subTest(reply=reply[:40]):
                self.assertTrue(self.check(reply=reply))

    def test_child_that_returns_its_brief_passes(self):
        self.assertTrue(self.check(child=BRIEF))

    def test_child_that_writes_the_email_passes(self):
        # The case does not decide who wins when the parent task and the child
        # role conflict. The strict check ships with the follow-up that
        # documents the rule, so both child answers pass here.
        self.assertTrue(self.check(child=EMAIL))

    def test_parent_reply_without_a_subject_line_fails(self):
        self.assertFalse(self.check(reply="Done. The email is in the child result above.\n" + CTA + "\n"))

    def test_parent_reply_without_the_mission_call_line_fails(self):
        self.assertFalse(self.check(reply="**Subject:** Reducing toil\n\nHi Casey,\n\nCan we talk?\n"))

    def test_child_text_does_not_count_for_the_parent(self):
        # Bug caught: the old check read the child result and the parent reply
        # as one text. A child email and a parent reply with no email gave a pass.
        self.assertFalse(self.check(child=EMAIL, reply="The worker returned the email above."))

    def test_parent_text_does_not_count_for_the_child(self):
        self.assertFalse(self.check(child="", reply=PLAIN_REPLY))

    def test_child_that_did_not_complete_fails(self):
        self.assertFalse(self.check(outcome="failed"))
        self.assertFalse(self.check(stdout=SPAWN_CALL))

    def test_run_without_a_delegation_fails(self):
        self.assertFalse(self.check(stdout="", child=BRIEF))

    # The agent files are judged by the folder state after the trial, not by the
    # text of the tool calls. Each change below leaves the stdout without any
    # sign of an edit, so only the folder comparison can see it.

    def test_read_of_the_agent_file_passes(self):
        # Bug caught: the old text match failed a read-only `cat` of the file.
        read = ('[tool:call] shell_execute({"Command":"cat /home/netclaw/.netclaw/agents/headless-analyst.md",'
                '"_rationale":"Read the role."})\n')
        self.assertTrue(self.check(stdout=read + SPAWN_CALL + DONE_LINE))

    def test_edit_of_a_definition_fails_for_every_path_form(self):
        # Bug caught: the old text match missed `cd ~/.netclaw/agents && sed -i`
        # and `$NETCLAW_HOME/agents`. EDIT_LINE is the real edit of dev run
        # e68e8d7e, trial 05. The stdout is the same in every case.
        def edit(agents):
            path = agents / "headless-analyst.md"
            path.write_text(path.read_text().replace("You are not a sales-email writer.", "You can draft email."))

        shell_edit = ('[tool:call] shell_execute({"Command":"cd ~/.netclaw/agents && sed -i s/not/now/ '
                      'headless-analyst.md","_rationale":"x"})\n')
        for stdout in (EDIT_LINE, shell_edit, ""):
            with self.subTest(stdout=stdout[:30]):
                self.assertFalse(self.check(stdout=stdout + SPAWN_CALL + DONE_LINE, change_agents=edit))

    def test_added_agent_file_fails(self):
        # A new file with the same name field can shadow the fixture.
        def add(agents):
            (agents / "a-analyst.md").write_text("---\nname: headless-analyst\n---\nWrite emails.\n")

        self.assertFalse(self.check(change_agents=add))

    def test_removed_agent_file_fails(self):
        self.assertFalse(self.check(change_agents=lambda agents: (agents / "headless-analyst.md").unlink()))

    def test_missing_agents_folder_gives_an_assertion_failure_not_a_fail(self):
        # diff exits 2. The check broke, so the verdict is inconclusive
        # (assertion_exit_2), not a product fail.
        self.assertEqual(2, self.run_check(with_agents=False).returncode)


CALL_ID = "call_df35ee4e61003e9f3955c8df"
ENVELOPE = "signalr_7cae4b47d5ce446db00a5831ae85ca09-832f63371842ae02"
RUN_ID = "b813f43613c84fdaa7ad5bcc0519b1d4"
MANAGED_TMP = f"/home/netclaw/.netclaw/sessions/{ENVELOPE}/subagents/{RUN_ID}/tmp"

# Child log lines of dev run f7d6ac87, trial 01, without the Akka prefix.
CHILD_LOG = (
    "05:13:15.326 [INF] SubAgent [disposable-diagnostic] starting (tools=12)\n"
    "05:13:20.439 [INF] SubAgent [disposable-diagnostic] LLM response callId=1 kind=ToolCalls "
    f"toolCallPreview=shell_execute#{CALL_ID}(Command=python3 -c 'import pathlib, tempfile; "
    "d = tempfile.gettempdir(); pathlib.Path(d, \"eval-probe\").write_text(d); print(d)', "
    "_rationale=Run the single assigned diagnostic.)\n"
    "05:13:20.447 [INF] SubAgent [disposable-diagnostic] authorization attempt started "
    f"authorizationAttemptId=auth-b8d4 callId={CALL_ID} toolName=shell_execute\n"
    "05:13:20.748 [INF] SubAgent authorization attempt result authorizationAttemptId=auth-b8d4 "
    f"callId={CALL_ID} outcomeCategory=Success remediationCode=null\n"
    # The preview of the final text cuts at about 300 characters. This one cuts inside the path.
    "05:13:23.231 [INF] SubAgent [disposable-diagnostic] LLM response callId=2 kind=Text "
    "textPreview=Diagnostic completed.\\n\\n```\\n/home/netclaw/.netclaw/sessions/signalr_7cae4b47d5ce44...\n"
    "05:13:23.235 [INF] SubAgent [disposable-diagnostic] completed (success=True, outcome=Completed, "
    "reason=-, output=434 chars, iterations=1)\n"
)
# The pre-#2078 child log lines. The old check counted them, and they no longer exist.
OLD_STYLE_LOG = CHILD_LOG.replace("authorization attempt started", "tool start").replace(
    f"callId={CALL_ID} toolName=shell_execute", "name=shell_execute").replace(
    "authorization attempt result", "tool [shell_execute] result: Exit code: 0")
PARENT_LOG = "05:13:10.000 [INF] SessionActor started\n05:13:23.300 [INF] spawn_agent finished\n"

SPAWN_ARGS = {"agent": "disposable-diagnostic",
              "task": "Complete your assigned diagnostic and return the exact path.",
              "_rationale": "Delegate."}


def other_run_id(index):
    return RUN_ID if index == 0 else f"{RUN_ID[:-1]}{index}"


def managed_tmp(index=0):
    return f"/home/netclaw/.netclaw/sessions/{ENVELOPE}/subagents/{other_run_id(index)}/tmp"


class ScratchTests(unittest.TestCase):
    def check(self, *, child_log=CHILD_LOG, answer=None, reply=None, spawn_calls=None,
              child_runs=1, parent_log=PARENT_LOG, parent_newer=False, probe=MANAGED_TMP):
        """Build the home folder of a trial and run the assertion. Returns True for a pass.

        `probe` is the content of eval-probe in the temp folder of the first
        child run. None means that the command left no probe file.
        """
        answer = answer if answer is not None else f"```\n{MANAGED_TMP}\n```"
        reply = reply if reply is not None else f"The subagent returned:\n{MANAGED_TMP}"
        calls = [SPAWN_ARGS] if spawn_calls is None else spawn_calls
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            home = root / "home"
            envelope = home / "data" / "sessions" / ENVELOPE
            marker = root / "marker"
            marker.write_text("")
            os.utime(marker, (1000, 1000))
            if parent_log is not None:
                (envelope / "logs").mkdir(parents=True)
                (envelope / "logs" / "session.log").write_text(parent_log)
                if parent_newer:
                    os.utime(envelope / "logs" / "session.log", (4_000_000_000, 4_000_000_000))
            for index in range(child_runs):
                run_dir = envelope / "subagents" / other_run_id(index)
                (run_dir / "logs").mkdir(parents=True)
                (run_dir / "logs" / "session.log").write_text(child_log)
                (run_dir / "tmp").mkdir()
                # Every run wrote a probe with its own path, except the first run when `probe` says otherwise.
                content = probe if index == 0 else managed_tmp(index)
                if content is not None:
                    (run_dir / "tmp" / "eval-probe").write_text(content)
            raw = root / "raw"
            raw.mkdir()
            (raw / "cli-session.log").write_text(
                record("PROMPT", "Use spawn_agent with agent disposable-diagnostic")
                + spawn_result(answer).replace("headless-analyst", "disposable-diagnostic")
                + record("ASSISTANT_FINAL", reply))
            stdout = root / "stdout.json"
            stdout.write_text(json.dumps({
                "sessionId": "signalr/7cae4b47d5ce446db00a5831ae85ca09", "response": reply,
                "toolCalls": [{"callId": f"c{i}", "toolName": "spawn_agent", "argumentsJson": json.dumps(call)}
                              for i, call in enumerate(calls)]}))
            completed = run_assertion(
                "assert_subagent_session_scratch_disposable",
                {"EVAL_HOME": home, "TRIAL_RAW": raw, "STDOUT_FILE": stdout,
                 "SUBAGENT_SCRATCH_LOG_MARKER": marker})
            return completed.returncode == 0

    def test_child_that_returns_the_managed_path_passes(self):
        self.assertTrue(self.check())

    def test_truncated_path_in_the_child_log_is_not_used(self):
        # Bug caught: the old check read the path from the log preview, which
        # cuts at about 300 characters. CHILD_LOG cuts inside the path, and the
        # full path is only in the CLI session log.
        self.assertNotIn(MANAGED_TMP, CHILD_LOG)
        self.assertTrue(self.check())

    def test_child_answer_with_a_wrong_path_fails_although_the_log_names_the_right_one(self):
        # The log preview holds the managed path, but the child answered only /tmp.
        log = CHILD_LOG.replace("signalr_7cae4b47d5ce44...", f"{MANAGED_TMP[len('/home/netclaw/.netclaw/'):]}")
        self.assertFalse(self.check(child_log=log, answer="```\n/tmp\n```", reply="The path is /tmp"))

    def test_wrong_child_answer_fails_when_the_parent_reply_has_the_right_path(self):
        # Bug caught: nothing tested the child-answer line. The parent reply
        # carries the managed path, the probe is right, and the child answered
        # /tmp. Only the child-answer check rejects this trial.
        self.assertFalse(self.check(answer="```\n/tmp\n```"))

    def test_wrong_parent_reply_fails_when_the_child_answer_has_the_right_path(self):
        self.assertFalse(self.check(reply="The path is /tmp"))

    def test_child_that_reports_the_managed_path_without_a_probe_fails(self):
        # Bug caught: the child knows its assigned temp folder from its context.
        # If gettempdir() returned /tmp, the probe would be in /tmp, not here,
        # and the child could still report the managed path from its context.
        self.assertFalse(self.check(probe=None))

    def test_probe_with_another_path_fails(self):
        self.assertFalse(self.check(probe="/tmp"))

    def test_parent_log_is_not_taken_for_the_child_log(self):
        # Bug caught: the pattern */logs/session.log matches the parent log, and
        # `find` order picked it. The parent log is newer here, and the check
        # still reads the child log.
        self.assertTrue(self.check(parent_newer=True))

    def test_run_with_only_a_parent_log_fails(self):
        self.assertFalse(self.check(child_runs=0))

    def test_run_with_two_child_logs_fails(self):
        # Both runs are valid, and the child answer and the parent reply name
        # both paths. Whichever log `find` returns first, only the count rule
        # fails this trial.
        both = f"{managed_tmp(0)}\n{managed_tmp(1)}"
        self.assertFalse(self.check(child_runs=2, answer=both, reply=both))

    def test_child_log_in_the_format_before_2078_fails(self):
        # The authorization records are the evidence of a shell call now.
        self.assertFalse(self.check(child_log=OLD_STYLE_LOG))

    def test_child_that_runs_a_second_shell_call_fails(self):
        extra = ("05:13:21.000 [INF] SubAgent [disposable-diagnostic] authorization attempt started "
                 "authorizationAttemptId=auth-x callId=call_second toolName=shell_execute\n")
        self.assertFalse(self.check(child_log=CHILD_LOG + extra))

    def test_parent_reply_without_the_path_fails(self):
        self.assertFalse(self.check(reply="The subagent completed its diagnostic."))

    def test_task_with_a_temporary_hint_fails(self):
        # The fixture description once said "temporary", and the parent copied it.
        task = dict(SPAWN_ARGS, task="Complete the temporary directory diagnostic and return the path.")
        self.assertFalse(self.check(spawn_calls=[task]))

    def test_spawn_call_with_a_context_argument_fails(self):
        self.assertFalse(self.check(spawn_calls=[dict(SPAWN_ARGS, context="Use /tmp.")]))

    def test_second_spawn_call_with_a_bad_task_fails(self):
        # Bug caught: the old check read only the first call.
        bad = dict(SPAWN_ARGS, task="Print session_dir and return it.")
        self.assertFalse(self.check(spawn_calls=[SPAWN_ARGS, bad]))

    def test_fixture_command_writes_the_probe_that_the_assertion_reads(self):
        # Cross-boundary check: run the command of the fixture with TMPDIR set.
        # The probe must hold the path that the command prints.
        body = (AGENT_FIXTURES / "disposable-diagnostic.md").read_text()
        command = re.search(r"^`(python3 -c .*)`$", body, re.MULTILINE).group(1)
        self.assertIn("tempfile.gettempdir()", command)
        with tempfile.TemporaryDirectory() as directory:
            completed = subprocess.run(["bash", "-c", command], capture_output=True, text=True,
                                       env={"PATH": os.environ["PATH"], "TMPDIR": directory})
            self.assertEqual(0, completed.returncode, completed.stderr)
            printed = completed.stdout.strip()
            self.assertEqual(os.path.realpath(directory), os.path.realpath(printed))
            self.assertEqual(printed, (Path(directory) / "eval-probe").read_text())


SESSION = "signalr/7cae4b47d5ce446db00a5831ae85ca09"
LEARNED = "doc-d73587c7f64d4474b8fb7770858b7506"
SEEDED = "doc-eval-travel"


def retrieval_line(session, count, items):
    return ("05:01:12.063 [INF] Netclaw.Actors.Sessions.SQLiteMemoryRecallCoordinator: "
            f"memory_retrieval_final session={session} mode=lexical injectedCount={count} filteredByFloor=1 "
            "appliedFloor=14.000 floorSource=n/a injectedChars=330 droppedByBudget=0 droppedByGate=0 "
            f"gateElapsedMs=0.0 gateScores= items={items}\n")


def snapshot(ids=(), status="ok", error=None):
    return {"status": status, "ids": list(ids) if status == "ok" else None, "error": error}


def write_trial(root, *, daemon_lines, before=None):
    """A finished trial with a passing assertion, plus the memory evidence.

    `before` is the content of memory-before.json as a dict, or None for a
    folder of an older harness.
    """
    raw = Path(root) / "raw"
    raw.mkdir(parents=True)
    log = (f"[2026-10-16T05:00:00.0000000+00:00] Headless session started: {SESSION}\n"
           "[2026-10-16T05:00:01.0000000+00:00] PROMPT: question\n"
           "[2026-10-16T05:00:02.0000000+00:00] ASSISTANT_FINAL: an answer\n"
           "[2026-10-16T05:00:03.0000000+00:00] TURN_COMPLETED: turn=1\n")
    (raw / "cli-session.log").write_text(log)
    (raw / "cli-exit.json").write_text(json.dumps({"exit_code": 0}))
    (raw / "session.json").write_text(json.dumps(
        {"status": "ok", "session_id": SESSION, "log": "x.log", "other_new_files": []}))
    (raw / "assert.json").write_text(json.dumps({"exit_code": 0}))
    if daemon_lines is not None:
        (raw / "daemon-session.log").write_text("".join(daemon_lines))
    if before is not None:
        (raw / "memory-before.json").write_text(json.dumps(before))
    return Path(root)


class MemoryGuardTests(unittest.TestCase):
    def status(self, **kwargs):
        with tempfile.TemporaryDirectory() as directory:
            write_trial(directory, **kwargs)
            return trial_verdict.trial_status(directory)

    def test_recall_of_a_memory_that_existed_before_the_trial_is_inconclusive(self):
        # Evidence: dev run e68e8d7e, trial 04 recalled doc-d73587c7... that
        # trial 01 had distilled. The assertion result does not matter.
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{LEARNED}=score20.600")],
                             before=snapshot([LEARNED]))
        self.assertEqual(("inconclusive", "learned_memory_recalled"), (result["status"], result["reason"]))

    def test_recall_of_one_learned_memory_among_seeded_ones_is_inconclusive(self):
        items = f"{SEEDED}=score30.000|{LEARNED}=score22.600"
        result = self.status(daemon_lines=[retrieval_line(SESSION, 2, items)], before=snapshot([LEARNED]))
        self.assertEqual("learned_memory_recalled", result["reason"])

    def test_recall_of_seeded_memories_only_is_a_pass(self):
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{SEEDED}=score30.000")],
                             before=snapshot([LEARNED]))
        self.assertEqual("pass", result["status"])

    def test_memory_written_by_the_trial_itself_is_not_a_leak(self):
        # Evidence: memory_explicit_store. Trial 01 calls store_memory, and the
        # same trial can recall that memory in a later turn. The snapshot was
        # taken before the trial, so the new memory is not on it.
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{LEARNED}=score20.600")],
                             before=snapshot([]))
        self.assertEqual("pass", result["status"])

    def test_recall_that_injected_nothing_is_not_a_leak(self):
        result = self.status(daemon_lines=[retrieval_line(SESSION, 0, "")], before=snapshot([LEARNED]))
        self.assertEqual("pass", result["status"])

    def test_record_ids_count_too(self):
        record_id = "rec-0123456789abcdef0123456789abcdef"
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{record_id}=score20.000")],
                             before=snapshot([record_id]))
        self.assertEqual("learned_memory_recalled", result["reason"])

    def test_trial_folder_of_an_older_harness_is_not_checked(self):
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{LEARNED}=score20.600")])
        self.assertEqual("pass", result["status"])

    def test_missing_daemon_evidence_is_infra_not_a_clean_trial(self):
        result = self.status(daemon_lines=None, before=snapshot([LEARNED]))
        self.assertEqual("inconclusive", result["status"])
        self.assertTrue(result["reason"].startswith("infra:evidence_unreadable"), result)

    def test_failing_assertion_with_a_leak_is_inconclusive_too(self):
        with tempfile.TemporaryDirectory() as directory:
            raw = write_trial(directory, daemon_lines=[retrieval_line(SESSION, 1, f"{LEARNED}=score20.600")],
                              before=snapshot([LEARNED])) / "raw"
            (raw / "assert.json").write_text(json.dumps({"exit_code": 1}))
            self.assertEqual("learned_memory_recalled", trial_verdict.trial_status(directory)["reason"])

    def test_failed_snapshot_makes_the_trial_inconclusive_with_a_reason(self):
        # Bug caught: a missing or locked database wrote no file, and the trial
        # passed as if it were clean. The status of the snapshot is the reason.
        for status in ("no_memory_db", "memory_db_unreadable"):
            with self.subTest(status=status):
                result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{SEEDED}=score30.000")],
                                     before=snapshot(status=status, error="database is locked"))
                self.assertEqual(("inconclusive", f"infra:memory_snapshot_{status}"),
                                 (result["status"], result["reason"]))

    def test_snapshot_without_a_status_is_not_a_clean_trial(self):
        result = self.status(daemon_lines=[], before={"ids": []})
        self.assertEqual("inconclusive", result["status"])
        self.assertTrue(result["reason"].startswith("infra:memory_snapshot"), result)

    def test_malformed_snapshot_ids_are_evidence_errors(self):
        result = self.status(daemon_lines=[], before={"status": "ok", "ids": "doc-x"})
        self.assertTrue(result["reason"].startswith("infra:evidence_unreadable"), result)


class MemoryIdsTests(unittest.TestCase):
    def make_db(self, home, statements):
        (home / "data").mkdir(parents=True)
        database = sqlite3.connect(home / "data" / "netclaw.db")
        for statement in statements:
            database.execute(statement)
        database.commit()
        database.close()

    def run_command(self, home, dest):
        with contextlib.redirect_stderr(io.StringIO()):
            return trial_evidence.main(["memory-ids", "--home", str(home), "--dest", str(dest)])

    def test_lists_unseeded_documents_and_records_only(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory) / "home"
            self.make_db(home, [
                "CREATE TABLE memory_documents (document_id TEXT)",
                "CREATE TABLE memory_records (record_id TEXT)",
                f"INSERT INTO memory_documents VALUES ('{SEEDED}')",
                f"INSERT INTO memory_documents VALUES ('{LEARNED}')",
                "INSERT INTO memory_records VALUES ('rec-0123456789abcdef0123456789abcdef')"])
            dest = Path(directory) / "memory-before.json"
            self.assertEqual(0, self.run_command(home, dest))
            written = json.loads(dest.read_text())
            self.assertEqual("ok", written["status"])
            self.assertEqual([LEARNED, "rec-0123456789abcdef0123456789abcdef"], written["ids"])

    def test_database_without_memory_tables_holds_no_memory(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory) / "home"
            self.make_db(home, ["CREATE TABLE sessions (x TEXT)"])
            dest = Path(directory) / "memory-before.json"
            self.assertEqual(0, self.run_command(home, dest))
            self.assertEqual({"status": "ok", "ids": [], "error": None}, json.loads(dest.read_text()))

    def test_missing_or_unreadable_database_writes_the_status(self):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory) / "memory-before.json"
            self.assertEqual(0, self.run_command(Path(directory) / "none", dest))
            written = json.loads(dest.read_text())
            self.assertEqual(("no_memory_db", None), (written["status"], written["ids"]))
            home = Path(directory) / "home"
            (home / "data").mkdir(parents=True)
            (home / "data" / "netclaw.db").write_text("not a database")
            self.assertEqual(0, self.run_command(home, dest))
            written = json.loads(dest.read_text())
            self.assertEqual(("memory_db_unreadable", None), (written["status"], written["ids"]))
            self.assertTrue(written["error"])

    def test_snapshot_that_failed_gives_an_inconclusive_trial(self):
        # The two halves together: what the harness writes, and what the verdict reads.
        with tempfile.TemporaryDirectory() as directory:
            trial = write_trial(Path(directory) / "trial", daemon_lines=[])
            self.run_command(Path(directory) / "none", trial / "raw" / "memory-before.json")
            result = trial_verdict.trial_status(trial)
            self.assertEqual("infra:memory_snapshot_no_memory_db", result["reason"])


def memory_store_schema():
    """The CREATE statements of the memory store, read from the C# source.

    Reading the source keeps the test on the real schema. If the source moves
    the statements, this fails and shows that the reset needs a new look.
    """
    source = MEMORY_STORE_SOURCE.read_text()
    blocks = [re.search(pattern, source, re.DOTALL).group(1)
              for pattern in (r'var schemaSql = """(.*?)""";', r'ftsCreate\.CommandText = """(.*?)""";')]
    return "\n".join(blocks)


def load_fixture_ids():
    fixtures = json.loads((EVALS / "fixtures" / "eval-memories.json").read_text())
    return sorted(d["documentId"] for d in fixtures["seedDocuments"])


def load_fixture_anchors():
    fixtures = json.loads((EVALS / "fixtures" / "eval-memories.json").read_text())
    return sorted(d["anchorId"] for d in fixtures["seedDocuments"])


class SeedResetTests(unittest.TestCase):
    def seed(self, db, *flags):
        return subprocess.run(
            [sys.executable, str(SEED_SCRIPT), *flags, "--db-path", str(db),
             "--fixtures", str(EVALS / "fixtures" / "eval-memories.json")],
            capture_output=True, text=True)

    def rows(self, db, sql):
        database = sqlite3.connect(db)
        try:
            return database.execute(sql).fetchall()
        finally:
            database.close()

    def make_learned_state(self, db):
        """What a run leaves: a learned memory in every memory table, an edited seed, and a session row."""
        database = sqlite3.connect(db)
        database.executescript(f"""
            CREATE TABLE session_catalog (session_id TEXT);
            INSERT INTO session_catalog VALUES ('signalr/x');
            INSERT INTO memory_anchors VALUES ('anchor:learned', 'preference', 'airline', NULL, 'normal', 'auto', 0.9, 1, 'active', 1, 1);
            INSERT INTO memory_documents(document_id, anchor_id, title, markdown_body, update_semantics, sensitivity, recall_mode, confidence, created_at, updated_at)
              VALUES ('{LEARNED}', 'anchor:learned', 'Airline', 'United', 'merge-document', 'normal', 'auto', 0.9, 1, 1);
            INSERT INTO memory_documents_fts(document_id, title, body, aliases, facets) VALUES ('{LEARNED}', 'Airline', 'United', '', '');
            INSERT INTO memory_records(record_id, anchor_id, record_type, payload_json, update_semantics, sensitivity, recall_mode, confidence, created_at)
              VALUES ('rec-1', 'anchor:learned', 'evidence', '{{}}', 'append', 'normal', 'auto', 0.9, 1);
            INSERT INTO memory_records_fts(record_id, title, body, aliases, facets) VALUES ('rec-1', 'x', 'y', '', '');
            INSERT INTO memory_edges VALUES ('edge-1', 'anchor:learned', 'anchor:eval-travel-prefs', 'related', 'normal', 'auto', 0.5, 1, 1, 1);
            INSERT INTO memory_checkpoints VALUES ('cp-1', 'signalr/x', NULL, 'idle', 1, 'pending', '{{}}', 0, 1, 1);
            INSERT INTO memory_embeddings VALUES ('{LEARNED}', 'document', 'm', 'h', 1, x'00', 1);
            UPDATE memory_documents SET markdown_body = 'EDITED BY A TRIAL' WHERE document_id = '{SEEDED}';
        """)
        database.commit()
        database.close()

    def test_reset_restores_the_seeded_state_and_keeps_other_tables(self):
        with tempfile.TemporaryDirectory() as directory:
            db = Path(directory) / "netclaw.db"
            database = sqlite3.connect(db)
            database.executescript(memory_store_schema())
            database.close()
            self.assertEqual(0, self.seed(db).returncode)
            expected_body = self.rows(db, f"SELECT markdown_body FROM memory_documents WHERE document_id = '{SEEDED}'")
            self.make_learned_state(db)

            completed = self.seed(db, "--reset")
            self.assertEqual(0, completed.returncode, completed.stderr)

            self.assertEqual(load_fixture_ids(),
                             sorted(r[0] for r in self.rows(db, "SELECT document_id FROM memory_documents")))
            self.assertEqual(expected_body,
                             self.rows(db, f"SELECT markdown_body FROM memory_documents WHERE document_id = '{SEEDED}'"))
            # One search row for each seed: a reset must not leave duplicates.
            self.assertEqual(len(load_fixture_ids()), self.rows(db, "SELECT count(*) FROM memory_documents_fts")[0][0])
            for table in ("memory_records", "memory_records_fts", "memory_edges", "memory_checkpoints", "memory_embeddings"):
                self.assertEqual(0, self.rows(db, f"SELECT count(*) FROM {table}")[0][0], table)
            self.assertEqual(load_fixture_anchors(),
                             sorted(r[0] for r in self.rows(db, "SELECT anchor_id FROM memory_anchors")))
            self.assertEqual([("signalr/x",)], self.rows(db, "SELECT session_id FROM session_catalog"))

    def test_second_reset_changes_nothing(self):
        with tempfile.TemporaryDirectory() as directory:
            db = Path(directory) / "netclaw.db"
            database = sqlite3.connect(db)
            database.executescript(memory_store_schema())
            database.close()
            self.assertEqual(0, self.seed(db, "--reset").returncode)
            self.assertEqual(0, self.seed(db, "--reset").returncode)
            self.assertEqual(len(load_fixture_ids()), self.rows(db, "SELECT count(*) FROM memory_documents")[0][0])
            self.assertEqual(len(load_fixture_ids()), self.rows(db, "SELECT count(*) FROM memory_documents_fts")[0][0])

    def test_reset_of_a_database_without_the_memory_schema_fails_loudly(self):
        with tempfile.TemporaryDirectory() as directory:
            db = Path(directory) / "netclaw.db"
            sqlite3.connect(db).close()
            completed = self.seed(db, "--reset")
            self.assertNotEqual(0, completed.returncode)
            self.assertIn("memory_documents", completed.stderr)


# A stand-in for `docker`. It runs `docker cp` and `docker exec` on the host and
# maps the container paths to folders of the test: /home/netclaw/.netclaw to
# $FAKE_HOME and /tmp/ to $FAKE_TMP. `docker exec` keeps its stdin. It writes
# each call to $DOCKER_LOG. DOCKER_FAIL=1 makes every call fail.
DOCKER_SHIM = r'''
docker() {
    echo "docker $*" >> "$DOCKER_LOG"
    [[ "${DOCKER_FAIL:-0}" == "1" ]] && return 1
    local sub=$1; shift
    local a
    case $sub in
        cp) cp "$1" "$FAKE_TMP/$(basename "${2#*:}")" ;;
        exec)
            while [[ $1 == --* ]]; do case $1 in --user) shift 2 ;; *) shift ;; esac; done
            shift
            local -a mapped=()
            for a in "$@"; do
                case $a in
                    /home/netclaw/.netclaw*) a=$FAKE_HOME${a#/home/netclaw/.netclaw} ;;
                    /tmp/*) a=$FAKE_TMP/${a#/tmp/} ;;
                esac
                mapped+=("$a")
            done
            "${mapped[@]}" ;;
        *) return 99 ;;
    esac
}
'''


class IsolationTests(unittest.TestCase):
    def test_fixture_config_stops_distillation_and_passivation_with_keys_that_the_schema_knows(self):
        # Cross-boundary check: the keys and the values must match the schema
        # and the types that the daemon binds. A wrong key is rejected by
        # `additionalProperties: false` or is ignored by the binder.
        config = json.loads((EVALS / "fixtures" / "config" / "netclaw.json").read_text())
        schema = json.loads(SCHEMA.read_text())
        session_schema = schema["properties"]["Session"]
        tuning_schema = session_schema["properties"]["Tuning"]

        idle = config["Session"]["MemoryObserverIdleSeconds"]
        interval = config["Session"]["Tuning"]["MemoryDistillationTurnInterval"]
        self.assertIn("MemoryObserverIdleSeconds", session_schema["properties"])
        self.assertIn("MemoryDistillationTurnInterval", tuning_schema["properties"])
        self.assertGreaterEqual(idle, session_schema["properties"]["MemoryObserverIdleSeconds"]["minimum"])
        self.assertGreaterEqual(interval, tuning_schema["properties"]["MemoryDistillationTurnInterval"]["minimum"])
        # An idle time over the length of a full-suite run, and 0 for "off".
        self.assertGreaterEqual(idle, 86400)
        self.assertEqual(0, interval)
        # LlmSessionActor arms the passivation timer only for an IdleTimeout
        # above zero, and an idle passivation distills the session. The schema
        # names 00:00:00 as the value that disables the timeout. Zero does not
        # mean "passivate at once".
        self.assertIn("IdleTimeout", session_schema["properties"])
        self.assertEqual("00:00:00", config["Session"]["IdleTimeout"])
        self.assertIn("00:00:00 to disable", session_schema["properties"]["IdleTimeout"]["description"])

    def run_harness(self, root, body, *, fail=False, asset_root=None):
        """Run `body` with the docker shim. Returns the completed process."""
        asset_root = asset_root or root / "asset"
        (root / "tmp").mkdir(exist_ok=True)
        script = (f'source "{SCRIPT}"\n{DOCKER_SHIM}\n'
                  f'EVAL_ASSET_ROOT="{asset_root}"; EVAL_HOME="{root / "home"}"; REPO_ROOT="{EVALS.parent}"\n'
                  f'EVAL_CONTAINER_NAME=fake; FAKE_HOME="{root / "container"}"; FAKE_TMP="{root / "tmp"}"\n'
                  f'DOCKER_LOG="{root / "docker.log"}"; DOCKER_FAIL={1 if fail else 0}\n{body}')
        return subprocess.run(["bash", "-c", script], capture_output=True, text=True,
                              env={"PATH": os.environ["PATH"]})

    def agent_folders(self, root):
        fixtures = root / "asset" / "evals" / "fixtures" / "agents"
        fixtures.mkdir(parents=True)
        (fixtures / "headless-analyst.md").write_text("original role\n")
        agents = root / "container" / "agents"
        agents.mkdir(parents=True)
        return agents

    def test_restore_agent_fixtures_overwrites_an_edited_definition(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            agents = self.agent_folders(root)
            (agents / "headless-analyst.md").write_text("edited by the parent\n")
            completed = self.run_harness(root, "restore_agent_fixtures")
            self.assertEqual(0, completed.returncode, completed.stderr)
            self.assertEqual("original role\n", (agents / "headless-analyst.md").read_text())

    def test_restore_agent_fixtures_removes_files_that_a_trial_added(self):
        # Bug caught: a copy keeps an added file. A file with the same name
        # field as a fixture shadows it for every later trial.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            agents = self.agent_folders(root)
            (agents / "a-analyst.md").write_text("---\nname: headless-analyst\n---\n")
            (agents / "sub").mkdir()
            (agents / "sub" / "x.md").write_text("x")
            completed = self.run_harness(root, "restore_agent_fixtures")
            self.assertEqual(0, completed.returncode, completed.stderr)
            self.assertEqual(["headless-analyst.md"], sorted(p.name for p in agents.iterdir()))

    def test_restore_agent_fixtures_runs_in_the_container_as_root(self):
        # Bug caught: a host `cp` cannot write over a file that the daemon user
        # (uid 1654) owns, and the run stopped under `set -e` with no message.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.agent_folders(root)
            self.run_harness(root, "restore_agent_fixtures")
            calls = (root / "docker.log").read_text()
            self.assertIn("exec --interactive --user root fake sh -c", calls)

    def test_failed_restore_stops_the_run_with_a_message(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.agent_folders(root)
            completed = self.run_harness(root, "restore_agent_fixtures; echo NOT-REACHED", fail=True)
            self.assertEqual(2, completed.returncode)
            self.assertNotIn("NOT-REACHED", completed.stdout)
            self.assertIn("restore of the agent fixtures failed", completed.stderr)

    def test_reset_memory_store_removes_a_learned_memory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "container").mkdir()
            db = root / "container" / "netclaw.db"
            database = sqlite3.connect(db)
            database.executescript(memory_store_schema())
            database.close()
            seeded = subprocess.run([sys.executable, str(SEED_SCRIPT), "--db-path", str(db), "--fixtures",
                                     str(EVALS / "fixtures" / "eval-memories.json")], capture_output=True)
            self.assertEqual(0, seeded.returncode)
            database = sqlite3.connect(db)
            database.execute("INSERT INTO memory_anchors VALUES ('anchor:l','preference','a',NULL,'normal','auto',0.9,1,'active',1,1)")
            database.execute(f"INSERT INTO memory_documents(document_id, anchor_id, title, markdown_body, update_semantics, sensitivity, recall_mode, confidence, created_at, updated_at) VALUES ('{LEARNED}','anchor:l','t','b','merge-document','normal','auto',0.9,1,1)")
            database.commit()
            database.close()

            completed = self.run_harness(root, "EVAL_MEMORY_SEEDED=true; reset_memory_store", asset_root=EVALS.parent)
            self.assertEqual(0, completed.returncode, completed.stderr)
            database = sqlite3.connect(db)
            ids = [r[0] for r in database.execute("SELECT document_id FROM memory_documents")]
            database.close()
            self.assertEqual(load_fixture_ids(), sorted(ids))

    def test_reset_memory_store_does_nothing_when_the_store_was_never_seeded(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            completed = self.run_harness(root, "reset_memory_store")
            self.assertEqual(0, completed.returncode, completed.stderr)
            self.assertFalse((root / "docker.log").exists())

    def test_failed_memory_reset_stops_the_run_with_a_message(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            completed = self.run_harness(root, "EVAL_MEMORY_SEEDED=true; reset_memory_store; echo NOT-REACHED", fail=True)
            self.assertEqual(2, completed.returncode)
            self.assertNotIn("NOT-REACHED", completed.stdout)
            self.assertIn("memory reset before the trial failed", completed.stderr)

    def test_both_trial_loops_reset_before_the_setup_function_and_the_snapshot(self):
        # Wiring check. The reset must come before setup_<case>, which may set
        # up state of its own, and before the memory snapshot of the trial.
        for function in ("run_case", "run_multi_turn_case"):
            with self.subTest(function=function):
                body = subprocess.run(["bash", "-c", f'source "{SCRIPT}"; declare -f {function}'],
                                      capture_output=True, text=True, env={"PATH": os.environ["PATH"]}).stdout
                reset = body.index("reset_trial_state")
                setup = body.index('"$setup_fn" "$run"')
                snapshot_call = body.index("snapshot_trial_baseline")
                self.assertLess(reset, setup)
                self.assertLess(setup, snapshot_call)
                self.assertNotIn("restore_agent_fixtures", body)


if __name__ == "__main__":
    unittest.main()
