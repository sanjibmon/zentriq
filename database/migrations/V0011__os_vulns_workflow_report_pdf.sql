-- V0011: Windows operating-system vulnerabilities (Microsoft Security Response Center CVRF), remediation workflow
-- (in progress, assignee, ticket, notes), asset criticality in the risk score, and Reports: PDF output and
-- aggregates (no schema change needed for aggregates: they live in the report definition JSON).
SET search_path = zentriq, public;

-- ---- CVE catalogue: Microsoft data ----------------------------------------------------------------------
ALTER TABLE vuln_cves
    ADD COLUMN title          text,                     -- e.g. "Windows Kernel Elevation of Privilege Vulnerability"
    ADD COLUMN ms_severity    text,                     -- Microsoft severity: Critical, Important, Moderate, Low
    ADD COLUMN ms_exploited   boolean NOT NULL DEFAULT false,   -- Microsoft: "Exploited: Yes"
    ADD COLUMN ms_disclosed   boolean NOT NULL DEFAULT false;   -- Microsoft: "Publicly disclosed: Yes"

-- Fixed builds per CVE and Windows product, from the monthly Security Update documents.
CREATE TABLE vuln_os_rules (
    id            bigserial PRIMARY KEY,
    cve_id        text NOT NULL REFERENCES vuln_cves(cve_id) ON DELETE CASCADE,
    month         text NOT NULL,                         -- MSRC document id, e.g. 2026-Oct
    product       text NOT NULL,                         -- MSRC product name, e.g. "Windows Server 2019"
    product_key   text NOT NULL,                         -- lower-case, single-spaced product name (matching key)
    fixed_build   text,                                  -- e.g. 10.0.17763.6893 (devices below it are vulnerable)
    kb            text,                                  -- e.g. 5044277
    kb_url        text,
    supersedes    text,
    restart       boolean
);
CREATE INDEX ix_vuln_os_rules_product ON vuln_os_rules (product_key);
CREATE INDEX ix_vuln_os_rules_cve ON vuln_os_rules (cve_id, month);

-- ---- findings: OS source, workflow ------------------------------------------------------------------------
ALTER TABLE vuln_findings
    ADD COLUMN source            text NOT NULL DEFAULT 'software' CHECK (source IN ('software','os')),
    ADD COLUMN kb                text,                   -- OS findings: the update that fixes it
    ADD COLUMN installed_version text,                   -- OS findings: the device's full build (software_version holds the stable base build)
    ADD COLUMN assignee_id       uuid REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN ticket            text,                   -- external ticket / change reference
    ADD COLUMN status_changed_at timestamptz;

ALTER TABLE vuln_findings DROP CONSTRAINT IF EXISTS vuln_findings_status_check;
ALTER TABLE vuln_findings ADD CONSTRAINT vuln_findings_status_check CHECK (status IN ('open','in_progress','fixed','accepted','false_positive'));
CREATE INDEX ix_vuln_findings_assignee ON vuln_findings (assignee_id) WHERE assignee_id IS NOT NULL;

-- History and notes per finding (status changes, assignment, comments).
CREATE TABLE vuln_finding_events (
    id          bigserial PRIMARY KEY,
    finding_id  bigint NOT NULL REFERENCES vuln_findings(id) ON DELETE CASCADE,
    at          timestamptz NOT NULL DEFAULT now(),
    user_id     uuid REFERENCES users(id) ON DELETE SET NULL,
    kind        text NOT NULL CHECK (kind IN ('note','status','assign','ticket','system')),
    text        text NOT NULL
);
CREATE INDEX ix_vuln_finding_events_finding ON vuln_finding_events (finding_id, at DESC);

-- ---- feeds ----------------------------------------------------------------------------------------------
ALTER TABLE vuln_feed_status DROP CONSTRAINT IF EXISTS vuln_feed_status_source_check;
ALTER TABLE vuln_feed_status ADD CONSTRAINT vuln_feed_status_source_check CHECK (source IN ('nvd','kev','epss','evaluate','msrc'));
INSERT INTO vuln_feed_status (source) VALUES ('msrc') ON CONFLICT (source) DO NOTHING;

ALTER TABLE vuln_imports DROP CONSTRAINT IF EXISTS vuln_imports_source_check;
ALTER TABLE vuln_imports ADD CONSTRAINT vuln_imports_source_check CHECK (source IN ('nvd','kev','epss','msrc'));

INSERT INTO system_settings (key, value) VALUES
 ('vuln.msrc_months', '12'::jsonb),            -- how many monthly Microsoft security updates to load
 ('vuln.criticality_weight', 'true'::jsonb)    -- weight the risk score by the device's business criticality
ON CONFLICT (key) DO NOTHING;

