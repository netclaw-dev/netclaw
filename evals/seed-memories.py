#!/usr/bin/env python3
"""Seed eval memories into the netclaw SQLite database.

Usage:
    python3 seed-memories.py --db-path /path/to/netclaw.db --fixtures fixtures/eval-memories.json
    python3 seed-memories.py --reset ...   # restore the seeded state before a trial

--reset deletes every row of every memory table, then seeds the fixtures
again, in one transaction. It restores a seed that a trial changed, and it
removes every memory that the daemon wrote. Other tables of the database
(the session catalog) are not touched.

Before it deletes, --reset waits until no curation checkpoint has the status
pending or processing. The curation worker writes its documents while a
checkpoint is in the status processing, and it writes them even after the
reset deleted the checkpoint row. The wait, the deletes, and the seed run in
one write transaction, so the worker cannot claim a checkpoint between the
check and the deletes.

Exit codes: 0 success. 1 any error. 3 the curation worker was still busy when
the wait ended (--idle-wait-seconds). Exit 3 leaves the database unchanged.
"""

import argparse
import json
import sqlite3
import sys
import time

EXIT_CURATION_BUSY = 3
IDLE_POLL_SECONDS = 0.25
LOCK_WAIT_SECONDS = 30


def now_ms():
    return int(time.time() * 1000)


def memory_tables(conn):
    """The names of all memory tables, including the FTS virtual tables.

    An FTS5 table has shadow tables (memory_documents_fts_data and so on).
    A DELETE on the virtual table clears them, so they are not listed.
    """
    rows = conn.execute(
        "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name LIKE 'memory\\_%' ESCAPE '\\'"
    ).fetchall()
    virtual = [name for name, sql in rows if sql and sql.upper().startswith("CREATE VIRTUAL TABLE")]
    return [
        name for name, _ in rows
        if not any(name.startswith(v + "_") for v in virtual)
    ]


def require_memory_schema(conn):
    tables = memory_tables(conn)
    for needed in ("memory_documents", "memory_checkpoints"):
        if needed not in tables:
            raise RuntimeError(f"{needed} does not exist, so the memory store cannot be reset")
    return tables


def curation_busy(conn):
    """True when the curation worker has, or is about to take, a checkpoint."""
    row = conn.execute(
        "SELECT count(*) FROM memory_checkpoints WHERE status IN ('pending', 'processing')"
    ).fetchone()
    return row[0] > 0


def begin_when_curation_is_idle(conn, wait_seconds):
    """Open a write transaction at a moment when no checkpoint is busy.

    BEGIN IMMEDIATE takes the write lock. The worker needs that lock to claim
    a checkpoint or to finish one, so the state cannot change until the caller
    commits. Returns False, with no transaction open, when the wait ended.
    """
    require_memory_schema(conn)
    deadline = time.monotonic() + wait_seconds
    while True:
        conn.execute("BEGIN IMMEDIATE")
        if not curation_busy(conn):
            return True
        conn.rollback()
        if time.monotonic() >= deadline:
            return False
        time.sleep(IDLE_POLL_SECONDS)


def clear_memory(conn):
    tables = require_memory_schema(conn)
    for table in tables:
        conn.execute(f'DELETE FROM "{table}"')


def seed_documents(conn, fixtures):
    # ON CONFLICT clauses handle upserts for anchors and documents.
    # FTS table uses INSERT OR REPLACE (no ON CONFLICT support in FTS5).
    ts = now_ms()
    for doc in fixtures["seedDocuments"]:
        conn.execute(
            """
            INSERT INTO memory_anchors(anchor_id, anchor_type, canonical_name, parent_anchor_id,
              sensitivity, recall_mode, confidence, freshness_at, status, created_at, updated_at)
            VALUES(?, ?, ?, NULL, ?, ?, ?, ?, 'active', ?, ?)
            ON CONFLICT(anchor_id) DO UPDATE SET
              anchor_type=excluded.anchor_type,
              canonical_name=excluded.canonical_name,
              sensitivity=excluded.sensitivity,
              recall_mode=excluded.recall_mode,
              confidence=excluded.confidence,
              freshness_at=excluded.freshness_at,
              updated_at=excluded.updated_at
            """,
            (
                doc["anchorId"],
                doc["anchorType"],
                doc["canonicalName"],
                doc["sensitivity"],
                doc["recallMode"],
                doc["confidence"],
                ts,
                ts,
                ts,
            ),
        )

        recall_mode = doc.get("recallMode", "auto")
        sensitivity = doc.get("sensitivity", "normal")
        if recall_mode == "auto" and sensitivity == "secret":
            recall_mode = "manual"

        conn.execute(
            """
            INSERT INTO memory_documents(document_id, anchor_id, memory_class, title, markdown_body,
              update_semantics, boundary, audience, sensitivity, recall_mode, confidence,
              freshness_at, created_at, updated_at)
            VALUES(?, ?, 'durable_fact', ?, ?, 'merge-document', 'boundary:trusted-instance', 'personal',
              ?, ?, ?, ?, ?, ?)
            ON CONFLICT(document_id) DO UPDATE SET
              title=excluded.title,
              markdown_body=excluded.markdown_body,
              boundary=excluded.boundary,
              audience=excluded.audience,
              sensitivity=excluded.sensitivity,
              recall_mode=excluded.recall_mode,
              confidence=excluded.confidence,
              freshness_at=excluded.freshness_at,
              updated_at=excluded.updated_at
            """,
            (
                doc["documentId"],
                doc["anchorId"],
                doc["title"],
                doc["markdownBody"],
                sensitivity,
                recall_mode,
                doc["confidence"],
                ts,
                ts,
                ts,
            ),
        )

        conn.execute(
            """
            INSERT OR REPLACE INTO memory_documents_fts(document_id, title, body, aliases, facets)
            VALUES(?, ?, ?, '', '')
            """,
            (doc["documentId"], doc["title"], doc["markdownBody"]),
        )
    conn.commit()


def main():
    parser = argparse.ArgumentParser(
        description="Seed eval memories into netclaw database"
    )
    parser.add_argument("--db-path", required=True, help="Path to netclaw.db")
    parser.add_argument("--fixtures", required=True, help="Path to fixtures JSON file")
    parser.add_argument("--reset", action="store_true",
                        help="Delete all memory rows first, in the same transaction as the seed")
    parser.add_argument("--idle-wait-seconds", type=float, default=30,
                        help="With --reset: how long to wait for the curation worker to go idle")
    args = parser.parse_args()

    with open(args.fixtures) as f:
        fixtures = json.load(f)

    # The daemon writes to the same file. Wait for its lock instead of failing.
    conn = sqlite3.connect(args.db_path, timeout=LOCK_WAIT_SECONDS)
    conn.row_factory = sqlite3.Row

    if args.reset:
        # The transaction stays open through the deletes. seed_documents
        # commits it with the inserts. An error before that rolls it back.
        if not begin_when_curation_is_idle(conn, args.idle_wait_seconds):
            print(
                f"[seed] a curation checkpoint was still pending or processing after "
                f"{args.idle_wait_seconds} s, so the memory store was not reset",
                file=sys.stderr,
            )
            conn.close()
            return EXIT_CURATION_BUSY
        clear_memory(conn)
    seed_documents(conn, fixtures)
    print(
        f"[seed] seeded {len(fixtures.get('seedDocuments', []))} memories into {args.db_path}"
    )

    conn.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
