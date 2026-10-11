#!/usr/bin/env bash
# Check the persisted input after the terminal exits without a model-response wait.
set -euo pipefail
python3 - <<'PYTHON'
import json
import os
import sqlite3
from pathlib import Path

home = Path(os.environ["NETCLAW_HOME"])
assert json.loads((home / "sessions-empty.json").read_text()) == [], "Empty chats created a session."
sessions = json.loads((home / "sessions-final.json").read_text())
assert len(sessions) == 1, f"Expected one session after resume; found {len(sessions)}."
database = home / "netclaw.db"
assert database.is_file(), f"The journal database is absent: {database}"
with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as connection:
    rows = connection.execute(
        "SELECT persistence_id FROM journal WHERE manifest = ? AND instr(message, ?) > 0",
        ("ia-v1", b"native-admission-before-quit-marker"),
    ).fetchall()
    resumed = connection.execute(
        "SELECT persistence_id FROM journal WHERE manifest = ? AND instr(message, ?) > 0",
        ("ia-v1", b"native-resume-marker"),
    ).fetchall()
    turns = connection.execute(
        "SELECT COUNT(*) FROM journal WHERE manifest = ? AND persistence_id = ?",
        ("tr-v1", rows[0][0] if rows else ""),
    ).fetchone()[0]
assert len(rows) == 1, f"Expected one durable input after Enter then Ctrl+Q; found {len(rows)}."
assert resumed == rows, "Resume used another session or duplicated an input."
assert turns == 2, f"Expected two durable turns after CLI exit and resume; found {turns}."
print(f"chat-admission-quit: no empty sessions; two durable inputs and turns in {rows[0][0]}")
PYTHON
