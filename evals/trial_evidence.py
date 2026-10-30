#!/usr/bin/env python3
"""Evidence helpers for the eval harness. Bash calls these once for each trial.

Each command reads raw sources and prints a small result. None of them judges
a trial. The commands:

  discover-session  Find the CLI session log that a trial created.
  actor-log         Copy the actor log of a session, and its sub-agent logs.
  daemon-session    Print the daemon lines that carry a session id token.
  daemon-snapshot   Save the line offset of each canonical daemon segment.
  daemon-window     Print new daemon lines after a snapshot, in segment order.
  memory-ids        Write the memory snapshot (status and the ids of unseeded memories).
  derive-seed       Derive the seed of one (case, trial) from the run seed.
  child-results     Print the child results that the parent read, from the CLI session log.
  parent-stdout     Print the plain-text stdout without the tool result text.
"""

import argparse
from datetime import date
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


DAEMON_FILE_NAME = re.compile(r"daemon-([0-9]{4}-[0-9]{2}-[0-9]{2})(?:\.([1-9][0-9]{0,9}))?\.log")


def daemon_segment_key(name):
    """Match the exact date and Int32 segment contract of DaemonLogFileName."""
    match = DAEMON_FILE_NAME.fullmatch(name)
    if match is None:
        return None
    segment = int(match[2] or "0")
    if segment > 2147483647:
        return None
    try:
        return date.fromisoformat(match[1]), segment
    except ValueError:
        return None


def daemon_segments(logs_dir):
    files = [(daemon_segment_key(path.name), path) for path in logs_dir.iterdir() if path.is_file()]
    files = sorted((key, path) for key, path in files if key is not None)
    if not files:
        raise ValueError(f"no daemon log with a canonical file name in {logs_dir}")
    return [path for _, path in files]


def snapshot_daemon_logs(logs_dir, destination):
    offsets = {}
    for path in daemon_segments(logs_dir):
        with path.open("rb") as handle:
            offsets[path.name] = sum(1 for _ in handle)
    destination.write_text(json.dumps(offsets, indent=2) + "\n", encoding="utf-8")
    return logs_dir / next(reversed(offsets))


def daemon_window(logs_dir, baseline_path):
    """Read each segment once. A lost baseline file or a shorter file is an error."""
    offsets = json.loads(baseline_path.read_text(encoding="utf-8"))
    if not isinstance(offsets, dict) or not offsets or any(
            daemon_segment_key(name) is None or type(count) is not int or count < 0
            for name, count in offsets.items()):
        raise ValueError(f"invalid daemon baseline: {baseline_path}")
    paths = daemon_segments(logs_dir)
    if set(offsets) - {path.name for path in paths}:
        raise ValueError("a daemon baseline segment is absent")
    lines = []
    segments = []
    for path in paths:
        before = offsets.get(path.name, 0)
        with path.open("rb") as handle:
            current = handle.readlines()
        if len(current) < before:
            raise ValueError(f"daemon segment is shorter than its baseline: {path.name}")
        lines.extend(current[before:])
        segments.append({"name": path.name, "before": before, "after": len(current)})
    # Keep the original report fields for consumers of assert.json.
    initial = max(offsets, key=daemon_segment_key)
    report = {"daemon_log": initial, "window_lines_before": offsets[initial],
              "window_lines_after": next(item["after"] for item in segments if item["name"] == initial),
              "extra_daemon_logs": [path.name for path in paths if path.name not in offsets],
              "daemon_segments": segments}
    return lines, report


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
    workspace data. For a sub-agent log, the resolved target must be a `*.log`
    file too. The data root also holds `keys/` and `config/`, and the
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
                # The name of the link is not enough. The target must be a
                # *.log file too, because the session folder also holds tmp/
                # and artifacts/ data that is not a log.
                if not stays_in(envelope, resolved_sub) or resolved_sub.suffix != ".log":
                    print(f"WARN: target of sub-agent log {sub_log} is not a *.log file in its "
                          "session folder, not copied", file=sys.stderr)
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


# The harness seeds its memories with ids that start with doc-eval-
# (evals/fixtures/eval-memories.json). The daemon makes every other id.
SEEDED_MEMORY_PREFIX = "doc-eval-"


