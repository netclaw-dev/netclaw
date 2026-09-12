"""Independent controls for cycle eval evidence and the provider relay."""

import copy
import io
import json
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import unittest

from background_fixture import handler_for
from cycle_evals import CASES, CORRECTION, STOP, CycleFixture, primary_receipts, verdict


def evidence(case="correction"):
    """Create an explicit contract example, not a fixture-generated snapshot."""
    ids = [f"script-{index}" for index in range(5 if case == "terminal" else 4)]
    expected = {"changed_result": 3, "metadata_repair": 1}.get(case, 2)
    recovery_path = "/isolated/recovery.txt"
    recovery_value = "value-from-the-real-tool"
    corrections = {} if case in {"changed_result", "metadata_repair"} else {ids[3]: CORRECTION}
    calls = [{"callId": call_id, "toolName": "load_tool" if index == 0 else "shell_execute",
              "argumentsJson": json.dumps({"Name": "shell_execute"} if index == 0 else {"Command": "primary"})}
             for index, call_id in enumerate(ids[:4])]
    last_result = "3" if case == "changed_result" else "cycle-stalled"
    tool_results = {ids[0]: "shell_execute", ids[1]: "cycle-stalled", ids[2]: "cycle-stalled",
                    **corrections}
    if case == "changed_result":
        tool_results.update({ids[1]: "1", ids[2]: "2", ids[3]: "3"})
    elif case == "metadata_repair":
        tool_results.update({ids[1]: "Error: _rationale is required.",
                             ids[2]: "Error: _rationale is required.", ids[3]: "cycle-stalled"})
    observed_calls = {}
    if case != "terminal":
        recovery = {"name": "file_read", "arguments": json.dumps({"Path": recovery_path})}
        observed_calls["real-recovery"] = recovery
        tool_results["real-recovery"] = recovery_value
        calls.append({"callId": "real-recovery", "toolName": recovery["name"],
                      "argumentsJson": recovery["arguments"]})
    snapshot = {"case": case, "effects": expected, "scripted_ids": ids,
                "main_requests": 6, "sidecar_requests": int(case == "compaction"), "model_requests": 1,
                "handoff": {"tools": [] if case == "terminal" else ["file_read"], "effects": expected,
                            "correction_ids": list(corrections), "stop_instruction": case == "terminal"},
                "tool_results": tool_results, "observed_calls": observed_calls,
                "recovery_path": recovery_path, "recovery_value": recovery_value}
    answer = {"status": "incomplete" if case == "terminal" else "complete", "completed_attempts": expected,
              "blocked_attempt_executed": False, "last_result": last_result,
              "recovered_value": "" if case == "terminal" else recovery_value}
    output = {"toolCalls": calls, "response": json.dumps(answer)}
    batch = "turn_tool_call_batch count=1 tools=shell_execute\n"
    actor_log = batch + ("Compaction complete (before=8, after=4)\n" if case == "compaction" else "") + batch
    return snapshot, output, actor_log


