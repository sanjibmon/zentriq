export type Rating = 'good' | 'fair' | 'poor';
export interface Issue { kind: string; severity: 'warn' | 'crit'; text: string; points: number }

export interface ExperienceRow {
  assetId: string; hostname: string; os: string | null; department: string | null; user: string | null;
  score: number; rating: Rating; bootMs: number | null; logonMs: number | null; crashes: number; hangs: number; bugchecks: number; shutdowns: number; serviceFailures: number;
  cpuHigh: number | null; memHigh: number | null; systemFree: number | null; battery: number | null; uptimeDays: number | null; issues: Issue[]; computedAt: string;
}

export interface ExperienceSummary {
  devices: number; score: number | null; good: number; fair: number; poor: number; bootMs: number | null;
  crashes: number; hangs: number; bugchecks: number; bugcheckDevices: number;
  trend: { day: string; score: number; devices: number }[];
  issues: { kind: string; devices: number }[];
  apps: { app: string; crashes: number; hangs: number; devices: number; version: string | null; last: string }[];
}

export interface ExperienceEventRow { at: string; kind: string; app: string | null; version: string | null; detail: string | null; ms: number | null; ms2: number | null }

export interface DeviceExperience {
  current: Omit<ExperienceRow, 'assetId' | 'hostname' | 'os' | 'department' | 'user'> | null;
  history: { day: string; score: number }[];
  days: { day: string; cpuAvg: number | null; cpuHigh: number; memAvg: number | null; memHigh: number; samples: number }[];
  events: ExperienceEventRow[];
}

export const RATING_LABEL: Record<Rating, string> = { good: 'Good', fair: 'Fair', poor: 'Poor' };
export const RATING_TONE: Record<Rating, 'ok' | 'warn' | 'crit'> = { good: 'ok', fair: 'warn', poor: 'crit' };

export const ISSUE_LABEL: Record<string, string> = {
  boot: 'Slow start', logon: 'Slow to become usable', crash: 'Application crashes', hang: 'Applications not responding', bugcheck: 'Blue screens',
  shutdown: 'Unexpected shutdowns', service: 'Service failures', cpu: 'Processor overloaded', memory: 'Memory short', disk: 'System drive full',
  battery: 'Battery worn', uptime: 'Not restarted',
};

export const EVENT_LABEL: Record<string, string> = {
  boot: 'Start', crash: 'Crash', hang: 'Not responding', bugcheck: 'Blue screen', shutdown: 'Unexpected shutdown', service: 'Service failure', oom: 'Out of memory',
};

export function seconds(ms: number | null | undefined): string {
  if (ms == null) return '—';
  const s = Math.round(ms / 1000);
  return s >= 60 ? `${Math.floor(s / 60)} min ${s % 60} s` : `${s} s`;
}

export const ratingOf = (score: number): Rating => (score >= 80 ? 'good' : score >= 60 ? 'fair' : 'poor');