def memory_snapshot(db_path):
    """Return the memory snapshot of the database as a dict: status, ids, and error.

    `ids` lists every memory (document and record) that the harness did not
    seed. The harness takes this list before each trial, after it reset the
    memory store. On a clean store the list is empty. An id on it means that
    something wrote a memory after the reset. The database is read-only here,
    because the daemon holds it open.

    Status is "ok", "no_memory_db" (the daemon has not created the file), or
    "memory_db_unreadable" (`error` holds the SQLite message). Both findings
    leave `ids` as None. The snapshot is always written, so the trial evidence
    records a snapshot that failed, and trial_verdict.py gives that trial no
    verdict. A table that does not exist yet holds no memory.
    """
    if not db_path.is_file():
        return {"status": "no_memory_db", "ids": None, "error": None}
    ids = []
    try:
        database = sqlite3.connect(db_path.resolve().as_uri() + "?mode=ro", uri=True)
        try:
            tables = {row[0] for row in database.execute("SELECT name FROM sqlite_master WHERE type = 'table'")}
            for table, column in (("memory_documents", "document_id"), ("memory_records", "record_id")):
                if table in tables:
                    ids += [row[0] for row in database.execute(f"SELECT {column} FROM {table}")]
        finally:
            database.close()
    except sqlite3.Error as error:
        return {"status": "memory_db_unreadable", "ids": None, "error": str(error)}
    return {"status": "ok", "ids": sorted(i for i in ids if not i.startswith(SEEDED_MEMORY_PREFIX)), "error": None}


def derive_seed(run_seed, case_name, trial):
    """Give each (case, trial) its own stable seed.

    A run filtered to one case then picks the same variants as the full run.
    The hash is fixed (SHA-256), so the value does not change between
    machines or Python versions.
    """
    material = f"{run_seed}\0{case_name}\0{trial}".encode()
    return int.from_bytes(hashlib.sha256(material).digest()[:4], "big")


# A CLI session log record starts with "[<ISO time>] KIND: ". The lines after
# it, up to the next record, belong to the same record.
# HeadlessChannel.Log writes "[{GetUtcNow():o}] KIND: ". The "o" format of a
# UTC DateTimeOffset has 7 fraction digits and the offset +00:00. A line in any
# other format is not a record start.
CLI_RECORD_START = re.compile(
    r"^\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00)\] ([A-Z_]+): ", re.MULTILINE)
CLI_TOOL_RECORD = re.compile(r"(\S+) call_id=(\S+) (args|result)=(.*)\Z", re.DOTALL)

# Since #2515 a child result reaches the parent as an admitted notice. The
# runtime writes the result to <run folder>/result.md (ChildRunDocuments), and
# the parent model reads that file with a real file_read call. The notice
# holds no child output, so the file_read result is the only record of the
# child answer that the parent received.
CHILD_RESULT_HEADER = "# Child result\n"
CHILD_RESULT_FILE = re.compile(r"(?:^|/)subagents/([0-9a-f]+)/result\.md\Z")
# A ranged file_read (StartLine or Limit above 0) puts "{n,6}\t" before each
# line (FileReadTool.ReadLinesAsync). A whole-file read does not.
RANGED_LINE_NUMBER = re.compile(r"^\s*\d+\t", re.MULTILINE)
CHILD_RESULT_FIELD = re.compile(r"^- (agent|run|outcome): (.*)$", re.MULTILINE)
CHILD_RESULT_OUTPUT = "\n## Output\n"
CHILD_RESULT_WORKING_CONTEXT = "\n## Working context\n"


def cli_records(text):
    """Split a CLI session log into (kind, body) pairs.

    The logger ends each record with one newline. The body keeps the newlines
    inside the record and drops that last one.

    A tool result can quote lines of a CLI session log, for example when the
    agent reads its own log. A quoted line looks like a record start. The
    logger writes records in time order (UTC, one fixed format), so inside a
    TOOL_RESULT a line whose time is earlier than the time of that record is
    part of the body, not a new record. The rule applies only to TOOL_RESULT.
    If the clock steps back after a tool result, the next record joins that
    result, the stdout then does not hold the result, and parent-stdout fails
    loudly.
    """
    starts = []
    for match in CLI_RECORD_START.finditer(text):
        if starts and starts[-1].group(2) == "TOOL_RESULT" and match.group(1) < starts[-1].group(1):
            continue
        starts.append(match)
    records = []
    for index, start in enumerate(starts):
        end = starts[index + 1].start() if index + 1 < len(starts) else len(text)
        body = text[start.end():end]
        if body.endswith("\n"):
            body = body[:-1]
        records.append((start.group(2), body))
    return records


