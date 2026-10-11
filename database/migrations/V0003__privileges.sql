-- Least-privilege grants for the application role.
-- The migrator calls zentriq.apply_app_grants(<app role>) after every migration run, so tables
-- added by later migrations are covered automatically.
SET search_path = zentriq, public;

CREATE OR REPLACE FUNCTION apply_app_grants(app_role text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = app_role) THEN
        RAISE EXCEPTION 'zentriq: application role % does not exist', app_role;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = app_role AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls)) THEN
        RAISE EXCEPTION 'zentriq: application role % must not be privileged', app_role;
    END IF;

    EXECUTE format('GRANT USAGE ON SCHEMA zentriq TO %I', app_role);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA zentriq TO %I', app_role);
    EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA zentriq TO %I', app_role);

    -- Append-only / insert-once tables.
    EXECUTE format('REVOKE UPDATE, DELETE ON zentriq.audit_events FROM %I', app_role);
    EXECUTE format('REVOKE UPDATE, DELETE ON zentriq.trial FROM %I', app_role);
    EXECUTE format('REVOKE UPDATE, DELETE ON zentriq.installation_history FROM %I', app_role);
    EXECUTE format('REVOKE DELETE ON zentriq.install_state FROM %I', app_role);
    EXECUTE format('REVOKE UPDATE, DELETE ON zentriq.workload_usage_snapshots FROM %I', app_role);

    -- Reference data and migration history are owned by the migrator only.
    EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON zentriq.schema_migrations FROM %I', app_role);
    EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON zentriq.permissions FROM %I', app_role);
    EXECUTE format('REVOKE INSERT, UPDATE ON zentriq.install_state FROM %I', app_role);
    -- The API may update only the columns it legitimately needs on install_state.
    EXECUTE format('GRANT UPDATE (setup_token_hash, setup_token_expires_at, initialized_at, initialized_by, last_seen_utc, clock_rollback_detected_at) ON zentriq.install_state TO %I', app_role);
END $$;
