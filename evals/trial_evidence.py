#!/usr/bin/env python3
"""Evidence helpers for the eval harness. Bash calls these once for each trial.

Each command reads raw sources and prints a small result. None of them judges
a trial. The commands:

  discover-session  Find the CLI session log that a trial created.
  actor-log         Copy the actor log of a session, and its sub-agent logs.
  daemon-session    Print the daemon lines that carry a session id token.
  derive-seed       Derive the seed of one (case, trial) from the run seed.
"""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import sqlite3
import sys

# discover-session exit codes. Any other non-zero code is a crash.
EXIT_NO_SESSION = 3
EXIT_AMBIGUOUS_SESSION = 4

CLI_SESSION_HEADER = re.compile(r"^\[[^\]]+\] Headless session started: (\S.*)$")

# The daemon sees the session store at this path. The harness sees it below
# EVAL_HOME/data.
CONTAINER_HOME = "/home/netclaw/.netclaw/"


def read_first_line(path):
    with open(path, errors="replace") as handle:
        return handle.readline().rstrip("\r\n")


def cli_session_id(path):
    """Return the session id in the first line of a CLI session log, or None.

    Crash logs, daemon logs, and headless-errors.log do not start with this
    header, so they are not session logs.
    """
    if path.suffix != ".log":
        return None
    match = CLI_SESSION_HEADER.match(read_first_line(path))
    return match.group(1) if match else None


def discover_session(logs_dir, known):
    """Find the one CLI session log that is new since `known`.

    A multi-turn trial appends to an older log. Its mtime is the newest, so
    the mtime says nothing about which log the trial created.
    """
    new_files = sorted(p for p in logs_dir.iterdir() if p.is_file() and p.name not in known)
    sessions = [(p, cli_session_id(p)) for p in new_files]
    sessions = [(p, session_id) for p, session_id in sessions if session_id]
    others = [p.name for p in new_files if p.name not in {s.name for s, _ in sessions}]

    if len(sessions) == 1:
        path, session_id = sessions[0]
        return 0, {"status": "ok", "session_id": session_id, "log": path.name,
                   "other_new_files": others}
    if not sessions:
        return EXIT_NO_SESSION, {"status": "no_session", "session_id": None, "log": None,
                                 "other_new_files": others}
    return EXIT_AMBIGUOUS_SESSION, {"status": "ambiguous_session", "session_id": None, "log": None,
                                    "candidates": [p.name for p, _ in sessions],
                                    "other_new_files": others}


def catalog_log_path(db_path, session_id):
    """Read the actor log path of a session from the netclaw.db session catalog.

    Returns (status, path). The path is the one the daemon sees. Status is
    "ok", "no_catalog_db", "no_catalog_row", "catalog_unreadable: ...", or
    "catalog_row_without_path".
    """
    if not db_path.is_file():
        return "no_catalog_db", None
    try:
        # Read-only, as the bash helpers read it. The daemon holds the file open.
        database = sqlite3.connect(db_path.resolve().as_uri() + "?mode=ro", uri=True)
        try:
            row = database.execute("SELECT log_path FROM sessions WHERE persistence_id = ?",
                                   (f"session-{session_id}",)).fetchone()
        finally:
            database.close()
    except sqlite3.Error as error:
        return f"catalog_unreadable: {error}", None
    if row is None:
        return "no_catalog_row", None
    if not row[0]:
        return "catalog_row_without_path", None
    return "ok", row[0]


def stays_in(root, path):
    """True when `path`, with `..` and symlinks resolved, is at or below `root`.

    The catalog text and the session folder are not trusted. A `..` segment
    or a symlink can leave the home even when the text starts with it.
    """
    return path.resolve().is_relative_to(root)


