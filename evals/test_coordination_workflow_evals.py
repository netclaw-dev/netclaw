"""Controls for the case adapter and cumulative child consumption."""

import copy
from contextlib import closing, redirect_stdout
import hashlib
import io
import json
import os
import shutil
import sqlite3
import errno
from pathlib import Path
import re
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from child_run_evals import REQUIRED_RATIONALE_ERROR, actual_file, collect, consumed_deliveries, context_paths, legacy_observer_mode
from coordination_workflow_evals import archive_read, archive_runtime_files, blocked_config, contract, prepare, prompt, workflow_stages
from test_child_run_evals import ACCEPTED, PATHS, child, parent, terminal

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "evals/fixtures/coordination-artifacts"
CASES = ("coordination_analyze_plan", "coordination_attachment_blocked")


def shell_functions(*names):
    source = (ROOT / "evals/run-evals.sh").read_text()
    return "\n".join(re.search(r"^" + re.escape(name) + r"\(\) \{\n.*?^\}", source,
                              re.MULTILINE | re.DOTALL).group(0) for name in names)


def stage_evidence(root):
    home = root / "home"
    setup = prepare(FIXTURE, home, root / "setup")
    receipt = {"session_id": "session-neutral", "accepted_runs": [], "verified_deliveries": [],
               "delivery_observations": {"complete": True, "deliveries": []}}
    events, requests = [], []
    for index, (key, agent) in enumerate((("findings_path", "headless-analyst"), ("plan_path", "task-worker")), 1):
        content = f"Neutral complete artifact {index}.\n"
        actual_file(home, setup[key]).write_text(content)
        accepted = {**ACCEPTED, "run_id": f"run-{index}", "scope_id": f"scope-{index}"}
        paths = {name: value.replace("/neutral/subagents/neutral", f"/neutral/subagents/run-{index}")
                 for name, value in PATHS.items()}
        receipt["accepted_runs"].append(accepted)
        body = {**terminal(), "run_id": accepted["run_id"], "scope_id": accepted["scope_id"],
                "log_path": paths["log_path"], "artifact_directory": paths["artifact_dir"]}
        receipt["verified_deliveries"].append({"accepted": accepted, "terminal": body})
        args = {"Agent": agent, "Task": setup[key],
                "Context": setup["findings_path"] if index == 2 else "Analyze the source."}
        for dto in ({"Type": "tool_call", "CallId": f"start-{index}", "ToolName": "spawn_agent",
                     "ArgumentsJson": json.dumps(args)},
                    {"Type": "tool_result", "CallId": f"start-{index}", "ToolName": "spawn_agent",
                     "Result": json.dumps(accepted)}):
            sequence = len(events) + 1
            events.append({"sequence": sequence, "observed_ns": sequence * 100, "output": {
                **dto, "SessionId": "session-neutral"}})
        request = child()
        request["messages"][1]["content"] = "Context:\n" + "\n".join(k + ": " + v for k, v in paths.items())
        request["messages"].extend([
            {"role": "assistant", "tool_calls": [{"id": f"write-{index}", "function": {
                "name": "file_write", "arguments": json.dumps({"Path": setup[key], "Content": content})}}]},
            {"role": "tool", "tool_call_id": f"write-{index}",
             "content": f"Successfully wrote {len(content.encode())} bytes to {setup[key]}"}])
        requests.append(request)
        delivery = parent(body, "delivery-" + str(index))
        delivery["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
            {"run_id": accepted["run_id"], "source_operation": "spawn_agent"})
        requests.append(delivery)
        receipt["delivery_observations"]["deliveries"].append({"accepted": accepted, "call_id": "delivery-" + str(index),
            "terminal": body, "request_admitted_ns": sequence * 100 + 10,
            "response_first_payload_ns": sequence * 100 + 20, "parent_boundary_ns": 10000})
    requests.append({"messages": [{"role": "user", "content":
        "[system: [available-subagents — use spawn_agent to delegate]\n\n## headless-analyst\nA source analyst.\n]"}]})
    requests = requests[::2][:2] + requests[1::2] + requests[-1:]
    receipt["calls"] = observer_calls(events)
    return receipt, events, requests, setup, home


def observer_calls(events):
    calls, pending = [], {}
    turn = 1
    for event in events:
        output = event["output"]
        if output["Type"] == "tool_call":
            row = {"id": output["CallId"], "name": output["ToolName"],
                   "arguments": json.loads(output["ArgumentsJson"]), "turn": turn,
                   "observed_ns": event["observed_ns"], "occurrence": len(calls) + 1}
            pending[row["id"]] = row
            calls.append(row)
        elif output["Type"] == "tool_result":
            row = pending.pop(output["CallId"])
            row.update(result=output["Result"], failure_code=output.get("ToolFailureCode"),
                       success=output.get("ToolFailureCode") is None)
        elif output["Type"] == "turn_completed":
            turn += 1
    assert not pending
    return calls


def artifact_pipeline_evidence(root, case):
    receipt, events, requests, setup, home = stage_evidence(root)
    contents = {}
    for index, key in enumerate(("findings_path", "plan_path"), 1):
        contents[key] = (FIXTURE / "artifacts" / ("findings-complete.md" if index == 1 else "plan-complete.md")).read_bytes().decode("utf-8")
        actual_file(home, setup[key]).write_bytes(contents[key].encode("utf-8"))
        for request in requests:
            for message in request["messages"]:
                for call in message.get("tool_calls", []):
                    if call["id"] == f"write-{index}":
                        call["function"]["arguments"] = json.dumps({"Path": setup[key], "Content": contents[key]})
                if message.get("tool_call_id") == f"write-{index}":
                    message["content"] = f"Successfully wrote {len(contents[key].encode('utf-8'))} bytes to {setup[key]}"

    def pair(name, identifier, args, result, failure=None):
        return [{"output": {"Type": "tool_call", "SessionId": receipt["session_id"], "CallId": identifier,
                            "ToolName": name, "ArgumentsJson": json.dumps(args)}},
                {"output": {"Type": "tool_result", "SessionId": receipt["session_id"], "CallId": identifier,
                            "ToolName": name, "Result": result, "ToolFailureCode": failure}}]

    events[2:2] = pair("file_read", "read-findings", {"Path": setup["findings_path"]}, contents["findings_path"])
    events.extend(pair("file_read", "read-plan", {"Path": setup["plan_path"]}, contents["plan_path"]))
    blocked = case == "coordination_attachment_blocked"
    attachment = ("Error: Personal trust context does not allow attach access to local files." if blocked else
                  f"File attached: plan.md (text/markdown) at {setup['plan_path']}")
    events.extend(pair("attach_file", "attach-plan", {"Path": setup["plan_path"], "DisplayName": "plan.md"},
                       attachment, "access_denied" if blocked else None))
    if not blocked:
        events.append({"output": {"Type": "file", "SessionId": receipt["session_id"],
                                  "FileName": "plan.md", "MimeType": "text/markdown", "FilePath": setup["plan_path"]}})
    for index, event in enumerate(events, 1):
        event.update(sequence=index, observed_ns=index * 100)
    receipt["calls"] = observer_calls(events)
    for index, delivery in enumerate(receipt["delivery_observations"]["deliveries"], 1):
        start = next(call for call in receipt["calls"] if call["id"] == f"start-{index}")
        delivery.update(request_admitted_ns=start["observed_ns"] + 110,
                        response_first_payload_ns=start["observed_ns"] + 120)
    history = []
    for event in events:
        dto = event["output"]
        if dto["Type"] == "tool_call":
            history.append({"role": "assistant", "tool_calls": [{"id": dto["CallId"], "function": {
                "name": dto["ToolName"], "arguments": dto["ArgumentsJson"]}}]})
        elif dto["Type"] == "tool_result":
            history.append({"role": "tool", "tool_call_id": dto["CallId"], "content": dto["Result"]})
    requests.append({"messages": history})
    receipt.update(status="observed", prompt_nonce="pipeline-nonce", observer_mode="collect",
                   initial_prompt_sha256=hashlib.sha256(prompt(setup).encode()).hexdigest(),
                   last_reply="The parent reviewed the plan.", case=case, prompt_ordinal=1)
    receipt["verified_deliveries"] = copy.deepcopy(receipt["verified_deliveries"])
    observer, relay = root / "observer", root / "child-runs/relay"
    observer.mkdir(); relay.mkdir(parents=True)
    data = {"Nonce": receipt["prompt_nonce"], "Mode": "collect", "InitialPrompt": prompt(setup),
            "SessionId": receipt["session_id"]}
    (observer / "observer-input.json").write_text(json.dumps(data))
    (observer / "session-output.jsonl").write_text("\n".join(json.dumps(event) for event in events))
    raw = {key: value for key, value in receipt.items() if key not in {"verified_deliveries", "case", "prompt_ordinal"}}
    (observer / "observer-receipt.json").write_text(json.dumps(raw))
    (observer / "verified-receipt.json").write_text(json.dumps(receipt))
    for index, request in enumerate(requests, 1):
        (relay / f"request-{index:04}.json").write_text(json.dumps(request))
    return receipt, home, observer


