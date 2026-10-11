"""Verify four fixed child process cases with actual typed evidence.

The journal projector supplies canonical event data. Provider prose supplies no
approval, process, file, or durable transition evidence.
"""

from collections import Counter
import json
from pathlib import PurePosixPath

from child_run_evals import (acceptance, bind_request, canonical_pairs, committed_positions,
                             context_paths, pair_matches, parent_call_pairs,
                             require, require_observed_rejections, unique_object)
from coordination_artifact_evals import occurrences
from background_fixture import message_text
from coordination_negative_evals import provider_arguments


CASES = {"child_run_routed_skill", "child_run_approval_once",
         "child_run_owner_recovery", "child_run_approval_cancel"}
EXPIRED_NOTICE = ("That approval prompt has expired — the session moved on or restarted. "
                  "Please re-issue the request and I'll ask again if approval is needed.")
MANIFESTS = {"ChildRunAccepted": "cra-v1", "ChildRunEvent": "cre-v1",
             "ToolApprovalRequested": "tar-v1", "ToolApprovalResolved": "tares-v1",
             "InputAdmitted": "ia-v1", "ToolTaskAdopted": "tta-v1", "InputClosed": "ic-v1"}
RATIONALE = "Execute the assigned process eval step."


def pair_key(row):
    return row["id"], row["name"], json.dumps(row["arguments"], sort_keys=True), row["result"]


def response_calls(wire):
    require(isinstance(wire, str), "The actual provider response wire is absent.")
    headers, body = wire.split("\r\n\r\n", 1)
    require(headers.splitlines()[0].endswith("200 OK"), "The actual provider response failed.")
    if "Content-Type: text/event-stream" in headers:
        payloads = [json.loads(line[6:], object_pairs_hook=unique_object) for line in body.splitlines()
                    if line.startswith("data: ") and line != "data: [DONE]"]
        require("data: [DONE]" in body, "The actual provider response lacks its end marker.")
        return [call for payload in payloads for choice in payload["choices"]
                for call in choice["delta"].get("tool_calls", [])]
    require("Content-Type: application/json" in headers, "The actual provider response has another representation.")
    return [call for choice in json.loads(body, object_pairs_hook=unique_object)["choices"]
            for call in choice["message"].get("tool_calls", [])]


def verify_calls(receipt, events, requests):
    actual, files = occurrences(events, receipt["session_id"])
    require(not files and len(actual) == len(receipt["calls"]), "The process calls or File outputs differ.")
    expected = Counter()
    for ordinal, (row, dto) in enumerate(zip(receipt["calls"], actual), 1):
        sequence = dto["call_sequence"]
        turn = 1 + sum(event["output"]["Type"] == "turn_completed" for event in events[:sequence - 1])
        require(type(row.get("occurrence")) is int and row["occurrence"] == ordinal
                and type(row.get("turn")) is int and row["turn"] == turn
                and type(row.get("observed_ns")) is int and row["observed_ns"] == events[sequence - 1]["observed_ns"]
                and row.get("success") is (dto["failure"] is None)
                and row.get("failure_code") == dto["failure"]
                and pair_matches((dto["id"], dto["name"], dto["arguments"], dto["result"]), row),
                "The process receipt differs from its actual DTO occurrence.")
        expected[pair_key(row)] += 1
    require_observed_rejections(receipt["calls"], actual)
    seen = Counter()
    for request in requests:
        if context_paths(request) is not None:
            continue
        current = Counter(pair_key({"id": pair[0], "name": pair[1], "arguments": pair[2], "result": pair[3]})
                          for pair in parent_call_pairs(request))
        for key in expected:
            require(current[key] <= expected[key], "A provider history duplicates an observed process call.")
        seen |= current
    require(all(seen[key] == count for key, count in expected.items()),
            "An observed process call lacks complete provider occurrence evidence.")
    return actual


