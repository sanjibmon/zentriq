-- V0014: Zentriq Remote Support (MeshCentral engine run by the ZentriqRemote service).
SET search_path = zentriq, public;

-- Devices with the remote support agent, as reported by the remote support server, mapped to assets.
CREATE TABLE remote_nodes (
    node_id           text PRIMARY KEY,                -- MeshCentral node id ("node//…")
    asset_id          uuid REFERENCES assets(id) ON DELETE SET NULL,
    mapped_by         text NOT NULL DEFAULT 'hostname' CHECK (mapped_by IN ('hostname','manual')),
    name              text NOT NULL,
    host              text,
    os                text,
    ip                text,
    connected         boolean NOT NULL DEFAULT false,
    last_connected_at timestamptz,
    synced_at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_remote_nodes_asset ON remote_nodes (asset_id);

-- Sessions started from Zentriq (who opened what, on which device, from where).
CREATE TABLE remote_sessions (
    id          bigserial PRIMARY KEY,
    user_id     uuid REFERENCES users(id) ON DELETE SET NULL,
    user_name   text NOT NULL,
    asset_id    uuid REFERENCES assets(id) ON DELETE SET NULL,
    node_id     text,
    device_name text,
    view        text NOT NULL CHECK (view IN ('desktop','terminal','files','device','console')),
    source_ip   text,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_remote_sessions_time ON remote_sessions (created_at DESC);
CREATE INDEX ix_remote_sessions_asset ON remote_sessions (asset_id, created_at DESC);

-- Install / remove the remote support agent through the Zentriq Agent.
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_kind_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_kind_check CHECK (kind IN ('service','uninstall','agent-update','remote-support'));
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_operation_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_operation_check CHECK (operation IN ('start','stop','restart','uninstall','update','install'));

INSERT INTO permissions (code, description) VALUES
 ('remote.manage', 'Configure remote support and install or remove the remote support agent')
ON CONFLICT (code) DO NOTHING;
UPDATE permissions SET description = 'Start remote support sessions (desktop, terminal, files)' WHERE code = 'remote.start';
UPDATE permissions SET description = 'Full remote support rights (all devices, remote console)' WHERE code = 'remote.unattended';
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r CROSS JOIN (VALUES ('remote.start'), ('remote.unattended'), ('remote.manage')) p(code)
 WHERE r.id = 'admin'
ON CONFLICT DO NOTHING;

INSERT INTO system_settings (key, value) VALUES ('remote.settings', '{"prompt":true,"notify":true,"privacyBar":true}'::jsonb)
ON CONFLICT (key) DO NOTHING;
