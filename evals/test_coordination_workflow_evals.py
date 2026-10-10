"""Controls for the case adapter and cumulative child consumption."""

import copy
from contextlib import closing
import hashlib
import json
import os
import shutil
import sqlite3
import errno
from pathlib import Path
import re
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from child_run_evals import actual_file, consumed_deliveries, legacy_observer_mode
from coordination_workflow_evals import archive_read, archive_runtime_files, blocked_config, contract, prepare, prompt, workflow_stages
from test_child_run_evals import ACCEPTED, PATHS, child, parent, terminal

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "evals/fixtures/coordination-artifacts"
CASES = ("coordination_analyze_plan", "coordination_attachment_blocked")


def shell_functions(*names):
    source = (ROOT / "evals/run-evals.sh").read_text()
    return "\n".join(re.search(r"^" + re.escape(name) + r"\(\) \{\n.*?^\}", source,
                              re.MULTILINE | re.DOTALL).group(0) for name in names)


def stage_evidence(root):
    home = root / "home"
    setup = prepare(FIXTURE, home, root / "setup")
    receipt = {"session_id": "session-neutral", "accepted_runs": [], "verified_deliveries": []}
    events, requests = [], []
    for index, (key, agent) in enumerate((("findings_path", "headless-analyst"), ("plan_path", "task-worker")), 1):
        content = f"Neutral complete artifact {index}.\n"
        actual_file(home, setup[key]).write_text(content)
        accepted = {**ACCEPTED, "run_id": f"run-{index}", "scope_id": f"scope-{index}"}
        paths = {name: value.replace("/neutral/subagents/neutral", f"/neutral/subagents/run-{index}")
                 for name, value in PATHS.items()}
        receipt["accepted_runs"].append(accepted)
        receipt["verified_deliveries"].append({"accepted": accepted, "terminal": {
            "log_path": paths["log_path"], "artifact_directory": paths["artifact_dir"]}})
        args = {"Agent": agent, "Task": setup[key],
                "Context": setup["findings_path"] if index == 2 else "Analyze the source."}
        for dto in ({"Type": "tool_call", "CallId": f"start-{index}", "ToolName": "spawn_agent",
                     "ArgumentsJson": json.dumps(args)},
                    {"Type": "tool_result", "CallId": f"start-{index}", "ToolName": "spawn_agent",
                     "Result": json.dumps(accepted)}):
            sequence = len(events) + 1
            events.append({"sequence": sequence, "observed_ns": sequence, "output": {
                **dto, "SessionId": "session-neutral"}})
        request = child()
        request["messages"][1]["content"] = "Context:\n" + "\n".join(k + ": " + v for k, v in paths.items())
        request["messages"].extend([
            {"role": "assistant", "tool_calls": [{"id": f"write-{index}", "function": {
                "name": "file_write", "arguments": json.dumps({"Path": setup[key], "Content": content})}}]},
            {"role": "tool", "tool_call_id": f"write-{index}",
             "content": f"Successfully wrote {len(content.encode())} bytes to {setup[key]}"}])
        requests.append(request)
    requests.append({"messages": [{"role": "user", "content":
        "[system: [available-subagents — use spawn_agent to delegate]\n\n## headless-analyst\nA source analyst.\n]"}]})
    return receipt, events, requests, setup, home


def observer_calls(events):
    calls, pending = [], {}
    turn = 1
    for event in events:
        output = event["output"]
        if output["Type"] == "tool_call":
            row = {"id": output["CallId"], "name": output["ToolName"],
                   "arguments": json.loads(output["ArgumentsJson"]), "turn": turn,
                   "observed_ns": event["observed_ns"], "occurrence": len(calls) + 1}
            pending[row["id"]] = row
            calls.append(row)
        elif output["Type"] == "tool_result":
            row = pending.pop(output["CallId"])
            row.update(result=output["Result"], failure_code=output.get("ToolFailureCode"),
                       success=output.get("ToolFailureCode") is None)
        elif output["Type"] == "turn_completed":
            turn += 1
    assert not pending
    return calls



