"""Check two isolated writer candidates and an unchanged dirty operator checkout."""

import argparse
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import uuid

from background_fixture import message_text
from child_run_evals import (acceptance, actual_file, canonical_pairs, context_paths, evidence_requests,
                             is_unexecuted_rationale_rejection, legacy_observer_mode, require, validate_prompt_receipt)
from coordination_artifact_evals import load_json, occurrences
from coordination_implement_review_evals import (FIXTURE, checkout_snapshot, paired_call_occurrences, paired_calls,
                                                project_declarations, sha, signature)
from coordination_negative_evals import provider_arguments

CASE = "coordination_two_writers"
WRITERS = {"writer-a": "source/catalog.py", "writer-b": "source/selection.py"}
READ_TOOLS = {"file_read", "file_list", "file_search", "load_tool", "search_tools", "skill_load",
              "skill_read_resource", "tool_output_read"}
CHECKS = {"writer-a": {"duplicate_retains_prior": True, "valid_order": True, "exact_case": True},
          "writer-b": {"exact_selection": True, "missing_is_none": True, "caller_unchanged": True}}


def initialization_script(root):
    return f'''set -euo pipefail
root={shlex.quote(root)}
mkdir -p "$root/operator/source"
cp "$root/catalog-baseline.py" "$root/operator/source/catalog.py"
cp "$root/selection-baseline.py" "$root/operator/source/selection.py"
printf 'operator baseline\\n' > "$root/operator/operator.txt"
git -C "$root/operator" init -b main
git -C "$root/operator" config user.name 'Neutral Eval'
git -C "$root/operator" config user.email 'eval@example.invalid'
git -C "$root/operator" add source/catalog.py source/selection.py operator.txt
git -C "$root/operator" commit -m 'Seed independent candidates'
git -C "$root/operator" worktree add --detach "$root/writer-a"
git -C "$root/operator" worktree add --detach "$root/writer-b"
printf 'operator staged\\n' > "$root/operator/operator.txt"
git -C "$root/operator" add operator.txt
printf 'operator staged and unstaged\\n' > "$root/operator/operator.txt"
printf 'operator untracked\\n' > "$root/operator/UNTRACKED.txt"
'''


def environment(home, setup, writer):
    row = setup["writers"][writer]
    common = actual_file(home, setup["operator"]) / ".git"
    require(sha((common / "config").read_bytes()) == setup["git_config_sha256"], "The shared fixture Git config changed.")
    metadata = common / "worktrees" / writer
    require(sha((actual_file(home, row["root"]) / ".git").read_bytes()) == row["git_pointer_sha256"]
            and sha((metadata / "gitdir").read_bytes()) == row["registration_sha256"],
            "A candidate lost its original registered Git worktree identity.")
    commondir = metadata / "commondir"
    require(sha(commondir.read_bytes()) == row["common_directory_sha256"]
            and (metadata / commondir.read_text().strip()).resolve(strict=True) == common.resolve(strict=True),
            "A candidate redirects its original common Git directory.")
    return {**os.environ, "GIT_DIR": str(metadata), "GIT_WORK_TREE": str(actual_file(home, row["root"])),
            "GIT_OPTIONAL_LOCKS": "0", "GIT_CONFIG_COUNT": "1", "GIT_CONFIG_KEY_0": "safe.directory",
            "GIT_CONFIG_VALUE_0": str(actual_file(home, row["root"]))}


def git(home, setup, writer, *args):
    return subprocess.check_output(["git", *args], env=environment(home, setup, writer))


def snapshot(home, setup, writer):
    row = setup["writers"][writer]
    return {"candidate_commit": git(home, setup, writer, "rev-parse", "HEAD").decode().strip(),
            "source_sha256": sha(actual_file(home, row["source"]).read_bytes()),
            "status": git(home, setup, writer, "status", "--porcelain=v1").decode(),
            "commit_count": git(home, setup, writer, "rev-list", "--count", setup["base_commit"] + "..HEAD").decode().strip(),
            "changed_files": git(home, setup, writer, "diff", "--name-only", setup["base_commit"], "HEAD").decode()}


