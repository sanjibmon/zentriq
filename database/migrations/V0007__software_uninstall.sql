-- V0007: software uninstall through the Zentriq Agent (recorded with the other device actions).
SET search_path = zentriq, public;

ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_kind_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_kind_check CHECK (kind IN ('service','uninstall'));
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_operation_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_operation_check CHECK (operation IN ('start','stop','restart','uninstall'));
ALTER TABLE asset_actions ADD COLUMN argument text;          -- e.g. the software version to remove
CREATE INDEX ix_asset_actions_time ON asset_actions (requested_at DESC);

INSERT INTO permissions (code, description) VALUES
 ('software.uninstall', 'Uninstall software from managed devices through the Zentriq Agent')
ON CONFLICT (code) DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code) VALUES ('admin', 'software.uninstall') ON CONFLICT DO NOTHING;