class CoordinationArchiveControls(unittest.TestCase):
    def fixture(self, root, copied=True):
        home = root / "home"
        evidence = root / "child-runs"
        setup_dir = evidence / "coordination-case"
        setup = prepare(FIXTURE, home, setup_dir)
        actual_file(home, setup["findings_path"]).write_bytes(b"actual findings\r\n")
        actual_file(home, setup["plan_path"]).write_bytes(b"actual plan\r\n")
        observer = evidence / "observer-neutral"
        observer.mkdir()
        relay = evidence / "relay"
        relay.mkdir()
        data = {"Nonce": "neutral-archive", "Mode": "collect", "InitialPrompt": prompt(setup), "SessionId": "session-neutral"}
        receipt = {"session_id": data["SessionId"], "prompt_nonce": data["Nonce"], "observer_mode": data["Mode"],
                   "initial_prompt_sha256": hashlib.sha256(data["InitialPrompt"].encode()).hexdigest(),
                   "status": "observed", "accepted_runs": [], "calls": []}
        events, requests = [], []
        def emit(kind, **values):
            index = len(events) + 1
            events.append({"sequence": index, "observed_ns": index, "output": {
                "Type": kind, "SessionId": data["SessionId"], **values}})
        history = {"messages": []}
        for index in (1, 2):
            accepted = {**ACCEPTED, "run_id": "run-" + str(index), "scope_id": "scope-" + str(index)}
            receipt["accepted_runs"].append(accepted)
            arguments = {"Agent": "task-worker", "Task": "A neutral assigned artifact."}
            emit("tool_call", CallId="start-" + str(index), ToolName="spawn_agent", ArgumentsJson=json.dumps(arguments))
            emit("tool_result", CallId="start-" + str(index), ToolName="spawn_agent", Result=json.dumps(accepted))
            history["messages"].extend([
                {"role": "assistant", "tool_calls": [{"id": "start-" + str(index), "function": {
                    "name": "spawn_agent", "arguments": json.dumps(arguments)}}]},
                {"role": "tool", "tool_call_id": "start-" + str(index), "content": json.dumps(accepted)}])
            child_root = "/home/netclaw/.netclaw/sessions/neutral/subagents/" + accepted["run_id"]
            paths = {"session_dir": "/home/netclaw/.netclaw/sessions/neutral/workspace",
                     "temp_dir": child_root + "/tmp", "artifact_dir": child_root + "/artifacts",
                     "log_path": child_root + "/logs/session.log"}
            request = child()
            request["messages"][1]["content"] = "Context:\n" + "\n".join(k + ": " + v for k, v in paths.items())
            requests.append(request)
            body = {**terminal(), "run_id": accepted["run_id"], "scope_id": accepted["scope_id"],
                    "log_path": paths["log_path"], "artifact_directory": paths["artifact_dir"]}
            delivery = parent(body, "delivery-" + str(index))
            delivery["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
                {"run_id": accepted["run_id"], "source_operation": "spawn_agent"})
            history["messages"].extend(delivery["messages"])
        target = ("/home/netclaw/.netclaw/sessions/neutral/workspace/attachments/plan-1.md" if copied else setup["plan_path"])
        actual_file(home, target).parent.mkdir(parents=True, exist_ok=True)
        actual_file(home, target).write_bytes(actual_file(home, setup["plan_path"]).read_bytes())
        result = "File attached: plan.md (text/markdown) at " + target + (" (copied into current session)" if copied else "")
        arguments = {"Path": setup["plan_path"]}
        emit("tool_call", CallId="attach-neutral", ToolName="attach_file", ArgumentsJson=json.dumps(arguments))
        emit("tool_result", CallId="attach-neutral", ToolName="attach_file", Result=result)
        emit("file", FilePath=target, FileName="plan.md", MimeType="text/markdown")
        history["messages"].extend([
            {"role": "assistant", "tool_calls": [{"id": "attach-neutral", "function": {
                "name": "attach_file", "arguments": json.dumps(arguments)}}]},
            {"role": "tool", "tool_call_id": "attach-neutral", "content": result}])
        requests.append(history)
        receipt["calls"] = observer_calls(events)
        log = actual_file(home, "/home/netclaw/.netclaw/sessions/neutral/logs/session.log")
        log.parent.mkdir(parents=True)
        log.write_text("\n".join("child_run_accepted owner=session-neutral runId=run-" + str(index) + " journalSequence=" + str(index)
                                for index in (1, 2)))
        def save():
            (observer / "observer-input.json").write_text(json.dumps(data))
            (observer / "observer-receipt.json").write_text(json.dumps(receipt))
            (observer / "session-output.jsonl").write_text("\n".join(json.dumps(row) for row in events))
            for index, request in enumerate(requests):
                (relay / f"request-{index:04}.json").write_text(json.dumps(request))
        save()
        return home, evidence, setup, observer, receipt, events, requests, target, save

    def archive(self, home, evidence):
        return archive_runtime_files(FIXTURE, home, evidence / "coordination-case", evidence)

    def test_retains_actual_sources_artifacts_and_both_delivery_locations(self):
        for copied in (False, True):
            with self.subTest(copied=copied), tempfile.TemporaryDirectory() as directory:
                home, evidence, setup, _, _, _, _, target, _ = self.fixture(Path(directory), copied)
                actual_file(home, target).write_bytes(b"actual delivered bytes\x00\r\n")
                inventory = self.archive(home, evidence)
                self.assertIs(inventory["capture_complete"], True)
                self.assertEqual(setup["source_root"], inventory["source_root"])
                self.assertEqual(4, len(inventory["files"]))
                for row in inventory["files"]:
                    content = (evidence / "coordination-case/runtime-files" / row["archive_path"]).read_bytes()
                    self.assertEqual(actual_file(home, row["canonical_path"]).read_bytes(), content)
                    self.assertEqual(len(content), row["length"])
                    self.assertEqual(hashlib.sha256(content).hexdigest(), row["sha256"])
                delivery = inventory["files"][-1]
                self.assertEqual(target, delivery["canonical_path"])
                self.assertEqual("attach-neutral", delivery["attribution"]["call_id"])
                self.assertNotIn("passed", inventory)
                if copied:
                    self.assertNotEqual(inventory["files"][2]["sha256"], delivery["sha256"])

    def test_preserves_missing_altered_and_partial_workflow_facts_without_credit(self):
        with tempfile.TemporaryDirectory() as directory:
            home, evidence, setup, _, receipt, events, _, _, save = self.fixture(Path(directory))
            actual_file(home, setup["source_root"] + "/source/catalog.py").write_bytes(b"altered actual source\r\n")
            actual_file(home, setup["findings_path"]).unlink()
            actual_file(home, setup["plan_path"]).write_bytes(b"partial plan")
            receipt["status"] = "incomplete"
            receipt["last_reply"] = ""
            events.clear()
            save()
            inventory = self.archive(home, evidence)
            self.assertIs(inventory["files"][0]["matches_expected"], False)
            self.assertIs(inventory["files"][1]["present"], False)
            self.assertNotIn("sha256", inventory["files"][1])
            self.assertEqual(b"partial plan", (evidence / "coordination-case/runtime-files/workspace/plan.md").read_bytes())
            self.assertEqual("incomplete", inventory["observers"][0]["status"])
            self.assertEqual(0, inventory["observers"][0]["file_outputs"])

    def test_preserves_no_observer_and_missing_source_facts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            home, evidence = root / "home", root / "child-runs"
            setup = prepare(FIXTURE, home, evidence / "coordination-case")
            actual_file(home, setup["source_root"] + "/source/catalog.py").unlink()
            inventory = self.archive(home, evidence)
            self.assertEqual([], inventory["observers"])
            self.assertTrue(all(row["present"] is False for row in inventory["files"]))

    def test_missing_delivery_target_remains_an_explicit_file_fact(self):
        with tempfile.TemporaryDirectory() as directory:
            home, evidence, _, _, _, _, _, target, _ = self.fixture(Path(directory))
            actual_file(home, target).unlink()
            inventory = self.archive(home, evidence)
            delivery = inventory["files"][-1]
            self.assertEqual("delivery", delivery["kind"])
            self.assertIs(delivery["present"], False)
            self.assertNotIn("sha256", delivery)
            self.assertNotIn("passed", inventory)

    def test_rejects_foreign_root_artifact_scope_and_links(self):
        for fault in ("root", "artifact", "source-link", "inside-link", "dangling-link", "parent-link", "destination-link"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, setup, _, _, _, _, _, _ = self.fixture(root)
                path = actual_file(home, setup["findings_path"])
                if fault in ("root", "artifact"):
                    setup["source_root" if fault == "root" else "findings_path"] = "/home/netclaw/.netclaw/workspaces/foreign"
                    (evidence / "coordination-case/setup.json").write_text(json.dumps(setup))
                elif fault == "parent-link":
                    source_dir = actual_file(home, setup["source_root"] + "/source")
                    source_dir.rename(root / "source-copy")
                    source_dir.symlink_to(root / "source-copy", target_is_directory=True)
                elif fault == "destination-link":
                    (evidence / "coordination-case/runtime-files").symlink_to(root / "outside")
                else:
                    if fault == "source-link":
                        path = actual_file(home, setup["source_root"] + "/source/catalog.py")
                    path.unlink()
                    target = (actual_file(home, setup["plan_path"]) if fault == "inside-link" else root / "outside")
                    if fault != "dangling-link" and fault != "inside-link":
                        target.write_bytes(b"outside marker")
                    path.symlink_to(target)
                with self.assertRaises((AssertionError, OSError)):
                    self.archive(home, evidence)

    def test_rejects_foreign_owner_unpaired_failed_and_arbitrary_delivery(self):
        for fault in ("owner", "provider-id", "failure", "result", "foreign-session", "private-path", "unknown-name", "child-context", "malformed-context", "start-provider-id", "event-order", "log-owner"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, setup, _, receipt, events, requests, target, save = self.fixture(root)
                if fault == "owner":
                    receipt["session_id"] = "foreign-owner"
                elif fault == "provider-id":
                    requests[-1]["messages"][-2]["tool_calls"][0]["id"] = "foreign-call"
                    requests[-1]["messages"][-1]["tool_call_id"] = "foreign-call"
                elif fault == "failure":
                    events[-2]["output"]["ToolFailureCode"] = "access_denied"
                elif fault == "result":
                    events[-2]["output"]["Result"] = "File attached: unsupported"
                elif fault == "foreign-session":
                    events[-1]["output"]["SessionId"] = "foreign-owner"
                elif fault in ("private-path", "unknown-name"):
                    replacement = target.replace("/neutral/", "/foreign/") if fault == "private-path" else target.replace("plan-1.md", "private.md")
                    events[-1]["output"]["FilePath"] = replacement
                    events[-2]["output"]["Result"] = events[-2]["output"]["Result"].replace(target, replacement)
                    requests[-1]["messages"][-1]["content"] = events[-2]["output"]["Result"]
                elif fault == "child-context":
                    requests[0]["messages"][1]["content"] = requests[0]["messages"][1]["content"].replace("workspace", "foreign")
                elif fault == "malformed-context":
                    requests[0]["messages"][1]["content"] = "Context without runtime paths."
                elif fault == "start-provider-id":
                    requests[-1]["messages"][0]["tool_calls"][0]["id"] = "foreign-start"
                    requests[-1]["messages"][1]["tool_call_id"] = "foreign-start"
                elif fault == "event-order":
                    events[-1]["sequence"] = True
                else:
                    actual_file(home, "/home/netclaw/.netclaw/sessions/neutral/logs/session.log").write_text("foreign owner")
                save()
                with self.assertRaises((AssertionError, ValueError)):
                    self.archive(home, evidence)

    def test_delivery_links_and_read_or_copy_errors_fail_loudly(self):
        for fault in ("link", "read", "write"):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, _, _, _, _, _, target, _ = self.fixture(root)
                if fault == "link":
                    actual_file(home, target).unlink()
                    outside = root / "outside"
                    outside.write_bytes(b"outside marker")
                    actual_file(home, target).symlink_to(outside)
                    with self.assertRaises((AssertionError, OSError)):
                        self.archive(home, evidence)
                else:
                    if fault == "read":
                        original = os.open
                        def fail(name, flags, *args, **kwargs):
                            if name == "plan-1.md":
                                raise OSError("owned archive read failure")
                            return original(name, flags, *args, **kwargs)
                        with patch("coordination_workflow_evals.os.open", side_effect=fail), self.assertRaises(OSError):
                            self.archive(home, evidence)
                    else:
                        original = Path.open
                        def fail(path, *args, **kwargs):
                            if args == ("xb",) and "runtime-files" in path.parts:
                                raise OSError("owned archive write failure")
                            return original(path, *args, **kwargs)
                        with patch.object(Path, "open", fail), self.assertRaises(OSError):
                            self.archive(home, evidence)
                inventory = json.loads((evidence / "coordination-case/runtime-files/inventory.json").read_text())
                self.assertIs(inventory["capture_complete"], False)
                self.assertTrue(inventory["error"])

    def test_ancestor_swap_cannot_redirect_the_actual_read(self):
        for swap_after_open in (False, True):
            with self.subTest(swap_after_open=swap_after_open), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, setup, _, _, _, _, _, _ = self.fixture(root)
                source = actual_file(home, setup["source_root"] + "/source")
                outside = root / "outside"
                outside.mkdir()
                (outside / "catalog.py").write_bytes(b"private outside marker")
                expected = (source / "catalog.py").read_bytes()
                original = os.open
                swapped = False
                def open_and_swap(name, flags, *args, **kwargs):
                    nonlocal swapped
                    if name == "source" and not swapped:
                        swapped = True
                        if swap_after_open:
                            descriptor = original(name, flags, *args, **kwargs)
                        source.rename(source.with_name("detached-source"))
                        source.symlink_to(outside, target_is_directory=True)
                        if swap_after_open:
                            return descriptor
                    return original(name, flags, *args, **kwargs)
                with patch("coordination_workflow_evals.os.open", side_effect=open_and_swap):
                    if swap_after_open:
                        self.assertEqual(expected, archive_read(home, setup["source_root"] + "/source/catalog.py"))
                    else:
                        with self.assertRaises(OSError) as error:
                            archive_read(home, setup["source_root"] + "/source/catalog.py")
                        self.assertIn(error.exception.errno, (errno.ELOOP, errno.ENOTDIR))
                self.assertTrue(swapped)
                self.assertEqual(b"private outside marker", (outside / "catalog.py").read_bytes())

    def test_nonregular_source_fails_without_an_open_that_waits_for_a_writer(self):
        with tempfile.TemporaryDirectory() as directory:
            home, evidence, setup, _, _, _, _, _, _ = self.fixture(Path(directory))
            source = actual_file(home, setup["findings_path"])
            source.unlink()
            os.mkfifo(source)
            with self.assertRaisesRegex(AssertionError, "regular file"):
                self.archive(home, evidence)

    def test_actual_archive_hook_retains_bytes_and_failure_still_cleans_owned_resources(self):
        for fault in (False, True):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                home, evidence, setup, _, _, _, _, _, _ = self.fixture(root)
                repo = root / "repo"
                (repo / "evals").mkdir(parents=True)
                shutil.copyfile(ROOT / "evals/coordination_workflow_evals.py", repo / "evals/coordination_workflow_evals.py")
                if fault:
                    supplied = actual_file(home, setup["findings_path"])
                    supplied.unlink()
                    supplied.symlink_to(root / "missing")
                command = shell_functions("check_prerequisites", "archive_eval_run", "cleanup_eval_env") + r"""
set -euo pipefail
docker() { printf '%s\n' "docker $*" >> "$ACTION_LOG"; if [[ "$1" == image ]]; then echo synthetic-image; fi; }
kill() { printf '%s\n' "kill $*" >> "$ACTION_LOG"; }
wait() { printf '%s\n' "wait $*" >> "$ACTION_LOG"; }
force_rmrf() { printf '%s\n' "home cleanup" >> "$ACTION_LOG"; rm -rf "$1"; }
resolve_eval_target() { :; }
mktemp() {
    case "$*" in
        *netclaw-eval-home-*) printf '%s\n' "$OWNED_HOME" ;;
        *netclaw-eval-tmp-*) printf '%s\n' "$OWNED_TEMP" ;;
        *) return 1 ;;
    esac
}
check_prerequisites
"""
                env = {**os.environ, "PYTHONDONTWRITEBYTECODE": "1", "PYTHONPATH": str(ROOT / "evals"),
                       "REPO_ROOT": str(repo), "EVAL_ASSET_ROOT": str(ROOT), "EVAL_HOME": str(home),
                       "TMPDIR_EVAL": str(evidence.parent), "COORDINATION_CASE_EVIDENCE": str(evidence / "coordination-case"),
                       "ACTION_LOG": str(root / "actions"), "RUN_ID": "owned-test", "NETCLAW_IMAGE": "synthetic-image",
                       "EVAL_CONTAINER_NAME": "owned-test", "CYCLE_FIXTURE_PID_SAVED": "11", "CHILD_FIXTURE_PID_SAVED": "12",
                       "FILTER_CASE": CASES[0], "RUNS": "1", "OWNED_HOME": str(home)}
                # The real temp root contains child-runs. Keep the operator root outside teardown.
                temp = root / "temp"
                temp.mkdir()
                evidence.rename(temp / "child-runs")
                env["TMPDIR_EVAL"] = str(temp)
                env["OWNED_TEMP"] = str(temp)
                env["COORDINATION_CASE_EVIDENCE"] = str(temp / "child-runs/coordination-case")
                database = home / "evals/results.db"
                database.parent.mkdir(parents=True)
                with closing(sqlite3.connect(database)) as connection:
                    connection.execute("CREATE TABLE eval_results (passed INTEGER, details TEXT)")
                    connection.execute("INSERT INTO eval_results VALUES (1, 'pass')")
                    connection.commit()
                result = subprocess.run(["bash", "-c", command], env=env, text=True, capture_output=True)
                self.assertEqual(1 if fault else 0, result.returncode, result.stderr)
                self.assertFalse(home.exists())
                self.assertFalse(temp.exists())
                actions = (root / "actions").read_text()
                self.assertIn("docker stop owned-test", actions)
                self.assertIn("kill 11", actions)
                self.assertIn("wait 12", actions)
                self.assertTrue(actions.rstrip().endswith("home cleanup"))
                with closing(sqlite3.connect(repo / "evals/runs/owned-test/results.db")) as connection:
                    self.assertEqual([(1, "pass")], connection.execute("SELECT passed, details FROM eval_results").fetchall())
                archive = repo / "evals/runs/owned-test/child-runs/coordination-case/runtime-files"
                inventory = json.loads((archive / "inventory.json").read_text())
                self.assertIs(inventory["capture_complete"], not fault)
                self.assertTrue((archive / "workspace/source/catalog.py").is_file())
                if fault:
                    self.assertIn("link", (archive.parent / "archive.stderr").read_text())
                else:
                    self.assertEqual(b"actual plan\r\n", (archive / "workspace/plan.md").read_bytes())


class CoordinationWorkflowControls(unittest.TestCase):
    def test_selection_uses_collect_only_for_first_current_prompt(self):
        functions = shell_functions("child_result_consumer")
        for case in CASES:
            self.assertEqual("collect", legacy_observer_mode(case, 1))
            for ordinal in (0, 2, True):
                with self.subTest(case=case, ordinal=ordinal), self.assertRaises(AssertionError):
                    legacy_observer_mode(case, ordinal)
            result = subprocess.run(["bash", "-c", functions + '\nFILTER_CASE="$1"; child_result_consumer',
                                     "bash", case], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotEqual(0, subprocess.run(["bash", "-c", functions +
                            '\nFILTER_CASE=; child_result_consumer']).returncode)

    def test_setup_copies_only_neutral_source_with_unique_owned_roots(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            setups = [prepare(FIXTURE, root / "home", root / f"evidence-{index}") for index in range(2)]
            self.assertNotEqual(setups[0]["source_root"], setups[1]["source_root"])
            for setup in setups:
                workspace = actual_file(root / "home", setup["source_root"])
                self.assertEqual(["source/catalog.py"], [str(path.relative_to(workspace))
                                 for path in workspace.rglob("*") if path.is_file()])
                self.assertEqual((FIXTURE / "source/catalog.py").read_bytes(),
                                 (workspace / "source/catalog.py").read_bytes())
                self.assertFalse(actual_file(root / "home", setup["findings_path"]).exists())
                self.assertFalse(actual_file(root / "home", setup["plan_path"]).exists())
                self.assertNotIn((FIXTURE / "artifacts/plan-complete.md").read_text(), prompt(setup))

    def test_post_start_workspace_permits_daemon_artifacts_without_source_or_sibling_permission_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sibling = root / "home/data/workspaces/operator-owned"
            sibling.mkdir(parents=True)
            sibling.chmod(0o700)
            fixture_modes = {p: stat.S_IMODE(p.stat().st_mode) for p in FIXTURE.rglob("*")}
            old_umask = os.umask(0o022)
            try:
                setup = prepare(FIXTURE, root / "home", root / "evidence")
            finally:
                os.umask(old_umask)
            workspace = actual_file(root / "home", setup["source_root"])
            self.assertEqual(0o777, stat.S_IMODE(workspace.stat().st_mode))
            self.assertEqual(0o755, stat.S_IMODE((workspace / "source").stat().st_mode))
            self.assertEqual(0o644, stat.S_IMODE((workspace / "source/catalog.py").stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE(sibling.stat().st_mode))
            self.assertEqual(fixture_modes, {p: stat.S_IMODE(p.stat().st_mode) for p in FIXTURE.rglob("*")})

    def test_blocked_config_preserves_base_and_other_policy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, destination = root / "base.json", root / "derived.json"
            original = {"Tools": {"AudienceProfiles": {"Personal": {"WriteFiles": {"Mode": "All"}},
                        "Team": {"AttachFiles": {"Mode": "None"}}}}, "marker": "neutral"}
            source.write_text(json.dumps(original))
            before = source.read_bytes()
            blocked_config(source, destination)
            expected = copy.deepcopy(original)
            expected["Tools"]["AudienceProfiles"]["Personal"].update(
                ReadFiles={"Mode": "All", "Roots": []}, AttachFiles={"Mode": "None", "Roots": []})
            self.assertEqual(expected, json.loads(destination.read_text()))
            self.assertEqual(before, source.read_bytes())
            with self.assertRaises(FileExistsError):
                blocked_config(source, destination)

    def test_actual_config_adapter_selects_denial_only_for_blocked_case(self):
        functions = shell_functions("prepare_coordination_config")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            base = root / "base.json"
            base.write_text('{"marker":"neutral"}')
            script = functions + '\nprepare_coordination_config\ncat "$NETCLAW_EVAL_CONFIG_FILE"'
            for case in CASES:
                env = {**os.environ, "FILTER_CASE": case, "REPO_ROOT": str(ROOT),
                       "NETCLAW_EVAL_CONFIG_FILE": str(base), "TMPDIR_EVAL": str(root / case)}
                result = subprocess.run(["bash", "-eu", "-c", script], env=env, capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stderr)
                actual = json.loads(result.stdout)
                if case.endswith("blocked"):
                    self.assertEqual("None", actual["Tools"]["AudienceProfiles"]["Personal"]["AttachFiles"]["Mode"])
                    self.assertEqual("All", actual["Tools"]["AudienceProfiles"]["Personal"]["ReadFiles"]["Mode"])
                else:
                    self.assertEqual({"marker": "neutral"}, actual)
            self.assertEqual('{"marker":"neutral"}', base.read_text())

    def test_main_selects_denied_config_before_daemon_start(self):
        functions = shell_functions("prepare_coordination_config", "main")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            base = root / "base.json"
            base.write_text('{"marker":"neutral"}')
            script = functions + r'''
check_prerequisites() { :; }
build_local_image() { :; }
child_result_consumer() { return 1; }
start_eval_daemon() { cat "$NETCLAW_EVAL_CONFIG_FILE"; exit 0; }
main
'''
            for case in CASES:
                env = {**os.environ, "FILTER_CASE": case, "FILTER_CATEGORY": "", "NETCLAW_BIN": "/bin/true",
                       "REPO_ROOT": str(ROOT), "NETCLAW_EVAL_CONFIG_FILE": str(base),
                       "TMPDIR_EVAL": str(root / case)}
                result = subprocess.run(["bash", "-eu", "-c", script], env=env, capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stderr)
                actual = json.loads(result.stdout)
                if case.endswith("blocked"):
                    self.assertEqual("None", actual["Tools"]["AudienceProfiles"]["Personal"]["AttachFiles"]["Mode"])
                else:
                    self.assertEqual({"marker": "neutral"}, actual)

    def test_registration_selects_one_explicit_case_and_excludes_default(self):
        script = shell_functions("run_all") + r'''
print_category() { :; }
end_category() { :; }
run_multi_turn_case() { :; }
run_case() {
    if [[ "$1" == --json ]]; then shift; fi
    case "$1" in coordination_analyze_plan|coordination_attachment_blocked) echo "$1";; esac
}
run_all
'''
        for case in (*CASES, ""):
            result = subprocess.run(["bash", "-eu", "-c", script], env={**os.environ, "FILTER_CASE": case, "PROMPT_TIMEOUT": "180",
                "LARGE_OUTPUT_EVAL_COMMAND": "neutral", "PROJECT_SCOPE_EVAL_ROOT": "/neutral",
                "PROJECT_SCOPE_EVAL_MISSING_ROOT": "/missing", "NATURALISTIC_PROJECT_ROOT": "/neutral",
                "NATURALISTIC_CWD_ROOT": "/neutral"},
                                    capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(case, result.stdout.strip())

    def test_actual_stage_binding_requires_two_producers_and_exact_full_write_receipts(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = stage_evidence(Path(directory))
            workflow_stages(*evidence)
            mutations = [
                lambda r, e, q, s: e.__delitem__(slice(0, 2)),
                lambda r, e, q, s: r["verified_deliveries"].pop(0),
                lambda r, e, q, s: r["accepted_runs"].pop(0),
                lambda r, e, q, s: q.pop(0),
                lambda r, e, q, s: q.pop(),
                lambda r, e, q, s: q[0]["messages"][-1].update(content="write claimed without receipt"),
                lambda r, e, q, s: q[0]["messages"][-1].update(tool_call_id="foreign"),
                lambda r, e, q, s: q[0]["messages"][-2]["tool_calls"][0]["function"].update(arguments=json.dumps(
                    {"Path": s["findings_path"], "Content": "forged bytes"})),
                lambda r, e, q, s: q[0]["messages"][-2]["tool_calls"][0]["function"].update(arguments=json.dumps(
                    {"Path": s["plan_path"], "Content": "wrong target"})),
                lambda r, e, q, s: r["verified_deliveries"][0]["terminal"].update(log_path="foreign"),
                lambda r, e, q, s: e[2]["output"].update(ArgumentsJson=json.dumps({"Agent": "summarizer",
                    "Task": s["plan_path"], "Context": s["findings_path"]})),
                lambda r, e, q, s: e[3]["output"].update(Result=e[1]["output"]["Result"]),
            ]
            for index, mutate in enumerate(mutations):
                receipt, events, requests, setup, home = copy.deepcopy(evidence)
                mutate(receipt, events, requests, setup)
                for sequence, event in enumerate(events, 1):
                    event["sequence"] = sequence
                with self.subTest(fault=index), self.assertRaises(AssertionError):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_shared_runtime_context_selects_one_worker_and_rejects_wrong_or_duplicate_profiles(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            for event in (events[0], events[2]):
                args = json.loads(event["output"]["ArgumentsJson"])
                args["Context"] = "Runtime artifact paths:\n" + setup["findings_path"] + "\n" + setup["plan_path"]
                event["output"]["ArgumentsJson"] = json.dumps(args)
            workflow_stages(receipt, events, requests, setup, home)
            original = copy.deepcopy(events)
            args = json.loads(events[2]["output"]["ArgumentsJson"])
            args["Agent"] = "headless-analyst"
            events[2]["output"]["ArgumentsJson"] = json.dumps(args)
            with self.assertRaisesRegex(AssertionError, "distinct assignment"):
                workflow_stages(receipt, events, requests, setup, home)
            events = copy.deepcopy(original)
            duplicate = copy.deepcopy(events[2:])
            for event in duplicate:
                event["output"]["CallId"] = "duplicate-worker"
            events.extend(duplicate)
            for sequence, event in enumerate(events, 1):
                event["sequence"] = sequence
                event["observed_ns"] = sequence
            with self.assertRaisesRegex(AssertionError, "plan_path"):
                workflow_stages(receipt, events, requests, setup, home)

    def test_coordination_assertion_retains_each_python_error_despite_outer_stderr_discard(self):
        function = shell_functions("assert_coordination_analyze_plan")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for failed_stage in ("contract", "artifact"):
                evidence = root / failed_stage
                evidence.mkdir()
                script = function + r'''
python3() {
    if [[ "$2" == contract ]]; then
        echo "contract diagnostic" >&2
        if [[ "$FAILED_STAGE" == contract ]]; then return 7; fi
        echo '{}'
    else
        echo "artifact diagnostic" >&2
        return 8
    fi
}
assert_coordination_analyze_plan 2>/dev/null
'''
                env = {**os.environ, "CHILD_LAST_EVIDENCE": str(evidence), "FAILED_STAGE": failed_stage,
                       "REPO_ROOT": str(ROOT), "EVAL_ASSET_ROOT": str(ROOT), "EVAL_HOME": str(root),
                       "COORDINATION_CASE_EVIDENCE": str(root), "TMPDIR_EVAL": str(root),
                       "case_name": "coordination_analyze_plan"}
                result = subprocess.run(["bash", "-eu", "-c", script], env=env, capture_output=True, text=True)
                with self.subTest(stage=failed_stage):
                    self.assertEqual(1 if failed_stage == "contract" else 8, result.returncode)
                    expected = "contract diagnostic\n"
                    if failed_stage == "artifact":
                        expected += "artifact diagnostic\n"
                    diagnostic = evidence / "coordination-assertion.stderr"
                    self.assertTrue(diagnostic.is_file(), "The case loses its assertion diagnostic.")
                    self.assertEqual(expected, diagnostic.read_text())
                    self.assertEqual("", result.stderr)

    def test_source_edit_restore_and_unrelated_actions_fail_despite_unchanged_final_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = stage_evidence(Path(directory))
            workflow_stages(*evidence)
            for actor, tool in (("child", "file_edit"), ("child", "shell_execute"),
                                ("parent", "file_edit"), ("parent", "file_write"),
                                ("parent", "shell_execute")):
                receipt, events, requests, setup, home = copy.deepcopy(evidence)
                source = actual_file(home, setup["source_root"] + "/source/catalog.py")
                before = source.read_bytes()
                if actor == "child":
                    requests[0]["messages"].insert(-2, {"role": "assistant", "tool_calls": [{
                        "id": "forbidden", "function": {"name": tool, "arguments": json.dumps({
                            "Path": setup["source_root"] + "/source/catalog.py", "Content": "temporary source change"})}}]})
                else:
                    for dto in ({"Type": "tool_call", "CallId": "forbidden", "ToolName": tool,
                                 "ArgumentsJson": json.dumps({"Path": setup["source_root"] + "/source/catalog.py"})},
                                {"Type": "tool_result", "CallId": "forbidden", "ToolName": tool, "Result": "source restored"}):
                        index = len(events) + 1
                        events.append({"sequence": index, "observed_ns": index,
                                       "output": {**dto, "SessionId": receipt["session_id"]}})
                self.assertEqual(before, source.read_bytes())
                with self.subTest(actor=actor, tool=tool), self.assertRaises(AssertionError):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_actual_volatile_user_nudge_supports_profile_discovery_without_assistant_claims(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            discovery = requests[-1]["messages"][0]
            workflow_stages(receipt, events, requests, setup, home)
            original = discovery["content"]
            for role in ("assistant", "tool", "user"):
                discovery.update(role=role, content=original[9:-1])
                with self.subTest(role=role), self.assertRaises(AssertionError):
                    workflow_stages(receipt, events, requests, setup, home)

    def test_completed_start_id_reuse_preserves_distinct_ordered_occurrences(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            for event in events[2:]:
                event["output"]["CallId"] = events[0]["output"]["CallId"]
            workflow_stages(receipt, events, requests, setup, home)
            events[1], events[2] = events[2], events[1]
            for sequence, event in enumerate(events, 1):
                event["sequence"] = sequence
                event["observed_ns"] = sequence
            with self.assertRaises(ValueError):
                workflow_stages(receipt, events, requests, setup, home)

    def test_child_write_rejects_unresolved_overlap_but_preserves_completed_reuse_and_cumulative_history(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            original = copy.deepcopy(requests[0])
            requests.insert(1, copy.deepcopy(original))
            workflow_stages(receipt, events, requests, setup, home)
            requests[0]["messages"].extend(copy.deepcopy(original["messages"][-2:]))
            workflow_stages(receipt, events, requests, setup, home)
            requests[0] = copy.deepcopy(original)
            requests[0]["messages"].insert(-1, copy.deepcopy(original["messages"][-2]))
            with self.assertRaises(AssertionError):
                workflow_stages(receipt, events, requests, setup, home)

    def test_profile_names_preserve_actual_case_insensitive_lookup(self):
        with tempfile.TemporaryDirectory() as directory:
            receipt, events, requests, setup, home = stage_evidence(Path(directory))
            for event in (events[0], events[2]):
                arguments = json.loads(event["output"]["ArgumentsJson"])
                arguments["Agent"] = arguments["Agent"].upper()
                event["output"]["ArgumentsJson"] = json.dumps(arguments)
            workflow_stages(receipt, events, requests, setup, home)

    def test_current_attempt_contract_rejects_stale_receipts_and_changed_source(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            receipt, events, requests, setup, home = stage_evidence(root)
            observer, relay = root / "observer", root / "relay"
            observer.mkdir(); relay.mkdir()
            data = {"Nonce": "nonce-current", "Mode": "collect", "InitialPrompt": prompt(setup), "SessionId": "session-neutral"}
            receipt.update(status="observed", prompt_nonce=data["Nonce"], observer_mode="collect",
                initial_prompt_sha256=hashlib.sha256(data["InitialPrompt"].encode()).hexdigest(),
                last_reply="Actual final reply", case=CASES[0], prompt_ordinal=1,
                delivery_observations={"complete": True})
            (observer / "observer-input.json").write_text(json.dumps(data))
            (observer / "session-output.jsonl").write_text("\n".join(json.dumps(row) for row in events))
            for index, request in enumerate(requests):
                (relay / f"request-{index:04}.json").write_text(json.dumps(request))
            for changes in ({"prompt_nonce": "stale"}, {"case": CASES[1]}, {"prompt_ordinal": 2},
                            {"observer_mode": "turn"}, {"delivery_observations": {"complete": False}}):
                (observer / "verified-receipt.json").write_text(json.dumps({**receipt, **changes}))
                with self.subTest(changes=changes), self.assertRaises(AssertionError):
                    contract(CASES[0], FIXTURE, home, root / "setup", observer, relay)
            (observer / "verified-receipt.json").write_text(json.dumps(receipt))
            result = contract(CASES[0], FIXTURE, home, root / "setup", observer, relay)
            self.assertEqual("delivered", result["delivery"])
            self.assertEqual(actual_file(home, setup["plan_path"]).read_bytes(),
                             (observer / "coordination-artifacts/plan.md").read_bytes())
            actual_file(home, setup["source_root"] + "/source/catalog.py").write_text("changed")
            with self.assertRaises(AssertionError):
                contract(CASES[0], FIXTURE, home, root / "setup", observer, relay)

    def test_two_stage_consumption_uses_cumulative_records_without_final_history_requirement(self):
        second = {**ACCEPTED, "run_id": "run-plan", "scope_id": "scope-plan"}
        body = {**terminal(), "run_id": second["run_id"], "scope_id": second["scope_id"]}
        request = parent(body, "delivery-plan")
        request["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps(
            {"run_id": second["run_id"], "source_operation": "spawn_agent"})
        starts = [{"accepted": value, "call_id": "start-" + value["run_id"], "source_operation": "spawn_agent"}
                  for value in (ACCEPTED, second)]
        records = [{"request": value, "request_id": index, "admitted_ns": index * 3,
                    "response_first_payload_ns": index * 3 + 1, "response_payload_written": True}
                   for index, value in enumerate((parent(), request), 1)]
        self.assertFalse(consumed_deliveries([], starts[:1], 10, [])["complete"])
        self.assertTrue(consumed_deliveries(records[:1], starts[:1], 10, [])["complete"])
        self.assertFalse(consumed_deliveries(records[:1], starts, 10, [])["complete"])
        result = consumed_deliveries(records, starts, 10, [])
        self.assertTrue(result["complete"])
        self.assertEqual([1, 2], [row["request_id"] for row in result["deliveries"]])
        third = {**ACCEPTED, "run_id": "run-extra", "scope_id": "scope-extra"}
        self.assertFalse(consumed_deliveries(records, starts + [{**starts[0], "accepted": third}], 10, [])["complete"])


if __name__ == "__main__":
    unittest.main()
