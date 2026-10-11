-- Zentriq baseline schema.
-- Applied by Zentriq.Infrastructure.Database.SqlMigrator while connected as the schema owner role
-- (zentriq_owner). The migrator wraps each file in a transaction and records it in
-- zentriq.schema_migrations together with a SHA-256 checksum. Never edit an applied file;
-- add a new V####__name.sql instead.

CREATE SCHEMA IF NOT EXISTS zentriq;
SET search_path = zentriq, public;

CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public;  -- gen_random_uuid(), digest()

-- ---------------------------------------------------------------------------
-- Installation state, initialization and migration history
-- ---------------------------------------------------------------------------
CREATE TABLE install_state (
    id                       smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    installation_id          uuid        NOT NULL DEFAULT gen_random_uuid(),
    created_at               timestamptz NOT NULL DEFAULT now(),
    product_version          text        NOT NULL,
    setup_token_hash         bytea,                -- SHA-256 of one-time setup token; NULL after initialization
    setup_token_expires_at   timestamptz,
    initialized_at           timestamptz,          -- set exactly once, in the same transaction that starts the trial
    initialized_by           uuid,
    last_seen_utc            timestamptz NOT NULL DEFAULT now(), -- monotonic high-water mark (clock-rollback detection)
    clock_rollback_detected_at timestamptz,
    upgraded_at              timestamptz
);

CREATE TABLE installation_history (
    id            bigserial PRIMARY KEY,
    occurred_at   timestamptz NOT NULL DEFAULT now(),
    action        text NOT NULL CHECK (action IN ('install','repair','upgrade','migrate','initialize','backup','restore','uninstall')),
    from_version  text,
    to_version    text,
    outcome       text NOT NULL CHECK (outcome IN ('started','succeeded','failed')),
    detail        jsonb NOT NULL DEFAULT '{}'::jsonb
);

-- ---------------------------------------------------------------------------
-- Organizations, sites, departments
-- ---------------------------------------------------------------------------
CREATE TABLE organizations (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE sites (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL REFERENCES organizations(id),
    code            text NOT NULL,
    name            text NOT NULL,
    timezone        text NOT NULL DEFAULT 'UTC',
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, code)
);

CREATE TABLE departments (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL REFERENCES organizations(id),
    code            text NOT NULL,
    name            text NOT NULL,
    UNIQUE (organization_id, code)
);

