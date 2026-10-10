#!/usr/bin/env python3
"""A child-request barrier and narrow oracles for actual background-child trials."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import threading
import time
import urllib.request
import uuid
from http.server import ThreadingHTTPServer

from background_fixture import Fixture, handler_for, message_text

CHILD_CONTRACT = "[Subagent Execution Contract]"
CASES = {"child_run_held_parent", "child_run_partial_cancel", "child_run_cli_acceptance"}
REQUIRED_RATIONALE_ERROR = ("Error: Required meta argument '_rationale' must be a non-empty string. "
                          "Supply one sentence that states the tool call intent. The tool was NOT executed.")


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "JSON repeats an object key.")
        result[key] = value
    return result


def acceptance(text):
    value = json.loads(text, object_pairs_hook=unique_object)
    require(isinstance(value, dict), "Acceptance is not an object.")
    require(all(isinstance(value.get(key), str) and value[key].strip()
                for key in ("run_id", "scope_id", "state", "control_tool")), "Acceptance lacks owner identifiers.")
    require(value["state"] == "Accepted" and value["control_tool"] == "check_agent_run", "Wrong acceptance contract.")
    return value


def is_unexecuted_rationale_rejection(failure_code, result):
    return failure_code == "invalid_rationale" and result == REQUIRED_RATIONALE_ERROR


def accepted_start_calls(calls):
    accepted = []
    for call in calls:
        if call["name"] != "spawn_agent":
            continue
        if call.get("success") is False and is_unexecuted_rationale_rejection(
                call.get("failure_code"), call.get("result")):
            continue
        require(call.get("success") is True and call.get("failure_code") is None,
                "A child start lacks success or the exact unexecuted rationale rejection.")
        acceptance(call["result"])
        accepted.append(call)
    return accepted


def context_paths(request):
    system = "\n".join(message_text(m.get("content")) for m in request.get("messages", []) if m.get("role") == "system")
    if CHILD_CONTRACT not in system:
        return None
    users = "\n".join(message_text(m.get("content")) for m in request.get("messages", []) if m.get("role") == "user")
    paths = {}
    for key in ("session_dir", "temp_dir", "artifact_dir", "log_path"):
        matches = re.findall(r"^" + key + r": (.+)$", users, re.MULTILINE)
        if len(matches) != 1:
            return None
        paths[key] = matches[0]
    return paths


def bind_request(request, accepted, status, nonce):
    require(status.get("run_id") == accepted["run_id"] and status.get("scope_id") == accepted["scope_id"],
            "Status belongs to another accepted child.")
    require(status.get("state") in {"Accepted", "Running", "Cancelling"}, "The child is not live at the barrier.")
    paths = context_paths(request)
    require(paths is not None, "The held request is not an actual child request with runtime storage context.")
    require(paths["log_path"] == status.get("log_path") and paths["artifact_dir"] == status.get("artifact_directory"),
            "The child request does not use the status-returned paths.")
    users = "\n".join(message_text(m.get("content")) for m in request["messages"] if m.get("role") == "user")
    require(nonce in users, "The child task lacks the exact trial nonce.")
    for path in paths.values():
        require(path.startswith("/") and ".." not in PurePosixPath(path).parts, "A child path is not canonical.")
    return paths


def write_completed(request, filename):
    calls = {}
    for message in request.get("messages", []):
        for call in message.get("tool_calls", []):
            function = call.get("function", {})
            if function.get("name") == "file_write":
                arguments = json.loads(function.get("arguments", "{}"))
                path = arguments.get("Path", arguments.get("path", ""))
                if PurePosixPath(path).name == filename:
                    calls[call["id"]] = path
        if message.get("role") == "tool" and message.get("tool_call_id") in calls:
            # The final oracle also checks the actual file and this paired result.
            return {"call_id": message["tool_call_id"], "path": calls[message["tool_call_id"]],
                    "result": message_text(message.get("content"))}
    return None


class ChildFixture(Fixture):
    def __init__(self, upstream, model, api_key, evidence):
        super().__init__(upstream, model, api_key)
        self.mode = "model"
        self.evidence = Path(evidence)
        self.evidence.mkdir(parents=True, exist_ok=False)
        self.records = []
        self.candidate = None
        self.binding = None
        self.release = False
        self.release_ns = 0
        self.abort = False
        self.case = ""

    def control(self, action, data):
        with self.condition:
            if action == "child-setup":
                require(not self.records and data["case"] in CASES, "The child fixture is not fresh.")
                self.nonce, self.case = data["nonce"], data["case"]
            elif action == "child-wait":
                if not self.condition.wait_for(lambda: self.candidate is not None and self.candidate["upstream_first_payload_ns"] > 0, timeout=30):
                    raise TimeoutError("The child upstream response did not reach the payload barrier.")
            elif action == "child-bind":
                require(self.binding is None and not self.release, "The child binding repeats or follows release.")
                accepted = acceptance(json.dumps(data["accepted"]))
                if not self.condition.wait_for(lambda: self.candidate is not None and self.candidate["upstream_first_payload_ns"] > 0, timeout=30):
                    raise TimeoutError("The child upstream response did not reach the payload barrier.")
                paths = bind_request(self.candidate["request"], accepted, data["status"], self.nonce)
                if self.case == "child_run_partial_cancel":
                    filename = f"partial-{self.nonce}.txt"
                    write = write_completed(self.candidate["request"], filename)
                    require(write is not None and write["path"] == paths["artifact_dir"] + "/" + filename,
                            "The held request lacks the exact partial artifact result.")
                    contents = actual_file(os.environ["EVAL_HOME"], write["path"]).read_text()
                    require(contents == "PARTIAL-" + self.nonce, "The partial write lacks actual file evidence before cancellation.")
                self.binding = {"accepted": accepted, "paths": paths, "request_id": self.candidate["request_id"],
                                "arrived_ns": self.candidate["admitted_ns"],
                                "upstream_first_payload_ns": self.candidate["upstream_first_payload_ns"], "bound_ns": time.monotonic_ns()}
            elif action == "child-release":
                require(self.binding is not None and not self.release, "Release requires one canonical child binding.")
                self.release_ns = time.monotonic_ns()
                self.release = True
                self.condition.notify_all()
            elif action == "child-drained":
                require(self.release, "The held request remains unreleased.")
                if not self.condition.wait_for(lambda: self.candidate["forward_complete"], timeout=30):
                    raise TimeoutError("The released provider request did not complete its local forward operation.")
            elif action == "child-consumed":
                pending = [row for row in self.records if row["response_first_payload_ns"]
                           and not row["response_payload_written"] and not row["forward_complete"]
                           and any(terminal_pairs([row["request"]], start["accepted"], start["call_id"], start["source_operation"], data["observed_calls"])
                                   for start in data["expected"])]
                if pending and not self.condition.wait_for(
                        lambda: all(row["response_payload_written"] or row["forward_complete"] for row in pending), timeout=30):
                    raise TimeoutError("A provider payload write lacks its local acknowledgement.")
                return consumed_deliveries(self.records, data["expected"], data["parent_boundary_ns"], data["observed_calls"])
            elif action == "child-abort":
                self.abort = True
                self.condition.notify_all()
            elif action != "snapshot":
                raise ValueError("Unknown child fixture control.")
            return self.snapshot()

    def snapshot(self):
        return {"nonce": self.nonce, "case": self.case, "binding": self.binding, "released": self.release, "release_ns": self.release_ns,
                "candidate": None if self.candidate is None else {k: self.candidate[k] for k in
                    ("request_id", "admitted_ns", "upstream_first_payload_ns", "forward_complete")},
                "requests": [{k: row[k] for k in ("request_id", "admitted_ns", "child", "held", "forward_complete", "upstream_first_payload_ns", "response_first_payload_ns", "response_payload_written")}
                             for row in self.records]}

    def completion(self, request):
        with self.condition:
            number = len(self.records) + 1
            child = context_paths(request) is not None
            nonce_present = any(self.nonce in message_text(m.get("content")) for m in request.get("messages", [])
                                if m.get("role") == "user") and bool(self.nonce)
            hold = self.case in {"child_run_held_parent", "child_run_partial_cancel"} and child and nonce_present and self.candidate is None
            if self.case == "child_run_partial_cancel":
                hold = hold and write_completed(request, f"partial-{self.nonce}.txt") is not None
            row = {"request_id": number, "admitted_ns": time.monotonic_ns(), "child": child,
                   "held": bool(hold), "forward_complete": False, "upstream_first_payload_ns": 0, "response_first_payload_ns": 0, "response_payload_written": False, "request": request}
            self.records.append(row)
            (self.evidence / f"request-{number:04}.json").write_bytes(json.dumps(request).encode())
            if hold:
                self.candidate = row

            return None


def child_handler(fixture):
    base = handler_for(fixture)

    class Handler(base):
        response_status = 0

        def send_response(self, code, message=None):
            self.response_status = code
            return super().send_response(code, message)

        def do_POST(self):
            try:
                super().do_POST()
            except AssertionError as error:
                self.send_json(400, {"error": str(error)})

        def forward(self, body):
            # Use the actual base forward path. Capture each byte that it writes.
            with fixture.condition:
                row = next(row for row in fixture.records if row["request"] is body)
            original = self.wfile
            handler = self
            path = fixture.evidence / f'response-{row["request_id"]:04}.wire'
            with path.open("xb") as capture:
                class Tee:
                    writes = 0
                    def write(self, value):
                        capture.write(value)
                        capture.flush()
                        self.writes += 1
                        first_payload = (self.writes > 1 and value and 200 <= handler.response_status < 300
                                         and row["upstream_first_payload_ns"] == 0)
                        if first_payload:
                            with fixture.condition:
                                row["upstream_first_payload_ns"] = time.monotonic_ns()
                                fixture.condition.notify_all()
                                if row["held"]:
                                    if not fixture.condition.wait_for(lambda: fixture.release or fixture.abort, timeout=600):
                                        raise TimeoutError("The owner did not release the held upstream response payload.")
                                    if fixture.abort and not fixture.release:
                                        raise ValueError("The task-owned trial aborted before response release.")
                                row["response_first_payload_ns"] = time.monotonic_ns()
                        written = original.write(value)
                        if first_payload:
                            with fixture.condition:
                                row["response_payload_written"] = True
                                fixture.condition.notify_all()
                        return written
                    def flush(self):
                        return original.flush()
                self.wfile = Tee()
                try:
                    super().forward(body)
                finally:
                    self.wfile = original
                    with fixture.condition:
                        row["forward_complete"] = True
                        fixture.condition.notify_all()
    return Handler


def require_observed_rejections(observed_calls, dto_calls):
    require(isinstance(observed_calls, list) and all(isinstance(row, dict) for row in observed_calls),
            "The attributed observer calls are absent or malformed.")
    for row in observed_calls:
        if not is_unexecuted_rationale_rejection(row.get("failure_code"), row.get("result")):
            continue
        ordinal = row.get("occurrence")
        require(type(ordinal) is int and 0 < ordinal <= len(dto_calls),
                "The rejected model call lacks its actual DTO occurrence.")
        actual = dto_calls[ordinal - 1]
        require(row.get("success") is False and actual.get("failure") == row["failure_code"]
                and pair_matches((actual["id"], actual["name"], actual["arguments"], actual["result"]), row),
                "The rejected observer call differs from its actual DTO occurrence.")


def terminal_pairs(requests, accepted, original_start_call_id, original_start_operation, observed_calls):
    require(isinstance(observed_calls, list) and all(isinstance(row, dict) for row in observed_calls),
            "The attributed observer calls are absent or malformed.")
    require(isinstance(original_start_operation, str) and original_start_operation, "The actual start operation is absent.")
    pairs = {}
    for request in requests:
        if context_paths(request) is not None:
            continue
        calls = {}
        delivered_in_request = set()
        rejected_in_request = {}
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []):
                function = call.get("function", {})
                arguments = json.loads(function.get("arguments", "{}"), object_pairs_hook=unique_object)
                require(isinstance(arguments, dict), "The tool arguments are not a JSON object.")
                if arguments.get("run_id") == accepted["run_id"] and arguments.get("source_operation"):
                    require(arguments == {"run_id": accepted["run_id"], "source_operation": original_start_operation},
                            "The terminal arguments differ from the canonical two-key object.")
                    require(message.get("role") == "assistant", "The terminal call lacks the assistant role.")
                    require(arguments["source_operation"] == function.get("name") == original_start_operation,
                            "The terminal source differs from the actual original start operation.")
                    require(call["id"] != original_start_call_id, "The terminal call reused the original start identifier.")
                    require(call["id"] not in calls, "The terminal call repeats within an unresolved occurrence.")
                    calls[call["id"]] = (function["name"], arguments)
            if message.get("role") == "tool" and message.get("tool_call_id") in calls:
                identifier = message["tool_call_id"]
                source, arguments = calls.pop(identifier)
                result = message_text(message.get("content"))
                ordinary = [row for row in observed_calls if row.get("id") == identifier]
                if ordinary:
                    matched = [row for row in ordinary if pair_matches((identifier, source, arguments, result), row)]
                    require(matched, "The terminal-shaped model call differs from its attributed observer pair.")
                    occurrences = [row.get("occurrence") for row in matched]
                    require(all(type(value) is int and value > 0 for value in occurrences)
                            and len(set(occurrences)) == len(occurrences),
                            "The rejected model call lacks distinct actual occurrence ordinals.")
                    require(all(row.get("success") is False and is_unexecuted_rationale_rejection(
                                row.get("failure_code"), row.get("result")) for row in matched),
                            "An ordinary model call cannot prove a framework terminal delivery.")
                    pair = (identifier, source, json.dumps(arguments, sort_keys=True), result)
                    rejected_in_request[pair] = rejected_in_request.get(pair, 0) + 1
                    require(rejected_in_request[pair] <= len(matched),
                            "The provider history repeats a rejection without a distinct actual occurrence.")
                    continue
                require(identifier not in delivered_in_request, "The terminal pair repeats inside one provider history.")
                delivered_in_request.add(identifier)
                body = json.loads(result, object_pairs_hook=unique_object)
                require(isinstance(body, dict), "The terminal body is not a JSON object.")
                require(body.get("run_id") == accepted["run_id"] and body.get("scope_id") == accepted["scope_id"],
                        "The terminal pair has a foreign owner.")
                require(body.get("source_operation") == source, "The terminal source operation differs.")
                require(identifier not in pairs or pairs[identifier] == body, "A terminal pair changed between requests.")
                pairs[identifier] = body
    require(len(pairs) <= 1, "The accepted run has multiple terminal call identifiers.")
    return pairs


def canonical_pairs(requests, accepted, original_start_call_id, original_start_operation, observed_calls):
    pairs = terminal_pairs(requests, accepted, original_start_call_id, original_start_operation, observed_calls)
    require(len(pairs) == 1, "The accepted run lacks exactly one attributed terminal call/result pair.")
    return next(iter(pairs.items()))


def consumed_deliveries(records, expected, parent_boundary_ns, observed_calls):
    if not expected:
        return {"complete": False, "deliveries": []}
    result = []
    for start in expected:
        accepted = acceptance(json.dumps(start["accepted"]))
        require(isinstance(start.get("call_id"), str) and start["call_id"], "The original start call identifier is absent.")
        observations = []
        all_pairs = terminal_pairs([row["request"] for row in records], accepted, start["call_id"], start["source_operation"], observed_calls)
        for row in records:
            pairs = terminal_pairs([row["request"]], accepted, start["call_id"], start["source_operation"], observed_calls)
            payload_ns = row.get("response_first_payload_ns", 0)
            if pairs and row.get("response_payload_written") and 0 < row["admitted_ns"] < payload_ns < parent_boundary_ns:
                identifier, terminal = next(iter(pairs.items()))
                observations.append({"accepted": accepted, "call_id": identifier, "terminal": terminal,
                                     "request_id": row["request_id"], "request_admitted_ns": row["admitted_ns"],
                                     "response_first_payload_ns": payload_ns, "parent_boundary_ns": parent_boundary_ns})
        if not observations:
            return {"complete": False, "deliveries": []}
        require(len(all_pairs) == 1, "The consumed child has no stable attributed terminal pair.")
        result.append(observations[0])
    return {"complete": True, "deliveries": result}


def committed_positions(log, session, run_id, call_id):
    result = {}
    for name in ("accepted", "terminal_recorded", "result_prepared", "delivery_admitted"):
        pattern = (r"child_run_" + name + r" owner=" + re.escape(session) + r" runId=" + re.escape(run_id)
                   + r" journalSequence=(\d+)(?: inputId=([^\s]+) callId=([^\s]+))?")
        matches = re.findall(pattern, log)
        require(len(matches) == 1, "The run lacks exactly one post-commit diagnostic: " + name)
        result[name] = int(matches[0][0])
        if name == "delivery_admitted":
            require(matches[0][1] and matches[0][2] == call_id, "The admitted input lacks the actual terminal call ID.")
    require(list(result.values()) == sorted(set(result.values())), "Child journal positions are not distinct and ordered.")
    return result


def actual_file(home, canonical):
    prefix = "/home/netclaw/.netclaw/"
    require(isinstance(canonical, str) and canonical.startswith(prefix), "The runtime path is outside the eval-owned mount.")
    relative = canonical[len(prefix):]
    require(".." not in PurePosixPath(relative).parts, "The runtime path escapes the eval-owned home.")
    root = Path(home).resolve() / "data"
    target = root / relative
    require(target.resolve().is_relative_to(root.resolve()), "A link escapes the eval-owned home.")
    return target


def verify_child_actions(requests, paths, nonce, cancel):
    calls = {}
    allowed = {"load_tool", "search_tools", "file_list", "file_read", "file_write"}
    for request in requests:
        if context_paths(request) != paths:
            continue
        write_occurrences = 0
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []):
                function = call.get("function", {})
                require(function.get("name") in allowed, "The child called an action outside the assigned task.")
                if function.get("name") == "file_write":
                    write_occurrences += 1
                    require(write_occurrences == 1, "One child history contains multiple artifact write occurrences.")
                    args = json.loads(function["arguments"])
                    require(call["id"] not in calls or calls[call["id"]] == args, "The child write changed between requests.")
                    calls[call["id"]] = args
    require(len(calls) == 1, "The child did not execute exactly one artifact write.")
    arguments = next(iter(calls.values()))
    expected = paths["artifact_dir"] + (f"/partial-{nonce}.txt" if cancel else f"/complete-{nonce}.txt")
    require(arguments.get("Path", arguments.get("path")) == expected
            and arguments.get("Content", arguments.get("content")) == ("PARTIAL-" if cancel else "COMPLETE-") + nonce,
            "The child artifact call has the wrong path or content.")


def parent_call_pairs(request):
    pending, pairs = {}, []
    for position, message in enumerate(request.get("messages", [])):
        for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
            identifier = call.get("id")
            require(identifier and identifier not in pending, "An unresolved parent call identifier repeats.")
            function = call["function"]
            arguments = json.loads(function["arguments"], object_pairs_hook=unique_object)
            pending[identifier] = (function["name"], arguments, position)
        if message.get("role") == "tool" and message.get("tool_call_id") in pending:
            identifier = message["tool_call_id"]
            name, arguments, start = pending.pop(identifier)
            pairs.append((identifier, name, arguments, message_text(message.get("content")), start, position))
    return pairs


def pair_matches(pair, row):
    def arguments(value):
        return {key: item for key, item in value.items() if key not in {"_timeout_seconds", "_background"}}
    return (pair[0] == row.get("id") and pair[1] == row["name"]
            and arguments(pair[2]) == arguments(row["arguments"]) and pair[3] == row.get("result"))


def verify_held_probe(receipt, requests, snapshot, home, nonce, cancel):
    accepted, binding, calls = receipt["accepted_run"], snapshot["binding"], receipt["calls"]
    first, second, release = receipt["first_turn_ns"], receipt["second_turn_ns"], snapshot["release_ns"]
    replies = receipt.get("all_replies", [])
    require(len(replies) >= 2 and f"PARENT-PROBE-{nonce}" in replies[1], "The held probe lacks its actual visible reply marker.")
    histories = [parent_call_pairs(request) for request in requests if context_paths(request) is None]
    controls = [row for row in calls if row["name"] == "check_agent_run" and row.get("success") is True
                and row.get("failure_code") is None
                and row["arguments"].get("RunId", row["arguments"].get("runId")) == accepted["run_id"]
                and row["arguments"].get("Cancel", row["arguments"].get("cancel", False)) is cancel
                and row["turn"] in ([2] if cancel else [1, 2])
                and binding["upstream_first_payload_ns"] < row.get("observed_ns", 0) < second
                and (not cancel or first < row["observed_ns"])]
    loads = [row for row in calls if row["name"] == "load_tool" and row.get("success") is True
             and row.get("failure_code") is None and row["turn"] in {1, 2}
             and row["arguments"].get("Name", row["arguments"].get("name")) == "check_agent_run"
             and row.get("result") == "check_agent_run"]
    chosen = None
    for control in controls:
        for history in histories:
            for pair in history:
                if pair_matches(pair, control) and any(pair_matches(loaded, load) and loaded[5] < pair[4]
                        and 0 < load.get("observed_ns", 0) < control["observed_ns"] and load["turn"] <= control["turn"]
                        for load in loads for loaded in history):
                    chosen = control
                    break
            if chosen is not None:
                break
        if chosen is not None:
            break
    require(chosen is not None, "The held probe lacks an exact status after a successful explicit control load.")
    status = json.loads(chosen["result"], object_pairs_hook=unique_object)
    require(isinstance(status, dict) and status.get("run_id") == accepted["run_id"]
            and status.get("scope_id") == accepted["scope_id"]
            and status.get("log_path") == binding["paths"]["log_path"]
            and status.get("artifact_directory") == binding["paths"]["artifact_dir"]
            and status.get("state") in ({"Cancelling", "Cancelled"} if cancel else {"Accepted", "Running"})
            and type(status.get("cancellation_requested")) is bool and type(status.get("dispatch_closed")) is bool
            and "terminal" in status, "The held probe status differs from the exact child state and paths.")
    if not cancel:
        require(status["cancellation_requested"] is False and status["dispatch_closed"] is False
                and status["terminal"] is None, "The held probe status does not describe an active child.")
    read_path = binding["paths"]["log_path"]
    log_bytes = actual_file(home, read_path).read_text()
    require(log_bytes, "The actual live child log is empty.")
    require(any(row["name"] == "file_read" and row.get("success") is True and row.get("failure_code") is None
                and row["turn"] == 2 and first < row.get("observed_ns", 0) < second < release
                and (not cancel or row["observed_ns"] < chosen["observed_ns"])
                and row["arguments"].get("Path", row["arguments"].get("path")) == read_path
                and any(line in row.get("result", "") for line in log_bytes.splitlines() if len(line) >= 16)
                and any(pair_matches(pair, row) for history in histories for pair in history)
                for row in calls), "The held probe lacks a fresh attributed read of the actual live child log.")
    return {"status_call_id": chosen["id"], "status_turn": chosen["turn"],
            "limit": "The status describes recorded state during this held request. It does not prove current provider health."}


def verify_trial(receipt, requests, snapshot, log, home, nonce, cancel):
    accepted = acceptance(json.dumps(receipt["accepted_run"]))
    require(receipt["status"] == "observed" and receipt["completed_turns"] >= 2, "The persistent parent flow did not complete.")
    consumption = receipt["delivery_observations"]
    require(consumption.get("complete") and len(consumption["deliveries"]) == 1, "The parent never consumed the actual terminal pair.")
    require(receipt["user_inputs"] == 2, "New input replaced automatic continuation.")
    binding = snapshot["binding"]
    require(binding and binding["accepted"] == accepted and snapshot["released"], "The child barrier lacks a canonical binding.")
    held_rows = [row for row in snapshot["requests"] if row["held"]]
    require(len(held_rows) == 1 and held_rows[0]["child"]
            and held_rows[0]["request_id"] == binding["request_id"]
            and held_rows[0]["admitted_ns"] == binding["arrived_ns"]
            and held_rows[0]["upstream_first_payload_ns"] == binding["upstream_first_payload_ns"],
            "The child binding differs from the actual held upstream request record.")
    require(0 < receipt["first_turn_ns"] < snapshot["release_ns"] <= receipt["release_ns"]
            and binding["arrived_ns"] < binding["upstream_first_payload_ns"] < receipt["second_turn_ns"] < snapshot["release_ns"]
            and binding["upstream_first_payload_ns"] <= binding["bound_ns"] <= snapshot["release_ns"],
            "The parent probe did not complete under the actual child barrier.")
    start_calls = accepted_start_calls(receipt["calls"])
    require(len(start_calls) == 1 and acceptance(start_calls[0]["result"]) == accepted
            and start_calls[0]["turn"] == 1, "The terminal lacks the one original initial-turn acceptance.")
    call_id, terminal = canonical_pairs(requests, accepted, start_calls[0]["id"], start_calls[0]["name"], receipt["calls"])
    positions = committed_positions(log, receipt["session_id"], accepted["run_id"], call_id)
    consumed = consumption["deliveries"][0]
    require(consumed["accepted"] == accepted and consumed["call_id"] == call_id and consumed["terminal"] == terminal
            and 0 < consumed["request_admitted_ns"] < consumed["response_first_payload_ns"] < consumed["parent_boundary_ns"],
            "The final parent response lacks actual model-consumption correlation.")
    observed_request = next((row for row in snapshot["requests"] if row["request_id"] == consumed["request_id"]), None)
    require(observed_request is not None and not observed_request["child"] and observed_request["response_payload_written"]
            and observed_request["admitted_ns"] == consumed["request_admitted_ns"]
            and observed_request["response_first_payload_ns"] == consumed["response_first_payload_ns"],
            "The consumption receipt differs from the actual relay request record.")
    require(0 < consumed["request_id"] <= len(requests), "The consumed request is absent from raw capture.")
    require(canonical_pairs([requests[consumed["request_id"] - 1]], accepted, start_calls[0]["id"], start_calls[0]["name"], receipt["calls"]) == (call_id, terminal),
            "The consumption receipt names a request without the actual terminal pair.")
    require(terminal.get("outcome") == ("Failed" if cancel else "Completed"), "The child terminal outcome differs.")
    if cancel:
        require(terminal.get("state") == "Cancelled" and terminal.get("reason") == "cancelled_by_parent",
                "The local cancellation disposition differs.")
    require(terminal.get("log_path") == binding["paths"]["log_path"]
            and terminal.get("artifact_directory") == binding["paths"]["artifact_dir"], "The terminal paths differ from bound child storage.")
    calls = receipt["calls"]
    starts = start_calls
    require(all(row["turn"] == 1 and row["arguments"].get("Agent", row["arguments"].get("agent")) == "child-run-worker"
                for row in starts),
            "The fixed flow did not start the assigned child exactly once.")
    verify_child_actions(requests, binding["paths"], nonce, cancel)
    probe = verify_held_probe(receipt, requests, snapshot, home, nonce, cancel)
    artifact = binding["paths"]["artifact_dir"] + (f"/partial-{nonce}.txt" if cancel else f"/complete-{nonce}.txt")
    contents = actual_file(home, artifact).read_text()
    require(contents == ("PARTIAL-" if cancel else "COMPLETE-") + nonce, "The actual child artifact content differs.")
    if cancel:
        held = next(row for row in snapshot["requests"] if row["held"])
        actual_write = write_completed(requests[held["request_id"] - 1], f"partial-{nonce}.txt")
        require(actual_write is not None, "The held request lacks its actual partial write result.")
        report_path, report_bytes = verify_partial_report(terminal, home, accepted, artifact, actual_write["result"])
        verify_cancellation_review(calls, accepted, terminal, consumed, report_path, report_bytes)
        require(not any(row["child"] and row["request_id"] > held["request_id"] for row in snapshot["requests"]),
                "The child entered a new provider operation after the cancellation barrier.")
    require(any(row["name"] == "file_read" and row["arguments"].get("Path", row["arguments"].get("path")) == artifact
                and row.get("success") and consumed["response_first_payload_ns"] < row["observed_ns"] < consumed["parent_boundary_ns"]
                and contents in row.get("result", "") for row in calls),
            "The parent did not read the actual artifact during automatic continuation.")
    require(("PARTIAL-" if cancel else "COMPLETE-") + nonce in receipt["last_reply"], "The automatic parent reply lacks actual artifact evidence.")
    return {"passed": True, "journal_positions": positions, "terminal_call_id": call_id, "held_probe": probe,
            "artifact_sha256": hashlib.sha256(contents.encode()).hexdigest(),
            "limit": "Local cancellation does not prove that an external provider stopped its accepted operation."}



def read_cancellation_report(home, report_path, artifact_directory):
    require(report_path == artifact_directory + "/cancelled-results.json"
            and str(PurePosixPath(report_path)) == report_path,
            "The report path differs from the exact confirmed artifact path.")
    target = actual_file(home, report_path)
    root = Path(home).resolve() / "data"
    for path in [target, *target.parents]:
        require(not path.is_symlink(), "The cancellation report path contains a link.")
        if path == root:
            break
    require(stat.S_ISREG(target.stat().st_mode), "The cancellation report is not a regular file.")
    result = subprocess.run(["docker", "exec", "--user", "netclaw", os.environ["EVAL_CONTAINER_NAME"],
                             "cat", "--", report_path], capture_output=True, check=True, timeout=30)
    capture = Path(os.environ["TMPDIR_EVAL"]) / "child-runs/actual-cancelled-results.json"
    capture.parent.mkdir(parents=True, exist_ok=True)
    capture.write_bytes(result.stdout)
    return result.stdout


def verify_partial_report(terminal, home, accepted, artifact, confirmed_write_result):
    checkpoint = terminal.get("checkpoint")
    require(isinstance(checkpoint, dict) and isinstance(checkpoint.get("CompletedRound"), int)
            and not isinstance(checkpoint["CompletedRound"], bool) and checkpoint["CompletedRound"] > 0,
            "The cancelled child lacks a completed durable checkpoint.")
    summary = checkpoint.get("Summary")
    activity = checkpoint.get("ConfirmedActivity")
    require(isinstance(summary, str) and summary.strip() and confirmed_write_result
            and confirmed_write_result in summary and isinstance(activity, dict)
            and activity.get("ConfirmedChangedFiles") == [artifact],
            "The cancelled checkpoint lacks confirmed partial artifact activity.")
    report_path = terminal["artifact_directory"] + "/cancelled-results.json"
    require(report_path in terminal.get("output", "") and not terminal.get("warning"),
            "The fixed local-report case lacks a confirmed framework report.")
    report_bytes = read_cancellation_report(home, report_path, terminal["artifact_directory"])
    report = json.loads(report_bytes.decode("utf-8"), object_pairs_hook=unique_object)
    require(isinstance(report, dict), "The actual partial report is not an object.")
    require(report.get("run_id") == accepted["run_id"] and report.get("state") == "Cancelled"
            and report.get("summary") == summary and report.get("confirmed_activity") == activity,
            "The actual partial report differs from the durable terminal checkpoint.")
    require(report.get("external_effects") == "Recorded receipts describe known local results. They do not prove external effects stopped.",
            "The partial report omits the canonical external-effect limit.")
    return report_path, report_bytes


def verify_cancellation_review(calls, accepted, terminal, consumed, report_path, report_bytes):
    require(any(row["name"] == "skill_load" and row.get("success") and row["turn"] == 1
                and row["arguments"].get("Name", row["arguments"].get("name")) == "agent-coordination"
                and "A cancellation acceptance does not prove dispatch closure or terminal completion."
                in row.get("result", "") for row in calls),
            "The parent lacks actual coordination guidance before the cancellation turn.")
    body = report_bytes.decode("utf-8")
    require(any(row["name"] == "file_read" and row.get("success")
                and row["arguments"].get("Path", row["arguments"].get("path")) == report_path
                and row["arguments"].get("StartLine", row["arguments"].get("startLine")) in (None, 0)
                and row["arguments"].get("Limit", row["arguments"].get("limit")) in (None, 0)
                and row.get("result") == body
                and consumed["response_first_payload_ns"] < row["observed_ns"] < consumed["parent_boundary_ns"]
                for row in calls), "The parent lacks a full actual partial-report read after terminal consumption.")
    controls = []
    for row in calls:
        if row["name"] != "check_agent_run" or not row.get("success"):
            continue
        args = row["arguments"]
        if args.get("RunId", args.get("runId")) != accepted["run_id"]:
            continue
        status = json.loads(row.get("result", ""), object_pairs_hook=unique_object)
        require(isinstance(status, dict) and status.get("run_id") == accepted["run_id"]
                and status.get("scope_id") == accepted["scope_id"], "The cancellation status has a foreign owner.")
        terminal_body = status.get("terminal")
        require("terminal" in status and (terminal_body is None or isinstance(terminal_body, str)),
                "The status terminal does not use the canonical JSON-string representation.")
        if terminal_body is None:
            require(status.get("state") in {"Accepted", "Running", "Cancelling"},
                    "A terminal status lacks its canonical terminal JSON string.")
        else:
            status["terminal"] = json.loads(terminal_body, object_pairs_hook=unique_object)
            require(isinstance(status["terminal"], dict) and status["terminal"] == terminal
                    and status.get("state") == terminal.get("state"),
                    "The status terminal differs from the actual consumed terminal.")
        if args.get("Cancel", args.get("cancel", False)) is True:
            require(status.get("cancellation_requested") is True and type(status.get("dispatch_closed")) is bool
                    and status.get("state") in {"Cancelling", "Cancelled"},
                    "The cancellation result lacks distinct admission and closure facts.")
            if not status["dispatch_closed"]:
                require(status.get("state") == "Cancelling" and status.get("terminal") is None,
                        "Cancellation admission falsely claims terminal completion before dispatch closure.")
            if status.get("state") == "Cancelled":
                require(status["dispatch_closed"] and status.get("terminal") == terminal,
                        "The cancellation result lacks the actual cancelled terminal.")
        controls.append((row, status))
    require(any(row["arguments"].get("Cancel", row["arguments"].get("cancel", False)) is False
                and status.get("cancellation_requested") is True and status.get("dispatch_closed") is True
                and status.get("state") == "Cancelled" and status.get("terminal") == terminal
                and consumed["response_first_payload_ns"] < row["observed_ns"] < consumed["parent_boundary_ns"]
                for row, status in controls),
            "The parent lacks explicit dispatch closure and the actual cancelled terminal status.")


def prompts(nonce, cancel):
    kind = "partial" if cancel else "complete"
    content = ("PARTIAL-" if cancel else "COMPLETE-") + nonce
    task = (f"Trial {nonce}. Write exactly {content}, without a newline, to {kind}-{nonce}.txt in your runtime artifact_dir. "
            "Use file_write. Use no shell command. Return the full artifact path and its content in your final result.")
    initial = (("Load agent-coordination by canonical name before this task. " if cancel else "")
               + f"Delegate this task once to child-run-worker: {task} "
               "Acknowledge the accepted child immediately with a brief visible reply. Do not wait for its result. "
               "When its later result arrives, read its artifact with file_read and report the actual content. "
               "If the child is cancelled, fully read cancelled-results.json from its confirmed artifact directory. "
               "Read the confirmed partial artifact. Then check its status once to confirm dispatch closure and the cancelled terminal. "
               "Cancellation admission alone does not prove either fact. Local closure does not prove external effects stopped. "
               "Report the actual content and the local cancellation state.")
    probe = ("For accepted child {{RUN_ID}}, inspect its current state and its authorized log path. "
             "Read that log with file_read. Use the deferred child control through normal tool discovery. "
             + ("After that read, cancel this child. Do not start another child. " if cancel else "Keep the child active. ")
             + f"Reply with PARENT-PROBE-{nonce} and a concrete fact from the actual log.")
    return initial, probe


def observer_command():
    path = Path(os.environ["NETCLAW_CHILD_OBSERVER"]).resolve()
    require(path.is_file(), "Build the canonical SessionObserver project before this run.")
    return ["dotnet", str(path)] if path.suffix == ".dll" else [str(path)]


def invoke_observer(port, prompt, evidence, mode, nonce, probe="", session="", output_format="json"):
    evidence = Path(evidence)
    evidence.mkdir(parents=True, exist_ok=False)
    data = {"DaemonEndpoint": "http://127.0.0.1:" + os.environ["EVAL_PORT"],
            "FixtureEndpoint": f"http://127.0.0.1:{port}", "Nonce": nonce,
            "InitialPrompt": prompt, "ProbePrompt": probe, "ProbeMarker": f"PARENT-PROBE-{nonce}",
            "EvidenceDirectory": str(evidence), "TimeoutSeconds": int(os.environ["PROMPT_TIMEOUT"]),
            "Mode": mode, "SessionId": session, "OutputFormat": output_format}
    input_path = evidence / "observer-input.json"
    input_path.write_text(json.dumps(data, indent=2))
    command = observer_command() + [str(input_path)]
    result = subprocess.run(command, capture_output=True, text=True,
                            timeout=int(os.environ["PROMPT_TIMEOUT"]) + 15)
    (evidence / "observer.stdout").write_text(result.stdout)
    (evidence / "observer.stderr").write_text(result.stderr)
    require(result.returncode == 0, "The persistent observer failed; inspect its receipt and raw outputs.")
    receipt = json.loads((evidence / "observer-receipt.json").read_text())
    validate_prompt_receipt(receipt, data)
    return receipt, result.stdout


def control(port, action, **body):
    request = urllib.request.Request(f"http://127.0.0.1:{port}/control/{action}", data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=40) as response:
        return json.load(response)


def evidence_requests(directory):
    return [json.loads(path.read_bytes()) for path in sorted(Path(directory).glob("request-*.json"))]


def session_logs(home, session, run_ids):
    require(run_ids, "The observed parent has no accepted run identifiers.")
    patterns = [r"child_run_accepted owner=" + re.escape(session) + r" runId=" + re.escape(run_id)
                + r" journalSequence=\d+" for run_id in run_ids]
    matches = []
    # Version-2 parent logs are direct envelope children. Child logs remain excluded.
    for path in sorted((Path(home) / "data/sessions").glob("*/logs/session.log")):
        text = path.read_text(errors="replace")
        if any(re.search(pattern, text) for pattern in patterns):
            matches.append(text)
    require(len(matches) == 1, "The observed parent lacks exactly one canonical session diagnostic log.")
    return matches[0]


def legacy_observer_mode(case, prompt_ordinal):
    require(isinstance(prompt_ordinal, int) and not isinstance(prompt_ordinal, bool), "The prompt ordinal is invalid.")
    if case == "coding_context_worktree_handoff":
        require(1 <= prompt_ordinal <= 4, "The worktree handoff prompt ordinal is outside its four-prompt contract.")
        return "collect" if prompt_ordinal == 3 else "turn"
    require(case in {"subagent_headless_ambiguous_task", "subagent_specialization_precedence",
                     "subagent_project_scope_declaration", "subagent_session_scratch_disposable",
                     "approval_natural_subagent_project_review", "coordination_analyze_plan",
                     "coordination_attachment_blocked", "productive_parent_child", "coordination_implement_review", "coordination_stale_incomplete", "coordination_conflicting_evidence"} and prompt_ordinal == 1,
            "The legacy child case or prompt ordinal is invalid.")
    return "collect"


def validate_prompt_receipt(receipt, data):
    require(receipt.get("status") == "observed", "The current prompt did not complete successfully.")
    require(receipt.get("prompt_nonce") == data["Nonce"] and receipt.get("observer_mode") == data["Mode"],
            "The observer receipt belongs to another prompt invocation.")
    require(receipt.get("initial_prompt_sha256") == hashlib.sha256(data["InitialPrompt"].encode()).hexdigest(),
            "The observer receipt has another prompt payload.")
    require(isinstance(receipt.get("last_reply"), str) and receipt["last_reply"].strip(), "The current final reply is absent.")
    require(isinstance(receipt.get("session_id"), str) and receipt["session_id"].strip(), "The current session identifier is absent.")
    if data["SessionId"]:
        require(receipt.get("session_id") == data["SessionId"], "The observer receipt has another resumed session.")


def collect(port, prompt, session, output_format, evidence, case, prompt_ordinal):
    mode = legacy_observer_mode(case, prompt_ordinal)
    receipt, output = invoke_observer(port, prompt, evidence, mode, uuid.uuid4().hex, session=session,
                                      output_format=output_format)
    receipt["case"] = case
    receipt["prompt_ordinal"] = prompt_ordinal
    if mode == "turn":
        receipt["verified_deliveries"] = []
        (Path(evidence) / "verified-receipt.json").write_text(json.dumps(receipt, indent=2))
        print(output, end="")
        return
    requests = evidence_requests(Path(os.environ["TMPDIR_EVAL"]) / "child-runs/relay")
    require(receipt["accepted_runs"] and receipt["delivery_observations"]["complete"],
            "The legacy response lacks an accepted child and actual terminal consumption.")
    log = session_logs(os.environ["EVAL_HOME"], receipt["session_id"],
                       [accepted["run_id"] for accepted in receipt["accepted_runs"]])
    deliveries = []
    accepted_starts = accepted_start_calls(receipt["calls"])
    for accepted in receipt["accepted_runs"]:
        starts = [call for call in accepted_starts if acceptance(call["result"]) == accepted]
        require(len(starts) == 1, "The legacy terminal lacks the exact original start occurrence.")
        call_id, terminal = canonical_pairs(requests, accepted, starts[0]["id"], starts[0]["name"], receipt["calls"])
        require(terminal.get("outcome") == "Completed", "A legacy child did not complete normally.")
        positions = committed_positions(log, receipt["session_id"], accepted["run_id"], call_id)
        deliveries.append({"accepted": accepted, "terminal": terminal, "journal_positions": positions})
    receipt["verified_deliveries"] = deliveries
    (Path(evidence) / "verified-receipt.json").write_text(json.dumps(receipt, indent=2))
    print(output, end="")
    if output_format == "text":
        for row in deliveries:
            print("[child:result] " + json.dumps(row["terminal"]))



def cli_acceptance(port, root, nonce):
    session = "eval/child-cli-" + nonce
    initial, _ = prompts(nonce, False)
    initial += " Include CLI-ACCEPTED-" + nonce + " in your immediate reply."
    environment = {**os.environ, "NETCLAW_HOME": os.environ["EVAL_HOME"],
                   "NETCLAW_DAEMON_ENDPOINT": "http://127.0.0.1:" + os.environ["EVAL_PORT"]}
    result = subprocess.run([os.environ["NETCLAW_BIN"], "chat", "-p", "--resume", session, initial],
                            env=environment, capture_output=True, text=True, timeout=int(os.environ["PROMPT_TIMEOUT"]))
    (root / "actual-cli.stdout").write_text(result.stdout)
    (root / "actual-cli.stderr").write_text(result.stderr)
    require(result.returncode == 0, "The actual headless CLI failed.")
    bodies = re.findall(r"^\[tool:result\] spawn_agent → (.+)$", result.stdout, re.MULTILINE)
    require(len(bodies) == 1, "The actual CLI lacks one start acceptance result.")
    accepted = acceptance(bodies[0])
    return verify_cli_acceptance(result.stdout, evidence_requests(root / "relay"),
                                 session_logs(os.environ["EVAL_HOME"], session, [accepted["run_id"]]), session, nonce)


def verify_cli_acceptance(stdout, requests, log, session, nonce):
    bodies = re.findall(r"^\[tool:result\] spawn_agent → (.+)$", stdout, re.MULTILINE)
    require(len(bodies) == 1, "The actual CLI lacks one start acceptance result.")
    accepted = acceptance(bodies[0])
    require("CLI-ACCEPTED-" + nonce in stdout, "The actual CLI lacks the parent's acceptance reply.")
    matches = {}
    for request in requests:
        if context_paths(request) is not None:
            continue
        calls = {call["id"]: call for message in request.get("messages", []) for call in message.get("tool_calls", [])}
        for message in request.get("messages", []):
            identifier = message.get("tool_call_id")
            if message.get("role") != "tool" or identifier not in calls:
                continue
            call = calls[identifier]
            if call.get("function", {}).get("name") != "spawn_agent":
                continue
            try:
                body = json.loads(message_text(message.get("content")))
            except ValueError:
                continue
            if body == accepted:
                matches[identifier] = body
    require(len(matches) == 1, "The model history lacks the same actual CLI acceptance object.")
    pattern = r"child_run_accepted owner=" + re.escape(session) + r" runId=" + re.escape(accepted["run_id"]) + r" journalSequence=(\d+)"
    positions = re.findall(pattern, log)
    require(len(positions) == 1, "The actual CLI acceptance lacks one durable accepted diagnostic.")
    return {"passed": True, "session_id": session, "accepted_run": accepted,
            "accepted_journal_position": int(positions[0]),
            "limit": "The CLI smoke proves initial acceptance only. Persistent trials prove later results separately."}

def run(port):
    require(os.environ.get("RUNS") == "1", "Each child trial requires one fresh harness invocation with RUNS=1.")
    case = os.environ["NETCLAW_EVAL_CASE"]
    require(case in CASES, "Select one child case.")
    nonce = uuid.uuid4().hex
    root = Path(os.environ["TMPDIR_EVAL"]) / "child-runs"
    root.mkdir(exist_ok=True)
    report = {"case": case, "nonce": nonce, "passed": False, "fresh_invocation": True,
              "home": os.environ["EVAL_HOME"], "container_name": os.environ["EVAL_CONTAINER_NAME"],
              "image_reference": os.environ["NETCLAW_IMAGE"], "user_inputs": 1 if case == "child_run_cli_acceptance" else 2,
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "cli_sha256": hashlib.sha256(Path(os.environ["NETCLAW_BIN"]).read_bytes()).hexdigest(),
              "observer_sha256": hashlib.sha256(Path(os.environ["NETCLAW_CHILD_OBSERVER"]).read_bytes()).hexdigest()}
    error = None
    try:
        report["container_id"] = subprocess.check_output(
            ["docker", "inspect", "--format", "{{.Id}}", os.environ["EVAL_CONTAINER_NAME"]], text=True).strip()
        control(port, "child-setup", nonce=nonce, case=case)
        if case == "child_run_cli_acceptance":
            report.update(cli_acceptance(port, root, nonce))
            return 0
        initial, probe = prompts(nonce, case == "child_run_partial_cancel")
        receipt, _ = invoke_observer(port, initial, root / "observer", "cancel" if case.endswith("cancel") else "held",
                                     nonce, probe)
        snapshot = control(port, "snapshot")
        (root / "fixture-snapshot.json").write_text(json.dumps(snapshot, indent=2))
        report.update(verify_trial(receipt, evidence_requests(root / "relay"), snapshot,
                                   session_logs(os.environ["EVAL_HOME"], receipt["session_id"],
                                                [receipt["accepted_run"]["run_id"]]), os.environ["EVAL_HOME"], nonce,
                                   case == "child_run_partial_cancel"))
        report["session_id"] = receipt["session_id"]
        artifact_path = snapshot["binding"]["paths"]["artifact_dir"] + (
            f"/partial-{nonce}.txt" if case.endswith("cancel") else f"/complete-{nonce}.txt")
        (root / "actual-artifact.txt").write_bytes(actual_file(os.environ["EVAL_HOME"], artifact_path).read_bytes())
    except (AssertionError, KeyError, ValueError, OSError, subprocess.SubprocessError) as failure:
        error = type(failure).__name__ + ": " + str(failure)
        report["error"] = error
    finally:
        # Preserve an aborted barrier state before teardown. Never remove another trial's resource.
        snapshot = control(port, "snapshot")
        (root / "fixture-final-snapshot.json").write_text(json.dumps(snapshot, indent=2))
        if not snapshot["released"]:
            control(port, "child-abort")
        (root / "trial-receipt.json").write_text(json.dumps(report, indent=2))
        (Path(os.environ["TMPDIR_EVAL"]) / "stdout_background-results.txt").write_text(json.dumps(
            {"runtime": [], "model": [report], "errors": [error] if error else [], "passed": report["passed"]}, indent=2))
    return 0 if report["passed"] else 1



def verified_final_response(evidence):
    directory = Path(evidence)
    data = json.loads((directory / "observer-input.json").read_text())
    receipt = json.loads((directory / "verified-receipt.json").read_text())
    validate_prompt_receipt(receipt, data)
    return receipt["last_reply"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["serve", "run", "collect", "assert-delivery", "log-path", "final-response"])
    parser.add_argument("--port", type=int)
    parser.add_argument("--prompt-file")
    parser.add_argument("--session", default="")
    parser.add_argument("--format", choices=["json", "text"], default="json")
    parser.add_argument("--evidence")
    parser.add_argument("--agent", default="")
    parser.add_argument("--case", default="")
    parser.add_argument("--prompt-ordinal", type=int, default=1)
    args = parser.parse_args()
    if args.action == "final-response":
        print(verified_final_response(args.evidence), end="")
        return 0
    if args.action in {"assert-delivery", "log-path"}:
        receipts = [json.loads(path.read_text()) for path in Path(args.evidence).glob("observer-*/verified-receipt.json")]
        deliveries = [row for receipt in receipts for row in receipt.get("verified_deliveries", [])]
        require(deliveries, "No durable later child delivery exists.")
        if args.agent:
            require(any(call["name"] == "spawn_agent" and call["arguments"].get("Agent", call["arguments"].get("agent")) == args.agent
                        for receipt in receipts for call in receipt["calls"]), "The requested child profile was not used.")
        if args.action == "log-path":
            require(len(deliveries) == 1, "The fixed consumer requires one actual child log.")
            path = actual_file(os.environ["EVAL_HOME"], deliveries[0]["terminal"]["log_path"])
            require(path.is_file(), "The actual child log is absent.")
            print(path)
        return 0
    if args.action == "serve":
        fixture = ChildFixture(os.environ["BACKGROUND_EVAL_UPSTREAM"], os.environ["NETCLAW_EVAL_MODEL_ID"],
                               os.environ.get("NETCLAW_EVAL_PROVIDER_API_KEY", ""),
                               Path(os.environ["TMPDIR_EVAL"]) / "child-runs/relay")
        server = ThreadingHTTPServer(("127.0.0.1", 0), child_handler(fixture))
        print(server.server_port, flush=True)
        try:
            server.serve_forever()
        finally:
            server.server_close()
    elif args.action == "collect":
        collect(args.port, Path(args.prompt_file).read_text(), args.session, args.format, args.evidence, args.case, args.prompt_ordinal)
    else:
        return run(args.port)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
