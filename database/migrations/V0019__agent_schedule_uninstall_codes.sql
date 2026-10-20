-- V0019: Zentriq Agent schedule (check-in and inventory intervals chosen at installation, changeable in the console),
-- online state per agent, outages, short enrollment codes and uninstall protection codes.
SET search_path = zentriq, public;

-- How often each agent checks in (connectivity) and sends inventory, in minutes.
ALTER TABLE agent_registrations
    ADD COLUMN checkin_minutes   integer NOT NULL DEFAULT 30  CHECK (checkin_minutes BETWEEN 2 AND 1440),
    ADD COLUMN inventory_minutes integer NOT NULL DEFAULT 180 CHECK (inventory_minutes BETWEEN 30 AND 10080),
    -- The agent counts as online until this time (set at each check-in: two intervals plus 5 minutes of grace).
    ADD COLUMN online_until      timestamptz,
    -- The most recent time the agent could not reach the server, as it reported after reconnecting.
    ADD COLUMN last_outage_from  timestamptz,
    ADD COLUMN last_outage_to    timestamptz,
    -- Uninstall protection: the device's key (encrypted with the server's credential key) for one-hour uninstall codes.
    ADD COLUMN uninstall_key_protected text,
    ADD COLUMN uninstall_protected boolean NOT NULL DEFAULT false;   -- the agent reported that it holds the key

-- Existing agents (earlier builds checked in every 2 minutes): online if seen recently.
UPDATE agent_registrations SET online_until = last_checkin_at + interval '7 minutes' WHERE last_checkin_at IS NOT NULL;
CREATE INDEX ix_agent_registrations_online ON agent_registrations (online_until) WHERE status = 'active';

-- Short enrollment codes (20 characters): the first 6 identify the code; the server keeps only SHA-256 of the code.
ALTER TABLE agent_enrollment_tokens
    ADD COLUMN short_id          text,
    ADD COLUMN checkin_minutes   integer CHECK (checkin_minutes BETWEEN 2 AND 1440),
    ADD COLUMN inventory_minutes integer CHECK (inventory_minutes BETWEEN 30 AND 10080);
CREATE UNIQUE INDEX ux_agent_enrollment_tokens_short ON agent_enrollment_tokens (short_id) WHERE short_id IS NOT NULL;

-- Defaults for new agents and uninstall protection (System-wide, Agents › Settings).
INSERT INTO system_settings (key, value) VALUES
    ('agents.settings', '{"checkinMinutes":30,"inventoryMinutes":180,"uninstallProtection":true}')
ON CONFLICT (key) DO NOTHING;
