-- V0043 (build 136): user access rules, MFA required per role, directory groups mapped to Zentriq groups, data scope
-- per role and group (enforced by PostgreSQL row-level security for scoped console users).

-- ---- Access rules per user --------------------------------------------------------------------------------------------
-- access_expires_at: the account stops signing in after this time (sessions end at their next request).
-- signin_hours: {"days":[1,2,3,4,5],"from":"08:00","to":"20:00"} in the server's time zone (0 = Sunday); null = any time.
-- allowed_networks: IPv4/IPv6 addresses or CIDR ranges the user may sign in from; empty = anywhere.
ALTER TABLE users
    ADD COLUMN access_expires_at timestamptz,
    ADD COLUMN signin_hours      jsonb,
    ADD COLUMN allowed_networks  text[] NOT NULL DEFAULT '{}';

-- Members of a role with mfa_required must use an authenticator app (like the organisation-wide setting, per role).
ALTER TABLE roles ADD COLUMN mfa_required boolean NOT NULL DEFAULT false;

-- ---- Data scope ----------------------------------------------------------------------------------------------------------
-- Empty arrays: no restriction. A grant through a Zentriq group uses the group's scope when it has one, otherwise the
-- role's. A user with at least one unrestricted grant sees every device.
ALTER TABLE roles
    ADD COLUMN scope_location_ids     uuid[] NOT NULL DEFAULT '{}',
    ADD COLUMN scope_department_ids   uuid[] NOT NULL DEFAULT '{}',
    ADD COLUMN scope_device_group_ids uuid[] NOT NULL DEFAULT '{}';
ALTER TABLE user_groups
    ADD COLUMN scope_location_ids     uuid[] NOT NULL DEFAULT '{}',
    ADD COLUMN scope_department_ids   uuid[] NOT NULL DEFAULT '{}',
    ADD COLUMN scope_device_group_ids uuid[] NOT NULL DEFAULT '{}';

-- ---- Directory groups mapped to Zentriq groups ---------------------------------------------------------------------------
ALTER TABLE auth_group_mappings ALTER COLUMN role_id DROP NOT NULL;
ALTER TABLE auth_group_mappings ADD COLUMN group_id uuid REFERENCES user_groups(id) ON DELETE CASCADE;
ALTER TABLE auth_group_mappings ADD CONSTRAINT auth_group_mappings_target_check CHECK ((role_id IS NULL) <> (group_id IS NULL));
CREATE UNIQUE INDEX ux_auth_group_mappings_group ON auth_group_mappings (provider, lower(group_ref), group_id) WHERE group_id IS NOT NULL;

-- Zentriq groups a directory user is in through mapped directory groups (replaced at each sign-in).
CREATE TABLE user_directory_groups (
    user_id  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id uuid NOT NULL REFERENCES user_groups(id) ON DELETE CASCADE,
    provider text NOT NULL,
    PRIMARY KEY (user_id, group_id)
);

-- Every role a user has: directly, through a Zentriq group (member, or through a mapped directory group), or from a
-- directory group mapped to a role.
CREATE OR REPLACE VIEW user_role_grants AS
    SELECT ur.user_id, ur.role_id, NULL::uuid AS group_id FROM user_roles ur
    UNION ALL
    SELECT m.user_id, gr.role_id, m.group_id FROM user_group_members m JOIN user_group_roles gr ON gr.group_id = m.group_id
    UNION ALL
    SELECT d.user_id, d.role_id, NULL::uuid FROM user_directory_roles d
    UNION ALL
    SELECT dg.user_id, gr.role_id, dg.group_id FROM user_directory_groups dg JOIN user_group_roles gr ON gr.group_id = dg.group_id;

-- ---- Row-level security for scoped users ---------------------------------------------------------------------------------
-- The API opens connections for a scoped user as the application role with "-c role=<app role>_scoped
-- -c zentriq.scope_user=<user id>". Policies for the application role itself are simply "true", so every other connection
-- (agents, the worker, unrestricted users) is not slowed down or limited in any way; only the "_scoped" role is filtered.
CREATE OR REPLACE FUNCTION scope_user() RETURNS uuid LANGUAGE sql STABLE AS
$$ SELECT NULLIF(current_setting('zentriq.scope_user', true), '')::uuid $$;

