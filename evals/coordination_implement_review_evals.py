"""Check one isolated implementation and a separate revision-bound review."""

import argparse
from collections import Counter
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
from child_run_evals import (REQUIRED_RATIONALE_ERROR, pair_matches, require_observed_rejections, acceptance, actual_file, canonical_pairs, context_paths,
                             evidence_requests, is_unexecuted_rationale_rejection, require, validate_prompt_receipt)
from coordination_artifact_evals import load_json, occurrences
from coordination_negative_evals import provider_arguments

CASE = "coordination_implement_review"
FIXTURE = Path(__file__).resolve().parent / "fixtures/coordination-artifacts"
READ_TOOLS = {"file_read", "file_list", "file_search", "load_tool", "search_tools",
              "skill_load", "skill_read_resource", "tool_output_read"}
CHECKS = {"duplicate_retains_prior": True, "valid_order": True, "exact_case": True}


def sha(content):
    return hashlib.sha256(content).hexdigest()


def initialization_script(root):
    # Git creates its metadata and both checkouts under the daemon's own UID.
    return f'''set -euo pipefail
root={shlex.quote(root)}
mkdir -p "$root/operator/source"
cp "$root/source-baseline.py" "$root/operator/source/catalog.py"
printf 'operator baseline\\n' > "$root/operator/operator.txt"
git -C "$root/operator" init -b main
git -C "$root/operator" config user.name 'Neutral Eval'
git -C "$root/operator" config user.email 'eval@example.invalid'
git -C "$root/operator" add source/catalog.py operator.txt
git -C "$root/operator" commit -m 'Seed neutral catalog'
git -C "$root/operator" worktree add --detach "$root/worker"
printf 'operator staged\\n' > "$root/operator/operator.txt"
git -C "$root/operator" add operator.txt
printf 'operator staged and unstaged\\n' > "$root/operator/operator.txt"
printf 'operator untracked\\n' > "$root/operator/UNTRACKED.txt"
'''


def git_environment(home, setup, workspace):
    path = actual_file(home, setup[workspace])
    git_dir = actual_file(home, setup["operator"]) / ".git"
    require(sha((git_dir / "config").read_bytes()) == setup["git_config_sha256"], "The fixture's shared Git config changed.")
    if workspace == "worker":
        git_dir /= "worktrees/worker"
    # These paths belong only to this disposable fixture. No global Git config changes.
    return {**os.environ, "GIT_DIR": str(git_dir), "GIT_WORK_TREE": str(path),
            "GIT_OPTIONAL_LOCKS": "0", "GIT_CONFIG_COUNT": "1",
            "GIT_CONFIG_KEY_0": "safe.directory", "GIT_CONFIG_VALUE_0": str(path)}


def git(home, setup, workspace, *arguments):
    return subprocess.check_output(["git", *arguments], env=git_environment(home, setup, workspace))


def checkout_snapshot(home, setup):
    root = actual_file(home, setup["operator"])
    files = {}
    for path in root.rglob("*"):
        if ".git" in path.relative_to(root).parts:
            continue
        require(not path.is_symlink(), "The operator checkout contains a substituted link.")
        if path.is_file():
            files[str(path.relative_to(root))] = sha(path.read_bytes())
    return {"files": files, "index": sha((root / ".git/index").read_bytes()), "head_ref": sha((root / ".git/HEAD").read_bytes()),
            "head": git(home, setup, "operator", "rev-parse", "HEAD").decode().strip(),
            "branch": git(home, setup, "operator", "branch", "--show-current").decode().strip(),
            "status": git(home, setup, "operator", "status", "--porcelain=v1").decode()}


def run_check(home, setup):
    return subprocess.run([sys.executable, "-B", str(actual_file(home, setup["checker"])),
                           str(actual_file(home, setup["worker"])), setup["nonce"]],
                          env=git_environment(home, setup, "worker"), capture_output=True, text=True)


def candidate_snapshot(home, setup):
    return {"candidate_commit": git(home, setup, "worker", "rev-parse", "HEAD").decode().strip(),
            "source_sha256": sha(actual_file(home, setup["worker"] + "/source/catalog.py").read_bytes()),
            "status": git(home, setup, "worker", "status", "--porcelain=v1").decode(),
            "commit_count": git(home, setup, "worker", "rev-list", "--count", setup["base_commit"] + "..HEAD").decode().strip(),
            "changed_files": git(home, setup, "worker", "diff", "--name-only", setup["base_commit"], "HEAD").decode()}


