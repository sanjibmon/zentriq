import type { IconName } from '../components/icons';

export interface NavItem {
  id: string;
  label: string;
  icon: IconName;
  path: string;
  /** Implemented in this release; others show the module status page. */
  ready: boolean;
  permission?: string;
  /** Administration pages (licensing, users, settings, audit): always available, whatever is installed or licensed. */
  admin?: boolean;
}

// Same groups, labels and icons as the Zentriq prototype navigation.
export const NAV: [string, NavItem[]][] = [
  ['Overview', [{ id: 'dashboard', label: 'Dashboard', icon: 'dashboard', path: '/', ready: true, permission: 'dashboard.view' }]],
  ['Discover & inventory', [
    { id: 'discovery', label: 'Discovery & Scans', icon: 'radar', path: '/discovery', ready: true, permission: 'assets.view' },
    { id: 'assets', label: 'IT Asset Inventory', icon: 'box', path: '/assets', ready: true, permission: 'assets.view' },
    { id: 'nonit', label: 'Non-IT Asset Inventory', icon: 'chair', path: '/non-it', ready: true, permission: 'nonit.view' },
    { id: 'hardware', label: 'Hardware Inventory', icon: 'cpu', path: '/hardware', ready: true, permission: 'assets.view' },
    { id: 'accessories', label: 'IT Accessories', icon: 'headset', path: '/accessories', ready: true, permission: 'accessories.view' },
    { id: 'software', label: 'Software Inventory', icon: 'apps', path: '/software', ready: true, permission: 'assets.view' },
    { id: 'network', label: 'Network Inventory', icon: 'network', path: '/network', ready: true, permission: 'assets.view' },
    { id: 'agents', label: 'Agents', icon: 'host', path: '/agents', ready: true, permission: 'assets.view' },
    { id: 'activity', label: 'Activity Log', icon: 'activity', path: '/activity', ready: true, permission: 'activity.view' },
  ]],
  ['Remote support', [
    { id: 'remote', label: 'Remote Support', icon: 'remote', path: '/remote', ready: true, permission: 'remote.start' },
  ]],
  ['Infrastructure', [
    { id: 'cloud', label: 'Cloud & Infrastructure', icon: 'cloud', path: '/cloud', ready: true, permission: 'cloud.view' },
    { id: 'lifecycle', label: 'Asset Lifecycle', icon: 'cycle', path: '/lifecycle', ready: true, permission: 'lifecycle.view' },
  ]],
  ['Secure & remediate', [
    { id: 'patch', label: 'Patch Management', icon: 'patch', path: '/modules/patch', ready: false },
    { id: 'vuln', label: 'Vulnerability Management', icon: 'bug', path: '/vulnerabilities', ready: true, permission: 'vuln.view' },
    { id: 'config', label: 'Security Configuration', icon: 'sliders', path: '/security', ready: true, permission: 'security.view' },
    { id: 'deploy', label: 'Software Deployment', icon: 'package', path: '/modules/deploy', ready: false },
    { id: 'licenses', label: 'Software Licenses', icon: 'key', path: '/modules/licenses', ready: false },
    { id: 'webapps', label: 'Web Apps & Accounts', icon: 'globe', path: '/modules/webapps', ready: false },
  ]],
  ['Insight & admin', [
    { id: 'reports', label: 'Reports & Analytics', icon: 'report', path: '/reports', ready: true, permission: 'reports.view' },
    { id: 'integrations', label: 'Integrations & Automation', icon: 'plug', path: '/integrations', ready: true, permission: 'integrations.manage' },
    { id: 'licensing', label: 'Licensing', icon: 'key', path: '/licensing', ready: true, permission: 'licensing.manage', admin: true },
    { id: 'audit', label: 'Audit Logs', icon: 'log', path: '/audit', ready: true, permission: 'audit.view', admin: true },
    { id: 'users', label: 'Users, Roles & Groups', icon: 'users', path: '/users', ready: true, permission: 'users.manage', admin: true },
    { id: 'backup', label: 'Backup & Restore', icon: 'archive', path: '/backup', ready: true, permission: 'backup.run', admin: true },
    { id: 'settings', label: 'System Settings', icon: 'gear', path: '/settings', ready: true, permission: 'settings.manage', admin: true },
  ]],
];

export const ALL_NAV = NAV.flatMap(([, items]) => items);

/** Product section of each navigation group (installed by Standard / Advanced setup, enabled by the edition). */
export const SECTION_OF_GROUP: Record<string, string> = {
  'Overview': 'overview', 'Discover & inventory': 'discover', 'Remote support': 'remote',
  'Infrastructure': 'infrastructure', 'Secure & remediate': 'secure', 'Insight & admin': 'insight',
};

/** The navigation item and section a path belongs to (longest matching path wins). */
export function navOfPath(path: string): { item: NavItem; section: string } | null {
  let best: { item: NavItem; section: string } | null = null;
  for (const [group, items] of NAV) {
    for (const item of items) {
      const hit = item.path === '/' ? path === '/' : path === item.path || path.startsWith(item.path + '/') || (item.id === 'reports' && path.startsWith('/reports'));
      if (hit && (!best || item.path.length > best.item.path.length)) best = { item, section: SECTION_OF_GROUP[group] ?? '' };
    }
  }
  return best;
}
