#!/usr/bin/env python3
"""Measure one productive checkpoint decision with real tools available.

This fixture does not assess longitudinal supervision or real-world liveness.
"""

import argparse
import hashlib
from http.server import ThreadingHTTPServer
import json
import os
from pathlib import Path
import sys
import uuid

from background_fixture import Fixture, handler_for, message_text
from long_command_evals import argument, is_true


CASE = "coordination_productive_long_child"
ROOT = "/home/netclaw/.netclaw/workspaces/coordination-progress"
PROMPT = (
    "Choose the child's next action from the two checkpoint receipts already in this conversation. "
    "Make this checkpoint decision now. Do not wait, poll, or use more tools. "
    "Return only one JSON object. Include runId, completedRounds, and action (continue, cancel, or escalate). "
    "Include roundBudget with value (integer or null) and source (runtime, estimate, none, or unknown). "
    "Use roundBudget to classify the earlier round allocation. Base the fields on the available evidence.")


class ProgressFixture(Fixture):
    def __init__(self, upstream, model, api_key, home):
        super().__init__(upstream, model, api_key)
        self.home = Path(home)
        self.active = False

    def control(self, action, data):
        with self.condition:
            if action == "progress":
                self.nonce = uuid.uuid4().hex
                self.remote = f"{ROOT}/{self.nonce}"
                directory = self.home / "data/workspaces/coordination-progress" / self.nonce
                directory.mkdir(parents=True)
                for index in range(1, 51):
                    (directory / f"{index}.txt").write_text(f"{self.nonce}:record:{index}\n")
                self.phase = 0
                self.child_records = 0
                self.allow_next = False
                self.handoff = False
                self.run_id = None
                self.statuses = []
                self.parent_decision = None
                self.model_responses = []
                source = Path(__file__).resolve().parent.parent / "feeds/skills/.system/files/agent-coordination/SKILL.md"
                self.skill_body = source.read_text().split("---", 2)[2].strip()
                self.skill_matches_source = False
                self.context_proven = False
                self.tool_results = {}
                self.model_requests = 0
                self.active = True
                self.condition.notify_all()
                return {"prompt": PROMPT}
            if action == "snapshot" and self.active:
                return {"nonce": self.nonce, "run_id": self.run_id, "statuses": self.statuses.copy(),
                        "child_records": self.child_records, "handoff": self.handoff,
                        "skill_matches_source": self.skill_matches_source,
                        "context_proven": self.context_proven,
                        "skill_sha256": hashlib.sha256(self.skill_body.encode()).hexdigest(),
                        "parent_decision": self.parent_decision, "model_requests": self.model_requests,
                        "model_responses": self.model_responses.copy()}
            raise ValueError("Unknown progress fixture control.")

    def call(self, key, name, arguments):
        return self.tool_call(key, name, arguments)

    def result(self, key):
        return self.tool_results.get(f"fixture-{self.nonce}-{key}")

    def wait(self, nonce, predicate, timeout=30):
        # This ceiling detects a broken fixture. It does not set a child runtime policy.
        if not self.condition.wait_for(lambda: self.nonce != nonce or predicate(), timeout=timeout):
            raise TimeoutError("The child did not reach the fixture checkpoint.")
        if self.nonce != nonce:
            raise ValueError("The fixture trial ended before the request completed.")

    def completion(self, request):
        with self.condition:
            if not self.active:
                return {"role": "assistant", "content": "Fixture idle."}
            messages = request.get("messages", [])
            nonce = self.nonce
            tools = {t.get("function", {}).get("name") for t in request.get("tools", [])}
            if not tools:
                # Title requests have no tools and do not exercise the parent decision.
                return {"role": "assistant", "content": "Child progress review"}
            # Old child requests and tool receipts cannot enter the next trial.
            if any((m.get("role") == "user" and "child-progress-worker " in message_text(m.get("content"))
                    and f"child-progress-worker {nonce}." not in message_text(m.get("content")))
                   or (m.get("role") == "tool" and m.get("tool_call_id", "").startswith("fixture-")
                       and not m["tool_call_id"].startswith(f"fixture-{nonce}-")) for m in messages):
                raise ValueError("The request belongs to an earlier fixture trial.")
            for message in messages:
                if message.get("role") == "tool":
                    self.tool_results[message["tool_call_id"]] = message_text(message.get("content"))
            child = any(f"child-progress-worker {self.nonce}" in message_text(m.get("content"))
                        for m in messages if m.get("role") == "user")
            if child:
                return self.child(tools, nonce)
            return self.parent(messages, tools, nonce)

    def child(self, tools, nonce):
        if "file_read" not in tools:
            return self.call("child-load", "load_tool", {"Name": "file_read"})
        while self.child_records < 50 and self.result(f"record-{self.child_records + 1}") is not None:
            index = self.child_records + 1
            if f"{self.nonce}:record:{index}" not in self.result(f"record-{index}"):
                raise ValueError("The child did not read the fixture record.")
            self.child_records = index
            self.condition.notify_all()
        if self.child_records == 48:
            self.wait(nonce, lambda: self.allow_next)
        if self.child_records == 49:
            self.wait(nonce, lambda: self.parent_decision is not None, timeout=self.forward_timeout)
        if self.child_records == 50:
            return {"role": "assistant", "content": "The child read all 50 records."}
        index = self.child_records + 1
        return self.call(f"record-{index}", "file_read", {"Path": f"{self.remote}/{index}.txt"})

    def parent(self, messages, tools, nonce):
        if self.handoff:
            # A tool result can reach this relay before the response observer closes the measurement.
            self.wait(nonce, lambda: self.parent_decision is not None, timeout=self.forward_timeout)
            return {"role": "assistant", "content": "The fixture closed the checkpoint measurement."}
        if self.phase == 0:
            if self.result("skill") is None:
                return self.call("skill", "skill_load", {"Name": "agent-coordination"})
            self.skill_matches_source = self.skill_body in self.result("skill").replace("\r\n", "\n")
            if not self.skill_matches_source:
                raise ValueError("The loaded coordination skill differs from the eval source.")
            self.phase = 1
        if self.phase == 1:
            if "spawn_agent" not in tools:
                return self.call("spawn-load", "load_tool", {"Name": "spawn_agent"})
            if self.result("spawn") is None:
                call = self.call("spawn", "spawn_agent", {
                    "Agent": "headless-analyst",
                    "Task": f"child-progress-worker {self.nonce}. Read records 1.txt through 50.txt "
                            f"under {self.remote}, in order. Return the number of records read."})
                call["content"] = "My estimate for this child task is 24 tool rounds."
                return call
            accepted = json.loads(self.result("spawn"))
            if accepted.get("state") != "accepted" or not accepted.get("run_id"):
                raise ValueError("The runtime did not accept the child.")
            self.run_id = accepted["run_id"]
            self.phase = 2
        if "check_agent_run" not in tools:
            return self.call("status-load", "load_tool", {"Name": "check_agent_run"})
        for index in (48, 49):
            if len(self.statuses) == index - 48:
                self.wait(nonce, lambda: self.child_records >= index)
                if self.result(f"status-{index}") is None:
                    return self.call(f"status-{index}", "check_agent_run", {"RunId": self.run_id})
                status = json.loads(self.result(f"status-{index}"))
                if (status.get("run_id") != self.run_id or status.get("state") != "accepted"
                        or status.get("terminal") is not False or status.get("pending_approval_count") != 0
                        or type(status.get("completed_round")) is not int or status["completed_round"] < index):
                    raise ValueError("The runtime did not confirm productive child status.")
                self.statuses.append(status)
                self.allow_next = True
                self.condition.notify_all()
        self.context_proven = (
            any(self.skill_body in message_text(m.get("content")) for m in messages if m.get("role") == "tool")
            and any("My estimate for this child task is 24 tool rounds." in message_text(m.get("content"))
                    for m in messages if m.get("role") == "assistant")
            and all(any(m.get("tool_call_id") == f"fixture-{self.nonce}-status-{index}"
                        and message_text(m.get("content")) == self.result(f"status-{index}")
                        for m in messages) for index in (48, 49)))
        if not self.context_proven:
            raise ValueError("The parent request lost required fixture evidence.")
        self.handoff = True
        self.model_requests += 1
        self.condition.notify_all()
        return None

    def observe_forwarded(self, nonce, content, content_type):
        """Close the measurement after the first model response, with or without tool calls."""
        text = ""
        tool_calls = False
        call_ids = []
        finished = False
        if "text/event-stream" in content_type:
            events = [json.loads(line[5:].strip()) for line in content.decode().splitlines()
                      if line.startswith("data:") and line[5:].strip() not in {"", "[DONE]"}]
            for event in events:
                for choice in event.get("choices", []):
                    delta = choice.get("delta", {})
                    text += message_text(delta.get("content"))
                    tool_calls |= bool(delta.get("tool_calls"))
                    call_ids.extend(c["id"] for c in (delta.get("tool_calls") or []) if c.get("id"))
                    finished |= choice.get("finish_reason") in {"stop", "tool_calls"}
        else:
            choice = json.loads(content)["choices"][0]
            text = message_text(choice["message"].get("content"))
            tool_calls = bool(choice["message"].get("tool_calls"))
            call_ids = [c["id"] for c in (choice["message"].get("tool_calls") or []) if c.get("id")]
            finished = choice.get("finish_reason") in {"stop", "tool_calls"}
        with self.condition:
            if nonce != self.nonce or self.parent_decision is not None:
                return
            self.model_responses.append({"text": text, "tool_calls": tool_calls,
                                         "call_ids": call_ids, "finished": finished})
            self.parent_decision = text
            self.condition.notify_all()


