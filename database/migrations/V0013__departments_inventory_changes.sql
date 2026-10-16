-- V0013: Departments / units for assets, and hardware & software change history.
SET search_path = zentriq, public;

-- ---- Departments ---------------------------------------------------------------------------------------
-- The baseline table is reused: names are unique (case-insensitive); code and organization are optional.
ALTER TABLE departments ALTER COLUMN organization_id DROP NOT NULL;
ALTER TABLE departments ALTER COLUMN code DROP NOT NULL;
ALTER TABLE departments
    ADD COLUMN description text,
    ADD COLUMN created_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    ADD COLUMN created_at  timestamptz NOT NULL DEFAULT now();
CREATE UNIQUE INDEX ux_departments_name ON departments (lower(name));

ALTER TABLE assets DROP CONSTRAINT IF EXISTS assets_department_id_fkey;
ALTER TABLE assets ADD CONSTRAINT assets_department_id_fkey FOREIGN KEY (department_id) REFERENCES departments(id) ON DELETE SET NULL;
CREATE INDEX ix_assets_department ON assets (department_id) WHERE department_id IS NOT NULL;

-- ---- Inventory change history ---------------------------------------------------------------------------
-- One row per detected difference between two inventories of the same device (kept 365 days).
CREATE TABLE asset_changes (
    id           bigserial PRIMARY KEY,
    asset_id     uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    detected_at  timestamptz NOT NULL DEFAULT now(),
    kind         text NOT NULL CHECK (kind IN ('hardware','software')),
    change       text NOT NULL CHECK (change IN ('added','removed','changed','installed','uninstalled','updated')),
    component    text NOT NULL,          -- Memory, Processor, Disk, Application, Windows update, Operating system …
    item         text NOT NULL,          -- what changed (disk model, application name, KB number …)
    old_value    text,
    new_value    text,
    source       text
);
CREATE INDEX ix_asset_changes_time ON asset_changes (kind, detected_at DESC);
CREATE INDEX ix_asset_changes_asset ON asset_changes (asset_id, detected_at DESC);

INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('Assets by department', 'assets', true, true, 'Number of devices per department / unit.',
  '{"columns":["department"],"groupBy":["department"],"filters":[],"sort":[{"column":"count","dir":"desc"}],"chart":"bar"}'),
 ('Asset register by department', 'assets', true, true, 'Every active device with its department, type, operating system and owner.',
  '{"columns":["department","hostname","ip","type","os","logged_on_user","owner","criticality","last_contact"],"filters":[],"sort":[{"column":"department","dir":"asc"},{"column":"hostname","dir":"asc"}]}'),
 ('Hardware changes (30 days)', 'changes', true, true, 'Hardware added, removed or replaced on each device in the last 30 days.',
  '{"columns":["detected","hostname","department","change","component","item","old_value","new_value"],"filters":[{"column":"kind","op":"eq","value":"hardware"},{"column":"detected","op":"last_days","value":"30"}],"sort":[{"column":"detected","dir":"desc"}]}'),
 ('Software changes (30 days)', 'changes', true, true, 'Software installed, uninstalled or updated on each device in the last 30 days.',
  '{"columns":["detected","hostname","department","change","component","item","old_value","new_value"],"filters":[{"column":"kind","op":"eq","value":"software"},{"column":"detected","op":"last_days","value":"30"}],"sort":[{"column":"detected","dir":"desc"}]}');
