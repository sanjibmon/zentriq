-- V0004: endpoint agents, credentialed (agentless) inventory, company profile, 30-device trial.
SET search_path = zentriq, public;

-- ---- Trial: one total device cap (all modules enabled) ------------------------------------
-- "devices" is a total across every workload category.
UPDATE system_settings
   SET value = '{"days":14,"limits":{"devices":30},"modules":["*"]}'::jsonb, updated_at = now()
 WHERE key = 'trial.policy';
-- Existing evaluation installations that still carry the previous default policy get the new one.
-- (Dates and the integrity MAC are unchanged; only the workload policy is replaced.) The trial row is
-- immutable at runtime (trigger); the migrator, as the schema owner, lifts the guard for this one statement.
ALTER TABLE trial DISABLE TRIGGER trg_trial_immutable;
UPDATE trial
   SET policy = '{"days":14,"limits":{"devices":30},"modules":["*"]}'::jsonb
 WHERE policy->'limits' = '{"endpoint":250,"server":25,"network_device":50,"other":50}'::jsonb;
ALTER TABLE trial ENABLE TRIGGER trg_trial_immutable;

-- ---- Company profile ---------------------------------------------------------------------
ALTER TABLE organizations
    ADD COLUMN legal_name     text,
    ADD COLUMN address_line1  text,
    ADD COLUMN address_line2  text,
    ADD COLUMN city           text,
    ADD COLUMN state_region   text,
    ADD COLUMN postal_code    text,
    ADD COLUMN country        text,
    ADD COLUMN tax_id         text,
    ADD COLUMN website        text,
    ADD COLUMN contact_name   text,
    ADD COLUMN contact_email  text,
    ADD COLUMN contact_phone  text,
    ADD COLUMN updated_at     timestamptz NOT NULL DEFAULT now();

-- ---- Permissions -------------------------------------------------------------------------
INSERT INTO permissions (code, description) VALUES
 ('agents.manage', 'Create agent enrollment codes and revoke agents')
ON CONFLICT (code) DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code) VALUES ('admin', 'agents.manage'), ('discovery', 'agents.manage')
ON CONFLICT DO NOTHING;

-- ---- Agents ------------------------------------------------------------------------------
ALTER TABLE agent_registrations
    ADD COLUMN hostname          text,
    ADD COLUMN os_name           text,
    ADD COLUMN last_ip           inet,
    ADD COLUMN cert_serial       text,
    ADD COLUMN cert_not_after    timestamptz,
    ADD COLUMN token_id          uuid,
    ADD COLUMN last_inventory_at timestamptz,
    ADD COLUMN revoked_at        timestamptz,
    ADD COLUMN revoked_by        uuid REFERENCES users(id);
CREATE UNIQUE INDEX ux_agent_registrations_thumbprint ON agent_registrations (cert_thumbprint) WHERE cert_thumbprint IS NOT NULL;

CREATE TABLE agent_enrollment_tokens (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL,
    token_hash  bytea NOT NULL UNIQUE,         -- SHA-256 of the secret; the secret itself is shown once
    server_url  text NOT NULL,                  -- URL agents use (e.g. public gateway name)
    site_id     uuid REFERENCES sites(id),
    max_uses    integer CHECK (max_uses IS NULL OR max_uses > 0),
    uses        integer NOT NULL DEFAULT 0,
    expires_at  timestamptz NOT NULL,
    revoked_at  timestamptz,
    created_by  uuid REFERENCES users(id),
    created_at  timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE agent_registrations ADD CONSTRAINT fk_agent_token FOREIGN KEY (token_id) REFERENCES agent_enrollment_tokens(id) ON DELETE SET NULL;

-- ---- Inventory ---------------------------------------------------------------------------
-- Complete normalized inventory document (network, patches, security, users, services...).
CREATE TABLE asset_inventory_documents (
    asset_id     uuid PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    source       text NOT NULL CHECK (source IN ('agent','winrm','ssh','snmp')),
    collected_at timestamptz NOT NULL,
    document     jsonb NOT NULL
);

ALTER TABLE software_inventory
    ADD COLUMN architecture text,
    ADD COLUMN source       text NOT NULL DEFAULT 'agent';

ALTER TABLE hardware_inventory
    ADD COLUMN serial_number text,
    ADD COLUMN smbios_uuid   text,
    ADD COLUMN cpu_threads   integer,
    ADD COLUMN chassis       text,
    ADD COLUMN source        text NOT NULL DEFAULT 'agent';

ALTER TABLE assets ADD COLUMN inventory_source text CHECK (inventory_source IN ('agent','winrm','ssh','snmp'));

-- ---- Credentials (agentless) -------------------------------------------------------------
-- secret_protected: AES-256-GCM (installation credential key); never returned by any API.
ALTER TABLE credentials ADD COLUMN options jsonb NOT NULL DEFAULT '{}'::jsonb;   -- port, https, SNMP v3 protocols, domain...
ALTER TABLE credentials ADD COLUMN description text;
ALTER TABLE credentials ADD COLUMN last_used_at timestamptz;

ALTER TABLE scan_results ADD COLUMN protocol text;
ALTER TABLE scan_results ADD COLUMN credential_id uuid REFERENCES credentials(id) ON DELETE SET NULL;

-- Active Directory discovery is now available.
INSERT INTO system_settings (key, value) VALUES ('discovery.ad_page_limit', '20000')
ON CONFLICT (key) DO NOTHING;

-- SSH host keys (trust on first use; a changed key is refused until an administrator clears it).
CREATE TABLE ssh_known_hosts (
    host          text PRIMARY KEY,
    key_type      text NOT NULL,
    fingerprint   text NOT NULL,             -- SHA-256, base64
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at  timestamptz NOT NULL DEFAULT now()
);
