-- repair-memory-schema.sql
--
-- One-off repair for operator SQLite databases created before PR #588
-- (ce6163c, "refactor(memory): remove Domain concept in favor of audience
-- isolation") merged on 2026-04-10.
--
-- #588 removed the `domain` column from memory_anchors, memory_documents,
-- memory_records, and memory_edges in the in-code schema at
-- src/Netclaw.Actors/Memory/SQLiteMemoryStore.cs, but intentionally did
-- not ship a schema migration (Netclaw is single-operator; no fleet of
-- other users to protect). CREATE TABLE IF NOT EXISTS is a no-op on
-- existing databases, so any DB created before that refactor still has
-- `domain TEXT NOT NULL` on all four tables. The INSERT statements in
-- SQLiteMemoryStore.cs do not pass a `domain` value, so every new-anchor,
-- new-document, and new-record write fails with:
--
--     SQLite Error 19: NOT NULL constraint failed: memory_anchors.domain
--
-- This script rebuilds the four affected tables with the current in-code
-- schema (no `domain` column), preserving all existing rows. It MUST stay
-- byte-compatible with the CREATE TABLE statements in
-- src/Netclaw.Actors/Memory/SQLiteMemoryStore.cs lines 28-133, so that
-- CREATE TABLE IF NOT EXISTS on next daemon start is a clean no-op.
--
-- Before running:
--   1. Stop the daemon.
--   2. Back up the database:
--        cp ~/.netclaw/netclaw.db ~/.netclaw/netclaw.db.pre-domain-drop
--   3. Dry-run against a copy first:
--        cp ~/.netclaw/netclaw.db /tmp/netclaw-test.db
--        sqlite3 /tmp/netclaw-test.db < scripts/repair-memory-schema.sql
--        sqlite3 /tmp/netclaw-test.db ".schema memory_anchors" | grep -q domain && echo FAIL || echo OK
--        sqlite3 /tmp/netclaw-test.db "SELECT count(*) FROM memory_documents;"
--   4. Apply to the live DB, then restart the daemon.
--
-- The FTS5 virtual tables (memory_documents_fts, memory_records_fts) are
-- standalone (no `content=` external-content mode), so they are
-- independent of the main tables' rowids and do not need to be touched.

PRAGMA foreign_keys = OFF;

BEGIN;

-- ----------------------------------------------------------------------
-- Drop indexes that reference the legacy `domain` column. The non-domain
-- indexes (idx_memory_documents_anchor, idx_memory_records_anchor,
-- idx_memory_edges_from, idx_memory_edges_to) stay in place and get
-- automatically dropped by SQLite when their base tables are renamed
-- below — they will be recreated at the end.
-- ----------------------------------------------------------------------
DROP INDEX IF EXISTS idx_memory_anchors_domain_mode;
DROP INDEX IF EXISTS idx_memory_documents_policy;
DROP INDEX IF EXISTS idx_memory_records_policy;

-- ----------------------------------------------------------------------
-- memory_anchors: rename → create new schema → copy → drop old
-- Column order matches SQLiteMemoryStore.cs lines 28-40 byte-for-byte.
-- ----------------------------------------------------------------------
ALTER TABLE memory_anchors RENAME TO memory_anchors_old;

CREATE TABLE memory_anchors(
  anchor_id TEXT PRIMARY KEY,
  anchor_type TEXT NOT NULL,
  canonical_name TEXT NOT NULL,
  parent_anchor_id TEXT NULL,
  sensitivity TEXT NOT NULL,
  recall_mode TEXT NOT NULL,
  confidence REAL NOT NULL,
  freshness_at INTEGER NULL,
  status TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);

INSERT INTO memory_anchors(
  anchor_id, anchor_type, canonical_name, parent_anchor_id,
  sensitivity, recall_mode, confidence, freshness_at,
  status, created_at, updated_at)
SELECT
  anchor_id, anchor_type, canonical_name, parent_anchor_id,
  sensitivity, recall_mode, confidence, freshness_at,
  status, created_at, updated_at
FROM memory_anchors_old;

DROP TABLE memory_anchors_old;

-- ----------------------------------------------------------------------
-- memory_documents: rename → create new schema → copy → drop old
-- Column order matches SQLiteMemoryStore.cs lines 42-62 byte-for-byte.
-- ----------------------------------------------------------------------
ALTER TABLE memory_documents RENAME TO memory_documents_old;

CREATE TABLE memory_documents(
  document_id TEXT PRIMARY KEY,
  anchor_id TEXT NOT NULL,
  memory_class TEXT NOT NULL DEFAULT 'durable_fact',
  title TEXT NOT NULL,
  markdown_body TEXT NOT NULL,
  aliases_json TEXT NULL,
  facets_json TEXT NULL,
  slots_json TEXT NULL,
  update_semantics TEXT NOT NULL,
  boundary TEXT NULL,
  audience TEXT NULL,
  sensitivity TEXT NOT NULL,
  recall_mode TEXT NOT NULL,
  confidence REAL NOT NULL,
  freshness_at INTEGER NULL,
  expires_at INTEGER NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY(anchor_id) REFERENCES memory_anchors(anchor_id)
);

INSERT INTO memory_documents(
  document_id, anchor_id, memory_class, title, markdown_body,
  aliases_json, facets_json, slots_json, update_semantics,
  boundary, audience, sensitivity, recall_mode, confidence,
  freshness_at, expires_at, created_at, updated_at)
SELECT
  document_id, anchor_id, memory_class, title, markdown_body,
  aliases_json, facets_json, slots_json, update_semantics,
  boundary, audience, sensitivity, recall_mode, confidence,
  freshness_at, expires_at, created_at, updated_at
FROM memory_documents_old;

DROP TABLE memory_documents_old;

-- ----------------------------------------------------------------------
-- memory_records: rename → create new schema → copy → drop old
-- Column order matches SQLiteMemoryStore.cs lines 70-90 byte-for-byte.
-- ----------------------------------------------------------------------
ALTER TABLE memory_records RENAME TO memory_records_old;

CREATE TABLE memory_records(
  record_id TEXT PRIMARY KEY,
  anchor_id TEXT NOT NULL,
  memory_class TEXT NOT NULL DEFAULT 'evidence',
  record_type TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  aliases_json TEXT NULL,
  facets_json TEXT NULL,
  slots_json TEXT NULL,
  supersedes_record_id TEXT NULL,
  update_semantics TEXT NOT NULL,
  boundary TEXT NULL,
  audience TEXT NULL,
  sensitivity TEXT NOT NULL,
  recall_mode TEXT NOT NULL,
  confidence REAL NOT NULL,
  freshness_at INTEGER NULL,
  expires_at INTEGER NULL,
  created_at INTEGER NOT NULL,
  FOREIGN KEY(anchor_id) REFERENCES memory_anchors(anchor_id)
);

INSERT INTO memory_records(
  record_id, anchor_id, memory_class, record_type, payload_json,
  aliases_json, facets_json, slots_json, supersedes_record_id,
  update_semantics, boundary, audience, sensitivity, recall_mode,
  confidence, freshness_at, expires_at, created_at)
SELECT
  record_id, anchor_id, memory_class, record_type, payload_json,
  aliases_json, facets_json, slots_json, supersedes_record_id,
  update_semantics, boundary, audience, sensitivity, recall_mode,
  confidence, freshness_at, expires_at, created_at
FROM memory_records_old;

DROP TABLE memory_records_old;

-- ----------------------------------------------------------------------
-- memory_edges: rename → create new schema → copy → drop old
-- Column order matches SQLiteMemoryStore.cs lines 98-111 byte-for-byte.
-- The edges feature has no writer in the current codebase, so this table
-- is expected to be empty, but we rebuild it anyway to remove the
-- constraint so future feature work doesn't hit the same trap.
-- ----------------------------------------------------------------------
ALTER TABLE memory_edges RENAME TO memory_edges_old;

CREATE TABLE memory_edges(
  edge_id TEXT PRIMARY KEY,
  from_anchor_id TEXT NOT NULL,
  to_anchor_id TEXT NOT NULL,
  relation_type TEXT NOT NULL,
  sensitivity TEXT NOT NULL,
  recall_mode TEXT NOT NULL,
  confidence REAL NOT NULL,
  freshness_at INTEGER NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY(from_anchor_id) REFERENCES memory_anchors(anchor_id),
  FOREIGN KEY(to_anchor_id) REFERENCES memory_anchors(anchor_id)
);

INSERT INTO memory_edges(
  edge_id, from_anchor_id, to_anchor_id, relation_type,
  sensitivity, recall_mode, confidence, freshness_at,
  created_at, updated_at)
SELECT
  edge_id, from_anchor_id, to_anchor_id, relation_type,
  sensitivity, recall_mode, confidence, freshness_at,
  created_at, updated_at
FROM memory_edges_old;

DROP TABLE memory_edges_old;

-- ----------------------------------------------------------------------
-- Recreate indexes. These match the CREATE INDEX IF NOT EXISTS
-- statements in SQLiteMemoryStore.cs lines 64-68, 92-96, 113-117.
-- Note that idx_memory_documents_anchor, idx_memory_records_anchor,
-- idx_memory_edges_from, and idx_memory_edges_to were auto-dropped
-- when their base tables were renamed above, so they must be
-- recreated here alongside the replacements for the domain-referencing
-- indexes dropped at the top.
-- ----------------------------------------------------------------------
CREATE INDEX idx_memory_documents_anchor
  ON memory_documents(anchor_id, updated_at DESC);

CREATE INDEX idx_memory_documents_policy
  ON memory_documents(sensitivity, recall_mode, updated_at DESC);

CREATE INDEX idx_memory_records_anchor
  ON memory_records(anchor_id, created_at DESC);

CREATE INDEX idx_memory_records_policy
  ON memory_records(sensitivity, recall_mode, created_at DESC);

CREATE INDEX idx_memory_edges_from
  ON memory_edges(from_anchor_id, relation_type);

CREATE INDEX idx_memory_edges_to
  ON memory_edges(to_anchor_id, relation_type);

COMMIT;

PRAGMA foreign_keys = ON;

-- Post-repair sanity: running these queries by hand should show no
-- `domain` column in the rebuilt tables.
--
--   .schema memory_anchors
--   .schema memory_documents
--   .schema memory_records
--   .schema memory_edges
--
-- And the row counts should match pre-repair counts.
