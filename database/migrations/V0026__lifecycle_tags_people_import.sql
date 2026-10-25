-- V0026: Asset Lifecycle — the organisation's own asset tag scheme (manual tags, or an automatic format with its own
-- prefix, year, location code and number), a people directory (add, edit, bulk upload; assignments add people too),
-- custom fields on IT assets, and bulk upload of IT assets, people and contracts from an Excel / CSV template.
SET search_path = zentriq, public;

-- Asset tags. mode: 'auto' gives the next tag in the format when an asset enters the register (added, uploaded, or found
-- by an agent / scan when tag_discovered is on); 'manual' never gives a tag by itself — people type their own.
INSERT INTO system_settings (key, value) VALUES
 ('lifecycle.tag_mode', '"auto"'),
 ('lifecycle.tag_format', '"{PREFIX}-{SEQ:6}"'),
 ('lifecycle.tag_org', '""'),
 ('lifecycle.tag_start', '1'),
 ('lifecycle.tag_discovered', 'false')
ON CONFLICT (key) DO NOTHING;
-- Keep the build 80 switch in step: auto_tag false meant "do not tag new items".
UPDATE system_settings SET value = '"manual"'
 WHERE key = 'lifecycle.tag_mode' AND EXISTS (SELECT 1 FROM system_settings s WHERE s.key = 'lifecycle.auto_tag' AND s.value = 'false'::jsonb);

-- lifecycle_tag_counters.prefix now holds the rendered tag without its number (for example "LAP-#", "ACME/2026/PUN/#"):
-- numbers restart per key. The build 80 keys ("LAP") are carried over to the default format's key ("LAP-#").
UPDATE lifecycle_tag_counters SET prefix = prefix || '-#' WHERE prefix !~ '#';

-- People directory: who IT assets are assigned to. Assignments by name stay as they are (assigned_to text).
CREATE TABLE lifecycle_people (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name          text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
    email         text CHECK (email IS NULL OR length(email) <= 254),
    employee_id   text CHECK (employee_id IS NULL OR length(employee_id) <= 50),
    title         text CHECK (title IS NULL OR length(title) <= 200),
    phone         text CHECK (phone IS NULL OR length(phone) <= 50),
    department_id uuid REFERENCES departments(id) ON DELETE SET NULL,
    location_id   uuid REFERENCES locations(id) ON DELETE SET NULL,
    manager       text CHECK (manager IS NULL OR length(manager) <= 200),
    status        text NOT NULL DEFAULT 'active' CHECK (status IN ('active','left')),
    notes         text CHECK (notes IS NULL OR length(notes) <= 4000),
    source        text NOT NULL DEFAULT 'manual' CHECK (source IN ('manual','import','assignment','directory')),
    created_at    timestamptz NOT NULL DEFAULT now(),
    created_by    text,
    updated_at    timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_lifecycle_people_name ON lifecycle_people (lower(btrim(name)));
CREATE UNIQUE INDEX ux_lifecycle_people_email ON lifecycle_people (lower(email)) WHERE email IS NOT NULL;
CREATE UNIQUE INDEX ux_lifecycle_people_empid ON lifecycle_people (lower(employee_id)) WHERE employee_id IS NOT NULL;

-- Everyone who already holds an IT asset joins the directory.
INSERT INTO lifecycle_people (name, email, department_id, source, created_by)
SELECT DISTINCT ON (lower(btrim(l.assigned_to))) btrim(l.assigned_to), l.assigned_email, a.department_id, 'assignment', 'system'
  FROM asset_lifecycle l JOIN assets a ON a.id = l.asset_id
 WHERE l.assigned_to IS NOT NULL AND btrim(l.assigned_to) <> ''
 ORDER BY lower(btrim(l.assigned_to)), l.assigned_at DESC NULLS LAST
ON CONFLICT DO NOTHING;

-- Custom fields on IT assets (the organisation's own columns: cost centre, insurance number, owner team …).
CREATE TABLE lifecycle_fields (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    key        text NOT NULL UNIQUE CHECK (key ~ '^[a-z][a-z0-9_]{0,39}$'),
    label      text NOT NULL CHECK (length(label) BETWEEN 1 AND 60),
    type       text NOT NULL DEFAULT 'text' CHECK (type IN ('text','number','date','list','yesno')),
    options    text[] NOT NULL DEFAULT '{}',
    required   boolean NOT NULL DEFAULT false,
    sort       integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE asset_lifecycle ADD COLUMN custom jsonb NOT NULL DEFAULT '{}';
