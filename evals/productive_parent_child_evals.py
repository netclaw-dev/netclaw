"""Check useful sequential feedback through the existing child collector.

The oracle uses real record files, parent DTO pairs, and child provider pairs.
It does not infer progress from a call count or a final text marker.
"""

import argparse
import hashlib
import json
from pathlib import Path
import uuid

from background_fixture import message_text
from child_run_evals import (require_observed_rejections, acceptance, actual_file, canonical_pairs, context_paths,
                             evidence_requests, is_unexecuted_rationale_rejection, require, validate_prompt_receipt)
from coordination_artifact_evals import load_json, occurrences
from coordination_implement_review_evals import spawn_argument

CASE = "productive_parent_child"
ROUNDS = {"parent": 65, "child": 35}
TOOLS = {"file_read", "file_write", "load_tool", "search_tools", "skill_load", "skill_read_resource",
         "set_working_directory"}


def encode(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False) + "\n"


def prepare(eval_home, evidence):
    nonce = uuid.uuid4().hex
    runtime_root = "/home/netclaw/.netclaw/workspaces/catalog-" + nonce
    workspace = actual_file(eval_home, runtime_root)
    workspace.mkdir(parents=True, exist_ok=False)
    workspace.chmod(0o777)
    evidence.mkdir(parents=True, exist_ok=False)
    setup = {"nonce": nonce, "root": runtime_root, "chains": {},
             "child_output": runtime_root + "/child-catalog.json",
             "combined_output": runtime_root + "/combined-catalog.json"}
    for owner, count in ROUNDS.items():
        paths = [runtime_root + "/" + owner + "/" + uuid.uuid4().hex + ".json" for _ in range(count)]
        records = []
        for index, path in enumerate(paths):
            record = {"id": uuid.uuid4().hex[:12], "value": uuid.uuid4().hex[:8],
                      "next": paths[index + 1] if index + 1 < count else None}
            content = encode(record)
            target = actual_file(eval_home, path)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content)
            records.append({"path": path, "content": content})
        setup["chains"][owner] = records
    # This truth stays outside the daemon's mounted home.
    (evidence / "setup.json").write_text(json.dumps(setup, indent=2))
    return setup


def prompt(setup):
    return f"""Reconstruct two neutral linked catalogs for trial {setup['nonce']}.
Start exactly one task-worker child before you reconstruct the parent catalog.
Discover the available profiles and use Agent=task-worker.
The parent must read its 65 records itself. Do not delegate those reads.
The parent entry is {setup['chains']['parent'][0]['path']}.
The child must read its 35 records itself. Do not read those records for the child.
The child entry is {setup['chains']['child'][0]['path']}.
Each small JSON record contains id, value, and next. Only the prior record reveals the next path.
Use one full file_read per record. Wait for its result before you request the next record.
Read every record once and follow next until null. Keep the exact order and values.
Do not guess paths, list directories, search files, run commands, or change source records.
Do not replace the actual records with summaries or invented values.
Give the child this trial identifier, its entry path, these rules, and its output path.
The child must write {setup['child_output']} with file_write.
Its JSON object must have nonce and records. Each records item must have only id and value.
The child must return its actual output path after the write succeeds.
The parent can do its independent catalog work while the child works.
Wait for automatic child delivery. Do not poll its status.
Read the complete child output with file_read after its actual terminal result arrives.
Then write {setup['combined_output']} with file_write.
Its JSON object must have nonce, parent_records, and child_records, with the exact ordered id/value items.
Write only the two assigned output files. Use file_write for each complete output.
Return only a JSON object with output, parent_records, child_records, and nonce.
Set output to the combined output path. Set the two counts to the actual catalog sizes.
""".rstrip("\n")


def records(setup, owner):
    return [{key: load_json(row["content"])[key] for key in ("id", "value")}
            for row in setup["chains"][owner]]


def direct_read(arguments):
    return arguments.get("StartLine") in (None, 0) and arguments.get("Limit") in (None, 0)


def provider_signature(identifier, arguments, result):
    # ChatMessageConverter omits only these execution hints from provider history.
    wire_arguments = {key: value for key, value in arguments.items()
                      if key not in {"_timeout_seconds", "_background"}}
    return identifier, json.dumps(wire_arguments, sort_keys=True), result


