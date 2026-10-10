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


if __name__ == "__main__":
    unittest.main()
