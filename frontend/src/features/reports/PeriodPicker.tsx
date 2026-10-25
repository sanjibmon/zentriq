import { Icon } from '../../components/Icon';
import { PERIODS, periodDays, periodText, type Dataset, type ReportPeriod } from './types';

/**
 * Time period of a report: all dates, a relative range (last month, this year …) or From – To dates, on one of the data
 * source's date columns. Saved with the report, so scheduled deliveries and dashboards use the same period.
 */
export function PeriodPicker({ ds, value, onChange }: { ds: Dataset | null; value: ReportPeriod | null | undefined; onChange: (p: ReportPeriod | null) => void }) {
  const dates = (ds?.columns ?? []).filter((c) => c.type === 'date');
  const p = value ?? null;
  if (ds && dates.length === 0) return <div className="period-bar"><span className="period-label"><Icon name="calendar" size={14} /> Time period</span><span className="faint">This data source has no dates.</span></div>;

  function setRange(range: string) {
    if (range === 'all') { onChange(null); return; }
    const column = p?.column && dates.some((c) => c.key === p.column) ? p.column : (ds?.defaultDate ?? dates[0]?.key ?? '');
    if (range === 'custom') {
      // Start the custom dates from what was shown, or the last 30 days.
      const [from, to] = p ? periodDays(p) : periodDays({ column, range: 'last_30_days' });
      onChange({ column, range, from, to });
    } else onChange({ column, range, from: null, to: null });
  }

  return (
    <div className="period-bar">
      <span className="period-label"><Icon name="calendar" size={14} /> Time period</span>
      <select className="in" aria-label="Time period" value={p?.range ?? 'all'} onChange={(e) => setRange(e.target.value)}>
        <option value="all">All dates</option>
        {PERIODS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}
      </select>
      {p && <>
        {p.range === 'custom' && <span className="period-dates">
          <input className="in" type="date" aria-label="From date" value={p.from ?? ''} max={p.to ?? undefined} onChange={(e) => onChange({ ...p, from: e.target.value || null })} title="From (inclusive)" />
          <span className="faint">to</span>
          <input className="in" type="date" aria-label="To date" value={p.to ?? ''} min={p.from ?? undefined} onChange={(e) => onChange({ ...p, to: e.target.value || null })} title="To (inclusive)" />
        </span>}
        <span className="faint">by</span>
        <select className="in" aria-label="Date column" value={p.column} onChange={(e) => onChange({ ...p, column: e.target.value })}>
          {dates.map((c) => <option key={c.key} value={c.key}>{c.label}</option>)}
        </select>
        {p.range !== 'custom' && <span className="period-text">{periodText(p)}</span>}
      </>}
    </div>
  );
}
