#!/usr/bin/env python3
"""The eval results database (results.db): schema, migration, and writes.

Every value reaches SQLite as a bound parameter. A prompt or an error text
can hold quotes and newlines, and string formatting would break on them.

eval_results holds one row for each trial:

  status  pass, fail, or inconclusive. NULL on a row from before result states.
  reason  NULL for a pass and a fail. The reason token for an inconclusive trial.
  passed  1 for a pass, 0 for a fail, NULL for an inconclusive trial. NULL keeps
          an old AVG(passed) query from counting an inconclusive trial as a fail.

A CHECK constraint ties the three columns together. A row from before result
states keeps its passed value and has no status.

Commands:

  init   --db FILE    Create the tables. Migrate a results.db from an older run.
  store  --db FILE ...  Insert one eval_results row.
"""

import argparse
from pathlib import Path
import sqlite3
import sys

TABLES = """
CREATE TABLE IF NOT EXISTS eval_runs (
    run_id             TEXT PRIMARY KEY,
    started_at         TEXT NOT NULL,
    netclaw_ver        TEXT NOT NULL,
    model_id           TEXT,
    runs_per_case      INTEGER NOT NULL,
    threshold          REAL NOT NULL,
    total_cases        INTEGER NOT NULL,
    passed_cases       INTEGER NOT NULL,
    overall_score      REAL NOT NULL,
    inconclusive_cases INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS eval_metrics (
    run_id            TEXT NOT NULL REFERENCES eval_runs(run_id),
    category          TEXT NOT NULL,
    case_name         TEXT NOT NULL,
    run_number        INTEGER NOT NULL,
    turn_number       INTEGER NOT NULL DEFAULT 1,
    input_tokens      INTEGER,
    output_tokens     INTEGER,
    cached_tokens     INTEGER,
    prompt_ms         REAL,
    predicted_tok_s   REAL,
    PRIMARY KEY (run_id, case_name, run_number, turn_number)
);
"""

RESULTS_COLUMNS = """
    run_id      TEXT NOT NULL REFERENCES eval_runs(run_id),
    category    TEXT NOT NULL,
    case_name   TEXT NOT NULL,
    run_number  INTEGER NOT NULL,
    prompt_used TEXT NOT NULL,
    passed      INTEGER,
    details     TEXT,
    status      TEXT,
    reason      TEXT,
    PRIMARY KEY (run_id, case_name, run_number),
    CHECK (status IS NULL
           OR (status = 'pass' AND passed = 1)
           OR (status = 'fail' AND passed = 0)
           OR (status = 'inconclusive' AND passed IS NULL))
"""

# The columns that a results.db from before result states already has.
LEGACY_RESULTS_COLUMNS = "run_id, category, case_name, run_number, prompt_used, passed, details"

PASSED_OF_STATUS = {"pass": 1, "fail": 0, "inconclusive": None}


def column_names(database, table):
    return [row[1] for row in database.execute(f"PRAGMA table_info({table})")]


def rebuild_results_table(database):
    """Move a legacy eval_results table to the new shape. Every row is kept.

    SQLite cannot drop NOT NULL from a column, so the table is copied into a
    new one. The rows keep their passed value. Their status and reason stay
    NULL, which means: recorded before result states.
    """
    database.execute("BEGIN")
    try:
        before = database.execute("SELECT COUNT(*) FROM eval_results").fetchone()[0]
        database.execute("ALTER TABLE eval_results RENAME TO eval_results_legacy")
        database.execute(f"CREATE TABLE eval_results ({RESULTS_COLUMNS})")
        database.execute(
            f"INSERT INTO eval_results ({LEGACY_RESULTS_COLUMNS}) "
            f"SELECT {LEGACY_RESULTS_COLUMNS} FROM eval_results_legacy")
        after = database.execute("SELECT COUNT(*) FROM eval_results").fetchone()[0]
        if before != after:
            raise sqlite3.DatabaseError(f"migration kept {after} of {before} eval_results rows")
        database.execute("DROP TABLE eval_results_legacy")
    except BaseException:
        database.execute("ROLLBACK")
        raise
    database.execute("COMMIT")


def init_db(path):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    # No implicit transactions: the rebuild runs in one explicit transaction.
    database = sqlite3.connect(path, isolation_level=None)
    try:
        database.executescript(TABLES)
        if not column_names(database, "eval_results"):
            database.execute(f"CREATE TABLE eval_results ({RESULTS_COLUMNS})")
        elif "status" not in column_names(database, "eval_results"):
            rebuild_results_table(database)
        if "inconclusive_cases" not in column_names(database, "eval_runs"):
            database.execute("ALTER TABLE eval_runs ADD COLUMN inconclusive_cases INTEGER NOT NULL DEFAULT 0")
    finally:
        database.close()


def store_result(path, run_id, category, case_name, run_number, prompt, status, reason, details):
    if status not in PASSED_OF_STATUS:
        raise ValueError(f"status must be pass, fail, or inconclusive, got: {status!r}")
    database = sqlite3.connect(path)
    try:
        with database:
            database.execute(
                "INSERT INTO eval_results "
                "(run_id, category, case_name, run_number, prompt_used, passed, details, status, reason) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                (run_id, category, case_name, run_number, prompt, PASSED_OF_STATUS[status],
                 details, status, reason or None))
    finally:
        database.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)

    init = commands.add_parser("init", help="create the tables and migrate an older results.db")
    init.add_argument("--db", type=Path, required=True)

    store = commands.add_parser("store", help="insert one eval_results row")
    store.add_argument("--db", type=Path, required=True)
    store.add_argument("--run-id", required=True)
    store.add_argument("--category", required=True)
    store.add_argument("--case", required=True)
    store.add_argument("--run-number", type=int, required=True)
    # Bash passes these as --prompt=VALUE, so a value that starts with a dash is safe.
    store.add_argument("--prompt", required=True)
    store.add_argument("--status", required=True)
    store.add_argument("--reason", default="")
    store.add_argument("--details", default="")

    args = parser.parse_args(argv)
    if args.command == "init":
        init_db(args.db)
        return 0
    store_result(args.db, args.run_id, args.category, args.case, args.run_number,
                 args.prompt, args.status, args.reason, args.details)
    return 0


if __name__ == "__main__":
    sys.exit(main())