def declaration_evidence(root, rejected=False, repeated=False):
    receipt, events, requests, setup, home = stage_evidence(root)
    args = {"Path": setup["source_root"], "_rationale": "Declare the assigned source project."}
    pair = [
        {"output": {"Type": "tool_call", "SessionId": receipt["session_id"],
                    "CallId": "declaration", "ToolName": "set_working_directory", "ArgumentsJson": json.dumps(args)}},
        {"output": {"Type": "tool_result", "SessionId": receipt["session_id"],
                    "CallId": "declaration", "ToolName": "set_working_directory",
                    "Result": REQUIRED_RATIONALE_ERROR if rejected else setup["source_root"],
                    "ToolFailureCode": "invalid_rationale" if rejected else None}}]
    events[:0] = [copy.deepcopy(event) for _ in range(2 if repeated else 1) for event in pair]
    for index, event in enumerate(events, 1):
        event.update(sequence=index, observed_ns=index * 100)
    receipt["calls"] = observer_calls(events)
    for index, row in enumerate(receipt["delivery_observations"]["deliveries"], 1):
        start = next(call for call in receipt["calls"] if call["id"] == f"start-{index}")
        row.update(request_admitted_ns=start["observed_ns"] + 110, response_first_payload_ns=start["observed_ns"] + 120)
    history = []
    for event in events:
        output = event["output"]
        if output["Type"] == "tool_call":
            history.append({"role": "assistant", "tool_calls": [{"id": output["CallId"], "function": {
                "name": output["ToolName"], "arguments": output["ArgumentsJson"]}}]})
        else:
            history.append({"role": "tool", "tool_call_id": output["CallId"], "content": output["Result"]})
    requests.append({"messages": history})
    return receipt, events, requests, setup, home



def replacement_evidence(root, stage):
    receipt, events, requests, setup, home = stage_evidence(root)
    accepted = {**ACCEPTED, "run_id": "run-failed-" + str(stage), "scope_id": "scope-failed-" + str(stage)}
    body = {**terminal(), "run_id": accepted["run_id"], "scope_id": accepted["scope_id"],
            "state": "Failed", "outcome": "Failed", "reason": "no_activity_timeout", "log_path": None, "artifact_directory": None}
    index = (stage - 1) * 2
    pair = copy.deepcopy(events[index:index + 2])
    for event in pair:
        event["output"]["CallId"] = "start-failed-" + str(stage)
    pair[1]["output"]["Result"] = json.dumps(accepted)
    events[index:index] = pair
    for sequence, event in enumerate(events, 1):
        event.update(sequence=sequence, observed_ns=sequence * 100)
    receipt["calls"] = observer_calls(events)
    receipt["accepted_runs"].insert(stage - 1, accepted)
    receipt["verified_deliveries"].insert(stage - 1, {"accepted": accepted, "terminal": body})
    consumed = {"accepted": accepted, "call_id": "delivery-failed-" + str(stage), "terminal": body,
                "request_admitted_ns": 1, "response_first_payload_ns": 2, "parent_boundary_ns": 10000}
    receipt["delivery_observations"]["deliveries"].insert(stage - 1, consumed)
    for row in receipt["delivery_observations"]["deliveries"]:
        start = next(call for call in receipt["calls"] if call["result"] == json.dumps(row["accepted"]))
        row.update(request_admitted_ns=start["observed_ns"] + 110, response_first_payload_ns=start["observed_ns"] + 120)
    failure = parent(body, consumed["call_id"])
    failure["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
        {"run_id": accepted["run_id"], "source_operation": "spawn_agent"})
    request = child()
    child_root = "/home/netclaw/.netclaw/sessions/neutral/subagents/" + accepted["run_id"]
    paths = {"session_dir": child_root, "temp_dir": child_root + "/tmp", "artifact_dir": child_root + "/artifacts",
             "log_path": child_root + "/logs/session.log"}
    request["messages"][1]["content"] = "Context:\n" + "\n".join(key + ": " + value for key, value in paths.items())
    requests[-1:-1] = [request, failure]
    log = actual_file(home, "/home/netclaw/.netclaw/sessions/neutral/logs/session.log")
    log.parent.mkdir(parents=True)
    rows = []
    for index, run in enumerate(receipt["accepted_runs"]):
        delivery = next(row for row in receipt["delivery_observations"]["deliveries"] if row["accepted"] == run)
        for offset, name in enumerate(("accepted", "terminal_recorded", "result_prepared", "delivery_admitted"), 1):
            rows.append("child_run_" + name + " owner=" + receipt["session_id"] + " runId=" + run["run_id"] +
                        " journalSequence=" + str(index * 4 + offset) +
                        (" inputId=input-" + run["run_id"] + " callId=" + delivery["call_id"] if name == "delivery_admitted" else ""))
    log.write_text("\n".join(rows))
    return receipt, events, requests, setup, home


