"""Keep source-shaped title requests separate from process evidence."""

import copy
import io
import json
from pathlib import Path
import tempfile
import unittest

import background_process_fixture as runtime
import test_background_process_evals as controls


TITLE_PREFIX = ("Generate a short title (8 words or fewer) for this conversation. "
                "Reply with only the title — no quotes, no punctuation, no explanation.\n\n")


def title_body():
    return {"model": runtime.MODEL, "stream": True, "messages": [{"role": "user", "content":
        TITLE_PREFIX + "**User:** Start the assigned child.\n**Assistant:** The child is active.\n"}]}


def assigned(directory):
    fixture = runtime.ProcessFixture(Path(directory) / "relay")
    fixture.control("child-setup", {"case": "child_run_routed_skill", "nonce": "title-control",
                                   "container_id": "owned-container"})
    return fixture


class AuxiliaryTitleControls(unittest.TestCase):
    def test_source_title_shape_passes_with_absent_null_or_empty_tools(self):
        for tools in ["absent", None, []]:
            request = title_body()
            if tools != "absent":
                request["tools"] = tools
            self.assertTrue(runtime.title_request(request))
        for role in ["User", "Assistant", "Tool"]:
            request["messages"][0]["content"] = TITLE_PREFIX + "**" + role + ":** Actual context\n"
            self.assertTrue(runtime.title_request(request))

    def test_tools_extra_messages_and_non_user_roles_reject_title_classification(self):
        for fault in ["tools", "extra-user", "extra-system", "role", "call", "content-parts"]:
            with self.subTest(fault=fault):
                request = title_body()
                if fault == "tools":
                    request["tools"] = [{"type": "function", "function": {"name": "spawn_agent"}}]
                elif fault.startswith("extra"):
                    request["messages"].append({"role": fault.split("-")[1], "content": "another message"})
                elif fault == "role":
                    request["messages"][0]["role"] = "system"
                elif fault == "call":
                    request["messages"][0]["tool_calls"] = []
                else:
                    request["messages"][0]["content"] = [{"type": "text", "text": request["messages"][0]["content"]}]
                self.assertFalse(runtime.title_request(request))

    def test_changed_instruction_or_role_envelope_cannot_bypass_task_gates(self):
        for content in [TITLE_PREFIX.replace("8 words", "80 words") + "**User:** Actual context\n",
                        " " + TITLE_PREFIX + "**User:** Actual context\n",
                        TITLE_PREFIX + "User: Actual context\n", TITLE_PREFIX + "**System:** Actual context\n",
                        TITLE_PREFIX + "**User:** Actual context", TITLE_PREFIX]:
            with self.subTest(content=content), tempfile.TemporaryDirectory() as directory:
                request = title_body()
                request["messages"][0]["content"] = content
                self.assertFalse(runtime.title_request(request))
                fixture = assigned(directory)
                with self.assertRaises(AssertionError):
                    fixture.completion(request)
                self.assertEqual([], fixture.auxiliary_titles)

    def test_ordinary_task_cannot_substitute_for_a_title_envelope(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = assigned(directory)
            initial, _, _ = runtime.prompts(fixture.case, fixture.nonce)
            request = {"model": runtime.MODEL, "messages": [{"role": "user", "content": initial}],
                       "tools": [{"type": "function", "function": {"name": "skill_load"}}]}
            self.assertFalse(runtime.title_request(request))
            answer = fixture.completion(request)
            self.assertEqual("skill_load", answer["tool_calls"][0]["function"]["name"])
            self.assertEqual([], fixture.auxiliary_titles)
            self.assertEqual([1], [row["request_id"] for row in fixture.records])

    def test_title_requests_do_not_allocate_task_ids_or_release_a_child(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = assigned(directory)
            for _ in range(2):
                answer = fixture.completion(title_body())
                self.assertEqual("Process child contract", answer["content"])
                self.assertNotIn("tool_calls", answer)
            snapshot = fixture.snapshot()
            self.assertEqual([], snapshot["requests"])
            self.assertEqual([1, 2], [row["request_id"] for row in snapshot["auxiliary_titles"]])
            self.assertIsNone(snapshot["candidate"])
            self.assertIsNone(snapshot["binding"])
            self.assertFalse(snapshot["released"])
            self.assertFalse(snapshot["recovery_released"])
            initial, _, _ = runtime.prompts(fixture.case, fixture.nonce)
            fixture.completion({"model": runtime.MODEL, "messages": [{"role": "user", "content": initial}],
                "tools": [{"type": "function", "function": {"name": "skill_load"}}]})
            self.assertEqual([1], [row["request_id"] for row in fixture.records])
            self.assertEqual(["request-0001.json"], sorted(path.name for path in fixture.evidence.glob("request-*.json")))

    def test_real_reply_serializer_retains_only_auxiliary_raw_bytes(self):
        for stream in [False, True]:
            with self.subTest(stream=stream), tempfile.TemporaryDirectory() as directory:
                fixture = assigned(directory)
                request = title_body()
                request["stream"] = stream
                message = fixture.completion(request)
                handler = runtime.process_handler(fixture).__new__(runtime.process_handler(fixture))
                handler.request_version = "HTTP/1.1"
                handler.requestline = "POST /v1/chat/completions HTTP/1.1"
                handler.command = "POST"
                handler.wfile = io.BytesIO()
                handler.request_bytes = json.dumps(request, indent=2).encode()
                handler.reply(request, message)
                self.assertEqual(handler.request_bytes, (fixture.evidence / "title-request-0001.body").read_bytes())
                wire = (fixture.evidence / "title-response-0001.wire").read_bytes()
                self.assertEqual(handler.wfile.getvalue(), wire)
                self.assertIn(b"Process child contract", wire)
                self.assertTrue(fixture.auxiliary_titles[0]["response_payload_written"])
                self.assertEqual([], fixture.records)
                self.assertFalse((fixture.evidence / "response-0001.wire").exists())

    def test_auxiliary_records_earn_no_input_or_call_credit(self):
        record = controls.valid_routed()
        record["snapshot"]["auxiliary_titles"] = [{"request_id": 1, "request": title_body(), "auxiliary_title": True}]
        self.assertTrue(controls.oracle.verify_process_trial(**record)["passed"])
        self.assertEqual(2, record["receipt"]["user_inputs"])
        for count in [1, 3]:
            invalid = copy.deepcopy(record)
            invalid["receipt"]["user_inputs"] = count
            controls.ProcessEvidenceControls.rejects(self, invalid)
        missing_task = copy.deepcopy(record)
        expected = missing_task["setup"]["parent_inputs"][1]
        for request in missing_task["requests"]:
            request["messages"] = [message for message in request["messages"]
                if not (message.get("role") == "user" and message.get("content") == expected)]
        controls.ProcessEvidenceControls.rejects(self, missing_task)
        extra_input = copy.deepcopy(record)
        extra_input["requests"][-1]["messages"].append(title_body()["messages"][0])
        controls.ProcessEvidenceControls.rejects(self, extra_input)


if __name__ == "__main__":
    unittest.main()
