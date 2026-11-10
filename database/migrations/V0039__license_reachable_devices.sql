-- Build 119: devices count for the license only while they are reachable.
-- A device without the Zentriq Agent that did not answer the last scan of its address is marked; it counts again as
-- soon as it answers (last_seen_at moves past the mark) or an agent reports for it.
ALTER TABLE zentriq.assets ADD COLUMN IF NOT EXISTS unreachable_since timestamptz;
COMMENT ON COLUMN zentriq.assets.unreachable_since IS 'Set when a scan finds the address of a device without an agent unreachable; the device is not counted for the license while unreachable_since > last_seen_at.';
