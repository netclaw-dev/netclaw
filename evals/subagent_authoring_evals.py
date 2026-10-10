"""Check the author guide and its background contract from actual turn receipts."""

import json
from pathlib import Path
import re
import sys

from child_run_evals import (REQUIRED_RATIONALE_ERROR, acceptance, evidence_requests,
                             is_unexecuted_rationale_rejection, require)
from coordination_artifact_evals import load_json
from coordination_evals import DISCOVERY_TOOLS, observed_explanation_calls, observed_explanation_reply


CASE = "skill_activation_subagent_authoring"
SKILL = "subagent-authoring"
PROMPT = """I want to define a custom Netclaw worker for a useful long task.
Review the current subagent-authoring guide before you explain its runtime contract.
Give a small example as an object with name, description, and prompt strings.
Explain the initial start result, later result, parent control, cancellation, partial evidence, and owner restart.
State whether a static tool-call limit applies, and name the other limits that still apply.
Do not create a definition, start a child, execute a command, or change a file.
Use logical skill tools. You may read relevant logical skill resources.
Return one JSON object without Markdown fences or extra text.
Use exactly these keys:
example, static_tool_call_limit, remaining_limits, start_result_fields, start_state, start_control_tool,
start_is_task_success, terminal_uses_start_call_id, start_completion_cancels_child,
later_parent_input_cancels_child, parent_control_loader, control_authority, child_peer_control,
cancel_admission_is_dispatch_closure, cancel_admission_is_terminal, partial_evidence_retained,
finalization_seconds, finalization_can_call_model, finalization_can_use_task_tools,
finalization_can_edit_project, finalization_can_approve_tools, unresolved_owner_restart_state,
owner_restart_resumes_child, cancelled_or_lost_approval_prompt_state, late_approval_grants_or_retries,
structured_findings_review_owner, recurrent_stop_requires_final_model_call.
Use null or an integer for static_tool_call_limit.
Use string arrays for remaining_limits and start_result_fields.
Use booleans for the keys that state a proposition.
Use a JSON object for example and strings for the other keys.
Use the following token vocabulary. Select the values that the current guide supports.
For remaining_limits, select from cancellation, inactivity_timeout, authorization, operation_deadline, static_tool_call_budget, and none.
For start_result_fields, select from run_id, scope_id, state, control_tool, summary, final_result, and error.
For start_state and unresolved_owner_restart_state, select Accepted, Running, Completed, Failed, Cancelled, or Lost.
For start_control_tool and parent_control_loader, select spawn_agent, check_agent_run, load_tool, or none.
For control_authority, select owning_session_and_original_eligible_requester, any_same_session_actor, or any_authenticated_requester.
For cancelled_or_lost_approval_prompt_state, select pending, expired, or approved.
For structured_findings_review_owner, select parent, child, or automatic.
Use an integer for finalization_seconds.
"""

BOOLEAN_ANSWERS = {
    "start_is_task_success": False, "terminal_uses_start_call_id": False,
    "start_completion_cancels_child": False, "later_parent_input_cancels_child": False,
    "child_peer_control": False, "cancel_admission_is_dispatch_closure": False,
    "cancel_admission_is_terminal": False, "partial_evidence_retained": True,
    "finalization_can_call_model": False, "finalization_can_use_task_tools": False,
    "finalization_can_edit_project": False, "finalization_can_approve_tools": False,
    "owner_restart_resumes_child": False, "late_approval_grants_or_retries": False,
    "recurrent_stop_requires_final_model_call": False,
}
STRING_ANSWERS = {
    "start_state": "Accepted", "start_control_tool": "check_agent_run",
    "parent_control_loader": "load_tool",
    "control_authority": "owning_session_and_original_eligible_requester",
    "unresolved_owner_restart_state": "Lost",
    "cancelled_or_lost_approval_prompt_state": "expired",
    "structured_findings_review_owner": "parent",
}
SET_ANSWERS = {
    "remaining_limits": {"cancellation", "inactivity_timeout", "authorization", "operation_deadline"},
    "start_result_fields": {"run_id", "scope_id", "state", "control_tool"},
}


def check_answer(text):
    answer = load_json(text)
    keys = set(BOOLEAN_ANSWERS) | set(STRING_ANSWERS) | set(SET_ANSWERS) | {
        "example", "static_tool_call_limit", "finalization_seconds"}
    require(isinstance(answer, dict) and set(answer) == keys, "The author answer has another key set.")
    example = answer["example"]
    require(isinstance(example, dict) and set(example) == {"name", "description", "prompt"}
            and all(isinstance(value, str) and value.strip() for value in example.values()),
            "The definition example lacks its three nonempty strings.")
    require(answer["static_tool_call_limit"] is None, "The author answer restores a static tool-call budget.")
    require(type(answer["finalization_seconds"]) is int and answer["finalization_seconds"] == 5,
            "The author answer has another finalization duration.")
    for key, expected in BOOLEAN_ANSWERS.items():
        require(answer[key] is expected, "The author answer differs at " + key + ".")
    for key, expected in STRING_ANSWERS.items():
        require(answer[key] == expected, "The author answer differs at " + key + ".")
    for key, expected in SET_ANSWERS.items():
        value = answer[key]
        require(isinstance(value, list) and all(isinstance(item, str) for item in value)
                and len(value) == len(expected) and set(value) == expected,
                "The author answer differs at " + key + ".")
    return answer


