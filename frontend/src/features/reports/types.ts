// Shapes returned by /api/v1/reports (see backend ReportEndpoints.cs).

export type ColumnType = 'text' | 'number' | 'date' | 'yesno';

export interface DatasetColumn { key: string; label: string; type: ColumnType; group: string | null }
export interface Dataset { key: string; name: string; description: string; columns: DatasetColumn[] }

export interface ReportFilter { column: string; op: string; value?: string | null }
export interface ReportSort { column: string; dir: 'asc' | 'desc' }
export interface ReportSpec { columns: string[]; filters: ReportFilter[]; groupBy?: string[] | null; sort?: ReportSort[] | null }

export interface ReportSchedule { enabled: boolean; frequency: 'daily' | 'weekly' | 'monthly'; hour: number; weekday: number; day: number }

export interface ReportListItem {
  id: string; name: string; description: string | null; dataset: string; shared: boolean; system: boolean; owned: boolean; owner: string | null;
  updatedAt: string; schedule: ReportSchedule | null; scheduleText: string | null; recipients: string[]; format: 'xlsx' | 'csv';
  nextRunAt: string | null; lastRunAt: string | null; lastStatus: string | null;
}

export interface ReportDetail {
  id: string; name: string; description: string | null; dataset: string; definition: ReportSpec; shared: boolean; system: boolean; owned: boolean;
  schedule: ReportSchedule | null; recipients: string[]; format: 'xlsx' | 'csv';
}

export interface RunResult { columns: { key: string; label: string; type: ColumnType }[]; rows: (string | number | boolean | null)[][]; total: number; truncated: boolean }

export interface ReportRun {
  id: string; reportId: string | null; reportName: string; trigger: string; status: string; rows: number | null; format: string | null; fileName: string | null;
  downloadable: boolean; recipients: string[]; error: string | null; requestedBy: string | null; startedAt: string; finishedAt: string | null;
}

export const OPERATORS: { op: string; label: string; types: ColumnType[]; noValue?: boolean }[] = [
  { op: 'eq', label: 'is', types: ['text', 'number', 'date', 'yesno'] },
  { op: 'neq', label: 'is not', types: ['text', 'number', 'date', 'yesno'] },
  { op: 'contains', label: 'contains', types: ['text'] },
  { op: 'not_contains', label: 'does not contain', types: ['text'] },
  { op: 'starts', label: 'starts with', types: ['text'] },
  { op: 'in', label: 'is one of (comma-separated)', types: ['text'] },
  { op: 'gt', label: '>', types: ['number', 'date'] },
  { op: 'gte', label: '≥', types: ['number', 'date'] },
  { op: 'lt', label: '<', types: ['number', 'date'] },
  { op: 'lte', label: '≤', types: ['number', 'date'] },
  { op: 'last_days', label: 'in the last N days', types: ['date'] },
  { op: 'older_days', label: 'older than N days (or never)', types: ['date'] },
  { op: 'empty', label: 'is empty', types: ['text', 'number', 'date', 'yesno'], noValue: true },
  { op: 'not_empty', label: 'is not empty', types: ['text', 'number', 'date', 'yesno'], noValue: true },
];

export const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

export function cellText(v: unknown, type: ColumnType): string {
  if (v === null || v === undefined || v === '') return '—';
  if (type === 'date') { const d = new Date(String(v)); return isNaN(d.getTime()) ? String(v) : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }); }
  if (type === 'yesno') return v === true || v === 'yes' ? 'Yes' : v === false || v === 'no' ? 'No' : String(v);
  if (type === 'number' && typeof v === 'number') return v.toLocaleString();
  return String(v);
}

export function errorText(e: unknown) { return e instanceof Error ? e.message : String(e); }