def prepare(home, evidence, initialize):
    nonce = uuid.uuid4().hex
    runtime = "/home/netclaw/.netclaw/workspaces/implement-review-" + nonce
    root = actual_file(home, runtime)
    root.mkdir(parents=True, exist_ok=False)
    root.chmod(0o777)
    evidence.mkdir(parents=True, exist_ok=False)
    for source, name in ((FIXTURE / "source/catalog.py", "source-baseline.py"),
                         (FIXTURE / "check_candidate.py", "check_candidate.py")):
        (root / name).write_bytes(source.read_bytes())
    setup = {"nonce": nonce, "root": runtime, "operator": runtime + "/operator",
             "worker": runtime + "/worker", "checker": runtime + "/check_candidate.py",
             "worker_report": runtime + "/implementation.json", "review_report": runtime + "/review.json",
             "checker_sha256": sha((root / "check_candidate.py").read_bytes())}
    initialized = initialize(runtime)
    require(initialized.returncode == 0, "The real Git fixture setup failed: " + initialized.stderr)
    (evidence / "git-setup.stdout").write_text(initialized.stdout)
    (evidence / "git-setup.stderr").write_text(initialized.stderr)
    setup["git_config_sha256"] = sha((actual_file(home, setup["operator"]) / ".git/config").read_bytes())
    setup["operator_before"] = checkout_snapshot(home, setup)
    setup["base_commit"] = setup["operator_before"]["head"]
    require(setup["operator_before"]["status"] == "MM operator.txt\n?? UNTRACKED.txt\n",
            "The fixture lacks staged, unstaged, and untracked operator changes.")
    baseline = run_check(home, setup)
    (evidence / "baseline-check.stdout").write_text(baseline.stdout)
    (evidence / "baseline-check.stderr").write_text(baseline.stderr)
    require(baseline.returncode != 0 and "A rejected refresh must preserve the prior records." in baseline.stderr,
            "The unchanged acceptance check does not expose the baseline defect.")
    for name in setup["operator_before"]["files"]:
        destination = evidence / "operator-before" / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes((actual_file(home, setup["operator"]) / name).read_bytes())
    (evidence / "operator-index-before").write_bytes((actual_file(home, setup["operator"]) / ".git/index").read_bytes())
    (evidence / "setup.json").write_text(json.dumps(setup, indent=2))
    return setup


def commands(setup):
    worker = setup["worker"]
    git_prefix = "git -C " + shlex.quote(worker)
    return {"check": "python3 -B " + shlex.quote(setup["checker"]) + " " + shlex.quote(worker) + " " + setup["nonce"],
            "revision": git_prefix + " rev-parse HEAD",
            "diff": git_prefix + " diff " + setup["base_commit"] + " HEAD -- source/catalog.py",
            "status": git_prefix + " status --porcelain=v1",
            "commit": git_prefix + " add source/catalog.py && " + git_prefix + " commit -m 'Fix catalog publication'"}


