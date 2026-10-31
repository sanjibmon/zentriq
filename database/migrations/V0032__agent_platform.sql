-- Agents for Linux: the platform each agent runs on (windows-x64, linux-x64, linux-arm64). Agents before this version
-- do not report it; they are Windows agents.
SET search_path = zentriq, public;

ALTER TABLE agent_registrations ADD COLUMN platform text NOT NULL DEFAULT 'windows-x64'
    CHECK (platform IN ('windows-x64', 'linux-x64', 'linux-arm64'));
CREATE INDEX ix_agent_registrations_platform ON agent_registrations (platform);
