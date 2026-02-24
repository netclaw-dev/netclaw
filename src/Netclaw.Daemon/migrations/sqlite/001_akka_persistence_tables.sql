-- Netclaw SQLite migration 001
-- Initializes Akka.Persistence.Sql default SQLite tables.

CREATE TABLE IF NOT EXISTS journal (
    ordering INTEGER PRIMARY KEY AUTOINCREMENT,
    deleted INTEGER NOT NULL DEFAULT 0,
    persistence_id TEXT NOT NULL,
    sequence_number INTEGER NOT NULL,
    created INTEGER NOT NULL,
    tags TEXT,
    message BLOB NOT NULL,
    identifier INTEGER,
    manifest TEXT,
    writer_uuid TEXT,
    UNIQUE (persistence_id, sequence_number)
);

CREATE INDEX IF NOT EXISTS journal_ordering_idx ON journal (ordering);
CREATE INDEX IF NOT EXISTS journal_created_idx ON journal (created);
CREATE INDEX IF NOT EXISTS journal_persistence_id_idx ON journal (persistence_id);

CREATE TABLE IF NOT EXISTS snapshot (
    persistence_id TEXT NOT NULL,
    sequence_number INTEGER NOT NULL,
    created INTEGER NOT NULL,
    snapshot BLOB,
    manifest TEXT,
    serializer_id INTEGER,
    PRIMARY KEY (persistence_id, sequence_number)
);

CREATE INDEX IF NOT EXISTS snapshot_sequence_number_idx ON snapshot (sequence_number);
CREATE INDEX IF NOT EXISTS snapshot_created_idx ON snapshot (created);

CREATE TABLE IF NOT EXISTS journal_metadata (
    persistence_id TEXT NOT NULL,
    sequence_number INTEGER NOT NULL,
    PRIMARY KEY (persistence_id, sequence_number)
);

CREATE TABLE IF NOT EXISTS tags (
    ordering_id INTEGER NOT NULL,
    tag TEXT NOT NULL,
    sequence_nr INTEGER NOT NULL,
    persistence_id TEXT NOT NULL,
    PRIMARY KEY (ordering_id, tag)
);

CREATE INDEX IF NOT EXISTS tags_persistence_id_sequence_nr_idx ON tags (persistence_id, sequence_nr);
CREATE INDEX IF NOT EXISTS tags_tag_idx ON tags (tag);
