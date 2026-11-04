// Software Deployment: approved software, application control, device groups, packages and deployments.

export type Mode = 'off' | 'audit' | 'block';
export type Wdac = 'off' | 'audit' | 'enforce';

export interface Settings {
  mode: Mode; message: string; allowRequests: boolean; autoRemove: boolean; protectRemoteAgent: boolean; revision: number; enabledAt: string | null; defaultMessage: string;
}

export interface Summary {
  settings: Settings;
  approvedRules: number; blockedRules: number; groups: number; packages: number; pendingRequests: number;
  blocked24h: number; audited24h: number; detected7d: number;
  agents: number; capableAgents: number; blocking: number; auditing: number; wdac: number; remoteProtected: number;
  deploymentsOpen: number; deploymentsFailed7d: number;
  catalog: { version: string; entries: number };
}

export interface CatalogEntry {
  key: string; name: string; publisher: string | null; category: string; platforms: string[]; namePatterns: string[]; publisherPatterns: string[];
  website: string | null; windowsSilentArgs: string | null; debPackage: string | null; rpmPackage: string | null; builtIn: boolean; id: string | null; notes: string | null;
  approved: boolean; blocked: boolean; devices: number;
}
export interface Catalog { version: string; categories: string[]; entries: CatalogEntry[] }

export interface Rule {
  id: string; action: 'allow' | 'block'; label: string; name: string | null; publisher: string | null; versionMin: string | null; versionMax: string | null;
  sha256: string | null; platform: string | null; catalogKey: string | null; groupId: string | null; group: string | null; assetId: string | null; device: string | null;
  source: string; note: string | null; expiresAt: string | null; createdAt: string; createdBy: string | null;
}

export interface InventoryRow {
  name: string; publisher: string | null; platform: 'windows' | 'linux'; devices: number; versions: string[]; lastInstalled: string | null;
  approved: boolean; outcome: string; reason: string;
}

export interface Group {
  id: string; name: string; description: string | null; departmentIds: string[]; locationIds: string[]; assetIds: string[]; platform: string | null;
  appMode: Mode | null; wdacMode: Wdac; createdAt: string; devices: number; rules: number; departments: string[]; locations: string[];
}

export interface AppEvent {
  id: number; at: string; kind: 'blocked' | 'audited' | 'detected' | 'removed' | 'remove-failed' | 'wdac'; name: string; publisher: string | null; version: string | null;
  sha256: string | null; path: string | null; user: string | null; platform: string | null; detail: string | null; assetId: string | null; device: string | null;
}

export interface AppRequest {
  id: string; createdAt: string; status: 'pending' | 'approved' | 'rejected'; name: string; publisher: string | null; version: string | null; sha256: string | null;
  path: string | null; platform: string | null; user: string | null; reason: string | null; assetId: string | null; device: string | null;
  decidedAt: string | null; decidedBy: string | null; decisionNote: string | null; ruleId: string | null;
}

export interface Package {
  id: string; name: string; version: string | null; publisher: string | null; platform: 'windows' | 'linux'; format: 'msi' | 'exe' | 'deb' | 'rpm'; fileName: string;
  size: number; sha256: string; installArgs: string | null; uninstallArgs: string | null; catalogKey: string | null; detectName: string | null; notes: string | null;
  active: boolean; createdAt: string; createdBy: string | null; jobs: number; installed: number;
}

export interface Job {
  id: string; createdAt: string; operation: 'install' | 'uninstall'; note: string | null; packageId: string; package: string; version: string | null; platform: string;
  createdBy: string | null; devices: number; skipped: number; waiting: number; running: number; succeeded: number; failed: number;
}

export interface JobDevice {
  assetId: string; hostname: string | null; status: string; detail: string | null; result: string | null; actionId: string | null;
  requestedAt: string | null; sentAt: string | null; completedAt: string | null;
}

export interface DeviceRow {
  agentId: string; assetId: string | null; hostname: string | null; platform: string; agentVersion: string | null; appControl: string | null; wdac: string | null;
  remoteProtection: string | null; policyCurrent: boolean; appControlCapable: boolean; deployCapable: boolean; online: boolean; lastCheckin: string | null;
  department: string | null; location: string | null;
}

export const MODE_LABEL: Record<string, string> = { off: 'Off', audit: 'Audit (report only)', block: 'Block' };
export const KIND_LABEL: Record<string, string> = {
  blocked: 'Blocked', audited: 'Installed (audit)', detected: 'Found installed', removed: 'Removed', 'remove-failed': 'Removal failed', wdac: 'WDAC',
};
export const platformLabel = (p: string | null | undefined) => (p === 'linux' ? 'Linux' : p === 'windows' ? 'Windows' : 'Windows & Linux');

export const errText = (e: unknown) => {
  const b = (e as { body?: { errors?: Record<string, string[]>; title?: string } }).body;
  return b?.errors ? Object.values(b.errors).flat().join(' ') : b?.title ?? (e instanceof Error ? e.message : String(e));
};
