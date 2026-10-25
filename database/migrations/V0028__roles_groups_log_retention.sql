-- V0028: Role-based access per area, custom roles, user groups, and log retention with exports.
SET search_path = zentriq, public;

-- ---- Permissions per area -------------------------------------------------------------------------------------------
-- Until now "assets.view" / "assets.manage" opened every inventory area. Each area now has its own view / manage
-- permission, so a role can, for example, see only the non-IT inventory, its dashboard and its reports.
INSERT INTO permissions (code, description) VALUES
 ('dashboard.view',     'See the dashboard (widgets show only the areas the user may see)'),
 ('reports.view',       'Open Reports & Analytics and run reports on the areas the user may see'),
 ('activity.view',      'See the device activity log'),
 ('nonit.view',         'See the non-IT asset inventory'),
 ('nonit.manage',       'Add, change, upload and dispose of non-IT assets'),
 ('accessories.view',   'See IT accessories and their stock'),
 ('accessories.manage', 'Receive, issue, return and count IT accessories'),
 ('lifecycle.view',     'See the IT asset lifecycle, people and contracts'),
 ('lifecycle.manage',   'Change lifecycle stages, assign and return assets, manage people and contracts'),
 ('cloud.view',         'See cloud and virtual infrastructure'),
 ('cloud.manage',       'Add cloud and virtualization connections, synchronise and link resources')
ON CONFLICT (code) DO NOTHING;
UPDATE permissions SET description = 'See the IT asset inventory (devices, hardware, software, network, agents)' WHERE code = 'assets.view';
UPDATE permissions SET description = 'Change IT assets (details, departments, locations, retire)' WHERE code = 'assets.manage';

-- Existing roles keep exactly what they could do: the new view permissions where they could see assets, the new manage
-- permissions where they could change them.
INSERT INTO role_permissions (role_id, permission_code)
SELECT rp.role_id, p.code FROM role_permissions rp
 CROSS JOIN (VALUES ('dashboard.view'), ('reports.view'), ('activity.view'), ('nonit.view'), ('accessories.view'), ('lifecycle.view'), ('cloud.view')) p(code)
 WHERE rp.permission_code = 'assets.view'
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code)
SELECT rp.role_id, p.code FROM role_permissions rp
 CROSS JOIN (VALUES ('nonit.manage'), ('accessories.manage'), ('lifecycle.manage'), ('cloud.manage')) p(code)
 WHERE rp.permission_code = 'assets.manage'
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code) SELECT 'admin', code FROM permissions ON CONFLICT DO NOTHING;

-- API tokens keep what they could read: report data (reports.view) and, where they could read assets, every inventory area.
UPDATE api_tokens SET permissions = ARRAY(SELECT DISTINCT unnest(permissions || ARRAY['reports.view']))
 WHERE 'reports.export' = ANY(permissions) AND NOT 'reports.view' = ANY(permissions);
UPDATE api_tokens SET permissions = ARRAY(SELECT DISTINCT unnest(permissions || ARRAY['nonit.view', 'accessories.view', 'lifecycle.view', 'cloud.view', 'activity.view']))
 WHERE 'assets.view' = ANY(permissions);

-- ---- Roles -------------------------------------------------------------------------------------------------------------
ALTER TABLE roles ADD COLUMN created_at timestamptz NOT NULL DEFAULT now(),
                  ADD COLUMN updated_at timestamptz NOT NULL DEFAULT now(),
                  ADD COLUMN created_by uuid;

-- Ready-made roles for common duties (read-only; "Duplicate" makes an editable copy).
INSERT INTO roles (id, name, description, built_in) VALUES
 ('it_assets',    'IT Asset Manager',      'IT asset inventory, hardware, software, network, IT accessories and lifecycle; dashboard, reports and activity log', true),
 ('nonit',        'Non-IT Asset Manager',  'Only the non-IT asset inventory (furniture, appliances, vehicles …): add, change, upload; its dashboard widgets and reports', true),
 ('nonit_viewer', 'Non-IT Asset Viewer',   'Read-only view of the non-IT asset inventory, its dashboard widgets and reports', true),
 ('lifecycle',    'Asset Lifecycle Manager', 'Procurement to disposal of IT assets: lifecycle, people, contracts, IT accessories; dashboard and reports', true),
 ('cloud',        'Cloud Administrator',   'Cloud and virtual infrastructure: connections, synchronisation, findings; dashboard and reports', true),
 ('security',     'Security Analyst',      'Vulnerabilities and security configuration with read access to the IT inventory; dashboard and reports', true)
ON CONFLICT (id) DO NOTHING;

INSERT INTO role_permissions (role_id, permission_code) VALUES
 ('it_assets','dashboard.view'),('it_assets','assets.view'),('it_assets','assets.manage'),('it_assets','accessories.view'),('it_assets','accessories.manage'),
 ('it_assets','lifecycle.view'),('it_assets','lifecycle.manage'),('it_assets','activity.view'),('it_assets','reports.view'),('it_assets','reports.design'),
 ('it_assets','reports.export'),('it_assets','jobs.view'),
 ('nonit','dashboard.view'),('nonit','nonit.view'),('nonit','nonit.manage'),('nonit','reports.view'),('nonit','reports.design'),('nonit','reports.export'),
 ('nonit_viewer','dashboard.view'),('nonit_viewer','nonit.view'),('nonit_viewer','reports.view'),('nonit_viewer','reports.export'),
 ('lifecycle','dashboard.view'),('lifecycle','lifecycle.view'),('lifecycle','lifecycle.manage'),('lifecycle','accessories.view'),('lifecycle','accessories.manage'),
 ('lifecycle','reports.view'),('lifecycle','reports.design'),('lifecycle','reports.export'),
 ('cloud','dashboard.view'),('cloud','cloud.view'),('cloud','cloud.manage'),('cloud','reports.view'),('cloud','reports.design'),('cloud','reports.export'),('cloud','jobs.view'),
 ('security','dashboard.view'),('security','assets.view'),('security','vuln.view'),('security','vuln.manage'),('security','vuln.except'),('security','security.view'),
 ('security','security.manage'),('security','reports.view'),('security','reports.design'),('security','reports.export'),('security','jobs.view')
