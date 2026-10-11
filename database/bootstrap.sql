-- Executed by the migrator (as the owner role) before any versioned migration.
-- Idempotent.
CREATE SCHEMA IF NOT EXISTS zentriq;
CREATE TABLE IF NOT EXISTS zentriq.schema_migrations (
    version     integer PRIMARY KEY,
    name        text        NOT NULL,
    checksum    text        NOT NULL,
    applied_at  timestamptz NOT NULL DEFAULT now(),
    applied_by  text        NOT NULL DEFAULT current_user,
    duration_ms integer     NOT NULL,
    product_version text    NOT NULL
);