def retained_result(call, calls):
    """Require complete actual continuation windows when a result spills."""
    result = call["result"]
    marker = re.search(r"\n\n\[output truncated to (\d+) chars of (\d+); continue with tool_output_read "
                       r"using CallId='([^']+)' and a bounded Start/Limit window instead of re-running\]$", result)
    if marker is None:
        return result
    require(marker[3] == call["id"], "The spill refers to another source call.")
    total, cursor, chunks = int(marker[2]), 0, []
    for read in calls:
        if (read["name"] != "tool_output_read" or read["arguments"].get("CallId") != call["id"]
                or read["failure"] is not None):
            continue
        require(read["call_sequence"] > call["result_sequence"], "A continuation precedes its source result.")
        window = re.fullmatch(r"(.*)\n\[range start=(\d+) end=(\d+); next_start=(none|\d+); complete=(true|false)\]",
                              read["result"], re.DOTALL)
        require(window is not None, "The continuation lacks its canonical range metadata.")
        start, end = int(window[2]), int(window[3])
        requested_start = read["arguments"].get("Start")
        requested_start = 0 if requested_start is None else requested_start
        require(type(requested_start) is int and start == requested_start
                and end - start == len(window[1].encode("utf-16-le")) // 2 and 0 <= start <= end <= total,
                "The continuation has another range or length.")
        require((window[5] == "true" and window[4] == "none" and end == total)
                or (window[5] == "false" and window[4] == str(end) and end < total),
                "The continuation has another completion marker.")
        if start < cursor:
            prior = "".join(chunks).encode("utf-16-le")
            require(prior[start * 2:min(end, cursor) * 2] == window[1].encode("utf-16-le")[:(min(end, cursor) - start) * 2],
                    "The repeated continuation changes prior bytes.")
            if end <= cursor:
                continue
            content = window[1].encode("utf-16-le")[(cursor - start) * 2:].decode("utf-16-le")
        else:
            require(start == cursor, "The continuation leaves a gap in the full result.")
            content = window[1]
        chunks.append(content)
        cursor = end
    require(cursor == total, "The full retained result lacks complete actual reads.")
    return "".join(chunks)


def verify(receipt, events, requests, skill_root, observer_input):
    require(receipt.get("case") == CASE and observer_input.get("InitialPrompt") == PROMPT,
            "The author receipt has another case or fixed prompt.")
    calls = observed_explanation_calls(receipt, events, requests, observer_input)
    reply = observed_explanation_reply(receipt, events)
    answer_sequence = next(index for index, event in enumerate(events, 1)
                           if event["output"]["Type"] in {"text", "text_delta"} and event["output"].get("Text"))
    prior_calls = [call for call in calls if call["result_sequence"] < answer_sequence]
    guide = (skill_root / SKILL / "SKILL.md").read_bytes().decode("utf-8")
    require('version: "1.5.0"' in guide.split("---", 2)[1], "The frozen author guide has another version.")
    body = guide.split("---", 2)[2].strip()
    loads = []
    for call in calls:
        name, args = call["name"], call["arguments"]
        require(name in DISCOVERY_TOOLS, "The author task uses a physical tool or an action tool.")
        if call["result"] == REQUIRED_RATIONALE_ERROR:
            require(is_unexecuted_rationale_rejection(call["failure"], call["result"]),
                    "The canonical author rejection lacks its trusted code.")
        if is_unexecuted_rationale_rejection(call["failure"], call["result"]):
            continue
        require(call["failure"] is None, "An author tool fails without the exact typed nonexecution feedback.")
        if name == "skill_load":
            require(isinstance(args.get("Name"), str) and args["Name"].strip(), "The logical guide name is absent.")
            if call["result"].lstrip().startswith("{"):
                acceptance(call["result"])
                require(False, "The logical load starts a child instead of returning an inline guide.")
            if args["Name"].strip().lower() == SKILL:
                content = retained_result(call, prior_calls)
                require(content.startswith("## "), "The logical load does not return an inline guide.")
                require(content.startswith("## Subagent Authoring\nVersion: 1.5.0\n\n") and body in content,
                        "The author load lacks the full current canonical body.")
                if call["result_sequence"] < answer_sequence:
                    loads.append(call)
        elif name == "skill_read_resource":
            require(isinstance(args.get("SkillName"), str) and isinstance(args.get("ResourcePath"), str),
                    "The logical resource has invalid names.")
            skill, resource = args["SkillName"].strip().lower(), args["ResourcePath"]
            directory = (skill_root / skill).resolve()
            path = (directory / resource).resolve()
            require(directory.parent == skill_root.resolve() and path.is_relative_to(directory)
                    and path != directory and path.is_file(), "The resource is outside its frozen logical bundle.")
            header, separator, content = retained_result(call, calls).partition("\n")
            require(separator and header.startswith("path: ") and header.endswith("/" + skill + "/" + resource)
                    and content == path.read_bytes().decode("utf-8"), "The logical resource differs from its canonical bytes.")
    require(loads, "The author task lacks its full canonical guide load.")
    check_answer(reply)
    return {"passed": True, "session_id": receipt["session_id"], "calls": len(calls), "author_guide_version": "1.5.0"}


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["prompt"]:
            sys.stdout.write(PROMPT)
        else:
            require(len(sys.argv) == 5 and sys.argv[1] == "verify", "Use prompt or verify with evidence, relay, and skills.")
            directory, relay, skill_root = map(Path, sys.argv[2:])
            result = verify(load_json((directory / "verified-receipt.json").read_bytes()),
                [load_json(line) for line in (directory / "session-output.jsonl").read_text().splitlines()],
                evidence_requests(relay), skill_root, load_json((directory / "observer-input.json").read_bytes()))
            print(json.dumps(result))
    except (OSError, ValueError, TypeError, IndexError, AttributeError, AssertionError, KeyError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
