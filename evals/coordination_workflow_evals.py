"""Prepare two eval-owned coordination cases for the existing collect path."""

import argparse
import hashlib
import json
import re
from pathlib import Path
import uuid

from background_fixture import message_text
from child_run_evals import acceptance, actual_file, context_paths, evidence_requests, require, validate_prompt_receipt
from coordination_artifact_evals import occurrences


CHILD_TOOLS = {"load_tool", "search_tools", "file_list", "file_read", "file_search", "file_write",
               "tool_output_read", "skill_load", "skill_read_resource"}
PARENT_TOOLS = (CHILD_TOOLS - {"file_write"}) | {"spawn_agent", "check_agent_run", "attach_file"}


CASES = {"coordination_analyze_plan": "delivered", "coordination_attachment_blocked": "blocked"}


def source_identity(fixture_root):
    truth = json.loads((fixture_root / "truth.json").read_text())
    hashes = {}
    for name in truth["source_files"]:
        source = (fixture_root / name).resolve()
        require(source.is_relative_to(fixture_root.resolve()), "The source fixture escapes its root.")
        hashes[name] = hashlib.sha256(source.read_bytes()).hexdigest()
    revision = "sha256:" + hashlib.sha256("".join(
        name + "\0" + hashes[name] + "\n" for name in sorted(hashes)).encode()).hexdigest()
    require(hashes == truth["source_files"] and revision == truth["revision"], "The source fixture identity differs.")
    return hashes, revision


def prepare(fixture_root, eval_home, evidence):
    hashes, revision = source_identity(fixture_root)
    runtime_root = "/home/netclaw/.netclaw/workspaces/coordination-" + uuid.uuid4().hex
    workspace = actual_file(eval_home, runtime_root)
    workspace.mkdir(parents=True, exist_ok=False)
    # The daemon uses a different UID. Only this new eval workspace needs artifact write access.
    workspace.chmod(0o777)
    evidence.mkdir(parents=True, exist_ok=False)
    for name in hashes:
        destination = actual_file(eval_home, runtime_root + "/" + name)
        destination.parent.mkdir(parents=True, exist_ok=True)
        with destination.open("xb") as output:
            output.write((fixture_root / name).read_bytes())
        archived = evidence / name
        archived.parent.mkdir(parents=True, exist_ok=True)
        archived.write_bytes(destination.read_bytes())
    setup = {"source_root": runtime_root, "source_hashes": hashes, "revision": revision,
             "findings_path": runtime_root + "/findings.md", "plan_path": runtime_root + "/plan.md"}
    (evidence / "setup.json").write_text(json.dumps(setup, indent=2))
    return setup


def prompt(setup):
    return f"""Produce a complete Markdown plan for the neutral catalog at {setup['source_root']}.
The source is source/catalog.py. Its source identity is {setup['revision']}; this is not a Git commit.
Accepted objective: reject duplicate identifiers before publication and preserve the order of valid records.
Keep exact identifier equality. The User owns any later decision about case-insensitive identifiers.
Use the available coordination guidance and its analyze-plan workflow and artifact templates.
Assign one source analysis task to an available analyst, then assign one complete plan task to task-worker.
Use exactly these two child runs.
Discover the actual profiles. Give each child its explicit source scope and its distinct artifact path.
The findings path is {setup['findings_path']}.
The plan path is {setup['plan_path']}.
The analyst may read the source and write the findings only. Do not edit the source or run commands.
Each child must use file_write for its complete artifact. Do not use file_edit for this narrow case.
Read the complete findings before you assign the plan task.
Include both artifact paths in the plan task or its context. Give the worker the reviewed findings.
The worker may read the source and findings and write the complete plan only.
Read the complete plan before you attach that Markdown file through the normal attachment action.
Use full direct reads for these small artifacts. Do not substitute a summary or a path for review.
Report a real attachment limitation accurately. Do not claim delivery without a successful attachment.
Use the findings and plan template sections and columns. Complete every field without template placeholders.
Use this reference schema so the artifacts have stable traceability:
E1 and F1 concern Catalog.refresh. E2 and F2 concern Catalog.read.
Each evidence row must cite the exact relevant source lines, their line range, and the source identity.
Use A1/C1 for the duplicate-rejection and prior-state check, owned by Catalog.refresh and linked to F1.
Use A2/C2 for the valid-record order check, owned by Catalog.read and linked to F2.
Use R1 for the risk to prior state and valid order. Link it to C1 and C2.
Use Q1 for the unresolved identifier case rule, with decision owner User.
State actual checks and their limits. A plan does not prove implementation, CI, release, or deployment.
Keep the parent responsible for evidence review and user delivery.
""".rstrip("\n")


