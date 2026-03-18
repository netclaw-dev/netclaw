-- Daily usage statistics rollup table.
-- Each row accumulates counters for a single UTC date.
-- Used by DailyStatsRecorder for the GET /api/stats?days=N endpoint.

CREATE TABLE IF NOT EXISTS daily_stats (
    date_key          TEXT NOT NULL PRIMARY KEY,
    input_tokens      INTEGER NOT NULL DEFAULT 0,
    output_tokens     INTEGER NOT NULL DEFAULT 0,
    turns             INTEGER NOT NULL DEFAULT 0,
    sessions          INTEGER NOT NULL DEFAULT 0,
    memories_formed   INTEGER NOT NULL DEFAULT 0,
    memories_recalled INTEGER NOT NULL DEFAULT 0,
    skills_loaded     INTEGER NOT NULL DEFAULT 0
);
