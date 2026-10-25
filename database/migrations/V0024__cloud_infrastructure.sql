-- V0024: Cloud & Infrastructure. Read-only connections to Microsoft Azure, Amazon Web Services, VMware vCenter and
-- Microsoft Hyper-V hosts; the resources they report (virtual machines, hosts, datastores, disks, public IPs, networks,
-- security groups, load balancers, storage, databases, Kubernetes, functions), changes between syncs, and findings
-- (cost hygiene and security basics). Secrets are encrypted with the credential key, never stored in clear.
SET search_path = zentriq, public;

CREATE TABLE cloud_accounts (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name             text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    provider         text NOT NULL CHECK (provider IN ('azure','aws','vmware','hyperv')),
    settings         jsonb NOT NULL DEFAULT '{}',          -- non-secret: tenant/client id, access key id, regions, host names, options
    secret_protected text,                                 -- client secret / secret access key / vCenter password (AES-GCM, credential key)
    credential_id    uuid REFERENCES credentials(id) ON DELETE SET NULL,   -- Hyper-V: a saved Windows credential
    enabled          boolean NOT NULL DEFAULT true,
    sync_hours       integer NOT NULL DEFAULT 6 CHECK (sync_hours BETWEEN 1 AND 168),
    last_sync_at     timestamptz,
    last_status      text CHECK (last_status IN ('running','succeeded','partial','failed')),
    last_error       text,
    last_error_at    timestamptz,
    last_counts      jsonb NOT NULL DEFAULT '{}',
    last_warnings    text[] NOT NULL DEFAULT '{}',
    created_at       timestamptz NOT NULL DEFAULT now(),
    created_by       text,
    updated_at       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_cloud_accounts_name ON cloud_accounts (lower(name));

CREATE TABLE cloud_resources (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id     uuid NOT NULL REFERENCES cloud_accounts(id) ON DELETE CASCADE,
    provider       text NOT NULL,
    kind           text NOT NULL CHECK (kind IN ('vm','host','cluster','datastore','disk','public_ip','network','security_group','load_balancer','storage','database','kubernetes','function')),
    external_id    text NOT NULL,
    name           text,
    region         text,
    resource_group text,                                   -- resource group / VPC / cluster / datacenter
    parent_id      text,                                   -- external id of the host (VM), VPC (instance) …
    state          text,
    state_since    timestamptz,
    size           text,                                   -- VM size / instance type / SKU
    os             text,
    hostname       text,
    private_ip     text,
    public_ip      text,
    cpu            integer,
    memory_mb      bigint,
    disk_gb        numeric(14,1),
    bios_uuid      text,
    macs           text[] NOT NULL DEFAULT '{}',
    tags           jsonb NOT NULL DEFAULT '{}',
    properties     jsonb NOT NULL DEFAULT '{}',
    created_at_provider timestamptz,
    asset_id       uuid REFERENCES assets(id) ON DELETE SET NULL,     -- the inventoried device (agent or scan) that is this machine
    asset_link_manual boolean NOT NULL DEFAULT false,                 -- linked (or unlinked) by hand: automatic matching leaves it alone
    department_id  uuid REFERENCES departments(id) ON DELETE SET NULL,   -- from tags (department / cost-centre)
    location_id    uuid REFERENCES locations(id) ON DELETE SET NULL,     -- from tags (location)
    first_seen_at  timestamptz NOT NULL DEFAULT now(),
    last_seen_at   timestamptz NOT NULL DEFAULT now(),
    deleted_at     timestamptz,
    UNIQUE (account_id, external_id)
);
CREATE INDEX ix_cloud_resources_kind ON cloud_resources (kind) WHERE deleted_at IS NULL;
CREATE INDEX ix_cloud_resources_asset ON cloud_resources (asset_id) WHERE asset_id IS NOT NULL;
CREATE INDEX ix_cloud_resources_parent ON cloud_resources (account_id, parent_id);

CREATE TABLE cloud_changes (
    id           bigserial PRIMARY KEY,
    resource_id  uuid NOT NULL REFERENCES cloud_resources(id) ON DELETE CASCADE,
    at           timestamptz NOT NULL DEFAULT now(),
    change       text NOT NULL CHECK (change IN ('created','changed','deleted','restored')),
    detail       jsonb NOT NULL DEFAULT '{}'
);
CREATE INDEX ix_cloud_changes_at ON cloud_changes (at DESC);
CREATE INDEX ix_cloud_changes_resource ON cloud_changes (resource_id, at DESC);

CREATE TABLE cloud_findings (
    id            bigserial PRIMARY KEY,
    resource_id   uuid NOT NULL REFERENCES cloud_resources(id) ON DELETE CASCADE,
    rule          text NOT NULL,
    severity      text NOT NULL CHECK (severity IN ('info','low','medium','high','critical')),
    title         text NOT NULL,
    detail        text,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at  timestamptz NOT NULL DEFAULT now(),
    resolved_at   timestamptz,
    ignored       boolean NOT NULL DEFAULT false,
    ignored_by    text,
    UNIQUE (resource_id, rule)
);
CREATE INDEX ix_cloud_findings_open ON cloud_findings (severity) WHERE resolved_at IS NULL AND NOT ignored;
CREATE INDEX ix_cloud_findings_first ON cloud_findings (first_seen_at DESC);

INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('Cloud and virtual resources', 'cloud_resources', true, true, 'Every virtual machine, host and cloud resource with its connection, region, state, size and linked device.',
  '{"columns":["provider","account","kind","name","region","state","size","os","private_ip","public_ip","device"],"filters":[],"sort":[{"column":"provider","dir":"asc"},{"column":"name","dir":"asc"}]}'),
 ('Virtual machines by platform', 'cloud_resources', true, true, 'Number of virtual machines per platform and state.',
  '{"columns":["provider","state"],"groupBy":["provider","state"],"filters":[{"column":"kind","op":"eq","value":"Virtual machine"}],"sort":[{"column":"count","dir":"desc"}],"chart":"bar"}'),
 ('Virtual machines without inventory', 'cloud_resources', true, true, 'Virtual machines not linked to an inventoried device (no agent and not scanned).',
  '{"columns":["provider","account","name","region","state","os","private_ip"],"filters":[{"column":"kind","op":"eq","value":"Virtual machine"},{"column":"linked","op":"eq","value":"no"}],"sort":[{"column":"name","dir":"asc"}]}'),
 ('Open cloud and virtualization findings', 'cloud_findings', true, true, 'Open findings: idle and unattached resources, exposure to the internet, unencrypted or public storage, old snapshots, capacity.',
  '{"columns":["severity","title","resource","provider","account","first_seen"],"filters":[{"column":"status","op":"eq","value":"Open"}],"sort":[{"column":"severity_rank","dir":"desc"}]}');
