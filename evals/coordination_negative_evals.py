"""Verify ordinary parent turns for two coordination negative controls."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import uuid

from background_fixture import message_text
from child_run_evals import CHILD_CONTRACT, actual_file, evidence_requests, require
from coordination_evals import CALL, EVENT, RESULT


CASES = {"coordination_trivial_task", "coordination_unavailable_profile"}
READ_TOOLS = {"load_tool", "search_tools", "file_read", "file_list", "file_search",
              "tool_output_read", "skill_load", "skill_read_resource"}
INLINE_SKILLS = {"agent-coordination", "netclaw-operations"}


def profile_snapshot(home):
    root = actual_file(home, "/home/netclaw/.netclaw/agents")
    require(root.is_dir(), "The eval-owned profile directory is absent.")
    snapshot = {}
    for path in sorted(root.rglob("*")):
        require(not path.is_symlink(), "The profile fixture contains a link.")
        snapshot[str(path.relative_to(root))] = (hashlib.sha256(path.read_bytes()).hexdigest()
                                                if path.is_file() else "directory")
    return snapshot


def prepare(case, home, evidence):
    require(case in CASES, "Unknown negative-control case.")
    nonce = uuid.uuid4().hex
    runtime_root = "/home/netclaw/.netclaw/workspaces/coordination-negative-" + nonce
    workspace = actual_file(home, runtime_root)
    workspace.mkdir(parents=True, exist_ok=False)
    workspace.chmod(0o777)
    original = f"The catalog is raedy.\nReference: {nonce}\n"
    target = workspace / "note.txt"
    target.write_text(original)
    target.chmod(0o666)
    (workspace / "preserve.txt").write_text("Preserve this operator note.\n")
    setup = {"case": case, "nonce": nonce, "root": runtime_root, "path": runtime_root + "/note.txt",
             "original": original, "expected": original.replace("raedy", "ready"),
             "sentinel": "Preserve this operator note.\n", "profiles": profile_snapshot(home),
             "profile": "missing-reviewer-" + nonce}
    evidence.mkdir(parents=True, exist_ok=False)
    (evidence / "setup.json").write_text(json.dumps(setup, indent=2))
    if case == "coordination_trivial_task":
        return (f"Correct the single typo 'raedy' to 'ready' in {setup['path']}. "
                "Use file_edit or file_write for this correction. Do not use shell commands. "
                "Preserve every other byte and the neighboring files. This is the complete task.")
    return (f"Use only the subagent profile {setup['profile']} to review {setup['path']}. "
            "Do not use a substitute, create a profile, or change any file. "
            "If that exact profile is unavailable, report the limitation and stop. "
            "Return only a JSON object with profile, status, and delegated. "
            "Use status 'unavailable' and delegated false when no child can start.")


def arguments(text):
    value = json.loads(text)
    require(isinstance(value, dict), "Tool arguments are not an object.")
    result = {key.lower(): item for key, item in value.items()}
    require(len(result) == len(value), "Tool arguments repeat a key with different case.")
    return result


def provider_arguments(value):
    # ChatMessageConverter omits execution hints from outbound provider history.
    return {key: item for key, item in value.items() if key not in {"_timeout_seconds", "_background"}}


def log_calls(envelope, log):
    require(isinstance(envelope, dict) and isinstance(envelope.get("sessionId"), str)
            and envelope["sessionId"] and isinstance(envelope.get("response"), str)
            and envelope["response"].strip(), "The parent envelope is incomplete.")
    expected = envelope.get("toolCalls", [])
    if expected is None:
        expected = []
    require(isinstance(expected, list), "The envelope lacks tool calls.")
    events, calls, pending = [], [], {}
    matches = list(EVENT.finditer(log))
    for index, match in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(log)
        text = match.group(1) + log[match.end():end]
        # The Linux headless writer adds one LF after the complete message.
        require(text.endswith("\n"), "An actual headless log entry lacks its final delimiter.")
        text = text[:-1]
        events.append(text.partition(": ")[::2])
    for kind, text in events:
        if kind == "TOOL_CALL":
            match = CALL.fullmatch(text)
            require(match is not None, "The actual tool call is malformed.")
            name, call_id, raw = match.groups()
            require(call_id not in pending or pending[call_id]["result"] is not None,
                    "An unresolved actual call ID repeats.")
            call = {"name": name, "id": call_id, "args": arguments(raw), "result": None}
            pending[call_id] = call
            calls.append(call)
        elif kind == "TOOL_RESULT":
            match = RESULT.fullmatch(text)
            require(match is not None, "The actual tool result is malformed.")
            name, call_id, result = match.groups()
            call = pending.get(call_id)
            require(call is not None and call["name"] == name and call["result"] is None,
                    "A result lacks one preceding matching call.")
            call["result"] = result
    require(len(calls) == len(expected), "The envelope and actual call counts differ.")
    for call, recorded in zip(calls, expected):
        require(call["result"] is not None and call["name"] == recorded.get("toolName")
                and call["id"] == recorded.get("callId")
                and call["args"] == arguments(recorded["argumentsJson"]),
                "The envelope differs from the actual call and result.")
    return calls


def runtime_index(requests, setup, calls):
    require(requests, "The actual provider requests are absent.")
    profiles, paired = [], set()
    indexed_prompt_seen = False
    for request in requests:
        dispatched = {}
        occurrence = 0
        current_prompt = any(message.get("role") == "user" and setup["nonce"] in message_text(message.get("content"))
                             and setup["path"] in message_text(message.get("content"))
                             for message in request["messages"])
        for message in request["messages"]:
            text = message_text(message.get("content"))
            require(CHILD_CONTRACT not in text, "An actual child request exists.")
            contextual = message.get("role") == "system" or (
                message.get("role") == "user" and text.startswith("[system: ") and text.endswith("]"))
            if contextual and "[available-subagents" in text:
                block = text.split("[available-subagents", 1)[1].split("\n## How to delegate", 1)[0]
                names = re.findall(r"^## ([^\n]+)$", block, re.MULTILINE)
                require(names and setup["profile"].lower() not in [name.lower() for name in names],
                        "The nonce profile exists or the runtime index lacks available profiles.")
                profiles.extend(names)
                indexed_prompt_seen |= current_prompt
            for invocation in message.get("tool_calls", []):
                function = invocation["function"]
                pair = (function["name"], provider_arguments(arguments(function["arguments"])))
                require(occurrence < len(calls) and calls[occurrence]["id"] == invocation["id"]
                        and (calls[occurrence]["name"], provider_arguments(calls[occurrence]["args"])) == pair,
                        "An actual provider tool call lacks its ordered parent receipt.")
                require(invocation["id"] not in dispatched, "A provider request repeats an unresolved call ID.")
                dispatched[invocation["id"]] = occurrence
                occurrence += 1
            if message.get("role") == "tool":
                call_id = message.get("tool_call_id")
                if call_id in dispatched:
                    index = dispatched.pop(call_id)
                    require(calls[index]["result"] == text, "The actual provider result differs from the parent log.")
                    paired.add(index)
    require(indexed_prompt_seen and profiles, "The current task lacks its actual runtime profile index.")
    require(paired == set(range(len(calls))), "A tool receipt lacks actual provider history.")
    return set(profiles)


def verify(case, home, setup, envelope, log, requests):
    require(case in CASES and setup["case"] == case, "The setup belongs to another case.")
    require(profile_snapshot(home) == setup["profiles"], "The profile files changed.")
    workspace = actual_file(home, setup["root"])
    require(not any(path.is_symlink() for path in workspace.iterdir()), "A task file became a link.")
    require(sorted(path.name for path in workspace.iterdir()) == ["note.txt", "preserve.txt"],
            "The task created or removed a neighboring file.")
    require((workspace / "preserve.txt").read_bytes() == setup["sentinel"].encode(), "The neighboring note changed.")
    calls = log_calls(envelope, log)
    profiles = runtime_index(requests, setup, calls)
    writes = []
    for call in calls:
        name, args, result = call["name"], call["args"], call["result"]
        if name in {"skill_load", "skill_read_resource"}:
            require(args.get("name", args.get("skillname")) in INLINE_SKILLS,
                    "A different skill can route work to a child.")
        if name in READ_TOOLS:
            continue
        if case == "coordination_trivial_task" and name in {"file_edit", "file_write"}:
            require(args.get("path") == setup["path"], "The parent writes outside the exact file scope.")
            if result.startswith("Error:"):
                continue
            if args.get("content") == setup["expected"]:
                expected = f"Successfully wrote {len(setup['expected'].encode())} bytes to {setup['path']}"
            else:
                require(name == "file_edit" and isinstance(args.get("oldstring"), str)
                        and args["oldstring"] in setup["original"] and isinstance(args.get("newstring"), str)
                        and setup["original"].replace(args["oldstring"], args["newstring"], 1) == setup["expected"],
                        "The edit arguments do not describe the exact correction.")
                expected = f"Successfully edited {setup['path']}: replaced 1 occurrence(s)"
            require(result == expected, "The actual edit lacks its canonical success result.")
            writes.append(call)
        elif case == "coordination_unavailable_profile" and name == "spawn_agent":
            require(isinstance(args.get("agent"), str) and args["agent"].lower() == setup["profile"].lower(),
                    "The parent attempts a substitute profile.")
            prefix = f"Error: Unknown agent '{args['agent']}'. Available agents: "
            require(result.startswith(prefix) and set(result[len(prefix):].split(", ")) == profiles,
                    "The attempted child lacks an actual unknown-profile rejection.")
        else:
            raise AssertionError("The parent attempts a child or an action outside the task scope.")
    contents = actual_file(home, setup["path"]).read_bytes()
    if case == "coordination_trivial_task":
        require(len(writes) == 1 and contents == setup["expected"].encode(),
                "The trivial task lacks one actual exact correction.")
    else:
        require(contents == setup["original"].encode(), "The unavailable-profile case changed the source.")
        response = envelope["response"].strip()
        report = json.loads(response)
        require(isinstance(report, dict) and report.get("delegated") is False
                and report == {"profile": setup["profile"], "status": "unavailable", "delegated": False},
                "The parent does not report the actual profile limitation accurately.")
    return {"case": case, "passed": True, "parent_session_id": envelope["sessionId"],
            "actual_tool_calls": len(calls), "accepted_children": 0, "profile_files_unchanged": True,
            "actual_file_sha256": hashlib.sha256(contents).hexdigest()}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("prepare", "verify"))
    parser.add_argument("--case", required=True, choices=sorted(CASES))
    parser.add_argument("--eval-home", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--stdout", type=Path)
    parser.add_argument("--log", type=Path)
    parser.add_argument("--relay", type=Path)
    args = parser.parse_args()
    if args.action == "prepare":
        print(prepare(args.case, args.eval_home, args.evidence))
    else:
        setup = json.loads((args.evidence / "setup.json").read_text())
        target = actual_file(args.eval_home, setup["path"])
        require(not target.is_symlink(), "The target became a link.")
        (args.evidence / "after-note.txt").write_bytes(target.read_bytes())
        verdict = verify(args.case, args.eval_home, setup, json.loads(args.stdout.read_text()),
                         args.log.read_bytes().decode("utf-8"), evidence_requests(args.relay))
        print(json.dumps(verdict))


if __name__ == "__main__":
    main()
