-- Netclaw SQLite migration 002
-- Tracks one-time data migrations.

CREATE TABLE IF NOT EXISTS data_migrations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    description TEXT,
    executed_at INTEGER NOT NULL DEFAULT (unixepoch())
);

CREATE INDEX IF NOT EXISTS data_migrations_name_idx ON data_migrations(name);