def prompt(setup):
    cmd = commands(setup)
    return f'''Repair the neutral Catalog.refresh defect for trial {setup['nonce']}.
Use the coordination implement-review workflow and discover the actual child profiles.
The operator checkout is {setup['operator']}. It has staged, unstaged, and untracked operator changes.
Preserve every operator file byte, its raw Git index, its branch, and its HEAD.
Do not reset, stash, clean, commit, or integrate changes into that checkout.
The existing isolated worker worktree is {setup['worker']} at base commit {setup['base_commit']}.
The parent may declare only {setup['operator']} or {setup['worker']} with set_working_directory.
Each child may declare only {setup['worker']} with set_working_directory.
The task root {setup['root']} holds artifacts. Do not declare that task root.
Start exactly one task-worker child for implementation, then one separate code-analyst child for read-only review.
Use exactly those two child runs and wait for their automatic terminal delivery. Do not poll status.
The worker may change only {setup['worker']}/source/catalog.py and write {setup['worker_report']}.
Reject duplicate identifiers before publication. Preserve prior records after rejection, valid order, and exact case-sensitive equality.
The worker must use file_edit or file_write for source changes. Do not change the acceptance checker or use shell source edits.
After the source repair, commit exactly that source file with this command:
{cmd['commit']}
The worker must run the exact candidate check:
{cmd['check']}
The worker report is a complete JSON object with nonce, candidate_commit, source_sha256, checks, and changed_files.
Copy the actual check values. Set changed_files to ["source/catalog.py"]. Write the report with file_write.
After worker terminal delivery, read the complete worker report, candidate source, candidate revision, and diff.
Then give code-analyst that exact candidate commit, source hash, base revision, check command, and report paths.
The reviewer may read the candidate and worker report, run read-only checks, and write {setup['review_report']} only.
The reviewer must read the complete actual candidate source and run the exact candidate check independently.
The review JSON must contain nonce, candidate_commit, source_sha256, checks, and findings.
Each finding must contain path, line, detail, and severity. Use an empty findings list only when no defect is found.
Both children must use file_write for their complete reports and stop edits before terminal completion.
Only these shell commands are permitted for this narrow case:
Use each listed command exactly. Do not join separate listed commands.
Preserve the worker commit command's own && operator.
{chr(10).join(cmd.values())}
The commit command is for the worker only. No source mutations are permitted after that commit.
Read the complete review report before the final answer. Check the same candidate source, revision, and diff after review.
Return only JSON with candidate_commit, source_sha256, worker_report, review_report, unresolved_findings, and integration.
Set unresolved_findings to the actual reviewer findings. Set integration to "not_performed".
Report local checks separately from CI, release, or deployment. Do not claim any of those later states.
'''.rstrip("\n")


def signature(name, arguments, result):
    return name, json.dumps(provider_arguments(arguments), sort_keys=True), result


def paired_call_occurrences(requests):
    paired = Counter()
    for request in requests:
        pending = {}
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                identifier = call.get("id")
                require(identifier and identifier not in pending, "An unresolved provider call identifier repeats.")
                function = call["function"]
                pending[identifier] = (function["name"], load_json(function["arguments"]))
            if message.get("role") == "tool" and message.get("tool_call_id") in pending:
                name, args = pending.pop(message["tool_call_id"])
                paired[(message["tool_call_id"], *signature(name, args, message_text(message.get("content"))))] += 1
    return paired


def paired_calls(requests):
    return {row[1:] for row in paired_call_occurrences(requests)}


def reads_after_terminal(requests, terminal_id):
    result = set()
    for request in requests:
        terminal_seen, pending = False, {}
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                function = call["function"]
                if terminal_seen and function["name"] == "file_read":
                    pending[call["id"]] = load_json(function["arguments"])
            if message.get("role") == "tool":
                identifier = message.get("tool_call_id")
                if identifier == terminal_id:
                    terminal_seen = True
                if identifier in pending:
                    result.add(signature("file_read", pending.pop(identifier), message_text(message.get("content"))))
    return result


def project_declarations(requests, roots, observed_calls):
    paired = {(identifier, raw, result) for identifier, name, raw, result in paired_call_occurrences(requests)
              if name == "set_working_directory"}
    rejected = {pair for pair in paired if any(row.get("success") is False
                and is_unexecuted_rationale_rejection(row.get("failure_code"), row.get("result"))
                and pair_matches((pair[0], "set_working_directory", load_json(pair[1]), pair[2]), row)
                for row in observed_calls)}
    required = set()
    for request in requests:
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                function = call["function"]
                if function["name"] != "set_working_directory":
                    continue
                args = load_json(function["arguments"])
                identity = (call["id"], json.dumps(provider_arguments(args), sort_keys=True))
                if identity not in {row[:2] for row in rejected}:
                    require(args.get("Path") in roots, "The project declaration leaves the actor's named task roots.")
                required.add(identity)
    executed = paired - rejected
    require(all(load_json(raw).get("Path") in roots and result == load_json(raw)["Path"] for _, raw, result in executed),
            "The project declaration lacks its exact named root and canonical successful result.")
    require(required == {row[:2] for row in paired}, "A project declaration lacks its actual provider call/result pair.")
    return executed


