"""Exercise approval consumers with the canonical two serializer representations."""

import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import background_process_fixture as runtime
import background_process_evals as oracle
import test_background_process_evals as controls


WIRE = json.loads((Path(__file__).parent / "fixtures/child-runs/approval-prompt-wire.json").read_bytes())


def approval_record(cancel=False):
    record = controls.valid_approval(cancel)
    options = copy.deepcopy(WIRE["raw_observer_event"]["output"]["InteractionOptions"])
    prompt = next(event["output"] for event in record["events"] if event["output"]["Type"] == "tool_interaction")
    prompt["InteractionOptions"] = options
    next(row["data"] for row in record["journal"] if row["event_type"] == "ToolApprovalRequested")["options"] = [option["Key"] for option in options]
    return record


class ProcessPromptWireControls(unittest.TestCase):
    def test_canonical_web_prompt_allows_its_exact_answer_barrier(self):
        for case, action in [("child_run_approval_once", "approval-answer-ready"),
                             ("child_run_approval_cancel", "stale-answer-ready")]:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                fixture = runtime.ProcessFixture(Path(directory) / "relay")
                fixture.case, fixture.nonce = case, "owned"
                fixture.approval_prompt = copy.deepcopy(WIRE["web_control"])
                fixture.binding = {"paths": {"artifact_dir": "/home/netclaw/.netclaw/sessions/owned/artifacts"}}
                (Path(directory) / "home/data/sessions/owned/artifacts").mkdir(parents=True)
                data = {"call_id": WIRE["web_control"]["prompt"]["callId"],
                        "prompt_sequence": WIRE["web_control"]["prompt_sequence"]}
                with patch.dict(os.environ, {"EVAL_HOME": str(Path(directory) / "home")}):
                    try:
                        result = fixture.control(action, data)
                    except (KeyError, TypeError, AssertionError) as error:
                        self.fail("The canonical web prompt cannot reach its answer barrier: " + str(error))
                self.assertEqual(data["call_id"], result["approval_answer"]["call_id"])
                self.assertTrue(result["approval_answer"]["effect_absent"])

    def test_canonical_web_prompt_rejects_foreign_answer_identity(self):
        for field, value in [("call_id", "another-prompt"), ("prompt_sequence", 999)]:
            with self.subTest(field=field), tempfile.TemporaryDirectory() as directory:
                fixture = runtime.ProcessFixture(Path(directory) / "relay")
                fixture.approval_prompt = copy.deepcopy(WIRE["web_control"])
                data = {"call_id": WIRE["web_control"]["prompt"]["callId"],
                        "prompt_sequence": WIRE["web_control"]["prompt_sequence"]}
                data[field] = value
                with self.assertRaises((AssertionError, KeyError, TypeError)):
                    fixture.control("approval-answer-ready", data)
                self.assertIsNone(fixture.approval_answer)

    def test_canonical_raw_option_scalars_pass_both_complete_oracle_variants(self):
        for cancel in [False, True]:
            with self.subTest(cancel=cancel):
                try:
                    result = oracle.verify_process_trial(**approval_record(cancel))
                except (KeyError, TypeError, AssertionError) as error:
                    self.fail("The canonical raw options cannot reach the complete oracle: " + str(error))
                self.assertTrue(result["passed"])

    def test_raw_options_reject_changed_missing_and_nested_keys(self):
        for cancel in [False, True]:
            for fault in ["changed", "missing", "nested"]:
                with self.subTest(cancel=cancel, fault=fault):
                    record = approval_record(cancel)
                    options = next(event["output"] for event in record["events"]
                        if event["output"]["Type"] == "tool_interaction")["InteractionOptions"]
                    if fault == "changed":
                        options[0]["Key"] = "approve_everywhere"
                    elif fault == "missing":
                        options.pop(0)
                    else:
                        options[0]["Key"] = {"Value": "approve_once"}
                    with self.assertRaises((AssertionError, KeyError, TypeError)):
                        oracle.verify_process_trial(**record)


if __name__ == "__main__":
    unittest.main()
