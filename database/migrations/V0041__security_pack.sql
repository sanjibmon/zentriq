-- V0041 (build 121): Security Pack — a separately licensed add-on (module securitypack), Windows devices with the agent.
--  * Endpoint privilege management: local administrators removed (audit or enforce), approved applications run with
--    administrator rights by standard users, time-limited administrator rights on request.
--  * BitLocker management: encryption enforced, recovery passwords escrowed (encrypted), looked up with a reason.
--  * USB device control: removable storage blocked or read-only, allowed devices by ID, file copies audited.

-- One policy per area for all devices (group_id NULL) and optionally per device group (the strictest applies).
CREATE TABLE secpack_policies (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    area        text NOT NULL CHECK (area IN ('privilege','bitlocker','usb')),
    group_id    uuid REFERENCES device_groups(id) ON DELETE CASCADE,
    settings    jsonb NOT NULL DEFAULT '{}',
    updated_by  text,
    updated_at  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_secpack_policies ON secpack_policies (area, COALESCE(group_id, '00000000-0000-0000-0000-000000000000'::uuid));

INSERT INTO system_settings (key, value, updated_at) VALUES ('securitypack.revision', '{"revision": 1}', now()) ON CONFLICT (key) DO NOTHING;
ALTER TABLE agent_registrations ADD COLUMN secpack_revision bigint;

-- ---- Endpoint privilege management -------------------------------------------------------------------------------------
-- Applications standard users may run as administrator: by file hash, by signing publisher (optionally a file name) or by path.
CREATE TABLE epm_rules (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name         text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
    kind         text NOT NULL CHECK (kind IN ('hash','publisher','path')),
    value        text NOT NULL CHECK (length(value) BETWEEN 1 AND 500),   -- sha256 (hex) | publisher (subject CN / O) | path with wildcards
    file_name    text,                                                    -- publisher rules: only this file name (wildcards)
    group_id     uuid REFERENCES device_groups(id) ON DELETE CASCADE,     -- null: every device (or the device below)
    asset_id     uuid REFERENCES assets(id) ON DELETE CASCADE,            -- one device only
    uses_left    integer CHECK (uses_left IS NULL OR uses_left >= 0),      -- null: unlimited
    expires_at   timestamptz,
    enabled      boolean NOT NULL DEFAULT true,
    note         text,
    created_by   text,
    created_at   timestamptz NOT NULL DEFAULT now()
);

-- Requests from users: run an application as administrator, or administrator rights for a while.
CREATE TABLE epm_requests (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    user_name    text NOT NULL,
    kind         text NOT NULL CHECK (kind IN ('elevate','admin')),
    file_path    text,
    file_name    text,
    sha256       text,
    publisher    text,
    product      text,
    minutes      integer,
    reason       text,
    status       text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','approved','denied')),
    decided_by   text,
    decided_at   timestamptz,
    decision     text,
    rule_id      uuid REFERENCES epm_rules(id) ON DELETE SET NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_epm_requests_open ON epm_requests (status, created_at DESC);

-- Time-limited administrator rights: the agent adds the user to the local Administrators group until ends_at.
CREATE TABLE epm_grants (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    user_name    text NOT NULL,
    starts_at    timestamptz NOT NULL DEFAULT now(),
    ends_at      timestamptz NOT NULL,
    revoked_at   timestamptz,
    revoked_by   text,
    request_id   uuid REFERENCES epm_requests(id) ON DELETE SET NULL,
    reason       text,
    created_by   text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_epm_grants_active ON epm_grants (asset_id, ends_at) WHERE revoked_at IS NULL;

CREATE TABLE epm_events (
    id           bigserial PRIMARY KEY,
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    at           timestamptz NOT NULL,
    user_name    text,
    kind         text NOT NULL CHECK (kind IN ('elevated','refused','failed','admin_added','admin_removed','member_removed','member_kept')),
    file_path    text,
    sha256       text,
    publisher    text,
    rule_id      uuid,
    detail       text
);
CREATE INDEX ix_epm_events_at ON epm_events (at DESC);
CREATE INDEX ix_epm_events_asset ON epm_events (asset_id, at DESC);

-- Members of the local Administrators group, as last reported.
CREATE TABLE device_local_admins (
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    member       text NOT NULL,
    sid          text,
    kind         text,                          -- user | group | well-known | unknown
    status       text NOT NULL,                 -- allowed | granted | removed | to_remove (audit) | kept
    reported_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (asset_id, member)
);

-- ---- BitLocker ---------------------------------------------------------------------------------------------------------
CREATE TABLE bitlocker_volumes (
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    volume       text NOT NULL,                 -- C:, D: …
    kind         text,                          -- os | fixed | removable
    protection   text,                          -- on | off | unknown
    status       text,                          -- encrypted | encrypting | decrypted | decrypting | paused | unknown
    percent      real,
    method       text,
    protectors   text[] NOT NULL DEFAULT '{}',
    tpm          text,                          -- ready | absent | not_ready
    error        text,
    reported_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (asset_id, volume)
);

-- Recovery passwords (48 digits), encrypted with the server's key; the key id (protector GUID) is what the recovery screen shows.
CREATE TABLE bitlocker_keys (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    asset_id      uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    volume        text NOT NULL,
    protector_id  text NOT NULL,
    key_protected text NOT NULL,
    escrowed_at   timestamptz NOT NULL DEFAULT now(),
    retired_at    timestamptz,                  -- the protector is no longer on the volume (rotated, removed)
    viewed_at     timestamptz,
    viewed_by     text,
    rotate_requested_at timestamptz
);
CREATE UNIQUE INDEX ux_bitlocker_keys ON bitlocker_keys (asset_id, upper(protector_id));
CREATE INDEX ix_bitlocker_keys_protector ON bitlocker_keys (upper(protector_id));

-- ---- USB device control ------------------------------------------------------------------------------------------------
CREATE TABLE usb_allowed (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    device_id   text NOT NULL CHECK (length(btrim(device_id)) BETWEEN 4 AND 400),   -- VID_xxxx&PID_yyyy[\serial] or an instance ID (wildcards)
    name        text,
    access      text NOT NULL DEFAULT 'full' CHECK (access IN ('full','readonly')),
    group_id    uuid REFERENCES device_groups(id) ON DELETE CASCADE,
    note        text,
    created_by  text,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE usb_devices (
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    device_id    text NOT NULL,
    name         text,
    vid_pid      text,
    serial       text,
    first_seen   timestamptz NOT NULL DEFAULT now(),
    last_seen    timestamptz NOT NULL DEFAULT now(),
    last_access  text,                          -- full | readonly | blocked | audit
    PRIMARY KEY (asset_id, device_id)
);

CREATE TABLE usb_events (
    id          bigserial PRIMARY KEY,
    asset_id    uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    at          timestamptz NOT NULL,
    user_name   text,
    kind        text NOT NULL CHECK (kind IN ('connected','blocked','readonly','allowed','file_written','file_renamed','file_deleted','failed')),
    device_id   text,
    device_name text,
    file_path   text,
    size        bigint,
    detail      text
);
CREATE INDEX ix_usb_events_at ON usb_events (at DESC);
CREATE INDEX ix_usb_events_asset ON usb_events (asset_id, at DESC);

-- ---- Permissions ---------------------------------------------------------------------------------------------------------
INSERT INTO permissions (code, description) VALUES
 ('privilege.view',       'Security Pack: view endpoint privilege management (administrators, rules, requests, elevations)'),
 ('privilege.manage',     'Security Pack: privilege policies, elevation rules, approve requests, grant administrator rights'),
 ('bitlocker.view',       'Security Pack: view BitLocker status of devices'),
 ('bitlocker.manage',     'Security Pack: BitLocker policies, rotate recovery keys'),
 ('bitlocker.recover',    'Security Pack: show BitLocker recovery keys (every view is audited)'),
 ('devicecontrol.view',   'Security Pack: view USB devices and file copies'),
 ('devicecontrol.manage', 'Security Pack: USB policies and allowed devices')
ON CONFLICT (code) DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code) VALUES
 ('admin', 'privilege.view'), ('admin', 'privilege.manage'), ('admin', 'bitlocker.view'), ('admin', 'bitlocker.manage'), ('admin', 'bitlocker.recover'),
 ('admin', 'devicecontrol.view'), ('admin', 'devicecontrol.manage')
ON CONFLICT DO NOTHING;
