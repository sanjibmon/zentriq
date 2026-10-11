-- Run as the APPLICATION role (zentriq_app) after migrations + grants.
-- Every block must succeed; failures raise and stop psql (ON_ERROR_STOP=1).
\set ON_ERROR_STOP 1
SET search_path = zentriq, public;

-- 1. install_state singleton exists and can be initialized exactly once.
SELECT 1 AS install_state_present FROM install_state WHERE id = 1 \gset
UPDATE install_state SET initialized_at = now() WHERE id = 1 AND initialized_at IS NULL;

DO $$
BEGIN
    BEGIN
        UPDATE zentriq.install_state SET initialized_at = now() + interval '1 day' WHERE id = 1;
        RAISE EXCEPTION 'TEST FAIL: initialized_at changed twice';
    EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'PASS initialized_at immutable';
    END;
END $$;

-- 2. last_seen_utc is a high-water mark.
UPDATE install_state SET last_seen_utc = now() + interval '1 hour' WHERE id = 1;
UPDATE install_state SET last_seen_utc = now() - interval '10 days' WHERE id = 1;
DO $$
BEGIN
    IF (SELECT last_seen_utc FROM zentriq.install_state) < now() + interval '50 minutes' THEN
        RAISE EXCEPTION 'TEST FAIL: last_seen_utc moved backwards';
    END IF;
    RAISE NOTICE 'PASS last_seen_utc monotonic';
END $$;

-- 3. Trial: insert once, never update or delete; second insert is a no-op via ON CONFLICT.
INSERT INTO trial (installation_id, started_at, expires_at, policy, integrity_mac)
SELECT installation_id, now(), now() + interval '14 days', '{}', 'mac' FROM install_state
ON CONFLICT (id) DO NOTHING;
INSERT INTO trial (installation_id, started_at, expires_at, policy, integrity_mac)
SELECT installation_id, now() + interval '30 days', now() + interval '44 days', '{}', 'mac2' FROM install_state
ON CONFLICT (id) DO NOTHING;
DO $$
BEGIN
    IF (SELECT integrity_mac FROM zentriq.trial) <> 'mac' THEN RAISE EXCEPTION 'TEST FAIL: trial reset'; END IF;
    BEGIN
        UPDATE zentriq.trial SET expires_at = expires_at + interval '100 days';
        RAISE EXCEPTION 'TEST FAIL: trial updated';
    EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'PASS trial cannot be extended';
    END;
    BEGIN
        DELETE FROM zentriq.trial;
        RAISE EXCEPTION 'TEST FAIL: trial deleted';
    EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'PASS trial cannot be deleted';
    END;
END $$;

-- 4. Audit log: hash chain populated, rows immutable.
INSERT INTO audit_events (actor_name, action, target, outcome) VALUES ('test','test.one','x','Success');
INSERT INTO audit_events (actor_name, action, target, outcome) VALUES ('test','test.two','y','Failure');
DO $$
DECLARE a record; b record;
BEGIN
    SELECT * INTO a FROM zentriq.audit_events ORDER BY id LIMIT 1;
    SELECT * INTO b FROM zentriq.audit_events ORDER BY id DESC LIMIT 1;
    IF b.prev_hash IS DISTINCT FROM a.hash OR a.hash IS NULL THEN RAISE EXCEPTION 'TEST FAIL: audit chain broken'; END IF;
    BEGIN
        UPDATE zentriq.audit_events SET outcome = 'Success';
        RAISE EXCEPTION 'TEST FAIL: audit updated';
    EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'PASS audit immutable';
    END;
    BEGIN
        DELETE FROM zentriq.audit_events;
        RAISE EXCEPTION 'TEST FAIL: audit deleted';
    EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'PASS audit append-only';
    END;
END $$;

-- 5. App role cannot touch migration history or reference permissions, nor create objects.
DO $$
BEGIN
    BEGIN
        DELETE FROM zentriq.schema_migrations;
        RAISE EXCEPTION 'TEST FAIL: schema_migrations writable';
    EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'PASS schema_migrations protected';
    END;
    BEGIN
        EXECUTE 'CREATE TABLE zentriq.evil(x int)';
        RAISE EXCEPTION 'TEST FAIL: app role can create tables';
    EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'PASS app role cannot create objects';
    END;
END $$;

-- 6. Duplicate assets must reference the surviving asset.
DO $$
BEGIN
    BEGIN
        INSERT INTO zentriq.assets (hostname, status) VALUES ('dup', 'duplicate');
        RAISE EXCEPTION 'TEST FAIL: duplicate without merged_into accepted';
    EXCEPTION WHEN check_violation THEN RAISE NOTICE 'PASS duplicate requires merged_into';
    END;
END $$;

-- 7. Durable job dequeue with SKIP LOCKED works for the app role.
INSERT INTO jobs (job_type, idempotency_key) VALUES ('system.selftest', 'assert-1');
INSERT INTO jobs (job_type, idempotency_key) VALUES ('system.selftest', 'assert-1') ON CONFLICT (idempotency_key) DO NOTHING;
WITH next AS (
    SELECT id FROM jobs WHERE status = 'queued' AND run_after <= now()
    ORDER BY priority, run_after FOR UPDATE SKIP LOCKED LIMIT 1)
UPDATE jobs j SET status = 'running', attempts = attempts + 1, lease_owner = 'test', lease_expires_at = now() + interval '1 minute', started_at = now()
FROM next WHERE j.id = next.id;
DO $$
BEGIN
    IF (SELECT count(*) FROM zentriq.jobs WHERE idempotency_key = 'assert-1') <> 1 THEN RAISE EXCEPTION 'TEST FAIL: idempotency'; END IF;
    IF (SELECT status FROM zentriq.jobs WHERE idempotency_key = 'assert-1') <> 'running' THEN RAISE EXCEPTION 'TEST FAIL: dequeue'; END IF;
    RAISE NOTICE 'PASS job idempotency + dequeue';
END $$;

-- 8. Only one active commercial license at a time.
INSERT INTO license_records (license_id, schema_version, key_id, customer_name, installation_id, issued_at, not_before, expires_at, modules, raw_document)
VALUES ('L1', 1, 'k', 'c', gen_random_uuid(), now(), now(), now() + interval '1 year', '{*}', '{}');
DO $$
BEGIN
    BEGIN
        INSERT INTO zentriq.license_records (license_id, schema_version, key_id, customer_name, installation_id, issued_at, not_before, expires_at, modules, raw_document)
        VALUES ('L2', 1, 'k', 'c', gen_random_uuid(), now(), now(), now() + interval '1 year', '{*}', '{}');
        RAISE EXCEPTION 'TEST FAIL: two active licenses';
    EXCEPTION WHEN unique_violation THEN RAISE NOTICE 'PASS single active license';
    END;
END $$;

SELECT 'ALL DATABASE ASSERTIONS PASSED' AS result;
