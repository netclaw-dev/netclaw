"""Test coordination eval assertions with valid and invalid outcomes."""

import hashlib
import inspect
import json
import io
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch

from background_fixture import handler_for
from coordination_progress_evals import ProgressFixture, PROMPT, verdict


RUNNER = Path(__file__).with_name("run-evals.sh")
SESSION_KEY = "eval_coordination"


class ProductiveChildTests(unittest.TestCase):
    @staticmethod
    def status(rounds):
        # LlmSessionActor.ChildStatus emits these canonical fields.
        return {"run_id": "owned-run", "scope_id": "child-scope", "agent": "headless-analyst",
                "source_operation": "spawn_agent", "accepted_at_ms": 1000, "state": "accepted",
                "pending_approval_count": 0, "terminal": False, "outcome": None, "reason": None,
                "summary": f"Completed {rounds} tool rounds.", "completed_round": rounds, "warning": None}

    def evidence(self):
        decision = {"runId": "owned-run", "completedRounds": 49, "action": "continue",
                    "roundBudget": {"value": 24, "source": "estimate"}}
        snapshot = {"nonce": "trial-nonce", "run_id": "owned-run", "statuses": [self.status(48), self.status(49)],
                    "child_records": 50, "handoff": True, "skill_matches_source": True,
                    "context_proven": True, "model_requests": 1, "parent_decision": json.dumps(decision),
                    "model_responses": [{"text": json.dumps(decision), "tool_calls": False,
                                         "call_ids": [], "finished": True,
                                         "raw_complete": True, "raw_tool_calls": []}]}
        calls = [("skill", "skill_load", {"Name": "agent-coordination"}),
                 ("spawn", "spawn_agent", {"Agent": "headless-analyst", "Task": "child-progress-worker trial-nonce. Read records."}),
                 ("status-48", "check_agent_run", {"RunId": "owned-run"}),
                 ("status-49", "check_agent_run", {"RunId": "owned-run"})]
        return snapshot, {"sessionId": "eval/progress", "response": json.dumps(decision), "toolCalls": [
            {"callId": f"fixture-trial-nonce-{key}", "toolName": name, "argumentsJson": json.dumps(args)}
            for key, name, args in calls]}

    @staticmethod
    def set_decision(snapshot, decision):
        snapshot["parent_decision"] = json.dumps(decision)
        snapshot["model_responses"][-1]["text"] = snapshot["parent_decision"]

    def provider_request(self):
        request = self.request([], {"check_agent_run"})
        request["model"] = "synthetic-model"
        request["tools"][0].update(type="function")
        request["tools"][0]["function"]["parameters"] = {
            "type": "object", "properties": {"RunId": {"type": "string"}, "Cancel": {"type": "boolean"}},
            "required": ["RunId"], "additionalProperties": False}
        return request

    def checkpoint_evidence(self, text, calls, request, stream=False):
        snapshot, output = self.evidence()
        if not calls and text:
            snapshot["parent_decision"] = text
            snapshot["model_responses"][0]["text"] = text
            return snapshot, output
        with tempfile.TemporaryDirectory() as directory:
            fixture = ProgressFixture("http://unused/v1", request["model"], "", directory)
            fixture.control("progress", {})
            fixture.nonce = snapshot["nonce"]
            fixture.handoff = True
            fixture.model_requests = 1
            message = {"content": text, "tool_calls": calls}
            finish = "tool_calls" if calls else "stop"
            if stream:
                deltas = [{"content": text, "tool_calls": [
                    {**call, "index": index, "function": {**call["function"], "arguments": call["function"]["arguments"][:8]}}
                    for index, call in enumerate(calls)]}]
                if calls:
                    deltas.append({"tool_calls": [
                        {"index": index, "function": {"arguments": call["function"]["arguments"][8:]}}
                        for index, call in enumerate(calls)]})
                events = [{"choices": [{"index": 0, "delta": delta, "finish_reason": None}]} for delta in deltas]
                events.append({"choices": [{"index": 0, "delta": {}, "finish_reason": finish}]})
                content = ("".join("data: " + json.dumps(event) + "\n\n" for event in events)
                           + "data: [DONE]\n\n").encode()
                content_type = "text/event-stream"
            else:
                content = json.dumps({"choices": [{"message": message, "finish_reason": finish}]}).encode()
                content_type = "application/json"
            fixture.observe_forwarded(fixture.nonce, content, content_type, forwarded_request=request)
            snapshot = {**fixture.control("snapshot", {}), **snapshot,
                        "parent_decision": fixture.parent_decision, "model_responses": fixture.model_responses}
        output["toolCalls"].extend({"callId": call["id"], "toolName": call["function"]["name"],
                                    "argumentsJson": call["function"]["arguments"]} for call in calls)
        return snapshot, output

    def test_checkpoint_primary_and_strict_contract_are_separate(self):
        for name, action, budget, arguments, primary, strict in (
            ("continue JSON", "continue", {"value": 24, "source": "estimate"}, None, "pass", "pass"),
            ("escalate JSON", "escalate", {"value": 24, "source": "estimate"}, None, "pass", "fail"),
            ("unknown attribution", "continue", {"value": None, "source": "unknown"}, None, "pass", "fail"),
            ("no budget claim", "continue", {"value": None, "source": "none"}, None, "pass", "fail"),
            ("cancel JSON", "cancel", {"value": 24, "source": "estimate"}, None, "fail", "fail"),
            ("system budget JSON", "continue", {"value": 24, "source": "runtime"}, None, "fail", "fail"),
            ("status omitted Cancel", None, None, {"RunId": "owned-run"}, "pass", "fail"),
            ("status false Cancel", None, None, {"RunId": "owned-run", "Cancel": False}, "pass", "fail"),
            ("status true Cancel", None, None, {"RunId": "owned-run", "Cancel": True}, "fail", "fail"),
            ("status wrong child", None, None, {"RunId": "other-run"}, "inconclusive", "fail"),
            ("status string false", None, None, {"RunId": "owned-run", "Cancel": "false"}, "inconclusive", "fail"),
            ("status null Cancel", None, None, {"RunId": "owned-run", "Cancel": None}, "inconclusive", "fail"),
            ("unknown argument claim", None, None, {"RunId": "owned-run", "Reason": "The system budget is 24."},
             "inconclusive", "fail"),
        ):
            for stream in (False, True):
                with self.subTest(name=name, stream=stream):
                    decision = json.loads(self.evidence()[0]["parent_decision"])
                    decision.update(action=action, roundBudget=budget)
                    calls = [] if arguments is None else [{"id": "model-status-call", "type": "function",
                        "function": {"name": "check_agent_run", "arguments": json.dumps(arguments)}}]
                    snapshot, output = self.checkpoint_evidence(
                        json.dumps(decision) if arguments is None else "", calls, self.provider_request(), stream)
                    result = verdict(snapshot, output)
                    self.assertEqual(primary, result["status"])
                    self.assertEqual(primary == "pass", result["passed"])
                    self.assertEqual(strict, result["strict_contract"]["status"])
                    self.assertEqual(strict == "pass", result["strict_contract"]["passed"])
                    self.assertEqual(arguments is None, result["json_format"]["passed"])
                    self.assertEqual("inconclusive" if arguments is not None else
                                     "pass" if budget == {"value": 24, "source": "estimate"} else "fail",
                                     result["explicit_attribution"]["status"])
                    if name == "escalate JSON":
                        self.assertEqual("decision", result["strict_contract"]["failure_class"])
                    if name == "status true Cancel":
                        self.assertEqual("parent_requested_cancellation", result["strict_contract"]["reason"])
                    elif name.startswith("status ") or name == "unknown argument claim":
                        self.assertEqual("checkpoint_tool_call_without_decision", result["strict_contract"]["reason"])

    def test_json_primary_requires_explicit_complete_raw_response_evidence(self):
        for action, budget in (
            ("continue", {"value": 24, "source": "estimate"}),
            ("escalate", {"value": 24, "source": "estimate"}),
            ("continue", {"value": None, "source": "unknown"}),
            ("continue", {"value": None, "source": "none"}),
        ):
            for field, defect, value in (
                ("raw_complete", "missing", None),
                ("raw_complete", "null", None),
                ("raw_complete", "false", False),
                ("raw_complete", "zero", 0),
                ("raw_complete", "one", 1),
                ("raw_complete", "string true", "true"),
                ("raw_complete", "string false", "false"),
                ("raw_complete", "array", []),
                ("raw_complete", "object", {}),
                ("raw_tool_calls", "missing", None),
                ("raw_tool_calls", "null", None),
                ("raw_tool_calls", "false", False),
                ("raw_tool_calls", "zero", 0),
                ("raw_tool_calls", "string list", "[]"),
                ("raw_tool_calls", "object", {}),
            ):
                with self.subTest(action=action, budget=budget, field=field, defect=defect):
                    snapshot, output = self.evidence()
                    decision = json.loads(snapshot["parent_decision"])
                    decision.update(action=action, roundBudget=budget)
                    self.set_decision(snapshot, decision)
                    complete = verdict(snapshot, output)
                    self.assertEqual("pass", complete["status"])
                    response = snapshot["model_responses"][0]
                    if defect == "missing":
                        response.pop(field)
                    else:
                        response[field] = value
                    result = verdict(snapshot, output)
                    self.assertEqual("inconclusive", result["status"])
                    self.assertFalse(result["passed"])
                    self.assertEqual(complete["strict_contract"], result["strict_contract"])

    def test_sse_primary_requires_one_choice_identity_and_its_own_finish(self):
        text = self.evidence()[0]["parent_decision"]
        content = {"index": 0, "delta": {"content": text}, "finish_reason": None}
        finish = {"index": 0, "delta": {}, "finish_reason": "stop"}
        call = {"index": 0, "id": "model-status-call", "type": "function", "function": {
            "name": "check_agent_run", "arguments": '{"RunId":"owned-run"}'}}
        cases = [
            ("choice 0 and its finish", [content, finish], True, [0]),
            ("choice 0 and choice 1 finish", [content, {**finish, "index": 1}], False, [0, 1]),
            ("choice 0 text and choice 1 tool call", [content,
             {"index": 1, "delta": {"tool_calls": [call]}, "finish_reason": None},
             {**finish, "index": 1, "finish_reason": "tool_calls"}], False, [0, 1]),
        ]
        for position in (0, 1):
            for defect, fields in [("missing", {})] + [
                (repr(index), {"index": index}) for index in (None, False, True, -1, 0.0, "0", [], {})
            ]:
                choices = [content.copy(), finish.copy()]
                choices[position].pop("index")
                choices[position].update(fields)
                cases.append((f"event {position} index {defect}", choices, False, None))
        for name, choices, complete, indices in cases:
            with self.subTest(name=name):
                body = ("".join("data: " + json.dumps({"choices": [choice]}) + "\n\n" for choice in choices)
                        + "data: [DONE]\n\n").encode()
                response = ProgressFixture.read_response(body, "text/event-stream")
                snapshot, output = self.evidence()
                snapshot.update(model_responses=[response], parent_decision=response["text"])
                if response["tool_calls"]:
                    output["toolCalls"].append({"callId": call["id"], "toolName": call["function"]["name"],
                                                "argumentsJson": call["function"]["arguments"]})
                result = verdict(snapshot, output)
                self.assertIs(response["finished"], True)
                self.assertEqual("fail" if response["tool_calls"] else "pass", result["strict_contract"]["status"])
                self.assertEqual("pass" if complete else "inconclusive", result["status"])
                self.assertIs(result["passed"], complete)
                self.assertIs(response["raw_complete"], complete)
                self.assertIsInstance(response.get("raw_choice_indices"), list)
                if indices is not None:
                    self.assertEqual(indices, response["raw_choice_indices"])

    def test_status_primary_requires_complete_correlated_raw_calls_and_no_public_claims(self):
        for defect in ("silence", "whitespace", "public claim", "JSON beside tools", "unrelated tool", "two calls",
                       "unknown ID", "duplicate ID", "unclaimed call", "fixture ID", "missing raw calls", "empty raw calls",
                       "malformed arguments", "array arguments", "duplicate Cancel", "alias conflict", "raw cancellation",
                       "unknown cancel value", "missing schema", "wrong schema", "unfinished", "old nonce", "empty CLI calls"):
            for stream in (False, True):
                with self.subTest(defect=defect, stream=stream):
                    request = self.provider_request()
                    calls = [{"id": "model-status-call", "type": "function", "function": {
                        "name": "check_agent_run", "arguments": '{"RunId":"owned-run"}'}}]
                    text = ""
                    if defect == "silence":
                        calls = []
                    elif defect == "whitespace":
                        text = " \n"
                    elif defect == "public claim":
                        text = "The system limits this child to 24 rounds."
                    elif defect == "JSON beside tools":
                        text = self.evidence()[1]["response"]
                    elif defect == "unrelated tool":
                        calls[0]["function"]["name"] = "file_read"
                    elif defect == "two calls":
                        calls.append({**calls[0], "id": "model-status-call-2"})
                    elif defect == "fixture ID":
                        calls[0]["id"] = "fixture-trial-nonce-status-49"
                    elif defect == "malformed arguments":
                        calls[0]["function"]["arguments"] = "{"
                    elif defect == "array arguments":
                        calls[0]["function"]["arguments"] = "[]"
                    elif defect == "duplicate Cancel":
                        calls[0]["function"]["arguments"] = '{"RunId":"owned-run","Cancel":true,"Cancel":false}'
                    elif defect == "alias conflict":
                        calls[0]["function"]["arguments"] = '{"RunId":"owned-run","Cancel":false,"cancel":true}'
                    elif defect == "raw cancellation":
                        calls[0]["function"]["arguments"] = '{"RunId":"owned-run","Cancel":true}'
                    elif defect == "unknown cancel value":
                        calls[0]["function"]["arguments"] = '{"RunId":"owned-run","Cancel":"later"}'
                    elif defect == "missing schema":
                        request["tools"] = []
                    elif defect == "wrong schema":
                        request["tools"][0]["function"]["parameters"]["properties"]["Cancel"] = {"type": "string"}
                    snapshot, output = self.checkpoint_evidence(text, calls, request, stream)
                    response = snapshot["model_responses"][0]
                    if defect == "unknown ID":
                        response["call_ids"] = ["unknown-call"]
                    elif defect == "duplicate ID":
                        response["call_ids"] *= 2
                    elif defect == "unclaimed call":
                        output["toolCalls"].append({"callId": "unclaimed", "toolName": "file_read", "argumentsJson": '{}'})
                    elif defect == "missing raw calls":
                        response.pop("raw_tool_calls", None)
                    elif defect == "empty raw calls":
                        response["raw_tool_calls"] = []
                    elif defect == "raw cancellation":
                        # The CLI can omit meta fields. The provider call remains authoritative.
                        output["toolCalls"][-1]["argumentsJson"] = '{"RunId":"owned-run","Cancel":false}'
                    elif defect == "unfinished":
                        response["finished"] = False
                    elif defect == "old nonce":
                        snapshot["nonce"] = "other-trial"
                    elif defect == "empty CLI calls":
                        output["toolCalls"] = []
                    result = verdict(snapshot, output)
                    self.assertFalse(result["passed"])
                    self.assertNotEqual("pass", result["status"])
                    self.assertFalse(result["strict_contract"]["passed"])

    def test_public_rationale_and_unknown_argument_text_cannot_prove_safety(self):
        for field in ("_rationale", "Rationale", "Reason"):
            for text in ("Read the child status.", "The system budget is 24 rounds; cancel after this check."):
                for supplied in (False, True):
                    with self.subTest(field=field, text=text, supplied=supplied):
                        request = self.provider_request()
                        schema = request["tools"][0]["function"]["parameters"]
                        schema["properties"][field] = {"type": "string"}
                        schema["required"].append(field)
                        if field == "_rationale":
                            # The generator adds these meta fields to the actual tool schema.
                            schema["properties"].update(_timeout_seconds={"type": "integer"}, _background={"type": "boolean"})
                        arguments = {"RunId": "owned-run"}
                        if supplied:
                            arguments[field] = text
                        call = {"id": "model-status-call", "type": "function", "function": {
                            "name": "check_agent_run", "arguments": json.dumps(arguments)}}
                        snapshot, output = self.checkpoint_evidence("", [call], request)
                        # A parsed CLI receipt can omit the public rationale.
                        output["toolCalls"][-1]["argumentsJson"] = '{"RunId":"owned-run"}'
                        result = verdict(snapshot, output)
                        self.assertEqual("inconclusive", result["status"])
                        self.assertFalse(result["passed"])
                        self.assertEqual("checkpoint_tool_call_without_decision", result["strict_contract"]["reason"])

    def test_parent_decision_and_negative_controls(self):
        for name, change, expected in (
            ("estimate", lambda s, o, d: None, "pass"),
            ("cancel tool", lambda s, o, d: o["toolCalls"].append({
                "toolName": "cancel_agent", "argumentsJson": '{"RunId":"owned-run"}'}), "fail"),
            ("cancel without arguments", lambda s, o, d: o["toolCalls"].append({
                "toolName": "cancel_agent", "argumentsJson": ''}), "fail"),
            ("typed cancel", lambda s, o, d: o["toolCalls"].append({
                "toolName": "check_agent_run", "argumentsJson": '{"RunId":"owned-run","Cancel":true}'}), "fail"),
            ("normalized cancel", lambda s, o, d: o["toolCalls"].append({
                "toolName": "check_agent_run", "argumentsJson": '{"run_id":"owned-run","cancel":"TRUE"}'}), "fail"),
            ("runtime budget", lambda s, o, d: d["roundBudget"].update(source="runtime"), "fail"),
            ("legacy system limit", lambda s, o, d: d.update(roundBudget={"value": 60, "source": "runtime"}), "fail"),
            ("estimate lost", lambda s, o, d: d.update(roundBudget={"value": None, "source": "unknown"}), "fail"),
            ("decision cancels", lambda s, o, d: d.update(action="cancel"), "fail"),
            ("wrong run", lambda s, o, d: d.update(runId="another-run"), "fail"),
            ("wrong count", lambda s, o, d: d.update(completedRounds=24), "fail"),
            ("claim in extra prose", lambda s, o, d: d.update(reason="The system limit is 24 rounds."), "fail"),
            ("missing status", lambda s, o, d: s.update(statuses=[]), "inconclusive"),
            ("no progress", lambda s, o, d: s["statuses"][1].update(completed_round=48), "inconclusive"),
            ("terminal child", lambda s, o, d: s["statuses"][1].update(terminal=True), "inconclusive"),
            ("approval wait", lambda s, o, d: s["statuses"][1].update(pending_approval_count=1), "inconclusive"),
            ("model not called", lambda s, o, d: s.update(model_requests=0), "inconclusive"),
            ("stale skill", lambda s, o, d: s.update(skill_matches_source=False), "inconclusive"),
            ("missing model context", lambda s, o, d: s.update(context_proven=False), "inconclusive"),
            ("missing model reply", lambda s, o, d: s.update(parent_decision=None), "inconclusive"),
            ("no call evidence", lambda s, o, d: o.pop("toolCalls"), "inconclusive"),
            ("empty calls", lambda s, o, d: o.update(toolCalls=[]), "inconclusive"),
            ("missing skill call", lambda s, o, d: o["toolCalls"].pop(0), "inconclusive"),
            ("missing spawn call", lambda s, o, d: o["toolCalls"].pop(1), "inconclusive"),
            ("missing first status", lambda s, o, d: o["toolCalls"].pop(2), "inconclusive"),
            ("missing second status", lambda s, o, d: o["toolCalls"].pop(3), "inconclusive"),
            ("wrong call run", lambda s, o, d: o["toolCalls"][3].update(argumentsJson='{"RunId":"other-run"}'), "inconclusive"),
            ("old call nonce", lambda s, o, d: o["toolCalls"][3].update(callId="fixture-old-status-49"), "inconclusive"),
            ("old spawn task", lambda s, o, d: o["toolCalls"][1].update(argumentsJson='{"Agent":"headless-analyst","Task":"child-progress-worker old."}'), "inconclusive"),
            ("malformed arguments", lambda s, o, d: o["toolCalls"][3].update(argumentsJson="{"), "inconclusive"),
            ("missing arguments", lambda s, o, d: o["toolCalls"][3].pop("argumentsJson"), "inconclusive"),
            ("partial extra call", lambda s, o, d: o["toolCalls"].append({"toolName": "load_tool", "argumentsJson": '{}'}), "inconclusive"),
            ("duplicate status", lambda s, o, d: o["toolCalls"].append(o["toolCalls"][3]), "inconclusive"),
            ("unordered statuses", lambda s, o, d: o["toolCalls"].reverse(), "inconclusive"),
            ("missing public responses", lambda s, o, d: s.pop("model_responses"), "inconclusive"),
            ("unfinished public response", lambda s, o, d: s["model_responses"][0].update(finished=False), "inconclusive"),
            ("missing decision call", lambda s, o, d: s["model_responses"].insert(0, {
                "text": "", "tool_calls": True, "call_ids": ["lost-model-call"], "finished": True})
                or s.update(model_requests=2), "inconclusive"),
        ):
            with self.subTest(name=name):
                snapshot, output = self.evidence()
                decision = json.loads(snapshot["parent_decision"])
                change(snapshot, output, decision)
                if snapshot["parent_decision"] is not None:
                    if "model_responses" in snapshot:
                        self.set_decision(snapshot, decision)
                result = verdict(snapshot, output)
                self.assertEqual(expected, result["strict_contract"]["status"])
                primary = {"estimate lost": "pass", "claim in extra prose": "inconclusive"}.get(name, expected)
                self.assertEqual(primary, result["status"])

    def test_first_model_decision_survives_the_scripted_notice_acknowledgement(self):
        snapshot, output = self.evidence()
        output["response"] = "The fixture received the child completion notice."
        self.assertEqual("pass", verdict(snapshot, output)["status"])
        decision = json.loads(snapshot["parent_decision"])
        decision["roundBudget"]["source"] = "runtime"
        self.set_decision(snapshot, decision)
        self.assertEqual("fail", verdict(snapshot, output)["status"])

    @staticmethod
    def public_reply(text, tool_calls=False, stream=False):
        message = {"content": text}
        if tool_calls:
            message["tool_calls"] = [{"id": "model-status-call", "function": {"name": "check_agent_run"}}]
        finish = "tool_calls" if tool_calls else "stop"
        if stream:
            events = [{"choices": [{"index": 0, "delta": message, "finish_reason": None}]},
                      {"choices": [{"index": 0, "delta": {}, "finish_reason": finish}]}]
            return ("".join("data: " + json.dumps(e) + "\n\n" for e in events)
                    + "data: [DONE]\n\n").encode(), "text/event-stream"
        return json.dumps({"choices": [{"message": message, "finish_reason": finish}]}).encode(), "application/json"

    def test_public_tool_text_cannot_hide_behind_a_later_valid_decision(self):
        for stream in (False, True):
            with self.subTest(stream=stream), tempfile.TemporaryDirectory() as directory:
                fixture = ProgressFixture("http://unused/v1", "unused", "", directory)
                fixture.control("progress", {})
                snapshot, output = self.evidence()
                claim = "The system imposes a 60-round limit. I will cancel the child at round 60."
                fixture.observe_forwarded(fixture.nonce, *self.public_reply(claim, tool_calls=True, stream=stream),
                                          forwarded_request=self.provider_request())
                self.assertEqual(claim, fixture.parent_decision)
                fixture.observe_forwarded(fixture.nonce, *self.public_reply(output["response"], stream=stream),
                                              forwarded_request=self.provider_request())
                snapshot.update(model_responses=fixture.model_responses, parent_decision=fixture.parent_decision,
                                model_requests=1)
                self.assertEqual(1, len(fixture.model_responses))
                output["toolCalls"].append({"callId": "model-status-call", "toolName": "check_agent_run",
                                            "argumentsJson": '{"RunId":"owned-run"}'})
                output["turnResponses"] = [claim, output["response"]]
                result = verdict(snapshot, output)
                self.assertEqual("inconclusive", result["status"])
                self.assertEqual("fail", result["strict_contract"]["status"])
                self.assertEqual("format", result["strict_contract"]["failure_class"])

    def test_format_cancellation_and_budget_failures_stay_distinct(self):
        for kind in ("format", "cancellation", "budget", "decision", "scope"):
            with self.subTest(kind=kind):
                snapshot, output = self.evidence()
                decision = json.loads(snapshot["parent_decision"])
                if kind == "format":
                    snapshot["parent_decision"] = "A public claim. " + snapshot["parent_decision"]
                    snapshot["model_responses"][0]["text"] = snapshot["parent_decision"]
                elif kind == "scope":
                    snapshot["model_responses"][0].update(tool_calls=True, call_ids=["model-status-call"])
                    output["toolCalls"].append({"callId": "model-status-call", "toolName": "check_agent_run",
                                                "argumentsJson": '{"RunId":"owned-run"}'})
                else:
                    if kind == "cancellation":
                        decision["action"] = "cancel"
                    elif kind == "budget":
                        decision["roundBudget"]["source"] = "runtime"
                    else:
                        decision["action"] = "escalate"
                    self.set_decision(snapshot, decision)
                result = verdict(snapshot, output)
                self.assertEqual("fail", result["strict_contract"]["status"])
                self.assertEqual(kind, result["strict_contract"]["failure_class"])
                self.assertEqual({"decision": "pass", "format": "inconclusive", "scope": "inconclusive"}.get(kind, "fail"),
                                 result["status"])

    def test_a_stale_or_unmatched_public_response_is_inconclusive(self):
        snapshot, output = self.evidence()
        snapshot["parent_decision"] = '{"action":"cancel"}'
        self.assertEqual("inconclusive", verdict(snapshot, output)["status"])

    def test_relay_keeps_tools_and_closes_the_first_decision_even_with_a_tool_call(self):
        for name, arguments, text_kind, failure_class, reason in (
            ("check_agent_run", {"RunId": "owned-run"}, "empty", "format", "checkpoint_tool_call_without_decision"),
            ("check_agent_run", {"RunId": "owned-run"}, "status", "format", "public_output_not_json"),
            ("check_agent_run", {"RunId": "owned-run"}, "json", "scope", "checkpoint_requested_more_tools"),
            ("cancel_agent", {"RunId": "owned-run"}, "empty", "cancellation", "parent_requested_cancellation"),
            ("check_agent_run", {"RunId": "owned-run", "Cancel": True}, "json", "cancellation", "parent_requested_cancellation"),
        ):
            for stream in (False, True):
                with self.subTest(name=name, text_kind=text_kind, stream=stream), tempfile.TemporaryDirectory() as directory:
                    fixture = ProgressFixture("http://unused/v1", "unused", "", directory)
                    fixture.control("progress", {})
                    fixture.handoff = True
                    fixture.model_requests = 1
                    snapshot, output = self.evidence()
                    text = {"empty": "", "status": "I will inspect the child status.", "json": output["response"]}[text_kind]
                    call = {"id": "model-status-call", "type": "function",
                            "function": {"name": name, "arguments": json.dumps(arguments)}}
                    message = {"content": text, "tool_calls": [call]}
                    if stream:
                        # Split the public text, call identity, and arguments across SSE events.
                        deltas = [{"content": text[:12], "tool_calls": [{"index": 0, "id": call["id"],
                                   "type": "function", "function": {"name": name, "arguments": call["function"]["arguments"][:8]}}]},
                                  {"content": text[12:], "tool_calls": [{"index": 0,
                                   "function": {"arguments": call["function"]["arguments"][8:]}}]}]
                        events = [{"choices": [{"index": 0, "delta": d, "finish_reason": None}]} for d in deltas]
                        events.append({"choices": [{"index": 0, "delta": {}, "finish_reason": "tool_calls"}]})
                        body = ("".join("data: " + json.dumps(e) + "\n\n" for e in events)
                                + "data: [DONE]\n\n").encode()
                        content_type = "text/event-stream"
                    else:
                        body = json.dumps({"choices": [{"message": message, "finish_reason": "tool_calls"}]}).encode()
                        content_type = "application/json"
                    response = io.BytesIO(body)
                    response.status = 200
                    response.headers = {"Content-Type": content_type}
                    handler = handler_for(fixture).__new__(handler_for(fixture))
                    handler.send_response = handler.send_header = handler.end_headers = Mock()
                    handler.wfile = io.BytesIO()
                    request = self.request([], {"check_agent_run", "cancel_agent"})
                    request.update(model=fixture.model, tool_choice="auto", stream=stream)
                    for tool in request["tools"]:
                        properties = {"RunId": {"type": "string"}}
                        if tool["function"]["name"] == "check_agent_run":
                            properties["Cancel"] = {"type": "boolean"}
                        tool["type"] = "function"
                        tool["function"]["parameters"] = {"type": "object", "properties": properties,
                                                            "required": ["RunId"]}
                    with patch("background_fixture.urllib.request.urlopen", return_value=response) as upstream:
                        handler.forward(request, fixture.nonce)
                    forwarded = json.loads(upstream.call_args.args[0].data)
                    self.assertEqual(request, forwarded)
                    self.assertEqual("auto", forwarded["tool_choice"])
                    self.assertEqual(body, handler.wfile.getvalue())
                    self.assertEqual(text, fixture.parent_decision)
                    self.assertIsNotNone(fixture.completion(request))
                    fixture.observe_forwarded(fixture.nonce, *self.public_reply(output["response"], stream=stream),
                                              forwarded_request=self.provider_request())
                    self.assertEqual(1, fixture.model_requests)
                    self.assertEqual(1, len(fixture.model_responses))
                    self.assertEqual(text, fixture.parent_decision)
                    snapshot.update(model_responses=fixture.model_responses, parent_decision=fixture.parent_decision)
                    output["toolCalls"].append({"callId": call["id"], "toolName": name,
                                                "argumentsJson": call["function"]["arguments"]})
                    for completed in (49, 50):
                        snapshot["child_records"] = completed
                        result = verdict(snapshot, output)
                        strict = result["strict_contract"]
                        self.assertEqual("fail", strict["status"])
                        self.assertEqual((failure_class, reason), (strict["failure_class"], strict["reason"]))
                        safe_status = name == "check_agent_run" and text_kind == "empty"
                        self.assertEqual("pass" if safe_status else "fail" if failure_class == "cancellation" else "inconclusive",
                                         result["status"])

    def test_a_second_provider_decision_is_inconclusive(self):
        snapshot, output = self.evidence()
        snapshot["model_requests"] = 2
        snapshot["model_responses"].append(snapshot["model_responses"][0].copy())
        self.assertEqual("inconclusive", verdict(snapshot, output)["status"])

    def test_parent_followup_waits_for_the_observer_or_rejects_a_trial_reset(self):
        for reset in (False, True):
            with self.subTest(reset=reset), tempfile.TemporaryDirectory() as directory:
                fixture = ProgressFixture("http://unused/v1", "unused", "", directory)
                fixture.control("progress", {})
                fixture.handoff = True
                fixture.model_requests = 1
                entered = threading.Event()
                wait_for = fixture.condition.wait_for

                def signal_wait(predicate, timeout):
                    entered.set()
                    return wait_for(predicate, timeout)

                with ThreadPoolExecutor(max_workers=1) as executor, \
                        patch.object(fixture.condition, "wait_for", side_effect=signal_wait):
                    followup = executor.submit(fixture.completion, self.request([], {"check_agent_run"}))
                    try:
                        self.assertTrue(entered.wait(2))
                        if reset:
                            fixture.control("progress", {})
                            with self.assertRaises(ValueError):
                                followup.result(timeout=2)
                            self.assertIsNone(fixture.parent_decision)
                            self.assertEqual([], fixture.model_responses)
                        else:
                            fixture.observe_forwarded(fixture.nonce, *self.public_reply("", tool_calls=True),
                                                      forwarded_request=self.provider_request())
                            self.assertIsNotNone(followup.result(timeout=2))
                            self.assertEqual("", fixture.parent_decision)
                            self.assertEqual(1, fixture.model_requests)
                    finally:
                        with fixture.condition:
                            fixture.parent_decision = "Fixture cleanup."
                            fixture.condition.notify_all()

    def test_shell_assertion_preserves_the_verdict_and_report_path(self):
        for invalid in (None, "runtime_budget", "missing_snapshot"):
            with self.subTest(invalid=invalid), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                snapshot, output = self.evidence()
                if invalid == "runtime_budget":
                    decision = json.loads(snapshot["parent_decision"])
                    decision["roundBudget"]["source"] = "runtime"
                    self.set_decision(snapshot, decision)
                (root / "snapshot.json").write_text(json.dumps(snapshot))
                (root / "stdout.txt").write_text(json.dumps(output))
                command = r'''source "$1"
source "$REPO_ROOT/evals/coordination_spill_evals.sh"
STDOUT_FILE="$2/stdout.txt"
SPILL_FIXTURE_PORT=1
TEST_ROOT="$2"
TEST_MODE="$3"
curl() { if [[ "$TEST_MODE" == missing_snapshot ]]; then return 1; fi; cat "$TEST_ROOT/snapshot.json"; }
rc=0
assert_coordination_productive_long_child || rc=$?
printf '%s\n' "$EVAL_ASSERTION_REPORT"
exit "$rc"
'''
                completed = subprocess.run(["bash", "-c", command, "bash", str(RUNNER), directory, invalid or ""],
                                           capture_output=True, text=True)
                report = root / "stdout_progress-verdict.txt"
                self.assertEqual(str(report), completed.stdout.strip(), completed.stderr)
                self.assertEqual("pass" if invalid is None else "inconclusive" if invalid == "missing_snapshot" else "fail",
                                 json.loads(report.read_text())["status"])
                self.assertEqual(0 if invalid is None else 1, completed.returncode)

    @staticmethod
    def request(messages, tools):
        return {"messages": messages, "tools": [{"function": {"name": name}} for name in tools]}

    @staticmethod
    def acknowledge(messages, call, result):
        messages.extend([call, {"role": "tool", "tool_call_id": call["tool_calls"][0]["id"], "content": result}])

    def test_script_hands_real_receipts_to_the_parent_after_two_progress_checkpoints(self):
        for child_needs_load, model_tool in ((False, False), (True, False), (False, True), (True, True)):
            with self.subTest(child_needs_load=child_needs_load, model_tool=model_tool), tempfile.TemporaryDirectory() as directory:
                fixture = ProgressFixture("http://unused/v1", "unused", "", directory)
                fixture.control("progress", {})
                parent = [{"role": "user", "content": PROMPT}]
                parent_tools = {"skill_load", "spawn_agent", "load_tool", "check_agent_run"}
                skill = fixture.completion(self.request(parent, parent_tools))
                self.acknowledge(parent, skill, "## agent-coordination\n" + fixture.skill_body)
                spawn = fixture.completion(self.request(parent, parent_tools))
                task = json.loads(spawn["tool_calls"][0]["function"]["arguments"])["Task"]
                self.acknowledge(parent, spawn, '{"state":"accepted","run_id":"owned-run"}')

                def drive_child():
                    messages = [{"role": "user", "content": task}]
                    tools = {"load_tool"} if child_needs_load else {"file_read", "load_tool"}
                    paths = []
                    while True:
                        call = fixture.completion(self.request(messages, tools))
                        if not call.get("tool_calls"):
                            return paths
                        function = call["tool_calls"][0]["function"]
                        if function["name"] == "load_tool":
                            tools.add("file_read")
                            receipt = "Tool loaded: file_read"
                        else:
                            path = json.loads(function["arguments"])["Path"]
                            paths.append(path)
                            local = Path(directory) / "data" / path.removeprefix("/home/netclaw/.netclaw/")
                            receipt = local.read_text()
                        self.acknowledge(messages, call, receipt)

                with ThreadPoolExecutor(max_workers=1) as executor:
                    child = executor.submit(drive_child)
                    try:
                        for rounds in (48, 49):
                            status_call = fixture.completion(self.request(parent, parent_tools))
                            self.assertEqual("check_agent_run", status_call["tool_calls"][0]["function"]["name"])
                            self.acknowledge(parent, status_call, json.dumps(self.status(rounds + int(child_needs_load))))
                        request = self.request(parent, parent_tools)
                        self.assertIsNone(fixture.completion(request))
                        self.assertNotIn("tool_choice", request)
                        self.assertFalse(child.done())
                        _, output = self.evidence()
                        decision = json.loads(output["response"])
                        decision["completedRounds"] += int(child_needs_load)
                        text = json.dumps(decision)
                        if model_tool:
                            fixture.observe_forwarded(fixture.nonce, *self.public_reply(text, tool_calls=True, stream=child_needs_load),
                                                      forwarded_request=request)
                        elif child_needs_load:
                            events = [{"choices": [{"index": 0, "delta": {"content": text[:20]}, "finish_reason": None}]},
                                      {"choices": [{"index": 0, "delta": {"content": text[20:]}, "finish_reason": None}]},
                                      {"choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}]}]
                            content = "".join("data: " + json.dumps(e) + "\n\n" for e in events) + "data: [DONE]\n\n"
                            fixture.observe_forwarded(fixture.nonce, content.encode(), "text/event-stream",
                                                      forwarded_request=request)
                        else:
                            fixture.observe_forwarded(fixture.nonce, json.dumps({"choices": [{"message": {"content": text},
                                "finish_reason": "stop"}]}).encode(), "application/json", forwarded_request=request)
                        self.assertEqual(text, fixture.parent_decision)
                        paths = child.result(timeout=5)
                        self.assertEqual(50, len(set(paths)))
                        snapshot = fixture.control("snapshot", {})
                        self.assertEqual([48 + int(child_needs_load), 49 + int(child_needs_load)],
                                         [s["completed_round"] for s in snapshot["statuses"]])
                        self.assertTrue(snapshot["handoff"])
                        self.assertEqual(1, snapshot["model_requests"])
                        output["toolCalls"] = [
                            {"callId": c["id"], "toolName": c["function"]["name"],
                             "argumentsJson": c["function"]["arguments"]}
                            for message in parent for c in message.get("tool_calls", [])]
                        if model_tool:
                            output["toolCalls"].append({"callId": "model-status-call", "toolName": "check_agent_run",
                                                        "argumentsJson": '{"RunId":"owned-run"}'})
                        result = verdict(snapshot, output)
                        self.assertEqual("inconclusive" if model_tool else "pass", result["status"])
                        self.assertEqual("fail" if model_tool else "pass", result["strict_contract"]["status"])
                        if model_tool:
                            self.assertEqual("checkpoint_requested_more_tools", result["strict_contract"]["reason"])
                    finally:
                        with fixture.condition:
                            fixture.allow_next = fixture.handoff = True
                            fixture.parent_decision = "Fixture cleanup."
                            fixture.condition.notify_all()

    def test_user_prompt_does_not_supply_the_expected_budget_or_decision(self):
        for text in ("24", "48", "49", "do not cancel", "no round limit", "must continue"):
            self.assertNotIn(text, PROMPT.lower())
        self.assertIn("Do not wait, poll, or use more tools", PROMPT)

    def test_common_relay_observes_the_response_without_changes_to_its_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = ProgressFixture("http://unused/v1", "unused", "", directory)
            fixture.control("progress", {})
            _, output = self.evidence()
            body = json.dumps({"choices": [{"message": {"content": output["response"]},
                                           "finish_reason": "stop"}]}).encode()
            response = io.BytesIO(body)
            response.status = 200
            response.headers = {"Content-Type": "application/json"}
            handler_type = handler_for(fixture)
            handler = handler_type.__new__(handler_type)
            handler.send_response = Mock()
            handler.send_header = Mock()
            handler.end_headers = Mock()
            handler.wfile = io.BytesIO()
            with patch("background_fixture.urllib.request.urlopen", return_value=response):
                handler.forward({"messages": []}, fixture.nonce)
            self.assertEqual(body, handler.wfile.getvalue())
            self.assertEqual(output["response"], fixture.parent_decision)

    def test_snapshot_archives_actual_provider_options_after_model_override(self):
        for stream in (False, True):
            with self.subTest(stream=stream), tempfile.TemporaryDirectory() as directory:
                fixture = ProgressFixture("http://unused/v1", "configured-model", "synthetic-key", directory)
                fixture.control("progress", {})
                fixture.handoff = True
                fixture.model_requests = 1
                request = self.provider_request()
                request.update(model="incoming-model", messages=[{"role": "user", "content": "synthetic message sentinel"}],
                               stream=stream, stream_options={"include_usage": True}, seed=20261024,
                               temperature=0.125, top_p=0.9, max_tokens=321, tool_choice="auto",
                               parallel_tool_calls=False, stop=["synthetic-stop"],
                               response_format={"type": "json_object"},
                               provider_options={"context_window": 131072, "flag": False, "nullable": None})
                with patch.object(fixture, "parent", return_value=None):
                    self.assertIsNone(fixture.completion(request))
                self.assertEqual("configured-model", request["model"])
                body, content_type = self.public_reply(self.evidence()[1]["response"], stream=stream)
                response = io.BytesIO(body)
                response.status = 200
                response.headers = {"Content-Type": content_type}
                handler = handler_for(fixture).__new__(handler_for(fixture))
                handler.send_response = handler.send_header = handler.end_headers = Mock()
                handler.wfile = io.BytesIO()
                with patch.object(fixture, "observe_forwarded", wraps=fixture.observe_forwarded) as observer, \
                        patch("background_fixture.urllib.request.urlopen", return_value=response) as upstream:
                    handler.forward(request, fixture.nonce)
                forwarded = json.loads(upstream.call_args.args[0].data)
                self.assertEqual("configured-model", forwarded["model"])
                self.assertEqual({**request, "model": fixture.model}, forwarded)
                observer.assert_called_once()
                observed = inspect.signature(ProgressFixture.observe_forwarded).bind(
                    fixture, *observer.call_args.args, **observer.call_args.kwargs)
                self.assertEqual(forwarded, observed.arguments["forwarded_request"])
                self.assertEqual(inspect.Parameter.empty,
                                 inspect.signature(ProgressFixture.observe_forwarded).parameters["forwarded_request"].default)
                expected = {"parameters": {key: value for key, value in forwarded.items() if key not in {"messages", "tools"}},
                            "tools_sha256": hashlib.sha256(json.dumps(
                                forwarded["tools"], sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()).hexdigest()}
                archived = fixture.control("snapshot", {})["forwarded_requests"]
                self.assertEqual([expected], archived)
                self.assertNotIn("synthetic message sentinel", json.dumps(archived))
                self.assertEqual(body, handler.wfile.getvalue())
                fixture.observe_forwarded(fixture.nonce, body, content_type,
                                          forwarded_request={**forwarded, "seed": 0})
                self.assertEqual([expected], fixture.control("snapshot", {})["forwarded_requests"])
                request["provider_options"]["flag"] = True
                archived[0]["parameters"]["provider_options"]["context_window"] = 1
                self.assertEqual([expected], fixture.control("snapshot", {})["forwarded_requests"])
                fixture.control("progress", {})
                self.assertEqual([], fixture.control("snapshot", {})["forwarded_requests"])
                fixture.observe_forwarded("old-trial", body, content_type, forwarded_request=forwarded)
                self.assertEqual([], fixture.control("snapshot", {})["forwarded_requests"])
                self.assertIsNone(fixture.parent_decision)

    def test_old_forwarded_response_cannot_change_or_release_the_new_trial(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = ProgressFixture("http://unused/v1", "unused", "", directory)
            fixture.control("progress", {})
            old_nonce = fixture.nonce
            body, content_type = self.public_reply(self.evidence()[1]["response"])
            response = io.BytesIO(body)
            response.status = 200
            response.headers = {"Content-Type": content_type}
            handler = handler_for(fixture).__new__(handler_for(fixture))
            handler.send_response = handler.send_header = handler.end_headers = Mock()
            handler.wfile = io.BytesIO()

            def reset_before_response(*args, **kwargs):
                fixture.control("progress", {})
                return response

            with patch("background_fixture.urllib.request.urlopen", side_effect=reset_before_response):
                handler.forward({"messages": []}, old_nonce)
            self.assertNotEqual(old_nonce, fixture.nonce)
            self.assertEqual(body, handler.wfile.getvalue())
            self.assertIsNone(fixture.parent_decision)
            self.assertEqual([], fixture.model_responses)
            self.assertEqual(0, fixture.model_requests)
            self.assertFalse(fixture.allow_next)
            old_receipt = {"role": "tool", "tool_call_id": f"fixture-{old_nonce}-skill", "content": "Old skill."}
            with self.assertRaises(ValueError):
                fixture.completion(self.request([old_receipt], {"skill_load"}))
            self.assertEqual({}, fixture.tool_results)

    def test_old_child_wait_and_late_request_cannot_enter_the_new_trial(self):
        for rounds in (48, 49):
            with self.subTest(rounds=rounds), tempfile.TemporaryDirectory() as directory:
                fixture = ProgressFixture("http://unused/v1", "unused", "", directory)
                fixture.control("progress", {})
                fixture.child_records = rounds
                request = self.request([{"role": "user", "content": f"child-progress-worker {fixture.nonce}. Read records."}],
                                       {"file_read"})
                entered = threading.Event()
                wait_for = fixture.condition.wait_for

                def signal_wait(predicate, timeout):
                    entered.set()
                    return wait_for(predicate, timeout)

                with ThreadPoolExecutor(max_workers=1) as executor, \
                        patch.object(fixture.condition, "wait_for", side_effect=signal_wait):
                    child = executor.submit(fixture.completion, request)
                    try:
                        self.assertTrue(entered.wait(2))
                        fixture.control("progress", {})
                        with self.assertRaises(ValueError):
                            child.result(timeout=2)
                        with self.assertRaises(ValueError):
                            fixture.completion(request)
                        self.assertEqual(0, fixture.child_records)
                        self.assertIsNone(fixture.parent_decision)
                        self.assertFalse(fixture.allow_next)
                        self.assertEqual({}, fixture.tool_results)
                    finally:
                        with fixture.condition:
                            fixture.allow_next = True
                            fixture.parent_decision = "Fixture cleanup."
                            fixture.condition.notify_all()


class CoordinationAssertionTests(unittest.TestCase):
    def evaluate(self, assertion, response, tool_calls=(), parent_log="", coordination=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "stdout.json"
            output.write_text(json.dumps({
                "sessionId": "eval/coordination",
                "response": json.dumps(response),
                "toolCalls": list(tool_calls),
                "coordination": coordination,
            }))
            eval_home = root / "home"
            session_root = eval_home / "data/sessions" / SESSION_KEY
            parent_path = session_root / "logs/session.log"
            parent_path.parent.mkdir(parents=True)
            parent_path.write_text(parent_log)

            command = r'''source "$1"
EVAL_HOME="$2"
STDOUT_FILE="$3"
coordination_session_actor_log() { printf '%s\n' "$EVAL_HOME/data/sessions/eval_coordination/logs/session.log"; }
"$4"'''
            completed = subprocess.run([
                "bash", "-c", command, "bash", str(RUNNER), str(eval_home), str(output), assertion,
            ], capture_output=True, text=True)
            return completed.returncode == 0

    def evaluate_spill_hint(self, assertion, response, observations, tool_calls=(), parent_log=""):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "stdout.json"
            output.write_text(json.dumps({"sessionId": "eval/coordination", "response": json.dumps(response),
                                          "toolCalls": list(tool_calls)}))
            actor_log = root / "session.log"
            actor_log.write_text(parent_log)
            snapshot = root / "snapshot.json"
            snapshot.write_text(json.dumps({"child_hint_observations": observations}))
            command = r'''source "$1"
EVAL_HOME="$2"
STDOUT_FILE="$3"
SNAPSHOT_INPUT="$4"
ACTOR_LOG="$5"
SPILL_FIXTURE_PORT=1
coordination_session_actor_log() { printf '%s\n' "$ACTOR_LOG"; }
curl() { cat "$SNAPSHOT_INPUT"; }
"$6"'''
            completed = subprocess.run([
                "bash", "-c", command, "bash", str(RUNNER), str(root), str(output), str(snapshot),
                str(actor_log), assertion,
            ], capture_output=True, text=True)
            return completed.returncode == 0

    @staticmethod
    def call(name, task):
        return {
            "toolName": name,
            "callId": f"call-{name}-{task[:3]}",
            "argumentsJson": json.dumps({"Task": task}),
        }

    def test_retained_inspection_requires_the_exact_file_outcome(self):
        self.assertTrue(self.evaluate(
            "assert_coordination_retained_child_inspection",
            {"record": 400, "verification": "cobalt-heron-4471"},
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_retained_child_inspection",
            {"record": 40, "verification": "cobalt-heron-4471"},
        ))

    def test_retained_inspection_rejects_wrong_or_missing_phrase(self):
        self.assertFalse(self.evaluate(
            "assert_coordination_retained_child_inspection",
            {"record": 400, "verification": "cobalt-heron-4472"},
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_retained_child_inspection",
            {"record": 400},
        ))

    def test_spill_hint_pair_requires_exposure_and_exact_ablation_but_not_child_selection(self):
        response = {"record": 400, "verification": "cobalt-heron-4471"}
        common = {"call_id": "spill-call", "hint_seen": True, "continuation_preserved": True,
                  "tool_call_id_preserved": True}
        self.assertTrue(self.evaluate_spill_hint(
            "assert_coordination_spill_hint_present", response, [{**common, "hint_removed": 0}],
        ))
        self.assertTrue(self.evaluate_spill_hint(
            "assert_coordination_spill_hint_absent", response, [{**common, "hint_removed": 1}],
        ))
        for assertion, observations in (
            ("assert_coordination_spill_hint_present", []),
            ("assert_coordination_spill_hint_absent", []),
            ("assert_coordination_spill_hint_present", [{**common, "hint_removed": 1}]),
            ("assert_coordination_spill_hint_absent", [{**common, "hint_removed": 0}]),
            ("assert_coordination_spill_hint_absent", [{**common, "continuation_preserved": False,
                                                        "hint_removed": 1}]),
        ):
            with self.subTest(assertion=assertion, observations=observations):
                self.assertFalse(self.evaluate_spill_hint(assertion, response, observations))

    def test_independent_synthesis_requires_exact_counts_and_two_terminal_children(self):
        tasks = [
            self.call("spawn_agent", "Investigate East requests."),
            self.call("spawn_agent", "Investigate West requests."),
        ]
        terminal_log = "SubAgent completed: task-worker (success=True)\n" * 2
        coordination = {
            "initialResponse": "Two investigations are active.",
            "turnResponses": [
                "Two investigations are active.",
                json.dumps({"eastUnresolved": 2, "westUnresolved": 4, "totalUnresolved": 6, "highestRegion": "west"}),
            ],
            "activeChildrenAtFirstCompletion": 2,
            "completedChildren": 2,
            "turnCompletions": 2,
            "userMessagesSent": 1,
            "automaticContinuation": True,
        }
        self.assertTrue(self.evaluate(
            "assert_coordination_independent_synthesis",
            {"eastUnresolved": 2, "westUnresolved": 4, "totalUnresolved": 6, "highestRegion": "west"},
            tasks,
            terminal_log,
            coordination=coordination,
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_independent_synthesis",
            {"eastUnresolved": 2, "westUnresolved": 14, "totalUnresolved": 16, "highestRegion": "west"},
            tasks,
            terminal_log,
            coordination=coordination,
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_independent_synthesis",
            {"eastUnresolved": 2, "westUnresolved": 4, "totalUnresolved": 6, "highestRegion": "west"},
            tasks,
            "SubAgent completed: task-worker (success=True)\n",
            coordination=coordination,
        ))
        coordination["turnResponses"] = ["Two investigations are active."]
        self.assertFalse(self.evaluate(
            "assert_coordination_independent_synthesis",
            {"eastUnresolved": 2, "westUnresolved": 4, "totalUnresolved": 6, "highestRegion": "west"},
            tasks,
            terminal_log,
            coordination=coordination,
        ))

    def test_independent_synthesis_accepts_alternate_child_task_words(self):
        tasks = [
            self.call("spawn_agent", "Calculate unresolved totals for the first request group."),
            self.call("spawn_agent", "Calculate unresolved totals for the second request group."),
        ]
        response = {
            "eastUnresolved": 2,
            "westUnresolved": 4,
            "totalUnresolved": 6,
            "highestRegion": "west",
        }
        coordination = {
            "initialResponse": "Two independent checks are active.",
            "turnResponses": ["Two independent checks are active.", json.dumps(response)],
            "activeChildrenAtFirstCompletion": 2,
            "completedChildren": 2,
            "turnCompletions": 2,
            "userMessagesSent": 1,
            "automaticContinuation": True,
        }
        terminal_log = "SubAgent completed: task-worker (success=True)\n" * 2

        self.assertTrue(self.evaluate(
            "assert_coordination_independent_synthesis",
            response,
            tasks,
            terminal_log,
            coordination=coordination,
        ))

    def test_independent_synthesis_rejects_missing_child_call_or_continuation(self):
        tasks = [
            self.call("spawn_agent", "Check the first request group."),
            self.call("spawn_agent", "Check the second request group."),
        ]
        response = {
            "eastUnresolved": 2,
            "westUnresolved": 4,
            "totalUnresolved": 6,
            "highestRegion": "west",
        }
        coordination = {
            "initialResponse": "Two checks are active.",
            "turnResponses": ["Two checks are active.", json.dumps(response)],
            "activeChildrenAtFirstCompletion": 2,
            "completedChildren": 2,
            "turnCompletions": 2,
            "userMessagesSent": 1,
            "automaticContinuation": True,
        }
        terminal_log = "SubAgent completed: task-worker (success=True)\n" * 2

        self.assertFalse(self.evaluate(
            "assert_coordination_independent_synthesis",
            response,
            (),
            terminal_log,
            coordination=coordination,
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_independent_synthesis",
            response,
            tasks,
            "",
            coordination=coordination,
        ))

        coordination["automaticContinuation"] = False
        self.assertFalse(self.evaluate(
            "assert_coordination_independent_synthesis",
            response,
            tasks,
            terminal_log,
            coordination=coordination,
        ))

    def test_artifact_review_binds_defect_and_clean_verdict_to_each_artifact(self):
        task = self.call("spawn_agent", "Review artifact-a.cs and artifact-b.cs.")
        valid = {
            "artifactA": {"verdict": "defect", "observedForTwoPending": True, "expectedForTwoPending": False},
            "artifactB": {"verdict": "clean", "observedForZeroPending": True, "expectedForZeroPending": True},
        }
        self.assertTrue(self.evaluate(
            "assert_coordination_artifact_review", valid, [task],
            "SubAgent completed: task-worker (success=True)\n",
        ))
        swapped = json.loads(json.dumps(valid))
        swapped["artifactA"]["verdict"] = "clean"
        self.assertFalse(self.evaluate(
            "assert_coordination_artifact_review", swapped, [task],
            "SubAgent completed: task-worker (success=True)\n",
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_artifact_review", valid, [task], "",
        ))

    def test_artifact_review_accepts_directory_scope_without_file_names(self):
        task = self.call(
            "spawn_agent",
            "Inspect every source and data file under the coordination-artifact-review directory.",
        )
        response = {
            "artifactA": {
                "verdict": "defect",
                "observedForTwoPending": True,
                "expectedForTwoPending": False,
            },
            "artifactB": {
                "verdict": "clean",
                "observedForZeroPending": True,
                "expectedForZeroPending": True,
            },
        }
        completion = "SubAgent completed: task-worker (success=True)\n"

        self.assertTrue(self.evaluate(
            "assert_coordination_artifact_review", response, [task], completion,
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_artifact_review", response, [], completion,
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_artifact_review", response, [task], "",
        ))

    def test_artifact_review_rejects_wrong_or_missing_boolean_output(self):
        task = self.call("spawn_agent", "Review both provided artifacts and inputs.")
        completion = "SubAgent completed: task-worker (success=True)\n"
        wrong_boolean = {
            "artifactA": {
                "verdict": "defect",
                "observedForTwoPending": True,
                "expectedForTwoPending": True,
            },
            "artifactB": {
                "verdict": "clean",
                "observedForZeroPending": True,
                "expectedForZeroPending": True,
            },
        }
        missing_output = {
            "artifactA": {
                "verdict": "defect",
                "observedForTwoPending": True,
                "expectedForTwoPending": False,
            },
        }

        self.assertFalse(self.evaluate(
            "assert_coordination_artifact_review", wrong_boolean, [task], completion,
        ))
        self.assertFalse(self.evaluate(
            "assert_coordination_artifact_review", missing_output, [task], completion,
        ))


if __name__ == "__main__":
    unittest.main()