def cli_tool_records(text):
    """Yield (kind, tool, call_id, payload) for each TOOL_CALL and TOOL_RESULT record."""
    for kind, body in cli_records(text):
        if kind not in ("TOOL_CALL", "TOOL_RESULT"):
            continue
        match = CLI_TOOL_RECORD.match(body)
        if match:
            yield kind, match.group(1), match.group(2), match.group(4)


def call_arguments(arguments):
    """Return the arguments of a tool call as a dict. Text that is not a JSON object gives {}."""
    try:
        values = json.loads(arguments)
    except ValueError:
        return {}
    return values if isinstance(values, dict) else {}


def call_path(arguments):
    """Return the Path argument of a tool call, or None."""
    values = call_arguments(arguments)
    path = values.get("Path", values.get("path"))
    return path if isinstance(path, str) else None


def is_ranged_read(arguments):
    """True when file_read gets StartLine or Limit above 0, as FileReadTool treats them."""
    values = call_arguments(arguments)
    for name in ("StartLine", "Limit"):
        value = values.get(name, values.get(name[0].lower() + name[1:]))
        if isinstance(value, int) and not isinstance(value, bool) and value > 0:
            return True
    return False


def accepted_spawn_runs(text):
    """Return the run ids of the spawn_agent results with state "accepted"."""
    runs = set()
    for kind, tool, _, payload in cli_tool_records(text):
        if kind != "TOOL_RESULT" or tool != "spawn_agent":
            continue
        try:
            result = json.loads(payload)
        except ValueError:
            continue
        if isinstance(result, dict) and result.get("state") == "accepted" and isinstance(result.get("run_id"), str):
            runs.add(result["run_id"])
    return runs


def parse_child_result(body):
    """Parse the text of a result.md file. Return a dict, or None if the text is not one."""
    if not body.startswith(CHILD_RESULT_HEADER):
        return None
    output_at = body.find(CHILD_RESULT_OUTPUT)
    if output_at < 0:
        return None
    fields = dict(CHILD_RESULT_FIELD.findall(body[:output_at]))
    if not {"agent", "run", "outcome"} <= fields.keys():
        return None
    output = body[output_at + len(CHILD_RESULT_OUTPUT):]
    # The runtime writes the working context last. It lists folders and
    # files, not the child answer.
    context_at = output.rfind(CHILD_RESULT_WORKING_CONTEXT)
    if context_at >= 0:
        output = output[:context_at]
    return {"agent": fields["agent"], "run_id": fields["run"], "outcome": fields["outcome"],
            "output": output.strip()}


def child_results(text):
    """Return the child results that the parent read, in log order.

    A result counts only when all of these are true:
      - a spawn_agent result accepted the run;
      - the parent called file_read on <...>/subagents/<that run>/result.md;
      - the result of that call is a result.md text for the same run.
    The spawn_agent result is only "accepted" and holds no answer.
    """
    runs = accepted_spawn_runs(text)
    reads = {}
    results = []
    for kind, tool, call_id, payload in cli_tool_records(text):
        if tool != "file_read":
            continue
        if kind == "TOOL_CALL":
            path = call_path(payload)
            match = CHILD_RESULT_FILE.search(path) if path else None
            if match and match.group(1) in runs:
                reads[call_id] = (match.group(1), path, is_ranged_read(payload))
            continue
        if call_id not in reads:
            continue
        run_id, path, ranged = reads.pop(call_id)
        parsed = parse_child_result(RANGED_LINE_NUMBER.sub("", payload) if ranged else payload)
        if parsed is None or parsed["run_id"] != run_id:
            continue
        results.append({**parsed, "call_id": call_id, "path": path})
    return results


class ParentStdoutError(Exception):
    pass


# The CLI redacts each session log record (HeadlessChannel), but stdout prints
# the raw tool result. A redacted part of a logged result matches any text.
REDACTED = "***REDACTED***"


def find_block(stdout, block, at):
    """Return the (start, end) of block in stdout from at, or None."""
    if REDACTED not in block:
        found = stdout.find(block, at)
        return None if found < 0 else (found, found + len(block))
    match = re.compile(r"[\s\S]*?".join(re.escape(part) for part in block.split(REDACTED))).search(stdout, at)
    return None if match is None else (match.start(), match.end())