def collect_actor_log(home, session_id, destination):
    """Copy the actor log, and the sub-agent logs, of one session. Write actor-log.json.

    The record always says what happened. A missing catalog row or a missing
    file is a finding, not a reason to stop the run. The harness shows it.
    Only `*.log` files are copied, because a session folder can hold private
    workspace data. The data root also holds `keys/` and `config/`, and the
    model can write links in the session folders. So the checks are narrow:

      - The actor log must resolve under `data/sessions`.
      - A sub-agent log must resolve under the folder of this session.

    The resolved file is copied, not the link, so the file cannot change
    between the check and the copy.
    """
    record = {"session_id": session_id, "status": None, "catalog_log_path": None,
              "subagent_logs": [], "refused_subagent_logs": []}
    status, container_path = catalog_log_path(home / "data" / "netclaw.db", session_id)
    record["catalog_log_path"] = container_path

    if status == "ok" and not container_path.startswith(CONTAINER_HOME):
        status = "catalog_path_outside_home"
    data_root = (home / "data").resolve()
    sessions_root = (data_root / "sessions").resolve()
    log = None
    if status == "ok":
        log = data_root / container_path[len(CONTAINER_HOME):]
        resolved_log = log.resolve()
        if not stays_in(sessions_root, resolved_log):
            status = "catalog_path_outside_home"
    if status == "ok":
        if resolved_log.is_file():
            destination.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(resolved_log, destination / "session.log")
            # The session folder is <envelope>/logs/session.log. Sub-agents
            # live in <envelope>/subagents/<run id>/logs/*.log.
            envelope = log.parent.parent.resolve()
            sub_logs = sorted(log.parent.parent.glob("subagents/*/logs/*.log"))
            if not stays_in(sessions_root, envelope):
                print(f"WARN: session folder {envelope} is outside data/sessions, "
                      "sub-agent logs not copied", file=sys.stderr)
                sub_logs = []
            for sub_log in sub_logs:
                resolved_sub = sub_log.resolve()
                if not stays_in(envelope, resolved_sub):
                    print(f"WARN: sub-agent log {sub_log} resolves outside its session folder, "
                          "not copied", file=sys.stderr)
                    record["refused_subagent_logs"].append(str(sub_log.relative_to(log.parent.parent)))
                    continue
                target = destination / "subagents" / sub_log.parent.parent.name
                target.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(resolved_sub, target / sub_log.name)
                record["subagent_logs"].append(f"subagents/{target.name}/{sub_log.name}")
        else:
            status = "catalog_log_missing"
    record["status"] = status

    destination.mkdir(parents=True, exist_ok=True)
    (destination / "actor-log.json").write_text(json.dumps(record, indent=2) + "\n")
    return record


def session_token_pattern(session_id):
    """Match `session=<id>` or `sessionId=<id>` as a whole token.

    The token ends at a space or at the end of the line. A plain substring
    test would let `signalr/a` match `signalr/a-2`.
    """
    return re.compile(r"(?<![A-Za-z0-9_])session(?:Id)?=" + re.escape(session_id) + r"(?=\s|$)")


def daemon_session_lines(log_path, session_id):
    pattern = session_token_pattern(session_id)
    with open(log_path, errors="replace") as handle:
        return [line for line in handle if pattern.search(line)]


def derive_seed(run_seed, case_name, trial):
    """Give each (case, trial) its own stable seed.

    A run filtered to one case then picks the same variants as the full run.
    The hash is fixed (SHA-256), so the value does not change between
    machines or Python versions.
    """
    material = f"{run_seed}\0{case_name}\0{trial}".encode()
    return int.from_bytes(hashlib.sha256(material).digest()[:4], "big")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)

    discover = commands.add_parser("discover-session", help="print the CLI session log new since a snapshot")
    discover.add_argument("--logs-dir", type=Path, required=True)
    discover.add_argument("--known", type=Path, required=True,
                          help="file with the base names that existed before the CLI call, one for each line")

    actor = commands.add_parser("actor-log", help="copy the actor log of a session")
    actor.add_argument("--home", type=Path, required=True, help="EVAL_HOME")
    actor.add_argument("--session", required=True)
    actor.add_argument("--dest", type=Path, required=True)

    daemon = commands.add_parser("daemon-session", help="print daemon lines that carry a session token")
    daemon.add_argument("--log", type=Path, required=True)
    daemon.add_argument("--session", required=True)

    seed = commands.add_parser("derive-seed", help="print the seed of one (case, trial)")
    seed.add_argument("--seed", type=int, required=True)
    seed.add_argument("--case", required=True)
    seed.add_argument("--trial", type=int, required=True)

    args = parser.parse_args(argv)

    if args.command == "discover-session":
        known = {line.rstrip("\n") for line in args.known.read_text().splitlines() if line.strip()}
        code, result = discover_session(args.logs_dir, known)
        print(json.dumps(result))
        return code
    if args.command == "actor-log":
        print(json.dumps(collect_actor_log(args.home, args.session, args.dest)))
        return 0
    if args.command == "daemon-session":
        sys.stdout.writelines(daemon_session_lines(args.log, args.session))
        return 0
    print(derive_seed(args.seed, args.case, args.trial))
    return 0


if __name__ == "__main__":
    sys.exit(main())
