"""Adverse controls for actual analyst evidence and unavailable checks."""

import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from child_run_evals import REQUIRED_RATIONALE_ERROR, actual_file, legacy_observer_mode
from coordination_conflicting_evidence_evals import CASE, command, main, prepare, prompt, run_check, sha, verify
from test_child_run_evals import ACCEPTED, PATHS, child, parent, terminal
from test_coordination_workflow_evals import observer_calls, shell_functions


def pair(request, events, session, name, arguments, result, identifier, failure=None):
    request["messages"].extend([
        {"role": "assistant", "tool_calls": [{"id": identifier, "function": {"name": name, "arguments": json.dumps(arguments)}}]},
        {"role": "tool", "tool_call_id": identifier, "content": result}])
    if events is not None:
        for value in ({"Type": "tool_call", "CallId": identifier, "ToolName": name, "ArgumentsJson": json.dumps(arguments)},
                      {"Type": "tool_result", "CallId": identifier, "ToolName": name, "Result": result, "ToolFailureCode": failure}):
            n = len(events) + 1
            events.append({"sequence": n, "observed_ns": n, "output": {**value, "SessionId": session}})


def evidence(root, resolved=False):
    home = root / "home"
    setup = prepare(home, root / "evidence", resolved)
    receipt = {"session_id": "session-neutral", "accepted_runs": [], "verified_deliveries": [], "last_reply": ""}
    p = {"messages": [{"role": "system", "content": "[available-subagents]\n## code-analyst\nRead source evidence"},
                      {"role": "user", "content": prompt(setup)}]}
    events, children, reports = [], [], []
    for n, (key, row) in enumerate(setup["reports"].items(), 1):
        accepted = {**ACCEPTED, "run_id": "run-" + str(n), "scope_id": "scope-" + str(n)}
        paths = {k: v.replace("/subagents/neutral", "/subagents/run-" + str(n)) for k, v in PATHS.items()}
        assignment = "\n".join([setup["nonce"], setup["source_revision"], setup["root"], setup["manifest"], row["input"], row["output"]])
        pair(p, events, receipt["session_id"], "spawn_agent", {"Agent": "code-analyst", "Task": assignment}, json.dumps(accepted), "start-" + key)
        c = child()
        c["messages"][1]["content"] = "Context:\n[session]\n" + "\n".join(k + ": " + v for k, v in paths.items()) + "\nTask:\n" + assignment
        for name, path in (("input", row["input"]), ("manifest", setup["manifest"])):
            pair(c, None, None, "file_read", {"Path": path}, actual_file(home, path).read_bytes().decode(), name + "-" + key)
        original = json.loads(actual_file(home, row["input"]).read_bytes())
        report = {"nonce": setup["nonce"], "source_revision": setup["source_revision"], "evidence_id": key,
                  "source_path": row["input"], "measurement": "active_record_count", "claimed_count": original["claimed_count"],
                  "verification": "supplied_claim_only"}
        content = json.dumps(report, sort_keys=True) + "\n"
        actual_file(home, row["output"]).write_text(content)
        pair(c, None, None, "file_write", {"Path": row["output"], "Content": content},
             f"Successfully wrote {len(content.encode())} bytes to {row['output']}", "write-" + key)
        t = {**terminal(), "run_id": accepted["run_id"], "scope_id": accepted["scope_id"], "log_path": paths["log_path"],
             "artifact_directory": paths["artifact_dir"], "output": "The evidence report is at " + row["output"]}
        receipt["accepted_runs"].append(accepted)
        receipt["verified_deliveries"].append({"accepted": accepted, "terminal": t})
        consumed = parent(t, "delivery-" + key)
        consumed["messages"][0]["tool_calls"][0]["function"]["arguments"] = json.dumps({"run_id": accepted["run_id"], "source_operation": "spawn_agent"})
        p["messages"].extend(consumed["messages"])
        children.append(c)
        reports.append({"path": row["output"], "run_id": accepted["run_id"], "evidence_id": key, "claimed_count": original["claimed_count"]})
    for n, path in enumerate([setup["manifest"], *[v for row in setup["reports"].values() for v in row.values()]]):
        pair(p, events, receipt["session_id"], "file_read", {"Path": path}, actual_file(home, path).read_bytes().decode(), "review-" + str(n))
    checked = run_check(home, setup)
    pair(p, events, receipt["session_id"], "shell_execute", {"Command": command(setup)},
         "Exit code: " + str(checked.returncode) + "\n" + checked.stdout, "check-actual")
    claims = [{"evidence_id": r["evidence_id"], "value": r["claimed_count"]} for r in reports]
    conflicts = [] if resolved else [{"measurement": "active_record_count", "claims": claims}]
    receipt["last_reply"] = json.dumps({"nonce": setup["nonce"], "source_revision": setup["source_revision"], "reports": reports,
        "conflicts": conflicts, "verification": json.loads(checked.stdout), "conclusion": "resolved_verified" if resolved else "unresolved_evidence", "repair": "not_performed"})
    receipt["calls"] = observer_calls(events)
    return setup, receipt, events, [p, *children], home


