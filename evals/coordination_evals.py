"""Check logical coordination discovery from eval-owned runtime receipts."""

from collections import Counter
import json
from pathlib import Path
import re
import sys

from child_run_evals import (REQUIRED_RATIONALE_ERROR, context_paths, evidence_requests, is_unexecuted_rationale_rejection,
                             parent_call_pairs, require, require_observed_rejections, validate_prompt_receipt)
from coordination_artifact_evals import load_json, occurrences


SKILL = "agent-coordination"
RESOURCE = "references/implement-review.md"
DISCOVERY_TOOLS = {"skill_load", "skill_read_resource", "load_tool", "search_tools", "tool_output_read"}
EVENT = re.compile(r"^\[\d{4}-\d\d-\d\dT[^\]\n]+\] (.*)$", re.MULTILINE)
CALL = re.compile(r"^(\S+) call_id=(\S+) args=(.*)$", re.DOTALL)
RESULT = re.compile(r"^(\S+) call_id=(\S+) result=(.*)$", re.DOTALL)


def verify(envelope, log, skill_directory):
    """Require ordered, successful, canonical load and one workflow receipt."""
    if not isinstance(envelope, dict) or not isinstance(envelope.get("response"), str):
        return False
    if not envelope.get("sessionId") or not envelope["response"].strip():
        return False
    calls = envelope.get("toolCalls")
    if not isinstance(calls, list):
        return False
    events = []
    matches = list(EVENT.finditer(log))
    for index, match in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(log)
        message = (match.group(1) + log[match.end():end]).rstrip("\n")
        kind, _, payload = message.partition(": ")
        events.append((kind, payload))
    accepted = []
    seen = set()
    for call in calls:
        if not isinstance(call, dict):
            return False
        name, call_id = call.get("toolName"), call.get("callId")
        if not isinstance(call_id, str) or not call_id or call_id in seen:
            return False
        seen.add(call_id)
        if name not in DISCOVERY_TOOLS:
            return False
        try:
            arguments = json.loads(call["argumentsJson"])
        except (KeyError, TypeError, ValueError):
            return False
        if not isinstance(arguments, dict):
            return False
        if name == "skill_load" and arguments.get("Name", "").lower() != SKILL:
            return False
        if name == "skill_read_resource" and arguments.get("SkillName", "").lower() != SKILL:
            return False
        if name == "skill_load" and arguments.get("Name", "").lower() == SKILL:
            expected = (skill_directory / "SKILL.md").read_text().split("---", 2)[2].strip()
            kind = "load"
        elif name == "skill_read_resource" and arguments.get("SkillName", "").lower() == SKILL:
            if arguments.get("ResourcePath") != RESOURCE:
                return False
            expected = (skill_directory / RESOURCE).read_text().rstrip("\n")
            kind = "resource"
        else:
            continue
        dispatched = None
        for event_index, (event, text) in enumerate(events):
            if event != "TOOL_CALL":
                continue
            match = CALL.fullmatch(text)
            if match and match.group(1, 2) == (name, call_id):
                if json.loads(match.group(3)) != arguments:
                    return False
                dispatched = event_index
                break
        if dispatched is None:
            return False
        for event_index, (event, text) in enumerate(events):
            if event != "TOOL_RESULT" or event_index <= dispatched:
                continue
            match = RESULT.fullmatch(text)
            if not match or match.group(1, 2) != (name, call_id):
                continue
            content = match.group(3)
            if kind == "load" and content.startswith("## Agent Coordination\n") and expected in content:
                accepted.append((kind, dispatched, event_index))
            elif kind == "resource":
                header, separator, body = content.partition("\n")
                if separator and header.startswith("path: ") and header.endswith(f"/{SKILL}/{RESOURCE}") and body == expected:
                    accepted.append((kind, dispatched, event_index))
            break
    return any(load[0] == "load" and resource[0] == "resource" and load[2] < resource[1]
               for load in accepted for resource in accepted)


