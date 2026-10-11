-- Reference data: permissions, built-in roles and default system settings.
SET search_path = zentriq, public;

INSERT INTO permissions (code, description) VALUES
 ('assets.view',        'View assets & inventory'),
 ('assets.manage',      'Create, edit, retire and merge assets'),
 ('scans.run',          'Start and cancel scans'),
 ('scans.profiles',     'Manage scan profiles & schedules'),
 ('credentials.manage', 'Manage credential profiles'),
 ('patch.approve',      'Approve patches'),
 ('patch.deploy',       'Deploy patches'),
 ('deploy.run',         'Run software deployments'),
 ('remote.start',       'Start remote sessions'),
 ('remote.unattended',  'Unattended remote access'),
 ('reports.design',     'Design reports'),
 ('reports.export',     'Export reports'),
 ('dashboards.share',   'Share dashboards'),
 ('vuln.except',        'Approve risk exceptions'),
 ('jobs.view',          'View background jobs'),
 ('users.manage',       'Manage users, roles and sites'),
 ('licensing.manage',   'Import licenses and export activation requests'),
 ('backup.run',         'Run backups and maintenance operations'),
 ('settings.manage',    'Manage system settings'),
 ('audit.view',         'View audit logs');

INSERT INTO roles (id, name, description, built_in) VALUES
 ('admin',     'Administrator',      'Full control of the platform, including settings and credentials', true),
 ('discovery', 'Discovery Operator', 'Runs and schedules scans in approved scopes; manages scan profiles', true),
 ('patch',     'Patch Manager',      'Approves and deploys OS and application updates', true),
 ('remote',    'Support Technician', 'Starts attended remote sessions and views inventory', true),
 ('auditor',   'Read-only Auditor',  'Views inventory, reports and audit logs; cannot change anything', true);

INSERT INTO role_permissions (role_id, permission_code)
SELECT 'admin', code FROM permissions;

INSERT INTO role_permissions (role_id, permission_code) VALUES
 ('discovery','assets.view'),('discovery','scans.run'),('discovery','scans.profiles'),('discovery','reports.design'),
 ('discovery','reports.export'),('discovery','audit.view'),('discovery','jobs.view'),
 ('patch','assets.view'),('patch','patch.approve'),('patch','patch.deploy'),('patch','deploy.run'),
 ('patch','reports.design'),('patch','reports.export'),('patch','jobs.view'),
 ('remote','assets.view'),('remote','remote.start'),
 ('auditor','assets.view'),('auditor','reports.design'),('auditor','audit.view'),('auditor','jobs.view');

INSERT INTO system_settings (key, value) VALUES
 ('discovery.approved_scopes',   '["10.0.0.0/8","172.16.0.0/12","192.168.0.0/16"]'),
 ('discovery.max_targets',       '65536'),
 ('discovery.confirm_above',     '4096'),
 ('discovery.min_prefix',        '16'),
 ('discovery.default_concurrency','32'),
 ('discovery.max_concurrency',   '128'),
 ('discovery.probe_timeout_ms',  '1500'),
 ('security.mfa_required',       'false'),
 ('security.session_idle_minutes','30'),
 ('security.lockout_threshold',  '5'),
 ('security.lockout_minutes',    '15'),
 ('licensing.billable_seen_within_days', '30'),
 ('licensing.overage_grace_percent', '5'),
 ('trial.policy', '{"days":14,"limits":{"endpoint":250,"server":25,"network_device":50,"other":50},"modules":["*"]}'),
 ('clock.rollback_tolerance_minutes', '1440');
