"""The consent subject must match the actual pending child response."""

import unittest

import test_background_process_evals as controls


class ConsentSubjectControls(unittest.TestCase):
    rejects = controls.ProcessEvidenceControls.rejects

    def test_pending_subject_and_display_pass_in_both_variants(self):
        for cancel in [False, True]:
            with self.subTest(cancel=cancel):
                self.assertTrue(controls.oracle.verify_process_trial(**controls.valid_approval(cancel))["passed"])

    def test_wrong_displayed_command_cannot_authorize_pending_subject(self):
        for cancel in [False, True]:
            record = controls.valid_approval(cancel)
            prompt = next(event for event in record["events"] if event["output"]["Type"] == "tool_interaction")
            prompt["output"]["InteractionDisplayText"] = "printf foreign > foreign.txt"
            self.rejects(record)

    def test_pending_call_id_arguments_and_actual_response_write_are_required(self):
        for cancel in [False, True]:
            for fault in ["call", "arguments", "cwd", "absent", "unwritten", "foreign-request", "duplicate"]:
                with self.subTest(cancel=cancel, fault=fault):
                    record = controls.valid_approval(cancel)
                    wires = record["snapshot"]["response_wires"]
                    call = {"id": "protected-shell", "function": {"name": "shell_execute", "arguments":
                        controls.json.dumps({**record["setup"]["shell_arguments"],
                            "_rationale": "Execute the assigned process eval step."})}}
                    if fault == "call":
                        call["id"] = "another-pending-call"
                    elif fault in {"arguments", "cwd"}:
                        args = controls.json.loads(call["function"]["arguments"])
                        args["Command" if fault == "arguments" else "WorkingDirectory"] = "foreign"
                        call["function"]["arguments"] = controls.json.dumps(args)
                    elif fault == "absent":
                        wires.clear()
                    elif fault == "unwritten":
                        record["snapshot"]["requests"][0]["response_payload_written"] = False
                    elif fault == "foreign-request":
                        wires[0]["request_id"] = record["receipt"]["delivery_observations"]["deliveries"][0]["request_id"]
                    else:
                        wires.append(controls.copy.deepcopy(wires[0]))
                    if fault in {"call", "arguments", "cwd"}:
                        wires[0]["wire"] = controls.pending_wire(call)
                    self.rejects(record)

    def test_full_durable_candidates_remain_facts_without_host_rederivation(self):
        record = controls.valid_approval()
        requested = next(row for row in record["journal"] if row["event_type"] == "ToolApprovalRequested")
        requested["data"]["candidates"] = [{"Verb": "printf", "Directory": "/project", "VerbTokens": ["printf"],
            "ShellAssignments": {"FIXTURE": "value"}}]
        self.assertTrue(controls.oracle.verify_process_trial(**record)["passed"])


if __name__ == "__main__":
    unittest.main()