-- ---------------------------------------------------------------------------
-- Users, roles and permissions
-- ---------------------------------------------------------------------------
CREATE TABLE users (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email               text NOT NULL,
    display_name        text NOT NULL,
    password_hash       text NOT NULL,
    status              text NOT NULL DEFAULT 'active' CHECK (status IN ('active','disabled')),
    failed_attempts     integer NOT NULL DEFAULT 0,
    locked_until        timestamptz,
    mfa_enabled         boolean NOT NULL DEFAULT false,
    mfa_secret_protected text,          -- ASP.NET Data Protection payload, never returned by APIs
    security_stamp      uuid NOT NULL DEFAULT gen_random_uuid(),
    password_changed_at timestamptz NOT NULL DEFAULT now(),
    last_login_at       timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_users_email ON users (lower(email));

CREATE TABLE permissions (
    code        text PRIMARY KEY,
    description text NOT NULL
);

CREATE TABLE roles (
    id          text PRIMARY KEY,
    name        text NOT NULL UNIQUE,
    description text NOT NULL DEFAULT '',
    built_in    boolean NOT NULL DEFAULT false
);

CREATE TABLE role_permissions (
    role_id         text NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission_code text NOT NULL REFERENCES permissions(code),
    PRIMARY KEY (role_id, permission_code)
);

CREATE TABLE user_roles (
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id text NOT NULL REFERENCES roles(id),
    PRIMARY KEY (user_id, role_id)
);

CREATE TABLE user_sites (
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    site_id uuid NOT NULL REFERENCES sites(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, site_id)
);

-- ---------------------------------------------------------------------------
-- Assets and inventory
-- ---------------------------------------------------------------------------
CREATE TABLE assets (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    hostname            text,
    fqdn                text,
    primary_ip          inet,
    mac_address         macaddr,
    serial_number       text,
    category            text NOT NULL DEFAULT 'endpoint'
                        CHECK (category IN ('endpoint','server','network_device','printer','iot','virtual_host','other')),
    os_family           text CHECK (os_family IN ('windows','linux','macos','network','other')),
    os_name             text,
    status              text NOT NULL DEFAULT 'active'
                        CHECK (status IN ('active','retired','duplicate','excluded')),
    merged_into         uuid REFERENCES assets(id),
    site_id             uuid REFERENCES sites(id),
    department_id       uuid REFERENCES departments(id),
    criticality         text NOT NULL DEFAULT 'Medium' CHECK (criticality IN ('Critical','High','Medium','Low')),
    owner_name          text,
    source              text NOT NULL DEFAULT 'manual',
    inventory_status    text NOT NULL DEFAULT 'not_collected'
                        CHECK (inventory_status IN ('not_collected','success','unavailable','failed')),
    first_seen_at       timestamptz NOT NULL DEFAULT now(),
    last_seen_at        timestamptz NOT NULL DEFAULT now(),
    last_inventory_at   timestamptz,
    retired_at          timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now(),
    row_version         integer NOT NULL DEFAULT 1,
    CHECK (status <> 'duplicate' OR merged_into IS NOT NULL)
);
CREATE INDEX ix_assets_hostname ON assets (lower(hostname));
CREATE INDEX ix_assets_ip ON assets (primary_ip);
CREATE INDEX ix_assets_status_category ON assets (status, category);

CREATE TABLE asset_identifiers (
    id         bigserial PRIMARY KEY,
    asset_id   uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    kind       text NOT NULL CHECK (kind IN ('serial','mac','smbios_uuid','ad_object_guid','cloud_instance_id','agent_id','hostname_fqdn')),
    value      text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (kind, value)
);

CREATE TABLE hardware_inventory (
    asset_id     uuid PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    vendor       text,
    model        text,
    cpu          text,
    cpu_cores    integer,
    ram_gb       numeric(8,2),
    firmware     jsonb NOT NULL DEFAULT '{}'::jsonb,
    disks        jsonb NOT NULL DEFAULT '[]'::jsonb,
    collected_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE software_inventory (
    id           bigserial PRIMARY KEY,
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    name         text NOT NULL,
    publisher    text,
    version      text,
    installed_on date,
    collected_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_software_name ON software_inventory (lower(name));
CREATE INDEX ix_software_asset ON software_inventory (asset_id);

-- ---------------------------------------------------------------------------
-- Credentials (secrets protected server-side; never returned by the API)
-- ---------------------------------------------------------------------------
CREATE TABLE credentials (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name             text NOT NULL UNIQUE,
    kind             text NOT NULL CHECK (kind IN ('windows_domain','windows_local','ssh_password','ssh_key','snmp_v2c','snmp_v3','ldap_bind')),
    username         text,
    secret_protected text NOT NULL,
    allowed_scope    text,
    created_by       uuid REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    rotated_at       timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- Discovery
-- ---------------------------------------------------------------------------
CREATE TABLE discovery_profiles (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL,
    description text NOT NULL DEFAULT '',
    scan_type   text NOT NULL CHECK (scan_type IN ('ip','single','list','ad')),
    config      jsonb NOT NULL,
    schedule    jsonb,                        -- NULL = on demand only
    enabled     boolean NOT NULL DEFAULT true,
    owner_id    uuid REFERENCES users(id),
    shared      boolean NOT NULL DEFAULT false,
    next_run_at timestamptz,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE discovery_scans (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    number       bigserial UNIQUE,
    name         text NOT NULL,
    scan_type    text NOT NULL CHECK (scan_type IN ('ip','single','list','ad','ad-reinventory')),
    profile_id   uuid REFERENCES discovery_profiles(id) ON DELETE SET NULL,
    config       jsonb NOT NULL,
    status       text NOT NULL DEFAULT 'queued'
                 CHECK (status IN ('queued','running','cancelling','completed','completed_errors','failed','cancelled')),
    total_targets integer NOT NULL DEFAULT 0,
    processed    integer NOT NULL DEFAULT 0,
    assets_created integer NOT NULL DEFAULT 0,
    assets_updated integer NOT NULL DEFAULT 0,
    job_id       uuid,
    created_by   uuid REFERENCES users(id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    started_at   timestamptz,
    finished_at  timestamptz,
    error        text
);
CREATE INDEX ix_scans_status ON discovery_scans (status, created_at DESC);

CREATE TABLE scan_targets (
    id        bigserial PRIMARY KEY,
    scan_id   uuid NOT NULL REFERENCES discovery_scans(id) ON DELETE CASCADE,
    target    text NOT NULL,              -- IP address or hostname as resolved for probing
    excluded  boolean NOT NULL DEFAULT false,
    UNIQUE (scan_id, target)
);

CREATE TABLE scan_results (
    id          bigserial PRIMARY KEY,
    scan_id     uuid NOT NULL REFERENCES discovery_scans(id) ON DELETE CASCADE,
    target      text NOT NULL,
    ip_address  inet,
    hostname    text,
    status      text NOT NULL CHECK (status IN (
                  'unreachable','reachable_unauthenticated','auth_failed','inventory_unavailable',
                  'inventory_failed','success','excluded','skipped','cancelled')),
    open_ports  integer[] NOT NULL DEFAULT '{}',
    detail      text,
    asset_id    uuid REFERENCES assets(id) ON DELETE SET NULL,
    asset_created boolean NOT NULL DEFAULT false,
    duration_ms integer,
    recorded_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (scan_id, target)
);

-- ---------------------------------------------------------------------------
-- Directory connections and objects
-- ---------------------------------------------------------------------------
CREATE TABLE directory_connections (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name          text NOT NULL,
    domain_name   text NOT NULL,
    servers       text[] NOT NULL DEFAULT '{}',
    transport     text NOT NULL DEFAULT 'ldaps' CHECK (transport IN ('ldaps','starttls')),
    credential_id uuid REFERENCES credentials(id),
    created_at    timestamptz NOT NULL DEFAULT now(),
    last_validated_at timestamptz
);

CREATE TABLE directory_objects (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    connection_id     uuid NOT NULL REFERENCES directory_connections(id) ON DELETE CASCADE,
    object_guid       uuid NOT NULL,
    object_class      text NOT NULL,
    distinguished_name text NOT NULL,
    sam_account_name  text,
    dns_host_name     text,
    operating_system  text,
    enabled           boolean,
    last_logon_timestamp timestamptz,
    synced_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (connection_id, object_guid)
);

-- ---------------------------------------------------------------------------
-- Durable jobs
-- ---------------------------------------------------------------------------
CREATE TABLE jobs (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    job_type         text NOT NULL,
    status           text NOT NULL DEFAULT 'queued'
                     CHECK (status IN ('queued','running','succeeded','failed','cancelled','dead')),
    payload          jsonb NOT NULL DEFAULT '{}'::jsonb,
    idempotency_key  text UNIQUE,
    priority         integer NOT NULL DEFAULT 100,
    attempts         integer NOT NULL DEFAULT 0,
    max_attempts     integer NOT NULL DEFAULT 3 CHECK (max_attempts BETWEEN 1 AND 20),
    timeout_seconds  integer NOT NULL DEFAULT 3600 CHECK (timeout_seconds > 0),
    run_after        timestamptz NOT NULL DEFAULT now(),
    lease_owner      text,
    lease_expires_at timestamptz,
    cancel_requested boolean NOT NULL DEFAULT false,
    progress_pct     smallint NOT NULL DEFAULT 0 CHECK (progress_pct BETWEEN 0 AND 100),
    progress_text    text,
    result           jsonb,
    last_error       text,
    created_by       uuid REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    started_at       timestamptz,
    finished_at      timestamptz
);
CREATE INDEX ix_jobs_dequeue ON jobs (priority, run_after) WHERE status = 'queued';
CREATE INDEX ix_jobs_lease ON jobs (lease_expires_at) WHERE status = 'running';

CREATE TABLE job_targets (
    id         bigserial PRIMARY KEY,
    job_id     uuid NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    target     text NOT NULL,
    status     text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','running','succeeded','failed','skipped','cancelled')),
    detail     text,
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (job_id, target)
);

CREATE TABLE job_events (
    id          bigserial PRIMARY KEY,
    job_id      uuid NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    level       text NOT NULL DEFAULT 'info' CHECK (level IN ('debug','info','warning','error')),
    message     text NOT NULL
);
CREATE INDEX ix_job_events_job ON job_events (job_id, id);

CREATE TABLE worker_heartbeats (
    worker_id   text PRIMARY KEY,
    host_name   text NOT NULL,
    version     text NOT NULL,
    started_at  timestamptz NOT NULL,
    last_beat_at timestamptz NOT NULL
);

-- ---------------------------------------------------------------------------
-- Agents
-- ---------------------------------------------------------------------------
CREATE TABLE agent_registrations (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    asset_id       uuid REFERENCES assets(id) ON DELETE SET NULL,
    agent_version  text NOT NULL,
    cert_thumbprint text,
    status         text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','active','revoked')),
    enrolled_at    timestamptz NOT NULL DEFAULT now(),
    last_checkin_at timestamptz
);

-- ---------------------------------------------------------------------------
-- Dashboards
-- ---------------------------------------------------------------------------
CREATE TABLE dashboards (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name         text NOT NULL,
    owner_id     uuid REFERENCES users(id) ON DELETE SET NULL,
    shared       boolean NOT NULL DEFAULT false,
    role_default text REFERENCES roles(id),
    filters      jsonb NOT NULL DEFAULT '{}'::jsonb,
    refresh_seconds integer NOT NULL DEFAULT 0,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE dashboard_widgets (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    dashboard_id uuid NOT NULL REFERENCES dashboards(id) ON DELETE CASCADE,
    widget_type  text NOT NULL,
    title        text,
    position     integer NOT NULL,
    width        smallint NOT NULL DEFAULT 4 CHECK (width BETWEEN 1 AND 12),
    height       smallint NOT NULL DEFAULT 1 CHECK (height BETWEEN 1 AND 6),
    settings     jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE dashboard_permissions (
    dashboard_id uuid NOT NULL REFERENCES dashboards(id) ON DELETE CASCADE,
    role_id      text NOT NULL REFERENCES roles(id),
    can_edit     boolean NOT NULL DEFAULT false,
    PRIMARY KEY (dashboard_id, role_id)
);

CREATE TABLE user_preferences (
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    key     text NOT NULL,
    value   jsonb NOT NULL,
    PRIMARY KEY (user_id, key)
);

-- ---------------------------------------------------------------------------
-- Reports
-- ---------------------------------------------------------------------------
CREATE TABLE report_definitions (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name       text NOT NULL,
    dataset    text NOT NULL,              -- must be an approved dataset key, validated by the API
    owner_id   uuid REFERENCES users(id) ON DELETE SET NULL,
    shared     boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE report_versions (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    report_id     uuid NOT NULL REFERENCES report_definitions(id) ON DELETE CASCADE,
    version       integer NOT NULL,
    definition    jsonb NOT NULL,         -- columns, filters, grouping, sorting, aggregates (no SQL)
    created_by    uuid REFERENCES users(id),
    created_at    timestamptz NOT NULL DEFAULT now(),
    UNIQUE (report_id, version)
);

CREATE TABLE report_schedules (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    report_id   uuid NOT NULL REFERENCES report_definitions(id) ON DELETE CASCADE,
    enabled     boolean NOT NULL DEFAULT true,
    schedule    jsonb NOT NULL,
    format      text NOT NULL CHECK (format IN ('XLSX','PDF','XLSX+PDF')),
    recipients  text[] NOT NULL DEFAULT '{}',
    run_as_user uuid REFERENCES users(id),
    next_run_at timestamptz,
    last_run_at timestamptz
);

CREATE TABLE report_exports (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    report_id    uuid REFERENCES report_definitions(id) ON DELETE SET NULL,
    report_version integer,
    job_id       uuid REFERENCES jobs(id) ON DELETE SET NULL,
    format       text NOT NULL CHECK (format IN ('XLSX','PDF')),
    status       text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','running','ready','failed','expired')),
    requested_by uuid REFERENCES users(id),
    requested_at timestamptz NOT NULL DEFAULT now(),
    file_name    text,
    storage_path text,
    size_bytes   bigint,
    sha256       text,
    expires_at   timestamptz
);

-- ---------------------------------------------------------------------------
-- Product licensing: trial, signed licenses, entitlements, activations, usage
-- ---------------------------------------------------------------------------
CREATE TABLE trial (
    id            smallint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    installation_id uuid NOT NULL,
    started_at    timestamptz NOT NULL,
    expires_at    timestamptz NOT NULL,
    policy        jsonb NOT NULL,
    integrity_mac text NOT NULL,          -- HMAC-SHA256 over installation_id|started_at|expires_at with the install secret
    CHECK (expires_at > started_at)
);

CREATE TABLE license_records (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    license_id      text NOT NULL UNIQUE,
    schema_version  integer NOT NULL,
    key_id          text NOT NULL,
    customer_name   text NOT NULL,
    installation_id uuid NOT NULL,
    issued_at       timestamptz NOT NULL,
    not_before      timestamptz NOT NULL,
    expires_at      timestamptz NOT NULL,
    grace_days      integer NOT NULL DEFAULT 0,
    modules         text[] NOT NULL,
    raw_document    text NOT NULL,          -- the exact signed file, re-verified on every load
    status          text NOT NULL DEFAULT 'active' CHECK (status IN ('active','superseded','revoked')),
    imported_by     uuid REFERENCES users(id),
    imported_at     timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_license_one_active ON license_records ((status)) WHERE status = 'active';

CREATE TABLE license_entitlements (
    license_record_id uuid NOT NULL REFERENCES license_records(id) ON DELETE CASCADE,
    workload          text NOT NULL CHECK (workload IN ('endpoint','server','network_device','other')),
    quantity          integer NOT NULL CHECK (quantity >= 0),
    PRIMARY KEY (license_record_id, workload)
);

CREATE TABLE license_activations (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id    uuid NOT NULL UNIQUE,
    requested_by  uuid REFERENCES users(id),
    requested_at  timestamptz NOT NULL DEFAULT now(),
    request_document text NOT NULL,
    fulfilled_by_license text
);

CREATE TABLE workload_usage_snapshots (
    id          bigserial PRIMARY KEY,
    taken_at    timestamptz NOT NULL DEFAULT now(),
    workload    text NOT NULL,
    billable    integer NOT NULL,
    entitled    integer,
    source      text NOT NULL,             -- 'scheduled','enrollment','manual','import'
    details     jsonb NOT NULL DEFAULT '{}'::jsonb
);
CREATE INDEX ix_usage_taken ON workload_usage_snapshots (taken_at DESC);

-- ---------------------------------------------------------------------------
-- Patch and vulnerability findings
-- ---------------------------------------------------------------------------
CREATE TABLE patch_findings (
    id          bigserial PRIMARY KEY,
    asset_id    uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    patch_id    text NOT NULL,
    title       text NOT NULL,
    severity    text,
    state       text NOT NULL CHECK (state IN ('missing','installed','failed','superseded')),
    detected_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (asset_id, patch_id)
);

CREATE TABLE vulnerability_findings (
    id          bigserial PRIMARY KEY,
    asset_id    uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    cve_id      text NOT NULL,
    cvss        numeric(3,1),
    product     text,
    state       text NOT NULL DEFAULT 'open' CHECK (state IN ('open','remediated','accepted','false_positive')),
    detected_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (asset_id, cve_id, product)
);

-- ---------------------------------------------------------------------------
-- Integrations, notifications, audit, settings
-- ---------------------------------------------------------------------------
CREATE TABLE integration_configs (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    kind        text NOT NULL,
    name        text NOT NULL,
    enabled     boolean NOT NULL DEFAULT false,
    settings    jsonb NOT NULL DEFAULT '{}'::jsonb,   -- non-secret settings only
    credential_id uuid REFERENCES credentials(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE notifications (
    id         bigserial PRIMARY KEY,
    user_id    uuid REFERENCES users(id) ON DELETE CASCADE,   -- NULL = broadcast to administrators
    kind       text NOT NULL DEFAULT 'info' CHECK (kind IN ('info','ok','warn','crit')),
    title      text NOT NULL,
    body       text NOT NULL DEFAULT '',
    link       text,
    read_at    timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE audit_events (
    id          bigserial PRIMARY KEY,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    actor_id    uuid,
    actor_name  text NOT NULL,
    source_ip   text,
    action      text NOT NULL,
    target      text,
    outcome     text NOT NULL CHECK (outcome IN ('Success','Failure','Denied')),
    detail      jsonb NOT NULL DEFAULT '{}'::jsonb,
    prev_hash   bytea,
    hash        bytea NOT NULL
);
CREATE INDEX ix_audit_time ON audit_events (occurred_at DESC);

CREATE TABLE system_settings (
    key        text PRIMARY KEY,
    value      jsonb NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid
);

-- ---------------------------------------------------------------------------
-- Integrity guards that the application role cannot bypass
-- ---------------------------------------------------------------------------

-- Audit log is append-only and hash-chained. The hash is computed in the database so the
-- chain does not depend on application code.
CREATE OR REPLACE FUNCTION audit_chain() RETURNS trigger LANGUAGE plpgsql
SECURITY DEFINER SET search_path = zentriq, public AS $$
DECLARE prev bytea;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('zentriq.audit_chain'));
    SELECT hash INTO prev FROM zentriq.audit_events ORDER BY id DESC LIMIT 1;
    NEW.prev_hash := prev;
    NEW.hash := public.digest(
        coalesce(encode(prev,'hex'),'') || '|' || extract(epoch FROM NEW.occurred_at)::text || '|' || coalesce(NEW.actor_id::text,'') || '|' ||
        NEW.actor_name || '|' || NEW.action || '|' || coalesce(NEW.target,'') || '|' || NEW.outcome || '|' || NEW.detail::text,
        'sha256');
    RETURN NEW;
END $$;
CREATE TRIGGER trg_audit_chain BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION audit_chain();

CREATE OR REPLACE FUNCTION forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'zentriq: % on % is not permitted', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END $$;
CREATE TRIGGER trg_audit_immutable BEFORE UPDATE OR DELETE ON audit_events FOR EACH ROW EXECUTE FUNCTION forbid_change();

-- The trial row may be inserted once and never changed or deleted: rerunning setup, restarting
-- services or Windows, or restoring the application tier cannot reset it.
CREATE TRIGGER trg_trial_immutable BEFORE UPDATE OR DELETE ON trial FOR EACH ROW EXECUTE FUNCTION forbid_change();

-- install_state.initialized_at can be set once only; installation_id never changes.
CREATE OR REPLACE FUNCTION guard_install_state() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'zentriq: install_state cannot be deleted' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NEW.installation_id <> OLD.installation_id THEN
        RAISE EXCEPTION 'zentriq: installation_id is immutable' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF OLD.initialized_at IS NOT NULL AND NEW.initialized_at IS DISTINCT FROM OLD.initialized_at THEN
        RAISE EXCEPTION 'zentriq: initialized_at is immutable once set' USING ERRCODE = 'insufficient_privilege';
    END IF;
    -- last_seen_utc is a high-water mark: it may only move forward.
    IF NEW.last_seen_utc < OLD.last_seen_utc THEN
        NEW.last_seen_utc := OLD.last_seen_utc;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_install_state_guard BEFORE UPDATE OR DELETE ON install_state FOR EACH ROW EXECUTE FUNCTION guard_install_state();
