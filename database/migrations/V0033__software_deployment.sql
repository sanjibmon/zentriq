-- V0033: Software Deployment — the organisation's approved software list (built from the standard software catalogue,
-- custom entries and current inventory), application control on devices with the Zentriq Agent (installation of
-- software that is not approved is reported or blocked with a message, users can request approval), device groups,
-- optional WDAC on Windows, push installation of packages, and tamper protection for the remote support agent.
SET search_path = zentriq, public;

-- ---- Device groups -------------------------------------------------------------------------------------------------
-- Membership is resolved when used: devices of the departments, of the locations and the devices named, optionally only
-- of one platform. A group can tighten application control for its devices (mode) and turn on WDAC (Windows).
CREATE TABLE device_groups (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name           text NOT NULL,
    description    text,
    department_ids uuid[] NOT NULL DEFAULT '{}',
    location_ids   uuid[] NOT NULL DEFAULT '{}',
    asset_ids      uuid[] NOT NULL DEFAULT '{}',
    platform       text CHECK (platform IN ('windows','linux')),
    app_mode       text CHECK (app_mode IN ('off','audit','block')),          -- null: the organisation's mode
    wdac_mode      text NOT NULL DEFAULT 'off' CHECK (wdac_mode IN ('off','audit','enforce')),
    created_by     uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_device_groups_name ON device_groups (lower(name));

-- ---- Software catalogue: the organisation's own entries (the standard catalogue ships with Zentriq) --------------------
CREATE TABLE software_catalog (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    key                text NOT NULL UNIQUE,
    name               text NOT NULL,
    publisher          text,
    category           text NOT NULL DEFAULT 'Other',
    platforms          text[] NOT NULL DEFAULT '{windows}',
    name_patterns      text[] NOT NULL DEFAULT '{}',
    publisher_patterns text[] NOT NULL DEFAULT '{}',
    website            text,
    notes              text,
    created_by         uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at         timestamptz NOT NULL DEFAULT now()
);

-- ---- Approved (allow) and blocked software ------------------------------------------------------------------------
CREATE TABLE app_control_rules (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    action            text NOT NULL DEFAULT 'allow' CHECK (action IN ('allow','block')),
    label             text NOT NULL,
    name_pattern      text,
    publisher_pattern text,
    version_min       text,
    version_max       text,
    sha256            text CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    platform          text CHECK (platform IN ('windows','linux')),
    catalog_key       text,
    group_id          uuid REFERENCES device_groups(id) ON DELETE CASCADE,     -- a device group, or
    asset_id          uuid REFERENCES assets(id) ON DELETE CASCADE,            -- one device (neither: all devices)
    source            text NOT NULL DEFAULT 'manual' CHECK (source IN ('manual','catalog','inventory','request','package')),
    note              text,
    expires_at        timestamptz,
    created_by        uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    CHECK (name_pattern IS NOT NULL OR publisher_pattern IS NOT NULL OR sha256 IS NOT NULL)
);
CREATE INDEX ix_app_rules_group ON app_control_rules (group_id);
CREATE INDEX ix_app_rules_asset ON app_control_rules (asset_id) WHERE asset_id IS NOT NULL;

-- ---- What agents saw: blocked or audited installations, unapproved software found, automatic removals ---------------
CREATE TABLE app_control_events (
    id          bigserial PRIMARY KEY,
    asset_id    uuid REFERENCES assets(id) ON DELETE CASCADE,
    agent_id    uuid REFERENCES agent_registrations(id) ON DELETE SET NULL,
    occurred_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    kind        text NOT NULL CHECK (kind IN ('blocked','audited','detected','removed','remove-failed','wdac')),
    name        text NOT NULL,
    publisher   text,
    version     text,
    sha256      text,
    path        text,
    user_name   text,
    platform    text,
    detail      text
);
CREATE INDEX ix_app_events_time ON app_control_events (occurred_at DESC);
CREATE INDEX ix_app_events_asset ON app_control_events (asset_id, occurred_at DESC);

-- ---- Requests from users ("Request approval" on the message) -------------------------------------------------------
CREATE TABLE app_control_requests (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    asset_id      uuid REFERENCES assets(id) ON DELETE CASCADE,
    agent_id      uuid REFERENCES agent_registrations(id) ON DELETE SET NULL,
    user_name     text,
    name          text NOT NULL,
    publisher     text,
    version       text,
    sha256        text,
    path          text,
    platform      text,
    reason        text,
    status        text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','approved','rejected')),
    created_at    timestamptz NOT NULL DEFAULT now(),
    decided_by    uuid REFERENCES users(id) ON DELETE SET NULL,
    decided_at    timestamptz,
    decision_note text,
    rule_id       uuid REFERENCES app_control_rules(id) ON DELETE SET NULL
);
CREATE INDEX ix_app_requests_status ON app_control_requests (status, created_at DESC);

-- ---- Packages and deployments ---------------------------------------------------------------------------------------
CREATE TABLE deployment_packages (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name           text NOT NULL,
    version        text,
    publisher      text,
    platform       text NOT NULL CHECK (platform IN ('windows','linux')),
    format         text NOT NULL CHECK (format IN ('msi','exe','deb','rpm')),
    file_name      text NOT NULL,
    size           bigint NOT NULL,
    sha256         text NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    install_args   text,
    uninstall_args text,
    catalog_key    text,
    detect_name    text,                                   -- the name the software registers once installed
    notes          text,
    active         boolean NOT NULL DEFAULT true,
    created_by     uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE deployment_jobs (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    package_id   uuid NOT NULL REFERENCES deployment_packages(id) ON DELETE RESTRICT,
    operation    text NOT NULL CHECK (operation IN ('install','uninstall')),
    targets      jsonb NOT NULL DEFAULT '{}'::jsonb,       -- what was chosen (devices, groups, departments, all)
    note         text,
    created_by   uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_deployment_jobs_time ON deployment_jobs (created_at DESC);

CREATE TABLE deployment_job_devices (
    job_id    uuid NOT NULL REFERENCES deployment_jobs(id) ON DELETE CASCADE,
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    action_id uuid REFERENCES asset_actions(id) ON DELETE SET NULL,
    skipped   text,                                         -- why the device was not sent the package
    PRIMARY KEY (job_id, asset_id)
);
CREATE INDEX ix_deployment_job_devices_action ON deployment_job_devices (action_id);

-- Push installation and application control reach the agent as device actions.
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_kind_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_kind_check
    CHECK (kind IN ('service','uninstall','agent-update','remote-support','agent-control','deploy'));
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_operation_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_operation_check
    CHECK (operation IN ('start','stop','restart','uninstall','update','install','pause','resume','disable','protect','unprotect'));

-- What the agent reports: the policy revision it applied, application control and WDAC on the device, and the remote
-- support agent's protection.
ALTER TABLE agent_registrations
    ADD COLUMN app_policy_revision bigint,
    ADD COLUMN app_control_state   text,
    ADD COLUMN wdac_state          text,
    ADD COLUMN remote_protection   text;

-- Organisation-wide application control. Off until an administrator turns it on (audit first, then block).
INSERT INTO system_settings (key, value) VALUES ('appcontrol.settings',
 '{"mode":"off","message":"This is unauthorized software. Please contact your administrator for further installation.","allowRequests":true,"autoRemove":false,"protectRemoteAgent":true,"revision":1}')
ON CONFLICT (key) DO NOTHING;

-- ---- Permissions ----------------------------------------------------------------------------------------------------
INSERT INTO permissions (code, description) VALUES
 ('deploy.view',   'See Software Deployment: approved software, application control events, requests, packages and deployments'),
 ('deploy.manage', 'Change the approved software list, application control, device groups and packages; decide requests')
ON CONFLICT (code) DO NOTHING;
UPDATE permissions SET description = 'Deploy (install or uninstall) software packages on devices' WHERE code = 'deploy.run';
INSERT INTO role_permissions (role_id, permission_code) SELECT 'admin', code FROM permissions ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code)
SELECT DISTINCT rp.role_id, p.code FROM role_permissions rp CROSS JOIN (VALUES ('deploy.view'), ('deploy.manage')) p(code)
 WHERE rp.permission_code = 'deploy.run'
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code)
SELECT DISTINCT rp.role_id, 'deploy.view' FROM role_permissions rp WHERE rp.permission_code IN ('security.view', 'audit.view')
ON CONFLICT DO NOTHING;
