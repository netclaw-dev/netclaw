"""Prove prompt containment with independent request finalizers and fake Docker."""

from collections import Counter
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import unittest


RUNNER = Path(__file__).resolve().with_name("run-evals.sh")
OWNED_ID = "a" * 64
FOREIGN_ID = "b" * 64


def exchange(path, message):
    with socket.socket(socket.AF_UNIX) as client:
        client.settimeout(20)
        client.connect(str(path))
        client.sendall((json.dumps(message) + "\n").encode())
        with client.makefile("r") as response:
            return json.loads(response.readline())


class RequestFinalizers:
    """The request handlers outlive CLI exit. Only their finalizers reduce counts."""

    def __init__(self, path, fault):
        self.path = path
        self.fault = fault
        self.condition = threading.Condition()
        self.cancel = threading.Event()
        self.release = threading.Event()
        self.foreign_release = threading.Event()
        self.active = Counter()
        self.events = []
        self.errors = []
        self.workers = []
        self.listener = socket.socket(socket.AF_UNIX)
        self.listener.bind(str(path))
        self.listener.listen()
        self.thread = threading.Thread(target=self.serve)
        self.thread.start()

    def event(self, name, **data):
        with self.condition:
            self.events.append({"name": name, "active": dict(self.active), **data})
            self.condition.notify_all()

    def serve(self):
        while True:
            connection, _ = self.listener.accept()
            reader = connection.makefile("r")
            message = json.loads(reader.readline())
            if message[0] == "close":
                reader.close()
                connection.close()
                return
            worker = threading.Thread(target=self.handle, args=(connection, reader, message))
            self.workers.append(worker)
            worker.start()

    def handle(self, connection, reader, message):
        def reply(status=0, stdout="", stderr=""):
            connection.sendall((json.dumps([status, stdout, stderr]) + "\n").encode())

        try:
            action, *args = message
            if action == "request":
                owner, role, success = args
                with self.condition:
                    self.active[owner] += 1
                    self.event("request-entered", owner=owner, role=role)
                try:
                    if not success:
                        reply()
                        if owner == FOREIGN_ID:
                            self.foreign_release.wait()
                        else:
                            self.cancel.wait()
                            self.event("cancellation-observed", role=role)
                            self.release.wait()
                finally:
                    with self.condition:
                        if owner == OWNED_ID:
                            log = self.path.parent / "home/logs/backend-finalized.log"
                            with log.open("a") as output:
                                output.write(role + "\n")
                        self.active[owner] -= 1
                        self.event("request-finalized", owner=owner, role=role)
                if success:
                    reply()
            elif action == "docker":
                command, *options = args
                self.event("docker", command=command, args=options)
                if command == "run":
                    reply(stdout=OWNED_ID + "\n")
                elif command == "stop":
                    target = options[-1]
                    self.event("stop-requested", target=target)
                    if self.fault == "stop" or target not in (OWNED_ID, "netclaw-eval-owned"):
                        reply(1, stderr="synthetic stop failure\n")
                    else:
                        self.cancel.set()
                        with self.condition:
                            self.condition.wait_for(lambda: self.active[OWNED_ID] == 0)
                            self.event("container-exited")
                        reply(stdout=target + "\n")
                elif command in ("inspect", "wait", "ps", "container"):
                    if self.fault == "verify":
                        reply(1, stderr="synthetic Docker service failure\n")
                    else:
                        # --rm removed the owned container. A failed inspect alone is not proof.
                        if command == "inspect" or options[:1] == ["inspect"]:
                            reply(1, stderr="Error: No such container\n")
                        elif command == "wait":
                            self.event("exit-verified")
                            reply(stdout="0\n")
                        else:
                            self.event("exit-verified")
                            reply(stdout=FOREIGN_ID + "\n" if not any(OWNED_ID in x for x in options) else "")
                elif command == "exec":
                    reply(1 if "test" in options else 0)
                elif command == "image":
                    reply(stdout="sha256:fixture-image\n")
                else:
                    reply(1, stderr="unsupported fake Docker command\n")
            else:
                self.event(action, args=args)
                reply()
        except Exception as error:
            with self.condition:
                self.errors.append(repr(error))
                self.condition.notify_all()
        finally:
            reader.close()
            connection.close()

    def close(self):
        self.cancel.set()
        self.release.set()
        self.foreign_release.set()
        with socket.socket(socket.AF_UNIX) as client:
            client.connect(str(self.path))
            client.sendall(b'["close"]\n')
        self.thread.join(20)
        for worker in self.workers:
            worker.join(20)
        self.listener.close()
        if self.thread.is_alive() or any(worker.is_alive() for worker in self.workers):
            raise AssertionError("The fixture did not terminate its threads")