def verify_observed(receipt, events, requests, skill_directory, observer_input):
    """Bind logical discovery to actual DTO occurrences and provider history."""
    validate_prompt_receipt(receipt, observer_input)
    require(receipt["case"] == "skill_coordination_discovery" and receipt["observer_mode"] == "turn"
            and type(receipt["prompt_ordinal"]) is int and receipt["prompt_ordinal"] == 1
            and type(receipt["completed_turns"]) is int and receipt["completed_turns"] == 1
            and type(receipt["user_inputs"]) is int and receipt["user_inputs"] == 1
            and receipt["accepted_runs"] == [] and receipt["verified_deliveries"] == [],
            "Discovery requires one observed parent turn without a child.")
    calls, files = occurrences(events, receipt["session_id"])
    require(not files, "Discovery emits an unexpected file.")
    observed = receipt["calls"]
    require_observed_rejections(observed, calls)
    require(len(observed) == len(calls), "The observer call count differs from the actual DTO occurrences.")
    canonical = lambda value: json.dumps(value, sort_keys=True)
    argument_identity = lambda value: canonical({key: item for key, item in value.items()
                                                if key not in {"_timeout_seconds", "_background"}})
    signature = lambda row: (row["id"], row["name"], argument_identity(row["arguments"]), row["result"])
    for ordinal, (row, actual) in enumerate(zip(observed, calls), 1):
        require(type(row.get("occurrence")) is int and row["occurrence"] == ordinal
                and type(row.get("turn")) is int and row["turn"] == 1
                and type(row.get("observed_ns")) is int
                and row["observed_ns"] == events[actual["call_sequence"] - 1]["observed_ns"]
                and row.get("success") is (actual["failure"] is None)
                and row.get("failure_code") == actual["failure"]
                and row.get("id") == actual["id"] and row.get("name") == actual["name"]
                and canonical(row.get("arguments")) == canonical(actual["arguments"])
                and row.get("result") == actual["result"],
                "The observer metadata differs from its actual DTO occurrence.")
    expected_pairs = Counter(signature(row) for row in calls)
    seen = Counter()
    for request in requests:
        require(context_paths(request) is None, "Discovery starts an unexpected child provider request.")
        pairs = parent_call_pairs(request)
        require(sum(len(message.get("tool_calls", [])) for message in request.get("messages", [])
                    if message.get("role") == "assistant") == len(pairs),
                "A provider history retains an unresolved discovery call.")
        current = Counter()
        for identifier, name, args, result, *_ in pairs:
            pair = (identifier, name, argument_identity(args), result)
            current[pair] += 1
            require(current[pair] <= expected_pairs[pair],
                    "A provider history lacks a distinct actual discovery occurrence.")
        seen |= current
    require(seen == expected_pairs, "An actual discovery DTO lacks its exact provider occurrence.")
    loads, resources = [], []
    body = (skill_directory / "SKILL.md").read_bytes().decode("utf-8").split("---", 2)[2].strip()
    workflow = (skill_directory / RESOURCE).read_bytes().decode("utf-8")
    for call in calls:
        name, arguments = call["name"], call["arguments"]
        require(name in DISCOVERY_TOOLS, "Discovery uses a forbidden physical tool or action tool.")
        if name == "skill_read_resource":
            require(isinstance(arguments.get("SkillName"), str) and arguments["SkillName"].lower() == SKILL
                    and arguments.get("ResourcePath") == RESOURCE, "Discovery requests another skill resource.")
        if call["result"] == REQUIRED_RATIONALE_ERROR:
            require(is_unexecuted_rationale_rejection(call["failure"], call["result"]),
                    "The canonical discovery rejection lacks its trusted failure code.")
        if is_unexecuted_rationale_rejection(call["failure"], call["result"]):
            continue
        require(call["failure"] is None, "A discovery call fails without the canonical typed metadata rejection.")
        if name == "skill_load":
            require(isinstance(arguments.get("Name"), str) and arguments["Name"].lower() == SKILL,
                    "Discovery executes a foreign skill.")
            require(call["result"].startswith("## Agent Coordination\n") and body in call["result"],
                    "The successful skill load lacks the full current canonical body.")
            loads.append(call)
        elif name == "skill_read_resource":
            header, separator, content = call["result"].partition("\n")
            require(separator and header.startswith("path: ") and header.endswith(f"/{SKILL}/{RESOURCE}")
                    and content == workflow, "The successful workflow result differs from its canonical resource.")
            resources.append(call)
    require(loads and len(resources) == 1 and any(load["result_sequence"] < resources[0]["call_sequence"] for load in loads),
            "Discovery lacks its ordered full skill load and single successful workflow.")
    reply, has_delta, boundaries = [], False, []
    for event in events:
        dto = event["output"]
        require(dto["Type"] != "error", "The parent emits an error.")
        if dto["Type"] == "text_delta":
            require(isinstance(dto.get("Text"), str), "A visible text delta is malformed.")
            has_delta = True
            reply.append(dto["Text"])
        elif dto["Type"] == "text" and not has_delta:
            require(isinstance(dto.get("Text"), str), "The visible parent text is malformed.")
            reply.append(dto["Text"])
        elif dto["Type"] == "turn_completed":
            boundaries.append(dto)
    require(len(boundaries) == 1 and boundaries[0].get("TurnOutcome") == "completed"
            and type(boundaries[0].get("TurnNumber")) is int and boundaries[0]["TurnNumber"] > 0
            and events[-1]["output"]["Type"] == "turn_completed"
            and "".join(reply).strip() and "".join(reply) == receipt["last_reply"],
            "Discovery lacks its actual visible answer at the completed parent boundary.")
    return {"passed": True, "session_id": receipt["session_id"], "calls": len(calls),
            "rejected_attempts": sum(is_unexecuted_rationale_rejection(c["failure"], c["result"]) for c in calls)}


if __name__ == "__main__":
    try:
        if sys.argv[1:2] == ["--observed"]:
            directory, relay, skill = map(Path, sys.argv[2:5])
            result = verify_observed(load_json((directory / "verified-receipt.json").read_bytes()),
                [load_json(line) for line in (directory / "session-output.jsonl").read_text().splitlines()],
                evidence_requests(relay), skill, load_json((directory / "observer-input.json").read_bytes()))
            print(json.dumps(result))
            passed = True
        else:
            passed = verify(json.loads(Path(sys.argv[1]).read_text()), Path(sys.argv[2]).read_text(), Path(sys.argv[3]))
    except (OSError, ValueError, TypeError, IndexError, AttributeError, AssertionError, KeyError) as error:
        print(str(error), file=sys.stderr)
        passed = False
    sys.exit(0 if passed else 1)