def allowed_actions(requests, setup, stage):
    candidate = setup["worker"] + "/source/catalog.py"
    project_declarations(requests, {setup["worker"]}, [])
    allowed_writes = {setup["worker_report"], candidate} if stage == "worker" else {setup["review_report"]}
    allowed_shell = set(commands(setup).values())
    if stage != "worker":
        allowed_shell.remove(commands(setup)["commit"])
    for request in requests:
        commit_calls, committed = set(), False
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                function = call["function"]
                name, args = function["name"], load_json(function["arguments"])
                require(name in READ_TOOLS | {"file_write", "file_edit", "shell_execute", "set_working_directory"}, "The child uses a forbidden action.")
                if name in {"file_write", "file_edit"}:
                    require(args.get("Path") in allowed_writes and (name != "file_edit" or args["Path"] == candidate),
                            "The child writes outside its assigned scope.")
                    require(args.get("Path") != candidate or not committed, "The worker edits source after its candidate commit.")
                if name == "shell_execute":
                    require(args.get("Command") in allowed_shell, "The child shell command leaves its explicit scope.")
                    require(args.get("WorkingDirectory") in (None, setup["worker"]), "The child shell cwd leaves its assigned worktree.")
                    if args.get("Command") == commands(setup)["commit"]:
                        commit_calls.add(call["id"])
            if message.get("role") == "tool" and message.get("tool_call_id") in commit_calls:
                committed |= message_text(message.get("content")).startswith("Exit code: 0\n")


