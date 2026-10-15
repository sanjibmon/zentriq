-- V0010: Reports & Analytics (saved reports, schedules, run history) and Vulnerability Management
-- (CVE catalogue from NVD, CISA KEV and FIRST EPSS; software-to-CPE mapping; findings with SLA and exceptions).
SET search_path = zentriq, public;

-- ============================================================================================
-- Reports
-- ============================================================================================
-- The baseline placeholders were never used; replace them with one definition row (versioned by updated_at).
DROP TABLE IF EXISTS report_exports;
DROP TABLE IF EXISTS report_schedules;
DROP TABLE IF EXISTS report_versions;

ALTER TABLE report_definitions
    ADD COLUMN description  text,
    ADD COLUMN definition   jsonb NOT NULL DEFAULT '{}'::jsonb,   -- columns, filters, group by, sort (no SQL)
    ADD COLUMN system       boolean NOT NULL DEFAULT false,       -- built-in template (read-only)
    ADD COLUMN schedule     jsonb,                                -- {frequency: daily|weekly|monthly, hour, weekday, day}
    ADD COLUMN recipients   text[] NOT NULL DEFAULT '{}',
    ADD COLUMN format       text NOT NULL DEFAULT 'xlsx' CHECK (format IN ('xlsx','csv')),
    ADD COLUMN next_run_at  timestamptz,
    ADD COLUMN last_run_at  timestamptz,
    ADD COLUMN updated_by   uuid REFERENCES users(id) ON DELETE SET NULL;
CREATE INDEX ix_report_definitions_due ON report_definitions (next_run_at) WHERE schedule IS NOT NULL;

CREATE TABLE report_runs (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    report_id     uuid REFERENCES report_definitions(id) ON DELETE CASCADE,
    report_name   text NOT NULL,
    trigger       text NOT NULL CHECK (trigger IN ('manual','schedule')),
    status        text NOT NULL DEFAULT 'running' CHECK (status IN ('running','succeeded','failed')),
    row_count     integer,
    format        text CHECK (format IN ('xlsx','csv')),
    file_name     text,
    content       bytea,                 -- generated file, kept 30 days (housekeeping clears it)
    recipients    text[] NOT NULL DEFAULT '{}',
    error         text,
    requested_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    started_at    timestamptz NOT NULL DEFAULT now(),
    finished_at   timestamptz
);
CREATE INDEX ix_report_runs_report ON report_runs (report_id, started_at DESC);
CREATE INDEX ix_report_runs_time ON report_runs (started_at DESC);

