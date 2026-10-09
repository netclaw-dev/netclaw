"""Check logical coordination discovery from eval-owned runtime receipts."""

import json
from pathlib import Path
import re
import sys


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


if __name__ == "__main__":
    try:
        passed = verify(json.loads(Path(sys.argv[1]).read_text()), Path(sys.argv[2]).read_text(), Path(sys.argv[3]))
    except (OSError, ValueError, TypeError, IndexError, AttributeError):
        passed = False
    sys.exit(0 if passed else 1)
