-- V0035: Network inventory — connected devices register (every MAC address seen on a switch port with its IP address,
-- vendor, switch port and history), MAC address vendors (IEEE registry), interface subnets for IP address management.
-- Software License Management — software products, licences (perpetual, subscription, volume, OEM, …), allocations,
-- Microsoft 365 cloud licences, compliance alerts. Web Apps & Accounts — the web (SaaS) applications in use, their
-- accounts, usage seen on devices and Microsoft Entra single sign-on applications.
SET search_path = zentriq, public;

-- =====================================================================================================================
-- Network inventory
-- =====================================================================================================================
ALTER TABLE network_interfaces ADD COLUMN subnets cidr[] NOT NULL DEFAULT '{}';

-- MAC address prefixes (IEEE MA-L 24 bit, MA-M 28 bit, MA-S 36 bit) as lower-case hex without separators.
CREATE TABLE mac_vendors (
    prefix  text PRIMARY KEY CHECK (prefix ~ '^[0-9a-f]{6}([0-9a-f]|[0-9a-f]{3})?$'),
    vendor  text NOT NULL
);

-- The vendor of a MAC address: the longest registered prefix; randomised (locally administered) addresses are private.
CREATE FUNCTION mac_vendor(m macaddr) RETURNS text LANGUAGE sql STABLE AS $$
    SELECT CASE WHEN (get_byte(decode(left(replace(m::text, ':', ''), 2), 'hex'), 0) & 2) = 2 THEN 'Private (randomised address)'
                ELSE (SELECT v.vendor FROM zentriq.mac_vendors v
                       WHERE v.prefix IN (left(replace(m::text, ':', ''), 9), left(replace(m::text, ':', ''), 7), left(replace(m::text, ':', ''), 6))
                       ORDER BY length(v.prefix) DESC LIMIT 1) END
$$;

-- A few prefixes so that virtual machines and common gateways are named before the registry is downloaded.
INSERT INTO mac_vendors (prefix, vendor) VALUES
 ('005056', 'VMware, Inc.'), ('000c29', 'VMware, Inc.'), ('000569', 'VMware, Inc.'), ('001c14', 'VMware, Inc.'),
 ('00155d', 'Microsoft Corporation (Hyper-V)'), ('525400', 'QEMU / KVM virtual NIC'), ('080027', 'Oracle VirtualBox'),
 ('00163e', 'Xensource, Inc.'), ('00005e', 'IANA (VRRP virtual router)'), ('00000c', 'Cisco Systems, Inc')
ON CONFLICT (prefix) DO NOTHING;

-- Every device (MAC address) seen on an access port of a managed switch.
CREATE TABLE network_endpoints (
    mac              macaddr PRIMARY KEY,
    ip               inet,
    vendor           text,
    kind             text,                    -- guessed from the vendor and the port: phone, camera, printer, access point, iot, virtual …
    asset_id         uuid REFERENCES assets(id) ON DELETE SET NULL,   -- the inventoried device with this MAC address
    switch_id        uuid REFERENCES assets(id) ON DELETE SET NULL,
    if_index         integer,
    port             text,
    vlan             integer,
    behind_unmanaged boolean NOT NULL DEFAULT false,
    first_seen       timestamptz NOT NULL DEFAULT now(),
    last_seen        timestamptz NOT NULL DEFAULT now(),
    prev_switch_id   uuid REFERENCES assets(id) ON DELETE SET NULL,
    prev_port        text,
    moved_at         timestamptz,
    prev_ip          inet,
    ip_changed_at    timestamptz,
    baseline         boolean NOT NULL DEFAULT false,   -- present when the register was first built: never alerted as new
    acknowledged     boolean NOT NULL DEFAULT false,   -- an unknown device an administrator marked as known
    note             text CHECK (note IS NULL OR length(note) <= 500),
    ack_by           text,
    ack_at           timestamptz
);
CREATE INDEX ix_network_endpoints_switch ON network_endpoints (switch_id, if_index);
CREATE INDEX ix_network_endpoints_ip ON network_endpoints (ip);
CREATE INDEX ix_network_endpoints_unknown ON network_endpoints (first_seen) WHERE asset_id IS NULL AND NOT acknowledged;

