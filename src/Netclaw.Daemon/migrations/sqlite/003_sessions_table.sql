-- Session catalog: tracks active and historical sessions for observability.
-- Used by SessionCatalogService for the GET /api/sessions endpoint and per-session logging.

CREATE TABLE IF NOT EXISTS sessions (
    persistence_id  TEXT NOT NULL PRIMARY KEY,
    channel         TEXT NOT NULL,
    created_at      INTEGER NOT NULL,
    last_activity   INTEGER NOT NULL,
    status          TEXT NOT NULL DEFAULT 'active',
    turn_count      INTEGER NOT NULL DEFAULT 0,
    title           TEXT,
    description     TEXT,
    last_input_tokens INTEGER,
    log_path        TEXT,
    metadata        TEXT
);

CREATE INDEX IF NOT EXISTS idx_sessions_status ON sessions (status);
CREATE INDEX IF NOT EXISTS idx_sessions_last_activity ON sessions (last_activity);
