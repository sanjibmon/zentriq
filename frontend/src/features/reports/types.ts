// Shapes returned by /api/v1/reports (see backend ReportEndpoints.cs).

export type ColumnType = 'text' | 'number' | 'date' | 'yesno';

export interface DatasetColumn { key: string; label: string; type: ColumnType; group: string | null }
export interface Dataset { key: string; name: string; description: string; columns: DatasetColumn[]; defaultDate: string | null }
export interface DatasetCatalog { datasets: Dataset[]; aggregates: string[]; dateBuckets: string[]; charts: string[]; formats: ReportFormat[]; periods: string[] }
export type ReportFormat = 'xlsx' | 'csv' | 'pdf';

export interface ReportFilter { column: string; op: string; value?: string | null }
export interface ReportSort { column: string; dir: 'asc' | 'desc' }
export interface ReportAggregate { fn: 'count_distinct' | 'sum' | 'avg' | 'min' | 'max'; column: string }
/** Time period on a date column: a relative range (resolved when the report runs) or custom From / To dates (inclusive). */
export interface ReportPeriod { column: string; range: string; from?: string | null; to?: string | null }
export interface ReportSpec {
  columns: string[]; filters: ReportFilter[]; groupBy?: string[] | null; sort?: ReportSort[] | null;
  aggregates?: ReportAggregate[] | null; dateBucket?: string | null; chart?: string | null; period?: ReportPeriod | null;
}

export const PERIODS: [string, string][] = [
  ['custom', 'From – to dates'], ['today', 'Today'], ['yesterday', 'Yesterday'], ['last_7_days', 'Last 7 days'], ['last_30_days', 'Last 30 days'],
  ['last_90_days', 'Last 90 days'], ['this_week', 'This week'], ['last_week', 'Last week'], ['this_month', 'This month'], ['last_month', 'Last month'],
  ['this_quarter', 'This quarter'], ['last_quarter', 'Last quarter'], ['this_year', 'This year'], ['last_year', 'Last year'],
];

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const add = (d: Date, days: number) => { const x = new Date(d); x.setDate(x.getDate() + days); return x; };

/** First and last day of a period (yyyy-mm-dd; null = open), as the server resolves it (weeks start on Monday). */
export function periodDays(p: ReportPeriod, now = new Date()): [string | null, string | null] {
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const monday = add(today, -((today.getDay() + 6) % 7));
  const month = new Date(today.getFullYear(), today.getMonth(), 1);
  const quarter = new Date(today.getFullYear(), Math.floor(today.getMonth() / 3) * 3, 1);
  const year = new Date(today.getFullYear(), 0, 1);
  const m = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth() + n, 1);
  const r = (a: Date, b: Date): [string, string] => [iso(a), iso(b)];
  switch (p.range) {
    case 'today': return r(today, today);
    case 'yesterday': return r(add(today, -1), add(today, -1));
    case 'last_7_days': return r(add(today, -6), today);
    case 'last_30_days': return r(add(today, -29), today);
    case 'last_90_days': return r(add(today, -89), today);
    case 'this_week': return r(monday, add(monday, 6));
    case 'last_week': return r(add(monday, -7), add(monday, -1));
    case 'this_month': return r(month, add(m(month, 1), -1));
    case 'last_month': return r(m(month, -1), add(month, -1));
    case 'this_quarter': return r(quarter, add(m(quarter, 3), -1));
    case 'last_quarter': return r(m(quarter, -3), add(quarter, -1));
    case 'this_year': return r(year, add(new Date(year.getFullYear() + 1, 0, 1), -1));
    case 'last_year': return r(new Date(year.getFullYear() - 1, 0, 1), add(year, -1));
    default: return [p.from || null, p.to || null];
  }
}

export function periodText(p: ReportPeriod): string {
  const [a, b] = periodDays(p);
  const f = (s: string) => new Date(`${s}T00:00:00`).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
  if (a && b) return a === b ? f(a) : `${f(a)} – ${f(b)}`;
  if (a) return `from ${f(a)}`;
  if (b) return `until ${f(b)}`;
  return 'all dates';
}

export const AGGREGATES: { fn: ReportAggregate['fn']; label: string; types: ColumnType[] }[] = [
  { fn: 'count_distinct', label: 'Distinct count of', types: ['text', 'number', 'date', 'yesno'] },
  { fn: 'sum', label: 'Total of', types: ['number'] },
  { fn: 'avg', label: 'Average of', types: ['number'] },
  { fn: 'min', label: 'Minimum of', types: ['number', 'date'] },
  { fn: 'max', label: 'Maximum of', types: ['number', 'date'] },
];
export const DATE_BUCKETS: [string, string][] = [['day', 'Day'], ['week', 'Week'], ['month', 'Month'], ['quarter', 'Quarter'], ['year', 'Year']];
export const aggKey = (a: ReportAggregate) => `agg_${a.fn}_${a.column}`;

export interface ReportSchedule { enabled: boolean; frequency: 'daily' | 'weekly' | 'monthly'; hour: number; weekday: number; day: number }

export interface ReportListItem {
  id: string; name: string; description: string | null; dataset: string; shared: boolean; system: boolean; owned: boolean; owner: string | null;
  updatedAt: string; schedule: ReportSchedule | null; scheduleText: string | null; recipients: string[]; format: ReportFormat;
  nextRunAt: string | null; lastRunAt: string | null; lastStatus: string | null; period: string | null;
}

export interface ReportDetail {
  id: string; name: string; description: string | null; dataset: string; definition: ReportSpec; shared: boolean; system: boolean; owned: boolean;
  schedule: ReportSchedule | null; recipients: string[]; format: ReportFormat;
}

export interface RunResult { columns: { key: string; label: string; type: ColumnType }[]; rows: (string | number | boolean | null)[][]; total: number; truncated: boolean; period?: string | null }

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