def provider_pairs(requests, setup, owner, child_bytes, terminal_id):
    """Preserve occurrence order within each actual captured provider history."""
    chain = setup["chains"][owner]
    expected = {row["path"]: row["content"] for row in chain}
    output = setup["child_output" if owner == "child" else "combined_output"]
    allowed_reads = set(expected) | {output}
    if owner == "parent":
        allowed_reads.add(setup["child_output"])
    pairs, edges, writes, artifact_reads, starts, declarations = {}, set(), set(), set(), set(), set()
    predecessors = {chain[index]["path"]: chain[index - 1]["path"] for index in range(1, len(chain))}
    for request in requests:
        pending, seen_reads, terminal_seen, write_seen = {}, set(), False, False
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                identifier = call.get("id")
                require(identifier and identifier not in pending, "An unresolved provider call identifier repeats.")
                function = call["function"]
                name, args = function["name"], load_json(function["arguments"])
                require(name in TOOLS | ({"spawn_agent"} if owner == "parent" else set()),
                        "A catalog actor calls an action outside its assigned task.")
                pending[identifier] = (name, args, terminal_seen, chain[-1]["path"] in seen_reads)
                if name == "file_read":
                    path = args.get("Path")
                    require(path in allowed_reads and direct_read(args), "A catalog read leaves its full-read scope.")
                    if path in predecessors and predecessors[path] in seen_reads:
                        edges.add((predecessors[path], path))
                elif name == "file_write":
                    require(args.get("Path") == output, "A catalog write leaves its assigned output.")
                elif name == "set_working_directory":
                    require(args.get("Path") == setup["root"], "A catalog declaration leaves its exact project root.")
            if message.get("role") != "tool":
                continue
            identifier = message.get("tool_call_id")
            if identifier not in pending:
                # Compaction can retain a result whose earlier call is outside this request.
                continue
            name, args, terminal_before_call, last_record_before_call = pending.pop(identifier)
            if identifier == terminal_id:
                terminal_seen = True
            result = message_text(message.get("content"))
            path = args.get("Path")
            signature = provider_signature(identifier, args, result)
            if name == "file_read" and path in expected:
                require(result == expected[path], "A record read lacks its actual complete value.")
                require(path not in pairs or pairs[path] == signature, "A catalog record has multiple read occurrences.")
                require(path not in seen_reads, "A catalog record repeats within one provider history.")
                pairs[path] = signature
                seen_reads.add(path)
            elif name == "file_read" and path == setup["child_output"] and owner == "parent":
                require(result.encode() == child_bytes, "The parent provider lacks the complete actual child output.")
                if terminal_before_call:
                    artifact_reads.add(signature)
            elif name == "spawn_agent" and spawn_argument(args, "Agent"):
                starts.add(signature)
            elif name == "set_working_directory":
                require(result == setup["root"], "A catalog declaration lacks its canonical successful result.")
                declarations.add(signature)
            elif name == "file_write":
                require(not write_seen, "The catalog output write repeats within one provider history.")
                write_seen = True
                content = args.get("Content")
                require(isinstance(content, str) and result == f"Successfully wrote {len(content.encode())} bytes to {path}",
                        "A catalog output lacks its paired successful write receipt.")
                if last_record_before_call:
                    writes.add((identifier, content))
    require(set(pairs) == set(expected), "The actor lacks all required successful catalog feedback rounds.")
    require(edges == {(previous, path) for path, previous in predecessors.items()},
            "A next record call lacks its preceding actual result feedback.")
    require(len(writes) == 1, "The actor lacks one complete output write occurrence.")
    return pairs, next(iter(writes)), artifact_reads, starts, declarations


