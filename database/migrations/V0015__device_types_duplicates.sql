-- V0015: device types (detected or set by an administrator), what identified a device without credentials,
-- and duplicate clean-up.
SET search_path = zentriq, public;

ALTER TABLE assets
    ADD COLUMN device_type        text,                                   -- DeviceTypes keys: desktop, laptop, switch, firewall, printer, mfp, …
    ADD COLUMN device_type_manual boolean NOT NULL DEFAULT false,         -- set by an administrator: never overwritten by discovery
    ADD COLUMN manufacturer       text,                                   -- when no hardware inventory exists (web interface, SNMP vendor)
    ADD COLUMN model              text,
    ADD COLUMN identified_by      jsonb;                                  -- ["web interface mentions \"laserjet\"", "printing port open (9100)"]

-- Existing devices: take the type from the latest inventory (chassis), so filters and labels work at once.
UPDATE assets a SET device_type = CASE d.document #>> '{device,chassis}'
        WHEN 'network' THEN 'network' WHEN 'printer' THEN 'printer' WHEN 'laptop' THEN 'laptop' WHEN 'virtual' THEN 'virtual'
        WHEN 'desktop' THEN 'desktop' WHEN 'server' THEN 'server' ELSE NULL END
  FROM asset_inventory_documents d
 WHERE d.asset_id = a.id AND a.device_type IS NULL;

CREATE INDEX ix_assets_device_type ON assets (device_type) WHERE status = 'active';
CREATE INDEX ix_assets_serial ON assets (upper(serial_number)) WHERE status = 'active' AND serial_number IS NOT NULL;
CREATE INDEX ix_assets_mac ON assets (mac_address) WHERE status = 'active' AND mac_address IS NOT NULL;

-- Duplicate devices are merged automatically (the newest information is kept); administrators can turn this off.
INSERT INTO system_settings (key, value) VALUES ('inventory.duplicates', '{"auto":true}'::jsonb)
    ON CONFLICT (key) DO NOTHING;
