-- V0005: Active Directory connections and object inventory, password reset, viewable enrollment codes.
SET search_path = zentriq, public;

-- ---- Enrollment codes can be shown again (encrypted with the installation credential key) -----------
ALTER TABLE agent_enrollment_tokens ADD COLUMN code_protected text;

-- ---- Active Directory ---------------------------------------------------------------------------------
-- A connection holds everything needed to authenticate to a domain controller. The bind password is
-- encrypted (AES-256-GCM, installation credential key) and never returned by the API.
ALTER TABLE directory_connections
    ADD COLUMN server_host      text,                     -- DC IP address or host name used to connect
    ADD COLUMN dc_name          text,                     -- DC host name (for Kerberos/TLS name checks); optional
    ADD COLUMN port             integer NOT NULL DEFAULT 636 CHECK (port BETWEEN 1 AND 65535),
    ADD COLUMN base_dn          text,                     -- domain or OU to read, e.g. DC=corp,DC=example,DC=com
    ADD COLUMN username         text,
    ADD COLUMN secret_protected text,
    ADD COLUMN verify_certificate boolean NOT NULL DEFAULT true,
    ADD COLUMN inactive_days    integer NOT NULL DEFAULT 90,
    ADD COLUMN last_sync_at     timestamptz,
    ADD COLUMN last_sync_status text,
    ADD COLUMN last_error       text,
    ADD COLUMN counts           jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN created_by       uuid REFERENCES users(id);
ALTER TABLE directory_connections DROP CONSTRAINT IF EXISTS directory_connections_transport_check;
ALTER TABLE directory_connections ADD CONSTRAINT directory_connections_transport_check CHECK (transport IN ('ldaps','ldap','starttls'));
CREATE UNIQUE INDEX ux_directory_connections_name ON directory_connections (lower(name));

ALTER TABLE directory_objects
    ADD COLUMN name                text,
    ADD COLUMN display_name        text,
    ADD COLUMN mail                text,
    ADD COLUMN user_principal_name text,
    ADD COLUMN department          text,
    ADD COLUMN title               text,
    ADD COLUMN description         text,
    ADD COLUMN parent_dn           text,
    ADD COLUMN member_of           text[] NOT NULL DEFAULT '{}',
    ADD COLUMN when_created        timestamptz,
    ADD COLUMN password_last_set   timestamptz,
    ADD COLUMN operating_system_version text,
    ADD COLUMN asset_id            uuid REFERENCES assets(id) ON DELETE SET NULL;
CREATE INDEX ix_directory_objects_class ON directory_objects (connection_id, object_class);

-- ---- Password reset (self-service by e-mail) -------------------------------------------------------------
CREATE TABLE password_reset_tokens (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash   bytea NOT NULL UNIQUE,            -- SHA-256 of the secret sent by e-mail
    expires_at   timestamptz NOT NULL,
    used_at      timestamptz,
    requested_ip text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
INSERT INTO system_settings (key, value) VALUES
 ('smtp', '{"enabled":false}'),
 ('security.password_reset_minutes', '30')
ON CONFLICT (key) DO NOTHING;

INSERT INTO permissions (code, description) VALUES
 ('directory.manage', 'Configure Active Directory connections and run directory synchronisation')
ON CONFLICT (code) DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code) VALUES ('admin', 'directory.manage'), ('discovery', 'directory.manage')
ON CONFLICT DO NOTHING;