-- Built-in templates (shared, read-only; "Save as" makes an editable copy).
INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('All assets', 'assets', true, true, 'Every active device with type, operating system, agent and last contact.',
  '{"columns":["hostname","ip","type","category","os","logged_on_user","agent","last_contact","offline_days"],"filters":[],"sort":[{"column":"hostname","dir":"asc"}]}'),
 ('Devices offline 30+ days', 'assets', true, true, 'Devices not heard from (agent, scan or inventory) for 30 days or more.',
  '{"columns":["hostname","ip","type","os","last_contact","offline_days","agent"],"filters":[{"column":"offline_days","op":"gte","value":"30"}],"sort":[{"column":"offline_days","dir":"desc"}]}'),
 ('Devices without an agent', 'assets', true, true, 'Active devices inventoried agentlessly or not at all.',
  '{"columns":["hostname","ip","type","os","inventory_status","last_contact"],"filters":[{"column":"agent","op":"eq","value":"no"}],"sort":[{"column":"hostname","dir":"asc"}]}'),
 ('Hardware inventory', 'hardware', true, true, 'Manufacturer, model, serial number, processor, memory and storage per device.',
  '{"columns":["hostname","manufacturer","model","serial","cpu","cores","ram_gb","disk_gb","free_pct","tpm","secure_boot"],"filters":[],"sort":[{"column":"hostname","dir":"asc"}]}'),
 ('Low disk space', 'hardware', true, true, 'Devices with less than 15% free space on any volume.',
  '{"columns":["hostname","manufacturer","model","disk_gb","free_pct"],"filters":[{"column":"free_pct","op":"lt","value":"15"}],"sort":[{"column":"free_pct","dir":"asc"}]}'),
 ('Operating systems', 'assets', true, true, 'Number of devices per operating system.',
  '{"columns":["os"],"groupBy":["os"],"filters":[],"sort":[{"column":"count","dir":"desc"}]}'),
 ('Software installations', 'software', true, true, 'Every installed application with version and device.',
  '{"columns":["software","version","publisher","hostname","os","installed_on"],"filters":[],"sort":[{"column":"software","dir":"asc"}]}'),
 ('Software titles by device count', 'software', true, true, 'How many devices have each application.',
  '{"columns":["software"],"groupBy":["software"],"filters":[],"sort":[{"column":"count","dir":"desc"}]}'),
 ('Agent health', 'agents', true, true, 'Every agent with version, health and last check-in.',
  '{"columns":["hostname","agent_version","health","last_checkin","last_inventory","enrolled"],"filters":[],"sort":[{"column":"hostname","dir":"asc"}]}'),
 ('Device activity (30 days)', 'activity', true, true, 'Service actions, uninstalls and agent updates requested in the last 30 days.',
  '{"columns":["requested_at","hostname","action","target","status","detail","requested_by"],"filters":[{"column":"requested_at","op":"last_days","value":"30"}],"sort":[{"column":"requested_at","dir":"desc"}]}'),
 ('Open vulnerabilities', 'vulnerabilities', true, true, 'Open vulnerability findings by priority, with SLA due date.',
  '{"columns":["priority","severity","cve","cvss","kev","epss","software","version","hostname","due_at","overdue"],"filters":[{"column":"status","op":"eq","value":"open"}],"sort":[{"column":"risk","dir":"desc"}]}'),
 ('Actively exploited vulnerabilities (CISA KEV)', 'vulnerabilities', true, true, 'Open findings for CVEs on the CISA Known Exploited Vulnerabilities list.',
  '{"columns":["cve","software","version","hostname","kev_due","due_at","overdue"],"filters":[{"column":"status","op":"eq","value":"open"},{"column":"kev","op":"eq","value":"yes"}],"sort":[{"column":"due_at","dir":"asc"}]}');

-- ============================================================================================
-- Vulnerability management
-- ============================================================================================
DROP TABLE IF EXISTS vulnerability_findings;   -- unused baseline placeholder