def run_check(home, setup, writer):
    return subprocess.run(["python3", "-B", str(actual_file(home, setup["checker"])),
                           str(actual_file(home, setup["writers"][writer]["root"])), setup["nonce"], writer],
                          env=environment(home, setup, writer), capture_output=True, text=True,
                          timeout=int(os.environ.get("NETCLAW_EVAL_TIMEOUT", "60")))


def preserved(home, setup):
    require(checkout_snapshot(home, setup) == setup["operator_before"], "The dirty operator checkout, raw index, branch, or HEAD changed.")
    require(sha(actual_file(home, setup["checker"]).read_bytes()) == setup["checker_sha256"], "The fixture checker changed.")
    for name in WRITERS:
        environment(home, setup, name)


def prepare(home, evidence, initialize):
    nonce = uuid.uuid4().hex
    runtime = "/home/netclaw/.netclaw/workspaces/two-writers-" + nonce
    root = actual_file(home, runtime)
    root.mkdir(parents=True, exist_ok=False); root.chmod(0o777)
    evidence.mkdir(parents=True, exist_ok=False)
    for name, source in (("catalog-baseline.py", "source/catalog.py"), ("selection-baseline.py", "source/selection.py"),
                         ("check_two_writers.py", "check_two_writers.py")):
        (root / name).write_bytes((FIXTURE / source).read_bytes())
    initialized = initialize(runtime)
    (evidence / "git-setup.stdout").write_text(initialized.stdout)
    (evidence / "git-setup.stderr").write_text(initialized.stderr)
    require(initialized.returncode == 0, "The actual Git fixture setup failed: " + initialized.stderr)
    common = actual_file(home, runtime + "/operator") / ".git"
    setup = {"nonce": nonce, "root": runtime, "operator": runtime + "/operator",
             "checker": runtime + "/check_two_writers.py", "checker_sha256": sha((root / "check_two_writers.py").read_bytes()),
             "git_config_sha256": sha((common / "config").read_bytes()), "writers": {}}
    setup["operator_before"] = checkout_snapshot(home, setup)
    setup["base_commit"] = setup["operator_before"]["head"]
    require(setup["operator_before"]["status"] == "MM operator.txt\n?? UNTRACKED.txt\n", "The operator fixture lacks all three dirty states.")
    for name, relative in WRITERS.items():
        setup["writers"][name] = {"root": runtime + "/" + name, "source": runtime + "/" + name + "/" + relative,
            "report": runtime + "/" + name + ".json", "relative_source": relative,
            "git_pointer_sha256": sha((root / name / ".git").read_bytes()),
            "registration_sha256": sha((common / "worktrees" / name / "gitdir").read_bytes()),
            "common_directory_sha256": sha((common / "worktrees" / name / "commondir").read_bytes()),
            "baseline_sha256": sha((root / name / relative).read_bytes())}
        require(snapshot(home, setup, name)["candidate_commit"] == setup["base_commit"], "A writer does not start at the common base.")
        baseline = run_check(home, setup, name)
        (evidence / (name + "-baseline.stdout")).write_text(baseline.stdout)
        (evidence / (name + "-baseline.stderr")).write_text(baseline.stderr)
        expected = "A rejected refresh must preserve the prior records." if name == "writer-a" else "Selection must match the exact requested identifier."
        require(baseline.returncode != 0 and expected in baseline.stderr, "The actual baseline does not expose its assigned defect.")
    for name in setup["operator_before"]["files"]:
        destination = evidence / "operator-before" / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes((root / "operator" / name).read_bytes())
    (evidence / "operator-index-before").write_bytes((common / "index").read_bytes())
    preserved(home, setup)
    (evidence / "setup.json").write_text(json.dumps(setup, indent=2))
    return setup


