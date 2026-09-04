-- Immutable storage roots for sessions that use the versioned storage layout.
CREATE TABLE IF NOT EXISTS session_storage_bindings (
    session_id      TEXT NOT NULL PRIMARY KEY,
    layout_version  INTEGER NOT NULL,
    envelope_root   TEXT NOT NULL UNIQUE,
    created_at      INTEGER NOT NULL
);
