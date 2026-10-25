-- V0027: Backup & Restore — scheduled and manual backups of the whole installation, copied to destinations (another disk,
-- a network share, FTP/FTPS, SFTP, Azure Blob Storage, Amazon S3 or S3-compatible storage, Google Cloud Storage), with
-- retention, an optional backup password, the run history, and restore from the console.
SET search_path = zentriq, public;

-- Schedule, local retention and options. The nightly backup task of earlier builds (01:30, 14 kept) becomes this default.
-- The backup password is never stored: only a key derived from it (PBKDF2), encrypted with the installation credential key.
INSERT INTO system_settings (key, value) VALUES
 ('backup.settings', jsonb_build_object(
     'schedule', jsonb_build_object('enabled', true, 'frequency', 'daily', 'time', '01:30', 'weekdays', jsonb_build_array(0), 'day', 1),
     'keepLocal', 14, 'includeExports', true, 'includeLogs', false,
     'scheduleChangedAt', to_jsonb(now())))
ON CONFLICT (key) DO NOTHING;

CREATE TABLE backup_destinations (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name          text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    kind          text NOT NULL CHECK (kind IN ('folder', 'smb', 'ftp', 'sftp', 'azure', 's3', 'gcs')),
    enabled       boolean NOT NULL DEFAULT true,
    -- Scheduled and manual backups kept at the destination (0 = all).
    keep          integer NOT NULL DEFAULT 30 CHECK (keep BETWEEN 0 AND 1000),
    settings      jsonb NOT NULL DEFAULT '{}'::jsonb,
    -- Password / secret key / account key / SAS / private key / JSON key: AES-256-GCM with the credential key ("zq-gcm1:").
    secret        text,
    last_status   text CHECK (last_status IN ('ok', 'failed')),
    last_message  text,
    last_at       timestamptz,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    created_by    uuid
);
CREATE UNIQUE INDEX backup_destinations_name_uq ON backup_destinations (lower(btrim(name)));

CREATE TABLE backup_runs (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    trigger            text NOT NULL CHECK (trigger IN ('schedule', 'manual', 'pre-upgrade', 'pre-restore', 'command')),
    label              text NOT NULL,
    file_name          text NOT NULL,
    status             text NOT NULL CHECK (status IN ('running', 'succeeded', 'partial', 'failed', 'interrupted')),
    size_bytes         bigint,
    sha256             text,
    password_protected boolean NOT NULL DEFAULT false,
    -- What the backup holds (database, configuration, certificates, keys, remote support data, files) and its copies.
    contents           jsonb,
    destinations       jsonb NOT NULL DEFAULT '[]'::jsonb,
    error              text,
    requested_by       text,
    product_version    text,
    started_at         timestamptz NOT NULL DEFAULT now(),
    finished_at        timestamptz,
    local_deleted_at   timestamptz
);
CREATE INDEX backup_runs_started_idx ON backup_runs (started_at DESC);
CREATE INDEX backup_runs_trigger_idx ON backup_runs (trigger, started_at DESC);