def commands(setup, writer):
    row = setup["writers"][writer]; prefix = "git -C " + shlex.quote(row["root"])
    return {"check": "python3 -B " + shlex.quote(setup["checker"]) + " " + shlex.quote(row["root"]) + " " + setup["nonce"] + " " + writer,
            "revision": prefix + " rev-parse HEAD", "diff": prefix + " diff " + setup["base_commit"] + " HEAD -- " + row["relative_source"],
            "status": prefix + " status --porcelain=v1",
            "commit": prefix + " add " + row["relative_source"] + " && " + prefix + " commit -m 'Fix " + writer + "'"}


def prompt(setup):
    assignments = []
    for name, row in setup["writers"].items():
        cmd = commands(setup, name)
        assignments.append(f'''{name}: worktree {row['root']}; permitted source {row['source']}; report {row['report']}.
Use file_read for the full assigned source, then file_edit or file_write for that source only.
Commit exactly the assigned source once, then run the exact assigned check and write the complete report with file_write.
Stop source edits after the commit. These commands are permitted for {name}:
{chr(10).join(cmd.values())}''')
    return f'''Repair two independent code defects for trial {setup['nonce']}.
Use the coordination skill and discover the actual task-worker profile.
The dirty operator checkout is {setup['operator']}. Preserve all file bytes, raw index, untracked files, branch, and HEAD.
Do not reset, stash, clean, commit, or integrate anything in the operator checkout.
Two actual isolated Git worktrees already exist at base {setup['base_commit']}.
Start exactly two task-worker children, one per named worktree and report. Do not poll status or start extra children.
Writer A must validate duplicate catalog identifiers before publication and preserve prior records, valid order, and exact case.
Writer B must select the exact requested identifier, return None when absent, and preserve the caller's records and order.
Each writer repairs only its assigned defect. Do not repair the other source in that candidate.
{chr(10).join(assignments)}
The checker is {setup['checker']}. Children may read it but must not edit it.
Each complete report must contain nonce, writer, candidate_commit, source_sha256, checks, and changed_files.
Copy the actual check values. Set changed_files to the single assigned relative source path.
Wait for both automatic terminal deliveries. Then read both full reports and both full actual candidate sources.
For each worktree, run its exact revision, diff, and check commands. The parent cannot run either commit command.
Use file_list or file_search only inside the actor's named worktree. Children cannot read the other worktree or operator files.
A project declaration may name only the actor's assigned worktree; the parent may name either worktree or the operator root.
Do not edit files, run other commands, or integrate the candidates as the parent.
Return only JSON with nonce, candidates, operator_preserved, unresolved_findings, and integration.
List candidates in writer-a then writer-b order. Each item must contain writer, run_id, worktree, report, candidate_commit, source_sha256, and checks.
Set operator_preserved to true only if the original operator checkout remains unchanged.
Set unresolved_findings to an empty list only when both assigned checks pass and all required evidence exists.
Set integration to "not_performed". Do not claim CI, release, deployment, or merged source.
'''.rstrip("\n")


def provider_calls(requests):
    for request in requests:
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                yield call["id"], call["function"]["name"], load_json(call["function"]["arguments"])


def allowed_actions(requests, setup, writer=None):
    roots = {row["root"] for row in setup["writers"].values()} | {setup["operator"]}
    if writer:
        roots = {setup["writers"][writer]["root"]}
    project_declarations(requests, roots)
    permitted_commands = set()
    for name in (WRITERS if writer is None else [writer]):
        permitted_commands.update(value for key, value in commands(setup, name).items() if writer or key != "commit")
    for _, name, args in provider_calls(requests):
        require(name in READ_TOOLS | {"shell_execute", "set_working_directory"} | ({"file_write", "file_edit"} if writer else {"spawn_agent"}),
                "An actor uses a forbidden action.")
        if name == "shell_execute":
            require(args.get("Command") in permitted_commands and args.get("WorkingDirectory") in roots | {None},
                    "A shell command leaves the actor's exact candidate scope.")
        if name in {"file_write", "file_edit"}:
            row = setup["writers"][writer]
            require(args.get("Path") in {row["source"], row["report"]} and (name != "file_edit" or args["Path"] == row["source"]),
                    "A writer changes another workspace, report, or checker.")
        if name == "file_read":
            paths = {setup["checker"]}
            for key in (WRITERS if writer is None else [writer]):
                paths.update(setup["writers"][key][field] for field in ("source", "report"))
            require(args.get("Path") in paths, "A file read leaves the actor's exact assigned files.")
        if name in {"file_list", "file_search"}:
            require(args.get("Path" if name == "file_list" else "Root") in roots, "A directory action leaves the named roots.")
    if writer:
        row = setup["writers"][writer]
        for request in requests:
            pending_commit, committed = set(), False
            for message in request.get("messages", []):
                for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                    function = call["function"]; args = load_json(function["arguments"])
                    if function["name"] in {"file_write", "file_edit"} and args.get("Path") == row["source"]:
                        require(not committed, "A writer edits source after its committed revision.")
                    if function["name"] == "shell_execute" and args.get("Command") == commands(setup, writer)["commit"]:
                        pending_commit.add(call["id"])
                if message.get("role") == "tool" and message.get("tool_call_id") in pending_commit:
                    committed |= message_text(message.get("content")).startswith("Exit code: 0\n")