-- =====================================================================================================================
-- Web Apps & Accounts (before licences: a cloud software product can point to its web app)
-- =====================================================================================================================
CREATE TABLE webapps (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    catalog_key    text UNIQUE,
    name           text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 120),
    vendor         text,
    category       text NOT NULL DEFAULT 'Other',
    url            text,
    domains        text[] NOT NULL DEFAULT '{}',
    status         text NOT NULL DEFAULT 'review' CHECK (status IN ('sanctioned','review','unsanctioned','blocked')),
    owner          text,
    department_id  uuid REFERENCES departments(id) ON DELETE SET NULL,
    sso            boolean NOT NULL DEFAULT false,
    entra_app_id   text,
    cost_per_user  numeric(14,2),
    currency       text NOT NULL DEFAULT 'USD',
    renewal_date   date,
    notes          text CHECK (notes IS NULL OR length(notes) <= 4000),
    source         text NOT NULL DEFAULT 'manual' CHECK (source IN ('manual','catalog','usage','entra','m365')),
    first_seen_at  timestamptz,
    created_by     text,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_webapps_name ON webapps (lower(btrim(name)));

-- Use seen on devices (the device's DNS cache at inventory time, matched against the app's domains): per app, device and user.
CREATE TABLE webapp_usage (
    app_id     uuid NOT NULL REFERENCES webapps(id) ON DELETE CASCADE,
    asset_id   uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    user_name  text NOT NULL DEFAULT '',
    first_seen timestamptz NOT NULL DEFAULT now(),
    last_seen  timestamptz NOT NULL DEFAULT now(),
    days       integer NOT NULL DEFAULT 1,
    last_day   date NOT NULL DEFAULT current_date,
    PRIMARY KEY (app_id, asset_id, user_name)
);
CREATE INDEX ix_webapp_usage_asset ON webapp_usage (asset_id);

CREATE TABLE webapp_accounts (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    app_id        uuid NOT NULL REFERENCES webapps(id) ON DELETE CASCADE,
    user_name     text NOT NULL CHECK (length(btrim(user_name)) BETWEEN 1 AND 254),
    display_name  text,
    email         text,
    role          text NOT NULL DEFAULT 'user' CHECK (role IN ('owner','admin','user','guest','service')),
    status        text NOT NULL DEFAULT 'active' CHECK (status IN ('active','disabled','invited','removed')),
    source        text NOT NULL DEFAULT 'manual' CHECK (source IN ('manual','import','entra','m365','usage')),
    last_login    timestamptz,
    license_tier  text,
    cost          numeric(14,2),
    department    text,
    external_id   text,
    notes         text CHECK (notes IS NULL OR length(notes) <= 1000),
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    synced_at     timestamptz
);
CREATE UNIQUE INDEX ux_webapp_accounts_user ON webapp_accounts (app_id, lower(user_name));
CREATE INDEX ix_webapp_accounts_email ON webapp_accounts (lower(email));

-- =====================================================================================================================
-- Software License Management
-- =====================================================================================================================
-- Microsoft 365 / Entra ID connection (app registration, client credentials): subscribed licences, assigned users,
-- single sign-on applications and their users. The client credential is encrypted with the installation key.
CREATE TABLE sam_cloud_connections (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    provider          text NOT NULL DEFAULT 'm365' CHECK (provider IN ('m365')),
    name              text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 120),
    tenant_id         text NOT NULL,
    client_id         text NOT NULL,
    client_protected  text,
    enabled           boolean NOT NULL DEFAULT true,
    sync_apps         boolean NOT NULL DEFAULT true,     -- also read single sign-on applications into Web Apps & Accounts
    last_sync_at      timestamptz,
    last_status       text,
    last_error        text,
    last_note         text,
    created_by        text,
    created_at        timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_sam_cloud_connections_name ON sam_cloud_connections (lower(btrim(name)));

CREATE TABLE sam_cloud_skus (
    connection_id  uuid NOT NULL REFERENCES sam_cloud_connections(id) ON DELETE CASCADE,
    sku_id         text NOT NULL,
    sku_part       text NOT NULL,
    name           text,
    enabled_units  integer NOT NULL DEFAULT 0,    -- purchased and active
    suspended      integer NOT NULL DEFAULT 0,
    warning        integer NOT NULL DEFAULT 0,    -- in the grace period after expiry
    consumed       integer NOT NULL DEFAULT 0,    -- assigned to users
    status         text,                           -- Enabled | Warning | Suspended | Deleted | LockedOut
    next_lifecycle timestamptz,                    -- next renewal or expiry of its subscriptions (directory/subscriptions)
    is_trial       boolean NOT NULL DEFAULT false,
    synced_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (connection_id, sku_id)
);

