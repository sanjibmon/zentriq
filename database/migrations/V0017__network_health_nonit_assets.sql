-- V0017: deeper network inventory (traffic, discards, spanning tree per port, memory, sensors, fans and power supplies,
-- BGP/OSPF peers) and the Non-IT Asset Inventory (furniture, appliances, vehicles, tools … entered by hand, by barcode or
-- from an Excel form).
SET search_path = zentriq, public;

-- ---- Network inventory: health and traffic -------------------------------------------------------------------------
ALTER TABLE network_devices
    ADD COLUMN memory_percent  integer,
    ADD COLUMN memory_total_mb bigint,
    ADD COLUMN temperature_c   numeric(6,1),
    ADD COLUMN sensors         jsonb NOT NULL DEFAULT '[]',
    ADD COLUMN components      jsonb NOT NULL DEFAULT '[]',
    ADD COLUMN routing_peers   jsonb NOT NULL DEFAULT '[]',
    ADD COLUMN components_bad  integer NOT NULL DEFAULT 0,      -- fans / power supplies / temperatures not normal
    ADD COLUMN peers_down      integer NOT NULL DEFAULT 0,      -- BGP / OSPF neighbours not established
    ADD COLUMN busy_ports      integer NOT NULL DEFAULT 0;      -- ports above 70 % of their speed

ALTER TABLE network_interfaces
    ADD COLUMN in_octets     numeric(20,0),
    ADD COLUMN out_octets    numeric(20,0),
    ADD COLUMN in_discards   bigint,
    ADD COLUMN out_discards  bigint,
    ADD COLUMN counters_at   timestamptz,
    ADD COLUMN in_bps        bigint,            -- average since the previous collection
    ADD COLUMN out_bps       bigint,
    ADD COLUMN util_percent  numeric(5,1),      -- highest of in / out against the port speed
    ADD COLUMN stp_state     text,
    ADD COLUMN lag_if_index  integer;

