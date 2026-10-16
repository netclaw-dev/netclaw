#!/usr/bin/env python3
"""Seed eval memories into the netclaw SQLite database.

Usage:
    python3 seed-memories.py --db-path /path/to/netclaw.db --fixtures fixtures/eval-memories.json
    python3 seed-memories.py --reset ...   # restore the seeded state before a trial

--reset deletes every row of every memory table, then seeds the fixtures
again, in one transaction. It restores a seed that a trial changed, and it
removes every memory that the daemon wrote. Other tables of the database
(the session catalog) are not touched.
"""

import argparse
import json
import sqlite3
import sys
import time


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


def clear_memory(conn):
    tables = memory_tables(conn)
    if "memory_documents" not in tables:
        raise RuntimeError("memory_documents does not exist, so the memory store cannot be reset")
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
    args = parser.parse_args()

    with open(args.fixtures) as f:
        fixtures = json.load(f)

    # The daemon writes to the same file. Wait for its lock instead of failing.
    conn = sqlite3.connect(args.db_path, timeout=30)
    conn.row_factory = sqlite3.Row

    if args.reset:
        # DELETE opens a transaction. seed_documents commits it with the inserts.
        clear_memory(conn)
    seed_documents(conn, fixtures)
    print(
        f"[seed] seeded {len(fixtures.get('seedDocuments', []))} memories into {args.db_path}"
    )

    conn.close()


if __name__ == "__main__":
    main()