def pairs_after_terminals(requests, identifiers):
    paired = set()
    for request in requests:
        seen, pending = set(), {}
        for message in request.get("messages", []):
            if message.get("role") == "tool" and message.get("tool_call_id") in identifiers:
                seen.add(message["tool_call_id"])
            if seen == identifiers:
                for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                    function = call["function"]
                    require(call["id"] not in pending, "An unresolved post-terminal call repeats.")
                    pending[call["id"]] = (function["name"], load_json(function["arguments"]))
                if message.get("role") == "tool" and message.get("tool_call_id") in pending:
                    name, args = pending.pop(message["tool_call_id"])
                    paired.add((message["tool_call_id"], *signature(name, args, message_text(message.get("content")))))
    return paired


def require_writer_feedback_order(requests, setup, writer):
    row = setup["writers"][writer]
    cmd = commands(setup, writer)
    complete = False
    for request in requests:
        retained_baseline = any(name == "file_read" and load_json(arguments).get("Path") == row["source"]
                                and sha(result.encode()) == row["baseline_sha256"]
                                for name, arguments, result in paired_calls([request]))
        phase, pending = 0, {}
        for message in request.get("messages", []):
            for call in message.get("tool_calls", []) if message.get("role") == "assistant" else []:
                function = call["function"]; args = load_json(function["arguments"])
                name = function["name"]
                step = None
                if name == "file_read" and args.get("Path") == row["source"]:
                    step = 0
                elif name in {"file_write", "file_edit"} and args.get("Path") == row["source"]:
                    step = 1
                elif name == "shell_execute" and args.get("Command") == cmd["commit"]:
                    step = 2
                elif name == "shell_execute" and args.get("Command") == cmd["check"]:
                    step = 3
                elif name == "file_write" and args.get("Path") == row["report"]:
                    step = 4
                require(not (retained_baseline and step == 1 and phase == 0),
                        "A writer edits its source before successful full baseline feedback.")
                require(call["id"] not in pending, "An unresolved writer call identifier repeats.")
                pending[call["id"]] = (step, phase == step)
            if message.get("role") == "tool" and message.get("tool_call_id") in pending:
                step, entered_after_prior_result = pending.pop(message["tool_call_id"])
                result = message_text(message.get("content"))
                succeeded = (step == 0 and sha(result.encode()) == row["baseline_sha256"]
                             or step in {1, 4} and result.startswith("Successfully ")
                             or step in {2, 3} and result.startswith("Exit code: 0\n"))
                if entered_after_prior_result and succeeded and phase == step:
                    phase += 1
        complete |= phase == 5
    require(complete, "The writer lacks ordered source, edit, commit, check, and report feedback.")


