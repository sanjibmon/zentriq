-- V0045 (build 138): console sessions end after this many minutes without activity (typing, clicking, scrolling);
-- 0 = never (sessions still end 12 hours after signing in). Settings › Security.
INSERT INTO system_settings (key, value) VALUES ('security.idle_signout_minutes', '30')
ON CONFLICT (key) DO NOTHING;
