"""Controls for the case adapter and cumulative child consumption."""

import copy
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile
import unittest

from child_run_evals import actual_file, consumed_deliveries, legacy_observer_mode
from coordination_workflow_evals import blocked_config, contract, prepare, prompt, workflow_stages
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
