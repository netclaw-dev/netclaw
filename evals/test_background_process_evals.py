"""Independent controls for the fixed child process evidence contract."""

import copy
import importlib.util
import json
import os
from pathlib import Path
import unittest


INTERFACE = os.environ.get("BACKGROUND_PROCESS_INTERFACE")


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


if INTERFACE:
    interface_root = Path(INTERFACE)
    oracle = load("independent_process_oracle", interface_root / "background_process_evals.py")
    fixture = load("independent_process_fixture", interface_root / "valid_fixture.py")
else:
    import background_process_evals as oracle
    import process_evidence_fixture as fixture


def valid_routed():
    record = copy.deepcopy(fixture.candidate_valid())
    artifact = record["setup"]["artifact_path"]
    content = record["setup"]["artifact_bytes"]
    write_result(record)["content"] = f"Successfully wrote {len(content)} bytes to {artifact}"
    return record


def write_result(record):
    return next(message for message in record["requests"][0]["messages"]
                if message.get("role") == "tool" and message.get("tool_call_id") == "write")


def replace_parent_call(record, identifier, arguments=None, result=None):
    row = next(row for row in record["receipt"]["calls"] if row["id"] == identifier)
    if arguments is not None:
        row["arguments"] = arguments
    if result is not None:
        row["result"] = result
    for event in record["events"]:
        dto = event["output"]
        if dto.get("CallId") != identifier:
            continue
        if dto["Type"] == "tool_call":
            dto["ArgumentsJson"] = json.dumps(row["arguments"])
        elif dto["Type"] == "tool_result":
            dto["Result"] = row["result"]
    for request in record["requests"]:
        for message in request["messages"]:
            for call in message.get("tool_calls", []):
                if call["id"] == identifier:
                    call["function"]["arguments"] = json.dumps(row["arguments"])
            if message.get("tool_call_id") == identifier:
                message["content"] = row["result"]


def provider_pair(identifier, name, arguments, result):
    return [{"role": "assistant", "tool_calls": [{"id": identifier, "function": {
        "name": name, "arguments": json.dumps(arguments)}}]},
        {"role": "tool", "tool_call_id": identifier, "content": result}]


def normalize(record):
    """Keep independent observer, journal, and relay domains internally coherent."""
    turn = 1
    by_id = {row["id"]: row for row in record["receipt"]["calls"]}
    for sequence, event in enumerate(record["events"], 1):
        event["sequence"], event["observed_ns"] = sequence, sequence * 10
        dto = event["output"]
        if dto["Type"] == "tool_call":
            row = by_id[dto["CallId"]]
            row["turn"], row["observed_ns"] = turn, sequence * 10
        elif dto["Type"] == "turn_completed":
            turn += 1
    turns = [event for event in record["events"] if event["output"]["Type"] == "turn_completed"]
    record["receipt"]["first_turn_ns"] = turns[0]["observed_ns"]
    record["receipt"]["second_turn_ns"] = turns[1]["observed_ns"]
    record["receipt"]["completed_turns"] = len(turns)
    for ordinal, row in enumerate(record["receipt"]["calls"], 1):
        row["occurrence"] = ordinal
    consumed = record["receipt"]["delivery_observations"]["deliveries"][0]
    consumed["parent_boundary_ns"] = turns[-1]["observed_ns"]
    terminal = consumed["terminal"]
    messages = [copy.deepcopy(message) for message in record["requests"][-1]["messages"]
                if message.get("role") == "user"]
    operation = next(row for row in record["journal"] if row["event_type"] == "ChildRunAccepted")["data"]["source_operation"]
    delivery_pair = provider_pair("delivery", operation, {"run_id": terminal["run_id"],
        "source_operation": operation}, json.dumps(terminal))
    emitted = False
    for row in record["receipt"]["calls"]:
        if row["turn"] >= 3 and not emitted:
            messages.extend(delivery_pair)
            emitted = True
        messages.extend(provider_pair(row["id"], row["name"], row["arguments"], row["result"]))
    if not emitted:
        messages.extend(delivery_pair)
    record["requests"][-1] = {"messages": messages}
    for row in record["journal"]:
        if "terminal" in row["data"]:
            row["data"]["terminal"] = copy.deepcopy(terminal)
    stages = {row["data"].get("kind"): row["sequence_nr"] for row in record["journal"]}
    session, run = record["receipt"]["session_id"], terminal["run_id"]
    accepted = next(row["sequence_nr"] for row in record["journal"] if row["event_type"] == "ChildRunAccepted")
    record["log"] = "\n".join("child_run_" + name + " owner=" + session + " runId=" + run +
        " journalSequence=" + str(seq) + (" inputId=child-input callId=delivery" if name == "delivery_admitted" else "")
        for name, seq in [("accepted", accepted), ("terminal_recorded", stages["TerminalRecorded"]),
            ("result_prepared", stages["ResultPrepared"]), ("delivery_admitted", stages["DeliveryAdmitted"])])
    return record


