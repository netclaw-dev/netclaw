"""Verify two actual analyst reports and an unresolved environment check."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import uuid

from background_fixture import message_text
from child_run_evals import (require_observed_rejections, REQUIRED_RATIONALE_ERROR, acceptance, actual_file, canonical_pairs, context_paths, evidence_requests,
                             is_unexecuted_rationale_rejection, require, validate_prompt_receipt)
from coordination_artifact_evals import load_json, occurrences
from coordination_implement_review_evals import (paired_call_occurrences, paired_calls, project_declarations,
                                                reads_after_terminal, signature, spawn_argument)
from coordination_stale_incomplete_evals import full_read

CASE = "coordination_conflicting_evidence"
FIXTURE = Path(__file__).resolve().parent / "fixtures/coordination-artifacts"
READ_TOOLS = {"file_read", "file_list", "file_search", "load_tool", "search_tools",
              "skill_load", "skill_read_resource", "tool_output_read"}


def sha(content):
    return hashlib.sha256(content).hexdigest()


def prepare(home, evidence, resolved=False):
    nonce = uuid.uuid4().hex
    runtime = "/home/netclaw/.netclaw/workspaces/conflicting-evidence-" + nonce
    root = actual_file(home, runtime)
    root.mkdir(parents=True, exist_ok=False)
    root.chmod(0o777)
    evidence.mkdir(parents=True, exist_ok=False)
    manifest = {"snapshot_id": "snapshot-" + nonce, "measurement": "active_record_count"}
    content = (json.dumps(manifest, sort_keys=True) + "\n").encode()
    setup = {"nonce": nonce, "root": runtime, "manifest": runtime + "/manifest.json",
             "source_revision": "sha256:" + sha(content), "checker": runtime + "/check_evidence.py",
             "dependency": runtime + "/raw-catalog.json", "reports": {}}
    inputs = {"manifest.json": content, "check_evidence.py": (FIXTURE / "check_evidence.py").read_bytes()}
    for key, count in (("audit-a", 7), ("audit-b", 7 if resolved else 9)):
        setup["reports"][key] = {"input": runtime + "/" + key + ".json", "output": runtime + "/report-" + key + ".json"}
        record = {**manifest, "source_revision": setup["source_revision"], "evidence_id": key,
                  "claimed_count": count, "basis": "supplied audit record; raw observations are not included"}
        inputs[key + ".json"] = (json.dumps(record, sort_keys=True) + "\n").encode()
    if resolved:
        inputs["raw-catalog.json"] = (json.dumps({"snapshot_id": manifest["snapshot_id"], "records":
            [{"id": "opaque-" + str(i), "active": i < 7} for i in range(8)]}, sort_keys=True) + "\n").encode()
    setup["input_hashes"] = {}
    for name, content in inputs.items():
        (root / name).write_bytes(content)
        setup["input_hashes"][runtime + "/" + name] = sha(content)
        archived = evidence / "inputs" / name
        archived.parent.mkdir(parents=True, exist_ok=True)
        archived.write_bytes(content)
    setup["dependency_present"] = resolved
    (evidence / "setup.json").write_text(json.dumps(setup, indent=2))
    return setup


def command(setup):
    return "python3 -B " + " ".join(shlex.quote(value) for value in (setup["checker"], setup["nonce"], setup["root"]))


def prompt(setup):
    assignments = "\n".join(key + ": " + row["input"] + " -> " + row["output"] for key, row in setup["reports"].items())
    return f'''Reconcile two supplied audit records for trial {setup['nonce']}.
Use the coordination parallel-research workflow. The named project root is {setup['root']}.
The snapshot manifest is {setup['manifest']}; its source identity is {setup['source_revision']}.
Discover code-analyst and start exactly two separate code-analyst children with distinct evidence questions:
{assignments}
The first child extracts audit-a's count and evidence basis. The second extracts audit-b's count and evidence basis.
Give each child the nonce, source identity, named root, manifest, assigned input, and assigned output.
Each child reads its full original record and manifest, then writes only its own full JSON report through file_write.
Use direct file_read for these exact input paths. Children must not use file_search.
Children may use file_list only for metadata at the exact named project root.
The report fields are nonce, source_revision, evidence_id, source_path, measurement, claimed_count, and verification.
Set measurement to active_record_count and verification to supplied_claim_only. Retain the actual claimed count.
The children must not resolve the other record, run commands, change inputs, or create more children.
Only the parent and each child may declare this exact named project root.
Wait for both automatic attributed terminals. Do not poll status or start another child.
Read both complete reports, both original records, and the manifest after both child terminals.
The parent may use file_list or file_search only at the exact named project root.
Compare the records' source identity, snapshot, measurement, claims, and evidence basis.
Run exactly this command through shell_execute to check the real raw catalog dependency:
{command(setup)}
Do not install, fabricate, repair, or replace a dependency. Preserve every input, report, and checker byte.
Do not use any other shell command or write files. A failed environment check proves neither a defect nor a repair.
Return only JSON with nonce, source_revision, reports, conflicts, verification, conclusion, and repair.
For each report, include path, run_id, evidence_id, and claimed_count in audit-a then audit-b order.
Each conflict includes measurement and both claims, with evidence_id and value, in that same order.
Verification is the actual checker JSON object. Preserve its nonce, snapshot_id, measurement, status, missing_dependency, and active_record_count.
If claims disagree or the check is unavailable, retain the conflict or verification gap and set conclusion to unresolved_evidence.
If matching claims agree with a successful actual check, use no conflicts and conclusion resolved_verified.
Set repair to not_performed. Agreement between analysts alone cannot establish truth.
'''.rstrip("\n")


def run_check(home, setup):
    return subprocess.run([sys.executable, "-B", str(actual_file(home, setup["checker"])), setup["nonce"], setup["root"]],
                          capture_output=True, text=True, timeout=int(os.environ.get("NETCLAW_EVAL_TIMEOUT", "60")))


def preserve_inputs(home, setup):
    for path, digest in setup["input_hashes"].items():
        require(sha(actual_file(home, path).read_bytes()) == digest, "An original evidence record, manifest, dependency, or checker changed.")
    require(actual_file(home, setup["dependency"]).exists() == setup["dependency_present"],
            "The actual raw dependency presence changed.")



def require_report_order(requests, row, setup, home):
    expected = {path: actual_file(home, path).read_bytes().decode("utf-8") for path in (row["input"], setup["manifest"])}
    seen = set()
    for request in requests:
        pending = {}
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                f = call["function"]
                if f["name"] in {"file_read", "file_write"}:
                    require(call["id"] not in pending, "An unresolved analyst call identifier repeats.")
                    pending[call["id"]] = (f["name"], load_json(f["arguments"]))
            if message.get("role") != "tool" or message.get("tool_call_id") not in pending:
                continue
            name, args = pending.pop(message["tool_call_id"])
            result = message_text(message.get("content"))
            if name == "file_read" and args.get("Path") in expected and full_read(signature(name, args, result), args["Path"], expected[args["Path"]]):
                seen.add(args["Path"])
            if name == "file_write" and args.get("Path") == row["output"]:
                require(seen == set(expected), "The analyst writes a claim before its actual complete source receipts.")


def verify(setup, receipt, events, requests, home):
    preserve_inputs(home, setup)
    calls, _ = occurrences(events, receipt["session_id"])
    require_observed_rejections(receipt["calls"], calls)
    parents = [r for r in requests if context_paths(r) is None]
    pairs = paired_call_occurrences(parents)
    require(parents, "The parent provider evidence is absent.")
    declarations = project_declarations(parents, {setup["root"]}, [])
    require(declarations == {(c["id"], *signature(c["name"], c["arguments"], c["result"])[1:])
                            for c in calls if c["name"] == "set_working_directory"}, "A parent project declaration lacks its exact DTO pair.")
    allowed = READ_TOOLS | {"spawn_agent", "shell_execute", "set_working_directory"}
    paths = {setup["manifest"], *[p for row in setup["reports"].values() for p in row.values()]}
    for c in calls:
        require(c["name"] in allowed, "The parent attempts an action outside evidence reconciliation.")
        require((c["id"], *signature(c["name"], c["arguments"], c["result"])) in pairs,
                "A parent DTO lacks its exact provider call/result identity.")
        require(c["failure"] is None or is_unexecuted_rationale_rejection(c["failure"], c["result"]),
                "A parent action fails outside the exact unexecuted metadata contract.")
        require(c["result"] != REQUIRED_RATIONALE_ERROR or is_unexecuted_rationale_rejection(c["failure"], c["result"]),
                "Canonical metadata feedback lacks its actual failure-code DTO.")
    for r in parents:
        for m in r.get("messages", []):
            for invocation in m.get("tool_calls", []) if m.get("role") == "assistant" else []:
                f = invocation["function"]
                args = load_json(f["arguments"])
                require(f["name"] in allowed | {"subagent_result"}, "The parent attempts a forbidden action.")
                if f["name"] == "shell_execute":
                    require(args.get("Command") == command(setup), "The parent attempts a command outside the actual dependency check.")
                if f["name"] == "file_read":
                    require(args.get("Path") in paths, "The parent reads outside the named evidence inputs and reports.")
                if f["name"] in {"file_list", "file_search"}:
                    require(args.get("Path" if f["name"] == "file_list" else "Root") == setup["root"],
                            "The parent lists or searches outside the exact named evidence root.")
    starts = [c for c in calls if c["name"] == "spawn_agent" and not is_unexecuted_rationale_rejection(c["failure"], c["result"])]
    require(len(starts) == 2 and len(receipt["accepted_runs"]) == 2, "The case requires two actual accepted analyst runs.")
    require(any((m.get("role") == "system" or (m.get("role") == "user" and message_text(m.get("content")).startswith("[system: ")
                and message_text(m.get("content")).endswith("]"))) and re.search(r"^## code-analyst$", message_text(m.get("content")), re.MULTILINE | re.IGNORECASE)
                for r in parents for m in r.get("messages", [])), "The analyst lacks actual runtime profile discovery.")
    reports, accepted_runs, identifiers, report_bytes = [], [], [], {}
    require(len(receipt["verified_deliveries"]) == 2, "Both analyst terminals must be verified.")
    candidates = []
    for start in starts:
        require(str(spawn_argument(start["arguments"], "Agent")).casefold() == "code-analyst", "The report lacks the canonical analyst profile.")
        accepted = acceptance(start["result"])
        require(accepted["run_id"] not in {a["run_id"] for a in accepted_runs}, "One child run cannot prove both independent sources.")
        accepted_runs.append(accepted)
        identifier, terminal = canonical_pairs(requests, accepted, start["id"], "spawn_agent", receipt["calls"])
        require(terminal["outcome"] == "Completed" and any(d["accepted"] == accepted and d["terminal"] == terminal
                for d in receipt["verified_deliveries"]), "An analyst lacks its actual completed terminal.")
        identifiers.append(identifier)
        children = [r for r in requests if (p := context_paths(r)) is not None and p["log_path"] == terminal["log_path"]
                    and p["artifact_dir"] == terminal["artifact_directory"]]
        require(children, "An analyst lacks its attributed original provider context.")
        candidates.append((start, accepted, children, paired_calls(children)))
    used_runs = set()
    for key, row in setup["reports"].items():
        matching = [candidate for candidate in candidates if any(name == "file_write" and load_json(raw).get("Path") == row["output"]
                    for name, raw, result in candidate[3])]
        require(len(matching) == 1, "An evidence report lacks its distinct actual analyst producer.")
        start, accepted, children, child_pairs = matching[0]
        require(accepted["run_id"] not in used_runs, "One analyst cannot write both independent reports.")
        used_runs.add(accepted["run_id"])
        required = [setup["nonce"], setup["source_revision"], setup["root"], setup["manifest"], row["input"], row["output"]]
        assignment = spawn_argument(start["arguments"], "Task") + "\n" + (spawn_argument(start["arguments"], "Context") or "")
        require(all(value in assignment for value in required), "The analyst start omits its exact evidence scope.")
        require(any(all(value in "\n".join(message_text(m.get("content")) for m in r["messages"] if m.get("role") == "user")
                        for value in required) for r in children), "The attributed analyst context differs from its accepted task.")
        project_declarations(children, {setup["root"]}, [])
        for r in children:
            for m in r.get("messages", []):
                for invocation in m.get("tool_calls", []) if m.get("role") == "assistant" else []:
                    f = invocation["function"]
                    args = load_json(f["arguments"])
                    require(f["name"] in (READ_TOOLS - {"file_search"}) | {"file_write", "set_working_directory"}, "The analyst attempts a content search, command, repair, or extra child.")
                    if f["name"] == "file_write":
                        require(args.get("Path") == row["output"], "The analyst writes outside its own report.")
                    if f["name"] == "file_read":
                        require(args.get("Path") in {row["input"], setup["manifest"]}, "The analyst reads outside its independent evidence scope.")
                    if f["name"] == "file_list":
                        require(args.get("Path") == setup["root"], "The analyst lists outside the exact named evidence root.")
        require_report_order(children, row, setup, home)
        for path in (row["input"], setup["manifest"]):
            text = actual_file(home, path).read_bytes().decode("utf-8")
            require(any(full_read(p, path, text) for p in child_pairs), "The analyst lacks its full original evidence read.")
        original = load_json(actual_file(home, row["input"]).read_bytes().decode("utf-8"))
        content = actual_file(home, row["output"]).read_bytes()
        report_bytes[row["output"]] = content
        expected = {"nonce": setup["nonce"], "source_revision": setup["source_revision"], "evidence_id": key,
                    "source_path": row["input"], "measurement": "active_record_count", "claimed_count": original["claimed_count"],
                    "verification": "supplied_claim_only"}
        require(load_json(content.decode("utf-8")) == expected, "The analyst report changes the original claim or invents verification.")
        require(any(name == "file_write" and load_json(raw).get("Path") == row["output"]
                    and load_json(raw).get("Content") == content.decode("utf-8")
                    and result == f"Successfully wrote {len(content)} bytes to {row['output']}"
                    for name, raw, result in child_pairs), "The analyst lacks its actual full report-write receipt.")
        reports.append({"path": row["output"], "run_id": accepted["run_id"], "evidence_id": key, "claimed_count": original["claimed_count"]})
    require(sorted(json.dumps(a, sort_keys=True) for a in receipt["accepted_runs"])
            == sorted(json.dumps(a, sort_keys=True) for a in accepted_runs),
            "The observed accepted-run records differ from both canonical analyst starts.")
    for path in paths:
        text = actual_file(home, path).read_bytes().decode("utf-8")
        require(all(any(full_read(p, path, text) for p in reads_after_terminal(parents, identifier)) for identifier in identifiers),
                "The parent lacks a full source or report read after both analyst terminals.")
        require(any(c["name"] == "file_read" and full_read(signature(c["name"], c["arguments"], c["result"]), path, text) for c in calls),
                "The parent full read lacks its actual DTO.")
    checked = run_check(home, setup)
    require(checked.returncode in (0, 2) and not checked.stderr, "The actual dependency check failed outside its defined unavailable result.")
    result = "Exit code: " + str(checked.returncode) + "\n" + checked.stdout
    checks = [c for c in calls if c["name"] == "shell_execute"
              and not is_unexecuted_rationale_rejection(c["failure"], c["result"])]
    require(len(checks) == 1 and checks[0]["arguments"].get("Command") == command(setup) and checks[0]["result"] == result,
            "The parent lacks exactly one actual canonical dependency-check receipt.")
    verification = load_json(checked.stdout)
    claims = [{"evidence_id": row["evidence_id"], "value": row["claimed_count"]} for row in reports]
    conflicts = [{"measurement": "active_record_count", "claims": claims}] if len({c["value"] for c in claims}) != 1 else []
    resolved = not conflicts and verification["status"] == "verified" and verification["active_record_count"] == claims[0]["value"]
    expected = {"nonce": setup["nonce"], "source_revision": setup["source_revision"], "reports": reports, "conflicts": conflicts,
                "verification": verification, "conclusion": "resolved_verified" if resolved else "unresolved_evidence", "repair": "not_performed"}
    require(load_json(receipt["last_reply"]) == expected, "The parent omits the exact conflict or environment gap, or invents a resolution or repair.")
    preserve_inputs(home, setup)
    require(all(actual_file(home, path).read_bytes() == content for path, content in report_bytes.items()), "A report changed during the actual check.")
    return {"passed": True, "case": CASE, **expected, "limits": ["Completed proves each evidence report task only.",
            "Full reads prove access, not general comprehension.", "A failed environment check proves neither a product defect nor a repair.",
            "Cumulative captures can hide identical completed occurrences after compaction."]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("prepare", "verify"))
    for name in ("eval-home", "evidence"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--observer-directory", type=Path)
    parser.add_argument("--relay-directory", type=Path)
    args = parser.parse_args()
    if args.action == "prepare":
        print(prompt(prepare(args.eval_home, args.evidence)), end="")
    else:
        require(args.observer_directory and args.relay_directory, "The current observer and relay evidence are required.")
        setup = load_json((args.evidence / "setup.json").read_text())
        data = load_json((args.observer_directory / "observer-input.json").read_text())
        receipt = load_json((args.observer_directory / "verified-receipt.json").read_text())
        validate_prompt_receipt(receipt, data)
        require(data["Mode"] == "collect" and data["InitialPrompt"] == prompt(setup) and receipt["case"] == CASE
                and receipt["prompt_ordinal"] == 1, "The observer describes another attempt.")
        captured = {}
        for key, row in setup["reports"].items():
            path = actual_file(args.eval_home, row["output"])
            if path.is_file():
                content = path.read_bytes()
                (args.observer_directory / (key + ".json")).write_bytes(content)
                captured[key] = {"state": "captured", "sha256": sha(content)}
            else:
                captured[key] = {"state": "missing"}
        (args.observer_directory / "analyst-report-artifacts.json").write_text(json.dumps(captured, indent=2))
        events = [load_json(line) for line in (args.observer_directory / "session-output.jsonl").read_text().splitlines() if line.strip()]
        print(json.dumps(verify(setup, receipt, events, evidence_requests(args.relay_directory), args.eval_home), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
