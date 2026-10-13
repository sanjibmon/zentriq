-- V0006: device actions (service start/stop/restart), IPv4 normalisation, virtual-machine type, IPv6 display setting.
SET search_path = zentriq, public;

-- ---- Addresses: IPv4 clients were recorded as IPv4-mapped IPv6 (::ffff:a.b.c.d) by the dual-stack listener ----
UPDATE assets SET primary_ip = substr(host(primary_ip), 8)::inet
 WHERE primary_ip IS NOT NULL AND family(primary_ip) = 6 AND host(primary_ip) LIKE '::ffff:%.%';
UPDATE agent_registrations SET last_ip = substr(host(last_ip), 8)::inet
 WHERE last_ip IS NOT NULL AND family(last_ip) = 6 AND host(last_ip) LIKE '::ffff:%.%';

-- ---- A virtual machine is never a laptop/desktop, whatever chassis or battery the hypervisor emulates ----------
UPDATE hardware_inventory SET chassis = 'virtual'
 WHERE COALESCE(firmware->>'isVirtual', firmware->>'IsVirtual') = 'true' AND chassis IS DISTINCT FROM 'virtual';
UPDATE asset_inventory_documents SET document = jsonb_set(document, '{device,chassis}', '"virtual"')
 WHERE document #>> '{hardware,isVirtual}' = 'true' AND document #>> '{device,chassis}' IS DISTINCT FROM 'virtual';

INSERT INTO system_settings (key, value) VALUES ('display.show_ipv6', 'false') ON CONFLICT (key) DO NOTHING;

-- ---- Agent capabilities (reported at check-in) --------------------------------------------------------------
ALTER TABLE agent_registrations ADD COLUMN capabilities text[] NOT NULL DEFAULT '{}';

-- ---- Device actions --------------------------------------------------------------------------------------------
-- Run either by the device's Zentriq Agent (picked up at check-in) or agentlessly by the worker over WinRM/DCOM.
-- A credential typed by the administrator for one action is stored encrypted only until the action has run.
CREATE TABLE asset_actions (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    asset_id         uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    agent_id         uuid REFERENCES agent_registrations(id) ON DELETE SET NULL,
    channel          text NOT NULL CHECK (channel IN ('agent','agentless')),
    kind             text NOT NULL CHECK (kind IN ('service')),
    target           text NOT NULL,                       -- service name
    operation        text NOT NULL CHECK (operation IN ('start','stop','restart')),
    status           text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued','sent','running','succeeded','failed','expired')),
    detail           text,
    result_state     text,
    credential_id    uuid REFERENCES credentials(id) ON DELETE SET NULL,
    username         text,
    secret_protected text,
    job_id           uuid,
    requested_by     uuid REFERENCES users(id),
    requested_at     timestamptz NOT NULL DEFAULT now(),
    sent_at          timestamptz,
    completed_at     timestamptz
);
CREATE INDEX ix_asset_actions_asset ON asset_actions (asset_id, requested_at DESC);
CREATE INDEX ix_asset_actions_pending ON asset_actions (agent_id) WHERE status IN ('queued','sent');

INSERT INTO permissions (code, description) VALUES
 ('assets.control', 'Start, stop and restart services on managed devices')
ON CONFLICT (code) DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code) VALUES ('admin', 'assets.control') ON CONFLICT DO NOTHING;
