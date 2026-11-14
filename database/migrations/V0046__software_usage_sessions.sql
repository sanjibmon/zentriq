-- V0046 (build 142): software metering keeps each run of a program — when it was started and when it was closed —
-- besides the daily totals (software_usage). A run is the time a program had at least one process in a user's session,
-- sampled once a minute by the agent; ended_at is null while the program is still running.
CREATE TABLE software_usage_sessions (
    asset_id      uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    path          text NOT NULL,
    exe           text NOT NULL,
    product       text,
    user_name     text NOT NULL DEFAULT '',
    started_at    timestamptz NOT NULL,
    ended_at      timestamptz,
    last_seen_at  timestamptz NOT NULL,
    PRIMARY KEY (asset_id, path, user_name, started_at),
    CHECK (ended_at IS NULL OR ended_at >= started_at)
);
CREATE INDEX ix_software_usage_sessions_exe ON software_usage_sessions (lower(exe), started_at DESC);
CREATE INDEX ix_software_usage_sessions_asset ON software_usage_sessions (asset_id, started_at DESC);