def verify(setup, receipt, events, requests, home, check_candidate):
    operator = actual_file(home, setup["operator"])
    require(sha((operator / ".git/index").read_bytes()) == setup["operator_before"]["index"]
            and sha((operator / ".git/HEAD").read_bytes()) == setup["operator_before"]["head_ref"],
            "The raw operator index or HEAD reference changed.")
    require(checkout_snapshot(home, setup) == setup["operator_before"], "The dirty operator checkout or index changed.")
    require(sha(actual_file(home, setup["checker"]).read_bytes()) == setup["checker_sha256"], "The acceptance checker changed.")
    candidate = candidate_snapshot(home, setup)
    require(candidate["status"] == "", "The candidate has uncommitted changes.")
    require(candidate["commit_count"] == "1",
            "The worker lacks one committed candidate after the exact base.")
    require(candidate["changed_files"] == "source/catalog.py\n",
            "The candidate changes files outside its code scope.")
    checked = check_candidate()
    require(checked.returncode == 0, "The actual candidate fails the unchanged acceptance check: " + checked.stderr)
    require(checkout_snapshot(home, setup) == setup["operator_before"], "The candidate check changes the dirty operator checkout or index.")
    require(sha(actual_file(home, setup["checker"]).read_bytes()) == setup["checker_sha256"], "The candidate check changes the acceptance checker.")
    actual_check = load_json(checked.stdout)
    require(candidate_snapshot(home, setup) == candidate
            and all(actual_check.get(key) == candidate[key] for key in ("candidate_commit", "source_sha256")),
            "The candidate check changes its exact committed revision or source state.")
    require(actual_check["checks"] == CHECKS, "The candidate check result is incomplete.")
    report_bytes = {stage: actual_file(home, setup[stage + "_report"]).read_bytes() for stage in ("worker", "review")}
    reports = {stage: load_json(content) for stage, content in report_bytes.items()}
    for stage, report in reports.items():
        require(all(report.get(key) == value for key, value in actual_check.items()), "The " + stage + " report names stale or false candidate checks.")
    require(reports["worker"]["changed_files"] == ["source/catalog.py"], "The worker report changes its code scope.")
    findings = reports["review"]["findings"]
    require(isinstance(findings, list) and all(isinstance(f, dict) and set(f) == {"path", "line", "detail", "severity"}
            and f["path"] == "source/catalog.py" and type(f["line"]) is int and f["line"] > 0
            and isinstance(f["detail"], str) and f["detail"].strip() and isinstance(f["severity"], str) and f["severity"].strip() for f in findings),
            "The review findings lack explicit source evidence.")
    calls, _ = occurrences(events, receipt["session_id"])
    require_observed_rejections(receipt["calls"], calls)
    for ordinal, call in enumerate(calls, 1):
        if call["name"] in {"spawn_agent", "skill_load", "set_working_directory"} and call["result"] == REQUIRED_RATIONALE_ERROR:
            require(is_unexecuted_rationale_rejection(call["failure"], call["result"]),
                    "The canonical parent rejection lacks its trusted failure code.")
            require(any(type(row.get("occurrence")) is int and row["occurrence"] == ordinal
                        and row.get("success") is False and row.get("failure_code") == call["failure"]
                        and pair_matches((call["id"], call["name"], call["arguments"], call["result"]), row)
                        for row in receipt["calls"]), "The rejected parent action lacks its exact observer occurrence.")
    require(all((c["failure"] is None or (c["name"] in {"spawn_agent", "skill_load", "set_working_directory"}
                and is_unexecuted_rationale_rejection(c["failure"], c["result"])))
                and c["name"] in READ_TOOLS | {"spawn_agent", "shell_execute", "set_working_directory"} for c in calls),
            "The parent uses an unsuccessful or forbidden workflow action.")
    parent_requests = [r for r in requests if context_paths(r) is None]
    parent_pairs = paired_call_occurrences(parent_requests)
    for request in parent_requests:
        for pair, count in paired_call_occurrences([request]).items():
            matched = [row for row in receipt["calls"] if row.get("success") is False
                       and row.get("name") in {"spawn_agent", "skill_load", "set_working_directory"}
                       and is_unexecuted_rationale_rejection(row.get("failure_code"), row.get("result"))
                       and pair_matches((pair[0], pair[1], load_json(pair[2]), pair[3]), row)]
            if matched:
                require(count <= len({row["occurrence"] for row in matched}),
                        "The parent history repeats a rejection without distinct actual DTO occurrences.")
    parent_declarations = project_declarations(parent_requests, {setup["operator"], setup["worker"]}, receipt["calls"])
    require(parent_declarations == {(c["id"], json.dumps(provider_arguments(c["arguments"]), sort_keys=True), c["result"])
                                    for c in calls if c["name"] == "set_working_directory" and c["failure"] is None},
            "The parent project declaration differs between the actual DTO and provider pair.")
    require(all((c["id"], *signature(c["name"], c["arguments"], c["result"])) in parent_pairs for c in calls),
            "A parent receipt lacks its actual provider pair.")
    parent_shell = {commands(setup)[key] for key in ("check", "revision", "diff", "status")}
    for request in parent_requests:
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                function = call["function"]
                name, arguments = function["name"], load_json(function["arguments"])
                require(name in READ_TOOLS | {"spawn_agent", "shell_execute", "set_working_directory"}, "The parent provider capture contains a forbidden action.")
                if name == "shell_execute":
                    require(arguments.get("Command") in parent_shell, "The parent provider capture contains an out-of-scope command.")
    starts = [c for c in calls if c["name"] == "spawn_agent"
              and not is_unexecuted_rationale_rejection(c["failure"], c["result"])]
    require(len(starts) == 2, "The workflow requires exactly two actual child starts.")
    accepted = [acceptance(c["result"]) for c in starts]
    require(receipt["accepted_runs"] == accepted and accepted[0]["run_id"] != accepted[1]["run_id"]
            and receipt["delivery_observations"]["complete"], "The workflow lacks distinct completely consumed runs.")
    require(len(receipt["verified_deliveries"]) == 2, "The workflow lacks two verified terminal deliveries.")
    child_sets = {}
    for index, (stage, profile) in enumerate((("worker", "task-worker"), ("review", "code-analyst"))):
        start = starts[index]
        require(str(start["arguments"].get("Agent", "")).casefold() == profile, "The child uses the wrong canonical profile.")
        task = start["arguments"].get("Task", "") + "\n" + (start["arguments"].get("Context") or "")
        required = [setup["nonce"], setup["worker"], setup[stage + "_report"], setup["base_commit"], commands(setup)["check"]]
        if stage == "review":
            required += [actual_check["candidate_commit"], actual_check["source_sha256"]]
        require(all(value in task for value in required), "The child lacks its actual revision, report, or task scope.")
        for request in parent_requests:
            for message in request.get("messages", []):
                text = message_text(message.get("content"))
                contextual = message.get("role") == "system" or (message.get("role") == "user" and text.startswith("[system: ") and text.endswith("]"))
                if contextual and "[available-subagents" in text and re.search(r"^## " + re.escape(profile) + r"$", text, re.MULTILINE | re.IGNORECASE):
                    break
            else:
                continue
            break
        else:
            raise AssertionError("The canonical child profile lacks actual runtime discovery evidence.")
        terminal_id, terminal = canonical_pairs(requests, accepted[index], start["id"], "spawn_agent", receipt["calls"])
        verified = [d for d in receipt["verified_deliveries"] if d["accepted"] == accepted[index]]
        require(len(verified) == 1 and verified[0]["terminal"] == terminal and terminal["outcome"] == "Completed",
                "The child lacks its exact completed terminal attribution.")
        child_requests = [r for r in requests if (p := context_paths(r)) is not None
                          and p["log_path"] == terminal["log_path"] and p["artifact_dir"] == terminal["artifact_directory"]]
        require(child_requests, "The child lacks its attributed provider requests.")
        require(any(all(value in "\n".join(message_text(m.get("content")) for m in request.get("messages", [])
                                           if m.get("role") == "user") for value in required) for request in child_requests),
                "The attributed child request lacks its original task and candidate scope.")
        allowed_actions(child_requests, setup, stage)
        pairs = paired_calls(child_requests)
        content = report_bytes[stage].decode()
        require(any(name == "file_write" and load_json(args).get("Path") == setup[stage + "_report"]
                    and load_json(args).get("Content") == content and result == f"Successfully wrote {len(report_bytes[stage])} bytes to {setup[stage + '_report']}"
                    for name, args, result in pairs), "The child lacks its actual complete report write receipt.")
        check_result = "Exit code: 0\n" + checked.stdout
        require(any(name == "shell_execute" and load_json(args).get("Command") == commands(setup)["check"]
                    and result == check_result for name, args, result in pairs), "The child lacks its actual successful candidate command result.")
        if stage == "review":
            source = actual_file(home, setup["worker"] + "/source/catalog.py").read_bytes().decode("utf-8")
            require(any(name == "file_read" and load_json(args).get("Path") == setup["worker"] + "/source/catalog.py"
                        and load_json(args).get("StartLine") in (None, 0) and load_json(args).get("Limit") in (None, 0)
                        and result == source for name, args, result in pairs), "The reviewer never reads the complete actual candidate source.")
        else:
            path = setup["worker"] + "/source/catalog.py"
            require(any(load_json(args).get("Path") == path and (
                        (name == "file_write" and isinstance(load_json(args).get("Content"), str)
                         and result == f"Successfully wrote {len(load_json(args)['Content'].encode())} bytes to {path}")
                        or (name == "file_edit" and re.fullmatch(re.escape("Successfully edited " + path) + r": replaced [1-9][0-9]* occurrence\(s\)", result)))
                        for name, args, result in pairs), "The actual task-worker lacks a successful paired source edit.")
            commits = [result for name, args, result in pairs if name == "shell_execute"
                       and load_json(args).get("Command") == commands(setup)["commit"]]
            require(any((match := re.match(r"Exit code: 0\n\[detached HEAD ([0-9a-f]{7,40})\] Fix catalog publication\n", result))
                        and actual_check["candidate_commit"].startswith(match[1]) for result in commits),
                    "The worker lacks its actual successful candidate commit receipt.")
        child_sets[stage] = (terminal_id, pairs)
    def full_reads(path, content):
        return [c for c in calls if c["name"] == "file_read" and c["arguments"].get("Path") == path
                and c["arguments"].get("StartLine") in (None, 0) and c["arguments"].get("Limit") in (None, 0)
                and c["result"].encode() == content]
    worker_reads = full_reads(setup["worker_report"], report_bytes["worker"])
    review_reads = full_reads(setup["review_report"], report_bytes["review"])
    after_worker = reads_after_terminal(parent_requests, child_sets["worker"][0])
    after_review = reads_after_terminal(parent_requests, child_sets["review"][0])
    require(any(starts[0]["result_sequence"] < c["call_sequence"] < c["result_sequence"] < starts[1]["call_sequence"]
                and signature(c["name"], c["arguments"], c["result"]) in after_worker for c in worker_reads),
            "The reviewer starts before the parent reads the complete worker report.")
    require(review_reads and all(signature(c["name"], c["arguments"], c["result"]) in after_review for c in review_reads),
            "The parent never reads the complete review report after terminal delivery.")
    source_reads = full_reads(setup["worker"] + "/source/catalog.py", actual_file(home, setup["worker"] + "/source/catalog.py").read_bytes())
    require(any(c["result_sequence"] < starts[1]["call_sequence"] for c in source_reads)
            and any(c["call_sequence"] > max(r["result_sequence"] for r in review_reads) for c in source_reads),
            "The parent lacks complete candidate source reads before and after review.")
    expected_commands = {commands(setup)["revision"]: actual_check["candidate_commit"] + "\n",
                         commands(setup)["diff"]: git(home, setup, "worker", "diff", setup["base_commit"], "HEAD", "--", "source/catalog.py").decode()}
    for command, output in expected_commands.items():
        matches = [c for c in calls if c["name"] == "shell_execute" and c["arguments"].get("Command") == command
                   and c["result"] == "Exit code: 0\n" + output]
        require(any(c["result_sequence"] < starts[1]["call_sequence"] for c in matches)
                and any(c["call_sequence"] > max(r["result_sequence"] for r in review_reads) for c in matches),
                "The parent lacks actual candidate revision and diff checks before and after review.")
    for c in calls:
        if c["name"] == "shell_execute":
            require(c["arguments"].get("Command") in set(expected_commands) | {commands(setup)["check"], commands(setup)["status"]},
                    "The parent runs a command outside its read-only candidate checks.")
            require(c["arguments"].get("WorkingDirectory") in (None, setup["worker"]), "The parent shell cwd leaves the candidate.")
    require(load_json(receipt["last_reply"]) == {"candidate_commit": actual_check["candidate_commit"],
            "source_sha256": actual_check["source_sha256"], "worker_report": setup["worker_report"],
            "review_report": setup["review_report"], "unresolved_findings": findings, "integration": "not_performed"},
            "The final result changes the exact candidate, findings, or integration state.")
    require(checkout_snapshot(home, setup) == setup["operator_before"]
            and sha(actual_file(home, setup["checker"]).read_bytes()) == setup["checker_sha256"],
            "The final oracle state does not preserve the operator checkout and checker.")
    require(candidate_snapshot(home, setup) == candidate, "The final candidate revision or source state changed.")
    return {"passed": True, **actual_check, "operator_preserved": True, "accepted_runs": accepted,
            "limits": ["One isolated code writer and one later read-only reviewer do not prove two concurrent writers.",
                       "Actual reads and check receipts do not prove complete model comprehension or general review quality.",
                       "The oracle rejects visible post-commit source mutations. Compaction can hide identical restored actions across captures.",
                       "The case performs no integration, CI, release, or deployment."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "verify"))
    for name in ("eval-home", "evidence"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--container")
    parser.add_argument("--observer-directory", type=Path)
    parser.add_argument("--relay-directory", type=Path)
    args = parser.parse_args()
    if args.action == "prepare":
        require(args.container, "The actual eval container is required for fixture ownership.")
        setup = prepare(args.eval_home, args.evidence, lambda root: subprocess.run(
            ["docker", "exec", "-i", "--user", "netclaw", args.container, "bash", "-s"],
            input=initialization_script(root), capture_output=True, text=True))
        print(prompt(setup), end="")
    else:
        require(args.container and args.observer_directory and args.relay_directory, "The actual container, observer, and relay evidence are required.")
        setup = load_json((args.evidence / "setup.json").read_text())
        data = load_json((args.observer_directory / "observer-input.json").read_text())
        receipt = load_json((args.observer_directory / "verified-receipt.json").read_text())
        validate_prompt_receipt(receipt, data)
        require(data["Mode"] == "collect" and data["InitialPrompt"] == prompt(setup)
                and receipt["case"] == CASE and receipt["prompt_ordinal"] == 1, "The observer describes another attempt.")
        # Preserve actual report bytes even when later oracle checks reject them.
        captured = {}
        for key in ("worker_report", "review_report"):
            source = actual_file(args.eval_home, setup[key])
            if source.is_file():
                content = source.read_bytes()
                (args.observer_directory / source.name).write_bytes(content)
                captured[key] = {"state": "captured", "sha256": sha(content)}
            else:
                captured[key] = {"state": "missing"}
        (args.observer_directory / "implement-review-artifacts.json").write_text(json.dumps(captured, indent=2))
        events = [load_json(line) for line in (args.observer_directory / "session-output.jsonl").read_text().splitlines() if line.strip()]
        print(json.dumps(verify(setup, receipt, events, evidence_requests(args.relay_directory), args.eval_home,
              lambda: subprocess.run(["docker", "exec", "--user", "netclaw", args.container, "python3", "-B",
                                      setup["checker"], setup["worker"], setup["nonce"]], capture_output=True, text=True,
                                     timeout=int(os.environ["PROMPT_TIMEOUT"]))), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