def verify(setup, receipt, events, requests, eval_home):
    require(len(setup["chains"]["parent"]) == 65 and len(setup["chains"]["child"]) == 35,
            "The fixture does not cross both former iteration ceilings.")
    for owner in ROUNDS:
        for index, row in enumerate(setup["chains"][owner]):
            require(actual_file(eval_home, row["path"]).read_bytes() == row["content"].encode(), "A catalog source record changed.")
            expected_next = setup["chains"][owner][index + 1]["path"] if index + 1 < ROUNDS[owner] else None
            require(load_json(row["content"])["next"] == expected_next, "The fixture chain is not canonical.")
    calls, _ = occurrences(events, receipt["session_id"])
    require_observed_rejections(receipt["calls"], calls)
    attempts = [call for call in calls if call["name"] == "spawn_agent"]
    starts = [call for call in attempts if not is_unexecuted_rationale_rejection(call["failure"], call["result"])]
    require(len(starts) == 1 and starts[0]["failure"] is None
            and str(spawn_argument(starts[0]["arguments"], "Agent")).casefold() == "task-worker",
            "The catalog requires one actual task-worker start.")
    start = starts[0]
    task = spawn_argument(start["arguments"], "Task") + "\n" + (spawn_argument(start["arguments"], "Context") or "")
    require(all(value in task for value in (setup["nonce"], setup["chains"]["child"][0]["path"], setup["child_output"])),
            "The child assignment lacks its actual catalog scope.")
    accepted = acceptance(start["result"])
    require(receipt["accepted_runs"] == [accepted] and receipt["delivery_observations"]["complete"],
            "The collector lacks the one accepted child's complete consumption.")
    deliveries = receipt["verified_deliveries"]
    require(len(deliveries) == 1 and deliveries[0]["accepted"] == accepted,
            "The child catalog lacks its verified terminal attribution.")
    terminal_id, terminal = canonical_pairs(requests, accepted, start["id"], "spawn_agent", receipt["calls"])
    require(terminal == deliveries[0]["terminal"] and terminal["outcome"] == "Completed",
            "The child catalog did not complete under its actual terminal pair.")
    child_requests = [request for request in requests if (paths := context_paths(request)) is not None
                      and paths["log_path"] == terminal["log_path"]
                      and paths["artifact_dir"] == terminal["artifact_directory"]]
    parent_requests = [request for request in requests if context_paths(request) is None]
    require(child_requests, "The child catalog lacks its attributed provider requests.")
    child_bytes = actual_file(eval_home, setup["child_output"]).read_bytes()
    expected_child = {"nonce": setup["nonce"], "records": records(setup, "child")}
    require(load_json(child_bytes) == expected_child, "The child catalog has wrong values or order.")
    expected_combined = {"nonce": setup["nonce"], "parent_records": records(setup, "parent"),
                         "child_records": records(setup, "child")}
    combined_bytes = actual_file(eval_home, setup["combined_output"]).read_bytes()
    require(load_json(combined_bytes) == expected_combined, "The combined catalog has wrong values or order.")
    child_pairs, child_write, _, _, _ = provider_pairs(child_requests, setup, "child", child_bytes, terminal_id)
    parent_pairs, parent_write, artifact_reads, parent_starts, declarations = provider_pairs(
        parent_requests, setup, "parent", child_bytes, terminal_id)
    declared = [call for call in calls if call["name"] == "set_working_directory"]
    require(all(call["failure"] is None and call["arguments"].get("Path") == setup["root"]
                and call["result"] == setup["root"] for call in declared)
            and declarations == {provider_signature(call["id"], call["arguments"], call["result"]) for call in declared},
            "The parent project declaration lacks exact successful DTO and provider pairs.")
    require(parent_starts == {provider_signature(call["id"], call["arguments"], call["result"]) for call in attempts},
            "The actual start DTO lacks its paired provider acceptance.")
    require(child_write[1].encode() == child_bytes and parent_write[1].encode() == combined_bytes,
            "The actual catalog bytes differ from the paired full writes.")
    require(artifact_reads, "The parent never fully reads the actual child artifact.")
    previous = start["result_sequence"]
    for row in setup["chains"]["parent"]:
        matching = [call for call in calls if call["name"] == "file_read" and call["arguments"].get("Path") == row["path"]]
        require(len(matching) == 1, "The parent lacks one actual read occurrence per record.")
        call = matching[0]
        require(previous < call["call_sequence"] < call["result_sequence"] and call["failure"] is None
                and direct_read(call["arguments"]) and call["result"] == row["content"]
                and parent_pairs[row["path"]] == provider_signature(call["id"], call["arguments"], call["result"]),
                "The parent record lacks ordered successful DTO and provider pairs.")
        previous = call["result_sequence"]
    reviews = [call for call in calls if call["name"] == "file_read" and call["arguments"].get("Path") == setup["child_output"]
               and call["failure"] is None and direct_read(call["arguments"]) and call["result"].encode() == child_bytes]
    writes = [call for call in calls if call["name"] == "file_write"]
    require(len(writes) == 1 and writes[0]["id"] == parent_write[0]
            and writes[0]["arguments"].get("Path") == setup["combined_output"]
            and writes[0]["arguments"].get("Content", "").encode() == combined_bytes
            and writes[0]["failure"] is None
            and writes[0]["result"] == f"Successfully wrote {len(combined_bytes)} bytes to {setup['combined_output']}",
            "The parent DTO stream lacks its actual complete output write.")
    require(any(review["call_sequence"] < review["result_sequence"] < writes[0]["call_sequence"]
                and provider_signature(review["id"], review["arguments"], review["result"]) in artifact_reads
                for review in reviews) and previous < writes[0]["call_sequence"],
            "The parent writes its combined output before complete catalog and child artifact review.")
    require(all(call["name"] in TOOLS | {"spawn_agent"} for call in calls), "The parent uses a forbidden action.")
    require(load_json(receipt["last_reply"]) == {"output": setup["combined_output"], "parent_records": 65,
            "child_records": 35, "nonce": setup["nonce"]}, "The final report differs from the actual combined catalog.")
    return {"passed": True, "parent_feedback_rounds": len(parent_pairs), "child_feedback_rounds": len(child_pairs),
            "accepted_run": accepted, "combined_sha256": hashlib.sha256(combined_bytes).hexdigest(),
            "child_sha256": hashlib.sha256(child_bytes).hexdigest(),
            "limits": ["Deterministic controls prove oracle sensitivity, not live model behavior.",
                       "The case uses small full text reads and complete file_write outputs only.",
                       "Child captures prove at least 35 distinct useful record rounds and reject visible per-history repeats.",
                       "Compaction can hide identical child call reuse across captures. This oracle does not prove one global execution per child record or write.",
                       "Output capture precedes oracle checks. An observer failure before the assertion can still prevent artifact capture."]}


