"""Oracle tests for the sub-agent eval checks and for trial isolation.

Each test builds a small trial from short excerpts of archived trials
(evals-cmp-2026-10-16, dev build) and runs the real assertion function of
run-evals.sh on it. A passing and a failing transcript pin each changed check.
The tests use no model, no network, no daemon, and no sleep. Each test removes
its temp folder.

Groups:

  PrecedenceTests    subagent_specialization_precedence. The parent task wins
                     over the role of the child. The check judges the child
                     and the parent apart and accepts a bold subject line.
  ScratchTests       subagent_session_scratch_disposable. The check reads the
                     child log below subagents/, the authorization records,
                     and the full child answer in the CLI session log.
  MemoryGuardTests   A trial that recalls a memory it did not seed is
                     inconclusive (trial_verdict.py and trial_evidence.py).
  IsolationTests     The fixture config stops memory distillation, and the
                     harness restores the agent fixtures.
"""

import json
import os
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import unittest

import trial_evidence
import trial_verdict

EVALS = Path(__file__).resolve().parent
SCRIPT = EVALS / "run-evals.sh"
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
    def check(self, *, stdout=SPAWN_CALL + DONE_LINE, child=BRIEF, outcome="completed", reply=BOLD_REPLY):
        with tempfile.TemporaryDirectory() as directory:
            raw = Path(directory) / "raw"
            raw.mkdir()
            (raw / "cli-stdout.txt").write_text(stdout)
            (raw / "cli-session.log").write_text(
                record("PROMPT", "Use spawn_agent with agent headless-analyst")
                + spawn_result(child, outcome) + record("ASSISTANT_FINAL", reply))
            completed = run_assertion(
                "assert_subagent_specialization_precedence",
                {"TRIAL_RAW": raw, "STDOUT_FILE": raw / "cli-stdout.txt"})
            return completed.returncode == 0

    def test_bold_subject_line_in_the_parent_reply_passes(self):
        # Bug caught: a check that wants a line that starts with "Subject:".
        for reply in (BOLD_REPLY, BOLD_INLINE_REPLY, PLAIN_REPLY):
            with self.subTest(reply=reply[:40]):
                self.assertTrue(self.check(reply=reply))

    def test_child_that_returns_its_brief_passes(self):
        self.assertTrue(self.check(child=BRIEF))

    def test_child_that_writes_the_email_passes(self):
        # Decision: the parent task wins over the role of the child. The prompt
        # asks the child for the email, so the email is correct behavior.
        self.assertTrue(self.check(child=EMAIL))

    def test_parent_that_edits_the_agent_file_fails(self):
        self.assertFalse(self.check(stdout=SPAWN_CALL.replace("spawn_agent", "file_list") + EDIT_LINE
                                    + SPAWN_CALL + DONE_LINE))

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


CALL_ID = "call_df35ee4e61003e9f3955c8df"
ENVELOPE = "signalr_7cae4b47d5ce446db00a5831ae85ca09-832f63371842ae02"
RUN_ID = "b813f43613c84fdaa7ad5bcc0519b1d4"
MANAGED_TMP = f"/home/netclaw/.netclaw/sessions/{ENVELOPE}/subagents/{RUN_ID}/tmp"

