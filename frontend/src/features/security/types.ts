import type { Tone } from '../../components/ui';

export interface SecOverview {
  baselineId: string | null;
  baselines: { id: string; name: string }[];
  kpi: { devices: number; avgScore: number | null; compliant: number; failedChecks: number; failedHigh: number; notAssessed: number; exempt: number; unknown: number; good: number; poor: number };
  coverage: { windowsDevices: number; noSettingsData: number };
  byCategory: { category: string; pass: number; fail: number; unknown: number; exempt: number }[];
  topFailing: { rule: string; title: string; severity: string; failing: number; assessed: number }[];
  worst: { assetId: string; hostname: string | null; os: string | null; score: number; failed: number; failedHigh: number; criticality: string }[];
  distribution: { bucket: string; count: number }[];
  trend: { day: string; avgScore: number | null; devices: number | null; compliant: number | null; failed: number | null }[];
  exceptions: number;
  lastEvaluation: string | null;
}

export interface RuleParam { key: string; label: string; default: number; min: number; max: number; value: number }

export interface SecRule {
  id: string; title: string; category: string; defaultSeverity: string; severity: string; applies: string; reference: string; rationale: string; remediation: string;
  enabled: boolean; params: RuleParam[]; pass: number; fail: number; unknown: number; exempt: number;
}

export interface RuleDevice {
  assetId: string; hostname: string | null; os: string | null; category: string; status: string; actual: string | null; failingSince: string | null; evaluatedAt: string; severity: string;
}

export interface SecException {
  id: string; rule: string; title: string | null; assetId: string | null; hostname: string | null; baselineId: string | null; baseline: string | null;
  reason: string; expiresAt: string | null; createdBy: string | null; createdAt: string; expired: boolean;
}

export interface RuleDetail {
  rule: { id: string; title: string; category: string; severity: string; applies: string; reference: string; rationale: string; remediation: string };
  devices: RuleDevice[];
  exceptions: SecException[];
}

export interface DeviceScore {
  assetId: string; hostname: string | null; os: string | null; category: string; criticality: string; score: number | null;
  passed: number; failed: number; failedHigh: number; unknown: number; exempt: number; evaluatedAt: string;
}

export interface BaselineSummary {
  id: string; name: string; description: string | null; builtIn: boolean; enabled: boolean; categories: string[]; rules: number;
  updatedAt: string; updatedBy: string | null; devices: number; avgScore: number | null;
}

export interface BaselineDetail {
  id: string; name: string; description: string | null; builtIn: boolean; enabled: boolean; categories: string[];
  rules: { id: string; title: string; category: string; defaultSeverity: string; applies: string; enabled: boolean; severity: string; params: RuleParam[] }[];
}

export interface DeviceSecurity {
  baselines: { id: string; name: string; score: number | null; passed: number; failed: number; failedHigh: number; unknown: number; exempt: number; evaluatedAt: string }[];
  results: { baselineId: string; rule: string; title: string; category: string | null; status: string; severity: string; actual: string | null; failingSince: string | null; remediation: string | null; reference: string | null }[];
  settingsReported: boolean | null;
}

export const RESULT_LABEL: Record<string, string> = { pass: 'Pass', fail: 'Fail', unknown: 'Unknown', exempt: 'Exception' };
export function resultTone(s: string): Tone { return s === 'pass' ? 'ok' : s === 'fail' ? 'crit' : s === 'exempt' ? 'info' : 'warn'; }
export function secSevTone(s: string): Tone { return s === 'high' ? 'crit' : s === 'medium' ? 'warn' : 'info'; }
export const APPLIES_LABEL: Record<string, string> = { all: 'All Windows', server: 'Servers', endpoint: 'Workstations' };
export const SCORE_COLOR = (v: number | null | undefined) => v == null ? 'var(--line-strong)' : v >= 90 ? 'var(--ok)' : v >= 70 ? 'var(--warn)' : v >= 50 ? '#e8590c' : 'var(--crit)';
