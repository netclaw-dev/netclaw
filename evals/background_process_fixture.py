"""Supply four fixed scripted child flows without an upstream provider."""

import json
import argparse
from contextlib import closing
import hashlib
import os
from pathlib import Path
import re
import subprocess
import sqlite3
import threading
import time
import uuid
from http.server import ThreadingHTTPServer

from background_fixture import handler_for, message_text
from background_process_evals import CASES, RATIONALE
from child_run_evals import (CHILD_CONTRACT, ChildFixture, acceptance, bind_request,
                             context_paths, parent_call_pairs, require, write_completed,
                             control, evidence_requests, invoke_observer, observer_command, session_logs,
                             verify_partial_report)
from background_process_evals import verify_process_trial
from coordination_workflow_evals import archive_read


MODEL = "background-process-fixture"
PROFILE = "process-worker"
SKILL = "process-routed-skill"
GRANTS = "/home/netclaw/.netclaw/config/tool-approvals.json"
EXECUTABLE = "/home/netclaw/.netclaw/evals/process_marker"
TITLE_INSTRUCTION = ("Generate a short title (8 words or fewer) for this conversation. "
                     "Reply with only the title — no quotes, no punctuation, no explanation.\n\n")


def title_request(request):
    messages = request.get("messages")
    if request.get("tools") not in (None, []) or not isinstance(messages, list) or len(messages) != 1:
        return False
    message = messages[0]
    if not isinstance(message, dict) or message.get("role") != "user" or set(message) != {"role", "content"}:
        return False
    content = message["content"]
    if not isinstance(content, str) or not content.startswith(TITLE_INSTRUCTION):
        return False
    conversation = content[len(TITLE_INSTRUCTION):]
    return conversation.endswith("\n") and any(conversation.startswith("**" + role + ":** ")
                                               for role in ("User", "Assistant", "Tool"))


def prompts(case, nonce):
    require(case in CASES, "The process case is unknown.")
    partial = case in {"child_run_owner_recovery", "child_run_approval_cancel"}
    kind, content = ("partial", "PARTIAL-") if partial else ("complete", "COMPLETE-")
    task = (f"Trial {nonce}. Write exactly {content + nonce}, without a newline, to {kind}-{nonce}.txt "
            "in your runtime artifact_dir. Return the full artifact path and exact content.")
    if case in {"child_run_approval_once", "child_run_approval_cancel"}:
        task += f" Run only {EXECUTABLE} with your artifact_dir/effect-{nonce}.txt and {nonce} as its two arguments."
    else:
        task += " Use file_write. Use no shell command."
    initial = (f"Trial {nonce}. Start one {PROFILE} child for this assignment: {task} "
               + (f"Use skill_load with Name={SKILL}, Task equal to the assignment, and Context={overlay(nonce)}. "
                  if case == "child_run_routed_skill" else "Use spawn_agent. ")
               + "Load check_agent_run. Check the accepted RunId once with Cancel=false. "
               "Reply immediately after the status result. Do not wait for the child. "
               "When the terminal arrives, fully read the confirmed artifact and report its exact content.")
    probe = ("For accepted child {{RUN_ID}}, read its actual log path from the prior status with file_read. "
             + ("Then cancel that exact child with check_agent_run and Cancel=true. "
                if case == "child_run_approval_cancel" else "Keep the child active. ")
             + f"Reply with PARENT-PROBE-{nonce} and one actual log fact.")
    return initial, probe, task


def overlay(nonce):
    return "PROCESS-ROUTED-OVERLAY-" + nonce


