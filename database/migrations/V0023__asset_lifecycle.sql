-- V0023: Asset Lifecycle for IT assets (ISO/IEC 19770-1 / ITIL 4 ITAM stages): stage and status, asset tag, custody,
-- purchase and depreciation, lease, warranty, disposal with data sanitisation (NIST SP 800-88), contracts, policies per
-- device class, and an immutable lifecycle history.
SET search_path = zentriq, public;

-- Device class used by lifecycle policies (useful life, refresh age, tag prefix), from the device type or category.
CREATE OR REPLACE FUNCTION lifecycle_class(t text) RETURNS text
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE lower(coalesce(t, ''))
        WHEN 'laptop' THEN 'laptop' WHEN 'notebook' THEN 'laptop' WHEN 'tablet' THEN 'laptop'
        WHEN 'desktop' THEN 'desktop' WHEN 'endpoint' THEN 'desktop' WHEN 'workstation' THEN 'desktop' WHEN 'all-in-one' THEN 'desktop'
        WHEN 'mobile' THEN 'mobile'
        WHEN 'server' THEN 'server' WHEN 'hypervisor' THEN 'server' WHEN 'storage' THEN 'server' WHEN 'virtual_host' THEN 'server'
        WHEN 'virtual' THEN 'virtual'
        WHEN 'switch' THEN 'network' WHEN 'router' THEN 'network' WHEN 'firewall' THEN 'network' WHEN 'access_point' THEN 'network'
        WHEN 'wireless_controller' THEN 'network' WHEN 'load_balancer' THEN 'network' WHEN 'network' THEN 'network' WHEN 'network_device' THEN 'network'
        WHEN 'printer' THEN 'printer' WHEN 'mfp' THEN 'printer' WHEN 'scanner' THEN 'printer'
        ELSE 'other' END
$$;

-- Book value on a date: straight-line or double-declining balance down to the salvage value; 'none' keeps the cost.
CREATE OR REPLACE FUNCTION asset_book_value(cost numeric, salvage numeric, method text, life_months integer, start_date date, as_of date)
RETURNS numeric LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN cost IS NULL THEN NULL
        WHEN start_date IS NULL OR method = 'none' OR coalesce(life_months, 0) <= 0 THEN cost
        ELSE round(GREATEST(LEAST(coalesce(salvage, 0), cost),
            CASE WHEN method = 'declining_balance'
                 THEN CASE WHEN (as_of - start_date) / 30.4375 >= life_months THEN LEAST(coalesce(salvage, 0), cost)
                           ELSE cost * power(GREATEST(0, 1 - 2.0 / (life_months / 12.0)), GREATEST(0, (as_of - start_date) / 365.25)) END
                 ELSE cost - (cost - LEAST(coalesce(salvage, 0), cost)) * LEAST(1, GREATEST(0, (as_of - start_date) / 30.4375) / life_months)
            END), 2)
    END
$$;

CREATE TABLE lifecycle_policies (
    class               text PRIMARY KEY CHECK (class IN ('laptop','desktop','mobile','server','virtual','network','printer','other')),
    label               text NOT NULL,
    tag_prefix          text NOT NULL CHECK (tag_prefix ~ '^[A-Z0-9]{1,8}$'),
    useful_life_months  integer NOT NULL DEFAULT 60 CHECK (useful_life_months BETWEEN 0 AND 600),
    depreciation_method text NOT NULL DEFAULT 'straight_line' CHECK (depreciation_method IN ('straight_line','declining_balance','none')),
    salvage_percent     numeric(5,2) NOT NULL DEFAULT 5 CHECK (salvage_percent BETWEEN 0 AND 100),
    refresh_years       numeric(4,1) NOT NULL DEFAULT 5 CHECK (refresh_years BETWEEN 0 AND 30),
    updated_at          timestamptz NOT NULL DEFAULT now()
);
INSERT INTO lifecycle_policies (class, label, tag_prefix, useful_life_months, depreciation_method, salvage_percent, refresh_years) VALUES
 ('laptop',  'Laptops and tablets',   'LAP', 48, 'straight_line', 5, 4),
 ('desktop', 'Desktops',              'DSK', 60, 'straight_line', 5, 5),
 ('mobile',  'Mobile devices',        'MOB', 36, 'straight_line', 0, 3),
 ('server',  'Servers and storage',   'SRV', 72, 'straight_line', 5, 6),
 ('virtual', 'Virtual machines',      'VM',  0,  'none',          0, 0),
 ('network', 'Network devices',       'NET', 84, 'straight_line', 5, 7),
 ('printer', 'Printers and scanners', 'PRN', 60, 'straight_line', 5, 6),
 ('other',   'Other IT assets',       'AST', 60, 'straight_line', 5, 5);