def process_variant(case):
    record = valid_routed()
    record["case"] = record["setup"]["case"] = record["snapshot"]["case"] = case
    start = record["receipt"]["calls"][0]
    start["name"] = "spawn_agent"
    for event in record["events"]:
        if event["output"].get("CallId") == "start":
            event["output"]["ToolName"] = "spawn_agent"
    next(row for row in record["journal"] if row["event_type"] == "ChildRunAccepted")["data"]["source_operation"] = "spawn_agent"
    record["receipt"]["delivery_observations"]["deliveries"][0]["terminal"]["source_operation"] = "spawn_agent"
    return normalize(record)


def journal_row(record, event_type, **data):
    return {"persistence_id": "session-" + record["receipt"]["session_id"], "sequence_nr": 0,
        "serializer_id": 150, "manifest": oracle.MANIFESTS[event_type], "event_type": event_type,
        "data": {"session_id": record["receipt"]["session_id"], **data}}


def pending_wire(call):
    return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n" + json.dumps({
        "choices": [{"message": {"role": "assistant", "tool_calls": [call]}}]})


def valid_approval(cancel=False):
    case = "child_run_approval_cancel" if cancel else "child_run_approval_once"
    record = process_variant(case)
    session = record["receipt"]["session_id"]
    run = record["receipt"]["accepted_run"]["run_id"]
    authority = record["setup"]["authority"]
    authority["SupportsInteractiveApproval"] = True
    authority["ChannelType"] = "tui"
    if "ingress" in record["setup"]:
        record["setup"]["ingress"]["SupportsInteractiveApproval"] = True
        record["setup"]["ingress"]["ChannelType"] = "tui"
    record["setup"].update(shell_cwd="/project", candidates=[{"Verb": "printf", "Directory": "/project",
        "VerbTokens": ["printf"]}], shell_arguments={"Command": "printf authorized > effect.txt", "WorkingDirectory": "/project"},
        shell_result="authorized", effect_path="/project/effect.txt", effect_bytes=b"authorized", grant_bytes=b"{}")
    prompt = {"sequence": 0, "observed_ns": 0, "output": {"Type": "tool_interaction", "SessionId": session,
        "ToolName": "shell_execute", "CallId": "approval-prompt", "RequesterSenderId": authority["RequesterSenderId"],
        "InteractionCwd": "/project", "InteractionDisplayText": record["setup"]["shell_arguments"]["Command"], "InteractionOptions": [{"Key": "approve_once"}]}}
    first = next(i for i, event in enumerate(record["events"]) if event["output"]["Type"] == "turn_completed")
    record["events"].insert(first + 1, prompt)
    requested = journal_row(record, "ToolApprovalRequested", run_id=run, original_call_id="protected-shell",
        call_id="approval-prompt", attempt_id="attempt-1", requester=authority["RequesterSenderId"],
        cwd="/project", options=["approve_once"], authority=copy.deepcopy(authority), candidates=record["setup"]["candidates"])
    resolved = journal_row(record, "ToolApprovalResolved", run_id=run, call_id="approval-prompt",
        attempt_id="attempt-1", decision="Denied" if cancel else "ApprovedOnce")
    terminal_index = next(index for index, row in enumerate(record["journal"])
        if row["data"].get("kind") == "TerminalRecorded")
    before = record["journal"][:terminal_index]
    after = record["journal"][terminal_index:]
    extra = []
    if cancel:
        extra = [journal_row(record, "ChildRunEvent", run_id=run, kind=kind)
                 for kind in ["CancellationRequested", "DispatchClosed"]]
        terminal = record["receipt"]["delivery_observations"]["deliveries"][0]["terminal"]
        terminal.update(state="Cancelled", outcome="Cancelled", reason="cancelled_by_parent")
        replace_parent_call(record, "status", arguments={"RunId": run, "Cancel": True})
        for _ in range(2):
            record["events"].append({"sequence": 0, "observed_ns": 0, "output": {
                "Type": "text", "SessionId": session, "Text": oracle.EXPIRED_NOTICE}})
    else:
        child = copy.deepcopy(record["requests"][0])
        child["messages"].extend(provider_pair("protected-shell", "shell_execute", {**record["setup"]["shell_arguments"], "_rationale": "Execute the assigned process eval step."}, "authorized"))
        record["requests"].insert(1, child)
        consumed = record["receipt"]["delivery_observations"]["deliveries"][0]
        consumed["request_id"] = 3
        record["snapshot"]["requests"][-1]["request_id"] = 3
        record["snapshot"]["requests"].insert(1, {"request_id": 2, "held": False, "child": True,
            "admitted_ns": 960, "response_first_payload_ns": 980, "response_payload_written": True})
        record["snapshot"].update(approval_answer={"call_id": "approval-prompt", "effect_absent": True, "request_id": 1},
            protected_result_request_id=2)
    record["journal"] = before + [requested] + extra + [resolved] + after
    for sequence, row in enumerate(record["journal"], 1):
        row["sequence_nr"] = sequence
    if cancel:
        add_cancellation_review(record)
    normalize(record)
    prompt_sequence = prompt["sequence"]
    second_boundary = next(event["sequence"] for event in record["events"]
        if event["observed_ns"] == record["receipt"]["second_turn_ns"])
    data = {"call_id": "approval-prompt", "selected_key": "approve_once", "prompt_sequence": prompt_sequence}
    actions = []
    after_event = second_boundary
    if cancel:
        notices = [event["sequence"] for event in record["events"] if event["output"]["Type"] == "text"]
        actions.append({"kind": "cancellation_notice_consumed", "data": {"notice_sequence": notices[0]},
                        "after_event_sequence": notices[0]})
        after_event = notices[0]
    actions.extend({"kind": kind, "data": copy.deepcopy(data), "after_event_sequence": after_event}
        for kind in ["answer_started", "answer_completed"])
    for sequence, action in enumerate(actions, 1):
        action.update(sequence=sequence, observed_ns=10000 + sequence)
    record["actions"] = actions
    pending = {"id": "protected-shell", "type": "function", "function": {"name": "shell_execute",
        "arguments": json.dumps({**record["setup"]["shell_arguments"],
            "_rationale": "Execute the assigned process eval step."})}}
    record["snapshot"]["response_wires"] = [{"request_id": 1, "wire": pending_wire(pending)}]
    record["effects"].update(before_answer={"/project/effect.txt": None},
        after_terminal={"/project/effect.txt": None if cancel else b"authorized"}, grants_before=b"{}", grants_after=b"{}")
    return record