class ProcessFixture(ChildFixture):
    def __init__(self, evidence):
        super().__init__("", MODEL, "", evidence)
        self.upstream = None
        self.effects = {}
        self.approval_prompt = None
        self.approval_answer = None
        self.protected_result_request_id = None
        self.crash = None
        self.crash_pending = False
        self.recovery_released = False
        self.container_id = None
        self.auxiliary_titles = []

    def completion(self, request):
        require(request.get("model") == MODEL, "The process fixture received another model.")
        require(self.case in CASES and self.nonce, "The process fixture has no assigned case.")
        if title_request(request):
            with self.condition:
                number = len(self.auxiliary_titles) + 1
                self.auxiliary_titles.append({"request_id": number, "request": request, "auxiliary_title": True,
                    "child": False, "held": False, "forward_complete": False, "upstream_first_payload_ns": 0,
                    "response_first_payload_ns": 0, "response_payload_written": False})
            return {"role": "assistant", "content": "Process child contract"}
        paths = context_paths(request)
        system = "\n".join(message_text(row.get("content")) for row in request["messages"] if row.get("role") == "system")
        require(CHILD_CONTRACT not in system or paths is not None, "A child request lacks canonical storage context.")
        pairs = parent_call_pairs(request)
        partial = self.case in {"child_run_owner_recovery", "child_run_approval_cancel"}
        with self.condition:
            number = len(self.records) + 1
            hold = paths is not None and self.candidate is None
            if partial:
                hold = hold and write_completed(request, f"partial-{self.nonce}.txt") is not None
            row = {"request_id": number, "admitted_ns": time.monotonic_ns(), "child": paths is not None,
                   "held": hold, "forward_complete": False, "upstream_first_payload_ns": 0,
                   "response_first_payload_ns": 0, "response_payload_written": False, "request": request}
            self.records.append(row)
            (self.evidence / f"request-{number:04}.json").write_bytes(json.dumps(request).encode())
            if hold:
                self.candidate = row
            if paths is not None:
                shell_results = [pair for pair in pairs if pair[1] == "shell_execute"]
                if shell_results:
                    require(len(shell_results) == 1, "The protected child result repeats in one history.")
                    if self.protected_result_request_id is None:
                        self.protected_result_request_id = number
        return self.child_response(request, pairs, paths) if paths is not None else self.parent_response(request, pairs)

    def step(self, request, pairs, key, name, arguments):
        identifier = f"fixture-{self.nonce}-{key}"
        existing = [pair for pair in pairs if pair[0] == identifier]
        require(len(existing) <= 1, "The scripted process call repeats inside one history.")
        if existing:
            pair = existing[0]
            expected = {**arguments, "_rationale": RATIONALE}
            actual = {k: v for k, v in pair[2].items() if k not in {"_background", "_timeout_seconds"}}
            require(pair[1] == name and actual == expected, "The scripted process call changed.")
            return None, pair[3]
        tools = {tool["function"]["name"] for tool in request.get("tools", [])}
        if name not in tools:
            require(name != "load_tool" and "load_tool" in tools, "The requested process tool is unavailable.")
            response, result = self.step(request, pairs, "load-" + name, "load_tool", {"Name": name})
            require(response is not None, "The loaded process tool remains absent from the actual catalog.")
            return response, None
        return {"role": "assistant", "content": None, "tool_calls": [{"id": identifier, "type": "function",
            "function": {"name": name, "arguments": json.dumps({**arguments,
                "_rationale": RATIONALE})}}]}, None

    def child_response(self, request, pairs, paths):
        partial = self.case in {"child_run_owner_recovery", "child_run_approval_cancel"}
        kind, prefix = ("partial", "PARTIAL-") if partial else ("complete", "COMPLETE-")
        artifact = paths["artifact_dir"] + f"/{kind}-{self.nonce}.txt"
        content = prefix + self.nonce
        # The approval positive reaches its real prompt before any task effect.
        if partial or self.case == "child_run_routed_skill":
            response, result = self.step(request, pairs, "artifact", "file_write", {"Path": artifact, "Content": content})
            if response:
                return response
            require(result == f"Successfully wrote {len(content.encode())} bytes to {artifact}", "The actual artifact write failed.")
        if self.case in {"child_run_approval_once", "child_run_approval_cancel"}:
            effect = paths["artifact_dir"] + f"/effect-{self.nonce}.txt"
            arguments = {"Command": f"{EXECUTABLE} {effect} {self.nonce}", "WorkingDirectory": paths["session_dir"]}
            response, result = self.step(request, pairs, "protected", "shell_execute", arguments)
            if response:
                return response
            require(result == "Exit code: 0\n" + self.nonce + "\n\n[approval: once]",
                    "The protected command lacks its exact approved result.")
            if not partial:
                response, result = self.step(request, pairs, "artifact", "file_write", {"Path": artifact, "Content": content})
                if response:
                    return response
                require(result == f"Successfully wrote {len(content.encode())} bytes to {artifact}", "The actual artifact write failed.")
        return {"role": "assistant", "content": artifact + "\n" + content}

    def parent_response(self, request, pairs):
        initial, probe, task = prompts(self.case, self.nonce)
        users = [message_text(row.get("content")) for row in request["messages"] if row.get("role") == "user"]
        require(initial in users, "The actual parent lacks the fixed process prompt.")
        operation = "skill_load" if self.case == "child_run_routed_skill" else "spawn_agent"
        arguments = {"Name": SKILL, "Task": task, "Context": overlay(self.nonce)} if operation == "skill_load" else {"Agent": PROFILE, "Task": task}
        response, result = self.step(request, pairs, "start", operation, arguments)
        if response:
            return response
        accepted = acceptance(result)
        terminals = [json.loads(pair[3]) for pair in pairs if pair[2] == {
            "run_id": accepted["run_id"], "source_operation": operation} and pair[1] == operation]
        if terminals:
            require(len(terminals) == 1, "The parent received multiple child terminals.")
            terminal = terminals[0]
            partial = self.case in {"child_run_owner_recovery", "child_run_approval_cancel"}
            filename = ("partial-" if partial else "complete-") + self.nonce + ".txt"
            content = ("PARTIAL-" if partial else "COMPLETE-") + self.nonce
            response, body = self.step(request, pairs, "review", "file_read", {"Path": terminal["artifact_directory"] + "/" + filename})
            if response:
                return response
            require(body == content, "The parent read differs from the actual artifact.")
            if self.case == "child_run_approval_cancel":
                report_path = terminal["artifact_directory"] + "/cancelled-results.json"
                response, report = self.step(request, pairs, "partial-report", "file_read", {"Path": report_path})
                if response:
                    return response
                require(json.loads(report)["run_id"] == accepted["run_id"], "The actual partial report has another owner.")
                response, final_status = self.step(request, pairs, "terminal-status", "check_agent_run",
                                                   {"RunId": accepted["run_id"], "Cancel": False})
                if response:
                    return response
                state = json.loads(final_status)
                require(state["state"] == "Cancelled" and state["dispatch_closed"] is True,
                        "Cancellation lacks terminal status and dispatch closure.")
            return {"role": "assistant", "content": content + "; " + terminal["state"]}
        response, status_text = self.step(request, pairs, "status", "check_agent_run", {"RunId": accepted["run_id"], "Cancel": False})
        if response:
            return response
        status = json.loads(status_text)
        if probe.replace("{{RUN_ID}}", accepted["run_id"]) not in users:
            return {"role": "assistant", "content": "Accepted the child. Its result will arrive later."}
        response, log = self.step(request, pairs, "log", "file_read", {"Path": status["log_path"]})
        if response:
            return response
        require(any(len(line) >= 16 for line in log.splitlines()), "The actual child log has no visible line.")
        if self.case == "child_run_approval_cancel":
            response, _ = self.step(request, pairs, "cancel", "check_agent_run", {"RunId": accepted["run_id"], "Cancel": True})
            if response:
                return response
        return {"role": "assistant", "content": "PARENT-PROBE-" + self.nonce + ": " + next(line for line in log.splitlines() if len(line) >= 16)}

    def control(self, action, data):
        with self.condition:
            if action == "child-setup":
                require(not self.records and data["case"] in CASES, "The process fixture is not fresh.")
                self.nonce, self.case = data["nonce"], data["case"]
                self.container_id = data["container_id"]
                return self.snapshot()
            if action == "approval-open":
                require(self.approval_prompt is None and self.release and self.binding, "The prompt barrier repeats or precedes child release.")
                self.approval_prompt = data
                path = self.binding["paths"]["artifact_dir"] + f"/effect-{self.nonce}.txt"
                self.effects["before_answer"] = {path: archive_read(os.environ["EVAL_HOME"], path)}
                self.effects["grants_before"] = archive_read(os.environ["EVAL_HOME"], GRANTS)
                require(self.effects["before_answer"][path] is None, "The protected effect precedes the prompt.")
                require(self.effects["grants_before"] is not None, "The original approval store is absent.")
                (self.evidence.parent / "grants-before.bin").write_bytes(self.effects["grants_before"])
                (self.evidence.parent / "before-answer.json").write_text(json.dumps({"effect_path": path, "exists": False}))
                return self.snapshot()
            if action in {"approval-answer-ready", "stale-answer-ready"}:
                require(self.approval_prompt is not None and self.approval_answer is None
                        and data["call_id"] == self.approval_prompt["prompt"]["callId"]
                        and data["prompt_sequence"] == self.approval_prompt["prompt_sequence"], "The answer barrier names another prompt.")
                path = self.binding["paths"]["artifact_dir"] + f"/effect-{self.nonce}.txt"
                require(archive_read(os.environ["EVAL_HOME"], path) is None, "The protected effect precedes its answer.")
                self.approval_answer = {**data, "request_id": len(self.records), "effect_absent": True,
                                        "admitted_ns": time.monotonic_ns()}
                return self.snapshot()
            if action == "daemon-crash":
                require(self.case == "child_run_owner_recovery" and self.binding and self.crash is None,
                        "The abrupt crash lacks one held recovery case.")
                require(data["accepted"] == self.binding["accepted"], "The crash names another child.")
                artifact = self.binding["paths"]["artifact_dir"] + f"/partial-{self.nonce}.txt"
                require(archive_read(os.environ["EVAL_HOME"], artifact) == ("PARTIAL-" + self.nonce).encode(),
                        "The crash lacks actual confirmed partial bytes.")
                self.crash_pending = True
                self.crash = crash_daemon(data["session_id"], self.container_id)
                return self.snapshot()
            if action == "recovery-release":
                require(self.crash and self.crash["session_id"] == data["session_id"] and not self.recovery_released
                        and type(data["joined_sequence"]) is int and data["joined_sequence"] > 0,
                        "Recovery release lacks its same-session subscriber join.")
                self.recovery_released = True
                self.crash["new_ready"] = True
                self.condition.notify_all()
                return self.snapshot()
        result = super().control(action, data)
        if action == "child-bind":
            paths = result["binding"]["paths"]
            deadline = time.monotonic() + 30
            with self.condition:
                while True:
                    contents = archive_read(os.environ["EVAL_HOME"], paths["log_path"])
                    if contents and any(len(line) >= 16 for line in contents.decode().splitlines()):
                        break
                    remaining = deadline - time.monotonic()
                    require(remaining > 0, "The actual child log remained empty at its visibility deadline.")
                    self.condition.wait(min(0.1, remaining))
        return result

    def snapshot(self):
        return {**super().snapshot(), "approval_prompt": self.approval_prompt, "approval_answer": self.approval_answer,
                "protected_result_request_id": self.protected_result_request_id, "crash": self.crash,
                "recovery_released": self.recovery_released, "auxiliary_titles": self.auxiliary_titles}


