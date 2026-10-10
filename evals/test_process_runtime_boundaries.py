"""Pure controls for process fixture setup, owned crash commands, and cleanup."""

import json
import os
from pathlib import Path
from contextlib import closing
import sqlite3
import sys
import shlex
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import background_process_fixture as runtime


class ProcessRuntimeBoundaryControls(unittest.TestCase):
    def test_prepare_accepts_relay_parent_and_rejects_repeated_assignment(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            home, output = root / "home", root / "output"
            module = root / "module.py"
            marker = root / "fixtures/child-runs/process_marker.py"
            marker.parent.mkdir(parents=True)
            marker.write_bytes(b"#!/usr/bin/env python3\nprint('owned marker')\n")
            runtime.ProcessFixture(output / "child-runs/relay")
            with patch.dict(os.environ, {"NETCLAW_EVAL_CASE": "child_run_routed_skill", "RUNS": "1",
                                         "TMPDIR_EVAL": str(output), "EVAL_HOME": str(home)}), \
                    patch.object(runtime, "__file__", str(module)):
                runtime.prepare()
                setup = output / "child-runs/setup-base.json"
                retained = setup.read_bytes()
                self.assertEqual("child_run_routed_skill", json.loads(retained)["case"])
                self.assertEqual(marker.read_bytes(), (home / "data/evals/process_marker").read_bytes())
                skill = home / "skills" / runtime.SKILL / "SKILL.md"
                self.assertTrue(skill.is_file(), "The daemon skill mount cannot see the prepared routed skill.")
                self.assertIn(runtime.overlay(json.loads(retained)["nonce"]).encode(), skill.read_bytes())
                self.assertFalse((home / "data/skills" / runtime.SKILL).exists())
                with self.assertRaises(AssertionError):
                    runtime.prepare()
                self.assertEqual(retained, setup.read_bytes())

    def test_child_setup_passes_its_pinned_identity_to_the_crash_function(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = runtime.ProcessFixture(root / "relay")
            fixture.control("child-setup", {"case": "child_run_owner_recovery", "nonce": "n",
                                           "container_id": "owned-id"})
            accepted = {"run_id": "owned-run"}
            artifact_directory = "/home/netclaw/.netclaw/sessions/owner/subagents/owned-run/artifacts"
            fixture.binding = {"accepted": accepted, "paths": {"artifact_dir": artifact_directory}}
            actual = root / "home/data" / artifact_directory.removeprefix("/home/netclaw/.netclaw/") / "partial-n.txt"
            actual.parent.mkdir(parents=True)
            actual.write_bytes(b"PARTIAL-n")
            with patch.dict(os.environ, {"EVAL_HOME": str(root / "home")}), \
                    patch.object(runtime, "crash_daemon", return_value={"session_id": "owner"}) as crash:
                fixture.control("daemon-crash", {"session_id": "owner", "accepted": accepted})
                crash.assert_called_once_with("owner", "owned-id")
            fixture.crash = None
            with patch.object(runtime, "crash_daemon") as crash:
                with self.assertRaises(AssertionError):
                    fixture.control("daemon-crash", {"session_id": "owner", "accepted": {"run_id": "foreign"}})
                crash.assert_not_called()

    def test_crash_rejects_foreign_or_missing_container_identity_before_mutation(self):
        for expected in [None, "foreign-id"]:
            with self.subTest(expected=expected), patch.dict(os.environ, {"EVAL_CONTAINER_NAME": "netclaw-eval-owned"}), \
                    patch.object(runtime.subprocess, "check_output", return_value="owned-id\n") as command:
                with self.assertRaises(AssertionError):
                    runtime.crash_daemon("owner", expected)
                self.assertEqual(1, command.call_count)
                self.assertEqual("inspect", command.call_args.args[0][1])
        with patch.dict(os.environ, {"EVAL_CONTAINER_NAME": "production"}), \
                patch.object(runtime.subprocess, "check_output") as command:
            with self.assertRaises(AssertionError):
                runtime.crash_daemon("owner", "owned-id")
            command.assert_not_called()

    def test_crash_records_old_and_new_process_identity_with_exact_kill_target(self):
        commands, identities = [], iter([(4101, "100"), (4102, "200")])
        current = None
        def command(argv, **kwargs):
            nonlocal current
            commands.append(argv)
            if argv[1] == "inspect":
                return "owned-id\n"
            if argv[-3:] == ["pgrep", "-x", "netclawd"]:
                current = next(identities)
                return str(current[0]).encode()
            if argv[-2] == "cat":
                return (str(current[0]) + " (netclawd) " + " ".join(["S"] + ["0"] * 18 + [current[1]])).encode()
            self.assertEqual(["kill", "-KILL", "4101"], argv[-3:])
            return b""
        with patch.dict(os.environ, {"EVAL_CONTAINER_NAME": "netclaw-eval-owned"}), \
                patch.object(runtime.subprocess, "check_output", side_effect=command):
            result = runtime.crash_daemon("owner", "owned-id")
        self.assertEqual((4101, "100", 4102, "200"),
            (result["old_pid"], result["old_start"], result["new_pid"], result["new_start"]))
        self.assertFalse(result["new_ready"])
        self.assertEqual(1, sum(argv[-3:] == ["kill", "-KILL", "4101"] for argv in commands))
        self.assertTrue(all(argv[4] == "netclaw-eval-owned" for argv in commands[1:]))

    def test_backup_contains_committed_wal_bytes_before_projector_dispatch(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            home = root / "home"
            database = home / "data/netclaw.db"
            database.parent.mkdir(parents=True)
            output = root / "output"
            output.mkdir()
            with closing(sqlite3.connect(database)) as source:
                source.execute("PRAGMA journal_mode=WAL")
                source.execute("CREATE TABLE evidence (value TEXT)")
                source.execute("INSERT INTO evidence VALUES ('committed')")
                source.commit()
                source.execute("INSERT INTO evidence VALUES ('uncommitted')")
                def projector(argv, **kwargs):
                    retained = output / "journal-backup.sqlite"
                    with closing(sqlite3.connect(retained)) as connection:
                        self.assertEqual([("committed",)], connection.execute("SELECT value FROM evidence").fetchall())
                    self.assertEqual([str(retained), "owner", str(output / "journal.json")], argv[-3:])
                    (output / "journal.json").write_text("[]")
                    return SimpleNamespace(stdout=b"", stderr=b"", returncode=0)
                with patch.dict(os.environ, {"EVAL_HOME": str(home)}), \
                        patch.object(runtime, "observer_command", return_value=["projector"]), \
                        patch.object(runtime.subprocess, "run", side_effect=projector) as process:
                    self.assertEqual([], runtime.capture_journal(output, "owner"))
                    with self.assertRaises(AssertionError):
                        runtime.capture_journal(output, "owner")
                    self.assertEqual(1, process.call_count)

    def test_observer_failure_aborts_owned_held_response_and_retains_false(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            child = root / "child-runs"
            child.mkdir()
            (child / "setup-base.json").write_text(json.dumps({"case": "child_run_owner_recovery", "nonce": "n"}))
            actions = []
            def control(port, action, **fields):
                actions.append((port, action, fields))
                return {"released": False}
            with patch.dict(os.environ, {"TMPDIR_EVAL": str(root), "RUNS": "1", "NETCLAW_EVAL_CASE":
                    "child_run_owner_recovery", "EVAL_CONTAINER_NAME": "netclaw-eval-owned", "EVAL_HOME": str(root / "home"),
                    "NETCLAW_IMAGE": "owned-image"}), patch.object(runtime.subprocess, "check_output", return_value="owned-id\n"), \
                    patch.object(runtime, "control", side_effect=control), \
                    patch.object(runtime, "invoke_observer", side_effect=AssertionError("controlled observer failure")):
                self.assertEqual(1, runtime.run(1234))
            self.assertEqual(["child-setup", "snapshot", "child-abort"], [action[1] for action in actions])
            self.assertFalse(json.loads((child / "trial-receipt.json").read_text())["passed"])

    def test_shell_exports_run_count_before_actual_prepare(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scripts = root / "scripts"
            scripts.mkdir()
            actual = Path(runtime.__file__).parent
            (scripts / "run-background-evals.sh").write_bytes((actual / "run-background-evals.sh").read_bytes())
            output, home = root / "output", root / "home"
            observer, cli = root / "observer.dll", root / "cli"
            observer.write_bytes(b"command recorder only")
            cli.write_text("#!/usr/bin/env bash\nprintf 'control-version\\n'\n")
            cli.chmod(0o755)
            boundary = root / "daemon-boundary"
            source = ("REPO_ROOT=" + shlex.quote(str(actual.parent)) + "\n"
                + "TMPDIR_EVAL=" + shlex.quote(str(output)) + "\n"
                + "EVAL_HOME=" + shlex.quote(str(home)) + "\n"
                + "NETCLAW_BIN=" + shlex.quote(str(cli)) + "\n"
                + "RUNS=\"${NETCLAW_EVAL_RUNS:-5}\"\n"
                + "FILTER_CASE=\"$NETCLAW_EVAL_CASE\"\n"
                + "EVAL_CONTAINER_NAME=netclaw-eval-control\n"
                + "EVAL_PROVIDER_API_KEY=\"\"\nEVAL_DATA_PROTECTION_KEYS=\"\"\n"
                + "check_prerequisites() { :; }\nbuild_local_image() { :; }\n"
                + "cleanup_eval_env() { :; }\n"
                + "start_eval_daemon() { printf boundary > " + shlex.quote(str(boundary)) + "; return 23; }\n")
            (scripts / "run-evals.sh").write_text(source)
            commands = root / "commands"
            commands.mkdir()
            shim = commands / "python3"
            shim.write_text("#!" + sys.executable + "\nimport os,sys\n"
                + "if sys.argv[2:] == ['serve']:\n"
                + "    print('12345',flush=True)\n    sys.stdin.buffer.read()\n    sys.exit(0)\n"
                + "os.execv(" + repr(sys.executable) + ", [" + repr(sys.executable) + ", *sys.argv[1:]])\n")
            shim.chmod(0o755)
            environment = os.environ.copy()
            environment.pop("RUNS", None)
            environment.update({"NETCLAW_EVAL_RUNS": "1", "NETCLAW_EVAL_CASE": "child_run_routed_skill",
                "NETCLAW_CHILD_OBSERVER": str(observer), "PATH": str(commands) + os.pathsep + environment["PATH"]})
            result = subprocess.run(["bash", str(scripts / "run-background-evals.sh"), "--runtime-only"],
                env=environment, capture_output=True, text=True, check=False, timeout=15)
            self.assertEqual(23, result.returncode, result.stderr)
            self.assertTrue(boundary.is_file())
            setup = json.loads((output / "child-runs/setup-base.json").read_text())
            self.assertEqual("child_run_routed_skill", setup["case"])
            self.assertTrue((home / "skills" / runtime.SKILL / "SKILL.md").is_file())

    def test_actual_exit_trap_stops_owned_relay_after_archive_failure(self):
        script = Path(runtime.__file__).parent / "run-background-evals.sh"
        trap = next(line for line in script.read_text().splitlines() if line.startswith("trap "))
        for status, harness_exit in [(0, 0), (7, 0), (0, 9), (7, 9)]:
            with self.subTest(status=status, harness_exit=harness_exit):
                program = ("set -euo pipefail\ncleanup_eval_env() { echo cleanup; return " + str(status)
                    + "; }\nkill() { echo kill; }\nwait() { echo wait; }\nfixture_pid=42\n" + trap + "\nexit " + str(harness_exit) + "\n")
                result = subprocess.run(["bash", "-c", program], capture_output=True, text=True, check=False)
                self.assertEqual(["cleanup", "kill", "wait"], result.stdout.splitlines())
                self.assertEqual(harness_exit or status, result.returncode)



class ProcessArtifactOrderControls(unittest.TestCase):
    def fixture(self, directory, case):
        fixture = runtime.ProcessFixture(Path(directory) / "relay")
        fixture.case, fixture.nonce = case, "owned"
        paths = {"artifact_dir": str(Path(directory) / "artifacts"), "session_dir": str(Path(directory) / "workspace")}
        request = {"tools": [{"function": {"name": name}} for name in ["file_write", "shell_execute", "load_tool"]]}
        return fixture, paths, request

    def call(self, response):
        self.assertEqual(1, len(response["tool_calls"]))
        call = response["tool_calls"][0]
        return call["id"], call["function"]["name"], json.loads(call["function"]["arguments"])

    def artifact_pair(self, fixture, paths, result):
        partial = fixture.case in {"child_run_owner_recovery", "child_run_approval_cancel"}
        kind, prefix = ("partial", "PARTIAL-") if partial else ("complete", "COMPLETE-")
        path, content = paths["artifact_dir"] + "/" + kind + "-owned.txt", prefix + "owned"
        return ("fixture-owned-artifact", "file_write", {"Path": path, "Content": content,
                "_rationale": runtime.RATIONALE}, result), path, content

    def test_each_case_offers_the_exact_artifact_write_before_any_protected_call(self):
        for case in runtime.CASES:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                fixture, paths, request = self.fixture(directory, case)
                identifier, name, args = self.call(fixture.child_response(request, [], paths))
                pair, path, content = self.artifact_pair(fixture, paths, "")
                self.assertEqual((pair[0], "file_write", pair[2]), (identifier, name, args))

    def test_actual_full_write_then_exact_protected_result_ends_without_another_write(self):
        for case in ["child_run_approval_once", "child_run_approval_cancel"]:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                fixture, paths, request = self.fixture(directory, case)
                first = self.call(fixture.child_response(request, [], paths))
                self.assertEqual("file_write", first[1])
                path = Path(first[2]["Path"])
                path.parent.mkdir()
                content = first[2]["Content"]
                path.write_bytes(content.encode())
                pair = (*first, f"Successfully wrote {path.stat().st_size} bytes to {path}")
                second = self.call(fixture.child_response(request, [pair], paths))
                self.assertEqual("shell_execute", second[1])
                self.assertEqual({"Command": runtime.EXECUTABLE + " " + paths["artifact_dir"] + "/effect-owned.txt owned",
                    "WorkingDirectory": paths["session_dir"], "_rationale": runtime.RATIONALE}, second[2])
                result = fixture.child_response(request, [pair, (*second, "Exit code: 0\nowned\n\n[approval: once]")], paths)
                self.assertNotIn("tool_calls", result)
                self.assertEqual(str(path) + "\n" + content, result["content"])
                self.assertEqual(content.encode(), path.read_bytes())
                self.assertFalse((path.parent / "effect-owned.txt").exists())

    def test_a_failed_or_partial_write_cannot_offer_the_protected_command(self):
        for case in runtime.CASES:
            for failure in ["Access denied", "Successfully wrote 1 bytes", ""]:
                with self.subTest(case=case, failure=failure), tempfile.TemporaryDirectory() as directory:
                    fixture, paths, request = self.fixture(directory, case)
                    pair, _, _ = self.artifact_pair(fixture, paths, failure)
                    with self.assertRaises(AssertionError):
                        fixture.child_response(request, [pair], paths)

    def test_changed_write_scope_and_unapproved_command_result_reject(self):
        for case in ["child_run_approval_once", "child_run_approval_cancel"]:
            for fault in ["path", "content", "protected-result"]:
                with self.subTest(case=case, fault=fault), tempfile.TemporaryDirectory() as directory:
                    fixture, paths, request = self.fixture(directory, case)
                    pair, path, content = self.artifact_pair(fixture, paths, "")
                    pair = (*pair[:3], f"Successfully wrote {len(content.encode())} bytes to {path}")
                    if fault in {"path", "content"}:
                        pair[2]["Path" if fault == "path" else "Content"] = "foreign"
                        pairs = [pair]
                    else:
                        call = self.call(fixture.child_response(request, [pair], paths))
                        pairs = [pair, (*call, "Exit code: 0\nowned")]
                    with self.assertRaises(AssertionError):
                        fixture.child_response(request, pairs, paths)


class ProcessPrivateCaptureControls(unittest.TestCase):
    local_run = staticmethod(subprocess.run)
    container_id = "a" * 64
    image_id = "sha256:" + "b" * 64
    artifact_directory = "/home/netclaw/.netclaw/sessions/owner/subagents/run/artifacts"

    def invoke_reader(self, root, leaf="effect-owned.txt", mutate_container=None, mutate_record=None, bootstrap=None):
        canonical = self.artifact_directory + "/" + leaf
        home = root / "home"
        data = home / "data"
        data.mkdir(parents=True, exist_ok=True)
        local_path = root / "reader" / leaf
        evidence = root / "evidence"
        evidence.mkdir(exist_ok=True)
        inspected = {"Id": self.container_id, "Image": self.image_id, "Config": {"Image": "owned-image"},
            "Mounts": [{"Type": "bind", "Destination": "/home/netclaw/.netclaw", "Source": str(data)}]}
        if mutate_container:
            mutate_container(inspected)
        commands = []
        def inspect(argv, **kwargs):
            commands.append(argv)
            if argv[:3] == ["docker", "image", "inspect"]:
                return self.image_id + "\n"
            self.assertEqual(["docker", "inspect", "netclaw-eval-owned"], argv)
            return json.dumps([inspected]).encode()
        def execute(argv, **kwargs):
            commands.append(argv)
            self.assertEqual(["docker", "exec", "--interactive", "--user", "netclaw", self.container_id,
                              "python3", "-", canonical], argv)
            self.assertTrue(kwargs["check"])
            interpreter = [sys.executable, "-", str(local_path)]
            if bootstrap:
                interpreter = [sys.executable, "-c", bootstrap, str(local_path)]
            result = self.local_run(interpreter, input=kwargs["input"], capture_output=True, check=True)
            record = json.loads(result.stdout)
            self.assertEqual(str(local_path), record["path"])
            record["path"] = canonical
            if mutate_record:
                mutate_record(record)
            return SimpleNamespace(stdout=json.dumps(record).encode(), stderr=result.stderr, returncode=0)
        with patch.dict(os.environ, {"EVAL_CONTAINER_NAME": "netclaw-eval-owned", "NETCLAW_IMAGE": "owned-image"}), \
                patch.object(runtime.subprocess, "check_output", side_effect=inspect), \
                patch.object(runtime.subprocess, "run", side_effect=execute):
            result = runtime.private_archive_read(str(home), canonical, self.artifact_directory, "owned",
                                                   self.container_id, evidence)
        return result, evidence, commands

    def test_private_mode_bytes_and_missing_status_use_the_explicit_runtime_user(self):
        for leaf in ["effect-owned.txt", "cancelled-results.json"]:
            with self.subTest(leaf=leaf), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                file = root / "reader" / leaf
                file.parent.mkdir()
                file.write_bytes(b"private\x00bytes\n")
                file.chmod(0o600)
                contents, evidence, commands = self.invoke_reader(root, leaf)
                self.assertEqual(file.read_bytes(), contents)
                record = json.loads((evidence / ("private-file-" + leaf + ".json")).read_bytes())
                self.assertEqual((True, os.getuid(), 0o600, len(contents)),
                                 (record["exists"], record["uid"], record["mode"], record["size"]))
                self.assertEqual(3, len(commands))
                file.unlink()
                contents, evidence, _ = self.invoke_reader(root, leaf)
                self.assertIsNone(contents)
                self.assertEqual({"path": self.artifact_directory + "/" + leaf, "exists": False},
                    json.loads((evidence / ("private-file-" + leaf + ".json")).read_bytes()))

    def test_foreign_container_image_and_mount_fail_before_private_exec(self):
        changes = [lambda row: row.update(Id="c" * 64), lambda row: row.update(Image="foreign-image"),
            lambda row: row["Config"].update(Image="foreign-tag"),
            lambda row: row["Mounts"][0].update(Type="volume"),
            lambda row: row["Mounts"][0].update(Source="/foreign"),
            lambda row: row["Mounts"].append({"Type": "bind", "Destination": self.artifact_directory, "Source": "/other"})]
        for index, change in enumerate(changes):
            with self.subTest(change=index), tempfile.TemporaryDirectory() as directory:
                with self.assertRaisesRegex(AssertionError, "container, image, or owned mount changed"):
                    self.invoke_reader(Path(directory), mutate_container=change)

    def test_unassigned_or_escaping_private_path_fails_before_any_command(self):
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(runtime.subprocess, "check_output") as inspect, patch.object(runtime.subprocess, "run") as execute:
            for path in [self.artifact_directory + "/complete-owned.txt", self.artifact_directory + "/effect-other.txt",
                         self.artifact_directory + "/../artifacts/effect-owned.txt", "/etc/cancelled-results.json"]:
                with self.subTest(path=path), self.assertRaises(AssertionError):
                    runtime.private_archive_read(directory, path, self.artifact_directory, "owned", self.container_id, Path(directory))
            inspect.assert_not_called()
            execute.assert_not_called()

    def test_local_reader_rejects_links_special_files_and_foreign_owner(self):
        for kind in ["leaf-link", "ancestor-link", "fifo", "directory", "foreign-owner"]:
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                parent = root / "reader"
                target = parent / "effect-owned.txt"
                parent.mkdir()
                bootstrap = None
                if kind == "leaf-link":
                    (root / "outside").write_bytes(b"foreign")
                    target.symlink_to(root / "outside")
                elif kind == "ancestor-link":
                    parent.rmdir()
                    (root / "outside").mkdir()
                    (root / "outside/effect-owned.txt").write_bytes(b"foreign")
                    parent.symlink_to(root / "outside", target_is_directory=True)
                elif kind == "fifo":
                    os.mkfifo(target)
                elif kind == "directory":
                    target.mkdir()
                else:
                    target.write_bytes(b"owned bytes")
                    bootstrap = "import os, sys; actual=os.getuid(); os.getuid=lambda: actual+1; exec(sys.stdin.buffer.read())"
                with self.assertRaises(subprocess.CalledProcessError):
                    self.invoke_reader(root, bootstrap=bootstrap)
                self.assertFalse(list((root / "evidence").glob("private-file-*")))

    def test_open_directory_swap_stays_on_the_original_descriptor(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            parent = root / "reader"
            parent.mkdir()
            (parent / "effect-owned.txt").write_bytes(b"original authorized bytes")
            outside = root / "outside"
            outside.mkdir()
            (outside / "effect-owned.txt").write_bytes(b"foreign bytes")
            bootstrap = ("import os, pathlib, sys\nreal_open=os.open\n"
                "def opened(path, flags, *args, **kwargs):\n"
                " fd=real_open(path, flags, *args, **kwargs)\n"
                " if path=='reader' and flags & os.O_DIRECTORY:\n"
                "  parent=pathlib.Path(sys.argv[1]).parent\n"
                "  parent.rename(parent.with_name('retained'))\n"
                "  parent.symlink_to(parent.with_name('outside'),target_is_directory=True)\n"
                " return fd\nos.open=opened\nexec(sys.stdin.buffer.read())")
            contents, _, _ = self.invoke_reader(root, bootstrap=bootstrap)
            self.assertEqual(b"original authorized bytes", contents)
            self.assertTrue(parent.is_symlink())
            self.assertEqual(b"foreign bytes", (parent / "effect-owned.txt").read_bytes())

    def test_private_capture_rejects_corrupt_receipts_without_host_fallback(self):
        for change in [lambda row: row.update(path="/foreign"), lambda row: row.update(size=999),
                       lambda row: row.update(sha256="0" * 64), lambda row: row.update(hex_bytes="invalid")]:
            with self.subTest(change=change), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / "reader").mkdir()
                (root / "reader/effect-owned.txt").write_bytes(b"private")
                with patch.object(runtime, "archive_read") as host:
                    with self.assertRaises((AssertionError, ValueError)):
                        self.invoke_reader(root, mutate_record=change)
                    host.assert_not_called()

    def run_capture(self, directory, fail_private=False):
        root = Path(directory)
        child = root / "output/child-runs"
        child.mkdir(parents=True)
        (child / "relay").mkdir()
        (child / "observer").mkdir()
        for name in ["session-output.jsonl", "observer-actions.jsonl"]:
            (child / "observer" / name).write_bytes(b"")
        (child / "setup-base.json").write_text(json.dumps({"case": "child_run_approval_once", "nonce": "owned"}))
        (child / "grants-before.bin").write_bytes(b"{}")
        effect = self.artifact_directory + "/effect-owned.txt"
        (child / "before-answer.json").write_text(json.dumps({"effect_path": effect, "exists": False}))
        paths = {"artifact_dir": self.artifact_directory, "session_dir": "/home/netclaw/.netclaw/sessions/owner/subagents/run",
                 "log_path": "/home/netclaw/.netclaw/sessions/owner/subagents/run/logs/session.log"}
        receipt = {"session_id": "owner", "accepted_run": {"run_id": "run"}}
        snapshot = {"binding": {"paths": paths}, "released": True}
        private_calls, host_calls = [], []
        def private(home, path, artifact_directory, nonce, container, evidence):
            private_calls.append(path)
            self.assertEqual((self.artifact_directory, "owned", self.container_id, child),
                             (artifact_directory, nonce, container, evidence))
            if fail_private:
                raise subprocess.CalledProcessError(1, ["owned-private-reader"], stderr=b"permission denied")
            return b"owned" if path == effect else None
        def host(home, path):
            host_calls.append(path)
            if path in {effect, self.artifact_directory + "/cancelled-results.json"}:
                raise PermissionError("The host cannot read the private file.")
            return b"{}" if path == runtime.GRANTS else b"COMPLETE-owned" if path.endswith("complete-owned.txt") else b"log"
        with patch.dict(os.environ, {"RUNS": "1", "TMPDIR_EVAL": str(root / "output"), "EVAL_HOME": str(root / "home"),
                "NETCLAW_EVAL_CASE": "child_run_approval_once", "EVAL_CONTAINER_NAME": "netclaw-eval-owned", "NETCLAW_IMAGE": "owned-image"}), \
                patch.object(runtime.subprocess, "check_output", return_value=self.container_id + "\n"), \
                patch.object(runtime, "control", return_value=snapshot), \
                patch.object(runtime, "invoke_observer", return_value=(receipt, None)), \
                patch.object(runtime, "capture_journal", return_value=[{"event_type": "InputAdmitted", "data": {"authority": {}}},
                    {"event_type": "ToolApprovalRequested", "data": {}}]), \
                patch.object(runtime, "private_archive_read", side_effect=private, create=True), \
                patch.object(runtime, "archive_read", side_effect=host), \
                patch.object(runtime, "session_logs", return_value="retained log"), \
                patch.object(runtime, "verify_process_trial", return_value={"passed": True}) as verify:
            result = runtime.run(1)
        return result, json.loads((child / "trial-receipt.json").read_bytes()), private_calls, host_calls, verify.call_count

    def test_run_captures_private_bytes_explicitly_before_acceptance(self):
        with tempfile.TemporaryDirectory() as directory:
            result, report, private, host, verified = self.run_capture(directory)
            self.assertEqual(0, result)
            self.assertTrue(report["passed"])
            self.assertEqual([self.artifact_directory + "/effect-owned.txt", self.artifact_directory + "/cancelled-results.json"], private)
            self.assertFalse(set(private) & set(host))
            self.assertEqual(1, verified)
            child = Path(directory) / "output/child-runs"
            self.assertEqual(b"owned", (child / "actual-effect.bin").read_bytes())
            self.assertIsNone(json.loads((child / "actual-files.json").read_bytes())[self.artifact_directory + "/cancelled-results.json"])

    def test_private_exec_failure_preserves_false_without_oracle_credit(self):
        with tempfile.TemporaryDirectory() as directory:
            result, report, private, _, verified = self.run_capture(directory, fail_private=True)
            self.assertEqual(1, result)
            self.assertFalse(report["passed"])
            self.assertTrue(report["error"].startswith("CalledProcessError:"))
            self.assertEqual(1, len(private))
            self.assertEqual(0, verified)


if __name__ == "__main__":
    unittest.main()
