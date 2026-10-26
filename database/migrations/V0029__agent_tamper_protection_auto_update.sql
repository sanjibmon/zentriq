-- V0029: Agent tamper protection (stopping or disabling the agent service needs the same one-hour code as uninstalling;
-- the console can pause protection, restart or disable an agent) and optional automatic agent updates.
SET search_path = zentriq, public;

-- Console control of the agent itself: pause / resume tamper protection, restart, disable.
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_kind_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_kind_check CHECK (kind IN ('service','uninstall','agent-update','remote-support','agent-control'));
ALTER TABLE asset_actions DROP CONSTRAINT IF EXISTS asset_actions_operation_check;
ALTER TABLE asset_actions ADD CONSTRAINT asset_actions_operation_check
    CHECK (operation IN ('start','stop','restart','uninstall','update','install','pause','resume','disable'));
-- Updates queued automatically (Agents › Settings › Update agents automatically) are marked, for the console and the limit.
ALTER TABLE asset_actions ADD COLUMN automatic boolean NOT NULL DEFAULT false;

-- What the agent reports about tamper protection on the device, and the most recent tampering it undid or refused.
ALTER TABLE agent_registrations
    ADD COLUMN tamper_state   text CHECK (tamper_state IN ('on','pending','paused','off')),
    ADD COLUMN tamper_until   timestamptz,
    ADD COLUMN last_tamper_at timestamptz,
    ADD COLUMN last_tamper    text;

-- Automatic updates are off until an administrator turns them on; at most 25 agents update at the same time.
UPDATE system_settings SET value = value || '{"autoUpdate":false,"autoUpdateMax":25}'::jsonb
 WHERE key = 'agents.settings' AND NOT value ? 'autoUpdate';