class CycleVerdictTests(unittest.TestCase):
    def assert_rejected(self, snapshot, output, actor_log):
        # Malformed evidence can fail loudly. It must not produce a passing verdict.
        try:
            result = verdict(snapshot, output, actor_log)
        except (ValueError, KeyError, TypeError, AttributeError, IndexError):
            return
        self.assertFalse(result["passed"], result)

    def test_positive_examples_cover_every_case(self):
        self.assertEqual({"correction", "terminal", "compaction", "changed_result", "metadata_repair"}, CASES)
        for case in CASES:
            with self.subTest(case=case):
                result = verdict(*evidence(case))
                self.assertTrue(result["passed"], result)

    def test_effect_count_must_match_before_and_after_model_handoff(self):
        for case in CASES:
            for target in ("effects", "handoff"):
                for count in (0, 4):
                    with self.subTest(case=case, target=target, count=count):
                        snapshot, output, log = evidence(case)
                        if target == "effects":
                            snapshot["effects"] = count
                        else:
                            snapshot["handoff"]["effects"] = count
                        self.assert_rejected(snapshot, output, log)

    def test_correction_must_match_the_third_request_exactly_once(self):
        for case in ("correction", "terminal", "compaction"):
            for defect in ("missing", "wrong_id", "extra"):
                with self.subTest(case=case, defect=defect):
                    snapshot, output, log = evidence(case)
                    if defect != "extra":
                        snapshot["tool_results"].pop("script-3")
                    if defect != "missing":
                        snapshot["tool_results"]["unrelated-call"] = CORRECTION
                    self.assert_rejected(snapshot, output, log)

    def test_permitted_controls_reject_any_cycle_correction(self):
        for case in ("changed_result", "metadata_repair"):
            with self.subTest(case=case):
                snapshot, output, log = evidence(case)
                snapshot["tool_results"]["script-3"] = CORRECTION
                self.assert_rejected(snapshot, output, log)

    def test_fixture_only_or_unbounded_model_activity_cannot_pass(self):
        for requests in (0, 9):
            snapshot, output, log = evidence()
            snapshot["model_requests"] = requests
            self.assert_rejected(snapshot, output, log)
        snapshot, output, log = evidence()
        snapshot["handoff"] = None
        self.assert_rejected(snapshot, output, log)

    def test_terminal_requires_no_tools_and_the_runtime_stop_instruction(self):
        for field, value in (("tools", ["file_read"]), ("stop_instruction", False)):
            with self.subTest(field=field):
                snapshot, output, log = evidence("terminal")
                snapshot["handoff"][field] = value
                self.assert_rejected(snapshot, output, log)

    def test_terminal_rejects_any_post_stop_call_or_result(self):
        for defect in ("call", "result"):
            with self.subTest(defect=defect):
                snapshot, output, log = evidence("terminal")
                if defect == "call":
                    output["toolCalls"].append({"callId": "script-4", "toolName": "shell_execute"})
                else:
                    snapshot["tool_results"]["script-4"] = "cycle-stalled"
                self.assert_rejected(snapshot, output, log)

    def test_recovery_requires_a_real_matching_call_and_result(self):
        for defect in ("missing_call", "wrong_path", "wrong_tool", "missing_result", "wrong_result", "script_id"):
            with self.subTest(defect=defect):
                snapshot, output, log = evidence()
                call = snapshot["observed_calls"]["real-recovery"]
                if defect == "missing_call":
                    snapshot["observed_calls"].clear()
                elif defect == "wrong_path":
                    call["arguments"] = json.dumps({"Path": "/another/file"})
                elif defect == "wrong_tool":
                    call["name"] = "file_write"
                elif defect == "missing_result":
                    snapshot["tool_results"].pop("real-recovery")
                elif defect == "wrong_result":
                    snapshot["tool_results"]["real-recovery"] = "Error: file not found"
                else:
                    snapshot["observed_calls"]["script-1"] = snapshot["observed_calls"].pop("real-recovery")
                    snapshot["tool_results"]["script-1"] = snapshot["recovery_value"]
                self.assert_rejected(snapshot, output, log)

    def test_recovery_call_must_also_exist_in_the_cli_transcript(self):
        snapshot, output, log = evidence()
        output["toolCalls"].pop()
        self.assert_rejected(snapshot, output, log)

    def test_recovery_cannot_hide_a_later_counter_reset(self):
        snapshot, output, log = evidence()
        output["toolCalls"].append({"callId": "counter-reset", "toolName": "file_write",
                                    "argumentsJson": json.dumps({"Path": "/isolated/attempts.txt",
                                                                 "Content": "attempt\nattempt\n"})})
        self.assert_rejected(snapshot, output, log)

    def test_duplicate_or_reordered_script_calls_fail(self):
        for defect in ("duplicate_ids", "reordered_output"):
            with self.subTest(defect=defect):
                snapshot, output, log = evidence()
                if defect == "duplicate_ids":
                    snapshot["scripted_ids"][2] = snapshot["scripted_ids"][1]
                else:
                    output["toolCalls"][1:3] = reversed(output["toolCalls"][1:3])
                self.assert_rejected(snapshot, output, log)

    def test_false_final_report_fails(self):
        for case in CASES:
            mutations = (("status", "complete" if case == "terminal" else "incomplete"),
                         ("completed_attempts", 99), ("completed_attempts", True),
                         ("blocked_attempt_executed", True), ("recovered_value", "invented"))
            for field, value in mutations:
                with self.subTest(case=case, field=field, value=value):
                    snapshot, output, log = evidence(case)
                    answer = json.loads(output["response"])
                    answer[field] = value
                    output["response"] = json.dumps(answer)
                    self.assert_rejected(snapshot, output, log)

    def test_last_result_cannot_be_fabricated_or_absent(self):
        for case in CASES:
            for absent in (False, True):
                with self.subTest(case=case, absent=absent):
                    snapshot, output, log = evidence(case)
                    answer = json.loads(output["response"])
                    if absent:
                        answer.pop("last_result")
                    else:
                        answer["last_result"] = "The blocked operation succeeded."
                    output["response"] = json.dumps(answer)
                    self.assert_rejected(snapshot, output, log)

    def test_compaction_requires_reduction_between_the_two_real_batches(self):
        batch = "turn_tool_call_batch count=1 tools=shell_execute\n"
        compacted = "Compaction complete (before=8, after=4)\n"
        for log in (batch * 2, compacted + batch * 2, batch * 2 + compacted,
                    batch + "Compaction complete (before=8, after=8)\n" + batch,
                    batch + compacted + batch + compacted,
                    batch + "Compaction threshold reached during tool loop\n" + batch):
            with self.subTest(log=log):
                snapshot, output, _ = evidence("compaction")
                self.assert_rejected(snapshot, output, log)
        snapshot, output, log = evidence("compaction")
        snapshot["sidecar_requests"] = 0
        self.assert_rejected(snapshot, output, log)

    def test_other_cases_reject_unplanned_compaction(self):
        for case in CASES - {"compaction"}:
            with self.subTest(case=case):
                snapshot, output, log = evidence(case)
                self.assert_rejected(snapshot, output, log + "Compaction complete (before=8, after=4)\n")

    def test_malformed_evidence_never_passes(self):
        mutations = (lambda s, o: s.update(scripted_ids=None),
                     lambda s, o: s.update(tool_results=None),
                     lambda s, o: s["observed_calls"]["real-recovery"].update(arguments="{"),
                     lambda s, o: o.update(toolCalls=[{}]),
                     lambda s, o: o.update(response="Fixture setup complete."),
                     lambda s, o: o.update(response="null"),
                     lambda s, o: o.update(response="{}"))
        for index, mutate in enumerate(mutations):
            with self.subTest(index=index):
                snapshot, output, log = evidence()
                mutate(snapshot, output)
                self.assert_rejected(snapshot, output, log)


class CycleFixtureTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.fixture = CycleFixture("http://127.0.0.1:1/v1", "unit-fixture", "", self.directory.name, 10000)

    def start(self, case="correction"):
        setup = self.fixture.control("cycle", {"case": case})
        return {"messages": [{"role": "user", "content": setup["prompt"]}],
                "tools": [{"function": {"name": name}} for name in ("load_tool", "shell_execute", "file_read")]}

    def acknowledge(self, request, reply, result, effects):
        call = reply["tool_calls"][0]
        request["messages"].extend([{"role": "assistant", "content": None, "tool_calls": [call]},
                                    {"role": "tool", "tool_call_id": call["id"], "content": result}])
        self.fixture.counter.write_text("attempt\n" * effects)

    def to_correction(self, case="correction"):
        request = self.start(case)
        load = self.fixture.completion(request)
        self.acknowledge(request, load, "shell_execute", 0)
        first = self.fixture.completion(request)
        self.acknowledge(request, first, "cycle-stalled", 1)
        second = self.fixture.completion(request)
        self.acknowledge(request, second, "cycle-stalled", 2)
        third = self.fixture.completion(request)
        self.acknowledge(request, third, CORRECTION, 2)
        return request

    def test_correction_handoff_requires_the_runtime_result_and_preserves_tools(self):
        request = self.to_correction()
        self.assertIsNone(self.fixture.completion(request))
        snapshot = self.fixture.snapshot()
        self.assertEqual(1, snapshot["model_requests"])
        self.assertEqual(2, snapshot["handoff"]["effects"])
        self.assertIn("file_read", snapshot["handoff"]["tools"])
        self.assertEqual(snapshot["scripted_ids"][3:4], snapshot["handoff"]["correction_ids"])

    def test_missing_correction_cannot_reach_the_real_model(self):
        request = self.to_correction()
        request["messages"][-1]["content"] = "cycle-stalled"
        with self.assertRaises(ValueError):
            self.fixture.completion(request)
        self.assertEqual(0, self.fixture.model_requests)

    def test_a_third_effect_cannot_reach_the_real_model(self):
        request = self.to_correction()
        self.fixture.counter.write_text("attempt\n" * 3)
        with self.assertRaises(ValueError):
            self.fixture.completion(request)
        self.assertEqual(0, self.fixture.model_requests)

    def test_terminal_handoff_requires_no_tools_and_rejects_their_return(self):
        request = self.to_correction("terminal")
        repeat = self.fixture.completion(request)
        self.assertEqual("shell_execute", repeat["tool_calls"][0]["function"]["name"])
        request["messages"].append({"role": "system", "content": STOP})
        with self.assertRaises(ValueError):
            self.fixture.completion(request)
        request["tools"] = []
        self.assertIsNone(self.fixture.completion(request))
        self.assertEqual([], self.fixture.handoff["tools"])
        request["tools"] = [{"function": {"name": "file_read"}}]
        with self.assertRaises(ValueError):
            self.fixture.completion(request)

    def test_terminal_without_the_stop_instruction_is_not_a_sidecar(self):
        request = self.to_correction("terminal")
        self.fixture.completion(request)
        request["tools"] = []
        with self.assertRaises(ValueError):
            self.fixture.completion(request)
        self.assertEqual(0, self.fixture.model_requests)

    def test_changed_result_control_executes_three_distinct_outcomes_before_handoff(self):
        request = self.start("changed_result")
        load = self.fixture.completion(request)
        self.acknowledge(request, load, "shell_execute", 0)
        commands = []
        for count in (1, 2, 3):
            reply = self.fixture.completion(request)
            commands.append(json.loads(reply["tool_calls"][0]["function"]["arguments"])["Command"])
            self.acknowledge(request, reply, str(count), count)
        self.assertEqual(1, len(set(commands)))
        self.assertIsNone(self.fixture.completion(request))
        self.assertEqual(3, self.fixture.handoff["effects"])
        self.assertEqual([], self.fixture.handoff["correction_ids"])

    def test_metadata_control_repairs_the_same_primary_arguments_without_a_correction(self):
        request = self.start("metadata_repair")
        load = self.fixture.completion(request)
        self.acknowledge(request, load, "shell_execute", 0)
        attempts = []
        for index in range(3):
            reply = self.fixture.completion(request)
            attempts.append(json.loads(reply["tool_calls"][0]["function"]["arguments"]))
            result = "cycle-stalled" if index == 2 else "Error: _rationale is required."
            self.acknowledge(request, reply, result, int(index == 2))
        self.assertNotIn("_rationale", attempts[0])
        self.assertEqual(attempts[0], attempts[1])
        self.assertTrue(attempts[2].pop("_rationale"))
        self.assertEqual(attempts[0], attempts[2])
        self.assertIsNone(self.fixture.completion(request))
        self.assertEqual(1, self.fixture.handoff["effects"])
        self.assertEqual([], self.fixture.handoff["correction_ids"])

    def test_sidecar_does_not_consume_setup_or_real_model_budget(self):
        request = self.start("compaction")
        before = copy.deepcopy(self.fixture.snapshot())
        sidecar = {"messages": [{"role": "system", "content": "You are a session summarizer."}], "tools": []}
        self.assertIsNone(self.fixture.completion(sidecar))
        after = self.fixture.snapshot()
        for field in ("main_requests", "model_requests", "scripted_ids", "tool_results", "handoff"):
            self.assertEqual(before[field], after[field], field)
        self.assertEqual(before["sidecar_requests"] + 1, after["sidecar_requests"])
        self.assertEqual("load_tool", self.fixture.completion(request)["tool_calls"][0]["function"]["name"])

    def test_script_does_not_advance_without_the_previous_call_result(self):
        request = self.start()
        first = self.fixture.completion(request)
        try:
            retried = self.fixture.completion(request)
        except ValueError:
            return
        self.assertEqual(first["tool_calls"], retried["tool_calls"])

    def test_compaction_does_not_replay_removed_script_ids(self):
        request = self.start("compaction")
        load = self.fixture.completion(request)
        self.acknowledge(request, load, "shell_execute", 0)
        first = self.fixture.completion(request)
        self.assertEqual(8000, first["_fixture_usage"]["prompt_tokens"])
        self.acknowledge(request, first, "cycle-stalled", 1)
        request["messages"] = [request["messages"][0], *request["messages"][-2:]]
        second = self.fixture.completion(request)
        self.assertNotIn("_fixture_usage", second)
        self.assertNotEqual(first["tool_calls"][0]["id"], second["tool_calls"][0]["id"])
        self.assertEqual(first["tool_calls"][0]["function"], second["tool_calls"][0]["function"])

    def test_compacted_ack_requires_the_first_effect_and_observer_without_a_forged_receipt(self):
        request = self.start("compaction")
        load = self.fixture.completion(request)
        self.acknowledge(request, load, "shell_execute", 0)
        first = self.fixture.completion(request)
        first_id = first["tool_calls"][0]["id"]
        self.fixture.counter.write_text("attempt\n")
        observer = {"messages": [{"role": "system", "content": "You are a session summarizer."}]}
        self.assertIsNone(self.fixture.completion(observer))
        resumed = {"messages": [{"role": "user", "content": "Compacted observations."}],
                   "tools": request["tools"]}
        second = self.fixture.completion(resumed)
        self.assertNotEqual(first_id, second["tool_calls"][0]["id"])
        self.assertTrue(self.fixture.snapshot()["compacted_ack_used"])
        self.assertNotIn(first_id, self.fixture.snapshot()["tool_results"])
        self.fixture.counter.write_text("attempt\nattempt\n")
        with self.assertRaises(ValueError):
            self.fixture.completion(resumed)

    def test_compacted_ack_exception_rejects_wrong_case_phase_effect_or_absent_observer(self):
        observer = {"messages": [{"role": "system", "content": "You are a session summarizer."}]}
        for defect in ("wrong_case", "early_observer", "wrong_effect", "no_observer"):
            with self.subTest(defect=defect):
                request = self.start("correction" if defect == "wrong_case" else "compaction")
                load = self.fixture.completion(request)
                if defect == "early_observer":
                    self.assertIsNone(self.fixture.completion(observer))
                self.acknowledge(request, load, "shell_execute", 0)
                first = self.fixture.completion(request)
                self.fixture.counter.write_text("attempt\n" * (2 if defect == "wrong_effect" else 1))
                if defect in ("wrong_case", "wrong_effect"):
                    self.assertIsNone(self.fixture.completion(observer))
                resumed = {"messages": [{"role": "user", "content": "Compacted observations."}],
                           "tools": request["tools"]}
                with self.assertRaises(ValueError):
                    self.fixture.completion(resumed)
                self.assertFalse(self.fixture.snapshot()["compacted_ack_used"])
                self.assertNotIn(first["tool_calls"][0]["id"], self.fixture.snapshot()["tool_results"])

    def test_stale_requests_cannot_consume_a_new_trial(self):
        old = self.start()
        old_initial = copy.deepcopy(old)
        load = self.fixture.completion(old)
        self.acknowledge(old, load, "shell_execute", 0)
        only_result = {"messages": [old["messages"][-1]], "tools": old["tools"]}
        for stale in (old, only_result, old_initial):
            with self.subTest(messages=stale["messages"]):
                current = self.start()
                before = copy.deepcopy(self.fixture.snapshot())
                with self.assertRaises(ValueError):
                    self.fixture.completion(stale)
                after = self.fixture.snapshot()
                for field in ("scripted_ids", "main_requests", "model_requests", "tool_results"):
                    self.assertEqual(before[field], after[field], field)
                self.assertEqual("load_tool", self.fixture.completion(current)["tool_calls"][0]["function"]["name"])

    def test_request_budgets_fail_loudly(self):
        request = self.to_correction()
        self.assertIsNone(self.fixture.completion(request))
        self.fixture.model_requests = 8
        with self.assertRaises(ValueError):
            self.fixture.completion(request)
        self.fixture.main_requests = 16
        with self.assertRaises(ValueError):
            self.fixture.completion(request)
        sidecar = {"messages": [{"role": "system", "content": "You are a session summarizer."}]}
        self.fixture.sidecar_requests = 8
        with self.assertRaises(ValueError):
            self.fixture.completion(sidecar)


