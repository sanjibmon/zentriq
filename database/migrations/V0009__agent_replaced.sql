-- V0009: an agent re-enrolled on the same device (e.g. reinstalled with a new enrollment code) replaces its earlier
-- registration, which otherwise stays "active" forever and shows up as a second, outdated agent.
SET search_path = zentriq, public;

ALTER TABLE agent_registrations DROP CONSTRAINT IF EXISTS agent_registrations_status_check;
ALTER TABLE agent_registrations ADD CONSTRAINT agent_registrations_status_check CHECK (status IN ('pending','active','revoked','replaced'));

-- Existing duplicates: the older registration of a device whose newer registration has reported since.
UPDATE agent_registrations o SET status = 'replaced', revoked_at = now()
  FROM agent_registrations n
 WHERE o.status = 'active' AND n.status = 'active' AND o.id <> n.id
   AND o.asset_id IS NOT NULL AND o.asset_id = n.asset_id
   AND lower(COALESCE(o.hostname, '')) = lower(COALESCE(n.hostname, ''))
   AND o.enrolled_at < n.enrolled_at
   AND n.last_checkin_at IS NOT NULL AND (o.last_checkin_at IS NULL OR n.last_checkin_at > o.last_checkin_at);