def add_cancellation_review(record):
    terminal = record["receipt"]["delivery_observations"]["deliveries"][0]["terminal"]
    artifact = record["setup"]["artifact_path"]
    result = f"Successfully wrote {len(record['setup']['artifact_bytes'])} bytes to {artifact}"
    activity = {"ConfirmedChangedFiles": [artifact]}
    terminal["checkpoint"] = {"CompletedRound": 1, "Summary": result, "ConfirmedActivity": activity}
    report_path = terminal["artifact_directory"] + "/cancelled-results.json"
    terminal["output"] = artifact + "\n" + report_path
    body = json.dumps({"run_id": terminal["run_id"], "state": "Cancelled", "summary": result,
        "confirmed_activity": activity, "external_effects":
        "Recorded receipts describe known local results. They do not prove external effects stopped."}).encode()
    record["effects"]["files"][report_path] = body
    append_review_call(record, "cancel-report", "file_read", {"Path": report_path}, body.decode())
    final_status = {**record["receipt"]["accepted_run"], "state": "Cancelled", "dispatch_closed": True,
        "cancellation_requested": True, "terminal": json.dumps(terminal)}
    append_review_call(record, "final-status", "check_agent_run", {"RunId": terminal["run_id"], "Cancel": False},
        json.dumps(final_status))


