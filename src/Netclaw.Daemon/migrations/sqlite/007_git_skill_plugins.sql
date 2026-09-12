-- Netclaw SQLite migration 007
-- Stores managed Git plugin publications and rejected commit identities.

CREATE TABLE IF NOT EXISTS managed_plugin_receipts (
    source_id         TEXT NOT NULL PRIMARY KEY,
    repository_url    TEXT NOT NULL,
    source_format     TEXT NOT NULL,
    plugin_subdirectory TEXT,
    reference_kind    TEXT NOT NULL,
    reference_value   TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL,
    installed_commit  TEXT NOT NULL,
    last_observed_commit TEXT NOT NULL,
    manifest_name     TEXT NOT NULL,
    manifest_format   TEXT NOT NULL,
    installed_version TEXT,
    installed_at      INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS managed_plugin_rejections (
    source_id         TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL,
    commit_identity   TEXT NOT NULL,
    reason            TEXT NOT NULL,
    security_rejection INTEGER NOT NULL DEFAULT 0,
    alert_emitted     INTEGER NOT NULL DEFAULT 0,
    rejected_at       INTEGER NOT NULL,
    PRIMARY KEY (source_id, source_fingerprint, commit_identity)
);

CREATE INDEX IF NOT EXISTS managed_plugin_rejections_source_idx
    ON managed_plugin_rejections(source_id);