# Child log lines of dev run f7d6ac87, trial 01, without the Akka prefix.
CHILD_LOG = (
    "05:13:15.326 [INF] SubAgent [disposable-diagnostic] starting (tools=12)\n"
    "05:13:20.439 [INF] SubAgent [disposable-diagnostic] LLM response callId=1 kind=ToolCalls "
    f"toolCallPreview=shell_execute#{CALL_ID}(Command=python3 -c 'import tempfile; "
    "print(tempfile.gettempdir())', _rationale=Run the single assigned diagnostic.)\n"
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


class ScratchTests(unittest.TestCase):
    def check(self, *, child_log=CHILD_LOG, answer=f"```\n{MANAGED_TMP}\n```", reply=None, spawn_calls=None,
              child_runs=1, parent_log=PARENT_LOG, parent_newer=False):
        """Build the home folder of a trial and run the assertion. Returns True for a pass."""
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
                run_dir = envelope / "subagents" / (RUN_ID if index == 0 else f"{RUN_ID[:-1]}{index}") / "logs"
                run_dir.mkdir(parents=True)
                (run_dir / "session.log").write_text(child_log)
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

    def test_parent_log_is_not_taken_for_the_child_log(self):
        # Bug caught: the pattern */logs/session.log matches the parent log, and
        # `find` order picked it. The parent log is newer here, and the check
        # still reads the child log.
        self.assertTrue(self.check(parent_newer=True))

    def test_run_with_only_a_parent_log_fails(self):
        self.assertFalse(self.check(child_runs=0))

    def test_run_with_two_child_logs_fails(self):
        self.assertFalse(self.check(child_runs=2))

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


SESSION = "signalr/7cae4b47d5ce446db00a5831ae85ca09"
LEARNED = "doc-d73587c7f64d4474b8fb7770858b7506"
SEEDED = "doc-eval-travel"


def retrieval_line(session, count, items):
    return ("05:01:12.063 [INF] Netclaw.Actors.Sessions.SQLiteMemoryRecallCoordinator: "
            f"memory_retrieval_final session={session} mode=lexical injectedCount={count} filteredByFloor=1 "
            "appliedFloor=14.000 floorSource=n/a injectedChars=330 droppedByBudget=0 droppedByGate=0 "
            f"gateElapsedMs=0.0 gateScores= items={items}\n")


def write_trial(root, *, daemon_lines, before=None):
    """A finished trial with a passing assertion, plus the memory evidence."""
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
        (raw / "memory-before.txt").write_text("".join(f"{i}\n" for i in before))
    return Path(root)


class MemoryGuardTests(unittest.TestCase):
    def status(self, **kwargs):
        with tempfile.TemporaryDirectory() as directory:
            write_trial(directory, **kwargs)
            return trial_verdict.trial_status(directory)

    def test_recall_of_a_memory_from_an_earlier_trial_is_inconclusive(self):
        # Evidence: dev run e68e8d7e, trial 04 recalled doc-d73587c7... that
        # trial 01 had distilled. The assertion result does not matter.
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{LEARNED}=score20.600")],
                             before=[LEARNED])
        self.assertEqual(("inconclusive", "learned_memory_recalled"), (result["status"], result["reason"]))

    def test_recall_of_one_learned_memory_among_seeded_ones_is_inconclusive(self):
        items = f"{SEEDED}=score30.000|{LEARNED}=score22.600"
        result = self.status(daemon_lines=[retrieval_line(SESSION, 2, items)], before=[LEARNED])
        self.assertEqual("learned_memory_recalled", result["reason"])

    def test_recall_of_seeded_memories_only_is_a_pass(self):
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{SEEDED}=score30.000")],
                             before=[LEARNED])
        self.assertEqual("pass", result["status"])

    def test_memory_written_by_the_trial_itself_is_not_a_leak(self):
        # A memory_store call of this trial is not on the list taken before it.
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{LEARNED}=score20.600")], before=[])
        self.assertEqual("pass", result["status"])

    def test_candidate_that_recall_did_not_inject_is_not_a_leak(self):
        result = self.status(daemon_lines=[retrieval_line(SESSION, 0, "")], before=[LEARNED])
        self.assertEqual("pass", result["status"])

    def test_record_ids_count_too(self):
        record_id = "rec-0123456789abcdef0123456789abcdef"
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{record_id}=score20.000")],
                             before=[record_id])
        self.assertEqual("learned_memory_recalled", result["reason"])

    def test_trial_folder_of_an_older_harness_is_not_checked(self):
        result = self.status(daemon_lines=[retrieval_line(SESSION, 1, f"{LEARNED}=score20.600")])
        self.assertEqual("pass", result["status"])

    def test_missing_daemon_evidence_is_infra_not_a_clean_trial(self):
        result = self.status(daemon_lines=None, before=[LEARNED])
        self.assertEqual("inconclusive", result["status"])
        self.assertTrue(result["reason"].startswith("infra:evidence_unreadable"), result)

    def test_failing_assertion_with_a_leak_is_inconclusive_too(self):
        with tempfile.TemporaryDirectory() as directory:
            raw = write_trial(directory, daemon_lines=[retrieval_line(SESSION, 1, f"{LEARNED}=score20.600")],
                              before=[LEARNED]) / "raw"
            (raw / "assert.json").write_text(json.dumps({"exit_code": 1}))
            self.assertEqual("learned_memory_recalled", trial_verdict.trial_status(directory)["reason"])


class MemoryIdsTests(unittest.TestCase):
    def make_db(self, home, statements):
        (home / "data").mkdir(parents=True)
        database = sqlite3.connect(home / "data" / "netclaw.db")
        for statement in statements:
            database.execute(statement)
        database.commit()
        database.close()

    def run_command(self, home, dest):
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
            dest = Path(directory) / "memory-before.txt"
            self.assertEqual(0, self.run_command(home, dest))
            self.assertEqual([LEARNED, "rec-0123456789abcdef0123456789abcdef"], dest.read_text().split())

    def test_database_without_memory_tables_holds_no_memory(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory) / "home"
            self.make_db(home, ["CREATE TABLE sessions (x TEXT)"])
            dest = Path(directory) / "memory-before.txt"
            self.assertEqual(0, self.run_command(home, dest))
            self.assertEqual("", dest.read_text())

    def test_missing_or_unreadable_database_writes_no_file(self):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory) / "memory-before.txt"
            self.assertEqual(0, self.run_command(Path(directory) / "none", dest))
            self.assertFalse(dest.exists())
            home = Path(directory) / "home"
            (home / "data").mkdir(parents=True)
            (home / "data" / "netclaw.db").write_text("not a database")
            self.assertEqual(0, self.run_command(home, dest))
            self.assertFalse(dest.exists())


class IsolationTests(unittest.TestCase):
    def test_fixture_config_stops_memory_distillation_with_keys_that_the_schema_knows(self):
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
        self.assertEqual({"MemoryObserverIdleSeconds", "Tuning"}, set(config["Session"]))

    def test_restore_agent_fixtures_overwrites_an_edited_definition(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixtures = root / "asset" / "evals" / "fixtures" / "agents"
            fixtures.mkdir(parents=True)
            (fixtures / "headless-analyst.md").write_text("original role\n")
            agents = root / "home" / "data" / "agents"
            agents.mkdir(parents=True)
            (agents / "headless-analyst.md").write_text("edited by the parent\n")
            script = f'source "{SCRIPT}"; EVAL_ASSET_ROOT="{root / "asset"}"; EVAL_HOME="{root / "home"}"; restore_agent_fixtures'
            completed = subprocess.run(["bash", "-c", script], capture_output=True, text=True,
                                       env={"PATH": os.environ["PATH"]})
            self.assertEqual(0, completed.returncode, completed.stderr)
            self.assertEqual("original role\n", (agents / "headless-analyst.md").read_text())


if __name__ == "__main__":
    unittest.main()