def verify(setup, receipt, events, requests, home, check_candidate):
    preserved(home, setup)
    candidates, checked, report_bytes = {}, {}, {}
    for writer, row in setup["writers"].items():
        candidate = snapshot(home, setup, writer)
        require(candidate["status"] == "" and candidate["commit_count"] == "1" and candidate["changed_files"] == row["relative_source"] + "\n",
                "A writer lacks one clean source-only commit at the assigned base.")
        candidates[writer] = candidate
        result = check_candidate(writer)
        require(result.returncode == 0, "An actual candidate fails its unchanged check: " + result.stderr)
        checked[writer] = load_json(result.stdout)
        expected = {"nonce": setup["nonce"], "writer": writer, "candidate_commit": candidate["candidate_commit"],
                    "source_sha256": candidate["source_sha256"], "checks": CHECKS[writer]}
        require(json.dumps(checked[writer], sort_keys=True) == json.dumps(expected, sort_keys=True),
                "The actual check describes another candidate or incomplete result.")
        require(snapshot(home, setup, writer) == candidate, "The candidate check changes its source, revision, or index state.")
        preserved(home, setup)
        report_bytes[writer] = actual_file(home, row["report"]).read_bytes()
        require(json.dumps(load_json(report_bytes[writer]), sort_keys=True)
                == json.dumps({**expected, "changed_files": [row["relative_source"]]}, sort_keys=True),
                "A writer report is stale or names false checks.")
    require(len({c["candidate_commit"] for c in candidates.values()}) == 2, "The two isolated candidates lack distinct revisions.")
    calls, _ = occurrences(events, receipt["session_id"])
    parent_requests = [r for r in requests if context_paths(r) is None]
    allowed_actions(parent_requests, setup)
    parent_pairs = paired_call_occurrences(parent_requests)
    require(all((c["failure"] is None or c["name"] == "spawn_agent" and is_unexecuted_rationale_rejection(c["failure"], c["result"]))
                and (c["id"], *signature(c["name"], c["arguments"], c["result"])) in parent_pairs for c in calls),
            "A parent DTO lacks its exact successful provider call/result pair.")
    declarations = project_declarations(parent_requests, {setup["operator"], *(r["root"] for r in setup["writers"].values())})
    require(declarations == {(c["id"], json.dumps(provider_arguments(c["arguments"]), sort_keys=True), c["result"])
                             for c in calls if c["name"] == "set_working_directory"}, "A parent project declaration lacks exact DTO attribution.")
    starts = [c for c in calls if c["name"] == "spawn_agent" and not is_unexecuted_rationale_rejection(c["failure"], c["result"])]
    require(len(starts) == 2, "The case requires two actual accepted writer starts.")
    accepted = [acceptance(c["result"]) for c in starts]
    require(len({a["run_id"] for a in accepted}) == 2 and sorted(map(lambda x: json.dumps(x, sort_keys=True), accepted))
            == sorted(map(lambda x: json.dumps(x, sort_keys=True), receipt["accepted_runs"]))
            and receipt["delivery_observations"]["complete"] is True and len(receipt["verified_deliveries"]) == 2,
            "The case lacks two distinct canonical accepted runs and consumed deliveries.")
    require(any((m.get("role") == "system" or m.get("role") == "user" and message_text(m.get("content")).startswith("[system: ")
                 and message_text(m.get("content")).endswith("]")) and "[available-subagents" in message_text(m.get("content"))
                and re.search(r"^## task-worker$", message_text(m.get("content")), re.MULTILINE | re.IGNORECASE)
                for r in parent_requests for m in r.get("messages", [])), "The task-worker lacks actual runtime profile discovery.")
    runs_by_writer = {}
    for index, start in enumerate(starts):
        run = accepted[index]
        terminal_id, terminal = canonical_pairs(requests, run, start["id"], "spawn_agent")
        deliveries = [d for d in receipt["verified_deliveries"] if d["accepted"] == run]
        require(len(deliveries) == 1 and json.dumps(deliveries[0]["terminal"], sort_keys=True)
                == json.dumps(terminal, sort_keys=True) and terminal["outcome"] == "Completed",
                "A writer lacks its exact completed terminal delivery.")
        child_requests = [r for r in requests if (p := context_paths(r)) is not None and p["log_path"] == terminal["log_path"]
                          and p["artifact_dir"] == terminal["artifact_directory"]]
        outputs = {args.get("Path") for _, name, args in provider_calls(child_requests) if name == "file_write"}
        matching = [name for name, row in setup["writers"].items() if row["report"] in outputs]
        require(len(matching) == 1 and matching[0] not in runs_by_writer, "An actual writer lacks one distinct assigned report producer.")
        runs_by_writer[matching[0]] = (start, run, terminal_id, terminal, child_requests)
    terminals, attributed, final = set(), set(), []
    for writer, row in setup["writers"].items():
        start, run, terminal_id, terminal, child_requests = runs_by_writer[writer]
        require(str(start["arguments"].get("Agent", "")).casefold() == "task-worker", "A writer uses the wrong actual profile.")
        required = [setup["nonce"], row["root"], row["source"], row["report"], setup["base_commit"], commands(setup, writer)["check"]]
        assignment = start["arguments"].get("Task", "") + "\n" + (start["arguments"].get("Context") or "")
        require(all(value in assignment for value in required), "A writer start lacks its exact original task scope.")
        terminals.add(terminal_id)
        require(child_requests and terminal["log_path"] not in attributed, "The writer provider context is absent or shared.")
        attributed.add(terminal["log_path"])
        require(any(all(v in "\n".join(message_text(m.get("content")) for m in r.get("messages", []) if m.get("role") == "user") for v in required)
                    for r in child_requests), "The child provider request lacks the original writer scope.")
        allowed_actions(child_requests, setup, writer)
        require_writer_feedback_order(child_requests, setup, writer)
        pairs = paired_calls(child_requests)
        source = actual_file(home, row["source"]).read_bytes()
        require(any(name == "file_read" and load_json(args).get("Path") == row["source"] and sha(result.encode()) == row["baseline_sha256"]
                    and load_json(args).get("StartLine") in (None, 0) and load_json(args).get("Limit") in (None, 0)
                    for name, args, result in pairs), "The writer lacks its actual complete baseline source read.")
        require(any(load_json(args).get("Path") == row["source"] and (
                    name == "file_write" and load_json(args).get("Content") == source.decode() and result == f"Successfully wrote {len(source)} bytes to {row['source']}"
                    or name == "file_edit" and re.fullmatch(re.escape("Successfully edited " + row["source"]) + r": replaced [1-9][0-9]* occurrence\(s\)", result))
                    for name, args, result in pairs), "The actual writer lacks its paired successful source edit.")
        require(any(name == "shell_execute" and load_json(args).get("Command") == commands(setup, writer)["commit"]
                    and (match := re.match(r"Exit code: 0\n\[detached HEAD ([0-9a-f]{7,40})\] Fix " + writer + r"\n", result))
                    and candidates[writer]["candidate_commit"].startswith(match[1]) for name, args, result in pairs), "The writer lacks its exact candidate commit receipt.")
        actual_result = "Exit code: 0\n" + json.dumps(checked[writer], sort_keys=True) + "\n"
        require(any(name == "shell_execute" and load_json(args).get("Command") == commands(setup, writer)["check"] and result == actual_result
                    for name, args, result in pairs), "The writer lacks its actual successful check receipt.")
        content = report_bytes[writer]
        require(any(name == "file_write" and load_json(args).get("Path") == row["report"] and load_json(args).get("Content") == content.decode()
                    and result == f"Successfully wrote {len(content)} bytes to {row['report']}" for name, args, result in pairs), "The writer lacks its complete actual report write receipt.")
        final.append({"writer": writer, "run_id": run["run_id"], "worktree": row["root"], "report": row["report"],
                      "candidate_commit": candidates[writer]["candidate_commit"], "source_sha256": candidates[writer]["source_sha256"], "checks": CHECKS[writer]})
    after = pairs_after_terminals(parent_requests, terminals)
    for writer, row in setup["writers"].items():
        for path, content in ((row["source"], actual_file(home, row["source"]).read_bytes()), (row["report"], report_bytes[writer])):
            require(any(c["name"] == "file_read" and c["arguments"].get("Path") == path and c["arguments"].get("StartLine") in (None, 0)
                        and c["arguments"].get("Limit") in (None, 0) and c["result"].encode() == content
                        and (c["id"], *signature(c["name"], c["arguments"], c["result"])) in after for c in calls), "The parent lacks complete post-terminal source or report reads.")
        outputs = {"revision": candidates[writer]["candidate_commit"] + "\n",
                   "diff": git(home, setup, writer, "diff", setup["base_commit"], "HEAD", "--", row["relative_source"]).decode(),
                   "check": json.dumps(checked[writer], sort_keys=True) + "\n"}
        for key, output in outputs.items():
            require(any(c["name"] == "shell_execute" and c["arguments"].get("Command") == commands(setup, writer)[key]
                        and c["result"] == "Exit code: 0\n" + output and (c["id"], *signature(c["name"], c["arguments"], c["result"])) in after
                        for c in calls), "The parent lacks its actual post-terminal candidate revision, diff, or check.")
    require(json.dumps(load_json(receipt["last_reply"]), sort_keys=True) == json.dumps(
            {"nonce": setup["nonce"], "candidates": final, "operator_preserved": True,
             "unresolved_findings": [], "integration": "not_performed"}, sort_keys=True),
            "The final result changes a candidate, check, unresolved finding, or integration state.")
    require(type(load_json(receipt["last_reply"]).get("operator_preserved")) is bool, "The preservation claim is not a JSON Boolean.")
    preserved(home, setup)
    require(all(snapshot(home, setup, w) == candidates[w] for w in WRITERS), "A final candidate changed after its actual check.")
    return {"passed": True, "candidates": final, "operator_preserved": True,
            "limits": ["Two isolated writer runs do not prove overlapping executor schedules or a serialized shared-worktree fallback.",
                       "The case performs no candidate integration, CI, release, or deployment.",
                       "Cumulative captures can hide identical child occurrences after compaction.",
                       "Full reads prove access, not complete comprehension."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "verify"))
    for name in ("eval-home", "evidence"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--container", required=True)
    parser.add_argument("--observer-directory", type=Path)
    parser.add_argument("--relay-directory", type=Path)
    args = parser.parse_args()
    if args.action == "prepare":
        setup = prepare(args.eval_home, args.evidence, lambda root: subprocess.run(
            ["docker", "exec", "-i", "--user", "netclaw", args.container, "bash", "-s"],
            input=initialization_script(root), capture_output=True, text=True))
        print(prompt(setup), end="")
    else:
        require(args.observer_directory and args.relay_directory, "The actual observer and relay evidence are required.")
        setup = load_json((args.evidence / "setup.json").read_text())
        data = load_json((args.observer_directory / "observer-input.json").read_text())
        receipt = load_json((args.observer_directory / "verified-receipt.json").read_text())
        validate_prompt_receipt(receipt, data)
        require(data["Mode"] == legacy_observer_mode(CASE, receipt["prompt_ordinal"])
                and data["InitialPrompt"] == prompt(setup) and receipt["case"] == CASE,
                "The observer describes another case attempt.")
        captured = {}
        for writer, row in setup["writers"].items():
            for key in ("source", "report"):
                source = actual_file(args.eval_home, row[key]); destination = writer + "-" + source.name
                if source.is_file():
                    content = source.read_bytes(); (args.observer_directory / destination).write_bytes(content)
                    captured[destination] = {"state": "captured", "sha256": sha(content)}
                else:
                    captured[destination] = {"state": "missing"}
        (args.observer_directory / "two-writer-artifacts.json").write_text(json.dumps(captured, indent=2))
        events = [load_json(line) for line in (args.observer_directory / "session-output.jsonl").read_text().splitlines() if line.strip()]
        def check_candidate(writer):
            return subprocess.run(["docker", "exec", "--user", "netclaw", args.container, "python3", "-B", setup["checker"],
                                   setup["writers"][writer]["root"], setup["nonce"], writer], capture_output=True, text=True,
                                  timeout=int(os.environ["PROMPT_TIMEOUT"]))
        print(json.dumps(verify(setup, receipt, events, evidence_requests(args.relay_directory), args.eval_home, check_candidate), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