-- Whether a user is limited by a data scope: has roles, and none of them is granted without a scope.
CREATE OR REPLACE FUNCTION user_is_scoped(p_user uuid) RETURNS boolean LANGUAGE sql STABLE AS
$$
    SELECT EXISTS (SELECT 1 FROM zentriq.user_role_grants WHERE user_id = p_user)
       AND NOT EXISTS (
            SELECT 1 FROM zentriq.user_role_grants x
              JOIN zentriq.roles r ON r.id = x.role_id
              LEFT JOIN zentriq.user_groups ug ON ug.id = x.group_id
             WHERE x.user_id = p_user
               AND CASE WHEN ug.id IS NOT NULL AND cardinality(ug.scope_location_ids) + cardinality(ug.scope_department_ids) + cardinality(ug.scope_device_group_ids) > 0
                        THEN 1
                        ELSE cardinality(r.scope_location_ids) + cardinality(r.scope_department_ids) + cardinality(r.scope_device_group_ids) END = 0)
$$;

-- Devices the scoped user may see. SECURITY DEFINER: runs as the schema owner, which row-level security does not
-- apply to (no recursion through the assets policy).
CREATE OR REPLACE FUNCTION scope_asset_ids() RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = zentriq, pg_temp AS
$$
    WITH g AS (
        SELECT CASE WHEN ug.id IS NOT NULL AND cardinality(ug.scope_location_ids) + cardinality(ug.scope_department_ids) + cardinality(ug.scope_device_group_ids) > 0
                    THEN ug.scope_location_ids ELSE r.scope_location_ids END AS locs,
               CASE WHEN ug.id IS NOT NULL AND cardinality(ug.scope_location_ids) + cardinality(ug.scope_department_ids) + cardinality(ug.scope_device_group_ids) > 0
                    THEN ug.scope_department_ids ELSE r.scope_department_ids END AS deps,
               CASE WHEN ug.id IS NOT NULL AND cardinality(ug.scope_location_ids) + cardinality(ug.scope_department_ids) + cardinality(ug.scope_device_group_ids) > 0
                    THEN ug.scope_device_group_ids ELSE r.scope_device_group_ids END AS grps
          FROM user_role_grants x
          JOIN roles r ON r.id = x.role_id
          LEFT JOIN user_groups ug ON ug.id = x.group_id
         WHERE x.user_id = scope_user()
    )
    SELECT a.id FROM assets a
     WHERE EXISTS (
        SELECT 1 FROM g
         WHERE cardinality(g.locs) + cardinality(g.deps) + cardinality(g.grps) = 0
            OR a.location_id = ANY(g.locs)
            OR a.department_id = ANY(g.deps)
            OR EXISTS (SELECT 1 FROM device_groups dg
                        WHERE dg.id = ANY(g.grps)
                          AND (a.id = ANY(dg.asset_ids) OR a.department_id = ANY(dg.department_ids) OR a.location_id = ANY(dg.location_ids))
                          AND (dg.platform IS NULL OR dg.platform = a.os_family)))
$$;