-- ---- Non-IT assets ---------------------------------------------------------------------------------------------------
CREATE TABLE nonit_categories (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL,
    group_name  text NOT NULL,
    builtin     boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_nonit_categories_name ON nonit_categories (lower(name));

-- Organisation-defined extra fields (shown on the form, in the Excel template, export and reports).
CREATE TABLE nonit_fields (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    key         text NOT NULL UNIQUE CHECK (key ~ '^[a-z][a-z0-9_]{0,39}$'),
    label       text NOT NULL,
    type        text NOT NULL DEFAULT 'text' CHECK (type IN ('text','number','date','yesno','list')),
    options     text[] NOT NULL DEFAULT '{}',
    sort        integer NOT NULL DEFAULT 0,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE SEQUENCE nonit_tag_seq START 1;

CREATE TABLE nonit_assets (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    asset_tag          text NOT NULL,
    barcode            text,
    name               text NOT NULL,
    category_id        uuid REFERENCES nonit_categories(id) ON DELETE SET NULL,
    manufacturer       text,
    model              text,
    serial_number      text,
    status             text NOT NULL DEFAULT 'in_use'
                       CHECK (status IN ('in_use','in_store','under_repair','under_maintenance','reserved','lost','disposed')),
    condition          text CHECK (condition IN ('new','good','fair','poor','damaged')),
    quantity           integer NOT NULL DEFAULT 1 CHECK (quantity >= 0),
    location           text,
    department_id      uuid REFERENCES departments(id) ON DELETE SET NULL,
    assigned_to        text,
    purchase_date      date,
    vendor             text,
    purchase_cost      numeric(14,2),
    invoice_number     text,
    po_number          text,
    warranty_until     date,
    amc_vendor         text,
    amc_until          date,
    insurance_until    date,
    last_maintenance   date,
    next_maintenance   date,
    useful_life_years  numeric(5,1),
    salvage_value      numeric(14,2),
    disposed_on        date,
    notes              text,
    custom             jsonb NOT NULL DEFAULT '{}',
    created_at         timestamptz NOT NULL DEFAULT now(),
    created_by         text,
    updated_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         text,
    row_version        integer NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX ux_nonit_assets_tag ON nonit_assets (lower(asset_tag));
CREATE UNIQUE INDEX ux_nonit_assets_barcode ON nonit_assets (barcode) WHERE barcode IS NOT NULL;
CREATE INDEX ix_nonit_assets_category ON nonit_assets (category_id);
CREATE INDEX ix_nonit_assets_status ON nonit_assets (status);
CREATE INDEX ix_nonit_assets_serial ON nonit_assets (lower(serial_number));

CREATE TABLE nonit_history (
    id        bigserial PRIMARY KEY,
    asset_id  uuid NOT NULL REFERENCES nonit_assets(id) ON DELETE CASCADE,
    at        timestamptz NOT NULL DEFAULT now(),
    actor     text,
    action    text NOT NULL,                       -- created | updated | imported | status | moved | assigned | disposed
    changes   jsonb NOT NULL DEFAULT '{}'
);
CREATE INDEX ix_nonit_history_asset ON nonit_history (asset_id, at DESC);

-- Built-in categories: official non-IT items an organisation keeps on its asset register.
INSERT INTO nonit_categories (name, group_name, builtin) VALUES
 ('Office chair','Furniture',true), ('Desk / workstation','Furniture',true), ('Conference table','Furniture',true),
 ('Storage cabinet / almirah','Furniture',true), ('Filing cabinet','Furniture',true), ('Sofa / lounge seating','Furniture',true),
 ('Shelving / rack','Furniture',true), ('Reception counter','Furniture',true), ('Locker','Furniture',true),
 ('Air conditioner','Electrical & appliances',true), ('Refrigerator','Electrical & appliances',true), ('Water dispenser / purifier','Electrical & appliances',true),
 ('Microwave / oven','Electrical & appliances',true), ('Coffee / vending machine','Electrical & appliances',true), ('Fan / air cooler','Electrical & appliances',true),
 ('Water heater / geyser','Electrical & appliances',true), ('Air purifier','Electrical & appliances',true), ('Television / display','Electrical & appliances',true),
 ('Shredder','Office equipment',true), ('Laminator / binding machine','Office equipment',true), ('Franking machine','Office equipment',true),
 ('Currency counting machine','Office equipment',true), ('Whiteboard / projector screen','Office equipment',true), ('Time clock / attendance machine','Office equipment',true),
 ('Projector','Audio-visual',true), ('Speaker / PA system','Audio-visual',true), ('Video-conference camera','Audio-visual',true), ('Microphone','Audio-visual',true),
 ('Diesel generator','Facility & building',true), ('Inverter / battery bank','Facility & building',true), ('Elevator / lift','Facility & building',true),
 ('HVAC unit','Facility & building',true), ('Water pump','Facility & building',true), ('Electrical panel','Facility & building',true), ('Solar panel system','Facility & building',true),
 ('Fire extinguisher','Safety & security',true), ('Smoke / fire alarm panel','Safety & security',true), ('First-aid kit','Safety & security',true),
 ('Safe / vault','Safety & security',true), ('Metal detector','Safety & security',true), ('Emergency light','Safety & security',true),
 ('Car','Vehicles',true), ('Two-wheeler','Vehicles',true), ('Van / truck','Vehicles',true), ('Forklift','Vehicles',true), ('Bicycle','Vehicles',true),
 ('Power tool','Tools & machinery',true), ('Hand tool set','Tools & machinery',true), ('Ladder','Tools & machinery',true),
 ('Production machine','Tools & machinery',true), ('Compressor','Tools & machinery',true), ('Welding machine','Tools & machinery',true),
 ('Pantry equipment','Kitchen & pantry',true), ('Utensil set','Kitchen & pantry',true), ('Dishwasher','Kitchen & pantry',true),
 ('Medical equipment','Medical & lab',true), ('Laboratory instrument','Medical & lab',true), ('Weighing scale','Medical & lab',true),
 ('Artwork / decor','Other',true), ('Signage','Other',true), ('Plant / planter','Other',true);

INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('Network device health', 'network_devices', true, true, 'CPU, memory, temperature, fans and power supplies, routing neighbours and busy ports per network device.',
  '{"columns":["hostname","type","model","cpu","memory","temperature","components_bad","peers","peers_down","busy_ports","last_boot"],"filters":[],"sort":[{"column":"cpu","dir":"desc"}]}'),
 ('Busiest switch ports', 'network_ports', true, true, 'Ports by utilisation between the last two collections, with traffic in and out.',
  '{"columns":["hostname","port","port_description","speed","util","in_mbps","out_mbps","discards","connected"],"filters":[{"column":"util","op":"gt","value":"0"}],"sort":[{"column":"util","dir":"desc"}]}');

-- Donut charts are no longer offered: saved reports that used one are drawn as a share bar.
UPDATE report_definitions SET definition = jsonb_set(definition, '{chart}', '"share"') WHERE definition ->> 'chart' = 'donut';
UPDATE dashboard_widgets SET settings = jsonb_set(settings, '{chart}', '"share"') WHERE settings ->> 'chart' = 'donut';

-- Built-in Non-IT reports.
INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('Non-IT asset register', 'nonit_assets', true, true, 'Every non-IT asset with category, location, department, purchase and cover details.',
  '{"columns":["asset_tag","name","category","status","condition","quantity","location","department","assigned_to","purchase_date","cost","current_value","warranty_until"],"filters":[{"column":"status","op":"neq","value":"Disposed"}],"sort":[{"column":"asset_tag","dir":"asc"}]}'),
 ('Non-IT assets by category', 'nonit_assets', true, true, 'Number of items and purchase value per category group and category.',
  '{"columns":["category_group","category"],"groupBy":["category_group","category"],"aggregates":[{"fn":"sum","column":"quantity"},{"fn":"sum","column":"total_cost"}],"filters":[{"column":"status","op":"neq","value":"Disposed"}],"sort":[{"column":"count","dir":"desc"}],"chart":"bar"}'),
 ('Non-IT assets by location and department', 'nonit_assets', true, true, 'Where the items are and who uses them.',
  '{"columns":["location","department"],"groupBy":["location","department"],"filters":[{"column":"status","op":"neq","value":"Disposed"}],"sort":[{"column":"count","dir":"desc"}]}'),
 ('Non-IT warranty ending (90 days)', 'nonit_assets', true, true, 'Items whose warranty ends in the next 90 days, with their AMC and insurance cover.',
  '{"columns":["asset_tag","name","category","location","warranty_until","amc_vendor","amc_until","insurance_until"],"filters":[{"column":"warranty_days","op":"lte","value":"90"},{"column":"warranty_days","op":"gte","value":"0"}],"sort":[{"column":"warranty_until","dir":"asc"}]}'),
 ('Non-IT maintenance due (30 days)', 'nonit_assets', true, true, 'Items whose next maintenance is due within 30 days or overdue.',
  '{"columns":["asset_tag","name","category","location","last_maintenance","next_maintenance","maintenance_days","amc_vendor"],"filters":[{"column":"maintenance_days","op":"lte","value":"30"}],"sort":[{"column":"next_maintenance","dir":"asc"}]}'),
 ('Non-IT asset value and depreciation', 'nonit_assets', true, true, 'Purchase cost and current (straight-line) value per category group.',
  '{"columns":["category_group"],"groupBy":["category_group"],"aggregates":[{"fn":"sum","column":"total_cost"},{"fn":"sum","column":"current_value"}],"filters":[{"column":"status","op":"neq","value":"Disposed"}],"sort":[{"column":"count","dir":"desc"}],"chart":"bar"}'),
 ('Disposed and lost non-IT assets', 'nonit_assets', true, true, 'Items disposed of or reported lost.',
  '{"columns":["asset_tag","name","category","status","disposed_on","location","cost","notes"],"filters":[{"column":"status","op":"in","value":"Disposed,Lost / missing"}],"sort":[{"column":"disposed_on","dir":"desc"}]}');