CREATE TABLE vuln_cves (
    cve_id           text PRIMARY KEY,
    published_at     timestamptz,
    modified_at      timestamptz,
    status           text,                       -- NVD vulnStatus (Analyzed, Modified, Rejected, ...)
    description      text,
    cvss_version     text,
    cvss_score       numeric(3,1),
    cvss_vector      text,
    severity         text CHECK (severity IN ('critical','high','medium','low','none')),
    cwes             text[] NOT NULL DEFAULT '{}',
    reference_urls   text[] NOT NULL DEFAULT '{}',
    kev              boolean NOT NULL DEFAULT false,
    kev_added        date,
    kev_due          date,
    kev_ransomware   text,
    kev_action       text,
    epss             numeric(6,5),
    epss_percentile  numeric(6,5),
    epss_date        date,
    updated_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_vuln_cves_kev ON vuln_cves (cve_id) WHERE kev;

-- Vulnerable CPE match criteria per CVE (flattened from NVD configurations; vulnerable = true only).
CREATE TABLE vuln_cpe_rules (
    id                 bigserial PRIMARY KEY,
    cve_id             text NOT NULL REFERENCES vuln_cves(cve_id) ON DELETE CASCADE,
    part               text NOT NULL,             -- a (application), o (OS), h (hardware)
    vendor             text NOT NULL,
    product            text NOT NULL,
    version            text,                      -- exact version or NULL/'*' for ranges
    version_start_incl text,
    version_start_excl text,
    version_end_incl   text,
    version_end_excl   text,
    criteria           text NOT NULL
);
CREATE INDEX ix_vuln_cpe_rules_product ON vuln_cpe_rules (vendor, product);
CREATE INDEX ix_vuln_cpe_rules_cve ON vuln_cpe_rules (cve_id);

-- Installed software title -> CPE vendor:product. auto = built-in/heuristic, manual = set by an administrator.
CREATE TABLE vuln_product_map (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    software_key  text NOT NULL UNIQUE,           -- normalised title (lower-case, versions and architecture removed)
    software_name text NOT NULL,                  -- a representative installed name
    publisher     text,
    vendor        text,
    product       text,
    status        text NOT NULL DEFAULT 'auto' CHECK (status IN ('auto','manual','ignored','unmapped')),
    nvd_synced_at timestamptz,                    -- last successful NVD query for this vendor:product
    updated_by    uuid REFERENCES users(id) ON DELETE SET NULL,
    updated_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_vuln_product_map_cpe ON vuln_product_map (vendor, product);

CREATE TABLE vuln_findings (
    id               bigserial PRIMARY KEY,
    asset_id         uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    cve_id           text NOT NULL REFERENCES vuln_cves(cve_id) ON DELETE CASCADE,
    software_name    text NOT NULL,
    software_version text NOT NULL DEFAULT '',
    vendor           text NOT NULL,
    product          text NOT NULL,
    fixed_version    text,                         -- lowest version that is not vulnerable, when the rule has one
    status           text NOT NULL DEFAULT 'open' CHECK (status IN ('open','fixed','accepted','false_positive')),
    priority         text NOT NULL DEFAULT 'P4' CHECK (priority IN ('P1','P2','P3','P4')),
    risk_score       integer NOT NULL DEFAULT 0,
    first_seen_at    timestamptz NOT NULL DEFAULT now(),
    last_seen_at     timestamptz NOT NULL DEFAULT now(),
    due_at           timestamptz,
    fixed_at         timestamptz,
    exception_reason text,
    exception_until  timestamptz,
    exception_by     uuid REFERENCES users(id) ON DELETE SET NULL,
    exception_at     timestamptz,
    UNIQUE (asset_id, cve_id, software_name, software_version)
);
CREATE INDEX ix_vuln_findings_status ON vuln_findings (status, priority);
CREATE INDEX ix_vuln_findings_cve ON vuln_findings (cve_id);
CREATE INDEX ix_vuln_findings_asset ON vuln_findings (asset_id, status);

CREATE TABLE vuln_feed_status (
    source        text PRIMARY KEY CHECK (source IN ('nvd','kev','epss','evaluate')),
    last_run_at   timestamptz,
    last_ok_at    timestamptz,
    items         integer,
    message       text
);
INSERT INTO vuln_feed_status (source) VALUES ('nvd'), ('kev'), ('epss'), ('evaluate');

-- Feed files uploaded for offline / air-gapped servers; processed by the worker, content removed afterwards.
CREATE TABLE vuln_imports (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source       text NOT NULL CHECK (source IN ('nvd','kev','epss')),
    file_name    text NOT NULL,
    size_bytes   bigint NOT NULL,
    content      bytea,
    status       text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','running','done','failed')),
    items        integer,
    message      text,
    created_by   uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    finished_at  timestamptz
);

INSERT INTO system_settings (key, value) VALUES
 ('vuln.auto_sync', 'true'::jsonb),
 ('vuln.sla_days', '{"critical":15,"high":30,"medium":90,"low":180}'::jsonb),
 ('vuln.min_cvss', '0'::jsonb)
ON CONFLICT (key) DO NOTHING;

INSERT INTO permissions (code, description) VALUES
 ('vuln.view', 'View vulnerability findings'),
 ('vuln.manage', 'Map software to products, sync vulnerability feeds and import feed files')
ON CONFLICT (code) DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r CROSS JOIN (VALUES ('vuln.view'), ('vuln.manage'), ('vuln.except'), ('reports.design'), ('reports.export')) p(code)
 WHERE r.id = 'admin'
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, 'vuln.view' FROM roles r WHERE r.id <> 'admin' AND EXISTS (SELECT 1 FROM role_permissions rp WHERE rp.role_id = r.id AND rp.permission_code = 'assets.view')
ON CONFLICT DO NOTHING;
