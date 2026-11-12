-- V0042 (build 130): licence type "retail"; product keys found on devices.

ALTER TABLE sam_licenses DROP CONSTRAINT IF EXISTS sam_licenses_license_type_check;
ALTER TABLE sam_licenses ADD CONSTRAINT sam_licenses_license_type_check
    CHECK (license_type IN ('perpetual','subscription','volume','oem','retail','enterprise','trial','freeware','open_source'));

-- Product keys read from a device with its inventory (Windows, Office, other software that keeps its key in the
-- registry). The key itself is stored encrypted only (installation credential key); key_hint is its last characters.
-- Never part of the stored inventory document. Shown on request to people who may manage licences; each view is audited.
CREATE TABLE asset_product_keys (
    id            uuid PRIMARY KEY,
    asset_id      uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    product       text NOT NULL,
    publisher     text,
    application   text NOT NULL DEFAULT 'other' CHECK (application IN ('windows','office','firmware','other')),
    source        text NOT NULL,                 -- firmware | windows | registry
    key_hint      text NOT NULL,                 -- last five characters
    key_protected text NOT NULL,
    note          text,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_asset_product_keys_asset ON asset_product_keys (asset_id);
