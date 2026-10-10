"""Independent path and process identity controls for child process evidence."""

import unittest

import test_background_process_evals as controls


class ProcessScopeControls(unittest.TestCase):
    rejects = controls.ProcessEvidenceControls.rejects

    def test_child_artifact_path_cannot_escape_its_actual_scope(self):
        record = controls.valid_routed()
        artifact = record["snapshot"]["binding"]["paths"]["artifact_dir"] + "/../../foreign.txt"
        content = record["setup"]["artifact_bytes"]
        record["setup"]["artifact_path"] = artifact
        record["effects"]["files"] = {artifact: content}
        controls.replace_parent_call(record, "review", arguments={"Path": artifact})
        for message in record["requests"][0]["messages"]:
            for call in message.get("tool_calls", []):
                arguments = controls.json.loads(call["function"]["arguments"])
                arguments["Path"] = artifact
                call["function"]["arguments"] = controls.json.dumps(arguments)
            if message.get("tool_call_id") == "write":
                message["content"] = f"Successfully wrote {len(content)} bytes to {artifact}"
        record["receipt"]["delivery_observations"]["deliveries"][0]["terminal"]["output"] = artifact
        controls.normalize(record)
        self.rejects(record)

    def test_recovery_requires_nonempty_actual_process_start_identities(self):
        record = controls.valid_recovery()
        record["snapshot"]["crash"].update(old_start="", new_start="")
        self.rejects(record)


    def test_ingress_authority_anchors_all_downstream_copies(self):
        for key, value in [("TurnId", "forged-turn"), ("RequesterSenderId", "foreign")]:
            with self.subTest(key=key):
                record = controls.valid_routed()
                initial = next(row for row in record["journal"] if row["event_type"] == "InputAdmitted")
                forged = controls.copy.deepcopy(record["setup"]["authority"])
                forged[key] = value
                record["setup"]["authority"] = forged
                for row in record["journal"]:
                    if row is not initial and "authority" in row["data"]:
                        row["data"]["authority"] = controls.copy.deepcopy(forged)
                self.rejects(record)

    def test_initial_ingress_cannot_change_trust_or_requester_constraints(self):
        for key, value in [("RequesterSenderId", "foreign"), ("Boundary", "Untrusted"),
                             ("ChannelType", "slack"), ("SupportsInteractiveApproval", 0)]:
            with self.subTest(key=key):
                record = controls.valid_routed()
                record["setup"]["authority"][key] = value
                self.rejects(record)

    def test_two_original_inputs_require_actual_ingress_and_capture_coverage(self):
        for fault in ["ingress-absent", "capture-absent", "duplicate", "reversed"]:
            with self.subTest(fault=fault):
                record = controls.valid_routed()
                messages = record["requests"][-1]["messages"]
                if fault == "ingress-absent":
                    row = next(row for row in record["journal"] if row["event_type"] == "InputAdmitted")
                    record["journal"].remove(row)
                elif fault == "capture-absent":
                    messages.pop(1)
                elif fault == "duplicate":
                    messages.append(controls.copy.deepcopy(messages[0]))
                else:
                    messages[0], messages[1] = messages[1], messages[0]
                self.rejects(record)

    def test_cumulative_captures_and_canonical_nudge_envelope_remain_valid(self):
        record = controls.valid_routed()
        record["requests"][-1]["messages"].append({"role": "user", "content": "[system: A child result is ready.]"})
        record["requests"].append(controls.copy.deepcopy(record["requests"][-1]))
        self.assertTrue(controls.oracle.verify_process_trial(**record)["passed"])


if __name__ == "__main__":
    unittest.main()