def verify_process_trial(case, receipt, events, requests, snapshot, journal, actions, effects, setup, log):
    require(case in CASES and snapshot["case"] == setup["case"] == case, "The process case differs.")
    session, nonce = receipt["session_id"], setup["nonce"]
    require(receipt["status"] == "observed" and receipt.get("error") is None
            and receipt["prompt_nonce"] == nonce and receipt["user_inputs"] == 2,
            "The process case lacks normal completion or uses a substitute task.")
    actual = verify_calls(receipt, events, requests)
    expected_inputs = setup["parent_inputs"]
    require(len(expected_inputs) == 2 and len(set(expected_inputs)) == 2
            and all(isinstance(text, str) and text for text in expected_inputs), "The two fixed parent inputs differ.")
    seen_inputs = Counter()
    for request in requests:
        if context_paths(request) is not None:
            continue
        # SessionState.BuildNudgeMessage supplies this exact user-role envelope.
        users = [message_text(message.get("content")) for message in request["messages"]
                 if message.get("role") == "user" and not (
                     message_text(message.get("content")).startswith("[system: ")
                     and message_text(message.get("content")).endswith("]"))]
        require(all(text in expected_inputs for text in users)
                and all(users.count(text) <= 1 for text in expected_inputs)
                and users == [text for text in expected_inputs if text in users],
                "The parent provider history contains a substitute or repeated task.")
        seen_inputs |= Counter(users)
    require(seen_inputs == Counter(expected_inputs), "The provider lacks the exact two parent inputs.")
    turns = [event for event in events if event["output"]["Type"] == "turn_completed"]
    require(type(receipt["completed_turns"]) is int and receipt["completed_turns"] == len(turns) >= 3,
            "The process turn boundaries differ.")
    require(receipt["first_turn_ns"] == turns[0]["observed_ns"]
            and receipt["second_turn_ns"] == turns[1]["observed_ns"], "The process probe boundaries differ.")
    previous = 0
    for index, action in enumerate(actions, 1):
        require(type(action["sequence"]) is int and action["sequence"] == index
                and type(action["observed_ns"]) is int and action["observed_ns"] >= previous
                and type(action["after_event_sequence"]) is int
                and 0 < action["after_event_sequence"] <= len(events), "The observer action order differs.")
        previous = action["observed_ns"]
    operation = "skill_load" if case == "child_run_routed_skill" else "spawn_agent"
    starts = [row for row in receipt["calls"] if row["name"] == operation and row["success"] is True]
    require(len(starts) == 1, "The process case lacks exactly one successful start.")
    start = starts[0]
    accepted = acceptance(start["result"])
    require(accepted == receipt["accepted_run"] and receipt["accepted_runs"] == [accepted]
            and start["turn"] == 1 and start["arguments"] == setup["start_arguments"],
            "The process acceptance or original start differs.")
    binding = snapshot["binding"]
    require(binding["accepted"] == accepted, "The process binding differs.")
    if case == "child_run_owner_recovery":
        require(snapshot["released"] is False and snapshot.get("recovery_released") is True,
                "Recovery lacks its separate subscriber response release.")
    else:
        require(snapshot["released"] is True, "The normal child response remains held.")
    held = [row for row in snapshot["requests"] if row["held"] is True]
    require(len(held) == 1 and held[0]["child"] is True and held[0]["request_id"] == binding["request_id"],
            "The process case lacks one actual held child response.")
    require(type(binding["request_id"]) is int and 0 < binding["request_id"] <= len(requests),
            "The process binding lacks its provider request.")
    statuses = [row for row in receipt["calls"] if row["name"] == "check_agent_run" and row["success"] is True
                and row["arguments"].get("RunId") == accepted["run_id"] and row["turn"] == 1]
    require(len(statuses) == 1, "The initial turn lacks its exact child status.")
    paths = bind_request(requests[binding["request_id"] - 1], accepted, json.loads(statuses[0]["result"]), nonce)
    require(paths == binding["paths"], "The provider context differs from the actual status paths.")
    child_requests = [request for request in requests if context_paths(request) == paths]
    require(child_requests, "The process case lacks actual child dispatch.")
    call_id, terminal = canonical_pairs(requests, accepted, start["id"], operation, receipt["calls"])
    positions = committed_positions(log, session, accepted["run_id"], call_id)
    delivery = receipt["delivery_observations"]
    require(delivery.get("complete") is True and len(delivery["deliveries"]) == 1,
            "The parent lacks canonical terminal consumption.")
    consumed = delivery["deliveries"][0]
    require(consumed["accepted"] == accepted and consumed["terminal"] == terminal and consumed["call_id"] == call_id,
            "The consumed terminal differs from its actual provider pair.")
    records = [row for row in snapshot["requests"] if row["request_id"] == consumed["request_id"]]
    require(len(records) == 1 and records[0]["child"] is False
            and records[0]["response_payload_written"] is True
            and records[0]["admitted_ns"] == consumed["request_admitted_ns"]
            and records[0]["response_first_payload_ns"] == consumed["response_first_payload_ns"]
            and type(consumed["request_admitted_ns"]) is int and type(consumed["response_first_payload_ns"]) is int
            and 0 < consumed["request_admitted_ns"] < consumed["response_first_payload_ns"],
            "The terminal lacks an actual relay response acknowledgement.")
    require(any(event["observed_ns"] == consumed["parent_boundary_ns"] for event in turns[2:]),
            "The terminal lacks a later actual parent boundary.")
    require(canonical_pairs([requests[consumed["request_id"] - 1]], accepted, start["id"], operation,
                            receipt["calls"]) == (call_id, terminal), "The relay request lacks the consumed terminal.")
    rows = []
    previous = 0
    for row in journal:
        require(row["persistence_id"] == "session-" + session and type(row["sequence_nr"]) is int
                and row["sequence_nr"] > previous and row["serializer_id"] == 150
                and row["manifest"] == MANIFESTS[row["event_type"]] and row["data"]["session_id"] == session,
                "The canonical journal projection differs.")
        previous = row["sequence_nr"]
        rows.append(row)
    def one(event_type, **fields):
        matches = [row for row in rows if row["event_type"] == event_type
                   and all(row["data"].get(key) == value for key, value in fields.items())]
        require(len(matches) == 1, "The journal lacks one attributed " + event_type + ".")
        return matches[0]
    durable = one("ChildRunAccepted", run_id=accepted["run_id"])
    ingress = [row for row in rows if row["event_type"] == "InputAdmitted"]
    require(len(ingress) == 2 and [row["data"]["user_message"] for row in ingress] == expected_inputs
            and all(row["data"].get("run_id") is None
                    and row["data"].get("user_role") == "User"
                    and isinstance(row["data"].get("source_message_id"), str)
                    and row["data"]["source_message_id"].startswith("signalr:") for row in ingress)
            and ingress[0]["sequence_nr"] < durable["sequence_nr"], "The original parent ingress differs.")
    authority = ingress[0]["data"]["authority"]
    require(authority == setup["authority"] and isinstance(authority.get("TurnId"), str) and authority["TurnId"]
            and all(type(authority.get(key)) is type(value) and authority[key] == value
                    for key, value in setup["ingress"].items()), "The original ingress authority differs.")
    require(sum(row["event_type"] == "ChildRunAccepted" for row in rows) == 1
            and durable["sequence_nr"] == positions["accepted"]
            and durable["data"]["scope_id"] == accepted["scope_id"]
            and durable["data"]["source_operation"] == operation
            and durable["data"]["original_call_id"] == start["id"]
            and durable["data"]["authority"] == setup["authority"], "The durable original authority differs.")
    for kind, position in [("TerminalRecorded", "terminal_recorded"), ("ResultPrepared", "result_prepared"),
                           ("DeliveryAdmitted", "delivery_admitted")]:
        row = one("ChildRunEvent", run_id=accepted["run_id"], kind=kind)
        require(row["sequence_nr"] == positions[position], "The journal and commit diagnostics differ.")
        if kind != "DeliveryAdmitted":
            recorded = row["data"]["terminal"]
            expected = terminal
            if kind == "TerminalRecorded" and terminal["state"] == terminal["outcome"] == "Completed":
                expected = dict(terminal)
                for field, canonical_path in [("log_path", paths["log_path"]),
                                               ("artifact_directory", paths["artifact_dir"])]:
                    require(recorded[field] in (None, canonical_path),
                            "The raw terminal storage path differs from its canonical run path.")
                    expected[field] = recorded[field]
            require(recorded == expected, "The durable terminal differs from its canonical delivery.")
    admitted = one("ChildRunEvent", run_id=accepted["run_id"], kind="DeliveryAdmitted")
    adopted = one("ToolTaskAdopted", run_id=accepted["run_id"])
    require(admitted["data"]["authority"] == adopted["data"]["authority"] == setup["authority"]
            and admitted["data"]["input_id"] in adopted["data"]["input_ids"]
            and positions["result_prepared"] < admitted["sequence_nr"] == positions["delivery_admitted"]
            < adopted["sequence_nr"], "The child continuation lacks its original durable authority.")
    artifact, content = setup["artifact_path"], setup["artifact_bytes"]
    require(artifact.startswith(paths["artifact_dir"] + "/") and ".." not in PurePosixPath(artifact).parts
            and str(PurePosixPath(artifact)) == artifact and effects["files"].get(artifact) == content
            and type(content) is bytes and content, "The actual artifact bytes differ.")
    require(any(row["name"] == "file_read" and row["success"] is True and row["failure_code"] is None
                and row["arguments"].get("Path") == artifact and row["result"] == content.decode()
                and row["arguments"].get("StartLine") in (None, 0) and row["arguments"].get("Limit") in (None, 0)
                and turns[1]["observed_ns"] < row["observed_ns"] < consumed["parent_boundary_ns"]
                for row in receipt["calls"]), "The parent lacks a full actual artifact review.")
    require(content.decode() in receipt["last_reply"], "The final reply lacks actual artifact evidence.")
    if case == "child_run_routed_skill":
        require(terminal["state"] == terminal["outcome"] == "Completed", "The routed child did not complete.")
        require(setup["overlay"] and all(setup["overlay"] in json.dumps(request) for request in child_requests),
                "The actual routed child lacks the full skill overlay.")
        writes = Counter()
        for request in child_requests:
            current = Counter(pair_key({"id": p[0], "name": p[1], "arguments": p[2], "result": p[3]})
                              for p in parent_call_pairs(request) if p[1] == "file_write")
            writes |= current
        require(sum(writes.values()) == 1 and provider_arguments(json.loads(next(iter(writes))[2])) ==
            {"Content": content.decode(), "Path": artifact, "_rationale": RATIONALE}
            and next(iter(writes))[3] == f"Successfully wrote {len(content)} bytes to {artifact}",
            "The routed write lacks its exact successful receipt.")
    elif case == "child_run_owner_recovery":
        require(terminal["state"] == "Lost" and terminal["outcome"] == "Failed"
                and terminal["reason"] == "owner_restart_lost" and terminal.get("warning"),
                "Recovery lacks the canonical uncertain Lost result.")
        crash = snapshot["crash"]
        require(crash["signal"] == "SIGKILL" and crash["old_exited"] is True and crash["new_ready"] is True
                and type(crash["old_pid"]) is int and type(crash["new_pid"]) is int
                and crash["old_pid"] > 0 and crash["new_pid"] > 0
                and all(isinstance(crash[key], str) and crash[key] for key in ("old_start", "new_start"))
                and (crash["old_pid"], crash["old_start"]) != (crash["new_pid"], crash["new_start"]),
                "Recovery lacks an actual owned daemon replacement.")
        require(not any(row["child"] and row["request_id"] > held[0]["request_id"] for row in snapshot["requests"]),
                "Recovery dispatched a fresh child request.")
        resumes = [row for row in actions if row["kind"] in {"resume_started", "resume_completed"}]
        require([row["kind"] for row in resumes] == ["resume_started", "resume_completed"]
                and all(row["data"] == {"session_id": session} for row in resumes), "Recovery lacks same-session resume.")
        require(any(event["output"]["Type"] == "session_joined"
                    and resumes[0]["after_event_sequence"] < event["sequence"] <= resumes[1]["after_event_sequence"]
                    for event in events), "Recovery lacks an actual subscriber join before release.")
    else:
        verify_approval(case, receipt, events, requests, snapshot, rows, actions, effects, setup, accepted, terminal, one)
    return {"passed": True, "case": case, "session_id": session, "accepted_run": accepted,
            "journal_positions": positions, "terminal_call_id": call_id,
            "limit": "The fixed local case proves no remote effect certainty or real-model acceptance."}


