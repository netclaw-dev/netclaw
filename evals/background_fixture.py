#!/usr/bin/env python3
"""Loopback fixture for scripted setup and an OpenAI-compatible eval target."""

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import urllib.error
import urllib.request


def message_text(value):
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        return "\n".join(message_text(item) for item in value)
    if isinstance(value, dict):
        return message_text(value.get("text", ""))
    return ""


class Fixture:
    def __init__(self, upstream, model, api_key):
        self.upstream = upstream.rstrip("/") + "/chat/completions"
        self.model = model
        self.api_key = api_key
        self.condition = threading.Condition()
        self.mode = "quiet"
        self.nonce = ""
        self.commands = []
        self.background = True
        self.ready = {}
        self.released = set()
        self.model_requests = 0
        self.tool_results = {}

    def control(self, action, data):
        with self.condition:
            if action == "setup":
                self.mode = "setup"
                self.nonce = data["nonce"]
                self.commands = data["commands"]
                self.background = data.get("background", True)
            elif action == "mode":
                if data["mode"] not in {"model", "quiet"}:
                    raise ValueError("Unknown fixture mode.")
                self.mode = data["mode"]
            elif action == "release":
                self.released.add(data["nonce"])
                self.condition.notify_all()
            elif action == "wait-ready":
                keys = {f'{data["nonce"]}-{slot}' for slot in data["slots"]}
                if not self.condition.wait_for(lambda: keys <= self.ready.keys(), timeout=30):
                    raise TimeoutError("The fixture processes did not reach their barriers.")
            elif action != "snapshot":
                raise ValueError("Unknown fixture control.")
            return {"ready": self.ready.copy(),
                    "model_requests": self.model_requests, "tool_results": self.tool_results.copy()}

    def arrive(self, data, hold):
        key = f'{data["nonce"]}-{data["slot"]}'
        with self.condition:
            if key in self.ready:
                raise ValueError("A fixture process started more than once.")
            self.ready[key] = data["pid"]
            self.condition.notify_all()
            if hold and not self.condition.wait_for(
                    lambda: data["nonce"] in self.released, timeout=600):
                raise TimeoutError("The harness did not release the fixture process.")

    def completion(self, request):
        with self.condition:
            for message in request.get("messages", []):
                text = message_text(message.get("content"))
                if message.get("role") == "tool":
                    self.tool_results[message["tool_call_id"]] = text
            if self.mode == "model":
                self.model_requests += 1
                return None
            if self.mode == "quiet":
                return {"role": "assistant", "content": "Fixture acknowledged."}
            marker = f"Fixture setup {self.nonce}"
            messages = request.get("messages", [])
            if not any(marker in message_text(m.get("content")) for m in messages if m.get("role") == "user"):
                return {"role": "assistant", "content": "Fixture acknowledged."}
            tools = {t.get("function", {}).get("name") for t in request.get("tools", [])}
            if "shell_execute" not in tools:
                if "load_tool" not in tools:
                    raise ValueError("The daemon exposed neither shell_execute nor load_tool.")
                return self.tool_call("load", "load_tool", {"Name": "shell_execute"})
            acknowledgements = {m.get("tool_call_id") for m in messages if m.get("role") == "tool"}
            for index, command in enumerate(self.commands):
                key = f"job-{index}"
                if f"fixture-{self.nonce}-{key}" not in acknowledgements:
                    return self.tool_call(key, "shell_execute", {
                        "Command": command, "WorkingDirectory": "/home/netclaw/.netclaw/workspaces",
                        "_background": self.background, "_timeout_seconds": 600})
            return {"role": "assistant", "content": "Fixture setup complete."}

    def tool_call(self, key, name, arguments):
        return {"role": "assistant", "content": None, "tool_calls": [{
            "id": f"fixture-{self.nonce}-{key}", "type": "function",
            "function": {"name": name, "arguments": json.dumps({
                **arguments, "_rationale": "Prepare the isolated background eval."})}}]}


def handler_for(fixture):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *_):
            # The standard handler logs request URLs. Provider details do not belong in evidence.
            return

        def send_json(self, status, body):
            content = json.dumps(body).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)

        def do_GET(self):
            if self.path == "/v1/models":
                self.send_json(200, {"object": "list", "data": [
                    {"id": fixture.model, "object": "model", "owned_by": "eval"}]})
            elif self.path == "/health":
                self.send_json(200, {"status": "ok"})
            else:
                self.send_json(404, {"error": "Unknown fixture route."})

        def do_POST(self):
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 16 * 1024 * 1024:
                    raise ValueError("Invalid request length.")
                body = json.loads(self.rfile.read(length))
                if self.path.startswith("/control/"):
                    self.send_json(200, fixture.control(self.path.removeprefix("/control/"), body))
                elif self.path in {"/fixture/hold", "/fixture/mark"}:
                    fixture.arrive(body, self.path.endswith("hold"))
                    self.send_json(200, {"released": True})
                elif self.path == "/v1/chat/completions":
                    message = fixture.completion(body)
                    if message is None:
                        self.forward(body)
                    else:
                        self.reply(body, message)
                else:
                    self.send_json(404, {"error": "Unknown fixture route."})
            except (ValueError, KeyError, TimeoutError) as error:
                self.send_json(400, {"error": str(error)})

        def reply(self, request, message):
            finish = "tool_calls" if message.get("tool_calls") else "stop"
            base = {"id": "fixture-completion", "created": 0, "model": fixture.model}
            if not request.get("stream"):
                self.send_json(200, {**base, "object": "chat.completion", "choices": [
                    {"index": 0, "message": message, "finish_reason": finish}]})
                return
            delta = dict(message)
            if delta.get("tool_calls"):
                delta["tool_calls"] = [{"index": i, **call} for i, call in enumerate(delta["tool_calls"])]
            chunks = [
                {"index": 0, "delta": delta, "finish_reason": None},
                {"index": 0, "delta": {}, "finish_reason": finish}]
            content = "".join("data: " + json.dumps({**base, "object": "chat.completion.chunk",
                                                     "choices": [chunk]}) + "\n\n" for chunk in chunks)
            content = (content + "data: [DONE]\n\n").encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)

        def forward(self, body):
            headers = {"Content-Type": "application/json"}
            if fixture.api_key:
                headers["Authorization"] = "Bearer " + fixture.api_key
            request = urllib.request.Request(fixture.upstream, data=json.dumps(body).encode(), headers=headers)
            try:
                response = urllib.request.urlopen(request, timeout=300)
            except (urllib.error.URLError, TimeoutError):
                self.send_json(502, {"error": "The configured eval provider request failed."})
                return
            with response:
                self.send_response(response.status)
                self.send_header("Content-Type", response.headers.get("Content-Type", "application/json"))
                self.send_header("Connection", "close")
                self.end_headers()
                self.close_connection = True
                while chunk := response.read1(65536):
                    self.wfile.write(chunk)
                    self.wfile.flush()

    return Handler