CREATE TABLE sam_cloud_users (
    connection_id   uuid NOT NULL REFERENCES sam_cloud_connections(id) ON DELETE CASCADE,
    user_id         text NOT NULL,
    upn             text NOT NULL,
    display_name    text,
    mail            text,
    department      text,
    job_title       text,
    account_enabled boolean,
    last_sign_in    timestamptz,
    skus            text[] NOT NULL DEFAULT '{}',
    synced_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (connection_id, user_id)
);
CREATE INDEX ix_sam_cloud_users_upn ON sam_cloud_users (lower(upn));

-- Wildcard patterns (* and ?) as ILIKE patterns.
CREATE FUNCTION sam_like(patterns text[]) RETURNS text[] LANGUAGE sql IMMUTABLE AS $$
    SELECT COALESCE(array_agg(replace(replace(replace(replace(replace(p, '\', '\\'), '%', '\%'), '_', '\_'), '*', '%'), '?', '_')), '{}')
      FROM unnest(patterns) p WHERE btrim(p) <> ''
$$;

-- A software product the organisation licenses, and how its installations are found in the software inventory.
CREATE TABLE sam_products (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name               text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 160),
    publisher          text,
    category           text NOT NULL DEFAULT 'Other',
    name_patterns      text[] NOT NULL DEFAULT '{}',     -- installed software names, wildcards * and ?
    exclude_patterns   text[] NOT NULL DEFAULT '{}',
    publisher_patterns text[] NOT NULL DEFAULT '{}',
    metric             text NOT NULL DEFAULT 'device' CHECK (metric IN ('device','user','core','concurrent','site')),
    kind               text NOT NULL DEFAULT 'installed' CHECK (kind IN ('installed','cloud')),
    needs_license      boolean NOT NULL DEFAULT true,
    webapp_id          uuid REFERENCES webapps(id) ON DELETE SET NULL,
    cloud_skus         text[] NOT NULL DEFAULT '{}',     -- Microsoft 365 SKU part numbers (e.g. SPE_E3)
    catalog_key        text,
    notes              text CHECK (notes IS NULL OR length(notes) <= 4000),
    created_by         text,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_sam_products_name ON sam_products (lower(btrim(name)));

CREATE TABLE sam_licenses (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    product_id       uuid NOT NULL REFERENCES sam_products(id) ON DELETE CASCADE,
    name             text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
    license_type     text NOT NULL DEFAULT 'perpetual'
                     CHECK (license_type IN ('perpetual','subscription','volume','oem','enterprise','trial','freeware','open_source')),
    metric           text CHECK (metric IN ('device','user','core','concurrent','site')),   -- null: the product's
    quantity         integer CHECK (quantity IS NULL OR quantity >= 0),                        -- null: unlimited
    edition          text,
    version          text,
    downgrade_rights boolean NOT NULL DEFAULT false,
    vendor           text,
    reseller         text,
    po_number        text,
    invoice_number   text,
    contract_number  text,
    purchase_date    date,
    start_date       date,
    end_date         date,                 -- subscription / term end
    maintenance_end  date,                 -- support or software assurance of a perpetual licence
    auto_renew       boolean NOT NULL DEFAULT false,
    billing          text CHECK (billing IN ('monthly','quarterly','annual','multi_year','one_time')),
    unit_cost        numeric(14,2),
    total_cost       numeric(14,2),
    currency         text NOT NULL DEFAULT 'USD',
    notify_days      integer NOT NULL DEFAULT 30 CHECK (notify_days BETWEEN 0 AND 365),
    key_hint         text,                 -- last characters of the licence key, shown in the console
    key_protected    text,                 -- the full key, encrypted
    department_id    uuid REFERENCES departments(id) ON DELETE SET NULL,
    owner            text,
    cloud_connection_id uuid REFERENCES sam_cloud_connections(id) ON DELETE SET NULL,
    cloud_sku        text,                 -- SKU part number: quantity and use come from Microsoft 365
    status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active','retired')),
    synced           boolean NOT NULL DEFAULT false,   -- created and kept current by the Microsoft 365 connection
    notes            text CHECK (notes IS NULL OR length(notes) <= 4000),
    created_by       text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CHECK (end_date IS NULL OR start_date IS NULL OR end_date >= start_date)
);
CREATE INDEX ix_sam_licenses_product ON sam_licenses (product_id);
CREATE INDEX ix_sam_licenses_end ON sam_licenses (end_date) WHERE status = 'active';

-- Licences given to a device or a user (named-user / device licences).
CREATE TABLE sam_allocations (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    license_id  uuid NOT NULL REFERENCES sam_licenses(id) ON DELETE CASCADE,
    asset_id    uuid REFERENCES assets(id) ON DELETE CASCADE,
    user_name   text,
    note        text,
    created_by  text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    CHECK (asset_id IS NOT NULL OR user_name IS NOT NULL)
);
CREATE UNIQUE INDEX ux_sam_allocations_asset ON sam_allocations (license_id, asset_id) WHERE asset_id IS NOT NULL;
CREATE UNIQUE INDEX ux_sam_allocations_user ON sam_allocations (license_id, lower(user_name)) WHERE asset_id IS NULL;

-- Discrepancies found by the evaluation (hourly and after changes): one open row per problem; resolved when it is gone.
CREATE TABLE sam_alerts (
    id          bigserial PRIMARY KEY,
    key         text NOT NULL UNIQUE,
    kind        text NOT NULL CHECK (kind IN ('shortfall','unlicensed','expiring','expired','maintenance','cloud_overuse','cloud_expiring','unused','not_genuine',
                                              'webapp_unsanctioned','webapp_orphaned','webapp_inactive')),
    severity    text NOT NULL DEFAULT 'medium' CHECK (severity IN ('info','low','medium','high','critical')),
    product_id  uuid REFERENCES sam_products(id) ON DELETE CASCADE,
    license_id  uuid REFERENCES sam_licenses(id) ON DELETE CASCADE,
    asset_id    uuid REFERENCES assets(id) ON DELETE CASCADE,
    app_id      uuid REFERENCES webapps(id) ON DELETE CASCADE,
    title       text NOT NULL,
    detail      text,
    link        text,
    first_at    timestamptz NOT NULL DEFAULT now(),
    last_at     timestamptz NOT NULL DEFAULT now(),
    resolved_at timestamptz,
    notified_at timestamptz,
    dismissed   boolean NOT NULL DEFAULT false,
    dismissed_by text
);
CREATE INDEX ix_sam_alerts_open ON sam_alerts (kind) WHERE resolved_at IS NULL;

INSERT INTO system_settings (key, value) VALUES ('licenses.settings',
 '{"notifyEmails":[],"notifyInApp":true,"expiryDays":30,"unusedPercent":25,"inactiveDays":90,"webDiscovery":true}')
ON CONFLICT (key) DO NOTHING;

-- In-app notifications for one area: shown to users with that permission (and to administrators).
ALTER TABLE notifications ADD COLUMN permission text;

-- =====================================================================================================================
-- Permissions
-- =====================================================================================================================
INSERT INTO permissions (code, description) VALUES
 ('licenses.view',   'See Software License Management: products, licences, compliance, cloud licences and alerts'),
 ('licenses.manage', 'Add and change software products, licences, allocations and cloud licence connections'),
 ('webapps.view',    'See Web Apps & Accounts: web applications, accounts and usage'),
 ('webapps.manage',  'Add and change web applications and their accounts')
ON CONFLICT (code) DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code) SELECT 'admin', code FROM permissions ON CONFLICT DO NOTHING;
-- Roles that manage software (deployment) or assets get the new areas.
INSERT INTO role_permissions (role_id, permission_code)
SELECT DISTINCT rp.role_id, p.code FROM role_permissions rp CROSS JOIN (VALUES ('licenses.view'), ('licenses.manage'), ('webapps.view'), ('webapps.manage')) p(code)
 WHERE rp.permission_code IN ('deploy.manage', 'assets.manage')
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_code)
SELECT DISTINCT rp.role_id, p.code FROM role_permissions rp CROSS JOIN (VALUES ('licenses.view'), ('webapps.view')) p(code)
 WHERE rp.permission_code IN ('deploy.view', 'audit.view')
ON CONFLICT DO NOTHING;