def crash_daemon(session, expected_container_id):
    container = os.environ["EVAL_CONTAINER_NAME"]
    require(container.startswith("netclaw-eval-"), "The crash target lacks its eval container namespace.")
    identifier = subprocess.check_output(["docker", "inspect", "--format", "{{.Id}}", container], text=True).strip()
    require(identifier == expected_container_id, "The owned crash container identity changed.")
    def command(*args):
        return subprocess.check_output(["docker", "exec", "--user", "root", container, *args], timeout=10)
    def identity():
        pids = command("pgrep", "-x", "netclawd").decode().split()
        require(len(pids) == 1 and pids[0].isdigit(), "The owned container lacks exactly one daemon PID.")
        pid = int(pids[0])
        stat = command("cat", f"/proc/{pid}/stat").decode()
        return pid, stat.rsplit(")", 1)[1].split()[19]
    old_pid, old_start = identity()
    command("kill", "-KILL", str(old_pid))
    deadline = time.monotonic() + 40
    changed = threading.Condition()
    while True:
        try:
            new_pid, new_start = identity()
            if (new_pid, new_start) != (old_pid, old_start):
                break
        except subprocess.CalledProcessError:
            pass
        require(time.monotonic() < deadline, "The owned daemon supervisor did not replace the killed process.")
        with changed:
            changed.wait(min(0.1, deadline - time.monotonic()))
    # Resume supplies the actual readiness and same-session attachment gate.
    return {"session_id": session, "signal": "SIGKILL", "old_pid": old_pid, "old_start": old_start,
            "container_id": identifier, "old_exited": True, "new_pid": new_pid, "new_start": new_start, "new_ready": False}


