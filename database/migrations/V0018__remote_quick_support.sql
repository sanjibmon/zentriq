-- V0018: Remote Support quick support sessions (on-demand help for users anywhere, including over the internet).
-- Each session has its own remote support device group with consent always on; the person who needs help opens an
-- invitation link, runs the small support app (nothing is installed) and approves the technician's requests. The
-- group, and with it the temporary device, is removed when the session ends or expires.
SET search_path = zentriq, public;

CREATE TABLE remote_quick_sessions (
    id              bigserial PRIMARY KEY,
    code            text NOT NULL UNIQUE,                -- 9 digits, a reference the technician and user can read out
    mesh_id         text,                                -- the session's device group ("mesh//…")
    invite_url      text NOT NULL,
    customer        text,
    email           text,
    note            text,
    created_by      uuid REFERENCES users(id) ON DELETE SET NULL,
    created_by_name text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,                -- the invitation can be used until then
    status          text NOT NULL DEFAULT 'waiting' CHECK (status IN ('waiting','connected','ended','expired')),
    node_id         text,
    device_name     text,
    device_os       text,
    device_ip       text,
    online          boolean NOT NULL DEFAULT false,
    connected_at    timestamptz,
    last_seen_at    timestamptz,
    ended_at        timestamptz,
    ended_by        text,
    end_reason      text
);
CREATE INDEX ix_remote_quick_active ON remote_quick_sessions (status) WHERE status IN ('waiting','connected');
CREATE INDEX ix_remote_quick_time ON remote_quick_sessions (created_at DESC);

ALTER TABLE remote_sessions ADD COLUMN quick_session_id bigint REFERENCES remote_quick_sessions(id) ON DELETE SET NULL;

-- Non-IT asset groups as their own list, so a group can be added (and kept) before it has categories. Categories keep
-- the group name; a trigger adds the group when a category names a new one.
CREATE TABLE nonit_groups (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name       text NOT NULL,
    builtin    boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_nonit_groups_name ON nonit_groups (lower(name));
INSERT INTO nonit_groups (name, builtin)
SELECT DISTINCT ON (lower(group_name)) group_name, bool_or(builtin) OVER (PARTITION BY lower(group_name))
  FROM nonit_categories ORDER BY lower(group_name), builtin DESC;

CREATE FUNCTION nonit_category_group() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO zentriq.nonit_groups (name) VALUES (NEW.group_name) ON CONFLICT DO NOTHING;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_nonit_category_group BEFORE INSERT OR UPDATE OF group_name ON nonit_categories
    FOR EACH ROW EXECUTE FUNCTION nonit_category_group();