CREATE TABLE lifecycle_tag_counters (prefix text PRIMARY KEY, next_value integer NOT NULL DEFAULT 1);

CREATE TABLE asset_lifecycle (
    asset_id              uuid PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    stage                 text NOT NULL DEFAULT 'in_use'
                          CHECK (stage IN ('planned','on_order','in_stock','in_use','in_maintenance','missing','retired','disposed')),
    status                text NOT NULL DEFAULT 'in_use',
    stage_changed_at      timestamptz NOT NULL DEFAULT now(),
    stage_reason          text,
    -- set for items added to stock before Zentriq has seen them: the first sighting deploys them (In use)
    auto_deploy           boolean NOT NULL DEFAULT false,
    asset_tag             text,
    assigned_to           text,
    assigned_email        text,
    assigned_at           timestamptz,
    expected_return       date,
    acquisition           text NOT NULL DEFAULT 'purchase' CHECK (acquisition IN ('purchase','lease','rental','donation','byod')),
    purchase_date         date,
    in_service_date       date,
    purchase_cost         numeric(14,2) CHECK (purchase_cost >= 0),
    currency              text,
    vendor                text,
    invoice_number        text,
    po_number             text,
    depreciation_method   text CHECK (depreciation_method IN ('straight_line','declining_balance','none')),
    useful_life_months    integer CHECK (useful_life_months BETWEEN 0 AND 600),
    salvage_value         numeric(14,2) CHECK (salvage_value >= 0),
    lessor                text,
    lease_start           date,
    lease_end             date,
    lease_monthly_cost    numeric(14,2),
    warranty_provider     text,
    warranty_type         text,
    warranty_start        date,
    warranty_end          date,
    disposed_on           date,
    disposal_method       text CHECK (disposal_method IN ('recycled','sold','donated','returned_to_lessor','destroyed')),
    disposal_vendor       text,
    disposal_value        numeric(14,2),
    sanitization_method   text CHECK (sanitization_method IN ('clear','purge','destroy','not_required')),
    sanitized_by          text,
    sanitized_on          date,
    certificate_ref       text,
    notes                 text,
    updated_at            timestamptz NOT NULL DEFAULT now(),
    updated_by            text,
    row_version           integer NOT NULL DEFAULT 1,
    CHECK (status ~ '^[a-z_]{2,30}$')
);
CREATE UNIQUE INDEX ux_asset_lifecycle_tag ON asset_lifecycle (lower(asset_tag)) WHERE asset_tag IS NOT NULL;
CREATE INDEX ix_asset_lifecycle_stage ON asset_lifecycle (stage, status);
CREATE INDEX ix_asset_lifecycle_person ON asset_lifecycle (lower(assigned_to)) WHERE assigned_to IS NOT NULL;
CREATE INDEX ix_asset_lifecycle_warranty ON asset_lifecycle (warranty_end) WHERE warranty_end IS NOT NULL;

CREATE TABLE asset_lifecycle_events (
    id          bigserial PRIMARY KEY,
    asset_id    uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    at          timestamptz NOT NULL DEFAULT now(),
    actor       text,
    kind        text NOT NULL CHECK (kind IN ('created','stage','assigned','returned','updated','disposed','auto','seen_again','contract')),
    from_stage  text,
    to_stage    text,
    from_status text,
    to_status   text,
    person      text,
    reason      text,
    detail      jsonb NOT NULL DEFAULT '{}'
);
CREATE INDEX ix_asset_lifecycle_events_asset ON asset_lifecycle_events (asset_id, at DESC);
CREATE INDEX ix_asset_lifecycle_events_at ON asset_lifecycle_events (at DESC);

CREATE TABLE asset_contracts (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name          text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    kind          text NOT NULL CHECK (kind IN ('warranty','amc','support','lease','insurance','other')),
    vendor        text,
    reference     text,
    start_date    date,
    end_date      date,
    cost          numeric(14,2) CHECK (cost >= 0),
    currency      text,
    notify_days   integer NOT NULL DEFAULT 90 CHECK (notify_days BETWEEN 0 AND 365),
    notes         text,
    created_at    timestamptz NOT NULL DEFAULT now(),
    created_by    text,
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CHECK (end_date IS NULL OR start_date IS NULL OR end_date >= start_date)
);
CREATE TABLE asset_contract_assets (
    contract_id uuid NOT NULL REFERENCES asset_contracts(id) ON DELETE CASCADE,
    asset_id    uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    PRIMARY KEY (contract_id, asset_id)
);
CREATE INDEX ix_asset_contract_assets_asset ON asset_contract_assets (asset_id);

