-- V0022: Manufacturer, model and serial number entered by hand when the device does not report them, remembered by the
-- device's fixed identities (SMBIOS UUID, network adapter MAC addresses) so they come back after the agent or the
-- operating system is reinstalled, or the device is added again.
SET search_path = zentriq, public;

ALTER TABLE assets
    ADD COLUMN manual_manufacturer text CHECK (manual_manufacturer IS NULL OR length(manual_manufacturer) <= 200),
    ADD COLUMN manual_model        text CHECK (manual_model IS NULL OR length(manual_model) <= 200),
    ADD COLUMN manual_serial       text CHECK (manual_serial IS NULL OR length(manual_serial) <= 200);

-- What an administrator entered, independent of the asset record (survives deleting or re-creating the device).
CREATE TABLE hardware_profiles (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    manufacturer  text,
    model         text,
    serial_number text,
    asset_id      uuid REFERENCES assets(id) ON DELETE SET NULL,     -- the device it was entered on (latest)
    hostname      text,
    updated_at    timestamptz NOT NULL DEFAULT now(),
    updated_by    text
);
CREATE TABLE hardware_profile_identities (
    profile_id uuid NOT NULL REFERENCES hardware_profiles(id) ON DELETE CASCADE,
    kind       text NOT NULL CHECK (kind IN ('smbios_uuid','mac')),
    value      text NOT NULL,
    PRIMARY KEY (kind, value)
);
CREATE INDEX ix_hardware_profile_identities_profile ON hardware_profile_identities (profile_id);

-- A hardware value that is really a firmware placeholder ("To be filled by O.E.M.", "Default string", "0", "Not
-- Applicable", "System Serial Number", all zeros …) counts as not detected.
CREATE OR REPLACE FUNCTION hw_value(v text) RETURNS text
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN v IS NULL OR btrim(v) = '' THEN NULL
        WHEN lower(btrim(v)) IN ('to be filled by o.e.m.', 'to be filled by oem', 'default string', 'system serial number', 'system manufacturer',
                                 'system product name', 'system version', 'not specified', 'not applicable', 'none', 'n/a', 'na', 'oem', 'o.e.m.',
                                 'chassis serial number', 'base board serial number', 'invalid', 'unknown', 'empty', 'serial', '123456789', '0123456789',
                                 'xxxxxxxxxx', 'type1productconfigid', 'type1family', 'sku')
             THEN NULL
        WHEN btrim(v) ~ '^(.)\1*$' THEN NULL                       -- 0, 00000000, FFFFFFFF, ********
        ELSE btrim(v) END
$$;
