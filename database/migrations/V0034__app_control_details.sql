-- Software Deployment: everything the device knows about a blocked or reported installer (file, version resource,
-- signature, MSI codes, download source, what started it) is kept with the event and with the approval request.
SET search_path = zentriq, public;

ALTER TABLE app_control_events ADD COLUMN details jsonb;
ALTER TABLE app_control_requests ADD COLUMN details jsonb;