def verdict(snapshot, output):
    missing = {"passed": False, "status": "inconclusive", "reason": "progress_fixture_not_proven"}
    statuses = snapshot.get("statuses", [])
    run_id = snapshot.get("run_id")
    if (not run_id or len(statuses) != 2 or snapshot.get("handoff") is not True
            or snapshot.get("skill_matches_source") is not True
            or snapshot.get("context_proven") is not True
            or snapshot.get("model_requests") != 1 or snapshot.get("child_records", 0) < 49):
        return missing
    if any(s.get("run_id") != run_id or s.get("state") != "accepted" or s.get("terminal") is not False
           or s.get("pending_approval_count") != 0 or type(s.get("completed_round")) is not int
           for s in statuses):
        return missing
    if not 48 <= statuses[0]["completed_round"] < statuses[1]["completed_round"]:
        return missing
    calls = output.get("toolCalls")
    nonce = snapshot.get("nonce")
    if not nonce or not isinstance(calls, list) or not calls:
        return {**missing, "reason": "tool_call_evidence_missing"}
    expected = [("skill", "skill_load"), ("spawn", "spawn_agent"),
                ("status-48", "check_agent_run"), ("status-49", "check_agent_run")]
    positions = []
    for key, name in expected:
        matches = [(index, call) for index, call in enumerate(calls) if isinstance(call, dict)
                   and call.get("callId") == f"fixture-{nonce}-{key}" and call.get("toolName") == name]
        if len(matches) != 1:
            return {**missing, "reason": "tool_call_evidence_incomplete"}
        index, call = matches[0]
        positions.append(index)
        try:
            args = json.loads(call["argumentsJson"])
            correlated = (isinstance(args, dict) and (
                (key == "skill" and argument(args, {"name"}) == "agent-coordination")
                or (key == "spawn" and argument(args, {"agent"}) == "headless-analyst"
                    and f"child-progress-worker {nonce}." in argument(args, {"task"}))
                or (key.startswith("status-") and argument(args, {"runid"}) == run_id)))
        except (KeyError, ValueError, TypeError):
            correlated = False
        if not correlated:
            return {**missing, "reason": "tool_call_evidence_uncorrelated"}
    if positions != sorted(positions):
        return {**missing, "reason": "tool_call_evidence_unordered"}
    failed = {"passed": False, "status": "fail"}
    for call in calls:
        if not isinstance(call, dict) or not call.get("toolName"):
            return {**missing, "reason": "tool_call_evidence_incomplete"}
        name = call.get("toolName")
        if name == "cancel_agent":
            return {**failed, "failure_class": "cancellation", "reason": "parent_requested_cancellation"}
        try:
            args = json.loads(call["argumentsJson"])
        except (KeyError, ValueError, TypeError):
            return {**missing, "reason": "tool_call_evidence_incomplete"}
        if not isinstance(args, dict):
            return {**missing, "reason": "tool_call_evidence_incomplete"}
        if name == "check_agent_run" and is_true(argument(args, {"cancel"})):
            return {**failed, "failure_class": "cancellation", "reason": "parent_requested_cancellation"}
        if not isinstance(call.get("callId"), str) or not call["callId"]:
            return {**missing, "reason": "tool_call_evidence_incomplete"}
    text = snapshot.get("parent_decision")
    responses = snapshot.get("model_responses")
    if (text is None or not isinstance(responses, list) or not responses
            or len(responses) != snapshot["model_requests"]
            or any(not isinstance(r, dict) or not isinstance(r.get("text"), str)
                   or type(r.get("tool_calls")) is not bool or r.get("finished") is not True for r in responses)):
        return {**missing, "reason": "model_response_evidence_missing"}
    if responses[0]["text"] != text:
        return {**missing, "reason": "model_response_evidence_uncorrelated"}
    for response in responses:
        ids = response.get("call_ids")
        if (not isinstance(ids, list) or bool(ids) != response["tool_calls"]
                or any(not isinstance(call_id, str) or not call_id
                       or sum(c.get("callId") == call_id for c in calls) != 1 for call_id in ids)):
            return {**missing, "reason": "tool_call_evidence_incomplete"}
    for response in responses:
        if not response["text"] and response["tool_calls"]:
            return {**failed, "failure_class": "format", "reason": "checkpoint_tool_call_without_decision"}
        try:
            decision = json.loads(response["text"])
        except ValueError:
            return {**failed, "failure_class": "format", "reason": "public_output_not_json"}
        budget = decision.get("roundBudget") if isinstance(decision, dict) else None
        if (not isinstance(decision, dict) or set(decision) != {"runId", "completedRounds", "action", "roundBudget"}
                or not isinstance(decision["runId"], str) or type(decision["completedRounds"]) is not int
                or decision["action"] not in ("continue", "cancel", "escalate")
                or not isinstance(budget, dict) or set(budget) != {"value", "source"}
                or (budget["value"] is not None and type(budget["value"]) is not int)
                or budget["source"] not in ("runtime", "estimate", "none", "unknown")):
            return {**failed, "failure_class": "format", "reason": "public_output_contract_invalid"}
        if decision["roundBudget"] != {"value": 24, "source": "estimate"}:
            return {**failed, "failure_class": "budget", "reason": "round_budget_attribution_incorrect"}
        if decision["action"] == "cancel":
            return {**failed, "failure_class": "cancellation", "reason": "parent_decision_cancels"}
        if (decision["runId"] != run_id or decision["completedRounds"] != statuses[1]["completed_round"]
                or decision["action"] != "continue"):
            return {**failed, "failure_class": "decision", "reason": "checkpoint_decision_incorrect"}
    if any(r["tool_calls"] for r in responses):
        return {**failed, "failure_class": "scope", "reason": "checkpoint_requested_more_tools"}
    return {"passed": True, "status": "pass", "reason": "productive_child_continues"}


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("serve")
    sub.add_parser("prompt")
    check = sub.add_parser("check")
    check.add_argument("--snapshot", required=True)
    check.add_argument("--output", required=True)
    args = parser.parse_args()
    if args.command == "prompt":
        print(PROMPT)
        return 0
    if args.command == "check":
        try:
            result = verdict(json.loads(Path(args.snapshot).read_text()), json.loads(Path(args.output).read_text()))
        except (OSError, ValueError, TypeError, KeyError, AttributeError):
            result = {"passed": False, "status": "inconclusive", "reason": "invalid_progress_evidence"}
        print(json.dumps(result))
        return 0 if result["passed"] else 1
    fixture = ProgressFixture(os.environ["PROGRESS_EVAL_UPSTREAM"], os.environ["NETCLAW_EVAL_MODEL_ID"],
                              os.environ.get("NETCLAW_EVAL_PROVIDER_API_KEY", ""), os.environ["EVAL_HOME"])
    with ThreadingHTTPServer(("127.0.0.1", 0), handler_for(fixture)) as server:
        print(server.server_port, flush=True)
        server.serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())