class CoordinationReplacementControls(unittest.TestCase):
    def test_failed_analyst_and_failed_worker_retain_one_completed_stage_each(self):
        for stage in (1, 2):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as directory:
                receipt, events, requests, setup, home = replacement_evidence(Path(directory), stage)
                stages = workflow_stages(receipt, events, requests, setup, home)
                self.assertEqual(["start-1", "start-2"], [row["id"] for row in stages])
                self.assertEqual("Failed", receipt["verified_deliveries"][stage - 1]["terminal"]["outcome"])

    def test_collector_retains_canonical_failed_attempt_only_for_workflow_cases(self):
        for case in (*CASES, "subagent_specialization_precedence"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                receipt, _, requests, _, home = replacement_evidence(Path(directory), 1)
                with patch.dict(os.environ, {"TMPDIR_EVAL": directory, "EVAL_HOME": str(home)}), \
                     patch("child_run_evals.invoke_observer", return_value=(receipt, "Actual observer reply")), \
                     patch("child_run_evals.evidence_requests", return_value=requests), redirect_stdout(io.StringIO()):
                    if case in CASES:
                        collect(1, "task", "session-neutral", "text", directory, case, 1)
                        saved = json.loads((Path(directory) / "verified-receipt.json").read_text())
                        self.assertEqual(["Failed", "Completed", "Completed"], [row["terminal"]["outcome"] for row in saved["verified_deliveries"]])
                        self.assertEqual(3, len(saved["verified_deliveries"]))
                    else:
                        with self.assertRaisesRegex(AssertionError, "did not complete normally"):
                            collect(1, "task", "session-neutral", "text", directory, case, 1)
                        self.assertFalse((Path(directory) / "verified-receipt.json").exists())

    def test_failed_terminal_requires_consumption_before_replacement(self):
        for stage in (1, 2):
            for fault in ("missing", "incomplete", "late", "wrong-run", "wrong-call", "changed-body", "numeric-time", "forged-start-time", "provider-owner", "missing-pair", "duplicate-pair"):
                with self.subTest(stage=stage, fault=fault), tempfile.TemporaryDirectory() as directory:
                    receipt, events, requests, setup, home = replacement_evidence(Path(directory), stage)
                    observation = receipt["delivery_observations"]["deliveries"][stage - 1]
                    if fault == "missing":
                        receipt["delivery_observations"]["deliveries"].remove(observation)
                    elif fault == "incomplete":
                        receipt["delivery_observations"]["complete"] = False
                    elif fault == "late":
                        replacement = next(row for row in receipt["calls"] if row["id"] == "start-" + str(stage))
                        observation["response_first_payload_ns"] = replacement["observed_ns"] + 1
                    elif fault == "wrong-run":
                        observation["accepted"] = {**observation["accepted"], "run_id": "foreign-run"}
                    elif fault == "wrong-call":
                        observation["call_id"] = "foreign-terminal"
                    elif fault == "changed-body":
                        observation["terminal"] = {**observation["terminal"], "reason": "forged reason"}
                    elif fault == "numeric-time":
                        observation["request_admitted_ns"] = True
                    elif fault == "forged-start-time":
                        replacement = next(row for row in receipt["calls"] if row["id"] == "start-" + str(stage))
                        observation["response_first_payload_ns"] = replacement["observed_ns"] + 1
                        replacement["observed_ns"] += 50
                    elif fault == "provider-owner":
                        body = json.loads(requests[-2]["messages"][1]["content"])
                        body["scope_id"] = "foreign-scope"
                        requests[-2]["messages"][1]["content"] = json.dumps(body)
                    elif fault == "missing-pair":
                        requests.pop(-2)
                    else:
                        requests[-2]["messages"].extend(copy.deepcopy(requests[-2]["messages"]))
                    with self.assertRaises(AssertionError):
                        workflow_stages(receipt, events, requests, setup, home)

    def test_failed_attempt_retains_scope_and_actual_owner_requirements(self):
        for fault in ("tool", "write", "owner", "context", "profile", "dto", "ordinal"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                receipt, events, requests, setup, home = replacement_evidence(Path(directory), 1)
                request = requests[-3]
                if fault in {"tool", "write"}:
                    request["messages"].append({"role": "assistant", "tool_calls": [{"id": "forbidden", "function": {
                        "name": "shell_execute" if fault == "tool" else "file_write",
                        "arguments": json.dumps({"Path": setup["plan_path"], "Content": "wrong stage"})}}]})
                elif fault == "owner":
                    log = actual_file(home, "/home/netclaw/.netclaw/sessions/neutral/logs/session.log")
                    log.write_text(log.read_text().replace("owner=session-neutral", "owner=another-owner"))
                elif fault == "context":
                    request["messages"][1]["content"] = "Context:\nTask: Missing runtime paths."
                elif fault == "profile":
                    args = json.loads(events[0]["output"]["ArgumentsJson"])
                    args["Agent"] = "invented-profile"
                    events[0]["output"]["ArgumentsJson"] = json.dumps(args)
                    receipt["calls"] = observer_calls(events)
                elif fault == "dto":
                    receipt["calls"][0]["success"] = False
                else:
                    receipt["calls"][0]["occurrence"] = True
                with self.assertRaises((AssertionError, ValueError)):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_failed_terminal_storage_matches_actual_context_or_remains_null(self):
        for stage in (1, 2):
            for field, context_key in (("log_path", "log_path"), ("artifact_directory", "artifact_dir")):
                for value in (None, "actual", "foreign", ""):
                    with self.subTest(stage=stage, field=field, value=value), tempfile.TemporaryDirectory() as directory:
                        receipt, events, requests, setup, home = replacement_evidence(Path(directory), stage)
                        paths = context_paths(requests[-3])
                        body = receipt["verified_deliveries"][stage - 1]["terminal"]
                        body[field] = paths[context_key] if value == "actual" else (
                            paths[context_key].replace("/neutral/", "/foreign/") if value == "foreign" else value)
                        requests[-2]["messages"][1]["content"] = json.dumps(body)
                        if value in (None, "actual"):
                            self.assertEqual(["start-1", "start-2"], [row["id"] for row in
                                workflow_stages(receipt, events, requests, setup, home)])
                        else:
                            with self.assertRaisesRegex(AssertionError, "failed child terminal supplies foreign storage"):
                                workflow_stages(receipt, events, requests, setup, home)

    def test_failed_worker_cannot_supply_successful_write_credit(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = replacement_evidence(Path(directory), 2)
            request = requests[1]
            write = copy.deepcopy(request["messages"][-2:])
            request["messages"] = request["messages"][:-2]
            requests[-3]["messages"].extend(write)
            with self.assertRaisesRegex(AssertionError, "full artifact write"):
                workflow_stages(receipt, events, requests, setup, home)

    def test_extra_completed_stage_and_forged_completed_outcome_fail(self):
        for stage in (1, 2):
            for fault in ("forged", "canonical-extra"):
                with self.subTest(stage=stage, fault=fault), tempfile.TemporaryDirectory() as directory:
                    receipt, events, requests, setup, home = replacement_evidence(Path(directory), stage)
                    verified = receipt["verified_deliveries"][stage - 1]
                    body = {**verified["terminal"], "state": "Completed", "outcome": "Completed", "reason": None,
                            "log_path": context_paths(requests[-3])["log_path"],
                            "artifact_directory": context_paths(requests[-3])["artifact_dir"]}
                    verified["terminal"] = body
                    if fault == "canonical-extra":
                        requests[-2]["messages"][1]["content"] = json.dumps(body)
                        receipt["delivery_observations"]["deliveries"][stage - 1]["terminal"] = body
                        requests[-3]["messages"].extend(copy.deepcopy(requests[stage - 1]["messages"][-2:]))
                    with self.assertRaises(AssertionError):
                        workflow_stages(receipt, events, requests, setup, home)

    def test_extra_failed_or_duplicate_run_cannot_escape_stage_validation(self):
        for fault in ("duplicate", "unrelated", "after-completed"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                receipt, events, requests, setup, home = replacement_evidence(Path(directory), 1)
                if fault == "duplicate":
                    receipt["accepted_runs"].append(copy.deepcopy(receipt["accepted_runs"][0]))
                elif fault == "unrelated":
                    args = json.loads(events[0]["output"]["ArgumentsJson"])
                    args["Task"] = "An unrelated objective."
                    events[0]["output"]["ArgumentsJson"] = json.dumps(args)
                    receipt["calls"] = observer_calls(events)
                else:
                    events[:4] = events[2:4] + events[:2]
                    for index, event in enumerate(events, 1):
                        event.update(sequence=index, observed_ns=index * 100)
                    receipt["calls"] = observer_calls(events)
                    for row in receipt["delivery_observations"]["deliveries"]:
                        start = next(call for call in receipt["calls"] if call["result"] == json.dumps(row["accepted"]))
                        row.update(request_admitted_ns=start["observed_ns"] + 110, response_first_payload_ns=start["observed_ns"] + 120)
                with self.assertRaises(AssertionError):
                    workflow_stages(receipt, events, requests, setup, home)


class CoordinationArchiveControls(unittest.TestCase):
    def fixture(self, root, copied=True):
        home = root / "home"
        evidence = root / "child-runs"
        setup_dir = evidence / "coordination-case"
        setup = prepare(FIXTURE, home, setup_dir)
        actual_file(home, setup["findings_path"]).write_bytes(b"actual findings\r\n")
        actual_file(home, setup["plan_path"]).write_bytes(b"actual plan\r\n")
        observer = evidence / "observer-neutral"
        observer.mkdir()
        relay = evidence / "relay"
        relay.mkdir()
        data = {"Nonce": "neutral-archive", "Mode": "collect", "InitialPrompt": prompt(setup), "SessionId": "session-neutral"}
        receipt = {"session_id": data["SessionId"], "prompt_nonce": data["Nonce"], "observer_mode": data["Mode"],
                   "initial_prompt_sha256": hashlib.sha256(data["InitialPrompt"].encode()).hexdigest(),
                   "status": "observed", "accepted_runs": [], "calls": []}
        events, requests = [], []
        def emit(kind, **values):
            index = len(events) + 1
            events.append({"sequence": index, "observed_ns": index, "output": {
                "Type": kind, "SessionId": data["SessionId"], **values}})
        history = {"messages": []}
        for index in (1, 2):
            accepted = {**ACCEPTED, "run_id": "run-" + str(index), "scope_id": "scope-" + str(index)}
            receipt["accepted_runs"].append(accepted)
            arguments = {"Agent": "task-worker", "Task": "A neutral assigned artifact."}
            emit("tool_call", CallId="start-" + str(index), ToolName="spawn_agent", ArgumentsJson=json.dumps(arguments))
            emit("tool_result", CallId="start-" + str(index), ToolName="spawn_agent", Result=json.dumps(accepted))
            history["messages"].extend([
                {"role": "assistant", "tool_calls": [{"id": "start-" + str(index), "function": {
                    "name": "spawn_agent", "arguments": json.dumps(arguments)}}]},
                {"role": "tool", "tool_call_id": "start-" + str(index), "content": json.dumps(accepted)}])
            child_root = "/home/netclaw/.netclaw/sessions/neutral/subagents/" + accepted["run_id"]
            paths = {"session_dir": "/home/netclaw/.netclaw/sessions/neutral/workspace",
                     "temp_dir": child_root + "/tmp", "artifact_dir": child_root + "/artifacts",
                     "log_path": child_root + "/logs/session.log"}
            request = child()
            request["messages"][1]["content"] = "Context:\n" + "\n".join(k + ": " + v for k, v in paths.items())
            requests.append(request)
            body = {**terminal(), "run_id": accepted["run_id"], "scope_id": accepted["scope_id"],
                    "log_path": paths["log_path"], "artifact_directory": paths["artifact_dir"]}
            delivery = parent(body, "delivery-" + str(index))
            delivery["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
                {"run_id": accepted["run_id"], "source_operation": "spawn_agent"})
            history["messages"].extend(delivery["messages"])
        target = ("/home/netclaw/.netclaw/sessions/neutral/workspace/attachments/plan-1.md" if copied else setup["plan_path"])
        actual_file(home, target).parent.mkdir(parents=True, exist_ok=True)
        actual_file(home, target).write_bytes(actual_file(home, setup["plan_path"]).read_bytes())
        result = "File attached: plan.md (text/markdown) at " + target + (" (copied into current session)" if copied else "")
        arguments = {"Path": setup["plan_path"]}
        emit("tool_call", CallId="attach-neutral", ToolName="attach_file", ArgumentsJson=json.dumps(arguments))
        emit("tool_result", CallId="attach-neutral", ToolName="attach_file", Result=result)
        emit("file", FilePath=target, FileName="plan.md", MimeType="text/markdown")
        history["messages"].extend([
            {"role": "assistant", "tool_calls": [{"id": "attach-neutral", "function": {
                "name": "attach_file", "arguments": json.dumps(arguments)}}]},
            {"role": "tool", "tool_call_id": "attach-neutral", "content": result}])
        requests.append(history)
        receipt["calls"] = observer_calls(events)
        log = actual_file(home, "/home/netclaw/.netclaw/sessions/neutral/logs/session.log")
        log.parent.mkdir(parents=True)
        log.write_text("\n".join("child_run_accepted owner=session-neutral runId=run-" + str(index) + " journalSequence=" + str(index)
                                for index in (1, 2)))
        def save():
            (observer / "observer-input.json").write_text(json.dumps(data))
            (observer / "observer-receipt.json").write_text(json.dumps(receipt))
            (observer / "session-output.jsonl").write_text("\n".join(json.dumps(row) for row in events))
            for index, request in enumerate(requests):
                (relay / f"request-{index:04}.json").write_text(json.dumps(request))
        save()
        return home, evidence, setup, observer, receipt, events, requests, target, save

    def archive(self, home, evidence):
        return archive_runtime_files(FIXTURE, home, evidence / "coordination-case", evidence)

    def replacement_fixture(self, root):
        result = self.fixture(root)
        home, evidence, setup, observer, receipt, events, requests, target, save = result
        accepted = {**ACCEPTED, "run_id": "failed-archive", "scope_id": "failed-archive-scope"}
        body = {**terminal(), "run_id": accepted["run_id"], "scope_id": accepted["scope_id"],
                "state": "Failed", "outcome": "Failed", "reason": "no_activity_timeout",
                "log_path": None, "artifact_directory": None}
        args = json.dumps({"Agent": "task-worker", "Task": "A neutral assigned artifact."})
        failed_events = [{"output": {"Type": kind, "SessionId": receipt["session_id"], "CallId": "failed-archive-start",
                          "ToolName": "spawn_agent", **values}} for kind, values in (
                          ("tool_call", {"ArgumentsJson": args}), ("tool_result", {"Result": json.dumps(accepted)}))]
        events[:0] = failed_events
        for index, event in enumerate(events, 1):
            event.update(sequence=index, observed_ns=index * 100)
        receipt["calls"] = observer_calls(events)
        receipt["accepted_runs"].insert(0, accepted)
        receipt["delivery_observations"] = {"complete": True, "deliveries": [{"accepted": accepted,
            "call_id": "failed-archive-delivery", "terminal": body, "request_admitted_ns": 210,
            "response_first_payload_ns": 220, "parent_boundary_ns": 10000}]}
        history = requests[-1]["messages"]
        delivery = parent(body, "failed-archive-delivery")
        delivery["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
            {"run_id": accepted["run_id"], "source_operation": "spawn_agent"})
        history[:0] = [{"role": "assistant", "tool_calls": [{"id": "failed-archive-start", "function": {
            "name": "spawn_agent", "arguments": args}}]},
            {"role": "tool", "tool_call_id": "failed-archive-start", "content": json.dumps(accepted)}, *delivery["messages"]]
        failed_request = copy.deepcopy(requests[0])
        failed_request["messages"][1]["content"] = failed_request["messages"][1]["content"].replace("/run-1/", "/failed-archive/")
        requests.insert(0, failed_request)
        log = actual_file(home, "/home/netclaw/.netclaw/sessions/neutral/logs/session.log")
        log.write_text(log.read_text() + "\nchild_run_accepted owner=session-neutral runId=failed-archive journalSequence=3")
        save()
        return result

    def test_archives_actual_file_output_after_null_path_failed_attempt(self):
        for supplied in (False, True):
            with self.subTest(supplied=supplied), tempfile.TemporaryDirectory() as directory:
                home, evidence, _, _, receipt, _, requests, target, save = self.replacement_fixture(Path(directory))
                if supplied:
                    body = receipt["delivery_observations"]["deliveries"][0]["terminal"]
                    paths = context_paths(requests[0])
                    body.update(log_path=paths["log_path"], artifact_directory=paths["artifact_dir"])
                    requests[-1]["messages"][3]["content"] = json.dumps(body)
                    save()
                inventory = self.archive(home, evidence)
                self.assertIs(inventory["capture_complete"], True)
                delivery = next(row for row in inventory["files"] if row["kind"] == "delivery")
                self.assertEqual(target, delivery["canonical_path"])
                self.assertEqual(actual_file(home, target).read_bytes(),
                    (evidence / "coordination-case/runtime-files" / delivery["archive_path"]).read_bytes())
                self.assertNotIn("passed", inventory)

    def test_archive_failed_attempt_retains_canonical_consumption_owner_and_storage(self):
        for fault in ("missing-consumption", "incomplete", "foreign-consumption", "duplicate-consumption", "late-consumption",
                      "dto-success", "dto-ordinal", "provider-pair", "owner", "context", "log-path", "artifact-path", "no-completed-start"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                home, evidence, _, _, receipt, events, requests, _, save = self.replacement_fixture(Path(directory))
                observation = receipt["delivery_observations"]["deliveries"][0]
                if fault == "missing-consumption":
                    receipt["delivery_observations"]["deliveries"].clear()
                elif fault == "incomplete":
                    receipt["delivery_observations"]["complete"] = False
                elif fault == "foreign-consumption":
                    observation["accepted"] = {**observation["accepted"], "run_id": "foreign"}
                elif fault == "duplicate-consumption":
                    receipt["delivery_observations"]["deliveries"].append(copy.deepcopy(observation))
                elif fault == "late-consumption":
                    observation["response_first_payload_ns"] = observation["parent_boundary_ns"] + 1
                elif fault == "dto-success":
                    receipt["calls"][0]["success"] = False
                elif fault == "dto-ordinal":
                    receipt["calls"][0]["occurrence"] = True
                elif fault == "no-completed-start":
                    events[:] = events[:2] + events[-3:]
                    for index, event in enumerate(events, 1):
                        event.update(sequence=index, observed_ns=index * 100)
                    receipt["calls"] = observer_calls(events)
                elif fault == "provider-pair":
                    del requests[-1]["messages"][2:4]
                elif fault == "owner":
                    log = actual_file(home, "/home/netclaw/.netclaw/sessions/neutral/logs/session.log")
                    log.write_text(log.read_text().replace("owner=session-neutral runId=failed-archive", "owner=foreign runId=failed-archive"))
                elif fault == "context":
                    requests[0]["messages"][1]["content"] = "Context without runtime storage paths."
                else:
                    observation["terminal"]["log_path" if fault == "log-path" else "artifact_directory"] = "/foreign/storage"
                    requests[-1]["messages"][3]["content"] = json.dumps(observation["terminal"])
                save()
                with self.assertRaises(AssertionError):
                    self.archive(home, evidence)

    def test_retains_actual_sources_artifacts_and_both_delivery_locations(self):
        for copied in (False, True):
            with self.subTest(copied=copied), tempfile.TemporaryDirectory() as directory:
                home, evidence, setup, _, _, _, _, target, _ = self.fixture(Path(directory), copied)
                actual_file(home, target).write_bytes(b"actual delivered bytes\x00\r\n")
                inventory = self.archive(home, evidence)
                self.assertIs(inventory["capture_complete"], True)
                self.assertEqual(setup["source_root"], inventory["source_root"])
                self.assertEqual(4, len(inventory["files"]))
                for row in inventory["files"]:
                    content = (evidence / "coordination-case/runtime-files" / row["archive_path"]).read_bytes()
                    self.assertEqual(actual_file(home, row["canonical_path"]).read_bytes(), content)
                    self.assertEqual(len(content), row["length"])
                    self.assertEqual(hashlib.sha256(content).hexdigest(), row["sha256"])
                delivery = inventory["files"][-1]
                self.assertEqual(target, delivery["canonical_path"])
                self.assertEqual("attach-neutral", delivery["attribution"]["call_id"])
                self.assertNotIn("passed", inventory)
                if copied:
                    self.assertNotEqual(inventory["files"][2]["sha256"], delivery["sha256"])

    def test_preserves_missing_altered_and_partial_workflow_facts_without_credit(self):
        with tempfile.TemporaryDirectory() as directory:
            home, evidence, setup, _, receipt, events, _, _, save = self.fixture(Path(directory))
            actual_file(home, setup["source_root"] + "/source/catalog.py").write_bytes(b"altered actual source\r\n")
            actual_file(home, setup["findings_path"]).unlink()
            actual_file(home, setup["plan_path"]).write_bytes(b"partial plan")
            receipt["status"] = "incomplete"
            receipt["last_reply"] = ""
            events.clear()
            save()
            inventory = self.archive(home, evidence)
            self.assertIs(inventory["files"][0]["matches_expected"], False)
            self.assertIs(inventory["files"][1]["present"], False)
            self.assertNotIn("sha256", inventory["files"][1])
            self.assertEqual(b"partial plan", (evidence / "coordination-case/runtime-files/workspace/plan.md").read_bytes())
            self.assertEqual("incomplete", inventory["observers"][0]["status"])
            self.assertEqual(0, inventory["observers"][0]["file_outputs"])

    def test_preserves_no_observer_and_missing_source_facts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            home, evidence = root / "home", root / "child-runs"
            setup = prepare(FIXTURE, home, evidence / "coordination-case")
            actual_file(home, setup["source_root"] + "/source/catalog.py").unlink()
            inventory = self.archive(home, evidence)
            self.assertEqual([], inventory["observers"])
            self.assertTrue(all(row["present"] is False for row in inventory["files"]))

    def test_missing_delivery_target_remains_an_explicit_file_fact(self):
        with tempfile.TemporaryDirectory() as directory:
            home, evidence, _, _, _, _, _, target, _ = self.fixture(Path(directory))
            actual_file(home, target).unlink()
            inventory = self.archive(home, evidence)
            delivery = inventory["files"][-1]
            self.assertEqual("delivery", delivery["kind"])
            self.assertIs(delivery["present"], False)
            self.assertNotIn("sha256", delivery)
            self.assertNotIn("passed", inventory)

    def test_rejects_foreign_root_artifact_scope_and_links(self):
        for fault in ("root", "artifact", "source-link", "inside-link", "dangling-link", "parent-link", "destination-link"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, setup, _, _, _, _, _, _ = self.fixture(root)
                path = actual_file(home, setup["findings_path"])
                if fault in ("root", "artifact"):
                    setup["source_root" if fault == "root" else "findings_path"] = "/home/netclaw/.netclaw/workspaces/foreign"
                    (evidence / "coordination-case/setup.json").write_text(json.dumps(setup))
                elif fault == "parent-link":
                    source_dir = actual_file(home, setup["source_root"] + "/source")
                    source_dir.rename(root / "source-copy")
                    source_dir.symlink_to(root / "source-copy", target_is_directory=True)
                elif fault == "destination-link":
                    (evidence / "coordination-case/runtime-files").symlink_to(root / "outside")
                else:
                    if fault == "source-link":
                        path = actual_file(home, setup["source_root"] + "/source/catalog.py")
                    path.unlink()
                    target = (actual_file(home, setup["plan_path"]) if fault == "inside-link" else root / "outside")
                    if fault != "dangling-link" and fault != "inside-link":
                        target.write_bytes(b"outside marker")
                    path.symlink_to(target)
                with self.assertRaises((AssertionError, OSError)):
                    self.archive(home, evidence)

    def test_rejects_foreign_owner_unpaired_failed_and_arbitrary_delivery(self):
        for fault in ("owner", "provider-id", "failure", "result", "foreign-session", "private-path", "unknown-name", "child-context", "malformed-context", "start-provider-id", "event-order", "log-owner"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, setup, _, receipt, events, requests, target, save = self.fixture(root)
                if fault == "owner":
                    receipt["session_id"] = "foreign-owner"
                elif fault == "provider-id":
                    requests[-1]["messages"][-2]["tool_calls"][0]["id"] = "foreign-call"
                    requests[-1]["messages"][-1]["tool_call_id"] = "foreign-call"
                elif fault == "failure":
                    events[-2]["output"]["ToolFailureCode"] = "access_denied"
                elif fault == "result":
                    events[-2]["output"]["Result"] = "File attached: unsupported"
                elif fault == "foreign-session":
                    events[-1]["output"]["SessionId"] = "foreign-owner"
                elif fault in ("private-path", "unknown-name"):
                    replacement = target.replace("/neutral/", "/foreign/") if fault == "private-path" else target.replace("plan-1.md", "private.md")
                    events[-1]["output"]["FilePath"] = replacement
                    events[-2]["output"]["Result"] = events[-2]["output"]["Result"].replace(target, replacement)
                    requests[-1]["messages"][-1]["content"] = events[-2]["output"]["Result"]
                elif fault == "child-context":
                    requests[0]["messages"][1]["content"] = requests[0]["messages"][1]["content"].replace("workspace", "foreign")
                elif fault == "malformed-context":
                    requests[0]["messages"][1]["content"] = "Context without runtime paths."
                elif fault == "start-provider-id":
                    requests[-1]["messages"][0]["tool_calls"][0]["id"] = "foreign-start"
                    requests[-1]["messages"][1]["tool_call_id"] = "foreign-start"
                elif fault == "event-order":
                    events[-1]["sequence"] = True
                else:
                    actual_file(home, "/home/netclaw/.netclaw/sessions/neutral/logs/session.log").write_text("foreign owner")
                save()
                with self.assertRaises((AssertionError, ValueError)):
                    self.archive(home, evidence)

    def test_delivery_links_and_read_or_copy_errors_fail_loudly(self):
        for fault in ("link", "read", "write"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, _, _, _, _, _, target, _ = self.fixture(root)
                if fault == "link":
                    actual_file(home, target).unlink()
                    outside = root / "outside"
                    outside.write_bytes(b"outside marker")
                    actual_file(home, target).symlink_to(outside)
                    with self.assertRaises((AssertionError, OSError)):
                        self.archive(home, evidence)
                else:
                    if fault == "read":
                        original = os.open
                        def fail(name, flags, *args, **kwargs):
                            if name == "plan-1.md":
                                raise OSError("owned archive read failure")
                            return original(name, flags, *args, **kwargs)
                        with patch("coordination_workflow_evals.os.open", side_effect=fail), self.assertRaises(OSError):
                            self.archive(home, evidence)
                    else:
                        original = Path.open
                        def fail(path, *args, **kwargs):
                            if args == ("xb",) and "runtime-files" in path.parts:
                                raise OSError("owned archive write failure")
                            return original(path, *args, **kwargs)
                        with patch.object(Path, "open", fail), self.assertRaises(OSError):
                            self.archive(home, evidence)
                inventory = json.loads((evidence / "coordination-case/runtime-files/inventory.json").read_text())
                self.assertIs(inventory["capture_complete"], False)
                self.assertTrue(inventory["error"])

    def test_ancestor_swap_cannot_redirect_the_actual_read(self):
        for swap_after_open in (False, True):
            with self.subTest(swap_after_open=swap_after_open), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, setup, _, _, _, _, _, _ = self.fixture(root)
                source = actual_file(home, setup["source_root"] + "/source")
                outside = root / "outside"
                outside.mkdir()
                (outside / "catalog.py").write_bytes(b"private outside marker")
                expected = (source / "catalog.py").read_bytes()
                original = os.open
                swapped = False
                def open_and_swap(name, flags, *args, **kwargs):
                    nonlocal swapped
                    if name == "source" and not swapped:
                        swapped = True
                        if swap_after_open:
                            descriptor = original(name, flags, *args, **kwargs)
                        source.rename(source.with_name("detached-source"))
                        source.symlink_to(outside, target_is_directory=True)
                        if swap_after_open:
                            return descriptor
                    return original(name, flags, *args, **kwargs)
                with patch("coordination_workflow_evals.os.open", side_effect=open_and_swap):
                    if swap_after_open:
                        self.assertEqual(expected, archive_read(home, setup["source_root"] + "/source/catalog.py"))
                    else:
                        with self.assertRaises(OSError) as error:
                            archive_read(home, setup["source_root"] + "/source/catalog.py")
                        self.assertIn(error.exception.errno, (errno.ELOOP, errno.ENOTDIR))
                self.assertTrue(swapped)
                self.assertEqual(b"private outside marker", (outside / "catalog.py").read_bytes())

    def test_nonregular_source_fails_without_an_open_that_waits_for_a_writer(self):
        with tempfile.TemporaryDirectory() as directory:
            home, evidence, setup, _, _, _, _, _, _ = self.fixture(Path(directory))
            source = actual_file(home, setup["findings_path"])
            source.unlink()
            os.mkfifo(source)
            with self.assertRaisesRegex(AssertionError, "regular file"):
                self.archive(home, evidence)

    def test_actual_archive_hook_retains_bytes_and_failure_still_cleans_owned_resources(self):
        for fault in (False, True):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, setup, _, _, _, _, _, _ = self.fixture(root)
                repo = root / "repo"
                (repo / "evals").mkdir(parents=True)
                shutil.copyfile(ROOT / "evals/coordination_workflow_evals.py", repo / "evals/coordination_workflow_evals.py")
                if fault:
                    supplied = actual_file(home, setup["findings_path"])
                    supplied.unlink()
                    supplied.symlink_to(root / "missing")
                command = shell_functions("check_prerequisites", "archive_eval_run", "cleanup_eval_env") + r"""
set -euo pipefail
docker() { printf '%s\n' "docker $*" >> "$ACTION_LOG"; if [[ "$1" == image ]]; then echo synthetic-image; fi; }
kill() { printf '%s\n' "kill $*" >> "$ACTION_LOG"; }
wait() { printf '%s\n' "wait $*" >> "$ACTION_LOG"; }
force_rmrf() { printf '%s\n' "home cleanup" >> "$ACTION_LOG"; rm -rf "$1"; }
resolve_eval_target() { :; }
mktemp() {
    case "$*" in
        *netclaw-eval-home-*) printf '%s\n' "$OWNED_HOME" ;;
        *netclaw-eval-tmp-*) printf '%s\n' "$OWNED_TEMP" ;;
        *) return 1 ;;
    esac
}
check_prerequisites
"""
                env = {**os.environ, "PYTHONDONTWRITEBYTECODE": "1", "PYTHONPATH": str(ROOT / "evals"),
                       "REPO_ROOT": str(repo), "EVAL_ASSET_ROOT": str(ROOT), "EVAL_HOME": str(home),
                       "TMPDIR_EVAL": str(evidence.parent), "COORDINATION_CASE_EVIDENCE": str(evidence / "coordination-case"),
                       "ACTION_LOG": str(root / "actions"), "RUN_ID": "owned-test", "NETCLAW_IMAGE": "synthetic-image",
                       "EVAL_CONTAINER_NAME": "owned-test", "CYCLE_FIXTURE_PID_SAVED": "11", "CHILD_FIXTURE_PID_SAVED": "12",
                       "FILTER_CASE": CASES[0], "RUNS": "1", "OWNED_HOME": str(home)}
                # The real temp root contains child-runs. Keep the operator root outside teardown.
                temp = root / "temp"
                temp.mkdir()
                evidence.rename(temp / "child-runs")
                env["TMPDIR_EVAL"] = str(temp)
                env["OWNED_TEMP"] = str(temp)
                env["COORDINATION_CASE_EVIDENCE"] = str(temp / "child-runs/coordination-case")
                database = home / "evals/results.db"
                database.parent.mkdir(parents=True)
                with closing(sqlite3.connect(database)) as connection:
                    connection.execute("CREATE TABLE eval_results (passed INTEGER, details TEXT)")
                    connection.execute("INSERT INTO eval_results VALUES (1, 'pass')")
                    connection.commit()
                result = subprocess.run(["bash", "-c", command], env=env, text=True, capture_output=True)
                self.assertEqual(1 if fault else 0, result.returncode, result.stderr)
                self.assertFalse(home.exists())
                self.assertFalse(temp.exists())
                actions = (root / "actions").read_text()
                self.assertIn("docker stop owned-test", actions)
                self.assertIn("kill 11", actions)
                self.assertIn("wait 12", actions)
                self.assertTrue(actions.rstrip().endswith("home cleanup"))
                with closing(sqlite3.connect(repo / "evals/runs/owned-test/results.db")) as connection:
                    self.assertEqual([(1, "pass")], connection.execute("SELECT passed, details FROM eval_results").fetchall())
                archive = repo / "evals/runs/owned-test/child-runs/coordination-case/runtime-files"
                inventory = json.loads((archive / "inventory.json").read_text())
                self.assertIs(inventory["capture_complete"], not fault)
                self.assertTrue((archive / "workspace/source/catalog.py").is_file())
                if fault:
                    self.assertIn("link", (archive.parent / "archive.stderr").read_text())
                else:
                    self.assertEqual(b"actual plan\r\n", (archive / "workspace/plan.md").read_bytes())


class CoordinationWorkflowControls(unittest.TestCase):
    def run_artifact_pipeline(self, root, case, mutation=None):
        receipt, home, observer = artifact_pipeline_evidence(root, case)
        if mutation is not None:
            mutation(receipt)
            (observer / "verified-receipt.json").write_text(json.dumps(receipt))
        env = {**os.environ, "CHILD_LAST_EVIDENCE": str(observer), "REPO_ROOT": str(ROOT),
               "EVAL_ASSET_ROOT": str(ROOT), "EVAL_HOME": str(home), "TMPDIR_EVAL": str(root),
               "COORDINATION_CASE_EVIDENCE": str(root / "setup"), "case_name": case,
               "PYTHONDONTWRITEBYTECODE": "1"}
        result = subprocess.run(["bash", "-eu", "-c",
                                 shell_functions("assert_coordination_analyze_plan") + "\nassert_coordination_analyze_plan"],
                                env=env, capture_output=True, text=True)
        return result, observer

    def test_actual_shell_artifact_pipeline_uses_verified_receipt(self):
        for case in CASES:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                result, observer = self.run_artifact_pipeline(Path(directory), case)
                verdict = json.loads((observer / "coordination-verdict.json").read_text())
                self.assertEqual(0, result.returncode, verdict)
                self.assertTrue(verdict["passed"], verdict)
                self.assertNotIn("verified_deliveries", json.loads((observer / "observer-receipt.json").read_text()))

    def test_actual_shell_artifact_pipeline_rejects_invalid_verified_delivery(self):
        mutations = {
            "missing": lambda receipt: receipt["verified_deliveries"].pop(),
            "foreign": lambda receipt: receipt["verified_deliveries"][-1]["accepted"].update(scope_id="foreign-owner"),
            "corrupt": lambda receipt: receipt["verified_deliveries"][-1]["terminal"].update(outcome="Failed"),
        }
        for case in CASES:
            for name, mutation in mutations.items():
                with self.subTest(case=case, fault=name), tempfile.TemporaryDirectory() as directory:
                    result, observer = self.run_artifact_pipeline(Path(directory), case, mutation)
                    self.assertNotEqual(0, result.returncode)
                    self.assertFalse((observer / "coordination-verdict.json").exists())
                    self.assertTrue((observer / "coordination-assertion.stderr").read_text())

    def test_named_root_declaration_preserves_cumulative_history_and_completed_id_reuse(self):
        for rejected in (False, True):
            for repeated in (False, True):
                with self.subTest(rejected=rejected, repeated=repeated), tempfile.TemporaryDirectory() as directory:
                    evidence = declaration_evidence(Path(directory), rejected, repeated)
                    evidence[2].append(copy.deepcopy(evidence[2][-1]))
                    workflow_stages(*evidence)

    def test_declaration_rejects_wrong_root_and_noncanonical_result(self):
        for fault in ("root", "result", "failure"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                receipt, events, requests, setup, home = declaration_evidence(Path(directory))
                if fault == "root":
                    args = {"Path": setup["source_root"] + "/source"}
                    events[0]["output"]["ArgumentsJson"] = json.dumps(args)
                    requests[-1]["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(args)
                else:
                    events[1]["output"].update(Result="Project declaration denied.",
                                             ToolFailureCode="access_denied" if fault == "failure" else None)
                    requests[-1]["messages"][1]["content"] = events[1]["output"]["Result"]
                receipt["calls"] = observer_calls(events)
                with self.assertRaises(AssertionError):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_declaration_requires_actual_owner_and_complete_dto_provider_identity(self):
        for fault in ("dto-id", "provider-id", "arguments", "owner", "dto-absent", "provider-absent", "result-absent"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                receipt, events, requests, setup, home = declaration_evidence(Path(directory))
                if fault == "dto-id":
                    for event in events[:2]:
                        event["output"]["CallId"] = "foreign-id"
                elif fault == "provider-id":
                    requests[-1]["messages"][0]["tool_calls"][0]["id"] = "foreign-id"
                    requests[-1]["messages"][1]["tool_call_id"] = "foreign-id"
                elif fault == "arguments":
                    requests[-1]["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
                        {"Path": setup["source_root"], "_rationale": "A different intent."})
                elif fault == "owner":
                    events[0]["output"]["SessionId"] = "foreign-owner"
                elif fault == "dto-absent":
                    del events[:2]
                    for index, event in enumerate(events, 1):
                        event.update(sequence=index, observed_ns=index * 100)
                elif fault == "provider-absent":
                    requests.pop()
                else:
                    del requests[-1]["messages"][1]
                receipt["calls"] = observer_calls(events)
                with self.assertRaises((AssertionError, ValueError)):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_declaration_requires_exact_typed_observer_metadata(self):
        for rejected in (False, True):
            for fault in ("missing", "success", "numeric-success", "ordinal", "boolean-ordinal", "code"):
                with self.subTest(rejected=rejected, fault=fault), tempfile.TemporaryDirectory() as directory:
                    receipt, events, requests, setup, home = declaration_evidence(Path(directory), rejected)
                    row = receipt["calls"][0]
                    if fault == "missing":
                        del receipt["calls"][0]
                    elif fault == "success":
                        row["success"] = rejected
                    elif fault == "numeric-success":
                        row["success"] = 0 if rejected else 1
                    elif fault == "ordinal":
                        row["occurrence"] = 2
                    elif fault == "boolean-ordinal":
                        row["occurrence"] = True
                    else:
                        row["failure_code"] = None if rejected else "invalid_rationale"
                    with self.assertRaises(AssertionError):
                        workflow_stages(receipt, events, requests, setup, home)

    def test_declaration_provider_multiplicity_requires_distinct_actual_occurrences(self):
        for rejected in (False, True):
            with self.subTest(rejected=rejected), tempfile.TemporaryDirectory() as directory:
                evidence = declaration_evidence(Path(directory), rejected)
                history = evidence[2][-1]["messages"]
                history[2:2] = copy.deepcopy(history[:2])
                with self.assertRaises(AssertionError):
                    workflow_stages(*evidence)

    def test_declaration_dto_multiplicity_requires_provider_coverage(self):
        for rejected in (False, True):
            with self.subTest(rejected=rejected), tempfile.TemporaryDirectory() as directory:
                evidence = declaration_evidence(Path(directory), rejected, repeated=True)
                del evidence[2][-1]["messages"][2:4]
                with self.assertRaisesRegex(AssertionError, "sufficient provider occurrence"):
                    workflow_stages(*evidence)

    def test_distinct_declarations_can_use_split_and_cumulative_provider_histories(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = declaration_evidence(Path(directory), repeated=True)
            for event in events[2:4]:
                event["output"]["CallId"] = "second-declaration"
            history = requests.pop()["messages"]
            history[2]["tool_calls"][0]["id"] = "second-declaration"
            history[3]["tool_call_id"] = "second-declaration"
            receipt["calls"] = observer_calls(events)
            requests.extend([{"messages": history[:2]}, {"messages": history[2:]}])
            requests.append(copy.deepcopy(requests[-1]))
            workflow_stages(receipt, events, requests, setup, home)

    def test_identical_id_fragmentation_reports_insufficient_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = declaration_evidence(Path(directory), repeated=True)
            history = evidence[2].pop()["messages"]
            evidence[2].extend([{"messages": history[:2]}, {"messages": history[2:]}])
            with self.assertRaisesRegex(AssertionError, "sufficient provider occurrence"):
                workflow_stages(*evidence)

    def test_declaration_rejection_cannot_authorize_shell_attempts(self):
        for code, result in (("invalid_rationale", REQUIRED_RATIONALE_ERROR),
                             ("access_denied", "Tool access denied: shell_execute needs approval.")):
            with self.subTest(code=code), tempfile.TemporaryDirectory() as directory:
                receipt, events, requests, setup, home = declaration_evidence(Path(directory))
                args = json.dumps({"Command": "sha256sum source/catalog.py"})
                events[0]["output"].update(ToolName="shell_execute", ArgumentsJson=args)
                events[1]["output"].update(ToolName="shell_execute", ToolFailureCode=code, Result=result)
                function = requests[-1]["messages"][0]["tool_calls"][0]["function"]
                function.update(name="shell_execute", arguments=args)
                requests[-1]["messages"][1]["content"] = result
                receipt["calls"] = observer_calls(events)
                with self.assertRaisesRegex(AssertionError, "outside the assigned workflow"):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_selection_uses_collect_only_for_first_current_prompt(self):
        functions = shell_functions("child_result_consumer")
        for case in CASES:
            self.assertEqual("collect", legacy_observer_mode(case, 1))
            for ordinal in (0, 2, True):
                with self.subTest(case=case, ordinal=ordinal), self.assertRaises(AssertionError):
                    legacy_observer_mode(case, ordinal)
            result = subprocess.run(["bash", "-c", functions + '\nFILTER_CASE="$1"; child_result_consumer',
                                     "bash", case], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotEqual(0, subprocess.run(["bash", "-c", functions +
                            '\nFILTER_CASE=; child_result_consumer']).returncode)

    def test_setup_copies_only_neutral_source_with_unique_owned_roots(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            setups = [prepare(FIXTURE, root / "home", root / f"evidence-{index}") for index in range(2)]
            self.assertNotEqual(setups[0]["source_root"], setups[1]["source_root"])
            for setup in setups:
                workspace = actual_file(root / "home", setup["source_root"])
                self.assertEqual(["source/catalog.py"], [str(path.relative_to(workspace))
                                 for path in workspace.rglob("*") if path.is_file()])
                self.assertEqual((FIXTURE / "source/catalog.py").read_bytes(),
                                 (workspace / "source/catalog.py").read_bytes())
                self.assertFalse(actual_file(root / "home", setup["findings_path"]).exists())
                self.assertFalse(actual_file(root / "home", setup["plan_path"]).exists())
                self.assertNotIn((FIXTURE / "artifacts/plan-complete.md").read_text(), prompt(setup))

    def test_post_start_workspace_permits_daemon_artifacts_without_source_or_sibling_permission_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sibling = root / "home/data/workspaces/operator-owned"
            sibling.mkdir(parents=True)
            sibling.chmod(0o700)
            fixture_modes = {p: stat.S_IMODE(p.stat().st_mode) for p in FIXTURE.rglob("*")}
            old_umask = os.umask(0o022)
            try:
                setup = prepare(FIXTURE, root / "home", root / "evidence")
            finally:
                os.umask(old_umask)
            workspace = actual_file(root / "home", setup["source_root"])
            self.assertEqual(0o777, stat.S_IMODE(workspace.stat().st_mode))
            self.assertEqual(0o755, stat.S_IMODE((workspace / "source").stat().st_mode))
            self.assertEqual(0o644, stat.S_IMODE((workspace / "source/catalog.py").stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE(sibling.stat().st_mode))
            self.assertEqual(fixture_modes, {p: stat.S_IMODE(p.stat().st_mode) for p in FIXTURE.rglob("*")})

    def test_blocked_config_preserves_base_and_other_policy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, destination = root / "base.json", root / "derived.json"
            original = {"Tools": {"AudienceProfiles": {"Personal": {"WriteFiles": {"Mode": "All"}},
                        "Team": {"AttachFiles": {"Mode": "None"}}}}, "marker": "neutral"}
            source.write_text(json.dumps(original))
            before = source.read_bytes()
            blocked_config(source, destination)
            expected = copy.deepcopy(original)
            expected["Tools"]["AudienceProfiles"]["Personal"].update(
                ReadFiles={"Mode": "All", "Roots": []}, AttachFiles={"Mode": "None", "Roots": []})
            self.assertEqual(expected, json.loads(destination.read_text()))
            self.assertEqual(before, source.read_bytes())
            with self.assertRaises(FileExistsError):
                blocked_config(source, destination)

    def test_actual_config_adapter_selects_denial_only_for_blocked_case(self):
        functions = shell_functions("prepare_coordination_config")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            base = root / "base.json"
            base.write_text('{"marker":"neutral"}')
            script = functions + '\nprepare_coordination_config\ncat "$NETCLAW_EVAL_CONFIG_FILE"'
            for case in CASES:
                env = {**os.environ, "FILTER_CASE": case, "REPO_ROOT": str(ROOT),
                       "NETCLAW_EVAL_CONFIG_FILE": str(base), "TMPDIR_EVAL": str(root / case)}
                result = subprocess.run(["bash", "-eu", "-c", script], env=env, capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stderr)
                actual = json.loads(result.stdout)
                if case.endswith("blocked"):
                    self.assertEqual("None", actual["Tools"]["AudienceProfiles"]["Personal"]["AttachFiles"]["Mode"])
                    self.assertEqual("All", actual["Tools"]["AudienceProfiles"]["Personal"]["ReadFiles"]["Mode"])
                else:
                    self.assertEqual({"marker": "neutral"}, actual)
            self.assertEqual('{"marker":"neutral"}', base.read_text())

    def test_main_selects_denied_config_before_daemon_start(self):
        functions = shell_functions("prepare_coordination_config", "main")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            base = root / "base.json"
            base.write_text('{"marker":"neutral"}')
            script = functions + r'''
check_prerequisites() { :; }
build_local_image() { :; }
child_result_consumer() { return 1; }
start_eval_daemon() { cat "$NETCLAW_EVAL_CONFIG_FILE"; exit 0; }
main
'''
            for case in CASES:
                env = {**os.environ, "FILTER_CASE": case, "FILTER_CATEGORY": "", "NETCLAW_BIN": "/bin/true",
                       "REPO_ROOT": str(ROOT), "NETCLAW_EVAL_CONFIG_FILE": str(base),
                       "TMPDIR_EVAL": str(root / case)}
                result = subprocess.run(["bash", "-eu", "-c", script], env=env, capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stderr)
                actual = json.loads(result.stdout)
                if case.endswith("blocked"):
                    self.assertEqual("None", actual["Tools"]["AudienceProfiles"]["Personal"]["AttachFiles"]["Mode"])
                else:
                    self.assertEqual({"marker": "neutral"}, actual)

    def test_registration_selects_one_explicit_case_and_excludes_default(self):
        script = shell_functions("run_all") + r'''
print_category() { :; }
end_category() { :; }
run_multi_turn_case() { :; }
run_case() {
    if [[ "$1" == --json ]]; then shift; fi
    case "$1" in coordination_analyze_plan|coordination_attachment_blocked) echo "$1";; esac
}
run_all
'''
        for case in (*CASES, ""):
            result = subprocess.run(["bash", "-eu", "-c", script], env={**os.environ, "FILTER_CASE": case, "PROMPT_TIMEOUT": "180",
                "LARGE_OUTPUT_EVAL_COMMAND": "neutral", "PROJECT_SCOPE_EVAL_ROOT": "/neutral",
                "PROJECT_SCOPE_EVAL_MISSING_ROOT": "/missing", "NATURALISTIC_PROJECT_ROOT": "/neutral",
                "NATURALISTIC_CWD_ROOT": "/neutral"},
                                    capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(case, result.stdout.strip())

    def test_actual_stage_binding_requires_two_producers_and_exact_full_write_receipts(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = stage_evidence(Path(directory))
            workflow_stages(*evidence)
            mutations = [
                lambda r, e, q, s: e.__delitem__(slice(0, 2)),
                lambda r, e, q, s: r["verified_deliveries"].pop(0),
                lambda r, e, q, s: r["accepted_runs"].pop(0),
                lambda r, e, q, s: q.pop(0),
                lambda r, e, q, s: q.pop(),
                lambda r, e, q, s: q[0]["messages"][-1].update(content="write claimed without receipt"),
                lambda r, e, q, s: q[0]["messages"][-1].update(tool_call_id="foreign"),
                lambda r, e, q, s: q[0]["messages"][-2]["tool_calls"][0]["function"].update(arguments=json.dumps(
                    {"Path": s["findings_path"], "Content": "forged bytes"})),
                lambda r, e, q, s: q[0]["messages"][-2]["tool_calls"][0]["function"].update(arguments=json.dumps(
                    {"Path": s["plan_path"], "Content": "wrong target"})),
                lambda r, e, q, s: r["verified_deliveries"][0]["terminal"].update(log_path="foreign"),
                lambda r, e, q, s: e[2]["output"].update(ArgumentsJson=json.dumps({"Agent": "summarizer",
                    "Task": s["plan_path"], "Context": s["findings_path"]})),
                lambda r, e, q, s: e[3]["output"].update(Result=e[1]["output"]["Result"]),
            ]
            for index, mutate in enumerate(mutations):
                receipt, events, requests, setup, home = copy.deepcopy(evidence)
                mutate(receipt, events, requests, setup)
                for sequence, event in enumerate(events, 1):
                    event["sequence"] = sequence
                with self.subTest(fault=index), self.assertRaises(AssertionError):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_shared_runtime_context_selects_one_worker_and_rejects_wrong_or_duplicate_profiles(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            for event in (events[0], events[2]):
                args = json.loads(event["output"]["ArgumentsJson"])
                args["Context"] = "Runtime artifact paths:\n" + setup["findings_path"] + "\n" + setup["plan_path"]
                event["output"]["ArgumentsJson"] = json.dumps(args)
            receipt["calls"] = observer_calls(events)
            workflow_stages(receipt, events, requests, setup, home)
            original = copy.deepcopy(events)
            args = json.loads(events[2]["output"]["ArgumentsJson"])
            args["Agent"] = "headless-analyst"
            events[2]["output"]["ArgumentsJson"] = json.dumps(args)
            receipt["calls"] = observer_calls(events)
            with self.assertRaisesRegex(AssertionError, "distinct assignment"):
                workflow_stages(receipt, events, requests, setup, home)
            events = copy.deepcopy(original)
            duplicate = copy.deepcopy(events[2:])
            for event in duplicate:
                event["output"]["CallId"] = "duplicate-worker"
            events.extend(duplicate)
            for sequence, event in enumerate(events, 1):
                event["sequence"] = sequence
                event["observed_ns"] = sequence
            receipt["calls"] = observer_calls(events)
            with self.assertRaisesRegex(AssertionError, "repeats a run"):
                workflow_stages(receipt, events, requests, setup, home)

    def test_coordination_assertion_retains_each_python_error_despite_outer_stderr_discard(self):
        function = shell_functions("assert_coordination_analyze_plan")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for failed_stage in ("contract", "artifact"):
                evidence = root / failed_stage
                evidence.mkdir()
                script = function + r'''
python3() {
    if [[ "$2" == contract ]]; then
        echo "contract diagnostic" >&2
        if [[ "$FAILED_STAGE" == contract ]]; then return 7; fi
        echo '{}'
    else
        echo "artifact diagnostic" >&2
        return 8
    fi
}
assert_coordination_analyze_plan 2>/dev/null
'''
                env = {**os.environ, "CHILD_LAST_EVIDENCE": str(evidence), "FAILED_STAGE": failed_stage,
                       "REPO_ROOT": str(ROOT), "EVAL_ASSET_ROOT": str(ROOT), "EVAL_HOME": str(root),
                       "COORDINATION_CASE_EVIDENCE": str(root), "TMPDIR_EVAL": str(root),
                       "case_name": "coordination_analyze_plan"}
                result = subprocess.run(["bash", "-eu", "-c", script], env=env, capture_output=True, text=True)
                with self.subTest(stage=failed_stage):
                    self.assertEqual(1 if failed_stage == "contract" else 8, result.returncode)
                    expected = "contract diagnostic\n"
                    if failed_stage == "artifact":
                        expected += "artifact diagnostic\n"
                    diagnostic = evidence / "coordination-assertion.stderr"
                    self.assertTrue(diagnostic.is_file(), "The case loses its assertion diagnostic.")
                    self.assertEqual(expected, diagnostic.read_text())
                    self.assertEqual("", result.stderr)

    def test_source_edit_restore_and_unrelated_actions_fail_despite_unchanged_final_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = stage_evidence(Path(directory))
            workflow_stages(*evidence)
            for actor, tool in (("child", "file_edit"), ("child", "shell_execute"),
                                ("parent", "file_edit"), ("parent", "file_write"),
                                ("parent", "shell_execute")):
                receipt, events, requests, setup, home = copy.deepcopy(evidence)
                source = actual_file(home, setup["source_root"] + "/source/catalog.py")
                before = source.read_bytes()
                if actor == "child":
                    requests[0]["messages"].insert(-2, {"role": "assistant", "tool_calls": [{
                        "id": "forbidden", "function": {"name": tool, "arguments": json.dumps({
                            "Path": setup["source_root"] + "/source/catalog.py", "Content": "temporary source change"})}}]})
                else:
                    for dto in ({"Type": "tool_call", "CallId": "forbidden", "ToolName": tool,
                                 "ArgumentsJson": json.dumps({"Path": setup["source_root"] + "/source/catalog.py"})},
                                {"Type": "tool_result", "CallId": "forbidden", "ToolName": tool, "Result": "source restored"}):
                        index = len(events) + 1
                        events.append({"sequence": index, "observed_ns": index * 100,
                                       "output": {**dto, "SessionId": receipt["session_id"]}})
                self.assertEqual(before, source.read_bytes())
                with self.subTest(actor=actor, tool=tool), self.assertRaises(AssertionError):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_actual_volatile_user_nudge_supports_profile_discovery_without_assistant_claims(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            discovery = requests[-1]["messages"][0]
            workflow_stages(receipt, events, requests, setup, home)
            original = discovery["content"]
            for role in ("assistant", "tool", "user"):
                discovery.update(role=role, content=original[9:-1])
                with self.subTest(role=role), self.assertRaises(AssertionError):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_completed_start_id_reuse_preserves_distinct_ordered_occurrences(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            for event in events[2:]:
                event["output"]["CallId"] = events[0]["output"]["CallId"]
            receipt["calls"] = observer_calls(events)
            workflow_stages(receipt, events, requests, setup, home)
            events[1], events[2] = events[2], events[1]
            for sequence, event in enumerate(events, 1):
                event["sequence"] = sequence
                event["observed_ns"] = sequence
            with self.assertRaises(ValueError):
                workflow_stages(receipt, events, requests, setup, home)

    def test_child_write_rejects_unresolved_overlap_but_preserves_completed_reuse_and_cumulative_history(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            original = copy.deepcopy(requests[0])
            requests.insert(1, copy.deepcopy(original))
            workflow_stages(receipt, events, requests, setup, home)
            requests[0]["messages"].extend(copy.deepcopy(original["messages"][-2:]))
            workflow_stages(receipt, events, requests, setup, home)
            requests[0] = copy.deepcopy(original)
            requests[0]["messages"].insert(-1, copy.deepcopy(original["messages"][-2]))
            with self.assertRaises(AssertionError):
                workflow_stages(receipt, events, requests, setup, home)

    def test_profile_names_preserve_actual_case_insensitive_lookup(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            for event in (events[0], events[2]):
                arguments = json.loads(event["output"]["ArgumentsJson"])
                arguments["Agent"] = arguments["Agent"].upper()
                event["output"]["ArgumentsJson"] = json.dumps(arguments)
            receipt["calls"] = observer_calls(events)
            workflow_stages(receipt, events, requests, setup, home)

    def test_current_attempt_contract_rejects_stale_receipts_and_changed_source(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            receipt, events, requests, setup, home = stage_evidence(root)
            observer, relay = root / "observer", root / "relay"
            observer.mkdir(); relay.mkdir()
            data = {"Nonce": "nonce-current", "Mode": "collect", "InitialPrompt": prompt(setup), "SessionId": "session-neutral"}
            receipt.update(status="observed", prompt_nonce=data["Nonce"], observer_mode="collect",
                initial_prompt_sha256=hashlib.sha256(data["InitialPrompt"].encode()).hexdigest(),
                last_reply="Actual final reply", case=CASES[0], prompt_ordinal=1)
            (observer / "observer-input.json").write_text(json.dumps(data))
            (observer / "session-output.jsonl").write_text("\n".join(json.dumps(row) for row in events))
            for index, request in enumerate(requests):
                (relay / f"request-{index:04}.json").write_text(json.dumps(request))
            for changes in ({"prompt_nonce": "stale"}, {"case": CASES[1]}, {"prompt_ordinal": 2},
                            {"observer_mode": "turn"}, {"delivery_observations": {"complete": False}}):
                (observer / "verified-receipt.json").write_text(json.dumps({**receipt, **changes}))
                with self.subTest(changes=changes), self.assertRaises(AssertionError):
                    contract(CASES[0], FIXTURE, home, root / "setup", observer, relay)
            (observer / "verified-receipt.json").write_text(json.dumps(receipt))
            result = contract(CASES[0], FIXTURE, home, root / "setup", observer, relay)
            self.assertEqual("delivered", result["delivery"])
            self.assertEqual(actual_file(home, setup["plan_path"]).read_bytes(),
                             (observer / "coordination-artifacts/plan.md").read_bytes())
            actual_file(home, setup["source_root"] + "/source/catalog.py").write_text("changed")
            with self.assertRaises(AssertionError):
                contract(CASES[0], FIXTURE, home, root / "setup", observer, relay)

    def test_two_stage_consumption_uses_cumulative_records_without_final_history_requirement(self):
        second = {**ACCEPTED, "run_id": "run-plan", "scope_id": "scope-plan"}
        body = {**terminal(), "run_id": second["run_id"], "scope_id": second["scope_id"]}
        request = parent(body, "delivery-plan")
        request["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
            {"run_id": second["run_id"], "source_operation": "spawn_agent"})
        starts = [{"accepted": value, "call_id": "start-" + value["run_id"], "source_operation": "spawn_agent"}
                  for value in (ACCEPTED, second)]
        records = [{"request": value, "request_id": index, "admitted_ns": index * 3,
                    "response_first_payload_ns": index * 3 + 1, "response_payload_written": True}
                   for index, value in enumerate((parent(), request), 1)]
        self.assertFalse(consumed_deliveries([], starts[:1], 10, [])["complete"])
        self.assertTrue(consumed_deliveries(records[:1], starts[:1], 10, [])["complete"])
        self.assertFalse(consumed_deliveries(records[:1], starts, 10, [])["complete"])
        result = consumed_deliveries(records, starts, 10, [])
        self.assertTrue(result["complete"])
        self.assertEqual([1, 2], [row["request_id"] for row in result["deliveries"]])
        third = {**ACCEPTED, "run_id": "run-extra", "scope_id": "scope-extra"}
        self.assertFalse(consumed_deliveries(records, starts + [{**starts[0], "accepted": third}], 10, [])["complete"])


if __name__ == "__main__":
    unittest.main()
