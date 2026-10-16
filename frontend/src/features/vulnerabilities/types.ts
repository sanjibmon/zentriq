// Shapes returned by /api/v1/vulnerabilities (see backend VulnerabilityEndpoints.cs).
import type { Tone } from '../../components/ui';

export interface KeyCount { key: string; count: number }
export interface VulnOverview {
  kpi: {
    open: number; critical: number; high: number; kev: number; overdue: number; cves: number; affectedAssets: number; assets: number;
    accepted: number; fixed30: number; mttrDays: number; slaMet: number; cveCatalogue: number; mapped: number; unmapped: number;
    exploited: number; inProgress: number; unassigned: number; osOpen: number; softwareOpen: number; windowsDevices: number; windowsNoBuild: number; windowsCves: number;
  };
  bySource: KeyCount[];
  aging: { bucket: string; critical: number; high: number; medium: number; low: number }[];
  bySeverity: KeyCount[];
  byPriority: KeyCount[];
  topSoftware: KeyCount[];
  topAssets: { assetId: string; hostname: string | null; open: number; maxRisk: number; kev: number }[];
  topCves: { cve: string; severity: string | null; cvss: number | null; kev: boolean; epss: number | null; assets: number; priority: string; title: string | null; exploited: boolean }[];
  trend: { day: string; opened: number; fixed: number; open: number }[];
  feeds: { source: string; lastRunAt: string | null; lastOkAt: string | null; items: number | null; message: string | null }[];
  generatedAt: string;
}

export interface Finding {
  id: number; assetId: string; hostname: string | null; os: string | null; cve: string; severity: string | null; cvss: number | null; kev: boolean; epss: number | null;
  priority: string; risk: number; software: string; version: string; fixedVersion: string | null; status: string;
  firstSeenAt: string; lastSeenAt: string; dueAt: string | null; fixedAt: string | null;
  exceptionReason: string | null; exceptionUntil: string | null; exceptionBy: string | null; summary: string | null;
  source: 'software' | 'os'; kb: string | null; assigneeId: string | null; assignee: string | null; ticket: string | null;
  exploited: boolean; title: string | null; criticality: string | null;
}

export interface FindingDetail {
  finding: Finding & { vector: string | null; vendor: string; product: string; description: string | null; kevDue: string | null; remediation: string };
  events: { at: string; kind: 'note' | 'status' | 'assign' | 'ticket' | 'system'; text: string; by: string | null }[];
}

export interface Remediation {
  source: 'software' | 'os'; vendor: string; product: string; software: string; action: string; targetVersion: string | null; kb: string | null; kbUrl: string | null;
  findings: number; devices: number; cves: number; exploited: number; topPriority: string; riskReduction: number; maxCvss: number | null; overdue: number; withoutFix: number;
}

export interface Assignee { id: string; name: string; email: string | null }

export interface CveDetail {
  cve: {
    cve: string; publishedAt: string | null; modifiedAt: string | null; status: string | null; description: string | null; cvssVersion: string | null; cvss: number | null;
    vector: string | null; severity: string | null; cwes: string[]; references: string[]; kev: boolean; kevAdded: string | null; kevDue: string | null;
    kevRansomware: string | null; kevAction: string | null; epss: number | null; epssPercentile: number | null; epssDate: string | null; nvdUrl: string;
    title: string | null; msSeverity: string | null; msExploited: boolean; msDisclosed: boolean; msrcUrl: string | null;
  };
  products: { vendor: string; product: string; version: string | null; startIncl: string | null; startExcl: string | null; endIncl: string | null; endExcl: string | null }[];
  windows: { product: string; fixedBuild: string | null; kb: string | null; kbUrl: string | null; month: string; restart: boolean | null }[];
  affectedDevices: number;
}

export interface ProductMapping {
  id: string; software: string; key: string; publisher: string | null; vendor: string | null; product: string | null; status: 'auto' | 'manual' | 'ignored' | 'unmapped';
  syncedAt: string | null; updatedBy: string | null; updatedAt: string; cves: number; openFindings: number;
}

