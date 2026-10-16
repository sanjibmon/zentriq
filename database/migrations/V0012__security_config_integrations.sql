-- V0012: Security Configuration Assessment (read-only checks of Windows settings against baselines, with scores,
-- exceptions and history) and Integrations & Automation (notification channels, alert rules, delivery log,
-- read-only API tokens).
SET search_path = zentriq, public;

-- ============================================================================================
-- Security configuration
-- ============================================================================================

-- A baseline: which checks apply (with severity overrides and parameters) and to which devices.
-- definition = {"rules":[{"id":"SC-01","enabled":true,"severity":null,"params":{}}], "categories":["server"]}
CREATE TABLE sec_baselines (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name         text NOT NULL,
    description  text,
    built_in     boolean NOT NULL DEFAULT false,
    enabled      boolean NOT NULL DEFAULT true,
    definition   jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_by   uuid REFERENCES users(id) ON DELETE SET NULL,
    updated_by   uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_sec_baselines_name ON sec_baselines (lower(name));

-- The check catalogue (kept in step with the application's rule list by the evaluator), for reports and joins.
CREATE TABLE sec_rules (
    id           text PRIMARY KEY,
    title        text NOT NULL,
    category     text NOT NULL,
    severity     text NOT NULL CHECK (severity IN ('high','medium','low')),
    applies      text NOT NULL,
    reference    text,
    remediation  text,
    updated_at   timestamptz NOT NULL DEFAULT now()
);

-- Latest result of every check per device and baseline.
CREATE TABLE sec_results (
    baseline_id     uuid NOT NULL REFERENCES sec_baselines(id) ON DELETE CASCADE,
    asset_id        uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    rule_id         text NOT NULL,
    status          text NOT NULL CHECK (status IN ('pass','fail','unknown','exempt')),
    severity        text NOT NULL CHECK (severity IN ('high','medium','low')),
    actual          text,
    first_failed_at timestamptz,          -- start of the current failing period
    changed_at      timestamptz NOT NULL DEFAULT now(),
    evaluated_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (baseline_id, asset_id, rule_id)
);
CREATE INDEX ix_sec_results_rule ON sec_results (rule_id, status);
CREATE INDEX ix_sec_results_asset ON sec_results (asset_id);
CREATE INDEX ix_sec_results_changed ON sec_results (changed_at) WHERE status = 'fail';

-- Score per device and baseline (0–100, NULL when nothing could be checked).
CREATE TABLE sec_scores (
    baseline_id  uuid NOT NULL REFERENCES sec_baselines(id) ON DELETE CASCADE,
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    score        integer CHECK (score BETWEEN 0 AND 100),
    passed       integer NOT NULL DEFAULT 0,
    failed       integer NOT NULL DEFAULT 0,
    failed_high  integer NOT NULL DEFAULT 0,
    unknown      integer NOT NULL DEFAULT 0,
    exempt       integer NOT NULL DEFAULT 0,
    evaluated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (baseline_id, asset_id)
);

-- Daily compliance trend per baseline.
CREATE TABLE sec_history (
    day          date NOT NULL,
    baseline_id  uuid NOT NULL REFERENCES sec_baselines(id) ON DELETE CASCADE,
    avg_score    numeric(5,1),
    devices      integer NOT NULL,
    compliant    integer NOT NULL,        -- devices with no failed high-severity check
    failed       integer NOT NULL,        -- failed checks in total
    PRIMARY KEY (day, baseline_id)
);

-- Documented exceptions: a check that does not apply to a device (or to every device) for a reason, until a date.
CREATE TABLE sec_exceptions (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    rule_id      text NOT NULL,
    asset_id     uuid REFERENCES assets(id) ON DELETE CASCADE,      -- NULL = every device
    baseline_id  uuid REFERENCES sec_baselines(id) ON DELETE CASCADE, -- NULL = every baseline
    reason       text NOT NULL,
    expires_at   timestamptz,
    created_by   uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_sec_exceptions_rule ON sec_exceptions (rule_id);

INSERT INTO sec_baselines (name, description, built_in, definition) VALUES
 ('Windows security baseline', 'Zentriq''s default checks for Windows workstations and servers, aligned with CIS Controls v8 and the Microsoft security baselines.',
  true, '{"rules":[{"id":"SC-18","enabled":false},{"id":"SC-30","enabled":false}],"categories":[]}');

INSERT INTO system_settings (key, value) VALUES ('security.auto_evaluate', 'true'::jsonb)
ON CONFLICT (key) DO NOTHING;

-- ============================================================================================
-- Integrations & automation
-- ============================================================================================
DROP TABLE IF EXISTS integration_configs;   -- unused baseline placeholder

-- Where notifications go. Secrets (URLs with tokens, passwords, API tokens, signing keys) are encrypted
-- (AES-256-GCM, installation key) in secret_protected and never returned by the API.
CREATE TABLE int_channels (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    kind             text NOT NULL CHECK (kind IN ('webhook','slack','teams','email','syslog','servicenow','jira')),
    name             text NOT NULL,
    enabled          boolean NOT NULL DEFAULT true,
    settings         jsonb NOT NULL DEFAULT '{}'::jsonb,
    secret_protected text,
    last_ok_at       timestamptz,
    last_error_at    timestamptz,
    last_error       text,
    created_by       uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_int_channels_name ON int_channels (lower(name));

-- When to notify: an event type, a minimum severity, optional device filters, the channels and a throttle.
CREATE TABLE int_rules (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name             text NOT NULL,
    enabled          boolean NOT NULL DEFAULT true,
    event_type       text NOT NULL,
    min_severity     text NOT NULL DEFAULT 'info' CHECK (min_severity IN ('info','low','medium','high','critical')),
    conditions       jsonb NOT NULL DEFAULT '{}'::jsonb,    -- {"categories":[],"criticality":[],"offlineHours":24}
    channel_ids      uuid[] NOT NULL DEFAULT '{}',
    throttle_minutes integer NOT NULL DEFAULT 0 CHECK (throttle_minutes BETWEEN 0 AND 10080),
    last_fired_at    timestamptz,
    created_by       uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);

-- Events found by the detectors (one row per distinct occurrence; dedupe_key prevents repeats).
CREATE TABLE int_events (
    id           bigserial PRIMARY KEY,
    event_type   text NOT NULL,
    severity     text NOT NULL CHECK (severity IN ('info','low','medium','high','critical')),
    dedupe_key   text NOT NULL UNIQUE,
    title        text NOT NULL,
    body         text NOT NULL DEFAULT '',
    asset_id     uuid REFERENCES assets(id) ON DELETE SET NULL,
    data         jsonb NOT NULL DEFAULT '{}'::jsonb,
    link         text,
    occurred_at  timestamptz NOT NULL DEFAULT now(),
    routed_at    timestamptz
);
CREATE INDEX ix_int_events_unrouted ON int_events (id) WHERE routed_at IS NULL;
CREATE INDEX ix_int_events_time ON int_events (occurred_at DESC);

CREATE TABLE int_deliveries (
    id              bigserial PRIMARY KEY,
    event_id        bigint NOT NULL REFERENCES int_events(id) ON DELETE CASCADE,
    rule_id         uuid REFERENCES int_rules(id) ON DELETE SET NULL,
    channel_id      uuid REFERENCES int_channels(id) ON DELETE CASCADE,
    status          text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','sent','failed','suppressed')),
    attempts        integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    response        text,               -- HTTP status, ticket number...
    error           text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    sent_at         timestamptz
);
CREATE INDEX ix_int_deliveries_due ON int_deliveries (next_attempt_at) WHERE status = 'queued';
CREATE INDEX ix_int_deliveries_time ON int_deliveries (created_at DESC);
CREATE INDEX ix_int_deliveries_channel ON int_deliveries (channel_id, created_at DESC);

-- How far each detector has looked.
CREATE TABLE int_detector_state (
    detector   text PRIMARY KEY,
    watermark  timestamptz NOT NULL
);

-- Read-only API tokens for scripts, BI tools and SIEM pulls. Only a SHA-256 hash is stored.
CREATE TABLE api_tokens (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name          text NOT NULL,
    prefix        text NOT NULL,                 -- first characters, to recognise the token
    token_hash    bytea NOT NULL UNIQUE,
    permissions   text[] NOT NULL DEFAULT '{}',
    created_by    uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at    timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz,
    last_used_at  timestamptz,
    last_used_ip  text,
    revoked_at    timestamptz
);

-- ============================================================================================
-- Permissions, report datasets, built-in reports
-- ============================================================================================
INSERT INTO permissions (code, description) VALUES
 ('security.view', 'View security configuration results'),
 ('security.manage', 'Edit security baselines and exceptions'),
 ('integrations.manage', 'Configure integrations, alert rules and API tokens')
ON CONFLICT (code) DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r CROSS JOIN (VALUES ('security.view'), ('security.manage'), ('integrations.manage')) p(code)
 WHERE r.id = 'admin'
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, 'security.view' FROM roles r WHERE r.id <> 'admin' AND EXISTS (SELECT 1 FROM role_permissions rp WHERE rp.role_id = r.id AND rp.permission_code = 'assets.view')
ON CONFLICT DO NOTHING;

INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('Failed security checks', 'security', true, true, 'Every failed configuration check per device, with severity and the value found.',
  '{"columns":["hostname","rule","title","category","severity","actual","failing_days","baseline"],"filters":[{"column":"status","op":"eq","value":"fail"}],"sort":[{"column":"severity_rank","dir":"asc"}]}'),
 ('Security compliance by check', 'security', true, true, 'Number of devices failing each check.',
  '{"columns":["rule","title"],"groupBy":["rule","title"],"aggregates":[{"fn":"count_distinct","column":"hostname"}],"filters":[{"column":"status","op":"eq","value":"fail"}],"sort":[{"column":"count","dir":"desc"}],"chart":"bar"}'),
 ('Security score per device', 'assets', true, true, 'Configuration compliance score of every Windows device.',
  '{"columns":["hostname","os","category","security_score","security_failed","last_contact"],"filters":[{"column":"security_score","op":"not_empty","value":""}],"sort":[{"column":"security_score","dir":"asc"}]}');