-- One row per IT asset with its lifecycle, effective depreciation policy and book value (API, reports, dashboard).
CREATE VIEW v_asset_lifecycle AS
SELECT a.id AS asset_id, a.status AS asset_status, a.hostname, a.category, a.device_type, a.os_name, a.first_seen_at, a.last_seen_at,
       a.last_inventory_at, a.created_at AS asset_created_at, a.department_id, a.location_id, a.source,
       COALESCE(zentriq.hw_value(h.serial_number), zentriq.hw_value(a.serial_number), a.manual_serial) AS serial,
       COALESCE(zentriq.hw_value(h.vendor), zentriq.hw_value(a.manufacturer), a.manual_manufacturer) AS manufacturer,
       COALESCE(zentriq.hw_value(h.model), zentriq.hw_value(a.model), a.manual_model) AS model,
       c.class, lp.label AS class_label,
       l.stage, l.status, l.stage_changed_at, l.stage_reason, l.auto_deploy, l.asset_tag,
       l.assigned_to, l.assigned_email, l.assigned_at, l.expected_return,
       l.acquisition, l.purchase_date, l.in_service_date, l.purchase_cost, l.currency, l.vendor, l.invoice_number, l.po_number,
       COALESCE(l.depreciation_method, lp.depreciation_method) AS depreciation_method,
       COALESCE(l.useful_life_months, lp.useful_life_months) AS useful_life_months,
       COALESCE(l.salvage_value, round(l.purchase_cost * COALESCE(lp.salvage_percent, 0) / 100, 2)) AS salvage_value,
       (l.depreciation_method IS NOT NULL OR l.useful_life_months IS NOT NULL OR l.salvage_value IS NOT NULL) AS depreciation_custom,
       zentriq.asset_book_value(l.purchase_cost, COALESCE(l.salvage_value, round(l.purchase_cost * COALESCE(lp.salvage_percent, 0) / 100, 2)),
                                COALESCE(l.depreciation_method, lp.depreciation_method), COALESCE(l.useful_life_months, lp.useful_life_months),
                                COALESCE(l.purchase_date, l.in_service_date), current_date) AS book_value,
       l.lessor, l.lease_start, l.lease_end, l.lease_monthly_cost,
       l.warranty_provider, l.warranty_type, l.warranty_start, l.warranty_end,
       CASE WHEN l.warranty_end IS NULL THEN NULL ELSE l.warranty_end - current_date END AS warranty_days,
       l.disposed_on, l.disposal_method, l.disposal_vendor, l.disposal_value, l.sanitization_method, l.sanitized_by, l.sanitized_on, l.certificate_ref,
       l.notes, l.updated_at, l.updated_by, l.row_version,
       (current_date - COALESCE(l.purchase_date, l.in_service_date, a.first_seen_at::date)) / 30 AS age_months,
       COALESCE(lp.refresh_years, 0) AS refresh_years,
       (lp.refresh_years > 0 AND current_date - COALESCE(l.purchase_date, l.in_service_date, a.first_seen_at::date) >= lp.refresh_years * 365.25) AS refresh_due
  FROM asset_lifecycle l
  JOIN assets a ON a.id = l.asset_id AND a.status IN ('active','retired')
  LEFT JOIN asset_inventory_documents d ON d.asset_id = a.id
  LEFT JOIN hardware_inventory h ON h.asset_id = a.id
  CROSS JOIN LATERAL (SELECT zentriq.lifecycle_class(COALESCE(a.device_type, d.document #>> '{device,chassis}', h.chassis, a.category)) AS class) c
  LEFT JOIN lifecycle_policies lp ON lp.class = c.class;

-- Every IT asset has a lifecycle row from the moment it exists (discovery, agent, Active Directory, manual).
CREATE OR REPLACE FUNCTION asset_lifecycle_on_insert() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO zentriq.asset_lifecycle (asset_id, stage, status, in_service_date)
    VALUES (NEW.id, CASE WHEN NEW.status = 'retired' THEN 'retired' ELSE 'in_use' END,
            CASE WHEN NEW.status = 'retired' THEN 'end_of_life' ELSE 'in_use' END, current_date)
    ON CONFLICT (asset_id) DO NOTHING;
    RETURN NEW;
END $$;
CREATE TRIGGER tr_assets_lifecycle_insert AFTER INSERT ON assets FOR EACH ROW EXECUTE FUNCTION asset_lifecycle_on_insert();

-- Seen by discovery or an agent: items waiting in stock / on order are deployed, devices marked "not seen" are back.
CREATE OR REPLACE FUNCTION asset_lifecycle_on_seen() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE l zentriq.asset_lifecycle%ROWTYPE;
BEGIN
    SELECT * INTO l FROM zentriq.asset_lifecycle WHERE asset_id = NEW.id FOR UPDATE;
    IF NOT FOUND THEN RETURN NEW; END IF;
    IF (l.auto_deploy AND l.stage IN ('planned','on_order','in_stock')) OR (l.stage = 'missing' AND l.status = 'not_seen') THEN
        UPDATE zentriq.asset_lifecycle
           SET stage = 'in_use', status = CASE WHEN assigned_to IS NOT NULL THEN 'assigned' ELSE 'in_use' END,
               stage_changed_at = now(), stage_reason = 'Seen by Zentriq', auto_deploy = false,
               in_service_date = COALESCE(in_service_date, current_date), updated_at = now(), row_version = row_version + 1
         WHERE asset_id = NEW.id;
        INSERT INTO zentriq.asset_lifecycle_events (asset_id, actor, kind, from_stage, to_stage, from_status, to_status, reason)
        VALUES (NEW.id, 'system', 'auto', l.stage, 'in_use', l.status, CASE WHEN l.assigned_to IS NOT NULL THEN 'assigned' ELSE 'in_use' END,
                CASE WHEN l.stage = 'missing' THEN 'Seen again' ELSE 'First seen on the network: deployed' END);
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tr_assets_lifecycle_seen AFTER UPDATE OF last_seen_at ON assets
    FOR EACH ROW WHEN (NEW.last_seen_at > OLD.last_seen_at + interval '1 minute' AND NEW.status = 'active')
    EXECUTE FUNCTION asset_lifecycle_on_seen();

-- Existing devices: in use (or retired).
INSERT INTO asset_lifecycle (asset_id, stage, status, stage_changed_at, in_service_date)
SELECT id, CASE WHEN status = 'retired' THEN 'retired' ELSE 'in_use' END,
       CASE WHEN status = 'retired' THEN 'end_of_life' ELSE 'in_use' END,
       COALESCE(retired_at, first_seen_at), first_seen_at::date
  FROM assets
ON CONFLICT (asset_id) DO NOTHING;

INSERT INTO system_settings (key, value) VALUES
 ('lifecycle.not_seen_days', '60'),
 ('lifecycle.currency', '"INR"'),
 ('lifecycle.auto_tag', 'true')
ON CONFLICT (key) DO NOTHING;


-- Built-in reports.
INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('IT asset lifecycle register', 'asset_lifecycle', true, true, 'Every IT asset with its lifecycle stage, tag, custodian, cost, book value and warranty.',
  '{"columns":["asset_tag","device","class","stage","status","assigned_to","department","location","purchase_date","purchase_cost","book_value","warranty_end"],"filters":[{"column":"stage","op":"neq","value":"Disposed"}],"sort":[{"column":"asset_tag","dir":"asc"}]}'),
 ('IT assets by lifecycle stage', 'asset_lifecycle', true, true, 'Number of IT assets in each lifecycle stage and status.',
  '{"columns":["stage","status"],"groupBy":["stage","status"],"filters":[],"sort":[{"column":"count","dir":"desc"}],"chart":"bar"}'),
 ('Warranty ending in 90 days', 'asset_lifecycle', true, true, 'IT assets whose warranty ends in the next 90 days.',
  '{"columns":["asset_tag","device","class","assigned_to","location","warranty_provider","warranty_end","warranty_days"],"filters":[{"column":"warranty_days","op":"lte","value":"90"},{"column":"warranty_days","op":"gte","value":"0"}],"sort":[{"column":"warranty_end","dir":"asc"}]}'),
 ('IT asset value and depreciation', 'asset_lifecycle', true, true, 'Purchase cost and current book value per device class.',
  '{"columns":["class"],"groupBy":["class"],"aggregates":[{"fn":"sum","column":"purchase_cost"},{"fn":"sum","column":"book_value"}],"filters":[{"column":"stage","op":"neq","value":"Disposed"}],"sort":[{"column":"count","dir":"desc"}],"chart":"bar"}'),
 ('IT assets by custodian', 'asset_lifecycle', true, true, 'IT assets assigned to each person.',
  '{"columns":["assigned_to","asset_tag","device","class","department","assigned_at","expected_return"],"filters":[{"column":"assigned_to","op":"not_empty","value":""}],"sort":[{"column":"assigned_to","dir":"asc"}]}'),
 ('IT asset disposal register', 'asset_lifecycle', true, true, 'Disposed IT assets with disposal method, data sanitisation (NIST SP 800-88) and certificate.',
  '{"columns":["asset_tag","device","serial","disposed_on","disposal_method","disposal_vendor","disposal_value","sanitization_method","sanitized_by","certificate_ref"],"filters":[{"column":"stage","op":"eq","value":"Disposed"}],"sort":[{"column":"disposed_on","dir":"desc"}]}'),
 ('IT asset lifecycle history', 'asset_lifecycle_events', true, true, 'Every lifecycle change: stage, assignment, return and disposal, with who and why.',
  '{"columns":["at","device","asset_tag","kind","from_stage","to_stage","person","reason","by"],"filters":[],"sort":[{"column":"at","dir":"desc"}]}');
