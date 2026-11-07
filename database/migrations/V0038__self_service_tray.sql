-- V0038: the Zentriq tray icon on Windows devices.
--  * Software Center (self-service): packages of Software Deployment that IT offers to users; the agent installs them as
--    SYSTEM, so users need no administrator rights. Optional approval by IT and a device-group restriction.
--  * Get Remote Help: users send a help request from the tray; technicians see it in Remote Support and connect.

ALTER TABLE deployment_packages
    ADD COLUMN self_service             boolean NOT NULL DEFAULT false,
    ADD COLUMN self_service_approval    boolean NOT NULL DEFAULT false,
    ADD COLUMN self_service_description text,
    ADD COLUMN self_service_category    text,
    ADD COLUMN self_service_group_ids   uuid[] NOT NULL DEFAULT '{}';

CREATE TABLE self_service_requests (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    package_id    uuid NOT NULL REFERENCES deployment_packages(id) ON DELETE CASCADE,
    asset_id      uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    user_name     text,
    reason        text,
    status        text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','approved','denied','installed')),
    action_id     uuid REFERENCES asset_actions(id) ON DELETE SET NULL,
    decided_by    uuid REFERENCES users(id) ON DELETE SET NULL,
    decided_at    timestamptz,
    decision_note text,
    created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_self_service_requests_open ON self_service_requests (status, created_at DESC);
CREATE INDEX ix_self_service_requests_device ON self_service_requests (asset_id, package_id, created_at DESC);

CREATE TABLE help_requests (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    number      bigserial UNIQUE,
    asset_id    uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    user_name   text,
    message     text NOT NULL,
    contact     text,
    status      text NOT NULL DEFAULT 'open' CHECK (status IN ('open','in_progress','closed')),
    taken_by    uuid REFERENCES users(id) ON DELETE SET NULL,
    note        text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    closed_at   timestamptz
);
CREATE INDEX ix_help_requests_open ON help_requests (status, created_at DESC);

INSERT INTO system_settings (key, value) VALUES ('tray.settings',
 '{"enabled":true,"softwareCenter":true,"remoteHelp":true,"companyName":"","supportText":""}')
ON CONFLICT (key) DO NOTHING;
