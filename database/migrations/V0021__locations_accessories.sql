-- V0021: Locations (like departments) for IT assets, and IT Accessories: stock kept per location, every change a
-- movement (receive, issue, return, transfer, write-off, stock count), holdings per person, low-stock alerts.
SET search_path = zentriq, public;

-- ---- Locations ---------------------------------------------------------------------------------------------------
CREATE TABLE locations (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    code        text CHECK (code IS NULL OR length(code) <= 30),
    description text CHECK (description IS NULL OR length(description) <= 500),
    created_by  uuid REFERENCES users(id) ON DELETE SET NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_locations_name ON locations (lower(name));

ALTER TABLE assets ADD COLUMN location_id uuid REFERENCES locations(id) ON DELETE SET NULL;
CREATE INDEX ix_assets_location ON assets (location_id) WHERE location_id IS NOT NULL;

-- ---- IT accessories ----------------------------------------------------------------------------------------------
CREATE TABLE accessory_items (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name           text NOT NULL CHECK (length(name) BETWEEN 1 AND 150),
    category       text NOT NULL CHECK (length(category) BETWEEN 1 AND 60),
    brand          text,
    model          text,
    sku            text,                   -- part number / barcode
    unit_cost      numeric(14,2) CHECK (unit_cost IS NULL OR unit_cost >= 0),
    reorder_level  integer NOT NULL DEFAULT 0 CHECK (reorder_level >= 0),
    returnable     boolean NOT NULL DEFAULT true,     -- false = consumable (cables, batteries): not expected back
    serial_tracked boolean NOT NULL DEFAULT false,    -- each unit has a serial number
    notes          text,
    active         boolean NOT NULL DEFAULT true,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_accessory_items_name ON accessory_items (lower(name));
CREATE UNIQUE INDEX ux_accessory_items_sku ON accessory_items (lower(sku)) WHERE sku IS NOT NULL;

-- Quantity on hand per item and location (kept by the movements; never edited directly).
CREATE TABLE accessory_stock (
    item_id     uuid NOT NULL REFERENCES accessory_items(id) ON DELETE CASCADE,
    location_id uuid NOT NULL REFERENCES locations(id) ON DELETE RESTRICT,
    quantity    integer NOT NULL DEFAULT 0 CHECK (quantity >= 0),   -- usable
    faulty      integer NOT NULL DEFAULT 0 CHECK (faulty >= 0),     -- returned faulty, not issued again
    low_since   timestamptz,                                         -- below the reorder level since (alerts)
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (item_id, location_id)
);

-- Every change of stock. Immutable (corrections are new movements).
CREATE TABLE accessory_movements (
    id             bigserial PRIMARY KEY,
    occurred_at    timestamptz NOT NULL DEFAULT now(),
    kind           text NOT NULL CHECK (kind IN ('receive','issue','return','transfer','writeoff','adjust')),
    item_id        uuid NOT NULL REFERENCES accessory_items(id) ON DELETE CASCADE,
    location_id    uuid NOT NULL REFERENCES locations(id) ON DELETE RESTRICT,
    to_location_id uuid REFERENCES locations(id) ON DELETE RESTRICT,     -- transfer
    quantity       integer NOT NULL,                                     -- change of usable stock at location_id (+/-)
    faulty_change  integer NOT NULL DEFAULT 0,                           -- change of faulty stock (return faulty, write-off faulty)
    person         text,                                                 -- issue / return: who
    person_key     text,                                                 -- lower(person) for matching returns
    department_id  uuid REFERENCES departments(id) ON DELETE SET NULL,
    asset_id       uuid REFERENCES assets(id) ON DELETE SET NULL,        -- issued with this device
    condition      text CHECK (condition IS NULL OR condition IN ('good','faulty')),
    reason         text,                                                 -- new joiner, replacement, damaged, lost, count difference …
    reference      text,                                                 -- invoice / PO / ticket number
    vendor         text,
    unit_cost      numeric(14,2),
    serials        text[] NOT NULL DEFAULT '{}',
    note           text,
    by_user        uuid REFERENCES users(id) ON DELETE SET NULL,
    by_name        text
);
CREATE INDEX ix_accessory_movements_time ON accessory_movements (occurred_at DESC);
CREATE INDEX ix_accessory_movements_item ON accessory_movements (item_id, occurred_at DESC);
CREATE INDEX ix_accessory_movements_person ON accessory_movements (person_key) WHERE person_key IS NOT NULL;

-- Serial-numbered units (items with serial_tracked).
CREATE TABLE accessory_units (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    item_id      uuid NOT NULL REFERENCES accessory_items(id) ON DELETE CASCADE,
    serial       text NOT NULL,
    location_id  uuid REFERENCES locations(id) ON DELETE SET NULL,
    status       text NOT NULL CHECK (status IN ('in_stock','issued','faulty','written_off')),
    person       text,
    person_key   text,
    department_id uuid REFERENCES departments(id) ON DELETE SET NULL,
    asset_id     uuid REFERENCES assets(id) ON DELETE SET NULL,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (item_id, serial)
);

-- ---- reports -----------------------------------------------------------------------------------------------------
INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('Assets by location', 'assets', true, true, 'Number of devices per location.',
  '{"columns":["location"],"groupBy":["location"],"filters":[],"sort":[{"column":"count","dir":"desc"}],"chart":"bar"}'),
 ('Accessory stock on hand', 'accessory_stock', true, true, 'Quantity and value of every accessory in every location, with the reorder level.',
  '{"columns":["location","category","item","quantity","faulty","reorder_level","stock_value","below_reorder"],"filters":[],"sort":[{"column":"location","dir":"asc"},{"column":"item","dir":"asc"}]}'),
 ('Accessories below reorder level', 'accessory_stock', true, true, 'Accessories to order: stock at or below the reorder level.',
  '{"columns":["location","category","item","quantity","reorder_level"],"filters":[{"column":"below_reorder","op":"eq","value":"yes"}],"sort":[{"column":"quantity","dir":"asc"}]}'),
 ('Accessories issued (30 days)', 'accessory_movements', true, true, 'Accessories given to people in the last 30 days.',
  '{"columns":["occurred","item","category","quantity_abs","person","department","location","reason","by"],"filters":[{"column":"kind","op":"eq","value":"issue"},{"column":"occurred","op":"last_days","value":"30"}],"sort":[{"column":"occurred","dir":"desc"}]}'),
 ('Accessory consumption by department', 'accessory_movements', true, true, 'Accessories issued per department and item.',
  '{"columns":["department","item"],"groupBy":["department","item"],"aggregates":[{"fn":"sum","column":"quantity_abs"}],"filters":[{"column":"kind","op":"eq","value":"issue"}],"sort":[{"column":"department","dir":"asc"}]}');