class CycleUsageWireTests(unittest.TestCase):
    def test_usage_stays_out_of_the_message_and_has_one_accounting_record(self):
        fixture = type("FixtureIdentity", (), {"model": "unit-fixture"})()
        handler_type = handler_for(fixture)
        for stream in (False, True):
            with self.subTest(stream=stream):
                handler = object.__new__(handler_type)
                handler.wfile = io.BytesIO()
                handler.send_response = lambda *_: None
                handler.send_header = lambda *_: None
                handler.end_headers = lambda: None
                usage = {"prompt_tokens": 8000, "completion_tokens": 1, "total_tokens": 8001}
                handler.reply({"stream": stream}, {"role": "assistant", "content": "setup", "_fixture_usage": usage})
                wire = handler.wfile.getvalue().decode()
                self.assertNotIn("_fixture_usage", wire)
                chunks = ([json.loads(line[6:]) for line in wire.splitlines()
                           if line.startswith("data: ") and line != "data: [DONE]"] if stream else [json.loads(wire)])
                self.assertEqual([usage], [chunk["usage"] for chunk in chunks if chunk.get("usage") is not None])
                if stream:
                    self.assertTrue(wire.endswith("data: [DONE]\n\n"))


class PrimaryReceiptTests(unittest.TestCase):
    @staticmethod
    def record(message):
        return "[2026-01-01T00:00:00.0000000+00:00] " + message + "\n"

    def primary_log(self, case):
        snapshot, _, _ = evidence(case)
        success = "Exit code: 0\ncycle-stalled\n"
        correction = ("Netclaw stopped this tool batch because it would continue a repeated action-and-outcome cycle. "
                      "The same sequence completed twice without a changed result. No requested call executed.\n"
                      "Next action: choose a different action, load a missing tool, or finish the task.")
        rejected = ("Error: Required meta argument '_rationale' must be a non-empty string. "
                    "Supply one sentence that states the tool call intent. The tool was NOT executed.")
        results = ([f"Exit code: 0\n{count}\n" for count in (1, 2, 3)] if case == "changed_result" else
                   [rejected, rejected, success] if case == "metadata_repair" else [success, success, correction])
        records = [self.record(f"TOOL_RESULT: shell_execute call_id={call_id} result={result}")
                   for call_id, result in zip(snapshot["scripted_ids"][1:4], results)]
        return snapshot, records

    def test_exact_primary_receipts_pass_for_all_cases_without_provider_history(self):
        for case in CASES:
            with self.subTest(case=case):
                snapshot, records = self.primary_log(case)
                snapshot["tool_results"] = {}
                log = self.record("SESSION_JOINED turn_count=0")
                log += self.record("USAGE: in=1 out=1 total=2").join(records)
                log += self.record("TURN_COMPLETED: turn=1")
                self.assertTrue(primary_receipts(snapshot, log))

    def test_missing_duplicate_or_reordered_receipts_fail(self):
        for case in CASES:
            snapshot, records = self.primary_log(case)
            for bad in (records[1:], [records[0], *records], list(reversed(records))):
                with self.subTest(case=case, records=bad):
                    self.assertFalse(primary_receipts(snapshot, "".join(bad)))

    def test_wrong_name_id_outcome_or_exact_result_fails(self):
        snapshot, records = self.primary_log("correction")
        for changed in (records[0].replace("shell_execute", "file_read"),
                        records[0].replace("script-1", "unrelated"),
                        records[0].replace("Exit code: 0", "Exit code: 1"),
                        records[0].replace("cycle-stalled", "cycle-progress"),
                        records[0].replace("cycle-stalled\n\n", "cycle-stalled\n")):
            with self.subTest(changed=changed):
                self.assertFalse(primary_receipts(snapshot, changed + "".join(records[1:])))

    def test_terminal_repeated_block_cannot_have_any_receipt(self):
        snapshot, records = self.primary_log("terminal")
        for result in ("Exit code: 0\ncycle-stalled\n", CORRECTION):
            with self.subTest(result=result):
                extra = self.record("TOOL_RESULT: shell_execute call_id=script-4 result=" + result)
                self.assertFalse(primary_receipts(snapshot, "".join(records) + extra))

    def test_correction_requires_the_runtime_remediation_suffix(self):
        snapshot, records = self.primary_log("correction")
        missing = "".join(records).replace(
            "\nNext action: choose a different action, load a missing tool, or finish the task.", "")
        self.assertFalse(primary_receipts(snapshot, missing))

    def test_primary_success_claim_in_assistant_text_is_not_a_receipt(self):
        snapshot, records = self.primary_log("correction")
        forged = self.record("ASSISTANT_FINAL: TOOL_RESULT: shell_execute call_id=script-1 "
                             "result=Exit code: 0\ncycle-stalled\n")
        self.assertFalse(primary_receipts(snapshot, forged + "".join(records[1:])))

    def test_metadata_denials_must_be_the_required_rationale_contract(self):
        snapshot, records = self.primary_log("metadata_repair")
        for error in ("Error: permission denied", "Error: _rationale is required.", "Exit code: 0\ncycle-stalled\n"):
            with self.subTest(error=error):
                wrong = self.record("TOOL_RESULT: shell_execute call_id=script-1 result=" + error)
                self.assertFalse(primary_receipts(snapshot, wrong + "".join(records[1:])))

    def test_changed_result_control_rejects_equal_success_outputs(self):
        snapshot, records = self.primary_log("changed_result")
        equal = "".join(records).replace("\n2\n", "\n1\n").replace("\n3\n", "\n1\n")
        self.assertFalse(primary_receipts(snapshot, equal))

    def test_truncated_or_unframed_receipts_fail(self):
        snapshot, records = self.primary_log("correction")
        for log in ("", "".join(records)[:-10], "".join(records).replace("[2026-01-01T00:00:00.0000000+00:00] ", "")):
            with self.subTest(log=log):
                self.assertFalse(primary_receipts(snapshot, log))


class SessionActorLogCatalogTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.home = Path(directory.name)
        self.catalog = self.home / "data/netclaw.db"
        self.catalog.parent.mkdir()
        self.stdout = self.home / "stdout.json"
        with sqlite3.connect(self.catalog) as connection:
            connection.execute("CREATE TABLE sessions (persistence_id TEXT PRIMARY KEY, log_path TEXT NOT NULL)")

    def resolve(self, session_id):
        self.stdout.write_text(json.dumps({"sessionId": session_id}))
        script = Path(__file__).resolve().with_name("run-evals.sh")
        return subprocess.run(["bash", "-c", 'source "$1"; EVAL_HOME="$2"; STDOUT_FILE="$3"; '
                               'stdout_json_session_actor_log_path', "catalog-test", str(script),
                               str(self.home), str(self.stdout)], capture_output=True, text=True, timeout=10)

    def test_catalog_path_wins_over_session_id_and_quotes_are_literal(self):
        session_id = "operator's/session"
        canonical = self.home / "data/sessions/generated-storage-root/logs/session.log"
        canonical.parent.mkdir(parents=True)
        canonical.write_text("canonical")
        legacy = self.home / "data/sessions" / session_id.replace("/", "_") / "logs/session.log"
        legacy.parent.mkdir(parents=True)
        legacy.write_text("obsolete")
        with sqlite3.connect(self.catalog) as connection:
            connection.execute("INSERT INTO sessions VALUES (?, ?)",
                               ("session-" + session_id,
                                "/home/netclaw/.netclaw/sessions/generated-storage-root/logs/session.log"))
        before = self.catalog.read_bytes()
        result = self.resolve(session_id)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(str(canonical), result.stdout.strip())
        self.assertEqual(before, self.catalog.read_bytes())
        injection = self.resolve("' OR 1=1 --")
        self.assertNotEqual(0, injection.returncode)
        self.assertEqual("", injection.stdout)

    def test_missing_files_and_foreign_catalog_roots_fail(self):
        for path in ("/home/netclaw/.netclaw/sessions/absent/logs/session.log", "/another/root/session.log"):
            with self.subTest(path=path):
                with sqlite3.connect(self.catalog) as connection:
                    connection.execute("INSERT OR REPLACE INTO sessions VALUES (?, ?)", ("session-test", path))
                result = self.resolve("test")
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("", result.stdout)


if __name__ == "__main__":
    unittest.main()