ON CONFLICT DO NOTHING;

-- Sessions reload their permissions when this changes (role or group changes), without signing the user out.
ALTER TABLE users ADD COLUMN access_stamp uuid NOT NULL DEFAULT gen_random_uuid();

-- ---- User groups ------------------------------------------------------------------------------------------------------
-- A group carries roles; its members get them in addition to their own roles.
CREATE TABLE user_groups (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    description text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    created_by  uuid
);
CREATE UNIQUE INDEX user_groups_name_uq ON user_groups (lower(btrim(name)));
CREATE TABLE user_group_members (
    group_id uuid NOT NULL REFERENCES user_groups(id) ON DELETE CASCADE,
    user_id  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    PRIMARY KEY (group_id, user_id)
);
CREATE INDEX user_group_members_user_idx ON user_group_members (user_id);
CREATE TABLE user_group_roles (
    group_id uuid NOT NULL REFERENCES user_groups(id) ON DELETE CASCADE,
    role_id  text NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    PRIMARY KEY (group_id, role_id)
);

-- Every role a user has, directly or through a group.
CREATE VIEW user_role_grants AS
    SELECT ur.user_id, ur.role_id, NULL::uuid AS group_id FROM user_roles ur
    UNION ALL
    SELECT m.user_id, gr.role_id, m.group_id FROM user_group_members m JOIN user_group_roles gr ON gr.group_id = m.group_id;

-- ---- Log retention and exports ------------------------------------------------------------------------------------------
-- Audit log and device activity log are kept one year by default. Before older entries are deleted they are written to an
-- archive file (CSV in a zip, with checksums), and archives can also be made every 3, 6 or 12 months, or by hand.
INSERT INTO system_settings (key, value) VALUES
 ('logs.settings', jsonb_build_object(
     'auditRetentionDays', 365, 'activityRetentionDays', 365, 'archiveBeforeDelete', true,
     'schedule', jsonb_build_object('enabled', false, 'everyMonths', 3, 'includeAudit', true, 'includeActivity', true),
     'destinationId', NULL))
ON CONFLICT (key) DO NOTHING;

CREATE TABLE log_archives (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    trigger        text NOT NULL CHECK (trigger IN ('manual', 'schedule', 'before-delete')),
    period_from    timestamptz NOT NULL,
    period_to      timestamptz NOT NULL,
    include_audit  boolean NOT NULL DEFAULT true,
    include_activity boolean NOT NULL DEFAULT true,
    file_name      text,
    size_bytes     bigint,
    sha256         text,
    audit_rows     bigint,
    activity_rows  bigint,
    status         text NOT NULL CHECK (status IN ('queued', 'running', 'succeeded', 'failed')),
    copy           jsonb,
    error          text,
    requested_by   text,
    created_at     timestamptz NOT NULL DEFAULT now(),
    finished_at    timestamptz
);
CREATE INDEX log_archives_created_idx ON log_archives (created_at DESC);
CREATE INDEX log_archives_period_idx ON log_archives (trigger, period_from);

-- Audit events stay append-only for the application. Old entries can be removed only through this function, which only
-- the database owner (the Zentriq maintenance task) may run, and never for the last 90 days. The removal is noted in the
-- audit log itself with the hash of the last removed entry, so the chain can still be checked from there.
CREATE OR REPLACE FUNCTION audit_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND current_setting('zentriq.audit_purge', true) = 'on' THEN RETURN OLD; END IF;
    RAISE EXCEPTION 'zentriq: % on % is not permitted', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END $$;
DROP TRIGGER trg_audit_immutable ON audit_events;
CREATE TRIGGER trg_audit_immutable BEFORE UPDATE OR DELETE ON audit_events FOR EACH ROW EXECUTE FUNCTION audit_immutable();

CREATE OR REPLACE FUNCTION purge_audit_events(cutoff timestamptz) RETURNS bigint
LANGUAGE plpgsql SET search_path = zentriq, public AS $$
DECLARE n bigint; last_hash bytea; last_id bigint;
BEGIN
    IF cutoff > now() - interval '90 days' THEN
        RAISE EXCEPTION 'zentriq: audit events of the last 90 days cannot be removed' USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT id, hash INTO last_id, last_hash FROM audit_events WHERE occurred_at < cutoff ORDER BY id DESC LIMIT 1;
    IF last_id IS NULL THEN RETURN 0; END IF;
    PERFORM set_config('zentriq.audit_purge', 'on', true);
    DELETE FROM audit_events WHERE id <= last_id;
    GET DIAGNOSTICS n = ROW_COUNT;
    PERFORM set_config('zentriq.audit_purge', 'off', true);
    INSERT INTO audit_events (actor_name, action, target, outcome, detail)
    VALUES ('Zentriq maintenance', 'audit.purged', 'audit log', 'Success',
            jsonb_build_object('before', cutoff, 'rows', n, 'lastRemovedId', last_id, 'lastRemovedHash', encode(last_hash, 'hex')));
    RETURN n;
END $$;
REVOKE ALL ON FUNCTION purge_audit_events(timestamptz) FROM PUBLIC;
