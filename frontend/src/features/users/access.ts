// What each permission opens, grouped by console area (used by the role editor and the role list).

export interface AreaDef {
  area: string;
  hint: string;
  view?: string;
  manage?: string;
  viewLabel?: string;
  manageLabel?: string;
  extras?: [string, string][];
}

export const AREAS: AreaDef[] = [
  { area: 'Dashboard', hint: 'Widgets show only the areas below that the role can see', view: 'dashboard.view' },
  { area: 'IT asset inventory', hint: 'Devices, hardware, software, network inventory, agents, discovery results', view: 'assets.view', manage: 'assets.manage',
    extras: [['assets.control', 'Start / stop services'], ['software.uninstall', 'Uninstall software'], ['agents.manage', 'Agent enrollment and revoke'], ['directory.manage', 'Active Directory sync']] },
  { area: 'Discovery & scans', hint: 'Needs IT asset inventory › View', extras: [['scans.run', 'Run scans'], ['scans.profiles', 'Scan profiles'], ['credentials.manage', 'Scan credentials']] },
  { area: 'Activity log', hint: 'Service actions, uninstalls and agent updates on devices', view: 'activity.view' },
  { area: 'Non-IT asset inventory', hint: 'Furniture, appliances, vehicles, tools and other items', view: 'nonit.view', manage: 'nonit.manage' },
  { area: 'IT accessories', hint: 'Mouse, keyboard, headset … stock, issue and return', view: 'accessories.view', manage: 'accessories.manage' },
  { area: 'Asset lifecycle', hint: 'Stages, assignment, people, contracts, depreciation', view: 'lifecycle.view', manage: 'lifecycle.manage' },
  { area: 'Cloud & infrastructure', hint: 'Azure, AWS, GCP, VMware, Hyper-V and other platforms', view: 'cloud.view', manage: 'cloud.manage' },
  { area: 'Remote support', hint: 'Remote sessions on devices', view: 'remote.start', viewLabel: 'Start sessions', manage: 'remote.manage', manageLabel: 'Configure',
    extras: [['remote.unattended', 'Full remote rights']] },
  { area: 'Vulnerabilities', hint: 'Findings, feeds and remediation', view: 'vuln.view', manage: 'vuln.manage', extras: [['vuln.except', 'Approve exceptions']] },
  { area: 'Security configuration', hint: 'Windows configuration checks and baselines', view: 'security.view', manage: 'security.manage' },
  { area: 'Patch & deployment', hint: 'Coming modules', extras: [['patch.approve', 'Approve patches'], ['patch.deploy', 'Deploy patches'], ['deploy.run', 'Deploy software']] },
  { area: 'Reports & Analytics', hint: 'Reports run only on the areas the role can see', view: 'reports.view', manage: 'reports.design', manageLabel: 'Design',
    extras: [['reports.export', 'Export and e-mail'], ['dashboards.share', 'Share reports']] },
  { area: 'Integrations & automation', hint: 'Webhooks, alert rules, API tokens', manage: 'integrations.manage' },
  { area: 'Administration', hint: 'Platform settings', extras: [['users.manage', 'Users, roles and groups'], ['settings.manage', 'System settings'], ['licensing.manage', 'Licensing'],
    ['backup.run', 'Backup & restore'], ['audit.view', 'Audit log'], ['jobs.view', 'Job status']] },
];

/** "Non-IT asset inventory: view, change · Reports & Analytics: view" for the role list. */
export function describe(perms: string[]): string[] {
  const has = new Set(perms);
  const out: string[] = [];
  for (const a of AREAS) {
    const bits: string[] = [];
    if (a.view && has.has(a.view)) bits.push((a.viewLabel ?? 'view').toLowerCase());
    if (a.manage && has.has(a.manage)) bits.push((a.manageLabel ?? 'change').toLowerCase());
    for (const [p, l] of a.extras ?? []) if (has.has(p)) bits.push(l.toLowerCase());
    if (bits.length) out.push(`${a.area}: ${bits.join(', ')}`);
  }
  return out;
}