-- Sort key for version strings ("10.0.2" > "9.12"): the numeric parts as an array. Used by reports.
CREATE OR REPLACE FUNCTION version_key(v text) RETURNS numeric[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT array_agg(x::numeric ORDER BY ord)
      FROM unnest(regexp_split_to_array(COALESCE(v, ''), '[^0-9]+')) WITH ORDINALITY AS u(x, ord)
     WHERE x <> '' AND length(x) <= 30
$$;

-- ---- reports: PDF ----------------------------------------------------------------------------------------
ALTER TABLE report_definitions DROP CONSTRAINT IF EXISTS report_definitions_format_check;
ALTER TABLE report_definitions ADD CONSTRAINT report_definitions_format_check CHECK (format IN ('xlsx','csv','pdf'));
ALTER TABLE report_runs DROP CONSTRAINT IF EXISTS report_runs_format_check;
ALTER TABLE report_runs ADD CONSTRAINT report_runs_format_check CHECK (format IN ('xlsx','csv','pdf'));

-- More built-in reports (read-only templates; "Save as" makes an editable copy).
INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('Vulnerability summary by severity', 'vulnerabilities', true, true, 'Open findings per severity, with affected devices and average CVSS.',
  '{"columns":["severity"],"groupBy":["severity"],"aggregates":[{"fn":"count_distinct","column":"hostname"},{"fn":"avg","column":"cvss"},{"fn":"max","column":"risk"}],"filters":[{"column":"status","op":"in","value":"open,in_progress"}],"sort":[{"column":"count","dir":"desc"}],"chart":"donut"}'),
 ('Windows updates missing', 'vulnerabilities', true, true, 'Open operating-system vulnerabilities per device with the update (KB) that fixes them.',
  '{"columns":["hostname","os","installed_version","kb","fixed_version","cve","severity","priority","due_at","overdue"],"filters":[{"column":"source","op":"eq","value":"os"},{"column":"status","op":"in","value":"open,in_progress"}],"sort":[{"column":"hostname","dir":"asc"}]}'),
 ('Overdue vulnerabilities', 'vulnerabilities', true, true, 'Open findings past their SLA due date, oldest first.',
  '{"columns":["priority","cve","software","version","fixed_version","hostname","assignee","first_seen","due_at","age_days"],"filters":[{"column":"overdue","op":"eq","value":"yes"}],"sort":[{"column":"due_at","dir":"asc"}]}'),
 ('Vulnerability aging', 'vulnerabilities', true, true, 'Open findings by age bucket and severity.',
  '{"columns":["age_bucket","severity"],"groupBy":["age_bucket","severity"],"filters":[{"column":"status","op":"in","value":"open,in_progress"}],"sort":[{"column":"age_bucket","dir":"asc"}],"chart":"column"}'),
 ('Most vulnerable devices', 'vulnerabilities', true, true, 'Devices ranked by the total risk of their open findings.',
  '{"columns":["hostname"],"groupBy":["hostname"],"aggregates":[{"fn":"sum","column":"risk"},{"fn":"max","column":"cvss"},{"fn":"count_distinct","column":"cve"}],"filters":[{"column":"status","op":"in","value":"open,in_progress"}],"sort":[{"column":"agg_sum_risk","dir":"desc"}],"chart":"bar"}'),
 ('Hardware summary by model', 'hardware', true, true, 'Devices per manufacturer and model with average memory and disk.',
  '{"columns":["manufacturer","model"],"groupBy":["manufacturer","model"],"aggregates":[{"fn":"avg","column":"ram_gb"},{"fn":"avg","column":"disk_gb"},{"fn":"min","column":"free_pct"}],"filters":[],"sort":[{"column":"count","dir":"desc"}]}'),
 ('New devices per month', 'assets', true, true, 'Devices discovered per month (first seen).',
  '{"columns":["first_seen"],"groupBy":["first_seen"],"dateBucket":"month","filters":[{"column":"first_seen","op":"last_days","value":"365"}],"sort":[{"column":"first_seen","dir":"asc"}],"chart":"column"}'),
 ('Software catalogue', 'software_titles', true, true, 'Every application title with publisher, versions in use and number of devices.',
  '{"columns":["software","publisher","devices","versions","latest_version","open_vulns"],"filters":[],"sort":[{"column":"devices","dir":"desc"}]}'),
 ('Audit trail (30 days)', 'audit', true, true, 'Administrative actions in the console in the last 30 days.',
  '{"columns":["at","user","action","target","ip","result"],"filters":[{"column":"at","op":"last_days","value":"30"}],"sort":[{"column":"at","dir":"desc"}]}'),
 ('Discovery results (30 days)', 'discovery', true, true, 'Addresses checked by discovery scans in the last 30 days and what was found.',
  '{"columns":["scan","started","target","result","hostname","detail"],"filters":[{"column":"started","op":"last_days","value":"30"}],"sort":[{"column":"started","dir":"desc"}]}');

-- The built-in "Open vulnerabilities" report: include findings being worked on.
UPDATE report_definitions SET definition = jsonb_set(definition, '{filters}', '[{"column":"status","op":"in","value":"open,in_progress"}]'::jsonb)
 WHERE system AND name = 'Open vulnerabilities';
UPDATE report_definitions SET definition = jsonb_set(definition, '{filters,0}', '{"column":"status","op":"in","value":"open,in_progress"}'::jsonb)
 WHERE system AND name = 'Actively exploited vulnerabilities (CISA KEV)';