# Keep the existing CLI, timeout, Docker, and observer seams in one executable.
FAKE_TOOL = r'''#!/usr/bin/env python3
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys


def call(*message):
    with socket.socket(socket.AF_UNIX) as client:
        client.settimeout(20)
        client.connect(os.environ["BACKEND_SOCKET"])
        client.sendall((json.dumps(message) + "\n").encode())
        with client.makefile("r") as response:
            return json.loads(response.readline())


kind = Path(sys.argv[0]).name
args = sys.argv[1:]
if kind == "docker":
    status, stdout, stderr = call("docker", *args)
    print(stdout, end="")
    print(stderr, end="", file=sys.stderr)
    sys.exit(status)
if kind == "timeout":
    with open(os.environ["TIMEOUT_LOG"], "a") as log:
        log.write(" ".join(args) + "\n")
    read_fd, write_fd = os.pipe()
    child = subprocess.Popen(args[1:], pass_fds=(write_fd,),
                             env={**os.environ, "READY_FD": str(write_fd)})
    os.close(write_fd)
    try:
        if os.read(read_fd, 1) != b"1":
            raise RuntimeError("The fake CLI did not acknowledge admission")
        if os.environ["OUTER_TIMEOUT"] == "1":
            child.terminate()
        status = child.wait(timeout=20)
    finally:
        os.close(read_fd)
        if child.poll() is None:
            child.kill()
            child.wait()
    call("cli-exited", status)
    sys.exit(124 if os.environ["OUTER_TIMEOUT"] == "1" else status)

call("dispatch", kind, args)
success = os.environ["PROMPT_STATUS"] == "0"
session = args[args.index("--resume") + 1] if "--resume" in args else f"eval/fixture-{os.getpid()}"
log = (Path(args[-1]) if kind == "dotnet" else
       Path(os.environ["HOME_DIR"]) / "logs" / (session.replace("/", "-") + ".log"))
with log.open("a") as output:
    output.write(f"[2026-10-15T10:00:00+00:00] Headless session started: {session}\n")
    output.write("[2026-10-15T10:00:01+00:00] PROMPT: synthetic prompt\n")
    if os.environ["PROMPT_STATUS"] == "1":
        output.write("[2026-10-15T10:00:02+00:00] ERROR: synthetic provider failure\n")
    if success or os.environ["PROMPT_STATUS"] == "1":
        output.write("[2026-10-15T10:00:03+00:00] TURN_COMPLETED: turn=1\n")
for role in ("parent", "child"):
    call("request", os.environ["OWNED_ID"], role, success)
print(os.environ["PROMPT_STDOUT"], end="", flush=True)
print("synthetic prompt diagnostic", file=sys.stderr, flush=True)
os.write(int(os.environ["READY_FD"]), b"1")
os.close(int(os.environ["READY_FD"]))
if os.environ["OUTER_TIMEOUT"] == "1":
    signal.pause()
sys.exit(int(os.environ["PROMPT_STATUS"]))
'''