-- Whether a device with these values is in the scoped user's scope (checks a changed row: moving a device out of the
-- scope is refused).
CREATE OR REPLACE FUNCTION scope_allows(p_id uuid, p_location uuid, p_department uuid, p_os text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = zentriq, pg_temp AS
$$
    SELECT EXISTS (
        SELECT 1
          FROM user_role_grants x
          JOIN roles r ON r.id = x.role_id
          LEFT JOIN user_groups ug ON ug.id = x.group_id
          CROSS JOIN LATERAL (SELECT ug.id IS NOT NULL AND cardinality(ug.scope_location_ids) + cardinality(ug.scope_department_ids) + cardinality(ug.scope_device_group_ids) > 0 AS own) o
          CROSS JOIN LATERAL (SELECT CASE WHEN o.own THEN ug.scope_location_ids ELSE r.scope_location_ids END AS locs,
                                     CASE WHEN o.own THEN ug.scope_department_ids ELSE r.scope_department_ids END AS deps,
                                     CASE WHEN o.own THEN ug.scope_device_group_ids ELSE r.scope_device_group_ids END AS grps) g
         WHERE x.user_id = scope_user()
           AND (cardinality(g.locs) + cardinality(g.deps) + cardinality(g.grps) = 0
                OR p_location = ANY(g.locs) OR p_department = ANY(g.deps)
                OR EXISTS (SELECT 1 FROM device_groups dg WHERE dg.id = ANY(g.grps)
                              AND (p_id = ANY(dg.asset_ids) OR p_department = ANY(dg.department_ids) OR p_location = ANY(dg.location_ids))
                              AND (dg.platform IS NULL OR dg.platform = p_os))))
$$;

-- (Re)creates the row-level security of the devices and of every table that belongs to a device (asset_id). Called by
-- the migrator after each migration run, with the application role, so tables added later are covered too. Rows without
-- a device (asset_id null) stay visible to scoped users; new rows may always be inserted.
CREATE OR REPLACE FUNCTION apply_scope_policies(app_role text) RETURNS void
LANGUAGE plpgsql AS $fn$
DECLARE
    t record;
    scoped text := app_role || '_scoped';
    has_scoped boolean := EXISTS (SELECT 1 FROM pg_roles WHERE rolname = app_role || '_scoped');
    keycol text;
BEGIN
    FOR t IN
        SELECT 'assets'::text AS table_name, 'id'::text AS col
        UNION ALL
        SELECT c.table_name::text, 'asset_id' FROM information_schema.columns c
          JOIN information_schema.tables tb ON tb.table_schema = c.table_schema AND tb.table_name = c.table_name AND tb.table_type = 'BASE TABLE'
         WHERE c.table_schema = 'zentriq' AND c.column_name = 'asset_id' AND c.data_type = 'uuid'
    LOOP
        keycol := CASE WHEN t.col = 'id' THEN 'id' ELSE 'asset_id IS NULL OR asset_id' END;
        EXECUTE format('ALTER TABLE zentriq.%I ENABLE ROW LEVEL SECURITY', t.table_name);
        EXECUTE format('DROP POLICY IF EXISTS app_all ON zentriq.%I', t.table_name);
        EXECUTE format('CREATE POLICY app_all ON zentriq.%I TO %I USING (true) WITH CHECK (true)', t.table_name, app_role);
        EXECUTE format('DROP POLICY IF EXISTS scope_select ON zentriq.%I', t.table_name);
        EXECUTE format('DROP POLICY IF EXISTS scope_update ON zentriq.%I', t.table_name);
        EXECUTE format('DROP POLICY IF EXISTS scope_delete ON zentriq.%I', t.table_name);
        EXECUTE format('DROP POLICY IF EXISTS scope_insert ON zentriq.%I', t.table_name);
        IF has_scoped THEN
            EXECUTE format('CREATE POLICY scope_select ON zentriq.%I FOR SELECT TO %I USING (%s IN (SELECT zentriq.scope_asset_ids()))', t.table_name, scoped, keycol);
            IF t.col = 'id' THEN
                EXECUTE format('CREATE POLICY scope_update ON zentriq.%I FOR UPDATE TO %I USING (id IN (SELECT zentriq.scope_asset_ids())) WITH CHECK (zentriq.scope_allows(id, location_id, department_id, os_family))', t.table_name, scoped);
            ELSE
                EXECUTE format('CREATE POLICY scope_update ON zentriq.%I FOR UPDATE TO %I USING (%s IN (SELECT zentriq.scope_asset_ids()))', t.table_name, scoped, keycol);
            END IF;
            EXECUTE format('CREATE POLICY scope_delete ON zentriq.%I FOR DELETE TO %I USING (%s IN (SELECT zentriq.scope_asset_ids()))', t.table_name, scoped, keycol);
            EXECUTE format('CREATE POLICY scope_insert ON zentriq.%I FOR INSERT TO %I WITH CHECK (true)', t.table_name, scoped);
        END IF;
    END LOOP;
    IF has_scoped THEN
        PERFORM zentriq.apply_app_grants(scoped);
    END IF;
    -- Views run with their owner's rights unless security_invoker (PostgreSQL 15+) is set.
    IF current_setting('server_version_num')::int >= 150000 THEN
        EXECUTE 'ALTER VIEW zentriq.v_asset_lifecycle SET (security_invoker = true)';
    END IF;
END $fn$;