export interface VulnImport { id: string; source: string; fileName: string; size: number; status: string; items: number | null; message: string | null; by: string | null; createdAt: string; finishedAt: string | null }
export interface SlaDays { critical: number; high: number; medium: number; low: number }
export interface VulnSettings { autoSync: boolean; slaDays: SlaDays; minCvss: number; nvdApiKeySet: boolean; builtinProducts: number; msrcMonths: number; criticalityWeight: boolean }

export const SEVERITIES = ['critical', 'high', 'medium', 'low', 'none'] as const;
export const SEVERITY_COLOR: Record<string, string> = { critical: '#b42318', high: '#e8590c', medium: '#d4a017', low: '#2f7dbd', none: '#8a96a3', unknown: '#8a96a3' };
export const PRIORITY_COLOR: Record<string, string> = { P1: '#b42318', P2: '#e8590c', P3: '#d4a017', P4: '#2f7dbd' };
export const PRIORITY_HELP: Record<string, string> = {
  P1: 'Exploited in the wild (CISA KEV or Microsoft), or CVSS ≥ 9 with EPSS ≥ 10 %',
  P2: 'CVSS ≥ 9, or CVSS ≥ 7 with EPSS ≥ 10 %',
  P3: 'CVSS ≥ 7, or CVSS ≥ 4 with EPSS ≥ 10 %',
  P4: 'Everything else',
};

export const PRIORITY_SHORT: Record<string, string> = { P1: 'P1 · fix now', P2: 'P2 · urgent', P3: 'P3 · planned', P4: 'P4 · routine' };
export const cvssText = (n: number | null | undefined) => (n == null ? '—' : n.toFixed(1));

export function sevTone(s: string | null | undefined): Tone {
  return s === 'critical' ? 'crit' : s === 'high' ? 'crit' : s === 'medium' ? 'warn' : s === 'low' ? 'info' : '';
}
export function prioTone(p: string): Tone { return p === 'P1' || p === 'P2' ? 'crit' : p === 'P3' ? 'warn' : 'info'; }
export const pct = (n: number | null | undefined, digits = 1) => (n == null ? '—' : `${(n * 100).toFixed(digits)}%`);
export const cap = (s: string | null | undefined) => (s ? s[0]!.toUpperCase() + s.slice(1) : '—');
export function errorText(e: unknown) { return e instanceof Error ? e.message : String(e); }

export const STATUS_LABEL: Record<string, string> = { open: 'Open', in_progress: 'In progress', accepted: 'Risk accepted', false_positive: 'False positive', fixed: 'Fixed' };
export function statusTone(s: string): Tone { return s === 'open' ? 'crit' : s === 'in_progress' ? 'info' : s === 'fixed' ? 'ok' : 'warn'; }

/** CVSS v3.x / v4 base metrics in words, from the vector string. */
export function cvssParts(vector: string | null | undefined): [string, string][] {
  if (!vector) return [];
  const m = Object.fromEntries(vector.split('/').map((p) => p.split(':') as [string, string]).filter((p) => p.length === 2));
  const names: Record<string, [string, Record<string, string>]> = {
    AV: ['Attack vector', { N: 'Network', A: 'Adjacent network', L: 'Local', P: 'Physical' }],
    AC: ['Attack complexity', { L: 'Low', H: 'High' }],
    PR: ['Privileges required', { N: 'None', L: 'Low', H: 'High' }],
    UI: ['User interaction', { N: 'None', R: 'Required', P: 'Passive', A: 'Active' }],
    S: ['Scope', { U: 'Unchanged', C: 'Changed' }],
    C: ['Confidentiality', { N: 'None', L: 'Low', H: 'High' }],
    I: ['Integrity', { N: 'None', L: 'Low', H: 'High' }],
    A: ['Availability', { N: 'None', L: 'Low', H: 'High' }],
    VC: ['Confidentiality', { N: 'None', L: 'Low', H: 'High' }],
    VI: ['Integrity', { N: 'None', L: 'Low', H: 'High' }],
    VA: ['Availability', { N: 'None', L: 'Low', H: 'High' }],
    AT: ['Attack requirements', { N: 'None', P: 'Present' }],
  };
  return Object.entries(names).filter(([k]) => m[k] !== undefined).map(([k, [label, values]]) => [label, values[m[k]!] ?? m[k]!]);
}