def process_handler(fixture):
    class Handler(handler_for(fixture)):
        def do_POST(self):
            original = self.rfile
            handler = self
            class Capture:
                def read(self, count):
                    handler.request_bytes = original.read(count)
                    return handler.request_bytes
            self.rfile = Capture()
            try:
                super().do_POST()
            finally:
                self.rfile = original

        def forward(self, body):
            raise AssertionError("The scripted process fixture has no inference forward route.")

        def reply(self, request, message):
            row = next(row for row in fixture.records + fixture.auxiliary_titles if row["request"] is request)
            original = self.wfile
            prefix = "title-" if row.get("auxiliary_title") else ""
            if prefix:
                (fixture.evidence / f'title-request-{row["request_id"]:04}.body').write_bytes(self.request_bytes)
            capture = fixture.evidence / f'{prefix}response-{row["request_id"]:04}.wire'
            with capture.open("xb") as stream:
                class Tee:
                    writes = 0
                    def write(self, value):
                        stream.write(value)
                        stream.flush()
                        self.writes += 1
                        first = self.writes > 1 and value and not row["upstream_first_payload_ns"]
                        if first:
                            with fixture.condition:
                                row["upstream_first_payload_ns"] = time.monotonic_ns()
                                fixture.condition.notify_all()
                                if row["held"]:
                                    require(fixture.condition.wait_for(lambda: fixture.release or fixture.abort, timeout=180),
                                            "The process child response was not released.")
                                    require(not fixture.abort, "The process trial aborted its held response.")
                                if fixture.case == "child_run_owner_recovery" and not row["child"] and not prefix and fixture.crash_pending:
                                    require(fixture.condition.wait_for(lambda: fixture.recovery_released or fixture.abort, timeout=40),
                                            "The recovered parent lacks its subscriber release.")
                                row["response_first_payload_ns"] = time.monotonic_ns()
                        result = original.write(value)
                        if first:
                            row["response_payload_written"] = True
                        return result
                    def flush(self):
                        return original.flush()
                self.wfile = Tee()
                try:
                    super().reply(request, message)
                finally:
                    self.wfile = original
                    row["forward_complete"] = True
                    with fixture.condition:
                        fixture.condition.notify_all()
    return Handler


