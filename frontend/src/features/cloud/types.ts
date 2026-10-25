import type { Tone } from '../../components/ui';

export type Provider = 'azure' | 'aws' | 'vmware' | 'hyperv';

export const PROVIDERS: { id: Provider; label: string; short: string; kind: 'cloud' | 'virtual' }[] = [
  { id: 'azure', label: 'Microsoft Azure', short: 'Azure', kind: 'cloud' },
  { id: 'aws', label: 'Amazon Web Services', short: 'AWS', kind: 'cloud' },
  { id: 'vmware', label: 'VMware vCenter', short: 'vCenter', kind: 'virtual' },
  { id: 'hyperv', label: 'Microsoft Hyper-V', short: 'Hyper-V', kind: 'virtual' },
];
export const providerLabel = (p: string) => PROVIDERS.find((x) => x.id === p)?.label ?? p;
export const providerShort = (p: string) => PROVIDERS.find((x) => x.id === p)?.short ?? p;

export const KIND_LABEL: Record<string, string> = {
  vm: 'Virtual machine', host: 'Host', cluster: 'Cluster', datastore: 'Datastore', disk: 'Disk', public_ip: 'Public IP', network: 'Network',
  security_group: 'Security group', load_balancer: 'Load balancer', storage: 'Storage', database: 'Database', kubernetes: 'Kubernetes cluster', function: 'Function',
};
export const kindLabel = (k: string) => KIND_LABEL[k] ?? k;

export const SEVERITIES = ['critical', 'high', 'medium', 'low', 'info'] as const;
export const SEV_TONE: Record<string, Tone> = { critical: 'crit', high: 'crit', medium: 'warn', low: 'info', info: '' };

const RUNNING = ['running', 'poweredon', 'powered_on', 'active', 'available', 'succeeded', 'ready', 'connected', 'powered on'];
const STOPPED = ['stopped', 'deallocated', 'off', 'poweredoff', 'powered_off', 'saved', 'suspended', 'paused', 'unattached', 'unassociated'];
export function stateTone(s: string | null | undefined): Tone {
  const v = (s ?? '').toLowerCase();
  if (RUNNING.includes(v)) return 'ok';
  if (STOPPED.includes(v)) return 'warn';
  if (v.includes('fail') || v.includes('error') || v === 'disconnected' || v === 'not_responding') return 'crit';
  return '';
}
export const stateLabel = (s: string | null | undefined) => (s ? s.replace(/_/g, ' ').replace(/^\w/, (c) => c.toUpperCase()) : '—');

export interface CloudResource {
  id: string; accountId: string; account: string; provider: Provider; kind: string; externalId: string; name: string | null; region: string | null; group: string | null;
  parentId: string | null; state: string | null; stateSince: string | null; size: string | null; os: string | null; hostname: string | null;
  privateIp: string | null; publicIp: string | null; cpu: number | null; memoryMb: number | null; diskGb: number | null; biosUuid: string | null; macs: string[];
  tags: Record<string, string>; properties: Record<string, unknown>; createdAt: string | null; assetId: string | null; assetName: string | null; assetLinkManual: boolean;
  department: string | null; location: string | null; firstSeen: string; lastSeen: string; deletedAt: string | null; openFindings: number; worstSeverity: string | null;
}

export interface CloudFinding {
  id: number; rule: string; severity: string; title: string; detail: string | null; firstSeen: string; lastSeen: string; resolvedAt: string | null;
  ignored: boolean; ignoredBy: string | null; resourceId: string; resource: string | null; kind: string; provider: Provider; region: string | null; account: string; assetId: string | null;
}

export interface CloudChange {
  id: number; at: string; change: 'created' | 'changed' | 'deleted' | 'restored'; detail: Record<string, unknown>; resourceId: string; name: string | null; kind: string;
  provider: Provider; region?: string | null; account: string;
}

export interface CloudAccount {
  id: string; name: string; provider: Provider;
  settings: { tenantId?: string; clientId?: string; subscriptions?: string[]; accessKeyId?: string; regions?: string[]; host?: string; port?: number | null;
    username?: string; skipCertificateCheck?: boolean; hosts?: string[] };
  hasSecret: boolean; credentialId: string | null; credentialName: string | null; enabled: boolean; syncHours: number; lastSyncAt: string | null;
  lastStatus: string | null; lastError: string | null; lastErrorAt: string | null; lastWarnings: string[]; createdAt: string; resources: number;
  lastCounts: { resources?: number; byKind?: Record<string, number>; created?: number; changed?: number; deleted?: number; linked?: number; findings?: number; newFindings?: number; identity?: string };
  jobStatus: string | null; jobId: string | null;
}

export interface CloudSummary {
  accounts: number; accountsFailed: number; resources: number; vms: number; vmsRunning: number; vmsLinked: number; hosts: number; vcpu: number; memoryGb: number; storageGb: number;
  byProvider: { provider: Provider; n: number; vms: number; running: number }[]; byKind: Record<string, number>; findings: Record<string, number>;
  topFindings: { rule: string; title: string; severity: string; n: number }[]; changes7: Record<string, number>; recentChanges: CloudChange[];
  unlinkedVms: number; idleCost: number; lastSync: string | null;
}

export const gb = (mb: number | null | undefined) => (mb == null ? '—' : mb >= 1024 ? `${(mb / 1024).toFixed(mb % 1024 === 0 ? 0 : 1)} GB` : `${mb} MB`);
export const disk = (g: number | null | undefined) => (g == null ? '—' : g >= 1024 ? `${(g / 1024).toFixed(1)} TB` : `${Math.round(g)} GB`);
