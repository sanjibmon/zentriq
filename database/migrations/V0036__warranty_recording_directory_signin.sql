-- V0036: Release 1 of the Endpoint Central parity plan.
--  * Online warranty lookup (Dell, Lenovo, HP) by serial number: vendor API keys, the answer per device, the warranty
--    of the asset's lifecycle record filled from it (dates entered by hand are kept unless the setting says otherwise).
--  * Remote session recording: recorded sessions indexed for playback from Remote Support › History.
--  * Console sign-in with Active Directory (LDAP / LDAPS) and Microsoft Entra ID (OpenID Connect), with directory groups
--    mapped to Zentriq roles; local accounts and two-factor authentication stay as they are.
SET search_path = zentriq, public;

-- ---- Warranty lookup -----------------------------------------------------------------------------------------------
CREATE TABLE warranty_providers (
    vendor           text PRIMARY KEY CHECK (vendor IN ('dell','lenovo','hp')),
    enabled          boolean NOT NULL DEFAULT true,
    client_id        text,
    secret_protected text,                                    -- AES-GCM with the credential key; never returned
    api_url          text,                                    -- null: the vendor's standard address
    updated_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       text,
    last_ok_at       timestamptz,
    last_error       text,
    last_error_at    timestamptz
);

CREATE TABLE warranty_lookups (
    asset_id       uuid PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    vendor         text NOT NULL,
    serial         text NOT NULL,
    status         text NOT NULL CHECK (status IN ('found','not_found','error','no_key')),
    checked_at     timestamptz NOT NULL DEFAULT now(),
    product        text,
    ship_date      date,
    warranty_start date,
    warranty_end   date,
    service_level  text,
    entitlements   jsonb NOT NULL DEFAULT '[]'::jsonb,
    message        text,
    applied        boolean NOT NULL DEFAULT false           -- the lifecycle record shows this warranty
);
CREATE INDEX ix_warranty_lookups_checked ON warranty_lookups (checked_at);

-- Where the warranty of a lifecycle record came from: entered by hand, imported, or the manufacturer.
ALTER TABLE asset_lifecycle ADD COLUMN warranty_source text CHECK (warranty_source IN ('manual','import','vendor'));
UPDATE asset_lifecycle SET warranty_source = 'manual' WHERE warranty_end IS NOT NULL;

INSERT INTO system_settings (key, value) VALUES ('warranty.settings', '{"auto":true,"refreshDays":30,"overwriteManual":false}'::jsonb)
ON CONFLICT (key) DO NOTHING;

-- ---- Remote session recording -------------------------------------------------------------------------------------
ALTER TABLE remote_sessions ADD COLUMN recorded boolean NOT NULL DEFAULT false;

CREATE TABLE remote_recordings (
    id          bigserial PRIMARY KEY,
    file_name   text NOT NULL UNIQUE,                          -- relative to Remote\recordings
    session_id  bigint REFERENCES remote_sessions(id) ON DELETE SET NULL,
    user_id     uuid REFERENCES users(id) ON DELETE SET NULL,
    user_name   text,
    node_id     text,
    asset_id    uuid REFERENCES assets(id) ON DELETE SET NULL,
    device_name text,
    protocol    integer,                                       -- 1 terminal, 2 desktop, 5 files
    started_at  timestamptz,
    ended_at    timestamptz,
    size_bytes  bigint NOT NULL DEFAULT 0,
    indexed_at  timestamptz NOT NULL DEFAULT now(),
    deleted_at  timestamptz
);
CREATE INDEX ix_remote_recordings_session ON remote_recordings (session_id);
CREATE INDEX ix_remote_recordings_time ON remote_recordings (started_at DESC);

INSERT INTO permissions (code, description) VALUES
 ('remote.recordings', 'Play back recorded remote sessions (every playback is written to the audit log)')
ON CONFLICT (code) DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code) VALUES ('admin', 'remote.recordings') ON CONFLICT DO NOTHING;

-- ---- Directory sign-in --------------------------------------------------------------------------------------------
ALTER TABLE users ADD COLUMN auth_source text NOT NULL DEFAULT 'local' CHECK (auth_source IN ('local','ldap','entra'));
ALTER TABLE users ADD COLUMN external_id text;                 -- objectGUID (Active Directory) or object ID (Entra ID)
CREATE UNIQUE INDEX ux_users_external ON users (auth_source, external_id) WHERE external_id IS NOT NULL;

CREATE TABLE auth_providers (
    id               text PRIMARY KEY CHECK (id IN ('ldap','entra')),
    enabled          boolean NOT NULL DEFAULT false,
    config           jsonb NOT NULL DEFAULT '{}'::jsonb,
    secret_protected text,                                    -- Entra ID client secret
    updated_at       timestamptz NOT NULL DEFAULT now(),
    updated_by       text
);

CREATE TABLE auth_group_mappings (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    provider   text NOT NULL CHECK (provider IN ('ldap','entra')),
    group_ref  text NOT NULL CHECK (length(btrim(group_ref)) BETWEEN 1 AND 1000),   -- group DN, Entra group object ID or app role value
    group_name text,
    role_id    text NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    created_by text
);
CREATE UNIQUE INDEX ux_auth_group_mappings ON auth_group_mappings (provider, lower(group_ref), role_id);

-- Roles a directory user has through their directory groups (replaced at each sign-in).
CREATE TABLE user_directory_roles (
    user_id  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id  text NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    provider text NOT NULL,
    PRIMARY KEY (user_id, role_id)
);

-- Every role a user has: directly, through a Zentriq group, or through a mapped directory group.
CREATE OR REPLACE VIEW user_role_grants AS
    SELECT ur.user_id, ur.role_id, NULL::uuid AS group_id FROM user_roles ur
    UNION ALL
    SELECT m.user_id, gr.role_id, m.group_id FROM user_group_members m JOIN user_group_roles gr ON gr.group_id = m.group_id
    UNION ALL
    SELECT d.user_id, d.role_id, NULL::uuid FROM user_directory_roles d;
