"""One synthetic valid routed record for the independent process controls."""
import copy
import json
from child_run_evals import CHILD_CONTRACT


def candidate_valid():
    case, session, nonce = "child_run_routed_skill", "process-owner", "process-nonce"
    accepted = {"run_id": "process-run", "scope_id": "process-scope", "state": "Accepted", "control_tool": "check_agent_run"}
    root = "/home/netclaw/.netclaw/sessions/process-owner/subagents/process-run"
    paths = {"session_dir": root, "temp_dir": root + "/tmp", "artifact_dir": root + "/artifacts", "log_path": root + "/logs/session.log"}
    artifact, content = paths["artifact_dir"] + "/complete-process-nonce.txt", b"COMPLETE-process-nonce"
    authority = {"SessionId": session, "TurnId": "original-turn", "RequesterSenderId": "local", "Audience": "Personal", "Boundary": "boundary:trusted-instance", "ChannelType": "headless", "RequesterPrincipal": "Operator", "TransportAuthenticity": "LocalProcess", "PayloadTaint": "Trusted", "SourceKind": "signalr", "SupportsInteractiveApproval": False}
    start_args = {"Name": "process-routed-skill", "Task": "Trial process-nonce.", "Context": "process-overlay-process-nonce", "_rationale": "Run the assigned child."}
    status = {**accepted, "state": "Running", "log_path": paths["log_path"], "artifact_directory": paths["artifact_dir"], "cancellation_requested": False, "dispatch_closed": False, "terminal": None}
    terminal = {**accepted, "state": "Completed", "outcome": "Completed", "reason": None, "source_operation": "skill_load", "log_path": paths["log_path"], "artifact_directory": paths["artifact_dir"], "output": artifact, "warning": None, "checkpoint": None}
    events, calls = [], []
    def event(kind, **fields):
        seq = len(events) + 1
        events.append({"sequence": seq, "observed_ns": seq * 10, "output": {"Type": kind, "SessionId": session, **fields}})
    def call(identifier, name, args, result, turn):
        event("tool_call", CallId=identifier, ToolName=name, ArgumentsJson=json.dumps(args))
        ns = events[-1]["observed_ns"]
        event("tool_result", CallId=identifier, ToolName=name, Result=result, ToolFailureCode=None)
        calls.append({"id": identifier, "name": name, "arguments": args, "result": result, "success": True, "failure_code": None, "occurrence": len(calls) + 1, "turn": turn, "observed_ns": ns})
    call("start", "skill_load", start_args, json.dumps(accepted), 1)
    call("status", "check_agent_run", {"RunId": accepted["run_id"], "Cancel": False}, json.dumps(status), 1)
    event("turn_completed")
    first = events[-1]["observed_ns"]
    call("log", "file_read", {"Path": paths["log_path"]}, "The actual child log has a visible line.", 2)
    event("turn_completed")
    second = events[-1]["observed_ns"]
    call("review", "file_read", {"Path": artifact}, content.decode(), 3)
    event("turn_completed")
    boundary = events[-1]["observed_ns"]
    def pair(identifier, name, args, result):
        return [{"role": "assistant", "tool_calls": [{"id": identifier, "function": {"name": name, "arguments": json.dumps(args)}}]}, {"role": "tool", "tool_call_id": identifier, "content": result}]
    child = {"messages": [{"role": "system", "content": CHILD_CONTRACT + "\nprocess-overlay-process-nonce"}, {"role": "user", "content": "Context:\n[session]\n" + "\n".join(k + ": " + v for k, v in paths.items()) + "\nTask:\nTrial process-nonce."}] + pair("write", "file_write", {"Path": artifact, "Content": content.decode(), "_rationale": "Execute the assigned process eval step."}, f"Successfully wrote {len(content)} bytes to {artifact}")}
    parent_inputs = ["Initial process-nonce task.", "Probe process-nonce task."]
    messages = [{"role": "user", "content": text} for text in parent_inputs]
    for row in calls:
        messages += pair(row["id"], row["name"], row["arguments"], row["result"])
    messages += pair("delivery", "skill_load", {"run_id": accepted["run_id"], "source_operation": "skill_load"}, json.dumps(terminal))
    requests = [child, {"messages": messages}]
    delivery = {"accepted": accepted, "terminal": terminal, "call_id": "delivery", "request_id": 2, "request_admitted_ns": 1000, "response_first_payload_ns": 1100, "parent_boundary_ns": boundary}
    receipt = {"session_id": session, "prompt_nonce": nonce, "status": "observed", "error": None, "user_inputs": 2, "completed_turns": 3, "first_turn_ns": first, "second_turn_ns": second, "release_ns": 900, "accepted_run": accepted, "accepted_runs": [accepted], "calls": calls, "last_reply": content.decode(), "all_replies": ["Accepted", "PARENT-PROBE-process-nonce", content.decode()], "delivery_observations": {"complete": True, "deliveries": [delivery]}}
    snapshot = {"case": case, "binding": {"accepted": accepted, "paths": paths, "request_id": 1, "arrived_ns": 100, "upstream_first_payload_ns": 200, "bound_ns": 300}, "released": True, "release_ns": 900, "requests": [{"request_id": 1, "held": True, "child": True, "admitted_ns": 100, "upstream_first_payload_ns": 200, "response_first_payload_ns": 950, "response_payload_written": True}, {"request_id": 2, "held": False, "child": False, "admitted_ns": 1000, "response_first_payload_ns": 1100, "response_payload_written": True}]}
    journal = []
    def record(seq, event_type, manifest, **data):
        journal.append({"persistence_id": "session-" + session, "sequence_nr": seq, "serializer_id": 150, "manifest": manifest, "event_type": event_type, "data": {"session_id": session, **data}})
    record(1, "ChildRunAccepted", "cra-v1", run_id=accepted["run_id"], scope_id=accepted["scope_id"], source_operation="skill_load", original_call_id="start", authority=authority)
    record(2, "ChildRunEvent", "cre-v1", run_id=accepted["run_id"], kind="Started")
    record(3, "ChildRunEvent", "cre-v1", run_id=accepted["run_id"], kind="TerminalRecorded", terminal=terminal)
    record(4, "ChildRunEvent", "cre-v1", run_id=accepted["run_id"], kind="ResultPrepared", terminal=terminal)
    record(6, "ChildRunEvent", "cre-v1", run_id=accepted["run_id"], kind="DeliveryAdmitted", input_id="child-input", authority=authority)
    record(7, "ToolTaskAdopted", "tta-v1", run_id=accepted["run_id"], input_ids=["child-input"], authority=authority)
    for row in journal:
        row["sequence_nr"] *= 10
    journal.insert(0, {"persistence_id": "session-" + session, "sequence_nr": 1, "serializer_id": 150, "manifest": "ia-v1", "event_type": "InputAdmitted", "data": {"session_id": session, "run_id": None, "input_id": "original-input", "authority": authority, "source_message_id": "signalr:initial", "user_role": "User", "user_message": parent_inputs[0]}})
    journal.insert(2, {"persistence_id": "session-" + session, "sequence_nr": 15, "serializer_id": 150, "manifest": "ia-v1", "event_type": "InputAdmitted", "data": {"session_id": session, "run_id": None, "input_id": "probe-input", "authority": authority, "source_message_id": "signalr:probe", "user_role": "User", "user_message": parent_inputs[1]}})
    log = "\n".join("child_run_" + kind + " owner=" + session + " runId=" + accepted["run_id"] + " journalSequence=" + str(seq) + (" inputId=child-input callId=delivery" if kind == "delivery_admitted" else "") for kind, seq in [("accepted", 10), ("terminal_recorded", 30), ("result_prepared", 40), ("delivery_admitted", 60)])
    setup = {"case": case, "nonce": nonce, "start_arguments": start_args, "authority": authority, "parent_inputs": parent_inputs, "ingress": {key: value for key, value in authority.items() if key != "TurnId"}, "artifact_path": artifact, "artifact_bytes": content, "overlay": "process-overlay-process-nonce"}
    return dict(case=case, receipt=receipt, events=events, requests=requests, snapshot=snapshot, journal=journal, actions=[], effects={"files": {artifact: content}}, setup=setup, log=log)


if __name__ == "__main__":
    from background_process_evals import verify_process_trial
    print(json.dumps(verify_process_trial(**candidate_valid()), indent=2))
