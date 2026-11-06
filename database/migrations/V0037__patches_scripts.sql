-- V0037: Release 2 of the Endpoint Central parity plan.
--  * Patch Management: the Zentriq Agent scans for missing operating system updates (Windows Update Agent on Windows,
--    apt / dnf / yum / zypper on Linux) and installs the chosen ones; approvals, automatic deployment policies with a
--    maintenance window and restart behaviour, deployments and the patch state of every device.
--  * Scripts & Remediation: a script library (PowerShell, Command Prompt, Bash, sh, Python), runs on devices with the
--    output and exit code of every device, and scheduled remediations (a detection script decides whether the fix runs).

-- ---- Device actions -----------------------------------------------------------------------------------------------
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_kind_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_kind_check
    CHECK (kind IN ('service','uninstall','agent-update','remote-support','agent-control','deploy','patch','script'));
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_operation_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_operation_check
    CHECK (operation IN ('start','stop','restart','uninstall','update','install','pause','resume','disable','protect','unprotect','scan','run'));

-- Output of scripts (standard output and error, at most 64 KB each, and the exit code).
CREATE TABLE asset_action_outputs (
    action_id   uuid PRIMARY KEY REFERENCES asset_actions(id) ON DELETE CASCADE,
    exit_code   integer,
    stdout      text,
    stderr      text,
    outcome     text,                                   -- script: ok / failed; remediation: compliant / remediated / failed
    duration_ms integer,
    received_at timestamptz NOT NULL DEFAULT now()
);