def archive_outputs(setup, eval_home, evidence):
    captured = {}
    home = Path(eval_home)
    for name in ("child_output", "combined_output"):
        source = actual_file(home, setup[name])
        current = home
        require(not current.is_symlink(), "The eval home is a symbolic link.")
        for part in source.relative_to(home).parts:
            current /= part
            require(not current.is_symlink(), "An output artifact path contains a symbolic link.")
        if not source.exists():
            captured[name] = {"path": setup[name], "state": "missing"}
        else:
            require(source.is_file(), "The actual output artifact is not a file.")
            content = source.read_bytes()
            destination = evidence / source.name
            with destination.open("xb") as output:
                output.write(content)
            captured[name] = {"path": setup[name], "state": "captured", "bytes": len(content),
                              "sha256": hashlib.sha256(content).hexdigest(), "archive": destination.name}
    (evidence / "productive-output-capture.json").write_text(json.dumps(captured, indent=2))
    return captured


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "verify"))
    for name in ("eval-home", "evidence"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--observer-directory", type=Path)
    parser.add_argument("--relay-directory", type=Path)
    args = parser.parse_args()
    if args.action == "prepare":
        print(prompt(prepare(args.eval_home, args.evidence)), end="")
    else:
        require(args.observer_directory and args.relay_directory, "The actual observer and relay evidence are required.")
        setup = load_json((args.evidence / "setup.json").read_text())
        data = load_json((args.observer_directory / "observer-input.json").read_text())
        receipt = load_json((args.observer_directory / "verified-receipt.json").read_text())
        validate_prompt_receipt(receipt, data)
        require(data["Mode"] == "collect" and data["InitialPrompt"] == prompt(setup)
                and receipt["case"] == CASE and receipt["prompt_ordinal"] == 1,
                "The observer does not describe this productive catalog attempt.")
        events = [load_json(line) for line in (args.observer_directory / "session-output.jsonl").read_text().splitlines() if line.strip()]
        archive_outputs(setup, args.eval_home, args.observer_directory)
        result = verify(setup, receipt, events, evidence_requests(args.relay_directory), args.eval_home)
        print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
