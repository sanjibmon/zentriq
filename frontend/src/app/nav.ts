import type { IconName } from '../components/icons';

export interface NavItem {
  id: string;
  label: string;
  icon: IconName;
  path: string;
  /** Implemented in this release; others show the module status page. */
  ready: boolean;
  permission?: string;
}

// Same groups, labels and icons as the Zentriq prototype navigation.
export const NAV: [string, NavItem[]][] = [
  ['Overview', [{ id: 'dashboard', label: 'Dashboard', icon: 'dashboard', path: '/', ready: true }]],
  ['Discover & inventory', [
    { id: 'discovery', label: 'Discovery & Scans', icon: 'radar', path: '/discovery', ready: true },
    { id: 'assets', label: 'Asset Inventory', icon: 'box', path: '/assets', ready: true },
    { id: 'hardware', label: 'Hardware Inventory', icon: 'cpu', path: '/hardware', ready: true },
    { id: 'software', label: 'Software Inventory', icon: 'apps', path: '/software', ready: true },
    { id: 'agents', label: 'Agents', icon: 'host', path: '/agents', ready: true },
    { id: 'activity', label: 'Activity Log', icon: 'activity', path: '/activity', ready: true },
    { id: 'licenses', label: 'Software Licenses', icon: 'key', path: '/modules/licenses', ready: false },
    { id: 'webapps', label: 'Web Apps & Accounts', icon: 'globe', path: '/modules/webapps', ready: false },
  ]],
  ['Secure & remediate', [
    { id: 'patch', label: 'Patch Management', icon: 'patch', path: '/modules/patch', ready: false },
    { id: 'vuln', label: 'Vulnerability Management', icon: 'bug', path: '/modules/vuln', ready: false },
    { id: 'config', label: 'Security Configuration', icon: 'sliders', path: '/modules/config', ready: false },
    { id: 'deploy', label: 'Software Deployment', icon: 'package', path: '/modules/deploy', ready: false },
    { id: 'remote', label: 'Remote Support', icon: 'remote', path: '/modules/remote', ready: false },
  ]],
  ['Infrastructure', [
    { id: 'cloud', label: 'Cloud & Infrastructure', icon: 'cloud', path: '/modules/cloud', ready: false },
    { id: 'lifecycle', label: 'Asset Lifecycle', icon: 'cycle', path: '/modules/lifecycle', ready: false },
  ]],
  ['Insight & admin', [
    { id: 'reports', label: 'Reports & Analytics', icon: 'report', path: '/modules/reports', ready: false },
    { id: 'integrations', label: 'Integrations & Automation', icon: 'plug', path: '/modules/integrations', ready: false },
    { id: 'licensing', label: 'Licensing', icon: 'key', path: '/licensing', ready: true },
    { id: 'audit', label: 'Audit Logs', icon: 'log', path: '/audit', ready: true, permission: 'audit.view' },
    { id: 'users', label: 'Users, Roles & Sites', icon: 'users', path: '/users', ready: true, permission: 'users.manage' },
    { id: 'settings', label: 'System Settings', icon: 'gear', path: '/settings', ready: true, permission: 'settings.manage' },
  ]],
];

export const ALL_NAV = NAV.flatMap(([, items]) => items);