def append_review_call(record, identifier, name, arguments, result):
    boundary = next(index for index, event in enumerate(record["events"])
        if event["observed_ns"] == record["receipt"]["delivery_observations"]["deliveries"][0]["parent_boundary_ns"])
    session = record["receipt"]["session_id"]
    record["events"][boundary:boundary] = [
        {"sequence": 0, "observed_ns": 0, "output": {"Type": "tool_call", "SessionId": session,
            "CallId": identifier, "ToolName": name, "ArgumentsJson": json.dumps(arguments)}},
        {"sequence": 0, "observed_ns": 0, "output": {"Type": "tool_result", "SessionId": session,
            "CallId": identifier, "ToolName": name, "Result": result, "ToolFailureCode": None}}]
    record["receipt"]["calls"].append({"id": identifier, "name": name, "arguments": arguments, "result": result,
        "success": True, "failure_code": None, "turn": 3, "occurrence": 0, "observed_ns": 0})
    normalize(record)


def valid_recovery():
    record = process_variant("child_run_owner_recovery")
    terminal = record["receipt"]["delivery_observations"]["deliveries"][0]["terminal"]
    terminal.update(state="Lost", outcome="Failed", reason="owner_restart_lost", warning="The prior owner stopped.")
    record["snapshot"].update(released=False, recovery_released=True)
    record["snapshot"]["crash"] = {"signal": "SIGKILL", "old_pid": 4101, "old_start": "old-start",
        "old_exited": True, "new_pid": 4102, "new_start": "new-start", "new_ready": True}
    record["events"].insert(6, {"sequence": 0, "observed_ns": 0, "output": {
        "Type": "session_joined", "SessionId": record["receipt"]["session_id"]}})
    normalize(record)
    joined = next(event["sequence"] for event in record["events"] if event["output"]["Type"] == "session_joined")
    record["actions"] = [{"sequence": i, "observed_ns": 10000 + i, "kind": kind,
        "data": {"session_id": record["receipt"]["session_id"]}, "after_event_sequence": bound}
        for i, (kind, bound) in enumerate([("resume_started", joined - 1), ("resume_completed", joined)], 1)]
    return record