class ConflictingEvidenceControls(unittest.TestCase):

    def change_spawn_fields(self, args, change):
        identifiers = set()
        for event in args[2]:
            dto = event["output"]
            if dto["Type"] == "tool_call" and dto.get("ToolName") == "spawn_agent":
                identifiers.add(dto["CallId"])
                dto["ArgumentsJson"] = json.dumps(change(json.loads(dto["ArgumentsJson"])))
        for request in args[3]:
            for message in request["messages"]:
                for call in message.get("tool_calls", []):
                    function = call["function"]
                    if call["id"] in identifiers:
                        function["arguments"] = json.dumps(change(json.loads(function["arguments"])))
        args[1]["calls"] = observer_calls(args[2])

    def spawn_fields(self, spellings):
        args = copy.deepcopy(self.args)
        def change(value):
            original = value.pop("Task")
            task = original.splitlines()[-1]
            context = original.replace(task, "")
            value[spellings[0]] = value.pop("Agent")
            value[spellings[1]] = task
            value[spellings[2]] = context
            return value
        self.change_spawn_fields(args, change)
        return args

    def test_spawn_aliases_preserve_complete_verification_and_raw_evidence(self):
        for spellings in (("agent", "task", "context"), ("aGeNt", "tAsK", "cOnTeXt"),
                          ("a-g_e.n t", "t-a_s.k", "c-o_n.t e x t"), ("Agent", "task", "Context")):
            args = self.spawn_fields(spellings)
            before = copy.deepcopy(args[1:4])
            with self.subTest(spellings=spellings):
                self.assertTrue(verify(*args)["passed"])
                self.assertEqual(before, args[1:4])

    def test_spawn_alias_precedence_preserves_canonical_and_first_matches(self):
        args = self.spawn_fields(("Agent", "Task", "Context"))
        self.change_spawn_fields(args, lambda v: {"Tas\u212a": "wrong", **v, "agent": "wrong", "task": "wrong", "context": "wrong"})
        self.assertTrue(verify(*args)["passed"])
        args = self.spawn_fields(("aGeNt", "tAsK", "cOnTeXt"))
        self.change_spawn_fields(args, lambda v: {"a_gent": "wrong", "t_ask": "wrong", "con_text": "wrong", **v})
        self.assertTrue(verify(*args)["passed"])
        args = self.spawn_fields(("a_gent", "t_ask", "con_text"))
        self.change_spawn_fields(args, lambda v: {**v, "a-gent": "wrong", "t-ask": "wrong", "con-text": "wrong"})
        self.assertTrue(verify(*args)["passed"])
        for field in ("Agent", "Task", "Context"):
            args = self.spawn_fields(("agent", "task", "context"))
            self.change_spawn_fields(args, lambda v: {**v, field: "wrong"})
            with self.subTest(canonical_field=field), self.assertRaises(AssertionError):
                verify(*args)
        for field in ("agent", "task", "context"):
            args = self.spawn_fields(("aGeNt", "tAsK", "cOnTeXt"))
            self.change_spawn_fields(args, lambda v: {field: "wrong", **v})
            with self.subTest(first_case_match=field), self.assertRaises(AssertionError):
                verify(*args)
        for field in ("a-gent", "t-ask", "con-text"):
            args = self.spawn_fields(("a_gent", "t_ask", "con_text"))
            self.change_spawn_fields(args, lambda v: {field: "wrong", **v})
            with self.subTest(first_normalized_match=field), self.assertRaises(AssertionError):
                verify(*args)

    def test_spawn_unicode_aliases_cannot_replace_required_scope(self):
        valid = self.spawn_fields(("Agent", "Task", "Context"))
        self.assertTrue(verify(*valid)["passed"])
        for alias in ("Tas\u212a", "Ta\u017fk"):
            args = copy.deepcopy(valid)
            def rename(value):
                value[alias] = value.pop("Task")
                return value
            self.change_spawn_fields(args, rename)
            with self.subTest(alias=alias), self.assertRaises(AssertionError):
                verify(*args)
            args = copy.deepcopy(valid)
            self.change_spawn_fields(args, lambda v: {alias: "wrong", **v})
            with self.subTest(canonical_precedence=alias):
                self.assertTrue(verify(*args)["passed"])

    def test_lowercase_spawn_keeps_the_terminal_review_barrier(self):
        args = self.spawn_fields(("agent", "task", "context"))
        self.assertTrue(verify(*args)["passed"])
        messages = args[3][0]["messages"]
        result = next(message for message in messages
                      if message.get("tool_call_id", "").startswith(("delivery-", "terminal")))
        messages.remove(result)
        messages.append(result)
        with self.assertRaises(AssertionError):
            verify(*args)

    def test_lowercase_spawn_values_do_not_replace_required_profile_or_scope(self):
        valid = self.spawn_fields(("agent", "task", "context"))
        self.assertTrue(verify(*valid)["passed"])
        for field in ("agent", "task", "context"):
            args = copy.deepcopy(valid)
            self.change_spawn_fields(args, lambda v: {**v, field: "wrong"})
            with self.subTest(field=field), self.assertRaises(AssertionError):
                verify(*args)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.args = evidence(Path(self.temp.name))

    def reject(self, args=None):
        with self.assertRaises((AssertionError, ValueError)):
            verify(*(args or self.args))

    def test_two_real_analyst_reports_preserve_conflict_and_actual_unavailable_check(self):
        result = verify(*self.args)
        self.assertTrue(result["passed"])
        self.assertEqual([7, 9], [c["value"] for c in result["conflicts"][0]["claims"]])
        self.assertEqual("unavailable", result["verification"]["status"])
        self.assertEqual(self.args[0]["dependency"], result["verification"]["missing_dependency"])
        self.assertEqual("unresolved_evidence", result["conclusion"])
        checked = run_check(self.args[4], self.args[0])
        self.assertEqual(2, checked.returncode)
        self.assertFalse(actual_file(self.args[4], self.args[0]["dependency"]).exists())

    def test_matching_claims_and_actual_raw_records_require_resolved_verified_result(self):
        args = evidence(Path(self.temp.name) / "resolved", True)
        result = verify(*args)
        self.assertEqual("resolved_verified", result["conclusion"])
        self.assertEqual(7, result["verification"]["active_record_count"])
        self.assertEqual(0, run_check(args[4], args[0]).returncode)
        answer = json.loads(args[1]["last_reply"])
        answer["conclusion"] = "unresolved_evidence"
        args[1]["last_reply"] = json.dumps(answer)
        self.reject(args)

    def test_matching_claims_without_raw_dependency_do_not_establish_truth(self):
        args = evidence(Path(self.temp.name) / "agreement-unverified", True)
        setup, receipt, events, requests, home = args
        actual_file(home, setup["dependency"]).unlink()
        del setup["input_hashes"][setup["dependency"]]
        setup["dependency_present"] = False
        checked = run_check(home, setup)
        output = "Exit code: 2\n" + checked.stdout
        for event in events:
            if event["output"].get("CallId") == "check-actual" and event["output"]["Type"] == "tool_result":
                event["output"]["Result"] = output
        for message in requests[0]["messages"]:
            if message.get("tool_call_id") == "check-actual": message["content"] = output
        answer = json.loads(receipt["last_reply"])
        answer.update(verification=json.loads(checked.stdout), conclusion="unresolved_evidence")
        receipt["last_reply"] = json.dumps(answer)
        self.assertEqual("unresolved_evidence", verify(*args)["conclusion"])
        answer["conclusion"] = "resolved_verified"
        receipt["last_reply"] = json.dumps(answer)
        self.reject(args)

    def test_unresolved_provider_call_overlap_cannot_borrow_one_check_result(self):
        args = copy.deepcopy(self.args)
        messages = args[3][0]["messages"]
        n = next(i for i, m in enumerate(messages) if m.get("tool_call_id") == "check-actual")
        messages.insert(n, copy.deepcopy(messages[n - 1]))
        self.reject(args)

    def test_majority_truth_repair_and_environment_defect_claims_fail(self):
        for key, value in (("conflicts", []), ("conclusion", "resolved_verified"), ("conclusion", "product_defect"),
                           ("conclusion", "majority_accepted"), ("repair", "completed")):
            args = copy.deepcopy(self.args)
            answer = json.loads(args[1]["last_reply"]);answer[key] = value
            args[1]["last_reply"] = json.dumps(answer)
            with self.subTest(key=key, value=value): self.reject(args)
        args = copy.deepcopy(self.args)
        answer = json.loads(args[1]["last_reply"]);answer["verification"]["missing_dependency"] = None
        args[1]["last_reply"] = json.dumps(answer);self.reject(args)

    def test_both_distinct_accepted_analyst_terminals_are_required(self):
        for mutation in ("one", "duplicate", "foreign", "failed", "missing"):
            args = copy.deepcopy(self.args)
            if mutation == "one": args[1]["accepted_runs"].pop()
            elif mutation == "duplicate": args[1]["accepted_runs"][1] = args[1]["accepted_runs"][0]
            elif mutation == "foreign": args[1]["verified_deliveries"][1]["accepted"]["run_id"] = "foreign"
            elif mutation == "failed": args[1]["verified_deliveries"][1]["terminal"]["outcome"] = "Failed"
            else: args[1]["verified_deliveries"].pop()
            with self.subTest(mutation=mutation): self.reject(args)

    def test_accepted_receipt_cannot_borrow_run_id_with_foreign_canonical_fields(self):
        for field, value in (("scope_id", "foreign-owner"), ("state", "Running"), ("control_tool", "foreign-control")):
            args = copy.deepcopy(self.args)
            args[1]["accepted_runs"][0] = {**args[1]["accepted_runs"][0], field: value}
            self.assertEqual(self.args[1]["verified_deliveries"][0]["accepted"], args[1]["verified_deliveries"][0]["accepted"])
            with self.subTest(field=field): self.reject(args)

    def test_acceptance_and_terminal_receipt_order_does_not_change_canonical_identity(self):
        args = copy.deepcopy(self.args)
        args[1]["accepted_runs"].reverse()
        args[1]["verified_deliveries"].reverse()
        self.assertTrue(verify(*args)["passed"])

    def test_wrong_profile_or_unrelated_child_context_cannot_produce_a_report(self):
        for mutation in ("profile", "context", "assignment"):
            args = copy.deepcopy(self.args)
            if mutation == "profile":
                for event in args[2]:
                    if event["output"].get("CallId") == "start-audit-a" and event["output"]["Type"] == "tool_call":
                        event["output"]["ArgumentsJson"] = event["output"]["ArgumentsJson"].replace("code-analyst", "task-worker")
            elif mutation == "context": args[3][1]["messages"][1]["content"] = args[3][1]["messages"][1]["content"].replace(args[0]["nonce"], "foreign-nonce")
            else:
                for event in args[2]:
                    if event["output"].get("CallId") == "start-audit-a" and event["output"]["Type"] == "tool_call":
                        event["output"]["ArgumentsJson"] = event["output"]["ArgumentsJson"].replace(args[0]["nonce"], "foreign-nonce")
            with self.subTest(mutation=mutation): self.reject(args)

    def test_shared_context_paths_do_not_confuse_two_distinct_report_producers(self):
        args = copy.deepcopy(self.args)
        extra = "\n".join(p for row in args[0]["reports"].values() for p in row.values())
        for request in args[3]:
            for message in request["messages"]:
                for c in message.get("tool_calls", []):
                    if c["function"]["name"] == "spawn_agent" and c["id"].startswith("start-"):
                        data = json.loads(c["function"]["arguments"]);data["Context"] = extra
                        c["function"]["arguments"] = json.dumps(data)
        for event in args[2]:
            if event["output"]["Type"] == "tool_call" and event["output"]["ToolName"] == "spawn_agent":
                data = json.loads(event["output"]["ArgumentsJson"]);data["Context"] = extra
                event["output"]["ArgumentsJson"] = json.dumps(data)
        self.assertTrue(verify(*args)["passed"])

    def test_original_input_manifest_checker_and_dependency_presence_are_immutable(self):
        for path in self.args[0]["input_hashes"]:
            target = actual_file(self.args[4], path);original = target.read_bytes()
            try:
                target.write_bytes(original + b"\n")
                with self.subTest(path=path): self.reject()
            finally: target.write_bytes(original)
        target = actual_file(self.args[4], self.args[0]["dependency"])
        try:
            target.write_text('{}');self.reject()
        finally: target.unlink()

    def test_fabricated_changed_missing_or_unpaired_check_receipts_fail(self):
        for mutation in ("exit", "body", "unpaired", "dto", "foreign", "command", "failed"):
            args = copy.deepcopy(self.args)
            if mutation == "unpaired":
                args[3][0]["messages"][:] = [m for m in args[3][0]["messages"] if m.get("tool_call_id") != "check-actual"]
            for event in args[2]:
                d = event["output"]
                if d.get("CallId") != "check-actual": continue
                if mutation == "foreign": d["CallId"] = "foreign-check"
                if mutation == "exit" and d["Type"] == "tool_result": d["Result"] = d["Result"].replace("Exit code: 2", "Exit code: 0")
                if mutation == "body" and d["Type"] == "tool_result": d["Result"] += "fake"
                if mutation == "failed" and d["Type"] == "tool_result": d["ToolFailureCode"] = "authorization_denied"
                if mutation == "command" and d["Type"] == "tool_call": d["ArgumentsJson"] = json.dumps({"Command": "echo unavailable"})
            if mutation == "dto": args[2][:] = [e for e in args[2] if e["output"].get("CallId") != "check-actual"]
            for n, event in enumerate(args[2], 1): event.update(sequence=n, observed_ns=n)
            with self.subTest(mutation=mutation): self.reject(args)

    def test_mutually_matching_fabricated_check_pairs_cannot_replace_actual_command_result(self):
        args = copy.deepcopy(self.args)
        fake = "Exit code: 2\n" + json.dumps({"status": "unavailable", "missing_dependency": "invented"}) + "\n"
        for event in args[2]:
            if event["output"].get("CallId") == "check-actual" and event["output"]["Type"] == "tool_result":
                event["output"]["Result"] = fake
        for message in args[3][0]["messages"]:
            if message.get("tool_call_id") == "check-actual": message["content"] = fake
        self.reject(args)

    def test_missing_parent_read_or_foreign_parent_dto_id_fails(self):
        for identifier in ("review-0", "review-1", "review-2", "review-3", "review-4"):
            for mutation in ("foreign", "provider", "dto"):
                args = copy.deepcopy(self.args)
                if mutation == "provider": args[3][0]["messages"][:] = [m for m in args[3][0]["messages"] if m.get("tool_call_id") != identifier]
                elif mutation == "dto": args[2][:] = [e for e in args[2] if e["output"].get("CallId") != identifier]
                else:
                    for event in args[2]:
                        if event["output"].get("CallId") == identifier: event["output"]["CallId"] = "foreign-" + identifier
                for n, event in enumerate(args[2], 1): event.update(sequence=n, observed_ns=n)
                with self.subTest(identifier=identifier, mutation=mutation): self.reject(args)

    def test_reads_before_one_terminal_cannot_prove_final_parent_review(self):
        args = copy.deepcopy(self.args)
        messages = args[3][0]["messages"]
        n = next(i for i, m in enumerate(messages) if m.get("tool_call_id") == "delivery-audit-b")
        early = messages[n - 1:n + 1];del messages[n - 1:n + 1]
        messages.extend(early)
        self.reject(args)

    def test_child_full_reads_write_result_and_report_claim_are_required(self):
        for mutation in ("read", "write", "result", "claim", "verification"):
            args = copy.deepcopy(self.args)
            messages = args[3][1]["messages"]
            if mutation in {"read", "write"}:
                identifier = "input-audit-a" if mutation == "read" else "write-audit-a"
                messages[:] = [m for m in messages if m.get("tool_call_id") != identifier]
            elif mutation == "result":
                for m in messages:
                    if m.get("tool_call_id") == "write-audit-a": m["content"] = "File written"
            else:
                target = actual_file(args[4], args[0]["reports"]["audit-a"]["output"])
                original = target.read_bytes();content = json.loads(original)
                content["claimed_count" if mutation == "claim" else "verification"] = 9 if mutation == "claim" else "verified"
                try:
                    target.write_text(json.dumps(content));self.reject(args)
                finally: target.write_bytes(original)
                continue
            with self.subTest(mutation=mutation): self.reject(args)

    def test_child_report_write_requires_prior_actual_source_receipts(self):
        args = copy.deepcopy(self.args)
        messages = args[3][1]["messages"]
        messages[2:] = messages[-2:] + messages[2:-2]
        self.reject(args)

    def test_forbidden_parent_or_child_repairs_commands_and_extra_children_fail(self):
        for actor, name, arguments in ((0, "file_write", {"Path": self.args[0]["manifest"], "Content": "repair"}),
                                       (0, "shell_execute", {"Command": "echo repaired"}),
                                       (1, "file_edit", {"Path": self.args[0]["manifest"]}),
                                       (1, "shell_execute", {"Command": command(self.args[0])}),
                                       (1, "spawn_agent", {"Agent": "code-analyst"}),
                                       (1, "file_write", {"Path": self.args[0]["reports"]["audit-b"]["output"], "Content": "foreign"})):
            args = copy.deepcopy(self.args)
            pair(args[3][actor], args[2] if actor == 0 else None, args[1]["session_id"], name, arguments, "success", "forbidden")
            with self.subTest(actor=actor, name=name): self.reject(args)

    def test_cross_source_child_content_search_and_foreign_metadata_scope_fail(self):
        for actor, name, arguments in (
                (1, "file_search", {"Root": self.args[0]["root"], "Query": "claimed_count", "Mode": "content"}),
                (1, "file_search", {"Root": "/foreign", "Query": "claimed_count", "Mode": "name"}),
                (1, "file_list", {"Path": "/foreign"}),
                (0, "file_search", {"Root": "/foreign", "Query": "claimed_count", "Mode": "content"}),
                (0, "file_list", {"Path": "/foreign"})):
            args = copy.deepcopy(self.args)
            pair(args[3][actor], args[2] if actor == 0 else None, args[1]["session_id"], name, arguments,
                 json.dumps({"path": args[0]["reports"]["audit-b"]["input"], "content": "claimed_count: 9"}), "scope-leak")
            with self.subTest(actor=actor, name=name, arguments=arguments): self.reject(args)

    def test_exact_root_metadata_lists_and_parent_content_search_remain_valid(self):
        args = copy.deepcopy(self.args)
        for actor in (0, 1, 2):
            pair(args[3][actor], args[2] if actor == 0 else None, args[1]["session_id"], "file_list",
                 {"Path": args[0]["root"]}, "audit-a.json\naudit-b.json\nmanifest.json\n", "list-" + str(actor))
        pair(args[3][0], args[2], args[1]["session_id"], "file_search", {"Root": args[0]["root"], "Query": "claimed_count", "Mode": "content"},
             "audit-a.json: claimed_count 7\naudit-b.json: claimed_count 9\n", "parent-search")
        self.assertTrue(verify(*args)["passed"])

    def test_completed_id_reuse_cumulative_captures_and_mixed_case_profiles_stay_valid(self):
        args = copy.deepcopy(self.args)
        for event in args[2]:
            d = event["output"]
            if d.get("CallId") == "review-1": d["CallId"] = "review-0"
            if d["Type"] == "tool_call" and d["ToolName"] == "spawn_agent": d["ArgumentsJson"] = d["ArgumentsJson"].replace("code-analyst", "CODE-ANALYST")
        for request in args[3]:
            for m in request["messages"]:
                if m.get("tool_call_id") == "review-1": m["tool_call_id"] = "review-0"
                for c in m.get("tool_calls", []):
                    if c["id"] == "review-1": c["id"] = "review-0"
                    c["function"]["arguments"] = c["function"]["arguments"].replace('"code-analyst"', '"CODE-ANALYST"')
        args[3].extend(copy.deepcopy(args[3]))
        self.assertTrue(verify(*args)["passed"])

    def test_exact_project_declarations_work_and_wrong_or_unpaired_roots_fail(self):
        for actor in (0, 1):
            args = copy.deepcopy(self.args)
            root = args[0]["root"]
            pair(args[3][actor], args[2] if actor == 0 else None, args[1]["session_id"], "set_working_directory", {"Path": root}, root, "declare")
            self.assertTrue(verify(*args)["passed"])
            for m in args[3][actor]["messages"]:
                if m.get("tool_call_id") == "declare": m["content"] = "Error: denied"
            self.reject(args)
            args = copy.deepcopy(self.args)
            pair(args[3][actor], args[2] if actor == 0 else None, args[1]["session_id"], "set_working_directory", {"Path": root + "/foreign"}, root + "/foreign", "declare")
            self.reject(args)

    def test_exact_unexecuted_rationale_attempt_preserves_two_actual_children(self):
        args = copy.deepcopy(self.args)
        pair(args[3][0], args[2], args[1]["session_id"], "spawn_agent", {"Agent": "code-analyst", "Task": "Unexecuted metadata attempt."},
             REQUIRED_RATIONALE_ERROR, "invalid-start", "invalid_rationale")
        self.assertTrue(verify(*args)["passed"])
        args[2][-1]["output"]["ToolFailureCode"] = "authorization_denied"
        self.reject(args)

    def test_exact_parent_metadata_rejections_retain_actual_success_receipts(self):
        for name, arguments in (("skill_load", {"Name": "agent-coordination"}),
                                ("load_tool", {"Name": "shell_execute"}),
                                ("file_read", {"Path": self.args[0]["manifest"]}),
                                ("shell_execute", {"Command": command(self.args[0])})):
            args = copy.deepcopy(self.args)
            pair(args[3][0], args[2], args[1]["session_id"], name, arguments,
                 REQUIRED_RATIONALE_ERROR, "metadata-attempt", "invalid_rationale")
            with self.subTest(name=name):
                self.assertTrue(verify(*args)["passed"])

    def test_metadata_feedback_cannot_replace_the_actual_check_or_source_read(self):
        for identifier in ("check-actual", "review-0"):
            args = copy.deepcopy(self.args)
            for event in args[2]:
                output = event["output"]
                if output.get("CallId") == identifier and output["Type"] == "tool_result":
                    output.update(Result=REQUIRED_RATIONALE_ERROR, ToolFailureCode="invalid_rationale")
            for message in args[3][0]["messages"]:
                if message.get("tool_call_id") == identifier:
                    message["content"] = REQUIRED_RATIONALE_ERROR
            with self.subTest(identifier=identifier): self.reject(args)

    def test_parent_metadata_rejection_requires_exact_dto_and_provider_attribution(self):
        for mutation in ("code", "text", "provider", "dto", "foreign"):
            args = copy.deepcopy(self.args)
            pair(args[3][0], args[2], args[1]["session_id"], "load_tool", {"Name": "shell_execute"},
                 REQUIRED_RATIONALE_ERROR, "metadata-attempt", "invalid_rationale")
            if mutation == "code": args[2][-1]["output"]["ToolFailureCode"] = "authorization_denied"
            elif mutation == "text": args[2][-1]["output"]["Result"] += " extra"
            elif mutation == "provider": args[3][0]["messages"].pop()
            elif mutation == "dto": args[2][-1]["output"]["ToolFailureCode"] = None
            else: args[2][-1]["output"]["CallId"] = "foreign"
            with self.subTest(mutation=mutation): self.reject(args)

    def test_metadata_rejection_does_not_authorize_foreign_or_forbidden_attempts(self):
        for name, arguments in (("shell_execute", {"Command": "git push"}),
                                ("file_read", {"Path": "/foreign"}),
                                ("file_search", {"Root": "/foreign", "Query": "claim"}),
                                ("file_write", {"Path": self.args[0]["manifest"], "Content": "changed"})):
            args = copy.deepcopy(self.args)
            pair(args[3][0], args[2], args[1]["session_id"], name, arguments,
                 REQUIRED_RATIONALE_ERROR, "metadata-attempt", "invalid_rationale")
            with self.subTest(name=name): self.reject(args)

    def test_prose_or_fenced_json_cannot_replace_the_required_json_reply(self):
        for prefix, suffix in (("The reports disagree.\n", ""), ("```json\n", "\n```")):
            args = copy.deepcopy(self.args)
            args[1]["last_reply"] = prefix + args[1]["last_reply"] + suffix
            self.reject(args)

    def test_checker_cannot_change_preserved_inputs_or_reports_during_verification(self):
        setup, _, _, _, home = self.args
        original_check = run_check
        for target in (setup["manifest"], setup["reports"]["audit-a"]["output"]):
            path = actual_file(home, target);original = path.read_bytes()
            def altered(home, setup):
                result = original_check(home, setup)
                path.write_bytes(original + b"\n")
                return result
            try:
                with patch("coordination_conflicting_evidence_evals.run_check", altered): self.reject()
            finally: path.write_bytes(original)

    def test_actual_checker_timeout_cannot_produce_success(self):
        with patch("coordination_conflicting_evidence_evals.run_check", side_effect=subprocess.TimeoutExpired("check", 1)):
            with self.assertRaises(subprocess.TimeoutExpired): verify(*self.args)

    def test_verify_failure_archives_actual_wrong_report_before_teardown(self):
        setup, receipt, events, requests, home = self.args
        root = Path(self.temp.name);observer = root / "observer";relay = root / "relay"
        observer.mkdir();relay.mkdir()
        data = {"Mode": "collect", "InitialPrompt": prompt(setup), "Nonce": "neutral-observer-nonce", "SessionId": receipt["session_id"]}
        receipt.update(status="observed", observer_mode="collect", case=CASE, prompt_ordinal=1, prompt_nonce=data["Nonce"],
                       initial_prompt_sha256=hashlib.sha256(data["InitialPrompt"].encode()).hexdigest())
        for name, value in (("observer-input.json", data), ("verified-receipt.json", receipt)):
            (observer / name).write_text(json.dumps(value))
        (observer / "session-output.jsonl").write_text("\n".join(json.dumps(e) for e in events))
        for n, request in enumerate(requests, 1): (relay / f"request-{n:04}.json").write_text(json.dumps(request))
        wrong = b'{"wrong":"actual bytes"}\n'
        actual_file(home, setup["reports"]["audit-a"]["output"]).write_bytes(wrong)
        with patch("sys.argv", ["eval", "verify", "--eval-home", str(home), "--evidence", str(root / "evidence"),
                                "--observer-directory", str(observer), "--relay-directory", str(relay)]), self.assertRaises(AssertionError):
            main()
        self.assertEqual(wrong, (observer / "audit-a.json").read_bytes())
        self.assertEqual({"state": "captured", "sha256": sha(wrong)}, json.loads((observer / "analyst-report-artifacts.json").read_text())["audit-a"])

    def test_actual_shell_dispatch_sends_the_complete_prepared_prompt(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = shell_functions("setup_coordination_conflicting_evidence", "run_case", "run_all") + r'''
print_category() { :; }; end_category() { :; }; run_multi_turn_case() { :; }
check_daemon_alive() { :; }
pick_variant() { printf '%s' "$1"; }
run_prompt() { printf '%s' "$1" > "$CAPTURE"; printf '%s' "$2" > "$CAPTURE.format"; }
assert_coordination_conflicting_evidence() { return 0; }
store_result() { :; }; store_metrics() { :; }
CATEGORY_SKIPPED=false; CATEGORY_CASES=0; TOTAL_CASES=0
CATEGORY_PASSED=0; PASSED_CASES=0; FAILED_CASES=0
RUNS=1; THRESHOLD=1; FILTER_CATEGORY=""
run_all
'''
            env = {**os.environ, "FILTER_CASE": CASE, "REPO_ROOT": str(Path(__file__).resolve().parents[1]),
                   "EVAL_HOME": str(root / "home"), "TMPDIR_EVAL": str(root / "temporary"),
                   "CAPTURE": str(root / "actual-prompt"), "PYTHONDONTWRITEBYTECODE": "1"}
            result = subprocess.run(["bash", "-e", "-c", script], env=env, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            setup = json.loads((root / "temporary/child-runs/conflict-review-case/setup.json").read_text())
            self.assertEqual(prompt(setup).encode(), (root / "actual-prompt").read_bytes())
            self.assertEqual(b"json", (root / "actual-prompt.format").read_bytes())

    def test_case_uses_collect_only_at_explicit_first_prompt(self):
        self.assertEqual("collect", legacy_observer_mode(CASE, 1))
        with self.assertRaises(AssertionError): legacy_observer_mode(CASE, 2)
        script = shell_functions("child_result_consumer", "run_all") + '''
print_category() { :; }; end_category() { :; }; run_multi_turn_case() { :; }
run_case() { [[ "${1:-}" != --json ]] || shift; [[ "$1" != coordination_conflicting_evidence ]] || printf '%s\\n' "$1"; }
FILTER_CASE="$1"; FILTER_CATEGORY=""; run_all
'''
        for selected, expected in ((CASE, CASE + "\n"), ("", ""), ("coordination_stale_incomplete", "")):
            result = subprocess.run(["bash", "-c", script, "control", selected], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(expected, result.stdout)


if __name__ == "__main__":
    unittest.main()