def prepare():
    case = os.environ["NETCLAW_EVAL_CASE"]
    require(case in CASES and os.environ["RUNS"] == "1", "Select one fresh process case with RUNS=1.")
    root = Path(os.environ["TMPDIR_EVAL"]) / "child-runs"
    root.mkdir(parents=True, exist_ok=True)
    require(not (root / "setup-base.json").exists(), "The process fixture already has a prepared assignment.")
    nonce = uuid.uuid4().hex
    data = Path(os.environ["EVAL_HOME"]) / "data"
    agents = data / "agents"
    agents.mkdir(parents=True, exist_ok=True)
    (agents / (PROFILE + ".md")).write_text(
        "---\nname: process-worker\ndescription: Fixed local process eval worker.\nmodelRole: Main\n---\n"
        "Use only the assigned file and command operations.\nUse the runtime artifact directory.\n"
        "Do not start another agent or job.\nReturn the actual artifact path and content.\n")
    skill = Path(os.environ["EVAL_HOME"]) / "skills" / SKILL
    skill.mkdir(parents=True, exist_ok=False)
    skill.joinpath("SKILL.md").write_text(
        "---\nname: process-routed-skill\ndescription: Fixed routed process eval.\nmetadata:\n"
        "  version: \"1.0.0\"\n  subagent: process-worker\n---\n\n" + overlay(nonce) + "\n")
    executable = data / "evals" / "process_marker"
    executable.parent.mkdir(parents=True, exist_ok=True)
    executable.write_bytes(Path(__file__).parent.joinpath("fixtures/child-runs/process_marker.py").read_bytes())
    executable.chmod(0o755)
    (root / "setup-base.json").write_text(json.dumps({"case": case, "nonce": nonce,
        "marker_sha256": hashlib.sha256(executable.read_bytes()).hexdigest()}, indent=2))


