-- Zentriq Agent for macOS (build 144): agents report macos-x64 (Intel) or macos-arm64 (Apple silicon).
SET search_path = zentriq, public;

DO $$
DECLARE c text;
BEGIN
    FOR c IN SELECT conname FROM pg_constraint
             WHERE conrelid = 'zentriq.agent_registrations'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%platform%'
    LOOP
        EXECUTE format('ALTER TABLE agent_registrations DROP CONSTRAINT %I', c);
    END LOOP;
END $$;

ALTER TABLE agent_registrations ADD CONSTRAINT agent_registrations_platform_check
    CHECK (platform IN ('windows-x64', 'linux-x64', 'linux-arm64', 'macos-x64', 'macos-arm64'));