-- ---- Patch management -------------------------------------------------------------------------------------------
CREATE TABLE patches (
    id             text PRIMARY KEY,                    -- Windows: update ID; Linux: linux:<package>:<version>
    platform       text NOT NULL CHECK (platform IN ('windows','linux')),
    kb             text,                                -- KB article number (Windows)
    title          text NOT NULL,
    classification text NOT NULL DEFAULT 'updates' CHECK (classification IN ('critical','security','definition','updates','update-rollup','feature-pack','service-pack','driver','tools','upgrade','other')),
    severity       text NOT NULL DEFAULT 'unspecified' CHECK (severity IN ('critical','important','moderate','low','unspecified')),
    product        text,
    package        text,                                -- Linux package name
    version        text,                                -- Linux package version
    released_at    date,
    size_bytes     bigint,
    reboot         boolean,
    cves           text[] NOT NULL DEFAULT '{}',
    support_url    text,
    approval       text NOT NULL DEFAULT 'none' CHECK (approval IN ('none','approved','declined')),
    approved_by    uuid REFERENCES users(id) ON DELETE SET NULL,
    approved_at    timestamptz,
    first_seen_at  timestamptz NOT NULL DEFAULT now(),
    last_seen_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_patches_kb ON patches (kb) WHERE kb IS NOT NULL;

CREATE TABLE asset_patches (
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    patch_id     text NOT NULL REFERENCES patches(id) ON DELETE CASCADE,
    state        text NOT NULL CHECK (state IN ('missing','installed','failed','pending_reboot')),
    detected_at  timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    installed_at timestamptz,
    message      text,
    PRIMARY KEY (asset_id, patch_id)
);
CREATE INDEX ix_asset_patches_patch ON asset_patches (patch_id, state);

-- The last scan of each device.
CREATE TABLE asset_patch_scans (
    asset_id       uuid PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    scanned_at     timestamptz NOT NULL DEFAULT now(),
    ok             boolean NOT NULL,
    message        text,
    source         text,                                -- Windows Update, WSUS (server), apt, dnf, …
    reboot_pending boolean NOT NULL DEFAULT false,
    missing        integer NOT NULL DEFAULT 0
);

-- Automatic deployment: which updates go to which devices, in which maintenance window.
CREATE TABLE patch_policies (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name            text NOT NULL UNIQUE,
    enabled         boolean NOT NULL DEFAULT true,
    platform        text NOT NULL CHECK (platform IN ('windows','linux')),
    classifications text[] NOT NULL DEFAULT '{critical,security}',
    severities      text[] NOT NULL DEFAULT '{}',        -- empty: any severity
    approved_only   boolean NOT NULL DEFAULT false,
    delay_days      integer NOT NULL DEFAULT 0 CHECK (delay_days BETWEEN 0 AND 365),
    targets         jsonb NOT NULL DEFAULT '{}'::jsonb,  -- assetIds, groupIds, departmentIds, all
    days            integer[] NOT NULL DEFAULT '{0,1,2,3,4,5,6}',   -- 0 = Sunday
    start_time      time NOT NULL DEFAULT '22:00',
    window_hours    integer NOT NULL DEFAULT 4 CHECK (window_hours BETWEEN 1 AND 24),
    reboot          text NOT NULL DEFAULT 'when-needed' CHECK (reboot IN ('never','when-needed')),
    last_run_at     timestamptz,
    created_by      uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE patch_jobs (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    policy_id  uuid REFERENCES patch_policies(id) ON DELETE SET NULL,
    name       text NOT NULL,
    patch_ids  text[] NOT NULL DEFAULT '{}',             -- empty with all_missing: every missing update of the device
    all_missing boolean NOT NULL DEFAULT false,
    reboot     text NOT NULL DEFAULT 'when-needed' CHECK (reboot IN ('never','when-needed')),
    targets    jsonb NOT NULL DEFAULT '{}'::jsonb,
    note       text,
    created_by uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_patch_jobs_time ON patch_jobs (created_at DESC);

CREATE TABLE patch_job_devices (
    job_id    uuid NOT NULL REFERENCES patch_jobs(id) ON DELETE CASCADE,
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    action_id uuid REFERENCES asset_actions(id) ON DELETE SET NULL,
    patches   integer NOT NULL DEFAULT 0,
    skipped   text,
    PRIMARY KEY (job_id, asset_id)
);
CREATE INDEX ix_patch_job_devices_action ON patch_job_devices (action_id);

INSERT INTO system_settings (key, value) VALUES ('patch.settings',
 '{"autoScan":true,"scanHours":24,"rebootDelayMinutes":10,"rebootMessage":"Updates were installed on this computer. Save your work: it restarts in a few minutes."}')
ON CONFLICT (key) DO NOTHING;

-- ---- Scripts & remediation --------------------------------------------------------------------------------------
CREATE TABLE scripts (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name            text NOT NULL,
    description     text,
    category        text NOT NULL DEFAULT 'General',
    platform        text NOT NULL CHECK (platform IN ('windows','linux')),
    language        text NOT NULL CHECK (language IN ('powershell','cmd','bash','sh','python')),
    content         text NOT NULL,
    sha256          text NOT NULL,
    parameters      text,                                -- default command-line arguments
    timeout_seconds integer NOT NULL DEFAULT 600 CHECK (timeout_seconds BETWEEN 10 AND 14400),
    built_in        boolean NOT NULL DEFAULT false,
    created_by      uuid REFERENCES users(id) ON DELETE SET NULL,
    updated_by      uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_scripts_name ON scripts (lower(name));

-- Scheduled runs and remediations (a detection script runs first; the fix runs only where it reports a problem).
CREATE TABLE script_schedules (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name             text NOT NULL UNIQUE,
    enabled          boolean NOT NULL DEFAULT true,
    script_id        uuid NOT NULL REFERENCES scripts(id) ON DELETE RESTRICT,
    detect_script_id uuid REFERENCES scripts(id) ON DELETE RESTRICT,
    parameters       text,
    targets          jsonb NOT NULL DEFAULT '{}'::jsonb,
    frequency        text NOT NULL CHECK (frequency IN ('once','hourly','daily','weekly')),
    days             integer[] NOT NULL DEFAULT '{1}',   -- weekly: 0 = Sunday
    at_time          time NOT NULL DEFAULT '02:00',
    run_once_at      timestamptz,
    next_run_at      timestamptz,
    last_run_at      timestamptz,
    created_by       uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE script_jobs (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    script_id        uuid REFERENCES scripts(id) ON DELETE SET NULL,
    detect_script_id uuid REFERENCES scripts(id) ON DELETE SET NULL,
    schedule_id      uuid REFERENCES script_schedules(id) ON DELETE SET NULL,
    name             text NOT NULL,
    sha256           text NOT NULL,                      -- of the script that was sent
    parameters       text,
    targets          jsonb NOT NULL DEFAULT '{}'::jsonb,
    note             text,
    created_by       uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_script_jobs_time ON script_jobs (created_at DESC);

CREATE TABLE script_job_devices (
    job_id    uuid NOT NULL REFERENCES script_jobs(id) ON DELETE CASCADE,
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    action_id uuid REFERENCES asset_actions(id) ON DELETE SET NULL,
    skipped   text,
    PRIMARY KEY (job_id, asset_id)
);
CREATE INDEX ix_script_job_devices_action ON script_job_devices (action_id);

INSERT INTO system_settings (key, value) VALUES ('scripts.settings', '{"enabled":true,"keepOutputDays":90}')
ON CONFLICT (key) DO NOTHING;

-- ---- Permissions ----------------------------------------------------------------------------------------------------
INSERT INTO permissions (code, description) VALUES
 ('patch.view',     'View patch management: missing updates, device patch state, policies and deployments'),
 ('scripts.view',   'View the script library, runs and their output'),
 ('scripts.manage', 'Add and change scripts and scheduled remediations'),
 ('scripts.run',    'Run scripts on devices (as SYSTEM / root)')
ON CONFLICT (code) DO NOTHING;
UPDATE permissions SET description = 'Approve and decline patches; patch policies and settings' WHERE code = 'patch.approve';
UPDATE permissions SET description = 'Install patches on devices and scan devices for missing patches' WHERE code = 'patch.deploy';
INSERT INTO role_permissions (role_id, permission_code) VALUES
 ('admin', 'patch.view'), ('admin', 'scripts.view'), ('admin', 'scripts.manage'), ('admin', 'scripts.run'),
 ('patch', 'patch.view')
ON CONFLICT DO NOTHING;