def parent_stdout(stdout, text):
    """Return the plain-text stdout without the text of each tool result.

    The CLI prints a tool result as "[tool:result] <tool> → <result>". The
    result can have many lines, and the parent text follows it with no
    marker, so a line filter cannot find its end. The CLI session log holds
    the same result text, so this removes that exact text. Every tool result
    goes, whatever the tool: a child result read, a check_agent_run status, a
    skill_load or skill_read_resource text, and a file or command output are
    all text that the parent did not write.

    The blocks are removed in log order, each after the one before it. A
    block that the stdout does not hold raises an error: its text could then
    count as parent text. The log masks secrets in a result, so a masked part
    matches the raw text in stdout.
    """
    at = 0
    for kind, tool, call_id, payload in cli_tool_records(text):
        if kind != "TOOL_RESULT":
            continue
        block = f"[tool:result] {tool} \u2192 {payload}\n"
        span = find_block(stdout, block, at)
        if span is None:
            raise ParentStdoutError(f"the stdout does not hold the {tool} result of call {call_id}")
        stdout = stdout[:span[0]] + stdout[span[1]:]
        at = span[0]
    return stdout


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

    snapshot = commands.add_parser("daemon-snapshot", help="save per-segment line offsets and print the newest path")
    snapshot.add_argument("--logs-dir", type=Path, required=True)
    snapshot.add_argument("--dest", type=Path, required=True)

    window = commands.add_parser("daemon-window", help="print daemon lines after per-segment offsets")
    window.add_argument("--logs-dir", type=Path, required=True)
    window.add_argument("--baseline", type=Path, required=True)
    window.add_argument("--assert-report", type=Path, help="write assert.json for trial evidence")
    window.add_argument("--exit-code", type=int, help="assertion status for the optional report")

    memory = commands.add_parser("memory-ids", help="write the memory snapshot of the database")
    memory.add_argument("--home", type=Path, required=True, help="EVAL_HOME")
    memory.add_argument("--dest", type=Path, required=True)
    memory.add_argument("--reset-status", default="ok",
                        help="how the memory reset before the trial ended: ok, or a reason code "
                             "such as reset_curation_busy. A value other than ok replaces the snapshot.")

    seed = commands.add_parser("derive-seed", help="print the seed of one (case, trial)")
    seed.add_argument("--seed", type=int, required=True)
    seed.add_argument("--case", required=True)
    seed.add_argument("--trial", type=int, required=True)

    children = commands.add_parser("child-results", help="print the child results that the parent read")
    children.add_argument("--cli-log", type=Path, required=True)

    parent = commands.add_parser("parent-stdout", help="print the stdout without the tool result text")
    parent.add_argument("--stdout", type=Path, required=True)
    parent.add_argument("--cli-log", type=Path, required=True)

    args = parser.parse_args(argv)

    if args.command in ("daemon-snapshot", "daemon-window"):
        try:
            if args.command == "daemon-snapshot":
                print(snapshot_daemon_logs(args.logs_dir, args.dest))
            else:
                if (args.assert_report is None) != (args.exit_code is None):
                    parser.error("--assert-report and --exit-code are required together")
                lines, report = daemon_window(args.logs_dir, args.baseline)
                if args.assert_report is not None:
                    args.assert_report.write_text(json.dumps({"exit_code": args.exit_code, **report}, indent=2) + "\n",
                                                  encoding="utf-8")
                sys.stdout.buffer.writelines(lines)
        except (OSError, ValueError) as error:
            print(f"ERROR: {args.command}: {error}", file=sys.stderr)
            return 2
        return 0

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
    if args.command == "memory-ids":
        if args.reset_status != "ok":
            # The reset did not run, so the store can hold a memory of an
            # earlier trial. A database read would say nothing about that.
            snapshot = {"status": args.reset_status, "ids": None,
                        "error": "the memory reset before the trial did not run"}
        else:
            snapshot = memory_snapshot(args.home / "data" / "netclaw.db")
        args.dest.write_text(json.dumps(snapshot, indent=2) + "\n")
        if snapshot["status"] != "ok":
            print(f"WARN: memory snapshot failed: {snapshot['status']}. The trial will be inconclusive.",
                  file=sys.stderr)
        return 0
    if args.command == "child-results":
        for result in child_results(args.cli_log.read_text(encoding="utf-8", errors="replace")):
            print(json.dumps(result))
        return 0
    if args.command == "parent-stdout":
        try:
            text = parent_stdout(args.stdout.read_text(encoding="utf-8", errors="replace"),
                                 args.cli_log.read_text(encoding="utf-8", errors="replace"))
            sys.stdout.buffer.write(text.encode("utf-8"))
        except ParentStdoutError as error:
            print(f"ERROR: parent-stdout: {error}", file=sys.stderr)
            return 1
        return 0
    print(derive_seed(args.seed, args.case, args.trial))
    return 0


if __name__ == "__main__":
    sys.exit(main())