def read_lines(path):
    return [json.loads(line) for line in path.read_text().splitlines() if line]


def capture_journal(root, session):
    database = Path(os.environ["EVAL_HOME"]) / "data/netclaw.db"
    retained = root / "journal-backup.sqlite"
    require(database.is_file() and not retained.exists(), "The owned live journal or fresh backup path differs.")
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as source:
        with closing(sqlite3.connect(retained)) as destination:
            source.backup(destination)
    command = observer_command() + ["--project-process-journal", str(retained), session, str(root / "journal.json")]
    result = subprocess.run(command, capture_output=True, timeout=40)
    (root / "journal-projector.stdout").write_bytes(result.stdout)
    (root / "journal-projector.stderr").write_bytes(result.stderr)
    (root / "journal-projector-command.json").write_text(json.dumps({"command": command, "exit_code": result.returncode}))
    require(result.returncode == 0, "The canonical journal decoder failed; inspect its retained outputs.")
    return json.loads((root / "journal.json").read_text())


def run(port):
    require(os.environ["RUNS"] == "1", "Each process case requires one fresh harness invocation.")
    root = Path(os.environ["TMPDIR_EVAL"]) / "child-runs"
    base = json.loads((root / "setup-base.json").read_text())
    case, nonce = base["case"], base["nonce"]
    require(case == os.environ["NETCLAW_EVAL_CASE"], "The prepared process case changed.")
    identifier = subprocess.check_output(["docker", "inspect", "--format", "{{.Id}}",
        os.environ["EVAL_CONTAINER_NAME"]], text=True).strip()
    control(port, "child-setup", case=case, nonce=nonce, container_id=identifier)
    initial, probe, task = prompts(case, nonce)
    modes = {"child_run_routed_skill": "process-routed", "child_run_approval_once": "process-approval",
             "child_run_owner_recovery": "process-recovery", "child_run_approval_cancel": "process-approval-cancel"}
    report = {**base, "passed": False, "fresh_invocation": True, "container_id": identifier,
              "container_name": os.environ["EVAL_CONTAINER_NAME"], "home": os.environ["EVAL_HOME"],
              "image_reference": os.environ["NETCLAW_IMAGE"], "zero_forward_route": True}
    error = None
    try:
        receipt, _ = invoke_observer(port, initial, root / "observer", modes[case], nonce, probe)
        report["session_id"] = receipt["session_id"]
        snapshot = control(port, "snapshot")
        snapshot["response_wires"] = [{"request_id": int(path.stem.split("-")[1]), "wire": path.read_bytes().decode()}
                                      for path in sorted((root / "relay").glob("response-*.wire"))]
        (root / "fixture-snapshot.json").write_text(json.dumps(snapshot, indent=2))
        journal = capture_journal(root, receipt["session_id"])
        paths = snapshot["binding"]["paths"]
        partial = case in {"child_run_owner_recovery", "child_run_approval_cancel"}
        artifact = paths["artifact_dir"] + ("/partial-" if partial else "/complete-") + nonce + ".txt"
        content = ("PARTIAL-" if partial else "COMPLETE-") + nonce
        effect = paths["artifact_dir"] + "/effect-" + nonce + ".txt"
        captures = {}
        for label, path in [("artifact", artifact), ("effect", effect), ("grants-after", GRANTS),
                            ("child-log", paths["log_path"]),
                            ("cancelled-results", paths["artifact_dir"] + "/cancelled-results.json")]:
            contents = archive_read(os.environ["EVAL_HOME"], path)
            captures[path] = contents
            if contents is not None:
                (root / ("actual-" + label + ".bin")).write_bytes(contents)
        (root / "actual-files.json").write_text(json.dumps({path: None if contents is None else {
            "length": len(contents), "sha256": hashlib.sha256(contents).hexdigest()} for path, contents in captures.items()}, indent=2))
        ingress = next(row["data"]["authority"] for row in journal if row["event_type"] == "InputAdmitted")
        pin = {"SessionId": receipt["session_id"], "Audience": "Personal", "Boundary": "boundary:trusted-instance",
               "ChannelType": "tui" if case.startswith("child_run_approval") else "headless",
               "RequesterSenderId": "local", "RequesterPrincipal": "Operator", "TransportAuthenticity": "LocalProcess",
               "PayloadTaint": "Trusted", "SourceKind": "signalr", "SupportsInteractiveApproval": case.startswith("child_run_approval")}
        setup = {**base, "authority": ingress, "ingress": pin,
                 "parent_inputs": [initial, probe.replace("{{RUN_ID}}", receipt["accepted_run"]["run_id"])],
                 "start_arguments": {**({"Name": SKILL, "Task": task, "Context": overlay(nonce)}
                    if case == "child_run_routed_skill" else {"Agent": PROFILE, "Task": task}),
                    "_rationale": RATIONALE},
                 "artifact_path": artifact, "artifact_bytes": content.encode(), "overlay": overlay(nonce)}
        effects = {"files": captures}
        if case.startswith("child_run_approval"):
            request = next(row["data"] for row in journal if row["event_type"] == "ToolApprovalRequested")
            setup.update(shell_cwd=paths["session_dir"], shell_arguments={"Command": f"{EXECUTABLE} {effect} {nonce}",
                "WorkingDirectory": paths["session_dir"]}, shell_result="Exit code: 0\n" + nonce + "\n\n[approval: once]",
                effect_path=effect, effect_bytes=nonce.encode(), grant_bytes=(root / "grants-before.bin").read_bytes())
            effects.update(before_answer={effect: None}, after_terminal={effect: captures[effect]},
                           grants_before=setup["grant_bytes"], grants_after=captures[GRANTS])
            before = json.loads((root / "before-answer.json").read_text())
            require(before == {"effect_path": effect, "exists": False}, "The actual pre-answer effect capture differs.")
        log = session_logs(os.environ["EVAL_HOME"], receipt["session_id"], [receipt["accepted_run"]["run_id"]])
        (root / "actual-session-logs.txt").write_text(log)
        events = read_lines(root / "observer/session-output.jsonl")
        actions = read_lines(root / "observer/observer-actions.jsonl")
        requests = evidence_requests(root / "relay")
        report.update(verify_process_trial(case, receipt, events, requests, snapshot, journal, actions, effects, setup, log))
        if case == "child_run_approval_cancel":
            terminal = receipt["delivery_observations"]["deliveries"][0]["terminal"]
            verify_partial_report(terminal, os.environ["EVAL_HOME"], receipt["accepted_run"], artifact,
                                  f"Successfully wrote {len(content.encode())} bytes to {artifact}")
        (root / "setup.json").write_text(json.dumps(setup, default=lambda value: {"hex_bytes": value.hex()}, indent=2))
    except (AssertionError, KeyError, ValueError, OSError, subprocess.SubprocessError) as failure:
        error = type(failure).__name__ + ": " + str(failure)
        report.update(passed=False, error=error)
    finally:
        snapshot = control(port, "snapshot")
        (root / "fixture-final-snapshot.json").write_text(json.dumps(snapshot, indent=2))
        if not snapshot["released"] or case == "child_run_owner_recovery":
            control(port, "child-abort")
        (root / "trial-receipt.json").write_text(json.dumps(report, indent=2))
        (Path(os.environ["TMPDIR_EVAL"]) / "stdout_background-results.txt").write_text(json.dumps(
            {"runtime": [report], "model": [], "errors": [error] if error else [], "passed": report["passed"]}, indent=2))
    return 0 if report["passed"] else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["prepare", "serve", "run"])
    parser.add_argument("--port", type=int)
    parser.add_argument("--runtime-only", action="store_true")
    args = parser.parse_args()
    if args.action == "prepare":
        prepare()
        return 0
    if args.action == "run":
        require(args.runtime_only and args.port, "The process case requires its local runtime-only relay.")
        return run(args.port)
    fixture = ProcessFixture(Path(os.environ["TMPDIR_EVAL"]) / "child-runs/relay")
    server = ThreadingHTTPServer(("127.0.0.1", 0), process_handler(fixture))
    print(server.server_port, flush=True)
    try:
        server.serve_forever()
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
