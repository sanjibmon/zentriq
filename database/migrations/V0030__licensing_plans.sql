-- V0030: Licensing plan (license server): IT devices, non-IT asset packs, cloud / virtual instances and remote support
-- technicians as licensed metrics; online activation state lives in system_settings ('licensing.online').
SET search_path = zentriq, public;

ALTER TABLE license_entitlements DROP CONSTRAINT IF EXISTS license_entitlements_workload_check;
ALTER TABLE license_entitlements ADD CONSTRAINT license_entitlements_workload_check
    CHECK (workload IN ('endpoint','server','network_device','other','devices','nonit_assets','cloud_instances','technicians'));

-- Address of the Zentriq license server for online activation (changeable under Licensing).
INSERT INTO system_settings (key, value) VALUES ('licensing.server_url', to_jsonb('https://license.zentriq.com'::text))
ON CONFLICT (key) DO NOTHING;