class RunEvalsTimeoutTests(unittest.TestCase):
    def prepare(self, root):
        root = Path(root)
        paths = {name: root / name for name in ("tools", "repo", "assets", "home", "captures", "logs")}
        for path in paths.values():
            path.mkdir()
        paths["root"] = root
        paths["cli"] = root / "netclaw"
        (paths["repo"] / "evals/runs").mkdir(parents=True)
        (paths["home"] / "logs").mkdir()
        (paths["home"] / "logs/daemon-2026-10-14.log").write_text("synthetic daemon log\n")
        (paths["home"] / "skills").mkdir()
        templates = paths["assets"] / "src/Netclaw.Cli/Resources/identity"
        templates.mkdir(parents=True)
        for name in ("SOUL", "TOOLING"):
            (templates / f"{name}.template.md").write_text("Synthetic identity fixture.\n")
        fixtures = paths["assets"] / "evals/fixtures"
        (fixtures / "identity").mkdir(parents=True)
        (fixtures / "identity/AGENTS.md").write_text("Synthetic fixture.\n")
        (fixtures / "config").mkdir()
        for name in ("netclaw", "tool-approvals"):
            (fixtures / "config" / f"{name}.json").write_text("{}")
        (fixtures / "eval-memories.json").write_text('{"seedDocuments": []}')
        (paths["repo"] / "evals/seed-memories.py").write_text("pass\n")
        for path in [paths["cli"], *(paths["tools"] / name for name in ("timeout", "docker", "dotnet"))]:
            path.write_text(FAKE_TOOL)
            path.chmod(0o755)
        for name, body in {
            "stdbuf": 'shift 2\nexec "$@"',
            "curl": "exit 0",
            # Startup creates fixture repos. No Git command reaches a real repository.
            "git": "exit 0",
        }.items():
            path = paths["tools"] / name
            path.write_text("#!/usr/bin/env bash\n" + body + "\n")
            path.chmod(0o755)
        return paths

    def invoke_prompt(self, paths, row, backend):
        script = r'''source "$1"
PATH="$TOOLS:$PATH"
sleep() { :; }
REPO_ROOT="$REPO"
EVAL_ASSET_ROOT="$ASSETS"
EVAL_CONTAINER_NAME=netclaw-eval-owned
NETCLAW_IMAGE=fixture-image
NETCLAW_BIN="$CLI"
EVAL_HOME="$HOME_DIR"
TMPDIR_EVAL="$CAPTURES"
RUN_ID=containment-proof
STARTED_AT=now
RESULTS_DB=
DAEMON_LOG="$HOME_DIR/logs/daemon-2026-10-14.log"
RUNS=2
THRESHOLD=1
PROMPT_TIMEOUT=1
MAX_PROMPT_TIMEOUT=1
FILTER_CASE=
FILTER_CATEGORY=
CATEGORY_SKIPPED=false
trap cleanup_eval_env EXIT
check_daemon_alive() { :; }
setup_timeout_case() { printf 'setup:%s\n' "$1" >> "$EVENTS"; }
assert_timeout_case() { printf 'assert\n' >> "$EVENTS"; return 0; }
store_result() { printf 'score\n' >> "$EVENTS"; }
store_metrics() { :; }
start_eval_daemon
if [[ "$FOREIGN_CONTROL" == 1 ]]; then EVAL_CONTAINER_NAME=foreign-container; fi
case "$KIND" in
  text) run_case timeout_case 'timeout control' 'first prompt' 'second prompt';;
  json) run_case --json timeout_case 'timeout control' 'first prompt' 'second prompt';;
  resume) run_multi_turn_case timeout_case 'timeout control' 'first prompt' 'second prompt';;
  observer) run_case --json --coordination-observer timeout_case 'timeout control' 'first prompt' 'second prompt';;
  warmup) seed_eval_memories;;
esac
printf 'suite-continued\n' >> "$EVENTS"
'''
        env = {
            "PATH": os.environ["PATH"], "HOME": str(paths["root"]),
            "TMPDIR": str(paths["root"]), "PYTHONDONTWRITEBYTECODE": "1",
            "TOOLS": str(paths["tools"]), "REPO": str(paths["repo"]),
            "ASSETS": str(paths["assets"]), "CLI": str(paths["cli"]),
            "HOME_DIR": str(paths["home"]), "CAPTURES": str(paths["captures"]),
            "EVENTS": str(paths["logs"] / "runner-events.txt"), "KIND": row["kind"],
            "TIMEOUT_LOG": str(paths["logs"] / "timeout.log"),
            "PROMPT_STATUS": str(row.get("status", 124)), "PROMPT_STDOUT": row.get("stdout", "partial success"),
            "OUTER_TIMEOUT": str(int(row.get("outer", False))), "BACKEND_SOCKET": str(backend.path),
            "OWNED_ID": OWNED_ID, "FOREIGN_CONTROL": str(int(row.get("foreign", False))),
            "NETCLAW_EVAL_SEED": "7",
        }
        output = paths["logs"] / "runner-stdout.txt"
        error = paths["logs"] / "runner-stderr.txt"
        with output.open("w") as stdout, error.open("w") as stderr:
            process = subprocess.Popen(["bash", "-c", script, "timeout-test", str(RUNNER)],
                                       env=env, stdout=stdout, stderr=stderr, start_new_session=True)
        def reap():
            process.wait()
            backend.event("harness-exited", status=process.returncode)
        waiter = threading.Thread(target=reap)
        waiter.start()
        return process, waiter

    def test_prompt_failure_contains_owned_requests_before_abort(self):
        rows = [
            {"name": "text_timeout_with_delayed_backend_finalization", "kind": "text", "outer": True},
            {"name": "json_timeout_empty_output", "kind": "json", "stdout": "", "outer": True},
            {"name": "resume_first_turn_timeout", "kind": "resume", "outer": True},
            {"name": "observer_internal_timeout", "kind": "observer"},
            {"name": "observer_outer_timeout", "kind": "observer", "outer": True},
            {"name": "nonzero_status7", "kind": "text", "status": 7},
            {"name": "failed_turn_status1", "kind": "text", "status": 1},
            {"name": "warmup_timeout", "kind": "warmup", "outer": True},
            {"name": "stop_failure", "kind": "json", "fault": "stop"},
            {"name": "exit_verification_failure", "kind": "text", "fault": "verify"},
            {"name": "foreign_container_control", "kind": "text", "foreign": True},
            {"name": "success_multiturn", "kind": "resume", "status": 0},
        ]
        for row in rows:
            with self.subTest(row=row["name"]), tempfile.TemporaryDirectory() as directory:
                paths = self.prepare(directory)
                backend = RequestFinalizers(paths["root"] / "backend.sock", row.get("fault"))
                process = waiter = None
                try:
                    exchange(backend.path, ["request", FOREIGN_ID, "foreign", False])
                    process, waiter = self.invoke_prompt(paths, row, backend)
                    success = row.get("status", 124) == 0
                    archive = paths["repo"] / "evals/runs/containment-proof"
                    with backend.condition:
                        reached = backend.condition.wait_for(
                            lambda: any(e["name"] == "harness-exited" for e in backend.events)
                            or sum(e["name"] == "cancellation-observed" for e in backend.events) >= 2
                            or row.get("fault") == "stop" and any(e["name"] == "stop-requested" for e in backend.events),
                            timeout=20)
                        held_events = list(backend.events)
                        held_active = dict(backend.active)
                        held_status = process.poll()
                        archived_before_release = (archive / "run-info.txt").exists()
                    # Release only after the snapshot. All failure assertions use that snapshot.
                    backend.release.set()
                    waiter.join(20)
                    self.assertTrue(reached, (row, backend.errors))
                    self.assertFalse(waiter.is_alive(), "The harness did not exit after finalization")
                    self.assertEqual([], backend.errors)
                    events = backend.events
                    dispatches = [e for e in events if e["name"] == "dispatch"]
                    docker = [e for e in events if e["name"] == "docker"]
                    stops = [e for e in docker if e["command"] == "stop"]
                    diagnostic = (paths["logs"] / "runner-stderr.txt").read_text()
                    self.assertEqual(0 if success else 2, process.returncode, diagnostic)
                    self.assertEqual(4 if success else 1, len(dispatches), events)
                    self.assertEqual([OWNED_ID], [e["args"][-1] for e in stops], stops)
                    self.assertEqual(1, sum(e["command"] == "run" for e in docker))
                    self.assertEqual(1, backend.active[FOREIGN_ID])
                    self.assertFalse(any(e["name"] == "request-finalized" and e["owner"] == FOREIGN_ID for e in events))
                    if not success:
                        exits = [e for e in held_events if e["name"] == "cli-exited"]
                        self.assertEqual(1, len(exits))
                        self.assertEqual(2, exits[0]["active"][OWNED_ID])
                        self.assertEqual(2, held_active[OWNED_ID])
                        self.assertEqual(1, sum(e["name"] == "dispatch" for e in held_events))
                        self.assertFalse(archived_before_release, "The archive preceded containment")
                        if row.get("fault") != "stop":
                            self.assertIsNone(held_status, "The harness exited before request finalization")
                            self.assertEqual(2, sum(e["name"] == "cancellation-observed" for e in held_events))
                        marker = paths["logs"] / "runner-events.txt"
                        marker_text = marker.read_text() if marker.exists() else ""
                        self.assertNotIn("suite-continued", marker_text)
                        self.assertNotIn("setup:2", marker_text)
                    if row.get("fault"):
                        self.assertRegex(diagnostic.lower(), r"(?s)(contain|cleanup|stop).*(fail|unconfirm|cannot)")
                        self.assertTrue(paths["home"].is_dir(), "Cleanup uncertainty must retain the home")
                        self.assertTrue(paths["captures"].is_dir(), "Cleanup uncertainty must retain captures")
                        self.assertEqual(2 if row["fault"] == "stop" else 0, backend.active[OWNED_ID])
                    else:
                        self.assertEqual(0, backend.active[OWNED_ID])
                        names = [e["name"] for e in events]
                        self.assertLess(names.index("container-exited"), names.index("harness-exited"))
                        self.assertIn("exit-verified", names)
                        finalizers = archive / "container-logs/backend-finalized.log"
                        self.assertEqual(8 if success else 2, len(finalizers.read_text().splitlines()))
                        self.assertFalse(paths["home"].exists())
                        self.assertFalse(paths["captures"].exists())
                    self.assert_prompt_evidence(paths, row, archive)
                    if not success and not row.get("fault") and row["kind"] != "warmup":
                        self.assert_archived_verdict(paths, row, archive)
                    if success:
                        calls = [e["args"][1] for e in dispatches]
                        sessions = [args[args.index("--resume") + 1] for args in calls]
                        self.assertEqual(sessions[0], sessions[1])
                        self.assertEqual(sessions[2], sessions[3])
                        self.assertNotEqual(sessions[0], sessions[2])
                        self.assertTrue(all(e["active"].get(OWNED_ID, 0) == 0 for e in dispatches))
                finally:
                    backend.release.set()
                    if process is not None and process.poll() is None:
                        # The fixture owns this process group and all its fake commands.
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait()
                    if waiter is not None:
                        waiter.join(20)
                    backend.close()

    def assert_archived_verdict(self, paths, row, archive):
        """Classify the final archive without access to the deleted scratch evidence."""
        self.assertFalse(paths["home"].exists())
        self.assertFalse(paths["captures"].exists())
        trial = archive / "trials/timeout_case/01"
        raw = trial / "raw"
        before = {p.relative_to(raw): p.read_bytes() for p in raw.rglob("*") if p.is_file()}
        completed = subprocess.run(
            [sys.executable, str(RUNNER.with_name("trial_verdict.py")),
             "trial-status", "--trial-dir", str(trial)],
            cwd=archive, capture_output=True, text=True, timeout=20,
            env={"PATH": os.environ["PATH"], "HOME": str(paths["root"]),
                 "TMPDIR": str(paths["root"]), "PYTHONDONTWRITEBYTECODE": "1"})
        self.assertEqual(0, completed.returncode, completed.stderr)
        expected = {
            124: {"status": "inconclusive", "reason": "timeout", "error": None},
            1: {"status": "inconclusive", "reason": "turn_failed", "error": "synthetic provider failure"},
            7: {"status": "inconclusive", "reason": "infra:cli_exit_7", "error": None},
        }[row.get("status", 124)]
        self.assertEqual(expected, json.loads(completed.stdout))
        self.assertEqual("ok", json.loads((raw / "session.json").read_text())["status"])
        self.assertIn("Headless session started:", (raw / "cli-session.log").read_text())
        self.assertEqual(before, {p.relative_to(raw): p.read_bytes() for p in raw.rglob("*") if p.is_file()})

    def assert_prompt_evidence(self, paths, row, archive):
        status = row.get("status", 124)
        if row["kind"] == "warmup":
            raw = archive / "stdout"
            self.assertEqual(["124"], [p.read_text().strip() for p in raw.glob("prompt_status_*.txt")])
            stdout = list(raw.glob("stdout_*_warmup.txt"))
            stderr = list(raw.glob("stderr_*_warmup.txt"))
        else:
            exit_files = sorted((archive / "trials").rglob("cli-exit.json"))
            self.assertEqual(4 if status == 0 else 1, len(exit_files))
            self.assertEqual({status}, {json.loads(p.read_text())["exit_code"] for p in exit_files})
            stdout = [p for item in exit_files for p in item.parent.glob("cli-stdout.*")]
            stderr = [item.parent / "cli-stderr.txt" for item in exit_files]
        self.assertEqual(4 if status == 0 else 1, len(stdout))
        self.assertTrue(all(p.read_text() == row.get("stdout", "partial success") for p in stdout))
        self.assertTrue(all(p.read_text() == "synthetic prompt diagnostic\n" for p in stderr))
        self.assertEqual(4 if status == 0 else 1, len((paths["logs"] / "timeout.log").read_text().splitlines()))


if __name__ == "__main__":
    unittest.main()