class ProcessEvidenceControls(unittest.TestCase):
    def rejects(self, record):
        with self.assertRaises((AssertionError, ValueError)):
            oracle.verify_process_trial(**record)

    def test_valid_routed_record_and_cumulative_capture_pass(self):
        record = valid_routed()
        self.assertTrue(oracle.verify_process_trial(**record)["passed"])
        record["requests"].append(copy.deepcopy(record["requests"][-1]))
        self.assertTrue(oracle.verify_process_trial(**record)["passed"])

    def test_foreign_original_operation_or_authority_rejects(self):
        for field, value in [("source_operation", "spawn_agent"),
                             ("original_call_id", "foreign-start"),
                             ("authority", {"SessionId": "foreign"})]:
            with self.subTest(field=field):
                record = valid_routed()
                next(row for row in record["journal"] if row["event_type"] == "ChildRunAccepted")["data"][field] = value
                self.rejects(record)

    def test_foreign_run_or_owner_in_raw_evidence_rejects(self):
        for domain in ["observer", "journal", "binding"]:
            with self.subTest(domain=domain):
                record = valid_routed()
                if domain == "observer":
                    record["events"][0]["output"]["SessionId"] = "foreign"
                elif domain == "journal":
                    record["journal"][0]["persistence_id"] = "session-foreign"
                else:
                    record["snapshot"]["binding"]["accepted"]["run_id"] = "foreign"
                self.rejects(record)

    def test_missing_parent_provider_pair_rejects(self):
        record = valid_routed()
        record["requests"][-1]["messages"] = [
            message for message in record["requests"][-1]["messages"]
            if message.get("tool_call_id") != "review"]
        self.rejects(record)

    def test_raw_dto_and_receipt_disagreement_rejects(self):
        for field, value in [("success", False), ("occurrence", 1.0),
                             ("turn", 3.0), ("observed_ns", 110.0)]:
            with self.subTest(field=field):
                record = valid_routed()
                record["receipt"]["calls"][-1][field] = value
                self.rejects(record)

    def test_missing_or_altered_actual_artifact_rejects(self):
        for content in [None, b"", b"truncated", b"COMPLETE-process-nonce\n"]:
            with self.subTest(content=content):
                record = valid_routed()
                record["effects"]["files"][record["setup"]["artifact_path"]] = content
                self.rejects(record)

    def test_partial_read_cannot_supply_full_artifact_review(self):
        for args in [{"StartLine": 1}, {"Limit": 1}]:
            with self.subTest(arguments=args):
                record = valid_routed()
                arguments = {"Path": record["setup"]["artifact_path"], **args}
                replace_parent_call(record, "review", arguments=arguments)
                self.rejects(record)

    def test_failed_or_forged_write_receipt_cannot_supply_effect(self):
        for result in ["Error: Permission denied", "File written", "Successfully wrote 1 bytes to /foreign"]:
            with self.subTest(result=result):
                record = valid_routed()
                write_result(record)["content"] = result
                self.rejects(record)

    def test_actual_file_must_match_dispatched_write_bytes(self):
        record = valid_routed()
        command = next(message["tool_calls"][0] for message in record["requests"][0]["messages"]
                       if message.get("tool_calls"))
        arguments = json.loads(command["function"]["arguments"])
        arguments["Content"] = "different bytes"
        command["function"]["arguments"] = json.dumps(arguments)
        self.rejects(record)

    def test_duplicate_actual_write_rejects(self):
        record = valid_routed()
        messages = record["requests"][0]["messages"]
        extra = copy.deepcopy(messages[-2:])
        extra[0]["tool_calls"][0]["id"] = "second-write"
        extra[1]["tool_call_id"] = "second-write"
        messages.extend(extra)
        self.rejects(record)

    def test_missing_child_dispatch_or_changed_overlay_rejects(self):
        record = valid_routed()
        record["requests"][0]["messages"][0]["content"] = "A different child contract."
        self.rejects(record)

    def test_journal_terminal_must_match_canonical_provider_terminal(self):
        for field, value in [("outcome", "Failed"), ("run_id", "foreign-run"),
                             ("log_path", "/foreign/log")]:
            with self.subTest(field=field):
                record = valid_routed()
                row = next(row for row in record["journal"]
                           if row["data"].get("kind") == "TerminalRecorded")
                terminal = copy.deepcopy(row["data"]["terminal"])
                row["data"]["terminal"] = terminal
                terminal[field] = value
                self.rejects(record)

    def test_missing_or_foreign_durable_adoption_rejects(self):
        for change in ["absent", "authority", "input"]:
            with self.subTest(change=change):
                record = valid_routed()
                adoption = record["journal"][-1]
                if change == "absent":
                    record["journal"].pop()
                elif change == "authority":
                    adoption["data"]["authority"] = {"SessionId": "foreign"}
                else:
                    adoption["data"]["input_ids"] = ["foreign-input"]
                self.rejects(record)

    def test_new_user_task_cannot_replace_automatic_continuation(self):
        record = valid_routed()
        record["receipt"]["user_inputs"] = 3
        self.rejects(record)

    def test_unacknowledged_or_foreign_consumption_rejects(self):
        for change in ["response", "call", "request"]:
            with self.subTest(change=change):
                record = valid_routed()
                if change == "response":
                    record["snapshot"]["requests"][-1]["response_payload_written"] = False
                elif change == "call":
                    record["receipt"]["delivery_observations"]["deliveries"][0]["call_id"] = "foreign"
                else:
                    record["receipt"]["delivery_observations"]["deliveries"][0]["request_id"] = 1
                self.rejects(record)

    def test_final_prose_cannot_replace_the_actual_artifact_review(self):
        record = valid_routed()
        replace_parent_call(record, "review", result="I reviewed the complete artifact.")
        self.rejects(record)


    def test_each_process_variant_has_a_valid_control(self):
        for builder in [valid_approval, lambda: valid_approval(True), valid_recovery]:
            with self.subTest(builder=builder):
                self.assertTrue(oracle.verify_process_trial(**builder())["passed"])

    def test_approval_requires_durable_matching_resolution(self):
        for fault in ["absent", "attempt", "decision", "before-request"]:
            with self.subTest(fault=fault):
                record = valid_approval()
                row = next(row for row in record["journal"] if row["event_type"] == "ToolApprovalResolved")
                if fault == "absent":
                    record["journal"].remove(row)
                elif fault == "attempt":
                    row["data"]["attempt_id"] = "foreign"
                elif fault == "decision":
                    row["data"]["decision"] = "Denied"
                else:
                    row["sequence_nr"] = 1
                self.rejects(record)

    def test_approval_rejects_foreign_prompt_subjects(self):
        for field, value in [("CallId", "foreign"), ("RequesterSenderId", "foreign"),
                             ("InteractionCwd", "/foreign")]:
            with self.subTest(field=field):
                record = valid_approval()
                prompt = next(event for event in record["events"] if event["output"]["Type"] == "tool_interaction")
                prompt["output"][field] = value
                self.rejects(record)

    def test_approval_rejects_preconsent_absent_or_duplicate_effects(self):
        for fault in ["before", "absent", "different", "duplicate", "grant"]:
            with self.subTest(fault=fault):
                record = valid_approval()
                if fault == "before":
                    record["effects"]["before_answer"]["/project/effect.txt"] = b"authorized"
                elif fault in {"absent", "different"}:
                    record["effects"]["after_terminal"]["/project/effect.txt"] = None if fault == "absent" else b"different"
                elif fault == "grant":
                    record["effects"]["grants_after"] = b"new persistent grant"
                else:
                    messages = record["requests"][1]["messages"]
                    messages.extend(provider_pair("second-protected-shell", "shell_execute",
                        {**record["setup"]["shell_arguments"], "_rationale": "Execute the assigned process eval step."}, "authorized"))
                self.rejects(record)

    def test_approval_rejects_changed_original_retry_or_preanswer_barrier(self):
        for fault in ["call", "arguments", "result", "barrier"]:
            with self.subTest(fault=fault):
                record = valid_approval()
                if fault == "barrier":
                    record["snapshot"]["approval_answer"]["request_id"] = 2
                else:
                    messages = record["requests"][1]["messages"]
                    if fault == "call":
                        messages[-2]["tool_calls"][0]["id"] = messages[-1]["tool_call_id"] = "foreign"
                    elif fault == "arguments":
                        messages[-2]["tool_calls"][0]["function"]["arguments"] = json.dumps({"Command": "foreign"})
                    else:
                        messages[-1]["content"] = "Error: denied"
                self.rejects(record)

    def test_cancel_requires_a_distinct_notice_after_stale_answer(self):
        for fault in ["reused", "wrong-text", "before-answer"]:
            with self.subTest(fault=fault):
                record = valid_approval(True)
                if fault == "reused":
                    record["events"].pop()
                elif fault == "wrong-text":
                    record["events"][-1]["output"]["Text"] = "The RPC succeeded."
                else:
                    record["actions"][1]["after_event_sequence"] = len(record["events"])
                self.rejects(record)

    def test_cancel_rejects_effect_grant_and_fresh_child(self):
        for fault in ["effect", "grant", "dispatch", "shell"]:
            with self.subTest(fault=fault):
                record = valid_approval(True)
                if fault == "effect":
                    record["effects"]["after_terminal"]["/project/effect.txt"] = b"authorized"
                elif fault == "grant":
                    record["effects"]["grants_after"] = b"grant"
                elif fault == "dispatch":
                    record["snapshot"]["requests"].append({"child": True, "held": False, "request_id": 3})
                else:
                    record["requests"][0]["messages"].extend(provider_pair("protected-shell", "shell_execute",
                        {**record["setup"]["shell_arguments"], "_rationale": "Execute the assigned process eval step."}, "authorized"))
                self.rejects(record)

    def test_cancel_requires_durable_refusal_and_original_cancel_control(self):
        for fault in ["closure", "refusal", "control"]:
            with self.subTest(fault=fault):
                record = valid_approval(True)
                if fault == "control":
                    replace_parent_call(record, "status", arguments={"RunId": "process-run", "Cancel": False})
                else:
                    kind = "DispatchClosed" if fault == "closure" else "ToolApprovalResolved"
                    record["journal"] = [row for row in record["journal"]
                        if row["data"].get("kind", row["event_type"]) != kind]
                self.rejects(record)

    def test_recovery_requires_actual_process_replacement_and_readiness(self):
        for field, value in [("signal", "SIGTERM"), ("old_exited", False), ("new_ready", False),
                             ("old_pid", 4101.0), ("new_pid", 0)]:
            with self.subTest(field=field):
                record = valid_recovery()
                record["snapshot"]["crash"][field] = value
                self.rejects(record)
        record = valid_recovery()
        record["snapshot"]["crash"].update(new_pid=4101, new_start="old-start")
        self.rejects(record)

    def test_recovery_rejects_success_claim_and_child_relaunch(self):
        record = valid_recovery()
        record["receipt"]["delivery_observations"]["deliveries"][0]["terminal"].update(state="Completed", outcome="Completed")
        normalize(record)
        self.rejects(record)
        record = valid_recovery()
        record["snapshot"]["requests"].append({"child": True, "held": False, "request_id": 3})
        self.rejects(record)

    def test_recovery_requires_same_session_join_and_resume(self):
        for fault in ["join", "session", "order"]:
            with self.subTest(fault=fault):
                record = valid_recovery()
                if fault == "join":
                    record["events"] = [event for event in record["events"] if event["output"]["Type"] != "session_joined"]
                elif fault == "session":
                    record["actions"][1]["data"]["session_id"] = "foreign"
                else:
                    record["actions"].reverse()
                self.rejects(record)


    def test_raw_new_user_input_cannot_replace_same_session_continuation(self):
        for builder in [valid_routed, valid_approval, lambda: valid_approval(True), valid_recovery]:
            with self.subTest(builder=builder):
                record = builder()
                record["requests"][-1]["messages"].append({"role": "user",
                    "content": "Start a new task instead of the original child continuation."})
                self.rejects(record)

    def test_approval_answer_must_follow_independent_probe_boundary(self):
        record = valid_approval()
        prompt = next(event["sequence"] for event in record["events"]
            if event["output"]["Type"] == "tool_interaction")
        for action in record["actions"]:
            action["after_event_sequence"] = prompt
        self.rejects(record)

    def test_cancelled_terminal_must_be_consumed_before_stale_answer(self):
        record = valid_approval(True)
        notices = record["events"][-2:]
        del record["events"][-2:]
        second = next(index for index, event in enumerate(record["events"])
            if event["observed_ns"] == record["receipt"]["second_turn_ns"])
        record["events"][second + 1:second + 1] = notices
        normalize(record)
        notice_sequences = [event["sequence"] for event in record["events"]
            if event["output"]["Type"] == "text"]
        record["actions"][0]["data"]["notice_sequence"] = notice_sequences[0]
        for action in record["actions"]:
            action["after_event_sequence"] = notice_sequences[0]
        self.rejects(record)

    def test_recovery_action_metadata_requires_exact_observer_domain(self):
        for field, value in [("observed_ns", 1.0), ("sequence", 1.0),
                             ("after_event_sequence", 7.0)]:
            with self.subTest(field=field):
                record = valid_recovery()
                for action in record["actions"]:
                    action[field] = value
                self.rejects(record)


if __name__ == "__main__":
    unittest.main()