def verify_approval(case, receipt, events, requests, snapshot, journal, actions, effects, setup, accepted, terminal, one):
    prompts = [event for event in events if event["output"]["Type"] == "tool_interaction"]
    require(len(prompts) == 1, "The child lacks exactly one real approval prompt.")
    prompt = prompts[0]
    dto = prompt["output"]
    requested = one("ToolApprovalRequested", run_id=accepted["run_id"])
    request = requested["data"]
    require(dto["ToolName"] == "shell_execute" and dto["CallId"] == request["call_id"]
            and dto["RequesterSenderId"] == request["requester"] == setup["authority"]["RequesterSenderId"]
            and dto["InteractionCwd"] == request["cwd"] == setup["shell_cwd"]
            and request["authority"] == setup["authority"]
            and isinstance(request["candidates"], list) and request["candidates"]
            and dto.get("InteractionDisplayText") == setup["shell_arguments"]["Command"]
            and isinstance(request["attempt_id"], str) and request["attempt_id"],
            "The actual approval prompt lacks its original durable authority and candidate.")
    offers = []
    for response in snapshot["response_wires"]:
        number = response["request_id"]
        require(type(number) is int and 0 < number <= len(requests), "The actual response lacks its provider request.")
        if context_paths(requests[number - 1]) != snapshot["binding"]["paths"]:
            continue
        record = next(row for row in snapshot["requests"] if row["request_id"] == number)
        for call in response_calls(response["wire"]):
            if call["function"]["name"] == "shell_execute":
                require(record["child"] is True and record["response_payload_written"] is True,
                        "The pending child call lacks an actual response write.")
                offers.append((number, call))
    require(len(offers) == 1 and offers[0][1]["id"] == request["original_call_id"]
            and provider_arguments(json.loads(offers[0][1]["function"]["arguments"], object_pairs_hook=unique_object))
            == {**setup["shell_arguments"], "_rationale": RATIONALE},
            "The consent subject differs from the actual pending child call.")
    options = [option["Key"] for option in dto["InteractionOptions"]]
    require(options == request["options"] and "approve_once" in options, "The actual prompt options differ.")
    resolved = one("ToolApprovalResolved", run_id=accepted["run_id"], call_id=request["call_id"], attempt_id=request["attempt_id"])
    require(requested["sequence_nr"] < resolved["sequence_nr"], "The approval resolution precedes its request.")
    answers = [row for row in actions if row["kind"] == "answer_started"]
    completed = [row for row in actions if row["kind"] == "answer_completed"]
    require(len(answers) == len(completed) == 1 and answers[0]["sequence"] < completed[0]["sequence"]
            and answers[0]["data"] == completed[0]["data"] == {
                "call_id": dto["CallId"], "selected_key": "approve_once", "prompt_sequence": prompt["sequence"]}
            and prompt["sequence"] <= answers[0]["after_event_sequence"]
            and answers[0]["after_event_sequence"] >= next(event["sequence"] for event in events
                if event["observed_ns"] == receipt["second_turn_ns"]), "The actual approval answer differs.")
    require(effects["before_answer"].get(setup["effect_path"]) is None
            and effects["grants_before"] == effects["grants_after"] == setup["grant_bytes"],
            "The effect precedes consent or the case creates a persistent grant.")
    child_pairs = Counter()
    for captured in requests:
        if context_paths(captured) != snapshot["binding"]["paths"]:
            continue
        child_pairs |= Counter(pair_key({"id": p[0], "name": p[1], "arguments": p[2], "result": p[3]})
                               for p in parent_call_pairs(captured) if p[1] == "shell_execute")
    if case == "child_run_approval_once":
        require(resolved["data"]["decision"] == "ApprovedOnce" and terminal["state"] == terminal["outcome"] == "Completed"
                and effects["after_terminal"].get(setup["effect_path"]) == setup["effect_bytes"],
                "The approval lacks one actual completed effect.")
        require(sum(child_pairs.values()) == 1 and next(iter(child_pairs))[0] == request["original_call_id"]
                and provider_arguments(json.loads(next(iter(child_pairs))[2])) == {**setup["shell_arguments"], "_rationale": RATIONALE}
                and next(iter(child_pairs))[3] == setup["shell_result"], "The approved original retry differs or repeats.")
        barrier = snapshot["approval_answer"]
        require(barrier["call_id"] == dto["CallId"] and barrier["effect_absent"] is True
                and barrier["request_id"] < snapshot["protected_result_request_id"],
                "The protected result lacks a post-answer relay barrier.")
    else:
        require(resolved["data"]["decision"] == "Denied" and terminal["state"] == "Cancelled"
                and terminal["reason"] == "cancelled_by_parent" and not child_pairs
                and effects["after_terminal"].get(setup["effect_path"]) is None,
                "Cancellation lacks refusal or permits a protected effect.")
        cancelled = one("ChildRunEvent", run_id=accepted["run_id"], kind="CancellationRequested")
        closed = one("ChildRunEvent", run_id=accepted["run_id"], kind="DispatchClosed")
        require(cancelled["sequence_nr"] < closed["sequence_nr"]
                and resolved["sequence_nr"] < one("ChildRunEvent", run_id=accepted["run_id"], kind="TerminalRecorded")["sequence_nr"],
                "Cancellation lacks durable refusal and dispatch closure.")
        notices = [event for event in events if event["output"]["Type"] == "text" and event["output"].get("Text") == EXPIRED_NOTICE]
        consumed = [row for row in actions if row["kind"] == "cancellation_notice_consumed"]
        require(len(consumed) == 1 and consumed[0]["sequence"] < answers[0]["sequence"]
                and consumed[0]["after_event_sequence"] >= next(event["sequence"] for event in events
                    if event["observed_ns"] == receipt["delivery_observations"]["deliveries"][0]["parent_boundary_ns"])
                and any(event["sequence"] == consumed[0]["data"]["notice_sequence"]
                        <= answers[0]["after_event_sequence"] for event in notices)
                and any(event["sequence"] > answers[0]["after_event_sequence"] for event in notices),
                "The stale answer lacks a new expired-prompt notice.")
        require(any(row["name"] == "check_agent_run" and row["success"] is True
                    and row["arguments"].get("RunId") == accepted["run_id"]
                    and row["arguments"].get("Cancel") is True for row in receipt["calls"]),
                "The parent never cancelled the original child.")
        checkpoint = terminal.get("checkpoint")
        require(isinstance(checkpoint, dict) and type(checkpoint.get("CompletedRound")) is int
                and checkpoint["CompletedRound"] > 0 and not terminal.get("warning"),
                "The cancelled child lacks its canonical completed checkpoint.")
        report_path = terminal["artifact_directory"] + "/cancelled-results.json"
        body = effects["files"].get(report_path)
        require(type(body) is bytes and report_path in terminal["output"], "The actual cancellation report is absent.")
        partial = json.loads(body.decode(), object_pairs_hook=unique_object)
        require(partial.get("run_id") == accepted["run_id"] and partial.get("state") == "Cancelled"
                and partial.get("summary") == checkpoint["Summary"]
                and partial.get("confirmed_activity") == checkpoint["ConfirmedActivity"]
                and checkpoint["ConfirmedActivity"]["ConfirmedChangedFiles"] == [setup["artifact_path"]]
                and partial.get("external_effects") == "Recorded receipts describe known local results. They do not prove external effects stopped.",
                "The actual cancellation report differs from the durable checkpoint.")
        boundary = receipt["delivery_observations"]["deliveries"][0]["parent_boundary_ns"]
        reads = [row for row in receipt["calls"] if row["name"] == "file_read" and row["success"] is True
                 and row["arguments"].get("Path") == report_path and row["result"] == body.decode()
                 and row["arguments"].get("StartLine") in (None, 0) and row["arguments"].get("Limit") in (None, 0)
                 and row["turn"] >= 3 and row["observed_ns"] < boundary]
        statuses = []
        for row in receipt["calls"]:
            if row["name"] != "check_agent_run" or row["success"] is not True or row["turn"] < 3:
                continue
            if row["arguments"].get("RunId") != accepted["run_id"] or row["arguments"].get("Cancel") is not False:
                continue
            status = json.loads(row["result"], object_pairs_hook=unique_object)
            require(isinstance(status.get("terminal"), str), "The final status lacks its canonical terminal JSON string.")
            require(status.get("run_id") == accepted["run_id"] and status.get("scope_id") == accepted["scope_id"]
                    and status.get("state") == "Cancelled" and status.get("dispatch_closed") is True
                    and status.get("cancellation_requested") is True
                    and json.loads(status["terminal"], object_pairs_hook=unique_object) == terminal,
                    "The final cancellation status differs from its consumed terminal.")
            statuses.append(row)
        require(reads and statuses, "The parent lacks a full cancellation report read and final status.")
        terminal_id = receipt["delivery_observations"]["deliveries"][0]["call_id"]
        require(any(any(pair[0] == terminal_id and pair[5] < read[4] < status[4]
                            for pair in parent_call_pairs(capture))
                    for capture in requests if context_paths(capture) is None
                    for read in parent_call_pairs(capture) if any(pair_matches(read, row) for row in reads)
                    for status in parent_call_pairs(capture) if any(pair_matches(status, row) for row in statuses)),
                "The provider lacks terminal, report read, and final status in causal order.")
        require(not any(row["child"] and row["request_id"] > offers[0][0]
                        for row in snapshot["requests"]), "Cancellation permits a fresh child dispatch.")
