"""Adversarial controls for the fixed coordination artifact proof contract.

These controls create actual temporary files and runtime-shaped DTO records.
They prove oracle sensitivity, not an actual model or daemon workflow.
"""

import copy
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest


sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parent.parent
SOURCE = Path(os.environ.get("NETCLAW_COORDINATION_ARTIFACT_SOURCE", ROOT))
MODULE_PATH = SOURCE / "evals/coordination_artifact_evals.py"
FIXTURE_PATH = SOURCE / "evals/fixtures/coordination-artifacts"
SESSION = "eval/coordination-artifact-control"
NONCE = "coordination-artifact-neutral-trial"
RUNTIME_ROOT = "/home/netclaw/.netclaw/sessions/coordination-artifact-control"


class CoordinationArtifactControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not MODULE_PATH.is_file() or not FIXTURE_PATH.is_dir():
            raise RuntimeError(
                "The committed oracle and fixtures are required. Set "
                "NETCLAW_COORDINATION_ARTIFACT_SOURCE to their frozen checkout."
            )
        sys.path.insert(0, str(SOURCE / "evals"))
        spec = importlib.util.spec_from_file_location("coordination_artifact_under_test", MODULE_PATH)
        cls.oracle = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.oracle
        spec.loader.exec_module(cls.oracle)

    def setUp(self):
        self.owned = tempfile.TemporaryDirectory(prefix="coordination-oracle-control-")
        self.addCleanup(self.owned.cleanup)
        self.directory = Path(self.owned.name)
        self.fixture = self.directory / "fixture"
        shutil.copytree(FIXTURE_PATH, self.fixture)
        self.home = self.directory / "home"
        self.findings_runtime = RUNTIME_ROOT + "/artifacts/findings.md"
        self.plan_runtime = RUNTIME_ROOT + "/artifacts/plan.md"
        self.findings = self.local(self.findings_runtime)
        self.plan = self.local(self.plan_runtime)
        self.findings.parent.mkdir(parents=True)
        self.findings.write_bytes((self.fixture / "artifacts/findings-complete.md").read_bytes())
        self.plan.write_bytes((self.fixture / "artifacts/plan-complete.md").read_bytes())
        self.contract = {
            "session_id": SESSION,
            "prompt_nonce": NONCE,
            "findings_path": self.findings_runtime,
            "plan_path": self.plan_runtime,
            "delivery": "delivered",
        }
        self.receipt = {
            "status": "observed",
            "session_id": SESSION,
            "prompt_nonce": NONCE,
            "last_reply": "The reviewed plan uses the current source evidence.",
        }
        self.events = []
        self.pair("file_read", "findings-read", {"Path": self.findings_runtime}, self.findings.read_text())
        self.pair(
            "spawn_agent", "plan-start",
            {"Agent": "task-worker", "Task": "Write a complete plan at " + self.plan_runtime,
             "Context": "Use the reviewed findings at " + self.findings_runtime},
            json.dumps({"run_id": "run-neutral", "scope_id": "scope-neutral",
                        "state": "Accepted", "control_tool": "check_agent_run"}),
        )
        accepted = json.loads(self.outputs("tool_result", "plan-start")[0]["Result"])
        self.receipt["verified_deliveries"] = [{"accepted": accepted, "terminal": {
            "run_id": accepted["run_id"], "scope_id": accepted["scope_id"], "source_operation": "spawn_agent",
            "state": "Completed", "outcome": "Completed"}}]
        self.pair("file_read", "plan-read", {"Path": self.plan_runtime}, self.plan.read_text())
        self.pair("attach_file", "plan-attach", {"Path": self.plan_runtime, "DisplayName": "plan.md"},
                  self.attachment_receipt(self.plan_runtime))
        self.emit("file", FilePath=self.plan_runtime, FileName="plan.md", MimeType="text/markdown")

    def local(self, runtime):
        return self.home / "data" / runtime.removeprefix("/home/netclaw/.netclaw/")

    @staticmethod
    def attachment_receipt(path, copied=False):
        suffix = " (copied into current session)" if copied else ""
        return f"File attached: plan.md (text/markdown) at {path}{suffix}"

    def emit(self, kind, **fields):
        self.events.append({"sequence": len(self.events) + 1, "observed_ns": 1000 + len(self.events),
                            "output": {"Type": kind, "SessionId": SESSION, **fields}})

    def pair(self, name, identifier, arguments, result, failure=None):
        self.emit("tool_call", ToolName=name, CallId=identifier, ArgumentsJson=json.dumps(arguments))
        self.emit("tool_result", ToolName=name, CallId=identifier, Result=result, ToolFailureCode=failure)

    def outputs(self, kind=None, identifier=None):
        return [event["output"] for event in self.events
                if (kind is None or event["output"]["Type"] == kind)
                and (identifier is None or event["output"].get("CallId") == identifier)]

    def resequence(self):
        for index, event in enumerate(self.events):
            event["sequence"] = index + 1
            event["observed_ns"] = 1000 + index

    def verify(self):
        self.resequence()
        return self.oracle.verify(self.contract, self.receipt, self.events, self.home, self.fixture)

    def invoke_cli(self, malformed_events=False):
        self.resequence()
        contract = self.directory / "contract.json"
        receipt = self.directory / "observer-receipt.json"
        events = self.directory / "session-output.jsonl"
        contract.write_text(json.dumps(self.contract))
        receipt.write_text(json.dumps(self.receipt))
        events.write_text("\n".join(json.dumps(event) for event in self.events) + "\n")
        if malformed_events:
            with events.open("a") as stream:
                stream.write("{invalid event\n")
        return subprocess.run(
            [sys.executable, str(MODULE_PATH), "--fixture-root", str(self.fixture),
             "--eval-home", str(self.home), "--contract", str(contract),
             "--receipt", str(receipt), "--events", str(events)],
            capture_output=True, text=True,
            env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"},
        )

    def assert_valid(self):
        report = self.verify()
        self.assertIs(report["passed"], True, report)
        self.assertEqual(report["errors"], [], report)
        return report

    def assert_rejected(self):
        report = self.verify()
        self.assertIs(report["passed"], False, report)
        self.assertTrue(report["errors"], report)
        return report

    def refresh_reads(self):
        for output in self.outputs("tool_result"):
            if output["CallId"] == "findings-read":
                output["Result"] = self.findings.read_text()
            elif output["CallId"] == "plan-read":
                output["Result"] = self.plan.read_text()

    def make_attachment_copy(self):
        copied_runtime = RUNTIME_ROOT + "/attachments/plan.md"
        self.local(copied_runtime).parent.mkdir(parents=True)
        self.local(copied_runtime).write_bytes(self.plan.read_bytes())
        self.outputs("tool_result", "plan-attach")[0]["Result"] = self.attachment_receipt(copied_runtime, True)
        self.outputs("file")[0]["FilePath"] = copied_runtime
        return copied_runtime

    def block_attachment(self, missing_file=False):
        result = self.outputs("tool_result", "plan-attach")[0]
        result["ToolFailureCode"] = None if missing_file else "access_denied"
        result["Result"] = ("Error: File not found: " + self.plan_runtime if missing_file
                            else "Error: Attachment denied by the current path policy.")
        self.events = [event for event in self.events if event["output"]["Type"] != "file"]

    def change_artifact(self, artifact, transform):
        original = artifact.read_text()
        changed = transform(original)
        self.assertNotEqual(original, changed, "The counterexample must change its target.")
        artifact.write_text(changed)
        self.refresh_reads()

    def test_complete_artifacts_and_ordered_delivery_pass(self):
        self.assert_valid()
        report = self.oracle.verify_artifacts(self.fixture, self.findings, self.plan)
        self.assertIs(report["passed"], True, report)

    def test_shared_runtime_context_preserves_distinct_analyst_and_worker_assignments(self):
        self.assert_valid()
        worker = self.outputs("tool_call", "plan-start")[0]
        args = json.loads(worker["ArgumentsJson"])
        args["Agent"] = "TASK-WORKER"
        args["Context"] = "Runtime artifact paths:\n" + self.findings_runtime + "\n" + self.plan_runtime
        worker["ArgumentsJson"] = json.dumps(args)
        self.pair("spawn_agent", "analysis-start", {
            "Agent": "headless-analyst", "Task": "Write findings at " + self.findings_runtime,
            "Context": args["Context"]}, json.dumps({
                "run_id": "run-analysis", "scope_id": "scope-analysis",
                "state": "Accepted", "control_tool": "check_agent_run"}))
        self.events = self.events[-2:] + self.events[:-2]
        self.assert_valid()

    def test_wrong_plan_profile_cannot_supply_the_handoff(self):
        self.assert_valid()
        worker = self.outputs("tool_call", "plan-start")[0]
        args = json.loads(worker["ArgumentsJson"])
        args["Agent"] = "headless-analyst"
        worker["ArgumentsJson"] = json.dumps(args)
        rejected = self.assert_rejected()
        self.assertIn("one actual plan assignment", rejected["errors"][0])

    def test_two_worker_assignments_cannot_supply_one_plan_handoff(self):
        self.assert_valid()
        duplicate = copy.deepcopy(self.events[2:4])
        for event in duplicate:
            event["output"]["CallId"] = "second-worker"
        accepted = json.loads(duplicate[1]["output"]["Result"])
        accepted.update(run_id="run-second-worker", scope_id="scope-second-worker")
        duplicate[1]["output"]["Result"] = json.dumps(accepted)
        self.events[4:4] = duplicate
        rejected = self.assert_rejected()
        self.assertIn("one actual plan assignment", rejected["errors"][0])

    def add_failed_plan_attempt(self):
        failed_pair = copy.deepcopy(self.events[2:4])
        for event in failed_pair:
            event["output"]["CallId"] = "failed-plan-start"
        accepted = json.loads(failed_pair[1]["output"]["Result"])
        accepted.update(run_id="failed-plan-run", scope_id="failed-plan-scope")
        failed_pair[1]["output"]["Result"] = json.dumps(accepted)
        self.events[2:2] = failed_pair
        self.receipt["verified_deliveries"].insert(0, {"accepted": accepted, "terminal": {
            "run_id": accepted["run_id"], "scope_id": accepted["scope_id"], "source_operation": "spawn_agent",
            "state": "Failed", "outcome": "Failed", "reason": "no_activity_timeout"}})

    def test_failed_plan_attempt_preserves_completed_worker_and_full_review(self):
        self.assert_valid()
        self.add_failed_plan_attempt()
        self.assert_valid()
        for fault in ("full-read", "foreign-owner", "extra-completed"):
            events, receipt = copy.deepcopy(self.events), copy.deepcopy(self.receipt)
            if fault == "full-read":
                self.outputs("tool_result", "plan-read")[0]["Result"] = "A summary instead of the full plan."
            elif fault == "foreign-owner":
                self.receipt["verified_deliveries"][0]["terminal"]["scope_id"] = "foreign-scope"
            else:
                self.receipt["verified_deliveries"][0]["terminal"].update(state="Completed", outcome="Completed")
            with self.subTest(fault=fault):
                self.assert_rejected()
            self.events, self.receipt = events, receipt

    def test_findings_review_precedes_every_plan_attempt(self):
        self.assert_valid()
        self.add_failed_plan_attempt()
        self.events[:4] = self.events[2:4] + self.events[:2]
        rejected = self.assert_rejected()
        self.assertIn("complete findings read", rejected["errors"][0])

    def test_cli_consumes_raw_jsonl_and_returns_the_success_report(self):
        self.assert_valid()
        completed = self.invoke_cli()
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIs(json.loads(completed.stdout)["passed"], True)

    def test_cli_rejects_a_malformed_jsonl_record(self):
        self.assert_valid()
        positive = self.invoke_cli()
        self.assertEqual(positive.returncode, 0, positive.stderr)
        completed = self.invoke_cli(malformed_events=True)
        self.assertNotEqual(completed.returncode, 0)
        self.assertIs(json.loads(completed.stdout)["passed"], False)

    def test_heading_case_whitespace_and_generic_notation_remain_valid(self):
        self.assert_valid()
        for artifact in [self.findings, self.plan]:
            text = re.sub(r"(?m)^(#{1,6} )(.+)$", lambda match: match[1] + match[2].swapcase(),
                          artifact.read_text())
            text = text.replace("\n\n", "\n\n\n")
            artifact.write_text(text + "\nThe proposed type can use `Catalog[T]` or `<Catalog[T]>`.\n")
        self.refresh_reads()
        self.assert_valid()

    def test_completed_call_identifier_reuse_remains_valid(self):
        self.assert_valid()
        for output in self.outputs():
            if "CallId" in output:
                output["CallId"] = "reused-after-result"
        self.assert_valid()

    def test_authorized_attachment_copy_with_equal_bytes_remains_valid(self):
        self.assert_valid()
        self.make_attachment_copy()
        self.assert_valid()

    def test_handoff_paths_can_use_task_or_context(self):
        self.assert_valid()
        call = self.outputs("tool_call", "plan-start")[0]
        for field in ["Task", "Context"]:
            with self.subTest(field=field):
                call["ArgumentsJson"] = json.dumps({"Agent": "task-worker", field:
                    "Read " + self.findings_runtime + " and write the complete plan at " + self.plan_runtime})
                self.assert_valid()

    def test_findings_result_after_plan_spawn_call_is_missing_proof(self):
        self.assert_valid()
        result = self.events.pop(1)
        self.events.insert(2, result)
        self.assert_rejected()

    def test_unrelated_spawn_cannot_replace_the_plan_handoff(self):
        self.assert_valid()
        self.outputs("tool_call", "plan-start")[0]["ArgumentsJson"] = json.dumps(
            {"Agent": "task-worker", "Task": "Write another artifact", "Context": "Unrelated task"})
        self.assert_rejected()

    def test_full_findings_read_cannot_target_another_actual_file(self):
        self.assert_valid()
        other_runtime = RUNTIME_ROOT + "/artifacts/other-findings.md"
        self.local(other_runtime).write_bytes(self.findings.read_bytes())
        self.outputs("tool_call", "findings-read")[0]["ArgumentsJson"] = json.dumps({"Path": other_runtime})
        self.assert_rejected()

    def test_changed_findings_cannot_reuse_an_earlier_read(self):
        self.assert_valid()
        self.findings.write_text(self.findings.read_text() + "\nA later source note changes the reviewed artifact.\n")
        self.assert_rejected()

    def test_partial_or_spilled_findings_receipt_reports_missing_proof(self):
        self.assert_valid()
        result = self.outputs("tool_result", "findings-read")[0]
        original = result["Result"]
        for incomplete in [original[:80], "[output truncated; use a ranged read]",
                           json.dumps({"receipt": "spill-neutral", "tool": "tool_output_read"})]:
            with self.subTest(receipt=incomplete[:40]):
                result["Result"] = incomplete
                self.assert_rejected()
        result["Result"] = original
        self.assert_valid()

    def test_stale_source_revision_fails_with_current_read_receipts(self):
        self.assert_valid()
        revision = json.loads((self.fixture / "truth.json").read_text())["revision"]
        self.change_artifact(self.findings, lambda text: text.replace(revision, "sha256:" + "0" * 64))
        self.assert_rejected()

    def test_source_file_mutation_invalidates_predefined_truth(self):
        self.assert_valid()
        source = self.fixture / "source/catalog.py"
        source.write_text(source.read_text() + "\n# A different fixture revision.\n")
        self.assert_rejected()

    def test_wrong_evidence_source_path_fails(self):
        self.assert_valid()
        self.change_artifact(self.findings, lambda text: text.replace("source/catalog.py", "source/missing.py"))
        self.assert_rejected()

    def test_wrong_source_ranges_or_quoted_facts_fail(self):
        self.assert_valid()
        original = self.findings.read_text()
        changes = [("source/catalog.py:7-8@", "source/catalog.py:9-10@"),
                   ("`self.records = candidate`", "`self.records = approved`"),
                   ("| Catalog.refresh |", "| Other.refresh |")]
        for before, after in changes:
            with self.subTest(change=after):
                self.findings.write_text(original)
                self.change_artifact(self.findings, lambda text: text.replace(before, after))
                self.assert_rejected()
        self.findings.write_text(original)
        self.refresh_reads()
        self.assert_valid()

    def test_wider_source_citations_preserve_actual_required_statements(self):
        self.assert_valid()
        original = self.findings.read_text()
        for citation in ["source/catalog.py:6-8@", "source/catalog.py:5-8@",
                         "`source/catalog.py:5-8`; "]:
            with self.subTest(citation=citation):
                self.findings.write_text(original.replace("source/catalog.py:7-8@", citation))
                self.refresh_reads()
                self.assert_valid()

    def test_source_citations_require_one_canonical_in_bounds_cover(self):
        self.assert_valid()
        original = self.findings.read_text()
        invalid = ["source/catalog.py:8-8@", "source/catalog.py:5-7@",
                   "source/catalog.py:8-7@", "source/catalog.py:0-8@",
                   "source/catalog.py:5-18@", "other/source/catalog.py:7-8@",
                   "/source/catalog.py:7-8@", "../source/catalog.py:7-8@",
                   "source/catalog.py:7-8 source/catalog.py:5-8@"]
        for citation in invalid:
            with self.subTest(citation=citation):
                self.findings.write_text(original.replace("source/catalog.py:7-8@", citation))
                self.refresh_reads()
                self.assert_rejected()
        self.findings.write_text(original)
        self.refresh_reads()
        self.assert_valid()

    def test_nested_check_receipts_remain_valid(self):
        self.assert_valid()
        original = self.findings.read_text()
        checks = next(line for line in original.splitlines() if line.startswith("- Checks: "))
        for indent in ["  ", "\t"]:
            with self.subTest(indent=repr(indent)):
                nested = "- Checks:\n" + indent + "- " + checks.removeprefix("- Checks: ")
                self.findings.write_text(original.replace(checks, nested))
                self.refresh_reads()
                self.assert_valid()

    def test_empty_or_duplicate_checks_cannot_borrow_sibling_receipts(self):
        self.assert_valid()
        original = self.findings.read_text()
        checks = next(line for line in original.splitlines() if line.startswith("- Checks: "))
        for replacement in ["- Checks:", "- Checks:   \n\n", "- Checks:\n- Source inspection passed.",
                            "- Checks:\n  - ", checks + "\n" + checks]:
            with self.subTest(replacement=replacement):
                self.findings.write_text(original.replace(checks, replacement))
                self.refresh_reads()
                self.assert_rejected()
        self.findings.write_text(original)
        self.refresh_reads()
        self.assert_valid()

    def test_empty_scalar_cannot_borrow_the_source_revision(self):
        self.assert_valid()
        self.change_artifact(self.findings, lambda text: re.sub(
            r"(?m)^- Accepted objective:.*$", "- Accepted objective:", text))
        self.assert_rejected()

    def test_nested_receipt_field_cannot_replace_a_required_sibling(self):
        self.assert_valid()
        original = self.findings.read_text()
        checks = next(line for line in original.splitlines() if line.startswith("- Checks: "))
        nested = "- Checks:\n  - Source inspection passed."
        changed = original.replace(checks, nested).replace("- Confirmed effects:", "  - Confirmed effects:")
        self.findings.write_text(changed)
        self.refresh_reads()
        self.assert_rejected()
        self.findings.write_text(original)
        self.refresh_reads()
        self.assert_valid()

    def test_invented_evidence_identifier_fails(self):
        self.assert_valid()
        self.change_artifact(self.findings, lambda text: re.sub(r"\bE1\b", "E99", text))
        self.assert_rejected()

    def test_plan_reference_to_an_invented_finding_fails(self):
        self.assert_valid()
        self.change_artifact(self.plan, lambda text: re.sub(r"\bF1\b", "F99", text))
        self.assert_rejected()

    def test_duplicate_evidence_rows_fail(self):
        self.assert_valid()
        def duplicate(text):
            row = next(line for line in text.splitlines() if re.match(r"\|\s*E1\s*\|", line))
            return text.replace(row, row + "\n" + row, 1)
        self.change_artifact(self.findings, duplicate)
        self.assert_rejected()

    def test_summary_only_plan_fails_despite_full_read_and_successful_attach(self):
        self.assert_valid()
        self.plan.write_text("# Plan\n\nValidate duplicate identifiers before publication. Preserve record order.\n")
        self.refresh_reads()
        self.assert_rejected()

    def test_required_template_placeholders_fail(self):
        self.assert_valid()
        template = (ROOT / "feeds/skills/.system/files/agent-coordination/assets/plan.md").read_text()
        self.plan.write_text(template)
        self.refresh_reads()
        self.assert_rejected()

    def test_findings_summary_or_unresolved_template_fails(self):
        self.assert_valid()
        original = self.findings.read_text()
        template = (ROOT / "feeds/skills/.system/files/agent-coordination/assets/findings.md").read_text()
        for invalid in ["# Findings\n\nValidate duplicates before publication.\n", template]:
            with self.subTest(artifact=invalid[:35]):
                self.findings.write_text(invalid)
                self.refresh_reads()
                self.assert_rejected()
        self.findings.write_text(original)
        self.refresh_reads()
        self.assert_valid()

    def test_missing_source_specific_action_fails(self):
        self.assert_valid()
        self.change_artifact(self.plan, lambda text: re.sub(r"(?m)^\|\s*A1\s*\|.*\n?", "", text))
        self.assert_rejected()

    def test_action_without_the_required_before_identifier_fails(self):
        self.assert_valid()
        self.change_artifact(self.plan, lambda text: text.replace(
            "validates duplicate identifiers before it publishes", "validates duplicate identifiers after it publishes"))
        self.assert_rejected()

    def test_ambiguous_action_exposes_the_bounded_semantic_limit(self):
        self.assert_valid()
        self.change_artifact(self.plan, lambda text: text.replace(
            "validates duplicate identifiers before it publishes the candidate",
            "validates duplicates before notification, after publication; duplicate inputs use this sequence"))
        report = self.verify()
        print("SEMANTIC_FALSIFIER " + json.dumps(
            {"oracle_passed": report["passed"], "errors": report["errors"], "limits": report["limits"]}))
        if report["passed"]:
            self.assertTrue(report["limits"], "Identifier proof must retain its content-review limit.")
        else:
            self.assertTrue(report["errors"], "A rejected counterexample must identify the proof gap.")

    def test_invented_acceptance_check_fails(self):
        self.assert_valid()
        self.change_artifact(self.plan, lambda text: re.sub(r"\bC2\b", "C99", text))
        self.assert_rejected()

    def test_missing_risk_or_open_decision_proof_fails(self):
        self.assert_valid()
        original = self.plan.read_text()
        for identifier in ["R1", "Q1"]:
            with self.subTest(identifier=identifier):
                self.plan.write_text(original)
                self.change_artifact(self.plan, lambda text: re.sub(
                    r"(?m)^.*\b" + identifier + r"\b.*\n?", "", text))
                self.assert_rejected()
        self.plan.write_text(original)
        self.refresh_reads()
        self.assert_valid()

    def test_plan_read_after_attach_call_is_missing_proof(self):
        self.assert_valid()
        result = self.events.pop(5)
        self.events.insert(6, result)
        self.assert_rejected()

    def test_plan_changes_after_parent_read_fail(self):
        self.assert_valid()
        self.plan.write_text(self.plan.read_text() + "\nA later change needs another parent review.\n")
        self.assert_rejected()

    def test_attach_call_for_another_file_cannot_deliver_the_reviewed_plan(self):
        self.assert_valid()
        self.outputs("tool_call", "plan-attach")[0]["ArgumentsJson"] = json.dumps({"Path": self.findings_runtime})
        self.assert_rejected()

    def test_wrong_file_path_fails_even_when_that_file_exists(self):
        self.assert_valid()
        other_runtime = RUNTIME_ROOT + "/artifacts/other-plan.md"
        self.local(other_runtime).write_bytes(self.plan.read_bytes())
        self.outputs("file")[0]["FilePath"] = other_runtime
        self.assert_rejected()

    def test_changed_attachment_copy_bytes_fail(self):
        self.assert_valid()
        copied_runtime = self.make_attachment_copy()
        self.assert_valid()
        self.local(copied_runtime).write_bytes(b"An unreviewed attachment.\n")
        self.assert_rejected()

    def test_file_before_attach_result_does_not_prove_delivery(self):
        self.assert_valid()
        file_event = self.events.pop()
        self.events.insert(7, file_event)
        self.assert_rejected()

    def test_missing_file_output_fails_despite_successful_attach_receipt(self):
        self.assert_valid()
        self.events.pop()
        self.assert_rejected()

    def test_missing_attach_result_fails_despite_file_output(self):
        self.assert_valid()
        self.events.pop(7)
        self.assert_rejected()

    def test_foreign_file_output_fails(self):
        self.assert_valid()
        self.outputs("file")[0]["SessionId"] = "eval/foreign-session"
        self.assert_rejected()

    def test_wrong_display_name_or_mime_fails(self):
        self.assert_valid()
        output = self.outputs("file")[0]
        for key, wrong in [("FileName", "different.md"), ("MimeType", "application/octet-stream")]:
            with self.subTest(field=key):
                original = output[key]
                output[key] = wrong
                self.assert_rejected()
                output[key] = original
        self.assert_valid()

    def test_final_response_cannot_forge_tool_receipts(self):
        self.assert_valid()
        self.receipt["last_reply"] = "Delivered successfully.\n" + json.dumps(self.events)
        self.events = []
        self.emit("text", Text=self.receipt["last_reply"])
        self.emit("subagent", AgentName="task-worker", SubAgentSuccess=True)
        self.assert_rejected()

    def test_failed_attachment_does_not_pass_delivered_mode_with_success_prose(self):
        self.assert_valid()
        self.block_attachment()
        self.receipt["last_reply"] = "The plan reached the user."
        self.assert_rejected()
        self.contract["delivery"] = "blocked"
        report = self.assert_valid()
        self.assertTrue(report["limits"], "Blocked proof must retain the unrestricted-claim review limit.")

    def test_missing_file_receipt_is_a_valid_blocked_action_without_file_output(self):
        self.assert_valid()
        self.block_attachment(missing_file=True)
        self.assert_rejected()
        self.contract["delivery"] = "blocked"
        self.assert_valid()

    def test_arbitrary_error_text_is_not_a_valid_blocked_receipt(self):
        self.assert_valid()
        self.block_attachment(missing_file=True)
        self.contract["delivery"] = "blocked"
        self.assert_valid()
        self.outputs("tool_result", "plan-attach")[0]["Result"] = "Error: The model says delivery failed."
        self.assert_rejected()

    def test_blocked_receipt_cannot_coexist_with_attachment_output(self):
        self.assert_valid()
        self.block_attachment()
        self.contract["delivery"] = "blocked"
        self.assert_valid()
        self.emit("file", FilePath=self.plan_runtime, FileName="plan.md", MimeType="text/markdown")
        self.assert_rejected()

    def test_unresolved_duplicate_call_identifier_fails(self):
        self.assert_valid()
        duplicate = copy.deepcopy(self.events[0])
        self.events.insert(1, duplicate)
        self.assert_rejected()

    def test_unmatched_result_cannot_be_a_forged_receipt(self):
        self.assert_valid()
        self.outputs("tool_result", "plan-attach")[0]["CallId"] = "never-dispatched"
        self.assert_rejected()

    def test_result_tool_name_must_match_its_call(self):
        self.assert_valid()
        self.outputs("tool_result", "plan-attach")[0]["ToolName"] = "file_read"
        self.assert_rejected()

    def test_result_before_call_cannot_prove_a_parent_read(self):
        self.assert_valid()
        self.events[0], self.events[1] = self.events[1], self.events[0]
        self.assert_rejected()

    def test_repeated_completed_result_is_not_another_valid_occurrence(self):
        self.assert_valid()
        self.events.insert(2, copy.deepcopy(self.events[1]))
        self.assert_rejected()

    def test_stale_nonce_or_observer_session_fails(self):
        self.assert_valid()
        for key, wrong in [("prompt_nonce", "prior-trial"), ("session_id", "eval/another-session")]:
            with self.subTest(field=key):
                original = self.receipt[key]
                self.receipt[key] = wrong
                self.assert_rejected()
                self.receipt[key] = original
        self.assert_valid()

    def test_malformed_tool_arguments_fail_without_an_exception(self):
        self.assert_valid()
        self.outputs("tool_call", "findings-read")[0]["ArgumentsJson"] = "{"
        self.assert_rejected()

    def test_actual_plan_link_cannot_escape_the_eval_mount(self):
        self.assert_valid()
        outside = self.directory / "outside-plan.md"
        outside.write_bytes(self.plan.read_bytes())
        self.plan.unlink()
        self.plan.symlink_to(outside)
        self.assert_rejected()


if __name__ == "__main__":
    unittest.main()