def blocked_config(source, destination):
    config = json.loads(source.read_text())
    personal = config.setdefault("Tools", {}).setdefault("AudienceProfiles", {}).setdefault("Personal", {})
    personal["ReadFiles"] = {"Mode": "All", "Roots": []}
    personal["AttachFiles"] = {"Mode": "None", "Roots": []}
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("x") as output:
        json.dump(config, output, indent=2)


def workflow_stages(receipt, events, requests, setup, eval_home):
    require(len(receipt["accepted_runs"]) == 2, "The workflow requires exactly two accepted child runs.")
    calls, _ = occurrences(events, receipt["session_id"])
    require(all(call["name"] in PARENT_TOOLS for call in calls),
            "The parent calls an action outside the assigned workflow.")
    starts = [call for call in calls if call["name"] == "spawn_agent" and call["failure"] is None]
    stages = []
    for key in ("findings_path", "plan_path"):
        path = setup[key]
        assigned = [call for call in starts if path in (call["arguments"].get("Task", "") + "\n" +
                    (call["arguments"].get("Context") or ""))]
        if key == "findings_path":
            assigned = [call for call in assigned if str(call["arguments"].get("Agent", "")).lower() != "task-worker"]
        else:
            assigned = [call for call in assigned if setup["findings_path"] in (
                call["arguments"].get("Task", "") + "\n" + (call["arguments"].get("Context") or ""))]
        require(len(assigned) == 1, "The workflow lacks one distinct assignment for " + key + ".")
        start = assigned[0]
        agent = start["arguments"].get("Agent")
        require(isinstance(agent, str) and agent, "The child assignment lacks its actual profile name.")
        if key == "plan_path":
            require(agent.lower() == "task-worker", "The plan does not use the canonical task-worker profile.")
        else:
            discovery = []
            for request in requests:
                if context_paths(request) is not None:
                    continue
                for message in request.get("messages", []):
                    text = message_text(message.get("content"))
                    # The runtime puts the current profile index in a volatile user nudge.
                    contextual = message.get("role") == "system" or (
                        message.get("role") == "user" and text.startswith("[system: ") and text.endswith("]"))
                    if contextual and "[available-subagents" in text:
                        discovery.extend(re.findall(r"^## " + re.escape(agent) + r"\n([^\n]+)", text, re.MULTILINE | re.IGNORECASE))
            require(any("analyst" in (agent + " " + description).lower()
                        or "analysis" in description.lower() for description in discovery),
                    "The analyst profile lacks actual parent discovery evidence.")
        accepted = acceptance(start["result"])
        require(accepted in receipt["accepted_runs"], "The stage lacks the observed canonical acceptance.")
        verified = [row for row in receipt["verified_deliveries"] if row["accepted"] == accepted]
        require(len(verified) == 1, "The stage lacks one verified child terminal delivery.")
        terminal = verified[0]["terminal"]
        child_requests = [request for request in requests if (paths := context_paths(request)) is not None
                          and paths["log_path"] == terminal["log_path"]
                          and paths["artifact_dir"] == terminal["artifact_directory"]]
        require(child_requests, "The stage lacks its actual attributed child provider captures.")
        content = actual_file(eval_home, path).read_bytes()
        writes = set()
        for request in child_requests:
            pending = {}
            for message in request.get("messages", []):
                for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                    function = call.get("function", {})
                    require(function.get("name") in CHILD_TOOLS,
                            "The child calls an action outside its assigned artifact task.")
                    if function.get("name") == "file_write":
                        args = json.loads(function["arguments"])
                        require(args.get("Path") == path, "The child writes outside its assigned artifact.")
                        require(call.get("id") and call["id"] not in pending,
                                "The child reuses an unresolved artifact write identifier.")
                        pending[call["id"]] = args
                if message.get("role") == "tool" and message.get("tool_call_id") in pending:
                    args = pending.pop(message["tool_call_id"])
                    expected = f"Successfully wrote {len(content)} bytes to {path}"
                    if message_text(message.get("content")) == expected and args.get("Content", "").encode() == content:
                        writes.add(message["tool_call_id"])
        require(len(writes) == 1, "The stage lacks one paired full artifact write with exact bytes.")
        stages.append(start)
    require(acceptance(stages[0]["result"])["run_id"] != acceptance(stages[1]["result"])["run_id"]
            and stages[0]["result_sequence"] < stages[1]["call_sequence"],
            "The workflow does not preserve distinct ordered child stages.")


