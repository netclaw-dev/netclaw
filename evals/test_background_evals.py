"""Regression tests for the eval oracle and the provider fixture."""

import json
import contextlib
import io
import os
import threading
import tempfile
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import unittest
from unittest.mock import patch
import urllib.request

from background_evals import HOLD, MARK, ModelFailure, Run, queue_verdict, verify_grants, wait_for_job_state
from background_fixture import Fixture, handler_for


class LaunchOracleTests(unittest.TestCase):
    def test_model_failure_is_a_failed_model_verdict_not_a_harness_error(self):
        with tempfile.TemporaryDirectory() as directory:
            cli = Path(directory) / "cli"
            cli.write_text("fixture")
            env = {"EVAL_HOME": directory, "TMPDIR_EVAL": directory, "NETCLAW_BIN": str(cli),
                   "PROMPT_TIMEOUT": "30", "RUNS": "1", "NETCLAW_EVAL_CASE": "queued_grant_revoked",
                   "BACKGROUND_EVAL_UPSTREAM": "http://127.0.0.1:1/v1"}
            with patch.dict(os.environ, env), patch.object(Run, "control", return_value={"model_requests": 1}), \
                    patch.object(Run, "queue", side_effect=ModelFailure("queued_grant_report", "No query.")):
                run = Run(1)
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(1, run.execute(runtime_only=False))
                self.assertEqual([], run.report["errors"])
                self.assertEqual([{"case": "queued_grant_report", "passed": False, "reason": "No query."}], run.report["model"])
                self.assertFalse(run.report["passed"])

    def test_revocation_requires_a_terminal_approval_denial(self):
        denied = {"status": "Failed", "exitCode": -1}
        reason = "Tool requires approval but no interactive approval requester is available: shell_execute"
        self.assertTrue(queue_verdict(denied, False, reason, True)["passed"])
        for job, notification in [({"status": "Pending"}, reason),
                                  ({"status": "TimedOut", "exitCode": -1}, reason),
                                  (denied, "Executable not found")]:
            with self.subTest(job=job, notification=notification), self.assertRaises(AssertionError):
                queue_verdict(job, False, notification, True)

    def test_any_observed_start_fails_the_revocation_case(self):
        for status, code in [("Completed", 0), ("Failed", -1), ("Cancelled", -1)]:
            result = queue_verdict({"status": status, "exitCode": code}, True,
                                   "Tool 'shell_execute' requires approval", True)
            self.assertFalse(result["passed"])

    def test_valid_grant_control_requires_a_successful_process(self):
        job = {"status": "Completed", "exitCode": 0}
        self.assertTrue(queue_verdict(job, True, "", False)["passed"])
        with self.assertRaises(AssertionError):
            queue_verdict(job, False, "", False)
        with self.assertRaises(AssertionError):
            queue_verdict({"status": "Failed", "exitCode": 1}, True, "", False)

    def test_job_observer_handles_atomic_replace_after_its_first_read(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "job.json"
            path.write_text("Pending")
            first_read = threading.Event()

            def replace():
                if not first_read.wait(2):
                    return
                update = Path(directory) / "update.tmp"
                update.write_text("Failed")
                update.replace(path)

            def terminal():
                value = path.read_text()
                first_read.set()
                return value == "Failed"

            thread = threading.Thread(target=replace)
            thread.start()
            try:
                wait_for_job_state(directory, terminal, timeout=2)
                self.assertEqual("Failed", path.read_text())
                with self.assertRaises(TimeoutError):
                    wait_for_job_state(directory, lambda: False, timeout=0)
            finally:
                thread.join(2)

    def test_grant_oracle_rejects_broader_or_missing_authority(self):
        entries = [{"shell": "Bash", "match": "TokenPrefix", "verbTokens": [p], "directory": None}
                   for p in [HOLD, MARK]]
        valid = {"version": 3, "audiences": {"personal": {"shell_execute": entries}}}
        verify_grants(valid, {HOLD, MARK})
        mutations = [lambda d: d["audiences"].update(public={"shell_execute": entries}),
                     lambda d: d["audiences"]["personal"]["shell_execute"].append(entries[0]),
                     lambda d: d["audiences"]["personal"]["shell_execute"].pop(),
                     lambda d: d["audiences"]["personal"]["shell_execute"][0].update(verbTokens=["python3"]),
                     lambda d: d["audiences"]["personal"]["shell_execute"][0].update(directory="/tmp")]
        for mutate in mutations:
            document = json.loads(json.dumps(valid))
            mutate(document)
            with self.subTest(document=document), self.assertRaises(AssertionError):
                verify_grants(document, {HOLD, MARK})


class ProviderFixtureTests(unittest.TestCase):
    def setUp(self):
        self.fixture = Fixture("http://127.0.0.1:1/v1", "fixture", "")

    def test_setup_waits_for_tool_ack_and_ignores_unrelated_turns(self):
        self.fixture.control("setup", {"nonce": "abc", "commands": ["first", "second"]})
        request = {"messages": [{"role": "user", "content": "Fixture setup abc"}],
                   "tools": [{"function": {"name": "shell_execute"}}]}
        first = self.fixture.completion(request)["tool_calls"][0]
        self.assertEqual(first, self.fixture.completion(request)["tool_calls"][0])
        request["messages"].append({"role": "tool", "tool_call_id": first["id"], "content": "job queued"})
        second = self.fixture.completion(request)["tool_calls"][0]
        self.assertEqual("second", json.loads(second["function"]["arguments"])["Command"])
        self.assertNotIn("tool_calls", self.fixture.completion({"messages": []}))

    def test_blockers_need_explicit_release_and_duplicate_callbacks_fail(self):
        entered = threading.Event()
        finished = threading.Event()

        def arrive():
            entered.set()
            self.fixture.arrive({"nonce": "abc", "slot": 0, "pid": 123}, hold=True)
            finished.set()

        thread = threading.Thread(target=arrive)
        thread.start()
        try:
            self.assertTrue(entered.wait(1))
            self.fixture.control("wait-ready", {"nonce": "abc", "slots": [0]})
            self.assertFalse(finished.is_set())
            with self.assertRaises(ValueError):
                self.fixture.arrive({"nonce": "abc", "slot": 0, "pid": 456}, hold=False)
        finally:
            self.fixture.control("release", {"nonce": "abc"})
            thread.join(2)
        self.assertTrue(finished.is_set())

    def test_fixture_emits_parseable_stream_and_nonstream_tool_calls(self):
        self.fixture.control("setup", {"nonce": "abc", "commands": ["fixture"]})
        with ThreadingHTTPServer(("127.0.0.1", 0), handler_for(self.fixture)) as server:
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            try:
                for stream in [False, True]:
                    body = {"messages": [{"role": "user", "content": "Fixture setup abc"}],
                            "tools": [{"function": {"name": "load_tool"}}], "stream": stream}
                    request = urllib.request.Request(f"http://127.0.0.1:{server.server_port}/v1/chat/completions",
                                                     data=json.dumps(body).encode())
                    with urllib.request.urlopen(request, timeout=2) as response:
                        text = response.read().decode()
                    if stream:
                        chunks = text.split("\n\n")
                        self.assertIn("data: [DONE]", chunks)
                        message = json.loads(chunks[0].removeprefix("data: "))["choices"][0]["delta"]
                    else:
                        message = json.loads(text)["choices"][0]["message"]
                    self.assertEqual("load_tool", message["tool_calls"][0]["function"]["name"])
            finally:
                server.shutdown()
                thread.join(2)

    def test_model_phase_relays_actual_request_and_response(self):
        received = []

        class Upstream(BaseHTTPRequestHandler):
            def log_message(self, *_):
                return

            def do_POST(self):
                received.append((self.path, self.headers.get("Authorization"),
                                 json.loads(self.rfile.read(int(self.headers["Content-Length"])))))
                body = b'data: {"upstream":true}\n\ndata: [DONE]\n\n'
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        with ThreadingHTTPServer(("127.0.0.1", 0), Upstream) as upstream:
            self.fixture.upstream = f"http://127.0.0.1:{upstream.server_port}/v1/chat/completions"
            self.fixture.api_key = "fixture-key"
            self.fixture.control("mode", {"mode": "model"})
            with ThreadingHTTPServer(("127.0.0.1", 0), handler_for(self.fixture)) as relay:
                threads = [threading.Thread(target=server.serve_forever) for server in [upstream, relay]]
                for thread in threads:
                    thread.start()
                body = {"model": "fixture-model", "messages": [{"role": "user", "content": "Inspect the job."}], "stream": True}
                try:
                    request = urllib.request.Request(f"http://127.0.0.1:{relay.server_port}/v1/chat/completions",
                                                     data=json.dumps(body).encode())
                    with urllib.request.urlopen(request, timeout=2) as response:
                        self.assertEqual(b'data: {"upstream":true}\n\ndata: [DONE]\n\n', response.read())
                    self.assertEqual([("/v1/chat/completions", "Bearer fixture-key", body)], received)
                    self.assertEqual(1, self.fixture.control("snapshot", {})["model_requests"])
                finally:
                    upstream.shutdown()
                    relay.shutdown()
                    for thread in threads:
                        thread.join(2)


if __name__ == "__main__":
    unittest.main()
