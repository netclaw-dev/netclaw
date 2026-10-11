"""Controls for actual process producer record shapes and final report review."""

import unittest

import test_background_process_evals as controls


def remove_parent_call(record, identifier):
    record["receipt"]["calls"] = [row for row in record["receipt"]["calls"] if row["id"] != identifier]
    record["events"] = [event for event in record["events"] if event["output"].get("CallId") != identifier]
    controls.normalize(record)


class ProcessRecordShapeControls(unittest.TestCase):
    rejects = controls.ProcessEvidenceControls.rejects

    def test_initial_and_probe_ingress_require_user_role(self):
        for index in range(2):
            record = controls.valid_routed()
            rows = [row for row in record["journal"] if row["event_type"] == "InputAdmitted"]
            rows[index]["data"]["user_role"] = "Assistant"
            self.rejects(record)

    def test_recovery_requires_its_separate_release_without_child_release(self):
        self.assertTrue(controls.oracle.verify_process_trial(**controls.valid_recovery())["passed"])
        for fault in ["absent", "false", "child-release"]:
            with self.subTest(fault=fault):
                record = controls.valid_recovery()
                if fault == "absent":
                    del record["snapshot"]["recovery_released"]
                elif fault == "false":
                    record["snapshot"]["recovery_released"] = False
                else:
                    record["snapshot"]["released"] = True
                self.rejects(record)

    def test_cancellation_requires_both_actual_report_read_and_final_status(self):
        for identifier in ["cancel-report", "final-status"]:
            with self.subTest(identifier=identifier):
                record = controls.valid_approval(True)
                remove_parent_call(record, identifier)
                self.rejects(record)

    def test_cancellation_requires_full_unaltered_report_bytes(self):
        for fault in ["absent", "changed", "partial"]:
            with self.subTest(fault=fault):
                record = controls.valid_approval(True)
                terminal = record["receipt"]["delivery_observations"]["deliveries"][0]["terminal"]
                path = terminal["artifact_directory"] + "/cancelled-results.json"
                if fault == "absent":
                    del record["effects"]["files"][path]
                elif fault == "changed":
                    report = controls.json.loads(record["effects"]["files"][path])
                    report["summary"] = "I claim another confirmed result."
                    body = controls.json.dumps(report).encode()
                    record["effects"]["files"][path] = body
                    controls.replace_parent_call(record, "cancel-report", result=body.decode())
                else:
                    controls.replace_parent_call(record, "cancel-report", arguments={"Path": path, "Limit": 1})
                self.rejects(record)

    def test_final_cancel_status_requires_canonical_terminal_and_scope(self):
        for field, value in [("run_id", "foreign"), ("scope_id", "foreign"), ("dispatch_closed", False),
                             ("state", "Completed"), ("terminal", {"state": "Cancelled"})]:
            with self.subTest(field=field):
                record = controls.valid_approval(True)
                row = next(row for row in record["receipt"]["calls"] if row["id"] == "final-status")
                body = controls.json.loads(row["result"])
                body[field] = value
                controls.replace_parent_call(record, "final-status", result=controls.json.dumps(body))
                self.rejects(record)

    def test_provider_requires_terminal_then_report_read_then_final_status(self):
        for fault in ["terminal-late", "status-before-report"]:
            with self.subTest(fault=fault):
                record = controls.valid_approval(True)
                messages = record["requests"][-1]["messages"]
                identifier = "delivery" if fault == "terminal-late" else "cancel-report"
                index = next(index for index, message in enumerate(messages)
                    if any(call["id"] == identifier for call in message.get("tool_calls", [])))
                pair = messages[index:index + 2]
                del messages[index:index + 2]
                messages.extend(pair)
                self.rejects(record)

    def test_rationale_metadata_preserves_semantic_shell_argument_checks(self):
        self.assertTrue(controls.oracle.verify_process_trial(**controls.valid_approval())["passed"])
        for key, value in [("Command", "printf foreign > foreign.txt"), ("WorkingDirectory", "/foreign")]:
            with self.subTest(key=key):
                record = controls.valid_approval()
                call = record["requests"][1]["messages"][-2]["tool_calls"][0]
                arguments = controls.json.loads(call["function"]["arguments"])
                arguments[key] = value
                call["function"]["arguments"] = controls.json.dumps(arguments)
                self.rejects(record)


if __name__ == "__main__":
    unittest.main()