def contract(case, fixture_root, eval_home, setup_directory, observer_directory, relay_directory):
    require(case in CASES, "The coordination case is unknown.")
    setup = json.loads((setup_directory / "setup.json").read_text())
    hashes, revision = source_identity(fixture_root)
    require(setup["source_hashes"] == hashes and setup["revision"] == revision,
            "The setup belongs to another source fixture.")
    for name, expected in hashes.items():
        supplied = actual_file(eval_home, setup["source_root"] + "/" + name)
        require(hashlib.sha256(supplied.read_bytes()).hexdigest() == expected,
                "The source supplied to the children changed.")
    data = json.loads((observer_directory / "observer-input.json").read_text())
    receipt = json.loads((observer_directory / "verified-receipt.json").read_text())
    validate_prompt_receipt(receipt, data)
    require(data["Mode"] == "collect" and data["InitialPrompt"] == prompt(setup)
            and receipt["case"] == case and receipt["prompt_ordinal"] == 1,
            "The observer does not describe the current coordination attempt.")
    require(receipt["accepted_runs"] and receipt["delivery_observations"]["complete"],
            "The current attempt lacks complete child consumption.")
    events = [json.loads(line) for line in (observer_directory / "session-output.jsonl").read_text().splitlines() if line.strip()]
    workflow_stages(receipt, events, evidence_requests(relay_directory), setup, eval_home)
    artifacts = observer_directory / "coordination-artifacts"
    artifacts.mkdir(exist_ok=False)
    for key in ("findings_path", "plan_path"):
        source = actual_file(eval_home, setup[key])
        if source.is_file():
            (artifacts / source.name).write_bytes(source.read_bytes())
    return {"session_id": data["SessionId"] or receipt["session_id"], "prompt_nonce": data["Nonce"],
            "findings_path": setup["findings_path"], "plan_path": setup["plan_path"], "delivery": CASES[case]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    setup = commands.add_parser("prepare")
    for name in ("fixture-root", "eval-home", "evidence"):
        setup.add_argument("--" + name, type=Path, required=True)
    denied = commands.add_parser("blocked-config")
    for name in ("source", "destination"):
        denied.add_argument("--" + name, type=Path, required=True)
    assertion = commands.add_parser("contract")
    assertion.add_argument("--case", choices=CASES, required=True)
    for name in ("fixture-root", "eval-home", "setup-directory", "observer-directory", "relay-directory"):
        assertion.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    if args.action == "prepare":
        print(prompt(prepare(args.fixture_root, args.eval_home, args.evidence)), end="")
    elif args.action == "blocked-config":
        blocked_config(args.source, args.destination)
    else:
        print(json.dumps(contract(args.case, args.fixture_root, args.eval_home,
                                  args.setup_directory, args.observer_directory, args.relay_directory), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
