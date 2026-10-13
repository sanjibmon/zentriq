-- V0008: agent updates requested from the console (recorded with the other device actions).
SET search_path = zentriq, public;

ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_kind_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_kind_check CHECK (kind IN ('service','uninstall','agent-update'));
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_operation_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_operation_check CHECK (operation IN ('start','stop','restart','uninstall','update'));
